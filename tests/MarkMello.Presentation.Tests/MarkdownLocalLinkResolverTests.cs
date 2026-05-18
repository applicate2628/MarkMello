using MarkMello.Presentation.Views.Markdown;

namespace MarkMello.Presentation.Tests;

public sealed class MarkdownLocalLinkResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public MarkdownLocalLinkResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Tests.LocalLinks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void TryResolveReturnsMarkdownDocumentForRelativeMarkdownLink()
    {
        var sourcePath = WriteFile("source.md", "# Source");
        var targetPath = WriteFile("notes\\related.md", "# Related");

        var resolved = MarkdownLocalLinkResolver.TryResolve(
            "notes/related.md#details",
            sourcePath,
            File.Exists,
            out var target);

        Assert.True(resolved);
        Assert.Equal(MarkdownLocalLinkKind.MarkdownDocument, target.Kind);
        Assert.Equal(targetPath, target.Path);
    }

    [Fact]
    public void TryResolveReturnsExternalFileForRelativeNonMarkdownLink()
    {
        var sourcePath = WriteFile("source.md", "# Source");
        var targetPath = WriteFile("attachments\\report.pdf", "pdf");

        var resolved = MarkdownLocalLinkResolver.TryResolve(
            "attachments/report.pdf",
            sourcePath,
            File.Exists,
            out var target);

        Assert.True(resolved);
        Assert.Equal(MarkdownLocalLinkKind.ExternalFile, target.Kind);
        Assert.Equal(targetPath, target.Path);
    }

    [Fact]
    public void TryResolveIgnoresRemoteUrls()
    {
        var sourcePath = WriteFile("source.md", "# Source");

        var resolved = MarkdownLocalLinkResolver.TryResolve(
            "https://example.com/report.pdf",
            sourcePath,
            File.Exists,
            out var target);

        Assert.False(resolved);
        Assert.Equal(default, target);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
