using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

public class ApplicateSessionRecentTests
{
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
