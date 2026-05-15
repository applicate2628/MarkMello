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
    public async Task RenderEmitsBlockIndexAndKindForSyncMetadata()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        // One heading, one paragraph, one list, one code block, one quote, one rule, one math display.
        const string sourceText = "# Title\n\nFirst paragraph.\n\n- alpha\n- beta\n\n```python\nprint(1)\n```\n\n> quoted\n\n---\n\n$$x^2$$\n";
        var source = new MarkdownSource("blocks.md", "blocks.md", sourceText);

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        // Each block in the rendered Blocks list should appear in the HTML
        // with matching data-mm-block-index plus its kind. The metadata is
        // what the editor↔preview scroll sync uses to map editor lines to
        // visible preview elements.
        Assert.Contains("data-mm-block-index=\"0\" data-mm-block-kind=\"heading\"", document.Html);
        Assert.Contains("data-mm-block-index=\"1\" data-mm-block-kind=\"paragraph\"", document.Html);
        Assert.Contains("data-mm-block-index=\"2\" data-mm-block-kind=\"list\"", document.Html);
        Assert.Contains("data-mm-block-kind=\"code\"", document.Html);
        Assert.Contains("data-mm-block-kind=\"quote\"", document.Html);
        Assert.Contains("data-mm-block-kind=\"rule\"", document.Html);
        Assert.Contains("data-mm-block-kind=\"math\"", document.Html);
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
    public async Task RenderResolvesRemoteImagesThroughResolverWithoutEmittingRemoteUrls()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var resolver = new CountingImageSourceResolver();
        var source = new MarkdownSource("docs/sample.md", "sample.md", "![remote](https://example.com/a.png)");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            CancellationToken.None);

        Assert.Equal(1, resolver.CallCount);
        Assert.DoesNotContain("https://example.com", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,", document.Html);
    }

    [Fact]
    public async Task RenderInfersRemoteImageMimeTypeFromUrlPathWithoutQueryString()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var resolver = new CountingImageSourceResolver();
        var source = new MarkdownSource("docs/sample.md", "sample.md", "![remote](https://example.com/a.jpg?pid=Api)");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            CancellationToken.None);

        Assert.Equal(1, resolver.CallCount);
        Assert.DoesNotContain("https://example.com", document.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/jpeg;base64,", document.Html);
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

    [Fact]
    public async Task BundledRendererCssDisablesRootScrollbarWhenChromeIsHostedNatively()
    {
        var css = await new ApplicateWebAssetEmbedder()
            .ReadTextAssetAsync("renderer.css", CancellationToken.None);

        Assert.Contains(":root[data-mm-chrome=\"off\"]", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", css, StringComparison.Ordinal);
        Assert.Contains("scrollbar-width: none;", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderedWebDocumentStartsWithRendererChromeHiddenUntilHostPreferencesArrive()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer(new ApplicateWebAssetEmbedder());
        var source = new MarkdownSource("sample.md", "sample.md", "Body");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.Contains("data-mm-chrome=\"off\"", document.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BundledRendererCssUsesShellBackgroundColors()
    {
        var css = await new ApplicateWebAssetEmbedder()
            .ReadTextAssetAsync("renderer.css", CancellationToken.None);

        Assert.Contains("--mm-document-background: #fcfaf6;", css, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--mm-document-background: #14110e;", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MermaidCodeBlockEmitsPreCodeMarkup()
    {
        var html = await RenderAsync("```mermaid\ngraph TD\nA-->B\n```\n");
        Assert.Contains("class=\"mm-mermaid\"", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"language-mermaid\" data-mm-mermaid>", html, StringComparison.Ordinal);
        Assert.Contains("graph TD\nA--&gt;B", html, StringComparison.Ordinal);
        Assert.Contains("</code></pre>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MermaidCodeBlockIsCaseInsensitive()
    {
        var html = await RenderAsync("```MERMAID\nx\n```\n");
        Assert.Contains("data-mm-mermaid", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeBlockWithLanguageUsesFirstToken()
    {
        var html = await RenderAsync("```js title=foo.js\nconst x = 1;\n```\n");
        Assert.Contains("class=\"language-js\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("language-js title", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCodeBlockDefaultsToPlaintextLanguage()
    {
        var html = await RenderAsync("```\nplain text\n```\n");
        Assert.Contains("class=\"language-plaintext\"", html, StringComparison.Ordinal);
        Assert.Contains("data-mm-code", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeBlockEscapesHtmlSpecialCharacters()
    {
        var html = await RenderAsync("```js\n<script>alert(1)</script>\n```\n");
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderPlainDocumentExcludesMermaidAndHljsBundles()
    {
        var tempRoot = ApplicateWebAssetEmbedderTests.CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var renderer = new ApplicateHtmlMarkdownRenderer(embedder);
            var source = new MarkdownSource("test.md", "test.md", "Just text\n");
            var doc = await renderer.RenderAsync(source, ReadingPreferences.Default, imageSourceResolver: null, CancellationToken.None);

            Assert.DoesNotContain("/* mermaid-js */", doc.Html);
            Assert.DoesNotContain("/* hljs */", doc.Html);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RenderMermaidDocumentIncludesMermaidAndHljs()
    {
        var tempRoot = ApplicateWebAssetEmbedderTests.CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var renderer = new ApplicateHtmlMarkdownRenderer(embedder);
            var source = new MarkdownSource("test.md", "test.md", "```mermaid\ngraph TD\n```\n");
            var doc = await renderer.RenderAsync(source, ReadingPreferences.Default, imageSourceResolver: null, CancellationToken.None);

            Assert.Contains("/* mermaid-js */", doc.Html);
            Assert.Contains("/* hljs */", doc.Html);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RenderCodeOnlyDocumentIncludesHljsButNotMermaid()
    {
        var tempRoot = ApplicateWebAssetEmbedderTests.CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var renderer = new ApplicateHtmlMarkdownRenderer(embedder);
            var source = new MarkdownSource("test.md", "test.md", "```js\nconst x = 1;\n```\n");
            var doc = await renderer.RenderAsync(source, ReadingPreferences.Default, imageSourceResolver: null, CancellationToken.None);

            Assert.Contains("/* hljs */", doc.Html);
            Assert.DoesNotContain("/* mermaid-js */", doc.Html);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<string> RenderAsync(string markdown)
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("test.md", "test.md", markdown);
        var result = await renderer.RenderAsync(source, ReadingPreferences.Default, imageSourceResolver: null, CancellationToken.None);
        return result.Html;
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

    [Fact]
    public async Task RenderBodyAsyncReturnsBodyOnlyNoHtmlOrHead()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("test.md", "test.md", "# Hello\n\nSome **bold** text.");

        var result = await renderer.RenderBodyAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.BodyHtml));
        Assert.DoesNotContain("<!doctype", result.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", result.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<head", result.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", result.BodyHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1", result.BodyHtml, StringComparison.Ordinal);
        Assert.False(result.HasMermaidBlock);
        Assert.False(result.HasCodeBlockWithSyntax);
    }

    [Fact]
    public async Task RenderBodyAsyncFlagsMermaidAndHljs()
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var content = "```mermaid\ngraph TD;A-->B;\n```\n\n```typescript\nconst x = 1;\n```";
        var source = new MarkdownSource("test.md", "test.md", content);

        var result = await renderer.RenderBodyAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        Assert.True(result.HasMermaidBlock);
        Assert.True(result.HasCodeBlockWithSyntax);
    }
}
