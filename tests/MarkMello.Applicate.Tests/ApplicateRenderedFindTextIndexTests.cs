using System.IO;
using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRenderedFindTextIndexTests
{
    [Fact]
    public void SearchUsesOnlySemanticSegmentsAndNeverCrossesSegmentBoundaries()
    {
        var index = ApplicateRenderedFindTextIndex.Create(
            renderId: 11,
            projectionRevision: 3,
            [
                new ApplicateRenderedFindTextSegment(0, 4, 0, "pre"),
                new ApplicateRenderedFindTextSegment(1, 4, 3, "fix"),
                new ApplicateRenderedFindTextSegment(2, 8, 0, "suffix"),
            ]);

        Assert.Empty(index.Search("ef").Matches);

        var result = index.Search("fix");

        Assert.Equal(2, result.TotalCount);
        Assert.Collection(
            result.Matches,
            match =>
            {
                Assert.Equal("r11-p3-b4-s1-o3-l3-n1", match.MatchId);
                Assert.Equal(11, match.RenderId);
                Assert.Equal(3, match.ProjectionRevision);
                Assert.Equal(4, match.BlockIndex);
                Assert.Equal(1, match.SegmentOrdinal);
                Assert.Equal(3, match.BlockLocalOffset);
                Assert.Equal(3, match.Length);
                Assert.Equal("fix", match.NormalizedText);
                Assert.Equal(1, match.Ordinal);
            },
            match =>
            {
                Assert.Equal("r11-p3-b8-s2-o3-l3-n2", match.MatchId);
                Assert.Equal(8, match.BlockIndex);
                Assert.Equal(2, match.SegmentOrdinal);
                Assert.Equal(3, match.BlockLocalOffset);
                Assert.Equal(2, match.Ordinal);
            });
    }

    [Fact]
    public void SearchReportsBlockLocalOffsetsAndSkipsLengthChangingNormalization()
    {
        var index = ApplicateRenderedFindTextIndex.Create(
            renderId: 22,
            projectionRevision: 4,
            [
                new ApplicateRenderedFindTextSegment(0, 6, 10, "Hello WORLD hello"),
                new ApplicateRenderedFindTextSegment(1, 7, 0, "a\u0130b"),
            ]);

        var ascii = index.Search("hello");
        Assert.Collection(
            ascii.Matches,
            match =>
            {
                Assert.Equal(10, match.BlockLocalOffset);
                Assert.Equal("hello", match.NormalizedText);
            },
            match =>
            {
                Assert.Equal(22, match.BlockLocalOffset);
                Assert.Equal("hello", match.NormalizedText);
            });

        var expanded = index.Search("b");

        Assert.DoesNotContain(expanded.Matches, match => match.BlockIndex == 7);
    }

    [Fact]
    public void SearchUsesAlreadyReconstructedMultipartSegmentAsOneSearchBoundary()
    {
        var index = ApplicateRenderedFindTextIndex.Create(
            renderId: 31,
            projectionRevision: 2,
            [
                new ApplicateRenderedFindTextSegment(0, 5, 0, "alphabeta"),
                new ApplicateRenderedFindTextSegment(1, 6, 0, "alpha"),
                new ApplicateRenderedFindTextSegment(2, 6, 5, "beta"),
            ]);

        var result = index.Search("abet");

        var match = Assert.Single(result.Matches);
        Assert.Equal(4, match.BlockLocalOffset);
        Assert.Equal("r31-p2-b5-s0-o4-l4-n1", match.MatchId);
    }

    [Fact]
    public void RenderedIndexDoesNotReferencePlaintextIndexOwner()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "ApplicateRenderedFindTextIndex.cs");

        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("ApplicateBlockTextIndex", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find MarkMello.sln from the test working directory.");
    }
}
