using Avalonia.Controls;

namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal sealed class DocumentMinimapView : Grid
{
    private readonly DocumentMiniatureView _miniatureView = new();
    private readonly DocumentMinimapViewportOverlay _overlay = new();
    public DocumentMinimapView()
    {
        Focusable = false;
        IsTabStop = false;
        UseLayoutRounding = true;
        ClipToBounds = true;

        Children.Add(_miniatureView);
        Children.Add(_overlay);

        _overlay.ScrollRequested += OnOverlayScrollRequested;
    }

    public double ScrollOffset
    {
        get => _overlay.ScrollOffset;
        set => _overlay.UpdateScrollState(value, ScrollMaximum, ViewportHeight);
    }

    public double ScrollMaximum
    {
        get => _overlay.ScrollMaximum;
        set => _overlay.UpdateScrollState(ScrollOffset, value, ViewportHeight);
    }

    public double ViewportHeight
    {
        get => _overlay.ViewportHeight;
        set => _overlay.UpdateScrollState(ScrollOffset, ScrollMaximum, value);
    }

    public event EventHandler<DocumentMinimapScrollRequestedEventArgs>? ScrollRequested;

    public void SetSource(MarkdownDocumentView sourceDocumentView, DocumentMiniatureSnapshot snapshot)
    {
        _miniatureView.SetSource(sourceDocumentView, snapshot);
        _overlay.DocumentHeight = snapshot.TotalHeight;
        _overlay.InvalidateVisual();
    }

    public void ClearSource()
    {
        _miniatureView.ClearSource();
        _overlay.DocumentHeight = 0;
        _overlay.InvalidateVisual();
    }

    private void OnOverlayScrollRequested(object? sender, DocumentMinimapScrollRequestedEventArgs e)
        => ScrollRequested?.Invoke(this, e);
}
