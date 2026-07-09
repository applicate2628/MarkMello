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

    [Fact]
    public async Task SearchUsesRendererTableBodyPlainText()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var markdown = "| Header |\n"
            + "| --- |\n"
            + "| unique-row-token |\n";
        var body = await renderer.RenderBodyAsync(
            new MarkdownSource("table.md", "table.md", markdown),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var result = ApplicateBlockTextIndex.Create(body.Blocks).Search("unique-row-token");

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("table", body.Blocks[match.BlockIndex].Kind);
    }

    [Fact]
    public async Task RendererBlockPlainTextCoversRenderedTextForEveryBlockKind()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var markdown = "# heading-token\n\n"
            + "paragraph-token\n\n"
            + "> quote-token\n\n"
            + "- list-token\n\n"
            + "```text\ncode-token\n```\n\n"
            + "| header-token | Header B |\n"
            + "| --- | --- |\n"
            + "| body-token | body-two |\n\n"
            + "![image-alt](missing.png)\n\n"
            + "$$\nmath-token\n$$\n\n"
            + "---\n";
        var body = await renderer.RenderBodyAsync(
            new MarkdownSource("coverage.md", "coverage.md", markdown),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        AssertBlockPlainTextContains(body, "heading", "heading-token");
        AssertBlockPlainTextContains(body, "paragraph", "paragraph-token");
        AssertBlockPlainTextContains(body, "quote", "quote-token");
        AssertBlockPlainTextContains(body, "list", "list-token");
        AssertBlockPlainTextContains(body, "code", "code-token");
        AssertBlockPlainTextContains(body, "table", "header-token", "body-token", "body-two");
        AssertBlockPlainTextContains(body, "image", "image-alt");
        AssertBlockPlainTextContains(body, "math", "math-token");
        Assert.Contains(body.Blocks, block => block.Kind == "rule" && block.PlainText.Length == 0);
    }

    [Fact]
    public async Task SearchCountsNestedQuoteAndListTextOnceUsingChildMarkers()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var markdown = "> gamma in quote\n\n"
            + "- gamma in list\n";
        var body = await renderer.RenderBodyAsync(
            new MarkdownSource("nested.md", "nested.md", markdown),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var result = ApplicateBlockTextIndex.Create(body.Blocks).Search("gamma");

        Assert.Equal(2, result.TotalCount);
        Assert.DoesNotContain(result.Matches, match =>
            body.Blocks[match.BlockIndex].Kind is "quote" or "list");
    }

    [Fact]
    public async Task SearchPreservesTopLevelBlockCounts()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var markdown = "gamma in paragraph\n\n"
            + "```text\n"
            + "gamma in code\n"
            + "```\n";
        var body = await renderer.RenderBodyAsync(
            new MarkdownSource("top-level.md", "top-level.md", markdown),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var result = ApplicateBlockTextIndex.Create(body.Blocks).Search("gamma");

        Assert.Equal(2, result.TotalCount);
    }

    private static void AssertBlockPlainTextContains(
        ApplicateRenderedBody body,
        string kind,
        params string[] expectedTokens)
    {
        Assert.Contains(body.Blocks, block =>
            block.Kind == kind
            && expectedTokens.All(token => block.PlainText.Contains(token, StringComparison.Ordinal)));
    }
}
