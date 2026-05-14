using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal sealed class ApplicateDocumentMiniatureView : Control
{
    private ApplicateMarkdownDocumentView? _sourceDocumentView;
    private ApplicateDocumentMiniatureSnapshot _snapshot = ApplicateDocumentMiniatureSnapshot.Empty;

    public ApplicateDocumentMiniatureView()
    {
        Focusable = false;
        IsTabStop = false;
        ClipToBounds = true;
    }

    public void SetSource(ApplicateMarkdownDocumentView sourceDocumentView, ApplicateDocumentMiniatureSnapshot snapshot)
    {
        _sourceDocumentView = sourceDocumentView;
        _snapshot = snapshot;
        InvalidateVisual();
    }

    public void ClearSource()
    {
        _sourceDocumentView = null;
        _snapshot = ApplicateDocumentMiniatureSnapshot.Empty;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_sourceDocumentView is null || _snapshot.IsEmpty || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        using (context.PushOpacity(0.68))
        {
            _sourceDocumentView.RenderMiniature(context, new Rect(0, 0, Bounds.Width, Bounds.Height));
        }
    }
}
