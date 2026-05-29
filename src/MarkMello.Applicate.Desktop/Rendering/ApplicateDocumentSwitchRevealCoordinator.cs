using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Holds a solid theme-background cover over the document + TOC region during a
/// viewer DOCUMENT switch (tab change, startup, reload) so the user sees an
/// atomic reveal instead of the staged sequence "TOC collapses → blank pane →
/// content paints in chunks".
///
/// <para>Background: the app reuses a single WebView across every tab. On a
/// document switch the renderer clears and re-renders in place while the host
/// keeps the slot visible (the non-transactional path in
/// <see cref="ApplicateSharedWebViewHost"/>), and the Avalonia TOC collapses
/// because <c>DocumentHeadings</c> is renderer-sourced and empties until the
/// new headings arrive. The mode-toggle path already runs a covered/gated
/// reveal via <see cref="ApplicateSiblingMountBridge"/>; document switches did
/// not. This coordinator closes that asymmetry for the document-switch path
/// WITHOUT touching the host state machine or the mode-toggle reveal — it is
/// purely additive: show a cover, then drop it once the new document has
/// committed and painted.</para>
///
/// <para>Scope guard: it only acts on real <see cref="MainWindowViewModel.Document"/>
/// changes while the viewer is the active surface, and only hides on
/// non-transactional commits (<c>TransactionGeneration == 0</c>). Mode toggles
/// keep the same document, so they never trigger this coordinator, and their
/// transactional commits are ignored here — the bridge stays the sole owner of
/// the mode-toggle cover.</para>
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
    private readonly Func<bool> _isViewer;
    private readonly ApplicateModeRevealCoverWindow _cover = new();

    private MarkdownSource? _lastSource;
    private bool _covered;
    // Bumped per document-switch cover session so a stale reveal RAF chain from
    // a prior switch cannot hide the cover belonging to a newer switch.
    private long _coverGeneration;
    private bool _pendingShowOnBounds;
    private DispatcherTimer? _fallbackTimer;
    private bool _disposed;

    public ApplicateDocumentSwitchRevealCoordinator(
        Control coverHost,
        IApplicateSharedWebViewHost host,
        MainWindowViewModel viewModel,
        Func<bool> isViewer)
    {
        _coverHost = coverHost ?? throw new ArgumentNullException(nameof(coverHost));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _isViewer = isViewer ?? throw new ArgumentNullException(nameof(isViewer));

        _lastSource = viewModel.Document;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _host.CommitCompleted += OnCommitCompleted;
        _host.RendererFailed += OnRendererFailed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Leaving the active reader surface while covered (entering edit mode,
        // or no longer Viewing) must drop the cover immediately: the cover host
        // spans the content area that edit mode reuses, and the reader-mode
        // commit that would normally hide it never fires for the edit surface,
        // so it would otherwise obscure the editor until the 8s fallback.
        // (`IsViewer` is `State == Viewing`, which is ALSO true in edit mode —
        // edit is a sub-mode of Viewing — so `_isViewer()` already excludes
        // edit via `&& !IsEditMode` at the wiring site; this catches the case
        // where edit is entered AFTER the cover is already up.)
        if (e.PropertyName is nameof(MainWindowViewModel.IsEditMode)
            or nameof(MainWindowViewModel.IsViewer))
        {
            if (_covered && !_isViewer())
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

        // No document (welcome / closed), or not on the active reader surface
        // (edit mode) — nothing to reveal under a cover here.
        if (next is null || !_isViewer())
        {
            HideCover();
            return;
        }

        // New switch session — bump the generation so any in-flight reveal RAF
        // chain from a previous switch cannot hide THIS cover.
        _coverGeneration++;
        ShowCover();
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
        if (!_covered)
        {
            return;
        }

        // Only the viewer's own non-transactional document-switch commit
        // resolves this cover. Exclude: (a) transactional commits — the
        // mode-toggle bridge owns those; (b) non-viewer commits, e.g. the
        // off-screen edit-preview prime which ALSO uses generation 0 and would
        // otherwise hide the cover before the viewer's real commit.
        if (e.TransactionGeneration > 0 || e.Mode != ApplicateMode.Viewer)
        {
            return;
        }

        // The DOM has committed; wait two animation frames so the new content
        // has actually painted at the committed bounds before dropping the
        // cover, giving an atomic content+TOC reveal.
        HideCoverAfterPaint();
    }

    private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
    {
        // A failure routes to the failure view; do not keep the user behind a
        // blank cover.
        HideCover();
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
        HideCover();
    }

    private void HideCover()
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
        _cover.Hide();
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
        _host.CommitCompleted -= OnCommitCompleted;
        _host.RendererFailed -= OnRendererFailed;
        if (_pendingShowOnBounds)
        {
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        }
        ReleaseFallback();
        _cover.Dispose();
    }
}
