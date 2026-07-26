using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using CSharpMath.Avalonia;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateViewerViewTests
{
    [Fact]
    public async Task ConstructsWithoutSharedHostAndExposesEmptyWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
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
        var sizeChanged = ExtractMethodBody(codeBehind, "protected override void OnSizeChanged(");
        var syncFromViewModel = ExtractMethodBody(codeBehind, "private void SyncFromViewModel()");
        var ensureMounted = ExtractMethodBody(codeBehind, "private void EnsureSharedHostMountedForRender()");
        var queueMount = ExtractMethodBody(codeBehind, "private void QueueWebSlotLayoutMount()");
        var layoutUpdated = ExtractMethodBody(codeBehind, "private void OnWebSlotLayoutUpdatedForMount(");
        var detached = ExtractMethodBody(codeBehind, "protected override void OnDetachedFromVisualTree(");

        Assert.Contains("SyncFromViewModel();", handler, StringComparison.Ordinal);
        Assert.True(
            syncFromViewModel.IndexOf("ApplyColumnWidth();", StringComparison.Ordinal)
            < syncFromViewModel.IndexOf("EnsureSharedHostMountedForRender();", StringComparison.Ordinal));
        Assert.True(
            syncFromViewModel.IndexOf("EnsureSharedHostMountedForRender();", StringComparison.Ordinal)
            < syncFromViewModel.IndexOf("IssueRenderRequest();", StringComparison.Ordinal));
        Assert.True(
            sizeChanged.IndexOf("_hasValidBounds = true;", StringComparison.Ordinal)
            < sizeChanged.IndexOf("SyncFromViewModel();", StringComparison.Ordinal));
        Assert.Contains("_webSlot.Bounds.Width <= 0 || _webSlot.Bounds.Height <= 0", ensureMounted, StringComparison.Ordinal);
        Assert.Contains("_documentShell.UpdateLayout();", ensureMounted, StringComparison.Ordinal);
        Assert.Contains("QueueWebSlotLayoutMount();", ensureMounted, StringComparison.Ordinal);
        Assert.Contains("EnsureSharedHostMounted(force: true);", ensureMounted, StringComparison.Ordinal);
        Assert.Contains("_webSlot.LayoutUpdated += OnWebSlotLayoutUpdatedForMount;", queueMount, StringComparison.Ordinal);
        Assert.Contains("SyncFromViewModel();", layoutUpdated, StringComparison.Ordinal);
        Assert.Contains("ReleasePendingWebSlotLayoutMount();", detached, StringComparison.Ordinal);
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
    public void ViewerPullsRetainedHeadingsForConsumerDebtAfterRequestRender()
    {
        // TEXT PIN, not a behavioural guard (round-3 adversarial gate on
        // ca045b4, TASK 1, 2026-07-26): the real end-to-end coverage for this
        // pull (ApplicateSharedWebViewHostRealHostTests, e.g.
        // ReopenWithAChangedAvailableContentWidthAndAnEmptyTocRestoresTheHeadings)
        // drives ApplicateWebMarkdownDocumentView.TryRaiseRetainedHeadingsForConsumerDebt
        // directly through host.View -- it never calls IssueRenderRequest, so
        // deleting the production call below, or moving it before RequestRender,
        // left the whole 730-test Applicate suite green with no pin catching it.
        // This test catches removal, reordering, or a changed consumer-predicate
        // argument at the SOURCE-TEXT level only; it proves nothing about
        // runtime behaviour (that is the job of the real-host guards above and
        // HasHeadingDebtMatchesAllInputCombinations below).
        //
        // Argument text updated by R5 (design §9.4): the inline
        // !_viewModel.HasDocumentHeadings && !_failureView.IsVisible
        // expression moved to the single-owner ApplicateDeferredHeadingUpdater.
        // HasHeadingDebt predicate; this call site passes its own three inputs.
        var codeBehind = ReadViewerCodeBehind();
        var issueRender = ExtractMethodBody(codeBehind, "private void IssueRenderRequest()");

        Assert.Contains(
            "TryRaiseRetainedHeadingsForConsumerDebt(",
            issueRender,
            StringComparison.Ordinal);
        Assert.Contains(
            "consumerHasHeadingDebt: ApplicateDeferredHeadingUpdater.HasHeadingDebt(",
            issueRender,
            StringComparison.Ordinal);
        Assert.Contains("hasViewModel: true", issueRender, StringComparison.Ordinal);
        Assert.Contains("consumerHasHeadings: _viewModel.HasDocumentHeadings", issueRender, StringComparison.Ordinal);
        Assert.Contains("failureViewVisible: _failureView.IsVisible", issueRender, StringComparison.Ordinal);
        // F2 (round-5 gate finding, 2026-07-26): the ordering assertion below
        // passes vacuously if the left operand is deleted -- IndexOf returns
        // -1, and -1 < a positive index is still true. Pin the left
        // operand's existence as a precondition so deleting the RequestRender
        // call itself fails this test, instead of silently ceasing to prove
        // the ordering it claims to prove.
        Assert.Contains(
            "_sharedHost.RequestRender(_viewModel.Document, request, transactionGeneration: transactionGeneration);",
            issueRender,
            StringComparison.Ordinal);
        Assert.True(
            issueRender.IndexOf(
                "_sharedHost.RequestRender(_viewModel.Document, request, transactionGeneration: transactionGeneration);",
                StringComparison.Ordinal)
            < issueRender.IndexOf("TryRaiseRetainedHeadingsForConsumerDebt(", StringComparison.Ordinal),
            "The consumer-owned debt pull must run AFTER RequestRender (invariant I9, design D1.c) -- UpdateInputs may clear _hasLoadedDocument, which the pull's guard ladder depends on.");
    }

    [Fact]
    public void ViewerRetryCurrentRenderPullsRetainedHeadingsForConsumerDebtAfterRetryRender()
    {
        // TEXT PIN (design work-items/active/2026-07-25-toc-empty-on-open/
        // design.md §9.3/§9.5 D8): a retry that resolves via the shared
        // host's cache-hit fast path commits the document with
        // DocumentHeadings still empty from the renderer-failure clear, and
        // no other pull call site re-enters for it -- RetryCurrentRender must
        // run the SAME consumer-owned debt pull IssueRenderRequest uses,
        // after _sharedHost.RetryRender() (invariant I9: the pull runs after
        // a RequestRender, and RetryRender's Commit() is the render event
        // this pull settles for).
        var codeBehind = ReadViewerCodeBehind();
        var retryCurrentRender = ExtractMethodBody(codeBehind, "private void RetryCurrentRender()");

        Assert.Contains(
            "TryRaiseRetainedHeadingsForConsumerDebt(",
            retryCurrentRender,
            StringComparison.Ordinal);
        Assert.Contains(
            "consumerHasHeadingDebt: ApplicateDeferredHeadingUpdater.HasHeadingDebt(",
            retryCurrentRender,
            StringComparison.Ordinal);
        // F1 (round-5 gate finding, 2026-07-26): this call site was pinned
        // only by "TryRaiseRetainedHeadingsForConsumerDebt is invoked" and
        // ordering -- the three named arguments (mirroring the
        // IssueRenderRequest pin above) were not pinned, so
        // `failureViewVisible: _failureView.IsVisible` could silently become
        // `failureViewVisible: false` (or the other two arguments could
        // drift) with the full suite staying green.
        Assert.Contains("hasViewModel: true", retryCurrentRender, StringComparison.Ordinal);
        Assert.Contains("consumerHasHeadings: _viewModel.HasDocumentHeadings", retryCurrentRender, StringComparison.Ordinal);
        Assert.Contains("failureViewVisible: _failureView.IsVisible", retryCurrentRender, StringComparison.Ordinal);
        // F2 (round-5 gate finding, 2026-07-26): pin the left operand's
        // existence as a precondition -- otherwise deleting
        // `_sharedHost?.RetryRender();` makes IndexOf return -1, and
        // -1 < a positive index still satisfies the ordering assertion below.
        Assert.Contains("_sharedHost?.RetryRender();", retryCurrentRender, StringComparison.Ordinal);
        Assert.True(
            retryCurrentRender.IndexOf("_sharedHost?.RetryRender();", StringComparison.Ordinal)
            < retryCurrentRender.IndexOf("TryRaiseRetainedHeadingsForConsumerDebt(", StringComparison.Ordinal),
            "The consumer-owned debt pull must run AFTER RetryRender (invariant I9, design D8).");
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
        Assert.Contains("bool deferLargeUntilExplicitFlush", updater, StringComparison.Ordinal);
        Assert.Contains("ScheduleApply(", updater, StringComparison.Ordinal);
        // Strengthened by Part D (design 2026-07-25-toc-empty-on-open §2/§9
        // H2): defer is no longer solely "!_documentRenderedForCurrentRequest"
        // -- a same-source no-op reload re-emits headings via the CONSUMER's
        // own TryRaiseRetainedHeadingsForConsumerDebt pull (design REVISION 3
        // D1, ca045b4; corrected 2026-07-26, round-3 gate finding B8 -- this
        // is NOT a host-side re-emit, that branch was deleted from
        // ApplicateSharedWebViewHost.RequestRender), with NO fresh
        // DocumentRendered to ever flip that flag, so a >=250-heading no-op
        // reopen would defer forever without the additional conjunct. See
        // ShouldDeferLargeTocHeadingUpdatesMatchesAllFourInputCombinations
        // below for the behavioural guard this source-text pin cannot give.
        Assert.Contains(
            "deferLargeUntilExplicitFlush: deferLargeUntilExplicitFlush",
            handler,
            StringComparison.Ordinal);
        Assert.Contains("ShouldDeferLargeTocHeadingUpdates(", handler, StringComparison.Ordinal);
        Assert.Contains("_documentRenderedForCurrentRequest = true;", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void ShouldDeferLargeTocHeadingUpdatesMatchesAllFourInputCombinations(
        bool documentRenderedForCurrentRequest,
        bool hasLoadedAndPaintedDocument,
        bool expectedDefer)
    {
        // Behavioural guard for Part D (design 2026-07-25-toc-empty-on-open
        // §2/§9 H2): the source-text pin above can only prove the call site
        // wires this predicate in; it cannot prove the predicate's own
        // truth table. The (false, true) row is the one this design adds --
        // a same-source no-op reload never sets _documentRenderedForCurrentRequest
        // (no fresh DocumentRendered fires), but the document is already
        // loaded AND painted, so a >=250-heading reopen must NOT defer or
        // it parks forever.
        //
        // Gate finding F4 (2026-07-26): the predicate itself moved to
        // ApplicateDeferredHeadingUpdater (the policy owner) so both
        // consumer surfaces (viewer + edit-preview) share ONE implementation
        // instead of a byte-identical duplicate each. This single table
        // replaces the two that used to pin each surface's own copy --
        // collapsed per the gate's disposition, since both copies were
        // always identical and there is now only one owner to test.
        Assert.Equal(
            expectedDefer,
            ApplicateDeferredHeadingUpdater.ShouldDeferLargeTocHeadingUpdates(
                documentRenderedForCurrentRequest,
                hasLoadedAndPaintedDocument));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    public void HasHeadingDebtMatchesAllInputCombinations(
        bool hasViewModel,
        bool consumerHasHeadings,
        bool failureViewVisible,
        bool expectedDebt)
    {
        // P1 (design work-items/active/2026-07-25-toc-empty-on-open/
        // design.md §9.4/§9.5/§9.7 claim 8): replaces claim 1's probe, the
        // deleted G9 (ApplicateSharedWebViewHostRealHostTests), which never
        // constructed a failure view anywhere in its body and could not go
        // red on the failureViewVisible conjunct -- the same setup as G2,
        // exercising only the pull's own `if (!consumerHasHeadingDebt) return
        // false;` early return.
        //
        // Row (true, false, true) -> false is DEFECT-1: a stale TOC must NOT
        // refill beside a live failure view even though the consumer's own
        // collection is empty. Row (true, false, false) -> true is the only
        // row real debt exists. Dropping the consumerHasHeadings conjunct
        // turns row (true, true, false) red; dropping the failureViewVisible
        // conjunct turns row (true, false, true) red; dropping the
        // hasViewModel conjunct turns every all-false-input row red.
        Assert.Equal(
            expectedDebt,
            ApplicateDeferredHeadingUpdater.HasHeadingDebt(hasViewModel, consumerHasHeadings, failureViewVisible));
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
    public async Task TocPanelVirtualizedRowFactoryToleratesNullRecycleItem()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
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
        var requestScroll = ExtractMethodBody(tocPanel, "private void RequestActiveHeadingScroll(string? activeId, bool allowVirtualizedScroll)");
        var scrollRequestStart = refresh.IndexOf("RequestActiveHeadingScroll(activeId, allowVirtualizedScroll);", StringComparison.Ordinal);

        Assert.True(scrollRequestStart >= 0, "HighlightActiveHeading should request active-row scrolling after refreshing rows.");
        Assert.DoesNotContain("return;", refresh[..scrollRequestStart], StringComparison.Ordinal);
        Assert.Contains("if (allowVirtualizedScroll", requestScroll, StringComparison.Ordinal);
    }

    [Fact]
    public void TocPanelQueuesActiveHeadingScrollReplayUntilVisibleLayoutIsReady()
    {
        var tocPanel = ReadTocPanelCodeBehind();
        var propertyChanged = ExtractMethodBody(tocPanel, "private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)");
        var requestScroll = ExtractMethodBody(tocPanel, "private void RequestActiveHeadingScroll(string? activeId, bool allowVirtualizedScroll)");
        var armReplay = ExtractMethodBody(tocPanel, "private void ArmActiveHeadingScrollReplay(string activeId)");
        var clearReplay = ExtractMethodBody(tocPanel, "private void ClearActiveHeadingScrollReplay()");
        var detach = ExtractMethodBody(tocPanel, "private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)");

        Assert.Contains("nameof(MainWindowViewModel.IsTocVisible)", propertyChanged, StringComparison.Ordinal);
        Assert.Contains("RequestActiveHeadingScroll(_viewModel.ActiveHeadingId, allowVirtualizedScroll: true);", propertyChanged, StringComparison.Ordinal);
        Assert.Contains("!IsVisible", requestScroll, StringComparison.Ordinal);
        Assert.Contains("!_scroll.IsAttachedToVisualTree()", requestScroll, StringComparison.Ordinal);
        Assert.Contains("_scroll.Bounds.Height <= 0", requestScroll, StringComparison.Ordinal);
        Assert.Contains("!_rowIndexById.TryGetValue(activeId, out var index)", requestScroll, StringComparison.Ordinal);
        Assert.Contains("_scroll.LayoutUpdated += OnScrollLayoutUpdatedForActiveHeadingReplay;", armReplay, StringComparison.Ordinal);
        Assert.Contains("_scroll.LayoutUpdated -= OnScrollLayoutUpdatedForActiveHeadingReplay;", clearReplay, StringComparison.Ordinal);
        Assert.Contains("ClearActiveHeadingScrollReplay();", detach, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TocPanelClearsPreviouslyActiveMaterializedRowsAfterNewActiveRow()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
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

            var first = Assert.IsType<Button>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading("first", 1, "First", 0),
            ]));
            var second = Assert.IsType<Button>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading("second", 2, "Second", 10),
            ]));
            var third = Assert.IsType<Button>(buildHeadingRow?.Invoke(panel, [
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
    public async Task TocPanelRendersMathHeadingSegmentsWithMathView()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var panel = new ApplicateTocPanel();
            var buildHeadingRow = typeof(ApplicateTocPanel).GetMethod(
                "BuildHeadingRow",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var row = Assert.IsType<Button>(buildHeadingRow?.Invoke(panel, [
                new DocumentHeading(
                    "wave",
                    1,
                    "Wave Z_{0} ports",
                    0,
                    [
                        new DocumentHeadingInline(DocumentHeadingInlineKind.Text, "Wave "),
                        new DocumentHeadingInline(DocumentHeadingInlineKind.Math, "Z_{0}"),
                        new DocumentHeadingInline(DocumentHeadingInlineKind.Text, " ports"),
                    ]),
            ]));

            // BuildHeadingContent's only call site (ApplicateTocPanel.BuildHeadingRow)
            // adds this control to a Grid column and never casts it back to a
            // concrete type; every other assertion in this test reads only
            // Panel-base members (.Children). The contract here is "a Panel-
            // derived container for the text/math segments", not "specifically
            // a StackPanel" — Assert.IsType<Panel> was an exact-type check that
            // could never pass against the StackPanel BuildHeadingContent
            // actually returns (wrong from the same commit that added both,
            // 7bdaf75). Assert.IsAssignableFrom<Panel> asserts the real contract.
            var grid = Assert.IsType<Grid>(row.Content);
            var headingContent = Assert.IsAssignableFrom<Panel>(grid.Children[1]);
            Assert.Contains(headingContent.Children, child => child is TextBlock { Text: "Wave " });
            var mathView = Assert.Single(headingContent.Children.OfType<MathView>());
            // Assert.Equal("Z_{0}", ...) is wrong too, in the same never-reached
            // tail as the Panel-type assertion above: MathView.LaTeX is not a
            // passthrough of the string handed to the setter — CSharpMath parses
            // it into a MathList and re-serializes on read, and its canonical
            // writer drops braces around a SINGLE-atom subscript/superscript
            // group as redundant ("Z_{0}" -> "Z_0"). Verified in-target: a
            // multi-atom group round-trips WITH braces intact ("Z_{ab}" stays
            // "Z_{ab}"), so this is the library's documented-by-behavior
            // canonicalization of semantically-equivalent LaTeX, not data loss.
            // NormalizeTexForRenderer itself (MarkMello-owned) leaves "Z_{0}"
            // untouched — the divergence is entirely inside the third-party
            // control.
            Assert.Equal("Z_0", mathView.LaTeX);

            // row is now a Button (a TemplatedControl), not the old Border: its
            // "mm-toc-row" Template only resolves once the control is part of a
            // styled visual tree, so a bare Measure/Arrange on the unattached
            // control (which worked for Border, which draws its Child directly
            // with no template) leaves Content unlaid-out. Host it and force a
            // layout pass instead, same as ApplicateTocPanelKeyboardAccessibilityTests.
            var window = new Window { Content = row, Width = 300, Height = 40 };
            window.Show();
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            try
            {
                Assert.True(mathView.Bounds.Width > 0);
                Assert.True(mathView.Bounds.Height > 0);
            }
            finally
            {
                window.Close();
            }
            Assert.Contains(headingContent.Children, child => child is TextBlock { Text: " ports" });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TransactionGenerationContextInheritsToConsumerWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
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
