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
    // Bug #7: a shell navigation that fails (IsSuccess=false) while the
    // shell-ready latch is still pending must fault the latch + raise the
    // fallback, instead of leaving every shell-ready awaiter hung forever on a
    // blank surface (runtime-reproduced 2026-07-18).
    [InlineData(false, true, true)]   // nav failed + latch pending  -> fault (the fix)
    // Must NOT fault on the tolerated / harmless cases:
    [InlineData(true, true, false)]   // nav succeeded + latch pending -> shell will post document-ready
    [InlineData(false, false, false)] // nav failed but latch already completed/absent
    //   (legacy-mode superseded-navigate, or after document-ready posted) — this
    //   is the exact 2026-05-19 regression the pending-latch gate protects against
    [InlineData(true, false, false)]  // nav succeeded + latch completed/absent
    public void NavigationFailureFaultsShellReadyOnlyWhileLatchPending(
        bool navigationSucceeded,
        bool shellReadyPending,
        bool expectedFault)
    {
        Assert.Equal(
            expectedFault,
            ApplicateWebMarkdownDocumentView.ShouldFaultShellReadyOnNavigationFailureForTesting(
                navigationSucceeded,
                shellReadyPending));
    }

    [Theory]
    // A crash AFTER the shell went ready leaves the latch COMPLETED, so
    // TryInvalidateShellReady no-ops and _shellNavigated / _shellReady /
    // _shellDocumentReadyConsumed / _hasLoadedDocument keep describing the page
    // that just died — the next render skips re-navigation and posts
    // load-document into a corpse (runtime-reproduced 2026-07-21 by killing the
    // viewer's msedgewebview2 renderer child: kind=RenderProcessExited, then
    // "Web.RenderShell start"/"wait-shell-ready"/"shell-ready" in the same
    // millisecond with no "navigate-shell", against 2451 ms / 7849 ms healthy
    // baselines).
    //
    // Only the two kinds that actually kill the main frame may invalidate the
    // shell. Resetting on an auto-recovering kind would force a needless full
    // re-navigation of a live shell — and RenderProcessUnresponsive is raised
    // every few seconds on a merely busy machine, so treating it as fatal would
    // tear the shell down repeatedly under load.
    [InlineData("RenderProcessExited", true)]         // the reproduced case
    [InlineData("BrowserProcessExited", true)]        // WebView moves to Closed
    [InlineData("RenderProcessUnresponsive", false)]  // not an exit; page still alive
    [InlineData("FrameRenderProcessExited", false)]   // subframe only
    [InlineData("GpuProcessExited", false)]           // auto-recovered
    [InlineData("UtilityProcessExited", false)]       // auto-recovered
    [InlineData("SandboxHelperProcessExited", false)]
    [InlineData("PpapiPluginProcessExited", false)]
    [InlineData("PpapiBrokerProcessExited", false)]
    [InlineData("UnknownProcessExited", false)]       // never tear down on unknown
    public void ShellIsInvalidatedOnlyForProcessFailuresThatKillTheMainFrame(
        string kindName,
        bool expectedInvalidate)
    {
        // Guard the string seam: a misspelled kind would otherwise parse-fail and
        // masquerade as a passing "not fatal" case.
        Assert.True(
            ApplicateWebMarkdownDocumentView.IsKnownProcessFailureKindForTesting(kindName),
            $"'{kindName}' is not a CoreWebView2ProcessFailedKind member.");

        Assert.Equal(
            expectedInvalidate,
            ApplicateWebMarkdownDocumentView.ShouldInvalidateShellForProcessFailureForTesting(kindName));
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
