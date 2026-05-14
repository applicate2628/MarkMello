using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateHtmlDocumentTemplateTests
{
    private static readonly ApplicateWebBaseAssets BaseAssets = new(
        RendererCss: "/* renderer-css */",
        KatexCss: "/* katex-css */",
        KatexScript: "/* katex-js */",
        RendererScript: "/* renderer-js */");

    [Fact]
    public void BuildWithoutMermaidOrHljsIncludesOnlyBaseScripts()
    {
        var html = ApplicateHtmlDocumentTemplate.Build(
            "t.md", "<p>hi</p>", ReadingPreferences.Default, BaseAssets, mermaidAssets: null, hljsAssets: null);

        Assert.Contains("/* katex-js */", html);
        Assert.Contains("/* renderer-js */", html);
        Assert.DoesNotContain("/* mermaid", html);
        Assert.DoesNotContain("/* hljs", html);
    }

    [Fact]
    public void BuildWithMermaidIncludesMermaidScript()
    {
        var mermaid = new ApplicateWebMermaidAssets("/* mermaid-js */");
        var html = ApplicateHtmlDocumentTemplate.Build(
            "t.md", "<p>hi</p>", ReadingPreferences.Default, BaseAssets, mermaid, hljsAssets: null);

        Assert.Contains("/* mermaid-js */", html);
    }

    [Fact]
    public void BuildWithHljsIncludesScriptAndCssNestedByTheme()
    {
        var hljs = new ApplicateWebHighlightAssets(
            Script: "/* hljs-main */",
            LightCss: "pre code.hljs{display:block;background:#fff;color:#24292e}.hljs-keyword{color:#d73a49}",
            DarkCss: "pre code.hljs{display:block;background:#0d1117;color:#c9d1d9}.hljs-keyword{color:#ff7b72}");
        var html = ApplicateHtmlDocumentTemplate.Build(
            "t.md", "<p>hi</p>", ReadingPreferences.Default, BaseAssets, mermaidAssets: null, hljsAssets: hljs);

        Assert.Contains("/* hljs-main */", html);
        Assert.Contains("[data-theme=\"light\"] { pre code.hljs{display:block;background:#fff;color:#24292e}.hljs-keyword{color:#d73a49} }", html);
        Assert.Contains("[data-theme=\"dark\"] { pre code.hljs{display:block;background:#0d1117;color:#c9d1d9}.hljs-keyword{color:#ff7b72} }", html);
    }

    [Fact]
    public void BuildCspDropsNonceFromStyleSrc()
    {
        var html = ApplicateHtmlDocumentTemplate.Build(
            "t.md", "<p>hi</p>", ReadingPreferences.Default, BaseAssets, mermaidAssets: null, hljsAssets: null);

        Assert.Contains("style-src 'unsafe-inline'", html);
        Assert.DoesNotContain("style-src 'nonce-", html);
        Assert.Contains("script-src 'nonce-", html);
    }

    [Fact]
    public void BuildKeepsExistingMetaAndTitle()
    {
        var html = ApplicateHtmlDocumentTemplate.Build(
            "hello.md", "<p>hi</p>", ReadingPreferences.Default, BaseAssets, mermaidAssets: null, hljsAssets: null);

        Assert.Contains("<title>hello.md</title>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("data-mm-chrome=\"off\"", html);
    }
}
