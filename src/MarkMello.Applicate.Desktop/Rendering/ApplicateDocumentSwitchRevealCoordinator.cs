using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Holds a solid theme-background cover over the active document region during
/// a DOCUMENT switch (tab change, startup, reload) so the user sees an atomic
/// document reveal instead of the staged sequence "blank pane → content paints
/// in chunks".
///
/// <para>Background: the app reuses a single WebView across every tab. On a
/// document switch the renderer clears and re-renders in place while the active
/// host keeps the slot visible (the non-transactional path in
/// <see cref="ApplicateSharedWebViewHost"/>). The Avalonia TOC keeps its
/// existing shell column and swaps its rows when new headings arrive; this
/// cover intentionally targets only the document/edit content slot. The
/// mode-toggle path already runs a covered/gated
/// reveal via <see cref="ApplicateSiblingMountBridge"/>; document switches did
/// not. This coordinator closes that asymmetry for the document-switch path
/// WITHOUT touching the host state machine or the mode-toggle reveal — it is
/// purely additive: show a cover, then drop it once the new document has
/// committed and painted.</para>
///
/// <para>Scope guard: each coordinator instance only acts on real
/// <see cref="MainWindowViewModel.Document"/> changes while its wired surface
/// is active, and only hides on that surface's non-transactional commits
/// (<c>TransactionGeneration == 0</c>). Mode toggles keep the same document,
/// so they never trigger this coordinator, and their transactional commits are
/// ignored here — the bridge stays the sole owner of the mode-toggle cover.</para>
/// </summary>
internal sealed class ApplicateDocumentSwitchRevealCoordinator : IDisposable
{
    // Safety net only. CommitCompleted / RendererFailed are the authoritative
    // hide signals; this floor guarantees the cover can never get stuck if a
    // commit is dropped. Comfortably longer than a heavy-document cold render
    // so it never reveals partial content on the normal path.
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(8);

    private readonly Control _coverHost;
    private readonly IApplicateSharedWebViewHost _host;
    private readonly MainWindowViewModel _viewModel;
    private readonly ApplicateMode _mode;
    private readonly Func<bool> _isActiveSurface;
    private readonly bool _clearHeadingsOnRendererFailure;
    private readonly ApplicateModeRevealCoverWindow _cover = new();

    private MarkdownSource? _lastSource;
    private bool _covered;
    // Bumped per document-switch cover session so a stale reveal RAF chain from
    // a prior switch cannot hide the cover belonging to a newer switch.
    private long _coverGeneration;
    private bool _pendingShowOnBounds;
    private bool _commitCompletedForCover;
    private bool _documentRevealReadyForCover;
    private DispatcherTimer? _fallbackTimer;
    private bool _disposed;

    public ApplicateDocumentSwitchRevealCoordinator(
        Control coverHost,
        IApplicateSharedWebViewHost host,
        MainWindowViewModel viewModel,
        ApplicateMode mode,
        Func<bool> isActiveSurface,
        bool clearHeadingsOnRendererFailure = true)
    {
        _coverHost = coverHost ?? throw new ArgumentNullException(nameof(coverHost));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _mode = mode;
        _isActiveSurface = isActiveSurface ?? throw new ArgumentNullException(nameof(isActiveSurface));
        _clearHeadingsOnRendererFailure = clearHeadingsOnRendererFailure;

        _lastSource = viewModel.Document;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DocumentTransitionStarting += OnDocumentTransitionStarting;
        _host.CommitCompleted += OnCommitCompleted;
        _host.RendererFailed += OnRendererFailed;
        _host.View.DocumentRevealReady += OnDocumentRevealReady;
    }

    // Cover-first (atomic teardown). The VM raises this immediately BEFORE it
    // mutates Document, so the cover is up + painted before the synchronous
    // WebView document swap. The
    // later PropertyChanged(Document) handler then sees _covered == true and
    // skips a redundant generation bump / re-show. Replaces the old behaviour
    // where the cover was raised only from PropertyChanged(Document), ~73 ms
    // after teardown had already started on screen.
    private void OnDocumentTransitionStarting(object? sender, EventArgs e)
    {
        if (_disposed || !_isActiveSurface())
        {
            return;
        }
        BeginCoverSession();
        ShowCover();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Leaving the active reader surface while covered (entering edit mode,
        // or no longer Viewing) must drop the cover immediately: the cover host
        // spans the content area that edit mode reuses, and the reader-mode
        // commit that would normally hide it never fires for the edit surface,
        // so it would otherwise obscure the editor until the 8s fallback.
        // (`IsViewer` is `State == Viewing`, which is ALSO true in edit mode —
        // edit is a sub-mode of Viewing — so the active-surface predicate at
        // the wiring site distinguishes reader vs. edit and this catches the
        // case where the user leaves that surface AFTER the cover is already up.)
        if (e.PropertyName is nameof(MainWindowViewModel.IsEditMode)
            or nameof(MainWindowViewModel.IsViewer))
        {
            if (_covered && !_isActiveSurface())
            {
                HideCover();
            }
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.Document))
        {
            return;
        }

        var next = _viewModel.Document;
        if (ReferenceEquals(next, _lastSource))
        {
            return;
        }
        _lastSource = next;

        // No document (welcome / closed), or not on this coordinator's active
        // surface — nothing to reveal under a cover here.
        if (next is null || !_isActiveSurface())
        {
            HideCover();
            return;
        }

        // New switch session. If DocumentTransitionStarting already raised the
        // cover for THIS switch (cover-first), it is already up at the current
        // generation — re-showing would hide+re-show the cover window (a visible
        // flicker) and double-bump the generation. Skip when already covered;
        // only fall through for a Document mutation that did NOT begin via the
        // transition signal (e.g. a cover that failed to show on a cold start
        // with no bounds yet).
        if (_covered)
        {
            return;
        }
        BeginCoverSession();
        ShowCover();
    }

    private void BeginCoverSession()
    {
        _coverGeneration++;
        _commitCompletedForCover = false;
        _documentRevealReadyForCover = false;
    }

    private void ShowCover()
    {
        if (_disposed)
        {
            return;
        }

        if (_cover.Show(_coverHost))
        {
            // No UpdateLayout() here: unlike the mode-toggle bridge we do not
            // reparent/reconcile under the cover, so forcing a synchronous
            // layout pass (which on the deferred LayoutUpdated path re-enters
            // the layout system) buys nothing. Show() already positioned the
            // cover from the host's current screen rect.
            _covered = true;
            _pendingShowOnBounds = false;
            RestartFallback();
            ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-shown");
            TryHideCoverAfterCommitAndRevealReady();
            return;
        }

        // Show fails before the host has measured bounds (cold startup: the
        // first document is set before contentGrid's first layout pass). Defer
        // until bounds arrive, then cover if the switch is still in flight.
        if (!_pendingShowOnBounds)
        {
            _pendingShowOnBounds = true;
            _coverHost.LayoutUpdated += OnCoverHostLayoutUpdated;
            ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-deferred", "reason=no-bounds");
        }
    }

    private void OnCoverHostLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed || !_pendingShowOnBounds)
        {
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
            return;
        }

        if (_coverHost.Bounds.Width <= 1 || _coverHost.Bounds.Height <= 1)
        {
            return;
        }

        _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        _pendingShowOnBounds = false;
        ShowCover();
    }

    private void OnCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)
    {
        if (!_covered && !_pendingShowOnBounds)
        {
            return;
        }

        // Only the viewer's own non-transactional document-switch commit
        // resolves this cover. Exclude: (a) transactional commits — the
        // mode-toggle bridge owns those; (b) non-viewer commits, e.g. the
        // off-screen edit-preview prime which ALSO uses generation 0 and would
        // otherwise hide the cover before the viewer's real commit.
        if (e.TransactionGeneration > 0 || e.Mode != _mode)
        {
            return;
        }

        _commitCompletedForCover = true;
        TryHideCoverAfterCommitAndRevealReady();
    }

    private void OnDocumentRevealReady(object? sender, EventArgs e)
    {
        if (!_covered && !_pendingShowOnBounds)
        {
            return;
        }

        _documentRevealReadyForCover = true;
        TryHideCoverAfterCommitAndRevealReady();
    }

    private void TryHideCoverAfterCommitAndRevealReady()
    {
        if (!_covered || !_commitCompletedForCover || !_documentRevealReadyForCover)
        {
            return;
        }

        // The DOM has committed and the renderer has finished any post-ready
        // Mermaid/hljs preparation for this document; wait two animation frames
        // so the new content has actually painted at the committed bounds before
        // dropping the cover, giving an atomic content+TOC reveal.
        HideCoverAfterPaint();
    }

    private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
    {
        // A failure routes to the failure view; do not keep the user behind a
        // blank cover.
        HideCover();

        if (!_clearHeadingsOnRendererFailure)
        {
            return;
        }

        // R1 (atomic-transition safety net): with the eager OnDocumentChanged TOC
        // clear removed (C3), the old document's headings would otherwise persist
        // beside the failure view when the renderer fails mid-render WITHOUT a
        // Document/State change (e.g. a WebView crash — the source loaded fine, so
        // Document stays non-null and OnDocumentChanged never clears). A genuine
        // document LOAD failure nulls Document via ApplyLoadError, which the
        // OnDocumentChanged null-clear handles; this covers the render-time crash
        // the load-error path does not. Empty list = clear via the public API.
        _viewModel.UpdateDocumentHeadings(System.Array.Empty<DocumentHeading>());
    }

    private void HideCoverAfterPaint()
    {
        if (_disposed || !_covered)
        {
            return;
        }

        // Snapshot the cover session. A rapid second switch bumps
        // _coverGeneration and shows a fresh cover; this (now stale) RAF chain
        // must NOT hide that newer cover.
        var generation = _coverGeneration;
        var topLevel = TopLevel.GetTopLevel(_coverHost);
        if (topLevel is null)
        {
            Dispatcher.UIThread.Post(() => HideCoverForGeneration(generation), DispatcherPriority.Background);
            return;
        }

        topLevel.RequestAnimationFrame(_ =>
        {
            if (_disposed || generation != _coverGeneration)
            {
                return;
            }
            topLevel.RequestAnimationFrame(_ => HideCoverForGeneration(generation));
        });
    }

    private void HideCoverForGeneration(long generation)
    {
        if (_disposed || generation != _coverGeneration)
        {
            return;
        }
        HideCover(animated: true);
    }

    private void HideCover(bool animated = false)
    {
        ReleaseFallback();
        if (_pendingShowOnBounds)
        {
            _pendingShowOnBounds = false;
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        }
        if (!_covered)
        {
            return;
        }

        _covered = false;
        _commitCompletedForCover = false;
        _documentRevealReadyForCover = false;
        var duration = animated
            ? ApplicateMotion.ModeSwitchDuration(_viewModel.ReadingPreferences)
            : TimeSpan.Zero;
        _cover.Hide(duration);
        ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-hidden");
    }

    private void RestartFallback()
    {
        ReleaseFallback();
        _fallbackTimer = new DispatcherTimer { Interval = FallbackTimeout };
        _fallbackTimer.Tick += OnFallbackTick;
        _fallbackTimer.Start();
    }

    private void OnFallbackTick(object? sender, EventArgs e)
    {
        ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-fallback");
        HideCover();
    }

    private void ReleaseFallback()
    {
        if (_fallbackTimer is null)
        {
            return;
        }

        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= OnFallbackTick;
        _fallbackTimer = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.DocumentTransitionStarting -= OnDocumentTransitionStarting;
        _host.CommitCompleted -= OnCommitCompleted;
        _host.RendererFailed -= OnRendererFailed;
        _host.View.DocumentRevealReady -= OnDocumentRevealReady;
        if (_pendingShowOnBounds)
        {
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        }
        ReleaseFallback();
        _cover.Dispose();
    }
}
