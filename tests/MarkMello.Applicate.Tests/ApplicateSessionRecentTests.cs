using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

public class ApplicateSessionRecentTests
{
    /// <summary>
    /// `Empty` must hand out a FRESH instance per call. This is not a style preference: `init`
    /// protects the list REFERENCE and never its CONTENTS, so a cached singleton would put a mutable
    /// process-global `List&lt;string&gt;` behind every "no saved session" route in the store. This
    /// drives the exact corruption a consumer causes by holding the saved list instead of building a
    /// fresh one, then proves it cannot escape into the next empty session.
    /// <para>
    /// Goes RED against `Empty { get; } = new()` -- and loudly, because on that shape the leak also
    /// reaches unrelated tests through the shared instance. If this fails alongside a scatter of
    /// session failures, THIS is the cause; fix it here rather than at the downstream symptoms.
    /// Reference identity is not a provenance signal (d13 clause 3 forbids consumers re-deriving
    /// "was it observed" from it), which is exactly what makes per-call construction safe.
    /// </para>
    /// </summary>
    [Fact]
    public void EmptyIsFreshPerCallSoItsListsCannotBecomeProcessGlobal()
    {
        var first = ApplicateSession.Empty;
        first.RecentPaths.Add(@"C:\a\leaked-recent.md");
        first.OpenPaths.Add(@"C:\a\leaked-open.md");

        var second = ApplicateSession.Empty;

        // Assert the HARM before the mechanism: on a cached singleton this fails with "Collection was
        // not empty", which names the actual corruption (the next empty session carrying another
        // document's paths). Leading with NotSame would fail first and read as a style complaint.
        Assert.Empty(second.RecentPaths);
        Assert.Empty(second.OpenPaths);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void BuildRecentPathsMovesOpenedToFront()
    {
        var existing = new List<string> { "a.md", "b.md", "c.md" };

        var result = ApplicateSession.BuildRecentPaths(existing, "b.md");

        var expected = new List<string> { "b.md", "a.md", "c.md" };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildRecentPathsDedupsCaseInsensitive()
    {
        var existing = new List<string> { "A.md", "b.md" };

        // "a.md" opened; the existing "A.md" is the same file case-insensitively and is dropped.
        var result = ApplicateSession.BuildRecentPaths(existing, "a.md");

        var expected = new List<string> { "a.md", "b.md" };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildRecentPathsCapsAtMax()
    {
        var existing = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            existing.Add($"f{i}.md");
        }

        var result = ApplicateSession.BuildRecentPaths(existing, "new.md");

        Assert.Equal(ApplicateSession.MaxRecentPaths, result.Count);
        Assert.Equal("new.md", result[0]);
    }

    [Fact]
    public void BuildRecentPathsIgnoresBlankOpenedPath()
    {
        var existing = new List<string> { "a.md" };

        var result = ApplicateSession.BuildRecentPaths(existing, "");

        var expected = new List<string> { "a.md" };
        Assert.Equal(expected, result);
    }
}
