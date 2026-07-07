using System;
using System.Collections.Generic;
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
/// Single airspace-transition policy owner for Applicate's WebView-backed
/// surfaces. Stage A owns only DOCUMENT reveal sessions; startup, theme, and
/// mode sessions remain on their existing owners until their cutover slices.
/// </summary>
internal sealed class ApplicateAirspaceCompositor : IDisposable
{
    private readonly Control _coverHost;
    private readonly IApplicateDocumentRevealState _documentState;
    private readonly Func<IApplicateAirspaceCoverPresenter> _coverFactory;
    private readonly IApplicateAirspacePaintGate _paintGate;
    private readonly List<DocumentRevealSession> _documentSessions = [];
    private bool _disposed;

    public ApplicateAirspaceCompositor(Control coverHost, MainWindowViewModel viewModel)
        : this(
            coverHost,
            new MainWindowDocumentRevealState(viewModel),
            static () => new ModeRevealCoverPresenter(new ApplicateModeRevealCoverWindow()),
            new AvaloniaTwoFramePaintGate())
    {
    }

    internal ApplicateAirspaceCompositor(
        Control coverHost,
        IApplicateDocumentRevealState documentState,
        Func<IApplicateAirspaceCoverPresenter> coverFactory,
        IApplicateAirspacePaintGate paintGate)
    {
        _coverHost = coverHost ?? throw new ArgumentNullException(nameof(coverHost));
        _documentState = documentState ?? throw new ArgumentNullException(nameof(documentState));
        _coverFactory = coverFactory ?? throw new ArgumentNullException(nameof(coverFactory));
        _paintGate = paintGate ?? throw new ArgumentNullException(nameof(paintGate));
    }

    public IDisposable RegisterDocumentSession(
        IApplicateSharedWebViewHost host,
        ApplicateMode mode,
        Func<bool> isActiveSurface,
        bool clearHeadingsOnRendererFailure = true,
        bool skipInitialCoverSession = false,
        bool suppressSamePathReloadCover = false)
    {
        ArgumentNullException.ThrowIfNull(host);

        return RegisterDocumentSession(
            new SharedWebViewDocumentRevealSignals(host),
            mode,
            isActiveSurface,
            clearHeadingsOnRendererFailure,
            skipInitialCoverSession,
            suppressSamePathReloadCover);
    }

    internal IDisposable RegisterDocumentSession(
        IApplicateDocumentRevealSignals signals,
        ApplicateMode mode,
        Func<bool> isActiveSurface,
        bool clearHeadingsOnRendererFailure = true,
        bool skipInitialCoverSession = false,
        bool suppressSamePathReloadCover = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(isActiveSurface);

        var session = new DocumentRevealSession(
            _coverHost,
            signals,
            _documentState,
            _coverFactory(),
            _paintGate,
            mode,
            isActiveSurface,
            clearHeadingsOnRendererFailure,
            skipInitialCoverSession,
            suppressSamePathReloadCover);
        _documentSessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var session in _documentSessions)
        {
            session.Dispose();
        }
        _documentSessions.Clear();
    }

    private sealed class DocumentRevealSession : IDisposable
    {
        // Safety net only. CommitCompleted / RendererFailed are the
        // authoritative hide signals; this floor guarantees the cover can never
        // get stuck if a commit is dropped. Comfortably longer than a heavy-
        // document cold render so it never reveals partial content on the
        // normal path.
        private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(8);

        private readonly Control _coverHost;
        private readonly IApplicateDocumentRevealSignals _signals;
        private readonly IApplicateDocumentRevealState _documentState;
        private readonly IApplicateAirspaceCoverPresenter _cover;
        private readonly IApplicateAirspacePaintGate _paintGate;
        private readonly ApplicateMode _mode;
        private readonly Func<bool> _isActiveSurface;
        private readonly bool _clearHeadingsOnRendererFailure;
        // True for surfaces that update document content in place (the edit
        // surface: editor + live preview) instead of through a covered atomic
        // WebView reveal. On such a surface a same-path reload (F5 / Ctrl+S)
        // produces no re-render to resolve the cover, so the session must not
        // raise one for it.
        private readonly bool _suppressSamePathReloadCover;

        private MarkdownSource? _lastSource;
        private bool _covered;
        // Bumped per document-switch cover session so a stale reveal RAF chain
        // from a prior switch cannot hide the cover belonging to a newer switch.
        private long _coverGeneration;
        private bool _pendingShowOnBounds;
        private bool _commitCompletedForCover;
        private bool _documentRevealReadyForCover;
        private bool _skipNextCoverSession;
        private bool _skipNextDocumentChangeCover;
        private DispatcherTimer? _fallbackTimer;
        private bool _disposed;

        public DocumentRevealSession(
            Control coverHost,
            IApplicateDocumentRevealSignals signals,
            IApplicateDocumentRevealState documentState,
            IApplicateAirspaceCoverPresenter cover,
            IApplicateAirspacePaintGate paintGate,
            ApplicateMode mode,
            Func<bool> isActiveSurface,
            bool clearHeadingsOnRendererFailure,
            bool skipInitialCoverSession,
            bool suppressSamePathReloadCover)
        {
            _coverHost = coverHost;
            _signals = signals;
            _documentState = documentState;
            _cover = cover;
            _paintGate = paintGate;
            _mode = mode;
            _isActiveSurface = isActiveSurface;
            _clearHeadingsOnRendererFailure = clearHeadingsOnRendererFailure;
            _suppressSamePathReloadCover = suppressSamePathReloadCover;
            _skipNextCoverSession = skipInitialCoverSession;

            _lastSource = documentState.Document;
            _documentState.PropertyChanged += OnDocumentStatePropertyChanged;
            _documentState.DocumentTransitionStarting += OnDocumentTransitionStarting;
            _documentState.SuppressNextDocumentReveal += OnSuppressNextDocumentReveal;
            _signals.CommitCompleted += OnCommitCompleted;
            _signals.RendererFailed += OnRendererFailed;
            _signals.DocumentRevealReady += OnDocumentRevealReady;
        }

        // Cover-first (atomic teardown). The VM raises this immediately BEFORE
        // it mutates Document, so the cover is up + painted before the
        // synchronous WebView document swap. The later PropertyChanged(Document)
        // handler then sees _covered == true and skips a redundant generation
        // bump / re-show.
        private void OnSuppressNextDocumentReveal(object? sender, EventArgs e)
        {
            // The next document change is a same-path content update (the
            // health fix's reload); reuse the existing skip mechanism so
            // neither the cover-first transition nor the Document-change branch
            // raises a cover.
            _skipNextCoverSession = true;
        }

        private void OnDocumentTransitionStarting(object? sender, EventArgs e)
        {
            if (_disposed || !_isActiveSurface())
            {
                return;
            }

            // In-place-update surface (edit): cover-first cannot tell a same-
            // path reload from a real switch (the new path is unknown until
            // Document is assigned). Defer the whole cover decision to the
            // path-aware Document-change branch below, which skips the same-
            // path case and still covers a real switch.
            if (_suppressSamePathReloadCover)
            {
                return;
            }

            if (_skipNextCoverSession)
            {
                _skipNextCoverSession = false;
                _skipNextDocumentChangeCover = true;
                ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-skipped", "reason=startup-full-cover");
                return;
            }

            BeginCoverSession();
            ShowCover();
        }

        private void OnDocumentStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Leaving the active reader surface while covered (entering edit
            // mode, or no longer Viewing) must drop the cover immediately: the
            // cover host spans the content area that edit mode reuses, and the
            // reader-mode commit that would normally hide it never fires for the
            // edit surface.
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

            var next = _documentState.Document;
            if (ReferenceEquals(next, _lastSource))
            {
                return;
            }
            var previous = _lastSource;
            _lastSource = next;

            // No document (welcome / closed), or not on this session's active
            // surface — nothing to reveal under a cover here.
            if (next is null || !_isActiveSurface())
            {
                HideCover();
                return;
            }

            // Same-path reload on an in-place-update surface (edit). F5 reload
            // and Ctrl+S save re-assign a value-fresh, same-path MarkdownSource;
            // the editor + live preview update in place with NO covered WebView
            // re-render, so a cover would never get its commit + reveal-ready
            // pair and would sit on the full 8s fallback.
            if (_suppressSamePathReloadCover
                && previous is not null
                && string.Equals(next.Path, previous.Path, StringComparison.OrdinalIgnoreCase))
            {
                ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-skipped", "reason=same-path-edit-reload");
                return;
            }

            if (_skipNextCoverSession || _skipNextDocumentChangeCover)
            {
                _skipNextCoverSession = false;
                _skipNextDocumentChangeCover = false;
                ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-skipped", "reason=startup-full-cover");
                return;
            }

            // If DocumentTransitionStarting already raised the cover for this
            // switch, re-showing would hide+re-show the cover window and double-
            // bump the generation. Skip when already covered.
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
                // No extra synchronous layout call here: the cover presenter
                // retains the existing cover-window layout behaviour.
                _covered = true;
                _pendingShowOnBounds = false;
                RestartFallback();
                ApplicateTrace.DiagMs("pane-seq", "doc-switch-cover-shown");
                TryHideCoverAfterCommitAndRevealReady();
                return;
            }

            // Show fails before the host has measured bounds (cold startup: the
            // first document is set before contentGrid's first layout pass).
            // Defer until bounds arrive, then cover if the switch is still in
            // flight.
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

            // Only this surface's non-transactional document-switch commit
            // resolves this cover. Exclude transactional commits and
            // non-matching modes.
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

            // The DOM has committed and the renderer has finished post-ready
            // preparation for this document; wait two Avalonia frames so the new
            // content has actually painted before dropping the cover.
            HideCoverAfterPaint();
        }

        private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
        {
            // A failure routes to the failure view; do not keep the user behind
            // a blank cover.
            HideCover();

            if (!_clearHeadingsOnRendererFailure)
            {
                return;
            }

            // Reader surface only: clear stale headings beside the failure view
            // when the renderer fails mid-render without a Document/State change.
            _documentState.ClearDocumentHeadings();
        }

        private void HideCoverAfterPaint()
        {
            if (_disposed || !_covered)
            {
                return;
            }

            var generation = _coverGeneration;
            _paintGate.AfterTwoFrames(
                _coverHost,
                () => HideCoverForGeneration(generation));
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
                ? ApplicateMotion.ModeSwitchDuration(_documentState.ReadingPreferences)
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

            _documentState.PropertyChanged -= OnDocumentStatePropertyChanged;
            _documentState.DocumentTransitionStarting -= OnDocumentTransitionStarting;
            _documentState.SuppressNextDocumentReveal -= OnSuppressNextDocumentReveal;
            _signals.CommitCompleted -= OnCommitCompleted;
            _signals.RendererFailed -= OnRendererFailed;
            _signals.DocumentRevealReady -= OnDocumentRevealReady;
            if (_pendingShowOnBounds)
            {
                _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
            }
            ReleaseFallback();
            _cover.Dispose();
        }
    }

    private sealed class MainWindowDocumentRevealState(MainWindowViewModel viewModel) : IApplicateDocumentRevealState
    {
        private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _viewModel.PropertyChanged += value;
            remove => _viewModel.PropertyChanged -= value;
        }

        public event EventHandler? DocumentTransitionStarting
        {
            add => _viewModel.DocumentTransitionStarting += value;
            remove => _viewModel.DocumentTransitionStarting -= value;
        }

        public event EventHandler? SuppressNextDocumentReveal
        {
            add => _viewModel.SuppressNextDocumentReveal += value;
            remove => _viewModel.SuppressNextDocumentReveal -= value;
        }

        public MarkdownSource? Document => _viewModel.Document;

        public ReadingPreferences ReadingPreferences => _viewModel.ReadingPreferences;

        public void ClearDocumentHeadings()
            => _viewModel.UpdateDocumentHeadings(Array.Empty<DocumentHeading>());
    }

    private sealed class SharedWebViewDocumentRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateDocumentRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted
        {
            add => _host.CommitCompleted += value;
            remove => _host.CommitCompleted -= value;
        }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler? DocumentRevealReady
        {
            add => _host.View.DocumentRevealReady += value;
            remove => _host.View.DocumentRevealReady -= value;
        }
    }

    private sealed class ModeRevealCoverPresenter(ApplicateModeRevealCoverWindow cover)
        : IApplicateAirspaceCoverPresenter
    {
        private readonly ApplicateModeRevealCoverWindow _cover = cover ?? throw new ArgumentNullException(nameof(cover));

        public bool Show(Control host)
            => _cover.Show(host);

        public void Hide()
            => _cover.Hide();

        public void Hide(TimeSpan duration)
            => _cover.Hide(duration);

        public void Dispose()
            => _cover.Dispose();
    }

    private sealed class AvaloniaTwoFramePaintGate : IApplicateAirspacePaintGate
    {
        public void AfterTwoFrames(Control anchor, Action action)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentNullException.ThrowIfNull(action);

            var topLevel = TopLevel.GetTopLevel(anchor);
            if (topLevel is null)
            {
                Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
                return;
            }

            topLevel.RequestAnimationFrame(_ =>
            {
                topLevel.RequestAnimationFrame(_ => action());
            });
        }
    }
}

internal interface IApplicateDocumentRevealState : INotifyPropertyChanged
{
    event EventHandler? DocumentTransitionStarting;

    event EventHandler? SuppressNextDocumentReveal;

    MarkdownSource? Document { get; }

    ReadingPreferences ReadingPreferences { get; }

    void ClearDocumentHeadings();
}

internal interface IApplicateDocumentRevealSignals
{
    event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler? DocumentRevealReady;
}

internal interface IApplicateAirspaceCoverPresenter : IDisposable
{
    bool Show(Control host);

    void Hide();

    void Hide(TimeSpan duration);
}

internal interface IApplicateAirspacePaintGate
{
    void AfterTwoFrames(Control anchor, Action action);
}
