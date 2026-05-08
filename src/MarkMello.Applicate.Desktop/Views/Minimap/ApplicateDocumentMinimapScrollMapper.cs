using Avalonia;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal static class ApplicateDocumentMinimapScrollMapper
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

        var normalizedY = SysMath.Clamp(pointerY / minimapHeight, 0, 1);
        var requestedDocumentCenter = normalizedY * documentHeight;
        return SysMath.Clamp(requestedDocumentCenter - viewportHeight / 2, 0, maxScrollOffset);
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

        var normalizedHeight = SysMath.Clamp(viewportHeight / documentHeight, 0, 1);
        var minimumHeight = SysMath.Min(SysMath.Max(0, minThumbHeight), minimapHeight);
        var thumbHeight = SysMath.Clamp(minimapHeight * normalizedHeight, minimumHeight, minimapHeight);
        var maxThumbTop = SysMath.Max(0, minimapHeight - thumbHeight);
        var normalizedScroll = maxScrollOffset <= 0
            ? 0
            : SysMath.Clamp(scrollOffset / maxScrollOffset, 0, 1);

        return new Rect(0, maxThumbTop * normalizedScroll, minimapWidth, thumbHeight);
    }
}
