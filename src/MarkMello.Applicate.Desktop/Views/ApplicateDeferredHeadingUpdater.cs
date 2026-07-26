using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Views;

internal sealed class ApplicateDeferredHeadingUpdater
{
    private const int LargeHeadingUpdateThreshold = 250;
    private static readonly TimeSpan LargeHeadingFlushDelay = TimeSpan.FromMilliseconds(80);
    private int _version;
    private int _pendingVersion;
    private DocumentHeading[]? _pendingHeadings;
    private MainWindowViewModel? _pendingViewModel;
    private Func<bool>? _pendingCanApply;

    public void Invalidate()
    {
        _version = unchecked(_version + 1);
        ClearPending();
    }

    public void Apply(
        IReadOnlyList<DocumentHeading> headings,
        MainWindowViewModel viewModel,
        Func<bool> canApply,
        bool deferLargeUntilExplicitFlush = true)
    {
        ArgumentNullException.ThrowIfNull(headings);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(canApply);

        var version = unchecked(_version + 1);
        _version = version;

        if (!ShouldDefer(headings))
        {
            ClearPending();
            viewModel.UpdateDocumentHeadings(headings);
            return;
        }

        var snapshot = headings.ToArray();
        if (!deferLargeUntilExplicitFlush && headings.Count >= LargeHeadingUpdateThreshold)
        {
            ClearPending();
            ScheduleApply(version, snapshot, viewModel, canApply);
            return;
        }

        _pendingVersion = version;
        _pendingHeadings = snapshot;
        _pendingViewModel = viewModel;
        _pendingCanApply = canApply;
    }

    private static bool ShouldDefer(IReadOnlyList<DocumentHeading> headings)
        => headings.Count == 0 || headings.Count >= LargeHeadingUpdateThreshold;

    // Gate finding F4 (2026-07-26 architecture-reviewer, bf9d3be): this predicate
    // used to be duplicated byte-identically on both consumer surfaces
    // (ApplicateViewerView, ApplicateEditPreviewView) -- a C1 single-owner
    // violation caused by the design's own must-not-touch freeze on this file,
    // which mandated the predicate "on both consumer surfaces" while leaving no
    // seam here to host it. The freeze is relaxed for EXACTLY this one purpose;
    // everything else in this file (LargeHeadingUpdateThreshold, the pre-existing
    // 80 ms LargeHeadingFlushDelay / ScheduleApply timer, Apply/FlushPending)
    // stays untouched. This file already owns LargeHeadingUpdateThreshold and
    // ShouldDefer, so it is the natural single owner of this policy too.
    //
    // Part D (design work-items/active/2026-07-25-toc-empty-on-open/design.md
    // §2/§9 H2): a same-source no-op reload re-emits the retained heading list
    // synchronously (design REVISION 3 D1, ca045b4: the CONSUMER's own
    // ApplicateWebMarkdownDocumentView.TryRaiseRetainedHeadingsForConsumerDebt
    // pull, called immediately after its own RequestRender call -- not a host-
    // side re-emit; ApplicateSharedWebViewHost.RequestRender carries no
    // heading logic) with no fresh DocumentRendered to
    // ever flush a parked >=250-heading payload -- documentRenderedForCurrentRequest
    // stays false for the whole no-op reload (no new render, so the consumer's
    // "rendered" handler never fires), so the explicit-flush trigger never
    // arrives and the TOC would park forever. The second input recognises that
    // case: the document is already loaded AND painted (NOT specifically "for
    // the current source" -- gate finding F3 corrected that overstatement; see
    // each consumer's hasLoadedAndPaintedDocument computation), so there is
    // nothing left to wait for even though this specific request never
    // rendered anything. Pure predicate (repo idiom, mirrors
    // ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait) so all four input
    // combinations are synchronously unit-testable without a live shared host.
    internal static bool ShouldDeferLargeTocHeadingUpdates(
        bool documentRenderedForCurrentRequest,
        bool hasLoadedAndPaintedDocument)
        => !documentRenderedForCurrentRequest && !hasLoadedAndPaintedDocument;

    // R5 blocking finding 2 (design work-items/active/2026-07-25-toc-empty-on-
    // open/design.md §9.4): this consumer-side debt predicate used to be
    // inlined at both call sites (ApplicateViewerView.IssueRenderRequest,
    // ApplicateEditPreviewView.ApplyWebPreviewSource / RetryCurrentRender) --
    // a C1 single-owner violation, and the reason the removed G9 test could
    // never go red on the failure-view term: it lived in two files G9 never
    // constructed. This file already owns the sibling consumer-side heading
    // policy predicate for the same two views (ShouldDeferLargeTocHeadingUpdates
    // above), so it is the natural single owner of this one too -- not the
    // host's question (ApplicateWebMarkdownDocumentView), which only answers
    // "do I hold a payload valid for this source", not "does the caller have
    // debt at all".
    //
    // hasViewModel: false collapses the whole predicate to false regardless of
    // consumerHasHeadings, preserving the edit-preview surface's prior null
    // semantics (no bound ViewModel means no TOC surface to refill, so no
    // debt). failureViewVisible: true also forces false -- a deliberate
    // renderer-failure clear (E2) must never be mistaken for real debt (E1)
    // and pulled back beside a still-visible failure overlay (DEFECT-1).
    internal static bool HasHeadingDebt(
        bool hasViewModel,
        bool consumerHasHeadings,
        bool failureViewVisible)
        => hasViewModel && !consumerHasHeadings && !failureViewVisible;

    public void FlushPending()
    {
        var version = _pendingVersion;
        var snapshot = _pendingHeadings;
        var viewModel = _pendingViewModel;
        var canApply = _pendingCanApply;
        ClearPending();
        if (snapshot is null || viewModel is null || canApply is null)
        {
            return;
        }

        ScheduleApply(version, snapshot, viewModel, canApply);
    }

    private void ScheduleApply(
        int version,
        IReadOnlyList<DocumentHeading> snapshot,
        MainWindowViewModel viewModel,
        Func<bool> canApply)
    {
        _ = Task.Delay(LargeHeadingFlushDelay).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (_version != version || !canApply())
                {
                    return;
                }

                viewModel.UpdateDocumentHeadings(snapshot);
            }, DispatcherPriority.Background),
            TaskScheduler.Default);
    }

    private void ClearPending()
    {
        _pendingHeadings = null;
        _pendingViewModel = null;
        _pendingCanApply = null;
        _pendingVersion = 0;
    }
}
