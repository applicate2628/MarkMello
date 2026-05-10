using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MarkMello.Application.Abstractions;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;

namespace MarkMello.Applicate.Desktop;

public sealed class ApplicateMainWindow : MainWindow
{
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
}
