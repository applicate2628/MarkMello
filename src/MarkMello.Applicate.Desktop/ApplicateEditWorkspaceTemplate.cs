using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Presentation;
using MarkMello.Presentation.Views;
using MarkMello.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateEditWorkspaceTemplate : IDataTemplate
{
    public Control Build(object? param)
    {
        var view = new EditWorkspaceView
        {
            DataContext = param
        };
        var preview = new ApplicateEditPreviewView(App.Services?.GetService<IApplicateSharedWebViewHost>())
        {
            DataContext = param
        };
        view.TryReplacePreviewDocumentView(preview);
        return view;
    }

    public bool Match(object? data) => data is EditorSessionViewModel;
}
