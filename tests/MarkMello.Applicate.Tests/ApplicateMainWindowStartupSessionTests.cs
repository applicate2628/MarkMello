using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Tests.Fakes;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Falsifying guard for
/// <c>work-items/bugs/2026-07-26-reveal-gate-held-for-a-document-that-never-arrives.md</c>.
///
/// <para>One launch performs two INDEPENDENT window-side reads of the session file — the
/// constructor's hold decision (<c>ApplicateMainWindow.cs:179</c>) and the posted bridge restore
/// (<c>:3665</c>) — and nothing reconciles them. When the first succeeds and the second fails, the
/// reveal gate is installed for a startup document that then never restores.</para>
///
/// <para><b>What these tests can and cannot observe.</b> They assert the READ side of that
/// disagreement: how many physical window-side loads happen, which queued results get consumed, and
/// what the restore path did with the observation it got.</para>
///
/// <para><b>NOT asserted: reveal release.</b> Under the Avalonia Headless harness no
/// <c>IApplicateSharedWebViewHost</c> exists, so <c>ApplicateAirspaceCompositor.RegisterStartupSession</c>
/// takes its <c>signals is null</c> branch (<c>ApplicateAirspaceCompositor.cs:83-91</c>), sets
/// <c>Opacity = 1</c> and emits <c>startup-window-reveal-released reason=no-viewer-host</c>
/// synchronously inside the constructor — before the bridge post has even run, and identically
/// whether or not the defect is present. Reveal release is therefore not a discriminating observable
/// in this harness; asserting on it would be a guaranteed pass that proves nothing. The compositor's
/// reveal protocol is covered directly by <c>ApplicateAirspaceCompositorTests</c>.</para>
///
/// <para><b>NOT asserted: that the startup document reaches
/// <c>IOpenDocumentsService.OpenDocuments</c>.</b> The restore's full open runs a real threadpool
/// <c>File.ReadAllTextAsync</c> (<c>OpenDocumentsService.cs:63</c>), whose completion races the
/// Background-priority drain sentinel below — whichever posts to the dispatcher first wins, and the
/// test synchronises with neither. Proven, not assumed: with the startup file grown to 64 MB the
/// membership assertion failed with <c>Collection: []</c> while every count assertion in the same
/// run still passed. A guard that green only because a small file happened to be in the OS cache
/// would teach the next reader to re-run until green, so it is gone. What replaces it —
/// <c>viewModel.RecentFiles</c> — is derived from the SAME observed session, by
/// <c>SeedRecentPathsForRestore</c> + <c>SetRecentFiles</c>, with no suspension point between the
/// session read and the mirror: post-fix the bridge's <c>await</c> resolves an already-completed
/// task, so that whole stretch runs inline inside the one dispatcher turn. It is deterministic for a
/// structural reason, not a timing one, and it still discriminates — an implementation that memoized
/// the read but handed the bridge a null would leave it empty.</para>
/// </summary>
[Collection(ApplicateAppServicesTestGroup.Name)]
public sealed class ApplicateMainWindowStartupSessionTests
{
    [Fact]
    public async Task WindowSideStartupObservationIsPerformedExactlyOnceAndRestoresItsDocument()
    {
        using var docs = new TempMarkdownDirectory();
        var docPath = docs.WriteFile("startup.md", "# restored\n");

        // Result 1: an observed session naming a real file. Result 2: an unobserved (null) read —
        // "what the SECOND physical read would return if one were attempted". Post-fix it must stay
        // unconsumed, because there is no second physical window-side read.
        var store = new SequentialApplicateSessionStore(
            new ApplicateSession { OpenPaths = { docPath }, ActivePath = docPath },
            null);

        await RunWindowStartupAsync(store, (_, viewModel) =>
        {
            Assert.Equal(1, store.LoadCallCount);
            Assert.Equal(1, store.RemainingResults);
            Assert.False(
                store.ConsumedUnobservedResult,
                "the contradictory second read must never be performed");
            // The restore consumed result 1's CONTENT, not merely its count: this list is seeded
            // from `saved.OpenPaths` and mirrored to the view model in the same uninterruptible
            // dispatcher turn as the read. See the class remarks for why this, and not
            // OpenDocuments membership, is the deterministic observable here.
            Assert.Contains(
                viewModel.RecentFiles,
                item => string.Equals(item.Path, docPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, store.SavesOverUnobservedBaseline);
        });
    }

    /// <summary>
    /// d13 companion guard: when the first and only window-side observation is unobserved (null),
    /// exactly one load is attempted, nothing restores, and NOTHING is persisted over the baseline
    /// the process could not read — including after the post-restore convergence save runs.
    /// </summary>
    [Fact]
    public async Task UnobservedStartupSessionRestoresNothingAndPersistsNothing()
    {
        using var docs = new TempMarkdownDirectory();

        // Two nulls rather than one: both mean "unobserved", but queuing a second makes a second
        // physical read visible as a COUNT instead of as a queue-exhaustion throw that production's
        // contract-defensive catch would swallow.
        var store = new SequentialApplicateSessionStore(null, null);

        await RunWindowStartupAsync(store, (openDocs, viewModel) =>
        {
            Assert.Equal(1, store.LoadCallCount);
            Assert.Equal(1, store.RemainingResults);
            // Nothing restored. Both halves are deterministic here for the same reason: an
            // unobserved read coalesces to ApplicateSession.Empty, so the restore loop iterates
            // zero times and no open — and therefore no file read — is ever started.
            Assert.Empty(openDocs.OpenDocuments);
            Assert.False(viewModel.HasStoredRecentFiles);
            // The real no-write-over-unobserved check, and the one that does not depend on any
            // file read: the post-restore convergence SaveSession runs to completion inside the
            // same dispatcher turn, and the composer refuses it.
            Assert.Equal(0, store.SaveCallCount);
            Assert.Equal(0, store.SavesOverUnobservedBaseline);
        });
    }

    /// <summary>
    /// Constructs a real <see cref="ApplicateMainWindow"/> over the supplied store, drains the
    /// dispatcher, then runs <paramref name="assert"/> on the UI thread with the real open-documents
    /// service and the real view model the window was built over.
    ///
    /// <para><b>Draining.</b> The window is NOT shown. The bridge restore is posted from the
    /// constructor itself, so it runs without <c>Show()</c>; showing the window would additionally
    /// post <c>InstallStatusHintAboveWebView</c>, whose <c>Popup.IsOpen = true</c> throws
    /// "Unable to create IPopupImpl and no overlay layer is found" under the headless platform. The
    /// drain is a sentinel posted at <see cref="DispatcherPriority.Background"/>, i.e. BELOW the
    /// Normal-priority bridge post and every Normal-priority await continuation behind it. No sleep,
    /// delay, timeout, or fallback advancement is involved.</para>
    /// </summary>
    private static async Task RunWindowStartupAsync(
        SequentialApplicateSessionStore store,
        Action<IOpenDocumentsService, MainWindowViewModel> assert)
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        // The `return 0;` at the end of this lambda is LOAD-BEARING -- do not remove it.
        // HeadlessUnitTestSession.Dispatch overloads on Action, Func<TResult> and Func<Task<TResult>>.
        // A void-bodied async lambda binds to Func<TResult> with TResult=Task, so `await` observes
        // only the OUTER task and every assertion in the body is silently discarded. An explicit
        // return value forces the Func<Task<TResult>> overload, which unwraps the inner task.
        // See work-items/bugs/2026-07-26-headless-dispatch-swallows-assertions.md and the same
        // note at ApplicateSiblingMountTests.cs:1303. Verified here: before this return was added,
        // both tests in this file passed against the unfixed code they are written to fail.
        await headless.Dispatch(async () =>
        {
            using var scope = new ApplicateTestServiceScope(services =>
            {
                services.AddSingleton<ICommandLineActivation>(new NoCommandLineActivation());
                services.AddSingleton<IApplicateSessionStore>(store);
                services.AddSingleton<IOpenDocumentsService, OpenDocumentsService>();
            });

            var viewModel = MinimalMainWindowViewModelFactory.Create();
            _ = new ApplicateMainWindow(
                viewModel,
                new StartupSmokeTestOptions(false, TimeSpan.Zero),
                new NoOpSettingsStore());

            var sentinel = new TaskCompletionSource();
            Dispatcher.UIThread.Post(() => sentinel.SetResult(), DispatcherPriority.Background);
            await sentinel.Task;

            var openDocs = scope.GetService<IOpenDocumentsService>()
                ?? throw new InvalidOperationException("IOpenDocumentsService was not registered.");
            assert(openDocs, viewModel);
            return 0; // load-bearing -- see the overload-resolution comment above.
        }, CancellationToken.None);
    }

    private sealed class TempMarkdownDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "applicate-startup-session-" + Guid.NewGuid().ToString("N"));

        public TempMarkdownDirectory() => Directory.CreateDirectory(_root);

        public string WriteFile(string name, string content)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class NoCommandLineActivation : ICommandLineActivation
    {
        public string? GetActivationFilePath() => null;
    }

    private sealed class NoOpSettingsStore : ISettingsStore
    {
        public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ReadingPreferences.Default);
        public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ThemeMode.Light);
        public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken ct = default)
            => ValueTask.FromResult(AppLanguage.English);
        public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken ct = default)
            => ValueTask.FromResult<WindowPlacement?>(null);
        public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
