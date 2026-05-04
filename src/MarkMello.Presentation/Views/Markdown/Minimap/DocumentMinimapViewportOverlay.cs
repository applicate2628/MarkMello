using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal sealed class DocumentMinimapViewportOverlay : Control
{
    private const double DefaultWidth = 136;
    private const double ViewportThumbMinHeight = 28;
    private const double TrackHorizontalPadding = 4;
    private bool _isDragging;

    public DocumentMinimapViewportOverlay()
    {
        Focusable = false;
        IsTabStop = false;
        UseLayoutRounding = true;
        Cursor = TryCreateCursor(StandardCursorType.Hand);
    }

    public double DocumentHeight { get; set; }

    public double ScrollOffset { get; set; }

    public double ScrollMaximum { get; set; }

    public double ViewportHeight { get; set; }

    public event EventHandler<DocumentMinimapScrollRequestedEventArgs>? ScrollRequested;

    public void UpdateScrollState(double scrollOffset, double scrollMaximum, double viewportHeight)
    {
        ScrollOffset = scrollOffset;
        ScrollMaximum = scrollMaximum;
        ViewportHeight = viewportHeight;
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

        if (Bounds.Width <= 0 || Bounds.Height <= 0 || DocumentHeight <= 0)
        {
            return;
        }

        DrawHoverBackground(context);
        DrawViewportThumb(context);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var currentPoint = e.GetCurrentPoint(this);
        if (!currentPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        InvalidateVisual();
        e.Pointer.Capture(this);
        RequestScroll(e.GetPosition(this).Y);
        e.Handled = true;
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
        InvalidateVisual();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        InvalidateVisual();
    }

    private void DrawHoverBackground(DrawingContext context)
    {
        if (!IsPointerOver)
        {
            return;
        }

        var background = ResolveBrush("MmSurfaceBrush") ?? Brushes.Transparent;
        using (context.PushOpacity(0.22))
        {
            context.DrawRectangle(background, null, new Rect(0, 0, Bounds.Width, Bounds.Height), 5, 5);
        }
    }

    private void DrawViewportThumb(DrawingContext context)
    {
        var documentHeight = Math.Max(DocumentHeight, ScrollMaximum + ViewportHeight);
        var trackWidth = Math.Max(0, Bounds.Width - TrackHorizontalPadding * 2);
        var thumb = DocumentMinimapScrollMapper.CalculateViewportThumb(
            trackWidth,
            Bounds.Height,
            documentHeight,
            ViewportHeight,
            ScrollOffset,
            ScrollMaximum,
            ViewportThumbMinHeight).Translate(new Vector(TrackHorizontalPadding, 0));

        if (thumb.Width <= 0 || thumb.Height <= 0)
        {
            return;
        }

        var fill = ResolveBrush("MmSelectionBrush") ?? ResolveBrush("MmAccentSoftBrush") ?? Brushes.LightBlue;
        var stroke = ResolveBrush("MmAccentBrush") ?? ResolveBrush("MmTextFaintBrush") ?? Brushes.Gray;

        using (context.PushOpacity(IsPointerOver || _isDragging ? 0.42 : 0.26))
        {
            context.DrawRectangle(fill, null, thumb, 5, 5);
        }

        using (context.PushOpacity(IsPointerOver || _isDragging ? 0.78 : 0.52))
        {
            context.DrawRectangle(null, new Pen(stroke, 1), thumb, 5, 5);
        }
    }

    private void RequestScroll(double localY)
    {
        var documentHeight = Math.Max(DocumentHeight, ScrollMaximum + ViewportHeight);
        if (documentHeight <= 0)
        {
            return;
        }

        var requestedOffset = DocumentMinimapScrollMapper.MapPointerYToScrollOffset(
            localY,
            Bounds.Height,
            documentHeight,
            ViewportHeight,
            ScrollMaximum);

        ScrollRequested?.Invoke(this, new DocumentMinimapScrollRequestedEventArgs(requestedOffset));
    }

    private IBrush? ResolveBrush(string resourceKey)
        => this.TryFindResource(resourceKey, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : null;

    private static Cursor? TryCreateCursor(StandardCursorType cursorType)
    {
        try
        {
            return new Cursor(cursorType);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
