using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewShellModeTests
{
    [Fact]
    public void ShouldCompleteRenderGatesOnLoadedDocumentAndLayoutReady()
    {
        // The state machine that gates DocumentRendered is the same in shell
        // and legacy modes. Render completion no longer waits for minimap
        // state — minimapSourceReady can lag the first paint when an async
        // pipeline is cancelled mid-flight (F-04 multi-fire), and gating the
        // render on it caused the renderer to never declare completion after
        // tab-switch loads. The minimap visibility check now runs from the
        // renderer's own observer chain (queueMinimapViewportUpdate /
        // updateMinimapVisibility) once policy + layout settle, decoupled
        // from this render-completion gate.
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(false, false, false));
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, false, false));
        Assert.True(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, true, false));
        Assert.True(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, true, true));
    }

    [Theory]
    [InlineData("related.md#details")]
    [InlineData("related.md?plain=1")]
    public void LocalMarkdownLinkResolverIgnoresFragmentAndQueryWhenCheckingFileExtension(string href)
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.md");
        var targetPath = Path.Combine(temp.Path, "related.md");
        File.WriteAllText(sourcePath, "# Source");
        File.WriteAllText(targetPath, "# Related");

        var resolved = ApplicateWebMarkdownDocumentView.TryResolveLocalMarkdownLinkForTesting(
            href,
            sourcePath,
            out var resolvedPath);

        Assert.True(resolved);
        Assert.Equal(targetPath, resolvedPath);
    }

    [Fact]
    public void LocalMarkdownLinkResolverDoesNotTreatRemoteMarkdownUrlAsLocalPath()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.md");
        File.WriteAllText(sourcePath, "# Source");

        var resolved = ApplicateWebMarkdownDocumentView.TryResolveLocalMarkdownLinkForTesting(
            "https://example.com/related.md",
            sourcePath,
            out var resolvedPath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void LocalFileLinkResolverReturnsNonMarkdownFilesForShellLaunch()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.md");
        var targetPath = Path.Combine(temp.Path, "data.csv");
        File.WriteAllText(sourcePath, "# Source");
        File.WriteAllText(targetPath, "a,b");

        var resolved = ApplicateWebMarkdownDocumentView.TryResolveLocalFileLinkForTesting(
            "data.csv",
            sourcePath,
            out var resolvedPath);

        Assert.True(resolved);
        Assert.Equal(targetPath, resolvedPath);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MarkMello.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
