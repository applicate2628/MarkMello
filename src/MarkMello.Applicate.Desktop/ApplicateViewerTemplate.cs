using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Presentation;
using MarkMello.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateViewerTemplate : IDataTemplate
{
    public Control Build(object? param) => new ApplicateViewerView(
        App.Services?.GetService<IApplicateHtmlMarkdownRenderer>())
    {
        DataContext = param
    };

    public bool Match(object? data) => data is MainWindowViewModel;
}
