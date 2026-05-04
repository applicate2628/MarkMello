using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal sealed class DocumentMiniatureView : Control
{
    private const double DefaultWidth = 136;
    private MarkdownDocumentView? _sourceDocumentView;
    private DocumentMiniatureSnapshot _snapshot = DocumentMiniatureSnapshot.Empty;

    public DocumentMiniatureView()
    {
        Focusable = false;
        IsTabStop = false;
        UseLayoutRounding = true;
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    public void SetSource(MarkdownDocumentView sourceDocumentView, DocumentMiniatureSnapshot snapshot)
    {
        _sourceDocumentView = sourceDocumentView;
        _snapshot = snapshot;
        InvalidateVisual();
    }

    public void ClearSource()
    {
        _sourceDocumentView = null;
        _snapshot = DocumentMiniatureSnapshot.Empty;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? DefaultWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_sourceDocumentView is null || _snapshot.IsEmpty || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var background = ResolveBrush("MmSurfaceBrush");
        if (background is not null)
        {
            using (context.PushOpacity(0.12))
            {
                context.DrawRectangle(background, null, new Rect(0, 0, Bounds.Width, Bounds.Height), 6, 6);
            }
        }

        using (context.PushOpacity(0.68))
        {
            _sourceDocumentView.RenderMiniature(context, new Rect(0, 0, Bounds.Width, Bounds.Height));
        }
    }

    private IBrush? ResolveBrush(string resourceKey)
        => this.TryFindResource(resourceKey, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : null;
}
