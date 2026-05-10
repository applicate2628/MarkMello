using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateViewerViewTests
{
    [Fact]
    public void WidthHandleIsHostedOutsideDocumentLayer()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateViewerView();

            Assert.True(view.IsWidthHandleOutsideDocumentLayerForTesting);
        }, CancellationToken.None);
    }

    [Fact]
    public void WebDocumentPreferencesKeepShellMinimapMode()
    {
        var documentPreferences = ReadingPreferences.Default with
        {
            DocumentMinimapMode = DocumentMinimapMode.Auto,
            RendererBackend = MarkdownRendererBackend.WebView
        };
        var shellPreferences = documentPreferences with
        {
            DocumentMinimapMode = DocumentMinimapMode.Off
        };

        var actual = ApplicateViewerView.CreateWebDocumentReadingPreferences(documentPreferences, shellPreferences);

        Assert.Equal(DocumentMinimapMode.Off, actual.DocumentMinimapMode);
        Assert.Equal(documentPreferences.RendererBackend, actual.RendererBackend);
        Assert.Equal(documentPreferences.ContentWidth, actual.ContentWidth);
        Assert.Equal(documentPreferences.WidthResizerVisibility, actual.WidthResizerVisibility);
    }

    [Fact]
    public void WebDocumentLayerExpandsToHostViewport()
    {
        var actual = ApplicateViewerView.CalculateDocumentLayerWidth(
            documentColumnWidth: 900,
            hostWidth: 1500,
            useWebRenderer: true);

        Assert.Equal(1500, actual);
    }

    [Fact]
    public void NativeDocumentLayerStaysAtDocumentColumnWidth()
    {
        var actual = ApplicateViewerView.CalculateDocumentLayerWidth(
            documentColumnWidth: 900,
            hostWidth: 1500,
            useWebRenderer: false);

        Assert.Equal(900, actual);
    }

    [Theory]
    [InlineData(640, 20, 680)]
    [InlineData(640, -20, 600)]
    public void WidthDragKeepsCenteredColumnScaling(double startWidth, double deltaX, double expected)
    {
        var actual = ApplicateViewerView.CalculateWidthDragContentWidth(startWidth, deltaX);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WebAvailableContentWidthReservesReportedMinimapWidth()
    {
        var actual = ApplicateViewerView.CalculateAvailableContentWidth(
            boundsWidth: 1200,
            resizeReservedWidth: 168,
            documentHorizontalPadding: 144,
            useWebRenderer: true);

        Assert.Equal(856, actual);
    }

    [Fact]
    public void NativeAvailableContentWidthAlsoReservesHandleSlot()
    {
        var actual = ApplicateViewerView.CalculateAvailableContentWidth(
            boundsWidth: 1200,
            resizeReservedWidth: 176,
            documentHorizontalPadding: 144,
            useWebRenderer: false);

        Assert.Equal(824, actual);
    }

    [Theory]
    [InlineData(WidthResizerVisibility.OnHover, false, false, 2.0, 0.0, false)]
    [InlineData(WidthResizerVisibility.Always, false, false, 2.0, 0.18, false)]
    [InlineData(WidthResizerVisibility.OnHover, true, false, 5.0, 0.72, true)]
    [InlineData(WidthResizerVisibility.Always, true, false, 5.0, 0.72, true)]
    [InlineData(WidthResizerVisibility.OnHover, false, true, 7.0, 0.9, true)]
    public void WidthHandleVisualStateHonorsVisibilityMode(
        WidthResizerVisibility visibility,
        bool isHovering,
        bool isDragging,
        double expectedWidth,
        double expectedOpacity,
        bool expectedAccent)
    {
        var state = ApplicateViewerView.CalculateWidthHandleVisualState(visibility, isHovering, isDragging);

        Assert.Equal(expectedWidth, state.Width);
        Assert.Equal(expectedOpacity, state.Opacity);
        Assert.Equal(expectedAccent, state.UseAccentBrush);
    }

    [Fact]
    public void RendererTransitionKeepsActiveSurfaceInteractiveWhilePendingLoads()
    {
        var active = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.WebView,
            pendingReady: false);
        var pending = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.WebView,
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.WebView,
            pendingReady: false);

        Assert.True(active.IsVisible);
        Assert.True(active.IsHitTestVisible);
        Assert.Equal(1, active.Opacity);
        Assert.True(pending.IsVisible);
        Assert.False(pending.IsHitTestVisible);
        Assert.Equal(0, pending.Opacity);
    }

    [Fact]
    public void RendererTransitionHandsInteractionToPendingSurfaceWhenReady()
    {
        var active = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.WebView,
            pendingReady: true);
        var pending = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.WebView,
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.WebView,
            pendingReady: true);

        Assert.True(active.IsVisible);
        Assert.False(active.IsHitTestVisible);
        Assert.Equal(0, active.Opacity);
        Assert.True(pending.IsVisible);
        Assert.True(pending.IsHitTestVisible);
        Assert.Equal(1, pending.Opacity);
    }

    [Fact]
    public void RendererTransitionSettlesOnActiveSurfaceOnly()
    {
        var native = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.Native,
            ApplicateRendererSurfaceKind.WebView,
            pending: null,
            pendingReady: false);
        var web = ApplicateRendererSurfaceTransition.CalculateVisualState(
            ApplicateRendererSurfaceKind.WebView,
            ApplicateRendererSurfaceKind.WebView,
            pending: null,
            pendingReady: false);

        Assert.False(native.IsVisible);
        Assert.False(native.IsHitTestVisible);
        Assert.Equal(0, native.Opacity);
        Assert.True(web.IsVisible);
        Assert.True(web.IsHitTestVisible);
        Assert.Equal(1, web.Opacity);
    }
}
