using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Covers work-items/bugs/2026-08-04-the-app-has-no-global-unhandled-exception-handler.md
/// and the decision that settles it,
/// work-items/decisions/2026-08-04-fatal-failures-are-reported-not-swallowed.md.
///
/// <para>These tests exist because a global handler nobody has watched catch
/// anything is exactly the shape of guard this repository has twice found to be
/// decorative. So the dispatcher hook is exercised through a REAL escaped
/// <c>async void</c> continuation — the exact mechanism from the filing — rather
/// than by asserting that a subscription exists.</para>
///
/// <para><b>What these tests deliberately do NOT cover:</b> that the record
/// survives an actual process death. That cannot be observed from inside the
/// process being killed, and asserting it here would be the same decorative
/// shape. It is demonstrated instead by provoking a real crash in the shipped
/// executable and reading the file back afterwards from outside it.</para>
/// </summary>
public sealed class ApplicateFatalReportTests : IDisposable
{
    private readonly string _crashDirectory;

    public ApplicateFatalReportTests()
    {
        _crashDirectory = Path.Combine(
            Path.GetTempPath(),
            "markmello-fatal-report-tests",
            Guid.NewGuid().ToString("N"));
        ApplicateFatalReport.SetCrashDirectoryForTesting(_crashDirectory);
    }

    public void Dispose()
    {
        // Restored to a QUARANTINE directory rather than to null. The dispatcher
        // hook installed by the tests below stays installed for the remainder of
        // the test run, and any later escaped exception anywhere in the suite
        // would fire it -- with the override cleared, that write would land in
        // the operator's real %AppData%\MarkMello. It must not.
        ApplicateFatalReport.SetCrashDirectoryForTesting(QuarantineDirectory);

        try
        {
            Directory.Delete(_crashDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string QuarantineDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "markmello-fatal-report-tests",
        "quarantine");

    private string CrashFilePath => Path.Combine(_crashDirectory, ApplicateFatalReport.CrashFileName);

    private string ReadCrashFile() => File.ReadAllText(CrashFilePath);

    // ---------------------------------------------------------------------
    // The dispatcher hook, driven by the real escape path.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task TheDispatcherHookReportsAnEscapedAsyncVoidFailureToBothSinks()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            await session.Dispatch(async () =>
            {
                ApplicateFatalReport.InstallDispatcherHook();

                var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // TEST-ONLY containment, registered AFTER the production hook so
                // the production hook runs first. Production does NOT do this --
                // see TheDispatcherHookDoesNotMarkTheFailureHandled below, which
                // pins exactly that. Without it this escaped exception would
                // tear down the test runner, which is the whole point of the
                // decision being tested.
                void Contain(object? _, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    fired.TrySetResult();
                }

                Dispatcher.UIThread.UnhandledException += Contain;
                try
                {
                    // Dispatcher.Post binds Action, so this async lambda is an
                    // async void continuation: exactly the shape the filing
                    // proved kills the process.
                    Dispatcher.UIThread.Post(async () =>
                    {
                        await Task.Yield();
                        throw new InvalidOperationException("fatal-report-boom");
                    });

                    await fired.Task;
                }
                finally
                {
                    Dispatcher.UIThread.UnhandledException -= Contain;
                }

                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Sink 2 first: the durable record is the one that decides whether this
        // fix works at all, because the shipped WinExe has no console for sink 1
        // to reach.
        Assert.True(File.Exists(CrashFilePath), $"no durable record was written to {CrashFilePath}");
        var record = ReadCrashFile();
        Assert.Contains("origin=dispatcher", record, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), record, StringComparison.Ordinal);
        Assert.Contains("fatal-report-boom", record, StringComparison.Ordinal);

        // The stack trace is the reason the durable record carries ToString()
        // rather than just the message.
        Assert.Contains("MarkMello.Applicate.Tests", record, StringComparison.Ordinal);

        // Sink 1: the repository's existing always-on diagnostic convention.
        var trace = captured.ToString();
        Assert.Contains("fatal", trace, StringComparison.Ordinal);
        Assert.Contains("dispatcher", trace, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), trace, StringComparison.Ordinal);
        Assert.Contains("fatal-report-boom", trace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDispatcherHookDoesNotMarkTheFailureHandled()
    {
        // Decision 1: the handler reports, it does not rescue. Marking a
        // dispatcher exception handled swallows an unknown failure and carries
        // on over state that may already be corrupt.
        //
        // Observed behaviourally rather than by scanning for a spelling: this
        // subscriber is registered after the production hook, so whatever it
        // sees in e.Handled on entry is what the production hook left there.
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        var originalError = Console.Error;
        Console.SetError(new StringWriter());

        bool? handledAsLeftByProduction = null;

        try
        {
            await session.Dispatch(async () =>
            {
                ApplicateFatalReport.InstallDispatcherHook();

                var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                void Observe(object? _, DispatcherUnhandledExceptionEventArgs e)
                {
                    handledAsLeftByProduction = e.Handled;
                    e.Handled = true;
                    fired.TrySetResult();
                }

                Dispatcher.UIThread.UnhandledException += Observe;
                try
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        await Task.Yield();
                        throw new InvalidOperationException("fatal-report-not-handled");
                    });

                    await fired.Task;
                }
                finally
                {
                    Dispatcher.UIThread.UnhandledException -= Observe;
                }

                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // This assertion is load-bearing and was ADDED after the first version of
        // this test was caught passing vacuously: Handled defaults to false, so
        // "the production hook did not mark it handled" and "the production hook
        // never ran at all" are indistinguishable without positive evidence that
        // it ran. The durable record is that evidence.
        Assert.True(
            File.Exists(CrashFilePath) && ReadCrashFile().Contains("origin=dispatcher", StringComparison.Ordinal),
            "the production dispatcher hook never ran, so this test's Handled assertion would have been vacuous");

        Assert.True(handledAsLeftByProduction.HasValue, "the production dispatcher hook never ran");
        Assert.False(
            handledAsLeftByProduction!.Value,
            "the production dispatcher hook marked the failure Handled. It must report and let the process "
            + "die: a global handler cannot know whether continuing over the failure is safe, and containment "
            + "for failures that ARE known safe belongs at the call site that knows it.");
    }

    // ---------------------------------------------------------------------
    // The two hooks whose events cannot be raised from inside a live test
    // without killing the runner. Their real production handlers are driven
    // directly with real event args, so the assertions are about behaviour
    // rather than about a subscription existing.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheUnobservedTaskHookRecordsTheFaultWithoutObservingIt()
    {
        var args = new UnobservedTaskExceptionEventArgs(
            new AggregateException(new InvalidOperationException("unobserved-boom")));

        ApplicateFatalReport.OnUnobservedTaskException(null, args);

        Assert.False(
            args.Observed,
            "the unobserved-task hook called SetObserved(). Observing a fault purely to silence it is the same "
            + "swallow the decision refuses everywhere else.");

        var record = ReadCrashFile();
        Assert.Contains("origin=unobserved-task", record, StringComparison.Ordinal);
        Assert.Contains("unobserved-boom", record, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppDomainHookRecordsTheFailureAndThatTheProcessIsTerminating()
    {
        var args = new UnhandledExceptionEventArgs(
            new InvalidOperationException("appdomain-boom"),
            isTerminating: true);

        ApplicateFatalReport.OnAppDomainUnhandledException(null, args);

        var record = ReadCrashFile();
        Assert.Contains("origin=appdomain", record, StringComparison.Ordinal);
        Assert.Contains("terminating=True", record, StringComparison.Ordinal);
        Assert.Contains("appdomain-boom", record, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppDomainHookSurvivesAnExceptionObjectThatIsNotAnException()
    {
        // UnhandledExceptionEventArgs.ExceptionObject is typed as object and is
        // NOT required to be an Exception -- non-CLS-compliant throws reach here
        // as arbitrary objects. A report path that assumed Exception would throw
        // a cast failure while reporting a failure, which is the one thing it
        // must never do.
        var args = new UnhandledExceptionEventArgs("a bare string, not an exception", isTerminating: true);

        ApplicateFatalReport.OnAppDomainUnhandledException(null, args);

        var record = ReadCrashFile();
        Assert.Contains("origin=appdomain", record, StringComparison.Ordinal);
        Assert.Contains("a bare string, not an exception", record, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Only-forward: the report path can never be the thing that kills the
    // process.
    // ---------------------------------------------------------------------

    [Fact]
    public void ReportDoesNotThrowWhenTheDurableSinkCannotBeCreated()
    {
        // A crash directory whose PARENT is an existing file: Directory.
        // CreateDirectory throws IOException on this, so the durable sink fails
        // for a real, reproducible reason rather than a mocked one.
        var blocker = Path.Combine(Path.GetTempPath(), $"markmello-fatal-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "this is a file, not a directory");

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            ApplicateFatalReport.SetCrashDirectoryForTesting(Path.Combine(blocker, "crash"));

            // The assertion IS that this call returns normally.
            ApplicateFatalReport.Report("unit-test", new InvalidOperationException("unwritable-sink"));
        }
        finally
        {
            Console.SetError(originalError);
            ApplicateFatalReport.SetCrashDirectoryForTesting(_crashDirectory);
            File.Delete(blocker);
        }

        // ... and that the OTHER sink still got the report. The two sinks are
        // independent: a failure in one must not suppress the other.
        Assert.Contains("unwritable-sink", captured.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportDoesNotThrowWhenTheFailureItselfThrowsWhileBeingFormatted()
    {
        // A handler that throws while reporting a throw is strictly worse than
        // no handler, and ToString() is attacker-adjacent surface here: it runs
        // arbitrary user code on a dying process.
        var originalError = Console.Error;
        Console.SetError(new StringWriter());

        try
        {
            ApplicateFatalReport.Report("unit-test", new ThrowsWhenRendered());
        }
        finally
        {
            Console.SetError(originalError);
        }

        var record = ReadCrashFile();
        Assert.Contains("origin=unit-test", record, StringComparison.Ordinal);
    }

    private sealed class ThrowsWhenRendered
    {
        public override string ToString() => throw new InvalidOperationException("ToString is hostile");
    }

    // ---------------------------------------------------------------------
    // The durable record's content contract.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheDurableRecordCarriesTheStackTraceAndTheInnerExceptionChain()
    {
        var originalError = Console.Error;
        Console.SetError(new StringWriter());

        try
        {
            ApplicateFatalReport.Report("unit-test", Thrown());
        }
        finally
        {
            Console.SetError(originalError);
        }

        var record = ReadCrashFile();
        Assert.Contains("outer-failure", record, StringComparison.Ordinal);
        Assert.Contains("inner-cause", record, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), record, StringComparison.Ordinal);
    }

    private static InvalidOperationException Thrown()
    {
        try
        {
            try
            {
                throw new InvalidOperationException("inner-cause");
            }
            catch (InvalidOperationException inner)
            {
                throw new InvalidOperationException("outer-failure", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            return outer;
        }
    }

    [Fact]
    public void SuccessiveReportsAppendRatherThanOverwriteEachOther()
    {
        var originalError = Console.Error;
        Console.SetError(new StringWriter());

        try
        {
            ApplicateFatalReport.Report("unit-test", new InvalidOperationException("first-failure"));
            ApplicateFatalReport.Report("unit-test", new InvalidOperationException("second-failure"));
        }
        finally
        {
            Console.SetError(originalError);
        }

        var record = ReadCrashFile();
        Assert.Contains("first-failure", record, StringComparison.Ordinal);
        Assert.Contains("second-failure", record, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // The composition root actually arms all three hooks.
    //
    // A source scan, and therefore exactly the shape that has been decorative
    // here before -- so it carries a POSITIVE CONTROL: it proves it is reading
    // the real, live composition root before it is allowed to report success.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheApplicateCompositionRootArmsEveryHook()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MarkMello.Applicate.Desktop",
            "Program.cs");

        // Positive control. The guard that was decorative twice in this
        // repository was green because it never read the file it claimed to
        // check; absence of a match proves nothing unless the scan is first
        // shown to be looking at live production code.
        Assert.True(File.Exists(programPath), $"the composition root was not found at {programPath}");
        var source = File.ReadAllText(programPath);
        Assert.Contains(
            "StartWithClassicDesktopLifetime",
            source,
            StringComparison.Ordinal);

        Assert.Contains(
            $"{nameof(ApplicateFatalReport)}.{nameof(ApplicateFatalReport.Install)}()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{nameof(ApplicateFatalReport)}.{nameof(ApplicateFatalReport.InstallDispatcherHook)}",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
