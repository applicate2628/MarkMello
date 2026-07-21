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
    public void SingleCharDeltaNarrowsToTheExactChangedChar()
    {
        Assert.True(TryGetMinimalDelta("- [ ] task", "- [x] task", out var offset, out var removed, out var inserted));
        Assert.Equal(3, offset);
        Assert.Equal(1, removed);
        Assert.Equal("x", inserted);
    }

    [Fact]
    public void IdenticalTextsAreNotADelta()
    {
        Assert.False(TryGetMinimalDelta("same", "same", out _, out _, out _));
    }

    [Fact]
    public void FirstAndLastCharDeltasNarrowToOneChar()
    {
        Assert.True(TryGetMinimalDelta("Xbc", "abc", out var first, out var firstRemoved, out var firstInserted));
        Assert.Equal(0, first);
        Assert.Equal(1, firstRemoved);
        Assert.Equal("a", firstInserted);

        Assert.True(TryGetMinimalDelta("abX", "abc", out var last, out var lastRemoved, out var lastInserted));
        Assert.Equal(2, last);
        Assert.Equal(1, lastRemoved);
        Assert.Equal("c", lastInserted);
    }

    [Theory]
    // Pure append and pure prepend: nothing existing is removed.
    [InlineData("abc", "abcd", 3, 0, "d")]
    [InlineData("abc", "zabc", 0, 0, "z")]
    // Pure deletion: nothing is inserted.
    [InlineData("abcd", "abc", 3, 1, "")]
    [InlineData("zabc", "abc", 0, 1, "")]
    // Two changed chars far apart collapse to one span covering both — the
    // narrowest SINGLE replacement, which is what Document.Replace takes.
    [InlineData("- [ ] a\n- [ ] b", "- [x] a\n- [x] b", 3, 9, "x] a\n- [x")]
    // The health-repair shape: a multi-char suffix appended to typed text.
    [InlineData("alpha typed by user", "alpha typed by user REPAIRED", 19, 0, " REPAIRED")]
    // Degenerate ends.
    [InlineData("", "abc", 0, 0, "abc")]
    [InlineData("abc", "", 0, 3, "")]
    public void MinimalDeltaNarrowsToTheDifferingMiddle(
        string oldText,
        string newText,
        int expectedOffset,
        int expectedRemoved,
        string expectedInserted)
    {
        Assert.True(TryGetMinimalDelta(oldText, newText, out var offset, out var removed, out var inserted));
        Assert.Equal(expectedOffset, offset);
        Assert.Equal(expectedRemoved, removed);
        Assert.Equal(expectedInserted, inserted);
    }

    [Theory]
    [InlineData("- [ ] task", "- [x] task")]
    [InlineData("alpha typed by user", "alpha typed by user REPAIRED")]
    [InlineData("| a | b |", "| a | LONGER CELL |")]
    [InlineData("", "abc")]
    [InlineData("abc", "")]
    [InlineData("abc", "xyz")]
    // Surrogate pairs: the boundary must not land inside one, and the
    // reconstruction must still be exact.
    [InlineData("a\U0001F600b", "a\U0001F601b")]
    [InlineData("\U0001F600", "\U0001F600\U0001F600")]
    public void ApplyingTheDeltaReconstructsTheNewTextExactly(string oldText, string newText)
    {
        Assert.True(TryGetMinimalDelta(oldText, newText, out var offset, out var removed, out var inserted));

        // Exactly what TextDocument.Replace(offset, removed, inserted) does.
        var rebuilt = oldText[..offset] + inserted + oldText[(offset + removed)..];
        Assert.Equal(newText, rebuilt);

        // A replacement must never start or end inside a surrogate pair.
        Assert.False(offset > 0 && char.IsHighSurrogate(oldText[offset - 1]) && char.IsLowSurrogate(oldText[offset]));
        var end = offset + removed;
        Assert.False(end < oldText.Length && char.IsLowSurrogate(oldText[end]) && char.IsHighSurrogate(oldText[end - 1]));
    }

    private static bool TryGetMinimalDelta(
        string oldText,
        string newText,
        out int offset,
        out int removedLength,
        out string inserted)
        => MarkMello.Presentation.Views.EditWorkspaceView.TryGetMinimalDelta(
            oldText,
            newText,
            out offset,
            out removedLength,
            out inserted);
}
