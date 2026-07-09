using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateBlockTextIndexTests
{
    [Fact]
    public void SearchCountsMatchesAcrossAllBlocksInDocumentOrder()
    {
        var blocks = Enumerable.Range(0, 100)
            .Select(index => new ApplicateHtmlBlockMarker(
                index,
                "paragraph",
                index is 4 or 88 ? $"Block {index} has Needle text." : $"Block {index}."))
            .ToArray();

        var result = ApplicateBlockTextIndex.Create(blocks).Search("needle");

        Assert.Equal(2, result.TotalCount);
        Assert.Collection(
            result.Matches,
            match =>
            {
                Assert.Equal(1, match.Ordinal);
                Assert.Equal(4, match.BlockIndex);
                Assert.Equal(12, match.BlockLocalOffset);
                Assert.Equal(6, match.Length);
                Assert.Equal("needle", match.NormalizedText);
                Assert.Equal("b4-o12-l6-n1", match.MatchId);
                Assert.Null(match.StartBlockIndex);
                Assert.Null(match.EndBlockIndex);
            },
            match =>
            {
                Assert.Equal(2, match.Ordinal);
                Assert.Equal(88, match.BlockIndex);
                Assert.Equal(13, match.BlockLocalOffset);
                Assert.Equal(6, match.Length);
                Assert.Equal("needle", match.NormalizedText);
                Assert.Equal("b88-o13-l6-n2", match.MatchId);
            });
    }

    [Fact]
    public void SearchMirrorsRendererLengthExpansionCasefolding()
    {
        var index = ApplicateBlockTextIndex.Create(
        [
            new ApplicateHtmlBlockMarker(0, "paragraph", "Hello WORLD hello"),
            new ApplicateHtmlBlockMarker(1, "paragraph", "aİb"),
        ]);

        var ascii = index.Search("hello");
        Assert.Collection(
            ascii.Matches,
            match =>
            {
                Assert.Equal(0, match.BlockLocalOffset);
                Assert.Equal(5, match.Length);
            },
            match =>
            {
                Assert.Equal(12, match.BlockLocalOffset);
                Assert.Equal(5, match.Length);
            });

        var expanded = index.Search("b");
        Assert.DoesNotContain(expanded.Matches, match => match.BlockIndex == 1);
    }

    [Fact]
    public async Task SearchUsesRendererBlockPlainTextForMathAndCode()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var markdown = "Inline $a_{1}$ in prose.\n\n"
            + "$$\nT_{zt}e_{t}=0\n$$\n\n"
            + "```csharp\nconst string needle = \"code\";\n```\n";
        var body = await renderer.RenderBodyAsync(
            new MarkdownSource("search.md", "search.md", markdown),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var index = ApplicateBlockTextIndex.Create(body.Blocks);

        Assert.Single(index.Search("a_{1}").Matches);
        Assert.Single(index.Search("T_{zt}").Matches);
        var code = Assert.Single(index.Search("needle").Matches);
        Assert.Equal("code", body.Blocks[code.BlockIndex].Kind);
    }
}
