using MarkMello.Applicate.Desktop.Math;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateMarkdownDocumentRendererTests
{
    [Fact]
    public void RenderExtractsStandaloneDisplayMathBlocks()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("""
            Before

            $$
            T_{zt}e_{t} + T_{zz}e_{z} = 0
            $$

            After
            """);

        Assert.Contains(document.Blocks, block => block is ApplicateMathBlock math
            && math.Tex.Contains("T_{zt}", StringComparison.Ordinal));
        Assert.Contains(document.Blocks, block => block is MarkdownParagraphBlock paragraph
            && paragraph.Inlines.OfType<MarkdownTextInline>().Any(text => text.Text.Contains("Before", StringComparison.Ordinal)));
        Assert.Contains(document.Blocks, block => block is MarkdownParagraphBlock paragraph
            && paragraph.Inlines.OfType<MarkdownTextInline>().Any(text => text.Text.Contains("After", StringComparison.Ordinal)));
    }

    [Fact]
    public void RenderDoesNotExtractDisplayMathInsideCodeFences()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("""
            ```text
            $$
            raw
            $$
            ```
            """);

        Assert.DoesNotContain(document.Blocks, block => block is ApplicateMathBlock);
        Assert.Contains(document.Blocks, block => block is MarkdownCodeBlock code
            && code.Code.Contains("raw", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderTreatsStandaloneInlineMathLineAsDisplayMath()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("""
            Before

            $T_{zt}e_{t} + T_{zz}e_{z} = 0$

            After
            """);

        Assert.Contains(document.Blocks, block => block is ApplicateMathBlock math
            && math.Tex.Contains("T_{zt}", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderPreservesInlineMathInsideParagraphs()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("Before $\\beta^{2}$ after.");

        var paragraph = Assert.IsType<MarkdownParagraphBlock>(Assert.Single(document.Blocks));
        Assert.Contains(paragraph.Inlines, inline => inline is ApplicateMathInline math
            && math.Tex == "\\beta^{2}");
        Assert.DoesNotContain(paragraph.Inlines.OfType<MarkdownTextInline>(), text =>
            text.Text.Contains("APPLICATE_MATH", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeTexForRendererHandlesCommonAliases()
    {
        var normalized = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(@"\tfrac{1}{2} + \dfrac{a}{b} + x^{\prime} + y^\prime");

        Assert.Equal(@"\frac{1}{2} + \frac{a}{b} + x' + y'", normalized);
    }

    [Fact]
    public void NormalizeTexForRendererStripsUnsupportedBraceAnnotations()
    {
        var normalized = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(
            @"\underbrace{\frac{1}{\mu_{r}} x}_{\text{curl-curl}} - \overbrace{k_{0}^{2} y}^{mass}");

        Assert.Equal(@"\frac{1}{\mu_{r}} x - k_{0}^{2} y", normalized);
    }
}
