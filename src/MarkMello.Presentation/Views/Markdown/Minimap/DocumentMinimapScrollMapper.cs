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
        double minThumbHeight)
    {
        if (minimapWidth <= 0 || minimapHeight <= 0 || documentHeight <= 0 || viewportHeight <= 0)
        {
            return default;
        }

        var normalizedTop = Math.Clamp(scrollOffset / documentHeight, 0, 1);
        var normalizedHeight = Math.Clamp(viewportHeight / documentHeight, 0, 1);
        var thumbHeight = Math.Clamp(minimapHeight * normalizedHeight, Math.Min(minThumbHeight, minimapHeight), minimapHeight);
        var thumbTop = Math.Clamp(minimapHeight * normalizedTop, 0, Math.Max(0, minimapHeight - thumbHeight));

        return new Rect(0, thumbTop, minimapWidth, thumbHeight);
    }
}
