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
    public void ViewerContentSlotResolvesUnnamedContentControlOnce()
    {
        var viewerSlot = new ContentControl();
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
    public void MissingViewerContentSlotEmitsMountPointDiagnosticAndReturnsNull()
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

        Assert.Null(resolver.ViewerContentSlot);
        Assert.Contains(
            "mount-points|mount-point-miss|anchor=viewer-content-slot",
            diagnostics);
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
