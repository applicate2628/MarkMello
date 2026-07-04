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

    [Fact]
    public void SingleCharDeltaDetectedAtExactOffset()
    {
        Assert.True(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta(
            "- [ ] task", "- [x] task", out var offset));
        Assert.Equal(3, offset);
    }

    [Fact]
    public void IdenticalTextsAreNotADelta()
    {
        Assert.False(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta(
            "same", "same", out _));
    }

    [Fact]
    public void LengthChangeIsNotASingleCharDelta()
    {
        Assert.False(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta(
            "abc", "abcd", out _));
    }

    [Fact]
    public void TwoChangedCharsAreNotASingleCharDelta()
    {
        Assert.False(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta(
            "- [ ] a\n- [ ] b", "- [x] a\n- [x] b", out _));
    }

    [Fact]
    public void FirstAndLastCharDeltasDetected()
    {
        Assert.True(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta("Xbc", "abc", out var first));
        Assert.Equal(0, first);
        Assert.True(MarkMello.Presentation.Views.EditWorkspaceView.TryGetSingleCharDelta("abX", "abc", out var last));
        Assert.Equal(2, last);
    }
}
