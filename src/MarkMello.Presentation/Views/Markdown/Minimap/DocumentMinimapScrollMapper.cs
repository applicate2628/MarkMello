using Avalonia;

namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal static class DocumentMinimapScrollMapper
{
    public static double MapPointerYToScrollOffset(
        double pointerY,
        double minimapHeight,
        double documentHeight,
        double viewportHeight,
        double maxScrollOffset)
    {
        if (minimapHeight <= 0 || documentHeight <= 0 || maxScrollOffset <= 0)
        {
            return 0;
        }

        var normalizedY = Math.Clamp(pointerY / minimapHeight, 0, 1);
        var requestedDocumentCenter = normalizedY * documentHeight;
        return Math.Clamp(requestedDocumentCenter - viewportHeight / 2, 0, maxScrollOffset);
    }

    public static Rect CalculateViewportThumb(
        double minimapWidth,
        double minimapHeight,
        double documentHeight,
        double viewportHeight,
        double scrollOffset,
        double maxScrollOffset,
        double minThumbHeight)
    {
        if (minimapWidth <= 0 || minimapHeight <= 0 || documentHeight <= 0 || viewportHeight <= 0)
        {
            return default;
        }

        var normalizedHeight = Math.Clamp(viewportHeight / documentHeight, 0, 1);
        var minimumHeight = Math.Min(Math.Max(0, minThumbHeight), minimapHeight);
        var thumbHeight = Math.Clamp(minimapHeight * normalizedHeight, minimumHeight, minimapHeight);
        var maxThumbTop = Math.Max(0, minimapHeight - thumbHeight);
        var normalizedScroll = maxScrollOffset <= 0
            ? 0
            : Math.Clamp(scrollOffset / maxScrollOffset, 0, 1);

        return new Rect(0, maxThumbTop * normalizedScroll, minimapWidth, thumbHeight);
    }
}
