using Avalonia.Controls;

namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal sealed class ApplicateDocumentMinimapView : Grid
{
    private readonly ApplicateDocumentMiniatureView _miniatureView = new();
    private readonly ApplicateDocumentMinimapViewportOverlay _overlay = new();

    public ApplicateDocumentMinimapView()
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

    public event EventHandler<ApplicateDocumentMinimapScrollRequestedEventArgs>? ScrollRequested;

    public void SetSource(ApplicateMarkdownDocumentView sourceDocumentView, ApplicateDocumentMiniatureSnapshot snapshot)
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

    private void OnOverlayScrollRequested(object? sender, ApplicateDocumentMinimapScrollRequestedEventArgs e)
        => ScrollRequested?.Invoke(this, e);
}
