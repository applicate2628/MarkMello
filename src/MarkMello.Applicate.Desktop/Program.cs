using Avalonia;
using MarkMello.Application;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Activation;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Settings;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure;
using MarkMello.Infrastructure.Diagnostics;
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
        collection.AddSingleton<IApplicateShellAssetBundleFactory, ApplicateShellAssetBundleFactory>();
        collection.AddSingleton<IApplicateSharedWebViewHost, ApplicateSharedWebViewHost>();
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
