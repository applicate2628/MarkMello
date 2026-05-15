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
        Assert.True(active.ZIndex > pending.ZIndex);
        Assert.True(pending.IsVisible);
        Assert.False(pending.IsHitTestVisible);
        Assert.Equal(0, pending.Opacity);
        Assert.True(pending.TranslateX < 0);
    }

    [Theory]
    // Native surface only needs the document/prefs when it is the renderer
    // the user will actually see. The WebView is the primary renderer, so
    // when both requested and active are WebView the native surface stays
    // out of the render pipeline entirely — no Avalonia layout work for a
    // surface that will never paint. The native surface is only updated
    // when the request explicitly targets Native (user/toolchain choice)
    // or when it is currently active (fallback after a WebView failure).
    [InlineData((int)ApplicateRendererSurfaceKind.WebView, (int)ApplicateRendererSurfaceKind.Native, true, false, true)]
    [InlineData((int)ApplicateRendererSurfaceKind.WebView, (int)ApplicateRendererSurfaceKind.Native, false, false, true)]
    [InlineData((int)ApplicateRendererSurfaceKind.WebView, (int)ApplicateRendererSurfaceKind.Native, true, true, true)]
    [InlineData((int)ApplicateRendererSurfaceKind.Native, (int)ApplicateRendererSurfaceKind.Native, true, false, true)]
    [InlineData((int)ApplicateRendererSurfaceKind.Native, (int)ApplicateRendererSurfaceKind.WebView, true, false, true)]
    [InlineData((int)ApplicateRendererSurfaceKind.WebView, (int)ApplicateRendererSurfaceKind.WebView, true, false, false)]
    [InlineData((int)ApplicateRendererSurfaceKind.WebView, (int)ApplicateRendererSurfaceKind.WebView, false, true, false)]
    public void NativeSurfaceUpdateOnlyWhenNativeIsNeeded(
        int requestedSurface,
        int activeSurface,
        bool hasRenderedDocument,
        bool documentChanged,
        bool expected)
    {
        var actual = ApplicateViewerView.ShouldUpdateNativeSurfaceForTesting(
            (ApplicateRendererSurfaceKind)requestedSurface,
            (ApplicateRendererSurfaceKind)activeSurface,
            hasRenderedDocument,
            documentChanged);

        Assert.Equal(expected, actual);
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
        Assert.True(pending.ZIndex > active.ZIndex);
        Assert.True(pending.IsVisible);
        Assert.True(pending.IsHitTestVisible);
        Assert.Equal(1, pending.Opacity);
        Assert.Equal(0, pending.TranslateX);
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

    [Theory]
    [InlineData(120, 0, 16, 800, 120)]
    [InlineData(3, 1, 20, 800, 180)]
    [InlineData(1, 2, 16, 1000, 850)]
    [InlineData(1, 2, 24, 0, 24)]
    public void WebWheelDeltaUsesRendererDeltaMode(
        double deltaY,
        int deltaMode,
        double smallChangeHeight,
        double viewportHeight,
        double expected)
    {
        Assert.Equal(expected, ApplicateViewerView.NormalizeWebWheelDeltaForTesting(
            deltaY,
            deltaMode,
            smallChangeHeight,
            viewportHeight));
    }

}
