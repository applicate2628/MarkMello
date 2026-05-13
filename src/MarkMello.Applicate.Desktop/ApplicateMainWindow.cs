using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Presentation;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Applicate.Desktop;

public sealed class ApplicateMainWindow : MainWindow
{
    // Real bounds so WebView2 initialises correctly; Margin pushes the HWND
    // far enough offscreen that no part of it intersects the visible window
    // even on multi-monitor setups. HorizontalAlignment.Left +
    // VerticalAlignment.Top stop the panel from stretching to fill BodyPanel.
    // Evidence: scratch smoke at .scratch/webview-smoke/run.out.txt verified
    // the MarkMello renderer reaches all readiness gates with viewport
    // 640x360 while parked at Margin=-5000 with these settings.
    private const double WarmupPanelWidth = 1024;
    private const double WarmupPanelHeight = 768;
    private static readonly Thickness WarmupPanelMargin = new(-5000, 0, 0, 0);

    public ApplicateMainWindow(
        MainWindowViewModel viewModel,
        StartupSmokeTestOptions startupSmokeTestOptions,
        ISettingsStore settings)
        : base(viewModel, startupSmokeTestOptions, settings)
    {
        var viewerTemplate = new ApplicateViewerTemplate();
        var editWorkspaceTemplate = new ApplicateEditWorkspaceTemplate();
        DataTemplates.Insert(0, editWorkspaceTemplate);
        DataTemplates.Insert(0, viewerTemplate);
        InstallViewerHostTemplate(viewerTemplate);
        InstallSharedWebViewWarmupPanel();
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
    }

    private void InstallViewerHostTemplate(IDataTemplate viewerTemplate)
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        var viewerHost = bodyPanel?.Children
            .OfType<ContentControl>()
            .FirstOrDefault(static control => control.GetType() == typeof(ContentControl) && control.Name is null);
        if (viewerHost is null)
        {
            return;
        }

        viewerHost.ContentTemplate = viewerTemplate;
    }

    private void InstallSharedWebViewWarmupPanel()
    {
        var bodyPanel = this.FindControl<Panel>("BodyPanel");
        var sharedHost = App.Services?.GetService<IApplicateSharedWebViewHost>();
        if (bodyPanel is null || sharedHost is null)
        {
            return;
        }

        var warmupPanel = new Panel
        {
            Width = WarmupPanelWidth,
            Height = WarmupPanelHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = WarmupPanelMargin,
            IsHitTestVisible = false,
            UseLayoutRounding = true
        };

        bodyPanel.Children.Add(warmupPanel);
        sharedHost.SetWarmupParent(warmupPanel);
    }
}
