using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop;
using MarkMello.Presentation.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateMountPointsTests
{
    [Fact]
    public void ViewerContentSlotResolvesNamedAnchorOnce()
    {
        var viewerSlot = new ContentControl { Name = "ViewerContentSlot" };
        var bodyPanel = new Panel
        {
            Children =
            {
                new ContentControl { Name = "NamedUpstreamControl" },
                viewerSlot,
                new Border()
            }
        };
        var diagnostics = new List<string>();

        var resolver = new ApplicateMountPoints(bodyPanel, Capture(diagnostics));

        Assert.Same(viewerSlot, resolver.ViewerContentSlot);
        Assert.Same(viewerSlot, resolver.ViewerContentSlot);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ViewerContentSlotIgnoresAnUnnamedContentControl()
    {
        // The old resolver matched "first unnamed ContentControl of exactly this type", so an
        // unrelated bare ContentControl silently became the viewer host. Only the named anchor counts.
        var bodyPanel = new Panel
        {
            Children =
            {
                new ContentControl(),
                new ContentControl { Name = "ViewerContentSlot" }
            }
        };
        var diagnostics = new List<string>();

        var resolver = new ApplicateMountPoints(bodyPanel, Capture(diagnostics));

        Assert.Equal("ViewerContentSlot", resolver.ViewerContentSlot!.Name);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MissingViewerContentSlotEmitsMountPointDiagnosticAndThrows()
    {
        var bodyPanel = new Panel
        {
            Children =
            {
                new ContentControl { Name = "OnlyNamedContentControl" }
            }
        };
        var diagnostics = new List<string>();

        var resolver = new ApplicateMountPoints(bodyPanel, Capture(diagnostics));

        // Load-bearing anchor: the fork owns MainWindow.axaml, so a miss is an integration break.
        // It used to return null and let consumers quietly skip mounting the viewer.
        var error = Assert.Throws<InvalidOperationException>(() => resolver.ViewerContentSlot);
        Assert.Contains("ViewerContentSlot", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            "mount-points|mount-point-miss|anchor=viewer-content-slot",
            diagnostics);
    }

    [Fact]
    public void MissingBodyPanelEmitsMountPointDiagnosticAndThrows()
    {
        var diagnostics = new List<string>();

        var error = Assert.Throws<InvalidOperationException>(
            () => new ApplicateMountPoints(null, Capture(diagnostics)));

        Assert.Contains("BodyPanel", error.Message, StringComparison.Ordinal);
        Assert.Contains("mount-points|mount-point-miss|anchor=body-panel", diagnostics);
    }

    [Fact]
    public async Task EditPreviewMountPointsUsePreviewDocumentViewParentWhenNamedFrameIsMissing()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        // Dispatch queues onto the session thread and returns a Task; without
        // awaiting it an assertion failure inside the lambda would vanish into
        // an unobserved Task and the test could never fail (fable gate B3).
        await session.Dispatch(() =>
        {
            var editWorkspace = new EditWorkspaceView();
            var editPreview = new SyncPreviewControl();
            var diagnostics = new List<string>();
            var resolver = new ApplicateMountPoints(new Panel(), Capture(diagnostics));

            var mountPoints = resolver.ResolveEditPreviewMountPoints(editWorkspace, editPreview);

            Assert.NotNull(mountPoints.NativePreviewDocumentView);
            Assert.NotNull(mountPoints.PreviewDocumentFrame);
            Assert.Same(mountPoints.NativePreviewDocumentView.Parent, mountPoints.PreviewDocumentFrame);
            Assert.Same(editPreview, mountPoints.PreviewSourceLineSync);
            Assert.True(mountPoints.UsedPreviewDocumentFrameFallback);
            Assert.Contains(
                "mount-points|mount-point-miss|anchor=preview-document-frame",
                diagnostics);
            Assert.Contains(
                "mount-points|mount-point-fallback|anchor=preview-document-frame fallback=preview-document-view-parent",
                diagnostics);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MissingPreviewDocumentViewEmitsMountPointDiagnosticAndThrows()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            // Swap the loaded view's name scope for an empty one so the named anchor cannot resolve —
            // the same shape as an upstream merge dropping Name="PreviewDocumentView" from the axaml.
            var editWorkspace = new EditWorkspaceView();
            NameScope.SetNameScope(editWorkspace, new NameScope());
            Assert.Null(editWorkspace.FindControl<MarkdownDocumentView>("PreviewDocumentView"));

            var diagnostics = new List<string>();
            var resolver = new ApplicateMountPoints(new Panel(), Capture(diagnostics));

            var error = Assert.Throws<InvalidOperationException>(
                () => resolver.ResolveEditPreviewMountPoints(editWorkspace, new SyncPreviewControl()));

            Assert.Contains("PreviewDocumentView", error.Message, StringComparison.Ordinal);
            Assert.Contains(
                "mount-points|mount-point-miss|anchor=preview-document-view",
                diagnostics);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EditPreviewMountPointsThrowWhenBothFrameRoutesAreGone()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            // The named PreviewDocumentFrame is absent by design (the real axaml ships an unnamed
            // parent Border), so the resolver falls back to that parent. Re-parent the preview under
            // a NON-Border so BOTH routes are gone: the effective frame is load-bearing, and without
            // it the caller silently skipped mounting the edit preview — the exact silent-disable
            // this fail-fast exists to kill.
            var editWorkspace = new EditWorkspaceView();
            var preview = editWorkspace.FindControl<MarkdownDocumentView>("PreviewDocumentView");
            Assert.NotNull(preview);
            var parentBorder = Assert.IsType<Border>(preview.Parent);
            parentBorder.Child = null;
            var nonBorderParent = new Panel { Children = { preview } };
            Assert.IsType<Panel>(preview.Parent);
            Assert.NotNull(nonBorderParent);

            var diagnostics = new List<string>();
            var resolver = new ApplicateMountPoints(new Panel(), Capture(diagnostics));

            var error = Assert.Throws<InvalidOperationException>(
                () => resolver.ResolveEditPreviewMountPoints(editWorkspace, new SyncPreviewControl()));

            Assert.Contains("PreviewDocumentFrame", error.Message, StringComparison.Ordinal);
            Assert.Contains(
                "mount-points|mount-point-miss|anchor=preview-document-view-parent-frame",
                diagnostics);
        }, CancellationToken.None);
    }

    private static Action<string, string, string> Capture(List<string> diagnostics)
        => (group, evt, fields) => diagnostics.Add($"{group}|{evt}|{fields}");

    private sealed class SyncPreviewControl : ContentControl, ISourceLineScrollSyncPreview
    {
        public event EventHandler? SourceLineScrollSyncPreviewRendered
        {
            add { }
            remove { }
        }

        public event EventHandler<SourceLineScrollSyncEventArgs>? PreviewSourceLineChanged
        {
            add { }
            remove { }
        }

        public bool SyncEnabled => true;

        public void ScrollToSourceLine(int sourceLine)
        {
        }
    }
}
