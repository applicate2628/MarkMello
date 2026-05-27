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
    public void RenderDoesNotTreatLineWithTwoInlineMathSpansAsDisplayMath()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("$\\mathbf r_{q}\\in\\Gamma_{p}$ нужно получить local coordinates $(u_{q},v_{q})$");

        var paragraph = Assert.IsType<MarkdownParagraphBlock>(Assert.Single(document.Blocks));
        var math = paragraph.Inlines.OfType<ApplicateMathInline>().ToArray();
        Assert.Equal(2, math.Length);
        Assert.Equal("\\mathbf r_{q}\\in\\Gamma_{p}", math[0].Tex);
        Assert.Equal("(u_{q},v_{q})", math[1].Tex);
        Assert.Contains(paragraph.Inlines.OfType<MarkdownTextInline>(), text =>
            text.Text.Contains("нужно получить local coordinates", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderPreservesMathLikeTextInsideInlineCode()
    {
        var renderer = new ApplicateMarkdownDocumentRenderer();

        var document = renderer.Render("Keep `$x$` as code, but render $y$ as math.");

        var paragraph = Assert.IsType<MarkdownParagraphBlock>(Assert.Single(document.Blocks));
        Assert.Contains(paragraph.Inlines, inline => inline is MarkdownCodeInline code
            && code.Code == "$x$");
        Assert.Contains(paragraph.Inlines, inline => inline is ApplicateMathInline math
            && math.Tex == "y");
        Assert.DoesNotContain(paragraph.Inlines.OfType<MarkdownCodeInline>(), code =>
            code.Code.Contains("APPLICATE_MATH", StringComparison.Ordinal));
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

    [Fact]
    public void NormalizeTexForRendererStripsUnsupportedBoldsymbolFormatting()
    {
        var normalized = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(
            @"E_{h} = \sum_{j=1}^{N} x_{j}\boldsymbol\phi_{j} + \boldsymbol{\nabla \times \phi_{j}}");

        Assert.Equal(
            @"E_{h} = \sum_{j=1}^{N} x_{j}\phi_{j} + \nabla \times \phi_{j}",
            normalized);
    }

    [Fact]
    public void NormalizeTexForRendererStripsUnsupportedBoxedFormatting()
    {
        var normalized = ApplicateMarkdownDocumentRenderer.NormalizeTexForRenderer(
            @"\boxed{\text{в LHS мода задаёт admittance}} + \boxed{T_{zt}e_{t}}");

        Assert.Equal(@"\text{в LHS мода задаёт admittance} + T_{zt}e_{t}", normalized);
    }

    [Fact]
    public void MathLineBreakerWrapsAtTopLevelOperators()
    {
        const string tex = @"\sqrt{1.184375 - p^{2}} / 2.45 \cdot \tan(x) - \sqrt{p^{2} + 0.265625} \cdot \tanh(y) = 0";

        var chunks = ApplicateMathLineBreaker.SplitIntoChunks(tex);
        var rows = ApplicateMathLineBreaker.WrapIntoRows(
            tex,
            maxWidth: 42,
            measureWidth: text => text.Length);

        Assert.True(rows.Count > 1);
        Assert.Contains(chunks, chunk => chunk == @"\cdot");
        Assert.Contains(chunks, chunk => chunk.StartsWith(@"\tan", StringComparison.Ordinal));
        Assert.Contains(chunks, chunk => chunk.StartsWith(@"\tanh", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains(@"\sqrt{1.184375", StringComparison.Ordinal)
            && !row.Contains('}'));
    }

    [Fact]
    public void MathLineBreakerDoesNotSplitInsideLeftRightDelimiters()
    {
        const string tex = @"\frac{1}{\mu_{r}} \iint \mathbf{T}\cdot \mathbf{E}_{t} = -\frac{\beta^{2}}{\mu_{r}}\left[\iint \mathbf{T}\cdot \nabla E_{z} + \iint \mathbf{T}\cdot \mathbf{E}_{t}\right]";

        var chunks = ApplicateMathLineBreaker.SplitIntoChunks(tex);

        Assert.DoesNotContain(chunks, chunk => chunk.Contains(@"\left[", StringComparison.Ordinal)
            && !chunk.Contains(@"\right]", StringComparison.Ordinal));
        Assert.DoesNotContain(chunks, chunk => chunk.Contains(@"\right]", StringComparison.Ordinal)
            && !chunk.Contains(@"\left[", StringComparison.Ordinal));
    }
}
