using MarkMello.Presentation.Views.Markdown.Minimap;

namespace MarkMello.Presentation.Tests;

public sealed class DocumentMinimapScrollMapperTests
{
    [Fact]
    public void MapPointerYToScrollOffsetCentersRequestedDocumentPosition()
    {
        var offset = DocumentMinimapScrollMapper.MapPointerYToScrollOffset(
            pointerY: 250,
            minimapHeight: 500,
            documentHeight: 2_000,
            viewportHeight: 400,
            maxScrollOffset: 1_600);

        Assert.Equal(800, offset);
    }

    [Fact]
    public void MapPointerYToScrollOffsetClampsToScrollRange()
    {
        var beforeTop = DocumentMinimapScrollMapper.MapPointerYToScrollOffset(
            pointerY: -100,
            minimapHeight: 500,
            documentHeight: 2_000,
            viewportHeight: 400,
            maxScrollOffset: 1_600);
        var afterBottom = DocumentMinimapScrollMapper.MapPointerYToScrollOffset(
            pointerY: 700,
            minimapHeight: 500,
            documentHeight: 2_000,
            viewportHeight: 400,
            maxScrollOffset: 1_600);

        Assert.Equal(0, beforeTop);
        Assert.Equal(1_600, afterBottom);
    }

    [Fact]
    public void CalculateViewportThumbScalesCurrentViewport()
    {
        var thumb = DocumentMinimapScrollMapper.CalculateViewportThumb(
            minimapWidth: 80,
            minimapHeight: 500,
            documentHeight: 2_000,
            viewportHeight: 400,
            scrollOffset: 800,
            minThumbHeight: 24);

        Assert.Equal(80, thumb.Width);
        Assert.Equal(200, thumb.Y);
        Assert.Equal(100, thumb.Height);
    }
}
