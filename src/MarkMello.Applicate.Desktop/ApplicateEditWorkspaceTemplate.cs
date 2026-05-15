using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
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
        if (!view.TryReplacePreviewDocumentView(preview))
        {
            // Upstream merge (d902a7f) removed the `PreviewDocumentFrame` Border
            // name that TryReplacePreviewDocumentView relies on. Locate the
            // upstream MarkdownDocumentView and swap its anonymous Border child.
            var nativeDocView = view.FindControl<MarkdownDocumentView>("PreviewDocumentView");
            if (nativeDocView?.Parent is Border parentBorder)
            {
                // Upstream renders the markdown document inside a centred
                // Border (HorizontalAlignment="Center" MaxWidth=
                // DocumentColumnMaxWidth). The centring is for the native
                // Avalonia document surface that doesn't manage its own
                // column width; our ApplicateEditPreviewView's WebView
                // handles document column width via SendReadingPreferences
                // (the JS limits content to AvailableContentWidth in CSS).
                // Without stretching the Border, it sizes to the toolbar's
                // desired width (~144 px) and visibly shifts to the left
                // edge of the pane once the shared View attaches.
                parentBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
                parentBorder.Child = preview;
            }
        }
        return view;
    }

    public bool Match(object? data) => data is EditorSessionViewModel;
}
