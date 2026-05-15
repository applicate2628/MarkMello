using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateHtmlShellTemplateTests
{
    private static readonly ApplicateWebBaseAssets MinimalBase = new(
        RendererCss: "/* css */",
        KatexCss: "/* katex-css */",
        KatexScript: "// katex-js",
        RendererScript: "// renderer-js");

    [Fact]
    public void BuildShellIncludesEmptyMmDocumentMain()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            new ApplicateWebMermaidAssets("// mermaid"),
            new ApplicateWebHighlightAssets("// hljs", "/* light */", "/* dark */"));

        Assert.Contains("<main class=\"mm-document\"", html, StringComparison.Ordinal);
        // Body of the main element must be empty in the shell — no per-document content.
        Assert.Matches(@"<main class=""mm-document""[^>]*>\s*</main>", html);
    }

    [Fact]
    public void BuildShellAlwaysEmbedsMermaidAndHljsAssets()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            new ApplicateWebMermaidAssets("// mermaid-marker-9921"),
            new ApplicateWebHighlightAssets("// hljs-marker-9922", "/* light */", "/* dark */"));

        Assert.Contains("// mermaid-marker-9921", html, StringComparison.Ordinal);
        Assert.Contains("// hljs-marker-9922", html, StringComparison.Ordinal);
        Assert.Contains("// katex-js", html, StringComparison.Ordinal);
        Assert.Contains("// renderer-js", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellCarriesCspDirective()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            mermaidAssets: null,
            hljsAssets: null);

        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("script-src 'nonce-", html, StringComparison.Ordinal);
        Assert.Contains("style-src 'unsafe-inline'", html, StringComparison.Ordinal);
        Assert.Contains("img-src data:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellTitleIsStableForShellLifetime()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            mermaidAssets: null,
            hljsAssets: null);

        Assert.Contains("<title>MarkMello</title>", html, StringComparison.Ordinal);
    }
}
