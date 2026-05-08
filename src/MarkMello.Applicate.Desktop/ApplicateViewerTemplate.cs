using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateViewerTemplate : IDataTemplate
{
    public Control Build(object? param) => new ApplicateViewerView
    {
        DataContext = param
    };

    public bool Match(object? data) => data is MainWindowViewModel;
}
