using System.Text.Encodings.Web;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Trap-matrix tests for the lenient-link normalizer, run through the REAL
/// HTML pipeline (decorator → Markdig → HTML) so they verify end-to-end
/// behavior, not just the string transform. Design: .scratch/plans/design-links.md.
/// </summary>
public sealed class ApplicateLenientLinkNormalizerTests
{
    private static async Task<string> RenderAsync(string markdown)
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("sample.md", "sample.md", markdown);
        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);
        return document.Html;
    }

    [Fact]
    public async Task BareSpaceDestinationBecomesLink()
    {
        var html = await RenderAsync("[резюме](./Документы/Резюме для врачей.md)\n");

        Assert.Contains("<a ", html);
        Assert.DoesNotContain("](./Документы", html); // no literal markdown left
    }

    [Fact]
    public async Task AlreadyWrappedDestinationIsUntouched()
    {
        var html = await RenderAsync("[вопросы](<./Документы/Вопросы врачам (по специальностям).md>)\n");

        Assert.Contains("<a ", html);
    }

    [Fact]
    public async Task BareNestedParensWithSpacesBecomesLink()
    {
        var html = await RenderAsync("[вопросы](./Вопросы врачам (по специальностям).md)\n");

        Assert.Contains("<a ", html);
        Assert.DoesNotContain("](./Вопросы", html);
    }

    [Fact]
    public async Task ValidTitleFormStaysValid()
    {
        var html = await RenderAsync("[t](./f.md \"title\")\n");

        Assert.Contains("<a ", html);
        Assert.Contains("title", html);
    }

    [Fact]
    public async Task TitleContainingCloseParenIsNotDestroyed()
    {
        // fable round-1 blocker 1: a valid link whose quoted title contains ')'
        // must survive the normalizer untouched.
        var html = await RenderAsync("[t](./f.md \"ti)tle\")\n");

        Assert.Contains("<a ", html);
        Assert.DoesNotContain("&lt;./f.md", html); // no injected pointy dest
    }

    [Fact]
    public async Task SpaceDestWithTitleKeepsTitle()
    {
        var html = await RenderAsync("[t](./a b/f.md \"the title\")\n");

        Assert.Contains("<a ", html);
        Assert.Contains("the title", html);
        Assert.DoesNotContain("](./a b", html);
    }

    [Fact]
    public async Task FootnoteReferenceIsNotDestroyed()
    {
        // fable round-1 blocker 2: [^1](...) is a footnote ref + prose parens;
        // wrapping would turn it into a link and destroy the footnote.
        var html = await RenderAsync("text[^1](see note here)\n\n[^1]: the note\n");

        Assert.DoesNotContain("see note here</a>", html);
    }

    [Fact]
    public async Task ImageWithSpaceDestinationParsesAsImage()
    {
        // With a null image resolver the pipeline renders an image PLACEHOLDER
        // figure — the assertion is that the construct parsed as an IMAGE block
        // (identical to the <…>-wrapped form) instead of falling to literal text.
        var html = await RenderAsync("![alt](./a b/img.png)\n");

        Assert.Contains("data-mm-block-kind=\"image\"", html);
        Assert.DoesNotContain("](./a b", html);
    }

    [Fact]
    public async Task InlineCodeSpanIsUntouched()
    {
        var html = await RenderAsync("Use `[t](./a b/c.md)` literally.\n");

        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("&lt;./a b", html);
    }

    [Fact]
    public async Task FencedCodeBlockIsUntouched()
    {
        var html = await RenderAsync("```text\n[t](./a b/c.md)\n```\n");

        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("&lt;./a b", html);
    }

    [Fact]
    public async Task Percent20DestinationLeftAlone()
    {
        var html = await RenderAsync("[t](./a%20b/c.md)\n");

        Assert.Contains("<a ", html);
    }

    [Fact]
    public async Task MultipleLinksOnOneLineBothWrap()
    {
        var html = await RenderAsync("[a](./x y.md) and [b](./p q.md)\n");

        var first = html.IndexOf("<a ", StringComparison.Ordinal);
        var second = html.IndexOf("<a ", first + 3, StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "expected two links");
    }

    [Fact]
    public async Task LinkInsideTableCellWraps()
    {
        var html = await RenderAsync("| doc |\n| --- |\n| [t](./a b/c.md) |\n");

        Assert.Contains("<a ", html);
    }

    [Fact]
    public async Task NonAdjacentBracketParenLeftAlone()
    {
        var html = await RenderAsync("[t] (./a b.md)\n");

        Assert.DoesNotContain("<a ", html);
    }

    [Fact]
    public async Task DestWithRawAngleBracketAndSpaceBailsOut()
    {
        // Pointy destinations cannot carry raw '<'/'>' — bail, render as today.
        var html = await RenderAsync("[t](./a <b> c.md)\n");

        Assert.DoesNotContain("href", html);
    }

    [Fact]
    public async Task LineCountIsPreservedAcrossNormalization()
    {
        // Line-indexed consumers (TaskSourceLine, SourceSpan) depend on this: a
        // task item BELOW a wrapped link must keep its document-absolute line.
        var html = await RenderAsync("[t](./a b.md)\n\n- [ ] task after link\n");

        Assert.Contains("<a ", html);
        Assert.Contains(@"data-task-line=""2""", html);
    }
}
