using System.Threading.Tasks;
using Avalonia.Threading;

namespace MarkMello.Applicate.Desktop.Diagnostics;

/// <summary>
/// The one owner of "post asynchronous work to the UI thread without letting a
/// failure kill the window".
///
/// <para><b>The defect this exists to prevent.</b>
/// <see cref="Dispatcher.Post(System.Action, DispatcherPriority)"/> takes an
/// <see cref="System.Action"/>, so the natural-looking
/// <c>Dispatcher.UIThread.Post(async () =&gt; { ... })</c> compiles to an
/// <c>async void</c> continuation. When such a lambda throws, the exception has
/// no task to land on: <c>AsyncVoidMethodBuilder</c> hands it to the ambient
/// <c>AvaloniaSynchronizationContext</c>, which re-posts it to the dispatcher as
/// an ordinary job (<c>Task.ThrowAsync</c> →
/// <c>SendOrPostCallbackDispatcherOperation.InvokeCore</c>). Nothing in that
/// path catches it, so it escapes the dispatcher loop and TERMINATES THE
/// PROCESS. This was verified at runtime against the pinned
/// Avalonia.Headless 12.1.0, not inferred from the API shape — the filing that
/// prompted this class described the failure as a silent swallow, and the
/// measurement showed the opposite.</para>
///
/// <para>In a <c>WinExe</c> there is no console attached, so the crash is also
/// effectively undiagnosable from the user's side: the window simply
/// disappears and the stack trace goes to a stderr nobody reads.</para>
///
/// <para><b>The remedy, and why it lives here.</b> The fix is the shape this
/// repository already uses for fire-and-forget asynchronous work —
/// <c>Program.StartActiveDocumentPreRead</c> ("the task must not propagate any
/// exception … wrapped in a single catch-all") and
/// <c>ApplicateOpenTabPrefetchDocumentSource.OnUiThreadAsync</c>. Factoring it
/// into a single owner keeps one implementation of the invariant rather than
/// one hand-written copy per call site, and gives the reintroduction guard
/// (<c>DispatchAwaitDisciplineTests</c>) a structural rule it can enforce.</para>
///
/// <para><b>Failure policy.</b> A failure is REPORTED and swallowed, never
/// rethrown. Every caller is a UI event mirror (tab activation, single-instance
/// activation, session restore) whose work is already best-effort; re-throwing
/// would land straight back on the same async void crash path the class
/// exists to close. The report goes to <see cref="ApplicateTrace.Diag"/>, which
/// is always-on in Release, under the fixed <c>async-post</c> tag so the whole
/// family is greppable in one pass.</para>
/// </summary>
internal static class ApplicateDispatch
{
    /// <summary>Diagnostic tag shared by every guarded post.</summary>
    private const string Tag = "async-post";

    /// <summary>
    /// Post <paramref name="work"/> to the UI thread and run it with its
    /// failures contained.
    /// </summary>
    /// <param name="work">The asynchronous work. Because the parameter type is
    /// <see cref="Func{Task}"/> — a single, non-overloaded target — an
    /// <c>async () =&gt; { … }</c> argument binds to it exactly, so the returned
    /// task is the real one and <c>await</c> observes everything inside it.
    /// (Contrast the sibling <c>Task&lt;Task&gt;</c> defect recorded in
    /// <c>2026-07-26-async-lambda-dispatch-overload-swallows-exceptions.md</c>,
    /// which was caused precisely by overload ambiguity.)</param>
    /// <param name="site">Short stable identifier for the call site, emitted as
    /// the diagnostic event name.</param>
    /// <param name="priority">Dispatcher priority. Defaults to
    /// <c>default(DispatcherPriority)</c>, which is what
    /// <c>Dispatcher.Post(action)</c> itself passes — so an unqualified call
    /// here queues identically to the unqualified <c>Post</c> it replaces.</param>
    public static void PostGuarded(
        Func<Task> work,
        string site,
        DispatcherPriority priority = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    // ConfigureAwait(true): these callers mutate UI state after
                    // the await and must resume on the UI thread, matching the
                    // explicit ConfigureAwait(true) already on every await
                    // inside the bodies this wraps.
                    await work().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // Deliberately unfiltered. A typed catch is what the five
                    // original sites had (IOException / UnauthorizedAccessException),
                    // and every other exception type went straight to the crash
                    // path — so narrowing this would reopen the defect.
                    ApplicateTrace.Diag(Tag, site, $"ex={ex.GetType().Name} msg={ex.Message}");
                }
            },
            priority);
    }
}
