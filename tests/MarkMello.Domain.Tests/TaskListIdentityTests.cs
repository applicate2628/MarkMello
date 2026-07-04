using MarkMello.Domain;

namespace MarkMello.Domain.Tests;

public sealed class TaskListIdentityTests
{
    [Fact]
    public void KeyIsStableAcrossStateFlip()
    {
        // The state char is excluded from the label, so toggling keeps the key —
        // a self-toggle must not invalidate the identity.
        var unchecked_ = TaskListIdentity.ComputeKey("- [ ] task label");
        var checked_ = TaskListIdentity.ComputeKey("- [x] task label");

        Assert.NotNull(unchecked_);
        Assert.Equal(unchecked_, checked_);
    }

    [Fact]
    public void KeyDiffersForDifferentLabels()
    {
        Assert.NotEqual(
            TaskListIdentity.ComputeKey("- [ ] alpha"),
            TaskListIdentity.ComputeKey("- [ ] beta"));
    }

    [Fact]
    public void NonMarkerLineYieldsNullKey()
    {
        Assert.Null(TaskListIdentity.ComputeKey("plain prose line"));
        Assert.Null(TaskListIdentity.ComputeKey("> [ ] no bullet"));
        Assert.Null(TaskListIdentity.ComputeKey(null));
    }

    [Fact]
    public void BlockquotedAndNestedMarkersMatch()
    {
        Assert.NotNull(TaskListIdentity.ComputeKey("> - [ ] quoted"));
        Assert.NotNull(TaskListIdentity.ComputeKey("> > * [x] nested quote"));
        Assert.NotNull(TaskListIdentity.ComputeKey("  > 1. [ ] ordered in quote"));
    }

    [Fact]
    public void CrlfLineYieldsSameKeyAsLfLine()
    {
        // Split('\n') leaves a trailing '\r' on CRLF lines; Trim removes it, so
        // both sides hash identically regardless of the file's EOL style.
        Assert.Equal(
            TaskListIdentity.ComputeKey("- [ ] task label"),
            TaskListIdentity.ComputeKey("- [ ] task label\r"));
    }

    [Fact]
    public void MarkerPatternPreservesQuotePrefixInGroupOne()
    {
        var match = TaskListIdentity.TaskMarkerPattern.Match("> - [ ] quoted task");

        Assert.True(match.Success);
        Assert.Equal("> - [", match.Groups[1].Value);
        Assert.Equal(" ", match.Groups[2].Value);
        Assert.Equal("] quoted task", match.Groups[3].Value);
    }
}
