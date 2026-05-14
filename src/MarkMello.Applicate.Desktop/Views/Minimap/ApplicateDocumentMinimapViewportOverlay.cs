using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal sealed class ApplicateDocumentMinimapViewportOverlay : Control
{
    private const double MinThumbHeight = 28.0;
    private bool _isDragging;

    public ApplicateDocumentMinimapViewportOverlay()
    {
        Focusable = false;
        IsTabStop = false;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double ScrollOffset { get; private set; }

    public double ScrollMaximum { get; private set; }

    public double ViewportHeight { get; private set; }

    public double DocumentHeight { get; set; }

    public event EventHandler<ApplicateDocumentMinimapScrollRequestedEventArgs>? ScrollRequested;

    public void UpdateScrollState(double scrollOffset, double scrollMaximum, double viewportHeight)
    {
        ScrollOffset = scrollOffset;
        ScrollMaximum = scrollMaximum;
        ViewportHeight = viewportHeight;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var background = ResolveBrush("MmSurfaceBrush") ?? Brushes.Transparent;
        using (context.PushOpacity(0.16))
        {
            context.DrawRectangle(background, null, Bounds);
        }

        var thumb = ApplicateDocumentMinimapScrollMapper.CalculateViewportThumb(
            Bounds.Width,
            Bounds.Height,
            DocumentHeight,
            ViewportHeight,
            ScrollOffset,
            ScrollMaximum,
            MinThumbHeight);
        if (thumb == default)
        {
            return;
        }

        var fill = ResolveBrush("MmSelectionBrush") ?? ResolveBrush("MmAccentSoftBrush") ?? Brushes.LightBlue;
        var stroke = ResolveBrush("MmAccentBrush") ?? ResolveBrush("MmTextFaintBrush") ?? Brushes.Gray;
        using (context.PushOpacity(IsPointerOver || _isDragging ? 0.42 : 0.26))
        {
            context.DrawRectangle(fill, null, thumb, 6, 6);
        }

        using (context.PushOpacity(IsPointerOver || _isDragging ? 0.78 : 0.52))
        {
            context.DrawRectangle(null, new Pen(stroke, 1), thumb, 6, 6);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        e.Pointer.Capture(this);
        RequestScroll(e.GetPosition(this).Y);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging)
        {
            return;
        }

        RequestScroll(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
    }

    private void RequestScroll(double pointerY)
    {
        var requestedOffset = ApplicateDocumentMinimapScrollMapper.MapPointerYToScrollOffset(
            pointerY,
            Bounds.Height,
            DocumentHeight,
            ViewportHeight,
            ScrollMaximum);
        ScrollRequested?.Invoke(this, new ApplicateDocumentMinimapScrollRequestedEventArgs(requestedOffset));
    }

    private IBrush? ResolveBrush(string resourceKey)
        => this.TryFindResource(resourceKey, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : null;
}
