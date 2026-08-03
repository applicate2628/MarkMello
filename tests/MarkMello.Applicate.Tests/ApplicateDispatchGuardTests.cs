using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Behavioural counterpart to the text scan in
/// <see cref="DispatchAwaitDisciplineTests"/>. The scanner proves the guarded
/// SHAPE is present at every call site; this proves the shape actually contains
/// a failure — a distinction the sibling bug registry learned the hard way,
/// where several guards were green because they asserted nothing observable.
///
/// Covers work-items/bugs/2026-08-03-production-async-post-lambdas-are-unguarded.md.
/// </summary>
public sealed class ApplicateDispatchGuardTests
{
    [Fact]
    public async Task PostGuardedReportsAFailingBodyAndLeavesTheDispatcherRunning()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            // `return 0;` forces Dispatch<TResult>(Func<Task<TResult>>) rather
            // than the Task<Task> overload -- see
            // 2026-07-26-async-lambda-dispatch-overload-swallows-exceptions.md.
            // Without it every assertion below would be silently discarded.
            await session.Dispatch(async () =>
            {
                // RunContinuationsAsynchronously so resuming this test cannot be
                // inlined INTO the guarded body's SetResult call -- the assertions
                // must observe the state after the body's catch has run, and that
                // ordering is structural here, never a delay.
                var bodyReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var laterJobRan = false;

                ApplicateDispatch.PostGuarded(
                    async () =>
                    {
                        await Task.Yield();
                        bodyReached.SetResult();
                        throw new InvalidOperationException("guarded-boom");
                    },
                    "unit-test-site");

                // Queued behind the guarded post at the same priority: it can only
                // run if the failure above did NOT tear the dispatcher loop down.
                Dispatcher.UIThread.Post(() => laterJobRan = true);

                await bodyReached.Task;

                // Background sits strictly below Default, so this round trip
                // cannot complete until every Default-priority job already queued
                // -- including the throwing continuation and its catch -- has run.
                // Priority ordering, not a timer.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.True(laterJobRan, "a guarded failure must not stop the dispatcher from running later jobs");
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // The whole point of the fix: the failure becomes REPORTED, not silent
        // and not fatal. Assert the diagnostic carries enough to debug from --
        // the family tag, the call site, the exception type and its message.
        var log = captured.ToString();
        Assert.Contains("async-post", log, StringComparison.Ordinal);
        Assert.Contains("unit-test-site", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
        Assert.Contains("guarded-boom", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostGuardedRunsASucceedingBodyToCompletionAndReportsNothing()
    {
        // The success path must be untouched by the guard: the body still runs
        // to completion on the UI thread, and no diagnostic noise is emitted for
        // work that did not fail.
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);

        try
        {
            await session.Dispatch(async () =>
            {
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var ranOnUiThread = false;

                ApplicateDispatch.PostGuarded(
                    async () =>
                    {
                        await Task.Yield();
                        ranOnUiThread = Dispatcher.UIThread.CheckAccess();
                        completed.SetResult();
                    },
                    "unit-test-success-site");

                await completed.Task;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                Assert.True(ranOnUiThread, "the body must resume on the UI thread after its await");
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.DoesNotContain("async-post", captured.ToString(), StringComparison.Ordinal);
    }
}
