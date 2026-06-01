using Avalonia;
using MarkMello.Application;
using MarkMello.Application.Abstractions;
using MarkMello.Application.Diagnostics;
using MarkMello.Applicate.Desktop.Activation;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Settings;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure;
using MarkMello.Infrastructure.Diagnostics;
using MarkMello.Infrastructure.Platform;
using MarkMello.Infrastructure.Settings;
using MarkMello.Presentation;
using MarkMello.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarkMello.Applicate.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Anchors ApplicateTrace.ProcessStart on the very first line of Main.
        // Subsequent DiagMs() markers in this file and elsewhere measure
        // elapsed ms relative to this moment (round-2 perf-engineer plan
        // item C; the same Stopwatch shape was already proven by the
        // existing "[startup] AppBootstrap" / "[startup] FirstWindow" lines).
        ApplicateTrace.Touch();
        ApplicateTrace.DiagMs("startup-pre-window", "program-main-enter");

        if (!ApplicateSingleInstanceService.TryCreatePrimary(out var singleInstance))
        {
            return ApplicateSingleInstanceService.ForwardActivation(args) ? 0 : 1;
        }

        var metrics = new StopwatchStartupMetrics();
        metrics.Mark(StartupStage.AppBootstrap);

        try
        {
            ApplicateTrace.DiagMs("startup-pre-window", "configure-services-start");
            var services = ConfigureServices(metrics, args, singleInstance);
            ApplicateTrace.DiagMs("startup-pre-window", "configure-services-end");
            App.RegisterServices(services);
            ApplicateTrace.DiagMs("startup-pre-window", "single-instance-start");
            singleInstance!.StartListening();
            ApplicateTrace.DiagMs("startup-pre-window", "single-instance-end");

            // PE r2 §2 item D — Parallelize active-document I/O with shell load.
            // Fire-and-forget thread-pool task that pre-reads the argv document
            // (file read + canonicalization) and deposits it into
            // EarlyDocumentCache. By the time MainWindowViewModel.InitializeAsync
            // reaches LoadDocumentAsync (~273 ms cost serially per PE r2 §1 P2),
            // the pre-read is typically complete and the VM consumes the cache
            // entry instead of running File.ReadAllTextAsync itself.
            //
            // Constraints (PE r2 §4 + orchestrator brief):
            //  - swallow + log any I/O / parse exception so the process never
            //    crashes from the pre-read (cache stays empty -> VM falls
            //    through to existing path with its own typed-error handling)
            //  - canonicalize via Path.GetFullPath both at deposit (here) and
            //    at consume (VM) — matches existing CommandLineActivation +
            //    FileDocumentLoader canonicalization
            //  - skip entirely when no argv path is available (no benefit,
            //    no risk)
            //  - HTML body prime uses the already-built DI provider but keeps
            //    the file source rendezvous in EarlyDocumentCache so VM load
            //    behavior remains unchanged.
            StartActiveDocumentPreRead(args, services);

            // Multi-tab startup-scaling polish: also pre-read every path
            // listed in the persisted session file (OpenPaths) on the
            // thread pool. Deposits land in the same EarlyDocumentCache;
            // when the user's session has N tabs, the active tab consumes
            // its entry just like the argv path, and inactive stub-tabs
            // (which only materialize on click) hit the cache too if the
            // pre-read completed first. Cost on disk: O(N) bytes per
            // session file, but all reads overlap Avalonia init / shell
            // bootstrap. The session file is tiny JSON so reading it
            // synchronously on the main thread is cheaper than spinning
            // up another task just to discover the list of paths.
            StartSessionTabsPreRead();

            ApplicateTrace.DiagMs("startup-pre-window", "appbuilder-configure-start");
            var appBuilder = BuildAvaloniaApp();
            ApplicateTrace.DiagMs("startup-pre-window", "appbuilder-configure-end");

            ApplicateTrace.DiagMs("startup-pre-window", "classic-lifetime-start");
            return appBuilder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstance!.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Schedule a thread-pool pre-read of the argv document and deposit the
    /// result into <see cref="EarlyDocumentCache"/>. Skips entirely when no
    /// supported argv path is detected. All exceptions are caught and logged;
    /// on failure the cache stays empty and the view model's existing
    /// <c>FileDocumentLoader</c> path runs unchanged.
    /// </summary>
    private static void StartActiveDocumentPreRead(string[] args, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        string? activationPath;
        try
        {
            activationPath = new CommandLineActivation(args).GetActivationFilePath();
        }
        catch (Exception ex)
        {
            ApplicateTrace.Diag("startup-pre-window", $"perf-doc resolve-failed ex={ex.GetType().Name}");
            return;
        }

        if (string.IsNullOrEmpty(activationPath))
        {
            ApplicateTrace.DiagMs("startup-pre-window", "perf-doc skipped reason=no-argv-doc");
            return;
        }

        // Thread-pool fire-and-forget. The Task is intentionally not awaited
        // anywhere; the rendezvous is the cache lookup in
        // MainWindowViewModel.LoadDocumentAsync. The task must not propagate
        // any exception (no unhandled async exception crash) — both the read
        // and the deposit are wrapped in a single catch-all.
        _ = Task.Run(async () =>
        {
            try
            {
                ApplicateTrace.DiagMs("startup-pre-window", "perf-doc read-start", $"path={activationPath}");

                // Canonical absolute path — both deposit and consume key on
                // Path.GetFullPath (constraint 4: avoid argv-relative vs
                // absolute-path miss). CommandLineActivation already returns
                // a full path but call again for safety / symmetry with
                // FileDocumentLoader's own canonicalization.
                var canonical = Path.GetFullPath(activationPath);
                var content = File.ReadAllText(canonical);

                var source = new MarkdownSource(
                    Path: canonical,
                    FileName: Path.GetFileName(canonical),
                    Content: content);

                EarlyDocumentCache.Deposit(canonical, source);
                ApplicateTrace.DiagMs("startup-pre-window", "perf-doc read-done", $"bytes={content.Length}");
                await PrimeActiveDocumentRenderedBodyCacheAsync(services, source, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Swallow and log — VM will fall through to FileDocumentLoader
                // which has its own typed-error handling.
                ApplicateTrace.Diag("startup-pre-window", $"perf-doc read-failed ex={ex.GetType().Name} msg={ex.Message}");
            }
        });
    }

    private static async Task PrimeActiveDocumentRenderedBodyCacheAsync(
        IServiceProvider services,
        MarkdownSource source,
        CancellationToken cancellationToken)
    {
        var cache = services.GetRequiredService<ApplicateRenderedBodyCache>();
        var imageSourceResolver = services.GetService<IImageSourceResolver>();
        if (!cache.CanCache(source, imageSourceResolver))
        {
            ApplicateTrace.DiagMs(
                "startup-pre-window",
                "perf-doc body-prime-skipped",
                $"path={source.Path} reason=uncacheable");
            return;
        }

        var renderer = services.GetRequiredService<IApplicateHtmlMarkdownRenderer>();
        var settings = services.GetRequiredService<ISettingsStore>();
        var preferences = await settings.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        var renderedFromMarkdown = false;
        await cache.GetOrRenderAsync(
                source,
                preferences,
                imageSourceResolver,
                async ct =>
                {
                    renderedFromMarkdown = true;
                    ApplicateTrace.DiagMs(
                        "startup-pre-window",
                        "perf-doc body-prime-render-start",
                        $"path={source.Path}");
                    var body = await renderer.RenderBodyAsync(source, preferences, imageSourceResolver, ct)
                        .ConfigureAwait(false);
                    ApplicateTrace.DiagMs(
                        "startup-pre-window",
                        "perf-doc body-prime-render-end",
                        $"path={source.Path} htmlLength={body.BodyHtml.Length}");
                    return body;
                },
                cancellationToken)
            .ConfigureAwait(false);

        ApplicateTrace.DiagMs(
            "startup-pre-window",
            renderedFromMarkdown ? "perf-doc body-prime-stored" : "perf-doc body-prime-cache-hit",
            $"path={source.Path}");
    }

    /// <summary>
    /// Load the persisted session synchronously (small JSON file, sub-ms
    /// read) and dispatch parallel thread-pool reads for every listed
    /// path. Each read canonicalizes via <see cref="Path.GetFullPath"/>
    /// and deposits into <see cref="EarlyDocumentCache"/>. The deposits
    /// are fire-and-forget; the cache lookup in
    /// <c>MainWindowViewModel.LoadDocumentAsync</c> (for the active tab)
    /// and <c>OpenDocumentsService.EnsureLoadedAsync</c> (for stub tabs
    /// activated by the user later) is the rendezvous. All exceptions
    /// are swallowed so a single bad path does not poison the cold-start
    /// path; on failure the cache stays empty for that key and the VM /
    /// service falls through to its normal disk-read with typed-error
    /// handling.
    /// </summary>
    private static void StartSessionTabsPreRead()
    {
        ApplicateSession session;
        try
        {
            // Synchronous load — JsonApplicateSessionStore.LoadAsync is a
            // ValueTask wrapping File.ReadAllText on a small JSON file
            // (typically &lt;1 KB). GetAwaiter().GetResult() avoids
            // dragging the whole Program.Main into an async path just
            // to pull the list of paths.
            session = new JsonApplicateSessionStore().LoadAsync()
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ApplicateTrace.Diag(
                "startup-pre-window",
                $"perf-session-prefetch load-failed ex={ex.GetType().Name}");
            return;
        }

        var paths = session.OpenPaths;
        if (paths is null || paths.Count == 0)
        {
            ApplicateTrace.DiagMs(
                "startup-pre-window",
                "perf-session-prefetch skipped reason=no-session-paths");
            return;
        }

        // Snapshot the count BEFORE dispatching so the marker reflects
        // the requested fan-out, not whatever survives racing the rest
        // of startup. Each Task is independent; concurrent deposits to
        // the ConcurrentDictionary inside EarlyDocumentCache are safe.
        var dispatched = 0;
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            // Capture per-iteration variable for the lambda closure —
            // C# 5+ already scopes foreach this way, but kept explicit
            // for readability.
            var path = rawPath;

            // Fire-and-forget. The thread pool decides parallelism;
            // typical session sizes (3-10 tabs) do not stress the pool.
            _ = Task.Run(() =>
            {
                try
                {
                    var canonical = Path.GetFullPath(path);
                    var content = File.ReadAllText(canonical);
                    var source = new MarkdownSource(
                        Path: canonical,
                        FileName: Path.GetFileName(canonical),
                        Content: content);
                    EarlyDocumentCache.Deposit(canonical, source);
                    ApplicateTrace.Diag(
                        "startup-pre-window",
                        $"perf-session-prefetch deposit path={canonical} bytes={content.Length}");
                }
                catch (Exception ex)
                {
                    // Swallow: the stub-tab activation path (or the
                    // active-doc LoadDocumentAsync) re-attempts with its
                    // own typed-error surface. A missing/locked file in
                    // the saved session is non-fatal — the user just
                    // gets a "file unavailable" surface when they click
                    // the tab.
                    ApplicateTrace.Diag(
                        "startup-pre-window",
                        $"perf-session-prefetch failed path={path} ex={ex.GetType().Name}");
                }
            });
            dispatched++;
        }

        ApplicateTrace.DiagMs(
            "startup-pre-window",
            "perf-session-prefetch dispatched",
            $"count={dispatched}");
    }

    private static ServiceProvider ConfigureServices(
        IStartupMetrics metrics,
        string[] args,
        ApplicateSingleInstanceService? singleInstance)
    {
        var collection = new ServiceCollection();
        collection.AddInfrastructure(metrics, args);
        if (singleInstance is not null)
        {
            collection.AddSingleton(singleInstance);
        }
        collection.Replace(ServiceDescriptor.Singleton<IMarkdownDocumentRenderer, ApplicateMarkdownDocumentRenderer>());

        // Wrap the upstream ISettingsStore with the Applicate-side renderer-
        // backend coercion (design D8 / Phase 3). The wrapper is the OUTER
        // ISettingsStore; the inner JsonSettingsStore continues to own the
        // disk file. Constructing JsonSettingsStore here keeps the same
        // disk-file resolution behavior as the upstream registration in
        // AddInfrastructure (ApplicationData\MarkMello\settings.json).
        collection.Replace(ServiceDescriptor.Singleton<ISettingsStore>(
            static _ => new ApplicateRendererCoercingSettingsStore(new JsonSettingsStore())));

        collection.AddSingleton<ApplicateWebAssetEmbedder>();
        collection.AddSingleton<IApplicateHtmlMarkdownRenderer, ApplicateHtmlMarkdownRenderer>();
        collection.AddSingleton<ApplicateRenderedBodyCache>();
        collection.AddSingleton<IApplicateShellAssetBundleFactory, ApplicateShellAssetBundleFactory>();
        collection.AddSingleton<IApplicateSharedWebViewHostProvider, ApplicateSharedWebViewHostProvider>();
        collection.AddSingleton<IApplicateSharedWebViewHost>(
            static provider => provider.GetRequiredService<IApplicateSharedWebViewHostProvider>().ViewerHost);
        // D-phase race fix: VM cache-hit branch awaits this readiness signal
        // before publishing Document / State so the renderer pipeline can
        // complete its initial-visible-ready pass without a mid-load reparent.
        collection.AddSingleton<IRendererReadinessService, ApplicateRendererReadinessService>();
        collection.AddSingleton<IOpenDocumentsService, OpenDocumentsService>();
        collection.AddSingleton<IApplicateSessionStore>(new JsonApplicateSessionStore());
        collection.AddApplication();
        collection.AddPresentation();
        collection.Replace(ServiceDescriptor.Singleton<MainWindow>(provider => new ApplicateMainWindow(
            provider.GetRequiredService<MarkMello.Presentation.ViewModels.MainWindowViewModel>(),
            provider.GetRequiredService<StartupSmokeTestOptions>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetService<ApplicateSingleInstanceService>())));

        return collection.BuildServiceProvider();
    }
}
