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

    /// <summary>
    /// The CSP nonce in the policy directive must equal the one on the script tag.
    /// This is the invariant behind roadmap #7's "accepted unfixable residual": if the two
    /// ever diverged, CSP would block the only script tag in the shell, no JS would run at
    /// all, and shell-ready would never arrive — with no <c>window.onerror</c> to report it,
    /// because nothing executed. The design pass for that residual (see
    /// work-items/.../design-shell-init-residual.md) established that the case is
    /// unreachable at RUNTIME — both values come from one variable interpolated into one
    /// literal — so it can only be introduced by editing this template. A source-edit defect
    /// is guarded by a test, not by a runtime detector: that verdict is what makes this
    /// assertion the fix rather than new production machinery.
    /// </summary>
    [Fact]
    public void BuildShellScriptTagNonceMatchesTheCspDirectiveNonce()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            mermaidAssets: null,
            hljsAssets: null);

        var directive = System.Text.RegularExpressions.Regex.Match(html, @"script-src 'nonce-([^']+)'");
        var scriptTag = System.Text.RegularExpressions.Regex.Match(html, @"<script nonce=""([^""]+)""");

        Assert.True(directive.Success, "The CSP directive should carry a nonce.");
        Assert.True(scriptTag.Success, "The script tag should carry a nonce.");
        Assert.NotEmpty(directive.Groups[1].Value);
        Assert.Equal(directive.Groups[1].Value, scriptTag.Groups[1].Value);
    }

    // The shell's own title is the APP name, which is the right answer only while
    // no document is loaded. It is not stable for the shell's lifetime: the page
    // title is exported metadata (PDF Title field, captured HTML <head>), so the
    // renderer overwrites it with the document's name on every document swap —
    // loadDocument.ts, driven by the load-document message's documentName.
    [Fact]
    public void BuildShellTitleIsTheAppNameUntilADocumentLoads()
    {
        var html = ApplicateHtmlDocumentTemplate.BuildShell(
            ReadingPreferences.Default,
            MinimalBase,
            mermaidAssets: null,
            hljsAssets: null);

        Assert.Contains("<title>MarkMello</title>", html, StringComparison.Ordinal);
    }
}
