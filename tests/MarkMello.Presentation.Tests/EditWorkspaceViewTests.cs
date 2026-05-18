namespace MarkMello.Presentation.Tests;

public sealed class EditWorkspaceViewTests
{
    [Fact]
    public void ScrollSynchronizationPausesWhileScrollbarThumbIsDragged()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "EditWorkspaceView.axaml.cs"));

        Assert.Contains("_activeScrollBarDragSource", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnScrollBarDragPointerPressed", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PointerCaptureLostEvent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (_activeScrollBarDragSource is not null)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("AttachScrollBarDragHandlers", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RestartScrollBarDragSettleTimer", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SynchronizeFromScrollBarDragSource", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeScrollBarDragSource = null;\r\n            SynchronizeFromScrollBarDragSource(source);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryGetOwnedScrollViewerFromScrollBarChrome", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CompleteScrollBarDrag", codeBehind, StringComparison.Ordinal);
    }
}
