using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarkMello.Applicate.Desktop.Diagnostics;

/// <summary>
/// The one owner of "a failure nobody anticipated leaves a record before the
/// process dies".
///
/// <para><b>The gap this closes.</b> Before this type the application installed
/// NO global unhandled-exception handler of any kind — no
/// <see cref="AppDomain.UnhandledException"/>, no
/// <see cref="TaskScheduler.UnobservedTaskException"/>, no
/// <c>Dispatcher.UnhandledException</c> — so one escaped exception anywhere
/// closed the operator's window with no message, no log and no trace. Under a
/// <c>WinExe</c> with no console attached the death is silent enough that an
/// earlier filing described the mechanism as a <i>swallow</i> when a runtime
/// probe showed the process actually DIES.</para>
///
/// <para><b>It reports; it does not rescue.</b> Per
/// <c>work-items/decisions/2026-08-04-fatal-failures-are-reported-not-swallowed.md</c>,
/// no hook here marks a failure handled or observed. Marking a dispatcher
/// exception handled is swallowing an unknown failure and continuing on state
/// that may already be corrupt — a kostyl by this repository's own hygiene
/// rules, and a global handler is the one place that by construction CANNOT
/// know whether continuing is safe. Containment for failures KNOWN to be safe
/// belongs at the site that knows, which is what <see cref="ApplicateDispatch"/>
/// does for the five guarded dispatcher continuations. This type owns the
/// residue.</para>
///
/// <para><b>So this deliberately does not make the application stop crashing.</b>
/// Only-forward is still satisfied strictly: today the operator gets nothing,
/// afterwards he gets a record. Nothing about the crash gets worse.</para>
///
/// <para><b>Two sinks, because the process is dying.</b>
/// <see cref="ApplicateTrace.Diag"/> stays the primary sink and is flushed
/// synchronously on this path rather than left queued. It is not sufficient on
/// its own: in the shipped <c>WinExe</c> there is no console attached, so
/// <c>Console.Error</c> has nowhere to go. The durable sink is an append-only
/// file next to the settings and session stores, in the per-user application
/// data directory those two already prove writable. RUNTIME-VERIFIED that a
/// record written this way survives the process death that follows it.</para>
///
/// <para><b>Why not the Windows event log</b>, which was the obvious candidate.
/// It fails two independent gates, both measured rather than assumed:
/// <list type="number">
/// <item><description><c>System.Diagnostics.EventLog</c> is not in the
/// <c>net10.0</c> framework this repository targets — the compiler reports
/// <c>CS1069 ... has been forwarded to assembly 'System.Diagnostics.EventLog'</c>
/// — so reaching it means a new <c>PackageReference</c>.</description></item>
/// <item><description>Even with that package, a non-elevated process cannot use
/// it. <c>EventLog.SourceExists</c>, <c>CreateEventSource</c> and
/// <c>WriteEntry</c> under an unregistered source each throw
/// <c>SecurityException</c> ("to create the source, you need permission to read
/// all event logs"), because registering a source is an administrator
/// operation. Only writing under the pre-registered <c>"Application"</c> source
/// succeeds, which would file our crashes under a source that is not ours.</description></item>
/// </list>
/// A sink that silently fails to be written would rebuild the exact defect this
/// type exists to fix, one layer up.</para>
///
/// <para><b>Only-forward on the report path itself.</b> A handler that throws
/// while reporting a throw is strictly worse than no handler. Every sink here
/// is wrapped in its own unfiltered catch-all and the two are independent, so
/// neither can prevent the other; message formatting is guarded too, because
/// <c>ToString()</c> on a hostile exception can itself throw. In the worst case
/// both sinks fail silently and behaviour is exactly today's — never worse.</para>
/// </summary>
internal static class ApplicateFatalReport
{
    /// <summary>Diagnostic tag shared by every fatal report, so the whole family is greppable in one pass.</summary>
    private const string Tag = "fatal";

    /// <summary>Append-only crash record, beside <c>settings.json</c> and <c>applicate-session.json</c>.</summary>
    internal const string CrashFileName = "applicate-crash.log";

    /// <summary>
    /// Hard per-process ceiling on durable records. The dispatcher and app-domain
    /// hooks fire at most once (the process is dying), but
    /// <see cref="TaskScheduler.UnobservedTaskException"/> fires on finalization
    /// of ANY faulted task nobody awaited, so a bad build could otherwise grow
    /// this file without bound. A counter and a branch — not a timer, not a
    /// rotation policy that would add its own failure modes to a fatal path.
    /// </summary>
    private const int MaxDurableRecordsPerProcess = 32;

    private static int _installed;
    private static int _durableRecordsWritten;

    /// <summary>
    /// The dispatcher this type is currently hooked to, NOT a bool "installed"
    /// flag. The invariant worth holding is "the hook is attached to the CURRENT
    /// UI dispatcher", and a bool cannot express it: it reads as installed while
    /// the subscription sits on a dispatcher instance that no longer receives
    /// anything, and then actively PREVENTS re-attaching. Production has exactly
    /// one dispatcher for the process lifetime so the two agree there, but the
    /// difference is observable — <c>HeadlessUnitTestSession</c> hands out a
    /// fresh <c>Dispatcher</c> per dispatch on the same thread, and the bool
    /// version left the hook silently dead from the second test onwards.
    /// </summary>
    private static Dispatcher? _hookedDispatcher;

    /// <summary>
    /// Test-only redirect for the durable sink, so <c>dotnet test</c> never
    /// writes into the operator's real application-data directory. Null in
    /// production; there is no environment or command-line read here — a lower
    /// module reading ambient scenario policy is an upward control-flow leak.
    /// </summary>
    private static string? _crashDirectoryOverride;

    /// <summary>
    /// Subscribe the two process-wide hooks. Idempotent, and deliberately O(1):
    /// two event subscriptions and nothing else. This runs on EVERY launch, so
    /// it must not touch the disk — the crash directory is resolved lazily
    /// inside the fatal path, which by definition runs at most once.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        // Guarded for the same reason the report path is: arming the net must
        // never be the thing that takes the process down at startup.
        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }
        catch (Exception ex)
        {
            Report("install", ex, "hook=appdomain+taskscheduler");
        }
    }

    /// <summary>
    /// Subscribe the UI-thread hook. Separate from <see cref="Install"/> because
    /// it needs a dispatcher: <c>Dispatcher.UnhandledException</c> is an
    /// INSTANCE event on <c>Dispatcher.UIThread</c> (verified against the pinned
    /// Avalonia.Base 12.1.0, not inferred from the API shape), and touching
    /// <c>Dispatcher.UIThread</c> before Avalonia's platform setup would bind a
    /// dispatcher against an unresolved threading interface. The composition
    /// root therefore calls this from <c>AppBuilder.AfterSetup</c>, which runs
    /// after setup completes and before the lifetime starts.
    /// </summary>
    public static void InstallDispatcherHook()
    {
        try
        {
            // Inside the try: reaching Dispatcher.UIThread is itself the part
            // that can throw if this is ever called before platform setup.
            var dispatcher = Dispatcher.UIThread;
            if (ReferenceEquals(Interlocked.Exchange(ref _hookedDispatcher, dispatcher), dispatcher))
            {
                return;
            }

            dispatcher.UnhandledException += OnDispatcherUnhandledException;
        }
        catch (Exception ex)
        {
            Report("install", ex, "hook=dispatcher");
        }
    }

    /// <summary>
    /// The UI-thread path, where most of these live and where the context is
    /// richest.
    /// </summary>
    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // e.Handled is DELIBERATELY NOT SET, and that is the whole decision.
        // Setting it here would swallow an unknown failure and carry on over
        // state that may already be corrupt. Do not "fix the crash" by adding
        // it: the fix for a failure known to be safe belongs at the site that
        // knows it is safe. ApplicateFatalReportTests pins this behaviourally —
        // a subscriber registered after this one asserts Handled is still false.
        Report("dispatcher", e.Exception, "handled=false");
    }

    /// <summary>
    /// The last-resort net. Notification-only by nature — it cannot stop the
    /// process — and a failure reaching it has already escaped the dispatcher
    /// hook above. Both firing for one failure is expected, not duplication:
    /// they are two independent nets and the record names which one caught it.
    ///
    /// <para><b>Internal, and subscribed directly, with no wrapper between the
    /// subscription and the body the tests drive.</b> An earlier draft split the
    /// two, and arming the guard against a deliberately introduced swallow
    /// proved the guard could not see it: the mutation went into the subscribed
    /// wrapper while the test exercised the inner method. One method, one body,
    /// tested where production actually enters.</para>
    /// </summary>
    internal static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => Report(
            "appdomain",
            e.ExceptionObject,
            string.Create(CultureInfo.InvariantCulture, $"terminating={e.IsTerminating}"));

    /// <summary>
    /// A different class of failure: a task fault nobody awaited. It does not
    /// kill the process in modern .NET, which is exactly why it is worth
    /// recording — it is otherwise invisible. Subscribed directly, for the same
    /// reason as the handler above.
    /// </summary>
    internal static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // e.SetObserved() is DELIBERATELY NOT CALLED. Observing a fault purely
        // to silence it is the same swallow this type refuses everywhere else.
        // TheUnobservedTaskHookRecordsTheFaultWithoutObservingIt pins it, and
        // that test enters through THIS method.
        Report("unobserved-task", e.Exception, "observed=false");
    }

    /// <summary>
    /// Write one failure to both sinks. Never throws, for any
    /// <paramref name="failure"/>, in any process state.
    /// </summary>
    /// <param name="origin">Which hook caught it — <c>dispatcher</c>,
    /// <c>appdomain</c>, <c>unobserved-task</c> or <c>install</c>.</param>
    /// <param name="failure">Typed as <see cref="object"/> rather than
    /// <see cref="Exception"/> on purpose: <see cref="UnhandledExceptionEventArgs.ExceptionObject"/>
    /// is an object and is not required to be an Exception at all.</param>
    /// <param name="fields">Extra <c>key=value</c> context for the trace line.</param>
    internal static void Report(string origin, object? failure, string fields = "")
    {
        var summary = SafeSummary(failure);

        // Two independent sinks with independent guards: neither failure can
        // stop the other from being attempted.
        TryEmitTrace(origin, summary, fields);
        TryWriteDurableRecord(origin, failure, fields);
    }

    /// <summary>
    /// Sink 1 — the repository's existing diagnostic convention, flushed
    /// synchronously rather than left queued behind a dying process.
    /// </summary>
    private static void TryEmitTrace(string origin, string summary, string fields)
    {
        try
        {
            ApplicateTrace.Diag(Tag, origin, fields.Length > 0 ? $"{fields} {summary}" : summary);

            // The explicit flush the decision requires. Console.Error is
            // auto-flushing by default, but a redirected writer need not be,
            // and this path has no second chance.
            Console.Error.Flush();
        }
        catch
        {
            // Only-forward: a handler that throws while reporting a throw is
            // strictly worse than no handler.
        }
    }

    /// <summary>
    /// Sink 2 — the durable, out-of-process record. This is the sink that
    /// decides whether the fix works at all, because sink 1 writes to a console
    /// the shipped WinExe does not have.
    /// </summary>
    private static void TryWriteDurableRecord(string origin, object? failure, string fields)
    {
        try
        {
            var written = Interlocked.Increment(ref _durableRecordsWritten);
            if (written > MaxDurableRecordsPerProcess)
            {
                return;
            }

            var record = BuildRecord(origin, failure, fields, capped: written == MaxDurableRecordsPerProcess);
            var directory = _crashDirectoryOverride ?? ResolveCrashDirectory();
            WriteRecord(directory, record);
        }
        catch
        {
            // Only-forward, same contract as sink 1. An unwritable directory, a
            // full disk, a denied ACL and a hostile exception all land here and
            // leave behaviour exactly as it was before this type existed.
        }
    }

    /// <summary>
    /// Append <paramref name="record"/> to the crash file and force it out.
    ///
    /// <para>No lock: the dispatcher hook runs on the UI thread while the
    /// unobserved-task hook runs on the finalizer thread, and a lock held by a
    /// thread that is in the middle of dying would block the other one forever.
    /// Each record is written with a single <c>Write</c> call into a handle
    /// opened in append mode, which the OS positions atomically.</para>
    ///
    /// <para><c>Flush(flushToDisk: true)</c> rather than a plain flush: handing
    /// the bytes to the OS is already enough to survive THIS process dying
    /// (runtime-verified), and forcing them past the OS cache additionally
    /// covers an OS crash or power loss. Both are free on a path that runs at
    /// most a handful of times per process.</para>
    /// </summary>
    internal static void WriteRecord(string directory, string record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CrashFileName);
        var bytes = Encoding.UTF8.GetBytes(record);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// The full record: a UTC timestamp (never a local one — the reader may be
    /// anywhere), the origin, and the exception's own <c>ToString()</c>, which
    /// carries the stack trace and the inner-exception chain.
    /// </summary>
    internal static string BuildRecord(string origin, object? failure, string fields, bool capped)
    {
        var builder = new StringBuilder();
        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            builder.Append(CultureInfo.InvariantCulture, $"---- {Tag} {timestamp} origin={origin}");
            if (fields.Length > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $" {fields}");
            }

            builder.AppendLine();
            builder.AppendLine(SafeDetail(failure));

            if (capped)
            {
                builder.AppendLine(
                    $"---- {Tag} record cap of {MaxDurableRecordsPerProcess} reached; " +
                    "further records from this process are not written.");
            }
        }
        catch
        {
            // Formatting itself is guarded: ToString() on a hostile exception
            // can throw, and losing the record entirely would be worse than
            // recording that a failure occurred without its detail.
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"---- {Tag} origin={origin} <record could not be formatted>");
        }

        return builder.ToString();
    }

    /// <summary>One greppable line: type and message, matching the shape <see cref="ApplicateDispatch"/> emits.</summary>
    private static string SafeSummary(object? failure)
    {
        try
        {
            return failure switch
            {
                Exception ex => $"ex={ex.GetType().Name} msg={ex.Message}",
                null => "ex=<null>",
                _ => $"ex={failure.GetType().Name} msg={failure}",
            };
        }
        catch
        {
            return "ex=<unformattable>";
        }
    }

    /// <summary>The full detail, including stack trace and inner exceptions.</summary>
    private static string SafeDetail(object? failure)
    {
        try
        {
            return failure?.ToString() ?? "<null failure>";
        }
        catch
        {
            return "<failure detail could not be rendered>";
        }
    }

    /// <summary>
    /// Deliberately the same rule as <c>JsonSettingsStore.ResolveSettingsRootDirectory</c>
    /// and <c>JsonApplicateSessionStore.ResolveSessionRootDirectory</c>, so the
    /// crash record lands beside the settings and session files rather than in a
    /// fourth place nobody thinks to look. It is re-typed here rather than
    /// shared because the two existing copies live in different assemblies and
    /// factoring out a common per-user-data-directory owner would widen this
    /// change past the composition root; that missing owner is reported as an
    /// adjacent finding rather than fixed here.
    /// </summary>
    private static string ResolveCrashDirectory()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "MarkMello")
            : Path.Combine(appDataDirectory, "MarkMello");
    }

    /// <summary>
    /// Point the durable sink somewhere disposable for the duration of a test.
    /// Pass <c>null</c> to restore the real location.
    /// </summary>
    internal static void SetCrashDirectoryForTesting(string? directory)
    {
        _crashDirectoryOverride = directory;
        Interlocked.Exchange(ref _durableRecordsWritten, 0);
    }
}
