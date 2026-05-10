using System.Text.Encodings.Web;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateHtmlMarkdownRendererTests
{
    [Fact]
    public async Task RenderPreservesInlineAndDisplayMathForKatex()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var displayDelimiter = new string('$', 2);
        var markdown = "Inline $a_{1}$\n\n"
            + displayDelimiter
            + "\nT_{zt}e_{t}=0\n"
            + displayDelimiter;
        var source = new MarkdownSource("sample.md", "sample.md", markdown);

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.Contains("data-tex=\"a_{1}\"", document.Html);
        Assert.Contains("data-tex=\"T_{zt}e_{t}=0\"", document.Html);
        Assert.Contains("math-inline", document.Html);
        Assert.Contains("math-display", document.Html);
    }

    [Fact]
    public async Task RenderKeepsKatexSupportedMathSyntaxUnnormalized()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var displayDelimiter = new string('$', 2);
        const string inlineTex = @"\underbrace{x}_{\text{kept}}";
        const string displayTex = @"\overbrace{\dfrac{1}{\mu_{r}}}^{kept} + y^{\prime} + z^\prime + \tfrac{a}{b}";
        var markdown = "Inline $\\underbrace{x}_{\\text{kept}}$.\n\n"
            + displayDelimiter
            + "\n"
            + displayTex
            + "\n"
            + displayDelimiter;
        var source = new MarkdownSource("sample.md", "sample.md", markdown);

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.Contains($@"data-tex=""{HtmlEncoder.Default.Encode(inlineTex)}""", document.Html);
        Assert.Contains($@"data-tex=""{HtmlEncoder.Default.Encode(displayTex)}""", document.Html);
        Assert.DoesNotContain(@"data-tex=""x""", document.Html);
        Assert.DoesNotContain(@"\frac{1}{\mu_{r}}", document.Html);
        Assert.DoesNotContain(@"\frac{a}{b}", document.Html);
        Assert.DoesNotContain("y'", document.Html);
        Assert.DoesNotContain("z'", document.Html);
    }

    [Fact]
    public async Task RenderUsesDifferentTexPolicyThanNativeRenderer()
    {
        var htmlRenderer = new ApplicateHtmlMarkdownRenderer();
        var nativeRenderer = new ApplicateMarkdownDocumentRenderer();
        var displayDelimiter = new string('$', 2);
        const string tex = @"\underbrace{\dfrac{1}{2}}_{kept} + x^\prime";
        var markdown = displayDelimiter + "\n" + tex + "\n" + displayDelimiter;
        var source = new MarkdownSource("sample.md", "sample.md", markdown);

        var nativeDocument = nativeRenderer.Render(markdown);
        var htmlDocument = await htmlRenderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var nativeMath = Assert.IsType<ApplicateMathBlock>(Assert.Single(nativeDocument.Blocks));
        Assert.Equal(@"\frac{1}{2} + x'", nativeMath.Tex);
        Assert.Contains($@"data-tex=""{HtmlEncoder.Default.Encode(tex)}""", htmlDocument.Html);
    }

    [Fact]
    public async Task RenderEscapesRawHtmlScript()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("evil.md", "evil.md", "<script>alert(1)</script>");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.DoesNotContain("<script>alert(1)</script>", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&lt;script&gt;alert(1)&lt;/script&gt;", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", document.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderCreatesHeadingMap()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("headings.md", "headings.md", "# Intro\n\n## Details");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.Collection(
            document.Headings,
            heading => Assert.Equal("Intro", heading.Text),
            heading => Assert.Equal("Details", heading.Text));
    }

    [Fact]
    public async Task RenderDoesNotEmitExternalImageUrls()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("docs/sample.md", "sample.md", "![remote](https://example.com/a.png)");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.DoesNotContain("https://example.com", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", document.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderDoesNotResolveRemoteImagesThroughResolver()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var resolver = new CountingImageSourceResolver();
        var source = new MarkdownSource("docs/sample.md", "sample.md", "![remote](https://example.com/a.png)");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            CancellationToken.None);

        Assert.Equal(0, resolver.CallCount);
        Assert.DoesNotContain("https://example.com", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:image/", document.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderEmbedsBundledKatexAndRendererAssetsWhenAssetEmbedderIsProvided()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer(new ApplicateWebAssetEmbedder());
        var source = new MarkdownSource("sample.md", "sample.md", "Inline $a_{1}$");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.Contains("function renderMath", document.Html, StringComparison.Ordinal);
        Assert.Contains("@font-face", document.Html, StringComparison.Ordinal);
        Assert.Contains("KaTeX_Main", document.Html, StringComparison.Ordinal);
        Assert.Contains("data:font/", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fonts/KaTeX", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file://", document.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BundledRendererCssDoesNotClipDisplayMathVertically()
    {
        var css = await new ApplicateWebAssetEmbedder()
            .ReadTextAssetAsync("renderer.css", CancellationToken.None);

        Assert.Contains("overflow-y: visible;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-y: hidden;", css, StringComparison.Ordinal);
    }

    private sealed class CountingImageSourceResolver : IImageSourceResolver
    {
        public int CallCount { get; private set; }

        public Task<Stream?> TryOpenAsync(
            string url,
            string? baseDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var bytes = "not a real image"u8.ToArray();
            return Task.FromResult<Stream?>(new MemoryStream(bytes));
        }
    }
}
