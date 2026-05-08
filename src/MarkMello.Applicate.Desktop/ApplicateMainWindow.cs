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
        DataTemplates.Insert(0, new ApplicateViewerTemplate());
        Opened += (_, _) => Title = $"{Title} [Applicate overlay]";
    }
}
