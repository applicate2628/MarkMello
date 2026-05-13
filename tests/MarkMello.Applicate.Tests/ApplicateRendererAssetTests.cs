using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRendererAssetTests
{
    private static string AssetsRoot => Path.Combine(
        Path.GetDirectoryName(typeof(ApplicateRendererAssetTests).Assembly.Location)!,
        "RendererWeb", "assets");

    [Theory]
    [InlineData("mermaid/mermaid.min.js")]
    [InlineData("mermaid/LICENSE")]
    [InlineData("mermaid/.manifest.json")]
    [InlineData("highlightjs/highlight.min.js")]
    [InlineData("highlightjs/github.min.css")]
    [InlineData("highlightjs/github-dark.min.css")]
    [InlineData("highlightjs/LICENSE")]
    [InlineData("highlightjs/.manifest.json")]
    public void RendererAssetFileExists(string relativePath)
    {
        var fullPath = Path.Combine(AssetsRoot, relativePath);
        Assert.True(File.Exists(fullPath), $"Missing renderer asset: {relativePath}");
    }

    [Theory]
    [InlineData("mermaid/.manifest.json")]
    [InlineData("highlightjs/.manifest.json")]
    public void ManifestHasVersionAndLicenseFields(string relativePath)
    {
        var fullPath = Path.Combine(AssetsRoot, relativePath);
        using var stream = File.OpenRead(fullPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("version", out var version));
        Assert.False(string.IsNullOrWhiteSpace(version.GetString()));
        Assert.True(root.TryGetProperty("license", out var license));
        Assert.False(string.IsNullOrWhiteSpace(license.GetString()));
    }

    [Theory]
    [InlineData("mermaid/.manifest.json")]
    [InlineData("highlightjs/.manifest.json")]
    public void ManifestHasNoUnresolvedPlaceholders(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(AssetsRoot, relativePath));
        Assert.DoesNotContain("<from-step-", content, StringComparison.Ordinal);
        Assert.DoesNotContain("TBD", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MermaidManifestHashMatchesAssetBytes()
    {
        var manifestPath = Path.Combine(AssetsRoot, "mermaid", ".manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.True(doc.RootElement.TryGetProperty("sha256", out var sha));
        var expected = sha.GetString();
        var actualBytes = File.ReadAllBytes(Path.Combine(AssetsRoot, "mermaid", "mermaid.min.js"));
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        Assert.Equal(expected, actualHash, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("highlight.min.js")]
    [InlineData("github.min.css")]
    [InlineData("github-dark.min.css")]
    public void HighlightManifestHashMatchesAssetBytes(string assetFile)
    {
        var manifestPath = Path.Combine(AssetsRoot, "highlightjs", ".manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.True(doc.RootElement.TryGetProperty("sha256", out var shaMap));
        Assert.True(shaMap.TryGetProperty(assetFile, out var expectedProp));
        var expected = expectedProp.GetString();
        var actualBytes = File.ReadAllBytes(Path.Combine(AssetsRoot, "highlightjs", assetFile));
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        Assert.Equal(expected, actualHash, StringComparer.OrdinalIgnoreCase);
    }
}
