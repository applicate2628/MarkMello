using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MarkMello.Application.Abstractions;
using MarkMello.Domain.Diagnostics;
using MarkMello.Presentation.Diagnostics;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Presentation;

public partial class App : global::Avalonia.Application
{
    /// <summary>
    /// Сервис-провайдер, передаваемый из Program.Main до создания AppBuilder.
    /// Statiс — обусловлено тем, что Avalonia сама создаёт инстанс App.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    public static void RegisterServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    public override void Initialize()
    {
        StartupDiag.DiagMs("startup-app", "app-initialize-start");
        StartupDiag.DiagMs("startup-app", "xaml-load-start");
        AvaloniaXamlLoader.Load(this);
        StartupDiag.DiagMs("startup-app", "xaml-load-end");

        StartupDiag.DiagMs("startup-app", "resources-load-start");
        var localization = Services?.GetService<ILocalizationService>() ?? new LocalizationService();
        Resources["Localization"] = localization;
        StartupDiag.DiagMs("startup-app", "resources-load-end");

        StartupDiag.DiagMs("startup-app", "app-initialize-end");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupDiag.DiagMs("startup-app", "framework-init-start");

        if (Services is null)
        {
            base.OnFrameworkInitializationCompleted();
            StartupDiag.DiagMs("startup-app", "framework-init-end");
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var metrics = Services.GetRequiredService<IStartupMetrics>();
            StartupDiag.DiagMs("startup-app", "resolve-mainwindow-start");
            var window = Services.GetRequiredService<MainWindow>();
            StartupDiag.DiagMs("startup-app", "resolve-mainwindow-end");

            // Stage 2 фиксируем после первого Opened — это момент, когда окно реально показалось пользователю,
            // а не просто инстанцировано.
            window.Opened += (_, _) => metrics.Mark(StartupStage.FirstWindow);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
        StartupDiag.DiagMs("startup-app", "framework-init-end");
    }
}
