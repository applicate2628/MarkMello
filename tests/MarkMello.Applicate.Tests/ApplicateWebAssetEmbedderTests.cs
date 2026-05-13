using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebAssetEmbedderTests
{
    [Fact]
    public async Task LoadBaseBundleAsyncReadsAllBaseAssets()
    {
        var tempRoot = CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var assets = await embedder.LoadBaseBundleAsync(CancellationToken.None);

            Assert.Equal("/* renderer-css */", assets.RendererCss);
            Assert.Contains("/* katex-css */", assets.KatexCss);
            Assert.Equal("/* katex-js */", assets.KatexScript);
            Assert.Equal("/* renderer-js */", assets.RendererScript);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoadMermaidAsyncReadsMermaidScript()
    {
        var tempRoot = CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var assets = await embedder.LoadMermaidAsync(CancellationToken.None);
            Assert.Equal("/* mermaid-js */", assets.Script);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LoadHighlightAsyncReadsAllHighlightAssets()
    {
        var tempRoot = CreateAssetsFixture();
        try
        {
            var embedder = new ApplicateWebAssetEmbedder(tempRoot);
            var assets = await embedder.LoadHighlightAsync(CancellationToken.None);
            Assert.Equal("/* hljs */", assets.Script);
            Assert.Equal("/* light */", assets.LightCss);
            Assert.Equal("/* dark */", assets.DarkCss);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    internal static string CreateAssetsFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mm-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "katex", "fonts"));
        Directory.CreateDirectory(Path.Combine(root, "mermaid"));
        Directory.CreateDirectory(Path.Combine(root, "highlightjs"));

        File.WriteAllText(Path.Combine(root, "renderer.css"), "/* renderer-css */");
        File.WriteAllText(Path.Combine(root, "renderer.js"), "/* renderer-js */");
        File.WriteAllText(Path.Combine(root, "katex", "katex.min.css"), "/* katex-css */");
        File.WriteAllText(Path.Combine(root, "katex", "katex.min.js"), "/* katex-js */");
        File.WriteAllText(Path.Combine(root, "mermaid", "mermaid.min.js"), "/* mermaid-js */");
        File.WriteAllText(Path.Combine(root, "highlightjs", "highlight.min.js"), "/* hljs */");
        File.WriteAllText(Path.Combine(root, "highlightjs", "github.min.css"), "/* light */");
        File.WriteAllText(Path.Combine(root, "highlightjs", "github-dark.min.css"), "/* dark */");
        return root;
    }
}
