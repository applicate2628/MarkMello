using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewShellModeTests
{
    [Fact]
    public void ShouldCompleteRenderRequiresAllThreeSignalsInShellMode()
    {
        // The state machine that gates DocumentRendered is the same in shell
        // and legacy modes. This test pins the contract.
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(false, false, false));
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, false, false));
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, true, false));
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
