using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebResourcePolicyTests
{
    [Theory]
    [InlineData("about:blank")]
    public void AllowsOnlyRendererOwnedNavigation(string url)
    {
        Assert.True(ApplicateWebResourcePolicy.IsAllowedNavigation(url));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html;charset=utf-8;base64,PGh0bWw+PC9odG1sPg==")]
    public void AllowsInitialGeneratedDocumentNavigation(string url)
    {
        Assert.True(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(url));
    }

    [Fact]
    public void RejectsApplicateRendererVirtualScheme()
    {
        // Decision 3 (Phase 2 plan): the unused applicate-renderer:// virtual
        // host scheme is dropped to shrink the attack surface. The shell page
        // for Phase 2 lives under file:// in the generated-document folder.
        Assert.False(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(
            "applicate-renderer://document/index.html"));
    }

    [Fact]
    public void AcceptsRendererShellFileInGeneratedFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "markmello-shell-test");
        var shellPath = new Uri(Path.Combine(folder, "renderer-shell.html")).AbsoluteUri;
        Assert.True(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(shellPath, folder));
    }

    [Fact]
    public void AllowsInitialGeneratedDocumentFileOnlyInsideRendererFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "markmello-web-policy");
        var allowed = new Uri(Path.Combine(root, "document-1.html")).AbsoluteUri;
        var blockedOutside = new Uri(Path.Combine(Path.GetTempPath(), "document-1.html")).AbsoluteUri;
        var blockedExtension = new Uri(Path.Combine(root, "document-1.txt")).AbsoluteUri;

        Assert.True(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(allowed, root));
        Assert.False(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(blockedOutside, root));
        Assert.False(ApplicateWebResourcePolicy.IsAllowedInitialDocumentNavigation(blockedExtension, root));
        Assert.False(ApplicateWebResourcePolicy.IsAllowedNavigation(allowed));
    }

    [Theory]
    [InlineData("https://example.com/script.js")]
    [InlineData("http://example.com/image.png")]
    [InlineData("file:///private/example.txt")]
    [InlineData("data:text/html;base64,PGh0bWw+PC9odG1sPg==")]
    [InlineData("applicate-renderer://document/index.html")]
    [InlineData("javascript:alert(1)")]
    public void BlocksExternalNavigation(string url)
    {
        Assert.False(ApplicateWebResourcePolicy.IsAllowedNavigation(url));
    }

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("data:font/woff2;base64,d09GMgABAAAA")]
    public void AllowsOnlyInlineRendererResources(string url)
    {
        Assert.True(ApplicateWebResourcePolicy.IsAllowedResource(url));
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("file:///private/a.png")]
    [InlineData("applicate-renderer://assets/renderer.css")]
    [InlineData("javascript:alert(1)")]
    public void BlocksExternalResources(string url)
    {
        Assert.False(ApplicateWebResourcePolicy.IsAllowedResource(url));
    }
}
