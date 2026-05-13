using Avalonia;
using MarkMello.Application;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure;
using MarkMello.Infrastructure.Diagnostics;
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
        var metrics = new StopwatchStartupMetrics();
        metrics.Mark(StartupStage.AppBootstrap);

        var services = ConfigureServices(metrics, args);
        App.RegisterServices(services);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static ServiceProvider ConfigureServices(IStartupMetrics metrics, string[] args)
    {
        var collection = new ServiceCollection();
        collection.AddInfrastructure(metrics, args);
        collection.Replace(ServiceDescriptor.Singleton<IMarkdownDocumentRenderer, ApplicateMarkdownDocumentRenderer>());
        collection.AddSingleton<ApplicateWebAssetEmbedder>();
        collection.AddSingleton<IApplicateHtmlMarkdownRenderer, ApplicateHtmlMarkdownRenderer>();
        collection.AddSingleton<IApplicateSharedWebViewHost, ApplicateSharedWebViewHost>();
        collection.AddApplication();
        collection.AddPresentation();
        collection.Replace(ServiceDescriptor.Singleton<MainWindow>(provider => new ApplicateMainWindow(
            provider.GetRequiredService<MarkMello.Presentation.ViewModels.MainWindowViewModel>(),
            provider.GetRequiredService<StartupSmokeTestOptions>(),
            provider.GetRequiredService<ISettingsStore>())));

        return collection.BuildServiceProvider();
    }
}
