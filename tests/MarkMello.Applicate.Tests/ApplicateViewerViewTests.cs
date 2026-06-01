using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateViewerViewTests
{
    [Fact]
    public void ConstructsWithoutSharedHostAndExposesEmptyWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateViewerView();

            Assert.NotNull(view.WebSlotForTesting);
            Assert.False(view.IsFailureViewVisibleForTesting);
        }, CancellationToken.None);
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

    [Fact]
    public void RenderedDocumentChangeDoesNotIssueDuplicateWebRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)");

        Assert.Contains("nameof(MainWindowViewModel.Document)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(MainWindowViewModel.RenderedDocument)", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerRestoresReadingProgressAfterDocumentRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var issueRender = ExtractMethodBody(codeBehind, "private void IssueRenderRequest()");
        var scrollHandler = ExtractMethodBody(codeBehind, "private void OnHostScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)");
        var renderedHandler = ExtractMethodBody(codeBehind, "private void OnHostDocumentRendered(object? sender, EventArgs e)");

        Assert.Contains("_pendingScrollRestoreProgress", issueRender, StringComparison.Ordinal);
        Assert.Contains("_pendingScrollRestoreProgress.HasValue && !_sharedHost.View.LastLayoutReadyWasCached", scrollHandler, StringComparison.Ordinal);
        Assert.Contains("!_sharedHost.View.LastLayoutReadyWasCached", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("_sharedHost.View.ScrollToProgress(restoreProgress.Value);", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ReadingProgress = restoreProgress.Value;", renderedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerVisibilityChainStillAttachesBeforeRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnEffectiveVisibilityChanged()");

        Assert.True(
            handler.IndexOf("EnsureSharedHostMounted(force: true);", StringComparison.Ordinal)
            < handler.IndexOf("IssueRenderRequest();", StringComparison.Ordinal));
        Assert.DoesNotContain("Opacity", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerPassesContextualTransactionGenerationToSharedHost()
    {
        var codeBehind = ReadViewerCodeBehind();
        var issueRender = ExtractMethodBody(codeBehind, "private void IssueRenderRequest()");

        Assert.Contains(
            "ApplicateModeTransactionContext.GetTransactionGeneration(_webSlot)",
            issueRender,
            StringComparison.Ordinal);
        Assert.Contains(
            "transactionGeneration:",
            issueRender,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HeavyDocumentResizeDebouncesOnlyLiveWebWidthEcho()
    {
        var codeBehind = ReadViewerCodeBehind();
        var sizeChanged = ExtractMethodBody(codeBehind, "protected override void OnSizeChanged(");
        var syncFromViewModel = ExtractMethodBody(codeBehind, "private void SyncFromViewModel()");
        var widthDrag = ExtractMethodBody(codeBehind, "private void ApplyWidthDragDelta(");
        var hostWidthDrag = ExtractMethodBody(codeBehind, "private void OnHostWidthDragRequested(");
        var applyColumnWidth = ExtractMethodBody(codeBehind, "private void ApplyColumnWidth(");
        var debounceGate = ExtractMethodBody(codeBehind, "private bool ShouldDebounceLiveWebWidthUpdates()");

        Assert.Contains("ApplyColumnWidth(deferWebContentWidth: ShouldDebounceLiveWebWidthUpdates());", sizeChanged, StringComparison.Ordinal);
        Assert.Contains("ApplyColumnWidth();", syncFromViewModel, StringComparison.Ordinal);
        Assert.Contains("ApplyColumnWidth();", widthDrag, StringComparison.Ordinal);
        Assert.Contains("UpdateWidthDragManualContentWidth(e.DeltaX);", hostWidthDrag, StringComparison.Ordinal);
        Assert.Contains("return;", ExtractFromMarker(hostWidthDrag, "if (e.Phase == ApplicateWebWidthDragPhase.Move)"), StringComparison.Ordinal);
        Assert.Contains("ScheduleDeferredWebAvailableContentWidth(availableContentWidth);", applyColumnWidth, StringComparison.Ordinal);
        Assert.Contains("ApplyWebAvailableContentWidth(availableContentWidth);", applyColumnWidth, StringComparison.Ordinal);
        Assert.Contains("Content.Length: > HeavyDocumentResizeContentLengthThreshold", debounceGate, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerDefersLargeTocHeadingUpdatesBehindRendererReveal()
    {
        var codeBehind = ReadViewerCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnHostHeadingsChanged(");
        var unwire = ExtractMethodBody(codeBehind, "private void UnwireSharedHostEvents()");
        var rendered = ExtractMethodBody(codeBehind, "private void OnHostDocumentRendered(object? sender, EventArgs e)");
        var updater = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateDeferredHeadingUpdater.cs"));

        Assert.Contains("_headingUpdater.Apply(", handler, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_viewModel, viewModel)", handler, StringComparison.Ordinal);
        Assert.Contains("_headingUpdater.FlushPending();", rendered, StringComparison.Ordinal);
        Assert.Contains("_headingUpdater.Invalidate();", unwire, StringComparison.Ordinal);
        Assert.Contains("LargeHeadingUpdateThreshold = 250", updater, StringComparison.Ordinal);
        Assert.Contains("LargeHeadingFlushDelay = TimeSpan.FromMilliseconds(80)", updater, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(LargeHeadingFlushDelay)", updater, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Background", updater, StringComparison.Ordinal);
        Assert.Contains("public void FlushPending()", updater, StringComparison.Ordinal);
        Assert.Contains("viewModel.UpdateDocumentHeadings(snapshot);", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void TocHeadingUpdaterDefersEmptyPayloadsToAvoidCollapseFlash()
    {
        var updater = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateDeferredHeadingUpdater.cs"));

        Assert.Contains("headings.Count == 0", updater, StringComparison.Ordinal);
        Assert.Contains("ShouldDefer(headings)", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void TocPanelVirtualizesHeadingRows()
    {
        var tocPanel = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateTocPanel.cs"));

        Assert.Contains("ItemsControl _itemsControl", tocPanel, StringComparison.Ordinal);
        Assert.Contains("new VirtualizingStackPanel", tocPanel, StringComparison.Ordinal);
        Assert.Contains("_itemsControl.ItemsSource = headings;", tocPanel, StringComparison.Ordinal);
        Assert.Contains("_rowIndexById[heading.Id] = index;", tocPanel, StringComparison.Ordinal);
        Assert.Contains("_itemsControl.ScrollIntoView(index);", tocPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("_itemsHost.Children.Add(row);", tocPanel, StringComparison.Ordinal);
    }

    [Fact]
    public void TocPanelVirtualizedRowFactoryToleratesNullRecycleItem()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var panel = new ApplicateTocPanel();
            var method = typeof(ApplicateTocPanel).GetMethod("BuildHeadingRow", BindingFlags.Instance | BindingFlags.NonPublic);

            var row = method?.Invoke(panel, [null]);

            Assert.NotNull(row);
        }, CancellationToken.None);
    }

    [Fact]
    public void TocPanelActiveHeadingRefreshClearsAllMaterializedRowsBeforeScrolling()
    {
        var tocPanel = ReadTocPanelCodeBehind();
        var refresh = ExtractMethodBody(tocPanel, "private void HighlightActiveHeading(string? activeId, bool allowVirtualizedScroll)");
        var virtualizedScrollStart = refresh.IndexOf("if (allowVirtualizedScroll", StringComparison.Ordinal);

        Assert.True(virtualizedScrollStart >= 0, "HighlightActiveHeading should keep virtualized-row scroll fallback.");
        Assert.DoesNotContain("return;", refresh[..virtualizedScrollStart], StringComparison.Ordinal);
    }

    [Fact]
    public void TocPanelClearsPreviouslyActiveMaterializedRowsAfterNewActiveRow()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var panel = new ApplicateTocPanel();
            var buildHeadingRow = typeof(ApplicateTocPanel).GetMethod(
                "BuildHeadingRow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var highlightActiveHeading = typeof(ApplicateTocPanel).GetMethod(
                "HighlightActiveHeading",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(string), typeof(bool)],
                modifiers: null);

            var first = Assert.IsType<Border>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading("first", 1, "First", 0),
            ]));
            var second = Assert.IsType<Border>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading("second", 2, "Second", 10),
            ]));
            var third = Assert.IsType<Border>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading("third", 2, "Third", 20),
            ]));

            highlightActiveHeading?.Invoke(panel, ["third", false]);
            Assert.Same(Brushes.LightYellow, third.Background);

            highlightActiveHeading?.Invoke(panel, ["first", false]);

            Assert.Same(Brushes.LightYellow, first.Background);
            Assert.Same(Brushes.Transparent, second.Background);
            Assert.Same(Brushes.Transparent, third.Background);
        }, CancellationToken.None);
    }

    [Fact]
    public void TransactionGenerationContextInheritsToConsumerWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var parent = new Avalonia.Controls.Grid();
            var child = new Avalonia.Controls.Panel();
            parent.Children.Add(child);

            ApplicateModeTransactionContext.SetTransactionGeneration(parent, 123);

            Assert.Equal(123, ApplicateModeTransactionContext.GetTransactionGeneration(child));
        }, CancellationToken.None);
    }

    private static string ReadViewerCodeBehind()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateViewerView.cs"));

    private static string ReadTocPanelCodeBehind()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateTocPanel.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} should exist.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"{signature} should have a body.");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return source[braceStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} body was not closed.");
    }

    private static string ExtractFromMarker(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} should exist.");
        return source[start..];
    }
}
