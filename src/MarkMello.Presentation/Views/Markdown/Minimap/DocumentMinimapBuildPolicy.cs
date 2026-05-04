using Avalonia;

namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal static class DocumentMinimapBuildPolicy
{
    public const double MinHostWidth = 1100.0;
    public const double MinScrollableViewportRatio = 1.5;
    public const double LayoutSizeEpsilon = 0.5;
    public const double MaxDetailedDocumentHeight = 240_000.0;

    public static bool ShouldShow(
        double hostWidth,
        Size scrollExtent,
        Size scrollViewport,
        double scrollMaximumY)
    {
        var viewportHeight = scrollViewport.Height;
        var documentHeight = scrollExtent.Height;
        return hostWidth >= MinHostWidth
            && viewportHeight > 0
            && scrollMaximumY > 0
            && documentHeight >= viewportHeight * MinScrollableViewportRatio;
    }

    public static bool HasLayoutMetricsChanged(
        Size previousExtent,
        Size previousViewport,
        Size currentExtent,
        Size currentViewport)
        => HasSizeChanged(previousExtent, currentExtent)
            || HasSizeChanged(previousViewport, currentViewport);

    public static bool AllowsDetailedMiniature(DocumentMiniatureSnapshot snapshot)
        => !snapshot.IsEmpty && snapshot.TotalHeight <= MaxDetailedDocumentHeight;

    private static bool HasSizeChanged(Size previous, Size current)
        => Math.Abs(previous.Width - current.Width) > LayoutSizeEpsilon
            || Math.Abs(previous.Height - current.Height) > LayoutSizeEpsilon;
}
