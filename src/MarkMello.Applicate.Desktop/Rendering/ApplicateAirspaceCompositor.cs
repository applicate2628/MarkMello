using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.Services;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Single airspace-transition policy owner for Applicate's WebView-backed
/// surfaces. Stage A owns DOCUMENT, STARTUP, and THEME reveal sessions; mode
/// sessions remain on their existing owner until their cutover slice.
/// </summary>
internal sealed class ApplicateAirspaceCompositor : IDisposable
{
    private readonly Control _coverHost;
    private readonly IApplicateDocumentRevealState _documentState;
    private readonly Func<IApplicateAirspaceCoverPresenter> _coverFactory;
    private readonly IApplicateAirspacePaintGate _paintGate;
    private readonly IApplicateAirspaceScheduler _scheduler;
    private readonly List<IDisposable> _startupSessions = [];
    private readonly List<DocumentRevealSession> _documentSessions = [];
    private readonly List<IDisposable> _themeSessions = [];
    private bool _disposed;

    public ApplicateAirspaceCompositor(Control coverHost, MainWindowViewModel viewModel)
        : this(
            coverHost,
            new MainWindowDocumentRevealState(viewModel),
            static () => new ModeRevealCoverPresenter(new ApplicateModeRevealCoverWindow()),
            new AvaloniaTwoFramePaintGate(),
            new DispatcherAirspaceScheduler())
    {
    }

    internal ApplicateAirspaceCompositor(
        Control coverHost,
        IApplicateDocumentRevealState documentState,
        Func<IApplicateAirspaceCoverPresenter> coverFactory,
        IApplicateAirspacePaintGate paintGate,
        IApplicateAirspaceScheduler? scheduler = null)
    {
        _coverHost = coverHost ?? throw new ArgumentNullException(nameof(coverHost));
        _documentState = documentState ?? throw new ArgumentNullException(nameof(documentState));
        _coverFactory = coverFactory ?? throw new ArgumentNullException(nameof(coverFactory));
        _paintGate = paintGate ?? throw new ArgumentNullException(nameof(paintGate));
        _scheduler = scheduler ?? new DispatcherAirspaceScheduler();
    }

    public IDisposable RegisterStartupSession(
        Window window,
        IApplicateSharedWebViewHost? host,
        MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);

        return RegisterStartupSession(
            new WindowStartupRevealShell(window),
            host is null ? null : new SharedWebViewStartupRevealSignals(host),
            new MainWindowStartupRevealState(viewModel));
    }

    internal IDisposable RegisterStartupSession(
        IApplicateStartupRevealShell shell,
        IApplicateStartupRevealSignals? signals,
        IApplicateStartupRevealState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(state);

        if (signals is null)
        {
            shell.Opacity = 1;
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-reveal-released",
                "reason=no-viewer-host");
            return EmptyDisposable.Instance;
        }

        var session = new StartupRevealSession(
            shell,
            signals,
            state,
            _coverFactory(),
            _paintGate,
            _scheduler);
        _startupSessions.Add(session);
        return session;
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

    public IDisposable RegisterThemeSession(
        IApplicateSharedWebViewHost host,
        Func<bool> isActiveSurface)
    {
        ArgumentNullException.ThrowIfNull(host);

        return RegisterThemeSession(
            new SharedWebViewThemeRevealSignals(host),
            isActiveSurface);
    }

    internal IDisposable RegisterThemeSession(
        IApplicateThemeRevealSignals signals,
        Func<bool> isActiveSurface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(isActiveSurface);

        var session = new ThemeRevealSession(
            _coverHost,
            signals,
            _documentState,
            _coverFactory(),
            _paintGate,
            _scheduler,
            isActiveSurface);
        _themeSessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var session in _startupSessions)
        {
            session.Dispose();
        }
        _startupSessions.Clear();

        foreach (var session in _documentSessions)
        {
            session.Dispose();
        }
        _documentSessions.Clear();

        foreach (var session in _themeSessions)
        {
            session.Dispose();
        }
        _themeSessions.Clear();
    }

    private sealed class StartupRevealSession : IDisposable
    {
        private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PaintReleaseFallbackTimeout = TimeSpan.FromMilliseconds(250);

        private readonly IApplicateStartupRevealShell _shell;
        private readonly IApplicateStartupRevealSignals _signals;
        private readonly IApplicateStartupRevealState _state;
        private readonly IApplicateAirspaceCoverPresenter _cover;
        private readonly IApplicateAirspacePaintGate _paintGate;
        private readonly IApplicateAirspaceScheduler _scheduler;
        private readonly IApplicateAirspaceTimer _fallbackTimer;
        private readonly IApplicateAirspaceTimer _rendererSettleFallbackTimer;

        private bool _released;
        private bool _startupWindowOpened;
        private bool _documentRevealReady;
        private bool _waitForHeadings;
        private bool _headingsReady;
        private bool _rendererSettleArmed;
        private bool _rendererSettleReady;
        private string _rendererSettleReleaseReason = string.Empty;
        private bool _disposed;

        public StartupRevealSession(
            IApplicateStartupRevealShell shell,
            IApplicateStartupRevealSignals signals,
            IApplicateStartupRevealState state,
            IApplicateAirspaceCoverPresenter cover,
            IApplicateAirspacePaintGate paintGate,
            IApplicateAirspaceScheduler scheduler)
        {
            _shell = shell;
            _signals = signals;
            _state = state;
            _cover = cover;
            _paintGate = paintGate;
            _scheduler = scheduler;
            _waitForHeadings = state.IsTocPreferredVisible;
            _headingsReady = !_waitForHeadings || state.HasDocumentHeadings;
            _fallbackTimer = scheduler.CreateTimer(FallbackTimeout, OnFallbackTick);
            _rendererSettleFallbackTimer = scheduler.CreateTimer(
                ApplicateSharedWebViewHost.RendererSettleFallbackTimeout,
                OnRendererSettleFallbackTick);

            _shell.Opened += OnStartupWindowOpened;
            _shell.SizeChanged += OnStartupWindowSizeChanged;
            _shell.Closed += OnClosed;
            _signals.DocumentRevealReady += OnDocumentRevealReady;
            _signals.HeadingsChanged += OnHeadingsChanged;
            _signals.RendererFailed += OnRendererFailed;
            _state.PropertyChanged += OnStatePropertyChanged;
            _fallbackTimer.Start();
        }

        private void OnStartupWindowOpened(object? sender, EventArgs e)
        {
            _startupWindowOpened = true;
            QueueStartupCover("opened");
            TryRelease("window-opened");
        }

        private void OnStartupWindowSizeChanged(object? sender, EventArgs e)
        {
            if (!_startupWindowOpened)
            {
                return;
            }

            QueueStartupCover("size-changed");
        }

        private void QueueStartupCover(string reason)
            => _scheduler.Post(
                () =>
                {
                    if (_released)
                    {
                        return;
                    }

                    var shown = _cover.ShowStartupSplash(_shell.CoverHost, _state.Document?.FileName);
                    ApplicateTrace.DiagMs(
                        "startup-applicate-window",
                        "startup-window-cover-shown",
                        $"reason={reason} shown={shown}");
                },
                DispatcherPriority.Render);

        private void OnDocumentRevealReady(object? sender, EventArgs e)
            => _scheduler.Post(
                () =>
                {
                    _documentRevealReady = true;
                    TryRelease("document-reveal-ready");
                },
                DispatcherPriority.Render);

        private void OnHeadingsChanged(object? sender, IReadOnlyList<DocumentHeading> headings)
            => _scheduler.Post(
                () =>
                {
                    _waitForHeadings = _state.IsTocPreferredVisible && headings.Count > 0;
                    _headingsReady = !_waitForHeadings || headings.Count > 0;
                    TryRelease("headings-reported");
                },
                DispatcherPriority.Background);

        private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not nameof(MainWindowViewModel.DocumentHeadings)
                and not nameof(MainWindowViewModel.IsTocVisible)
                and not nameof(MainWindowViewModel.HasDocumentHeadings))
            {
                return;
            }

            if (!_waitForHeadings)
            {
                return;
            }

            _headingsReady = _state.HasDocumentHeadings || !_state.IsTocPreferredVisible;
            TryRelease("headings-applied");
        }

        private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
            => _scheduler.Post(
                () => Release("renderer-failed"),
                DispatcherPriority.Render);

        private void OnFallbackTick(object? sender, EventArgs e)
            => Release("fallback");

        private void TryRelease(string reason)
        {
            if (!_startupWindowOpened
                || !_documentRevealReady
                || (_waitForHeadings && !_headingsReady))
            {
                return;
            }

            if (ShouldWaitForRendererSettle())
            {
                ArmRendererSettle(reason);
                return;
            }

            ReleaseAfterPaint(reason);
        }

        private bool ShouldWaitForRendererSettle()
            => !_rendererSettleReady
               && ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(
                   _state.Document,
                   transactionGeneration: 0);

        private void ArmRendererSettle(string reason)
        {
            if (_rendererSettleArmed)
            {
                return;
            }

            _rendererSettleArmed = true;
            _rendererSettleReleaseReason = reason;
            _signals.RendererSettled += OnRendererSettled;
            _rendererSettleFallbackTimer.Start();
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-renderer-settle-armed",
                $"reason={reason}");
            _signals.RequestRendererSettleProbe();
        }

        private void OnRendererSettled(object? sender, EventArgs e)
            => _scheduler.Post(
                () => CompleteRendererSettle("ipc-ack"),
                DispatcherPriority.Render);

        private void OnRendererSettleFallbackTick(object? sender, EventArgs e)
            => CompleteRendererSettle("fallback-timer");

        private void CompleteRendererSettle(string path)
        {
            if (_released || !_rendererSettleArmed)
            {
                return;
            }

            _rendererSettleReady = true;
            ReleaseRendererSettleWait();
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-renderer-settle-complete",
                $"path={path}");
            var reason = string.IsNullOrWhiteSpace(_rendererSettleReleaseReason)
                ? path
                : _rendererSettleReleaseReason + "-" + path;
            ReleaseAfterPaint(reason);
        }

        private void ReleaseRendererSettleWait()
        {
            if (_rendererSettleArmed)
            {
                _signals.RendererSettled -= OnRendererSettled;
            }

            _rendererSettleArmed = false;
            _rendererSettleFallbackTimer.Stop();
        }

        private void Release(string reason)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Cleanup();
            _shell.Opacity = 1;
            _scheduler.Post(
                () => HideStartupCover(reason),
                DispatcherPriority.Render);
        }

        private void ReleaseAfterPaint(string reason)
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Cleanup();
            _shell.Opacity = 1;

            var hidden = false;
            IApplicateAirspaceTimer? fallbackTimer = null;
            void HideOnce(string releaseReason)
            {
                if (hidden)
                {
                    return;
                }

                hidden = true;
                fallbackTimer?.Stop();
                fallbackTimer?.Dispose();
                HideStartupCover(releaseReason);
            }

            void OnReleaseFallbackTick(object? sender, EventArgs e)
                => HideOnce(reason + "-fallback");

            fallbackTimer = _scheduler.CreateTimer(PaintReleaseFallbackTimeout, OnReleaseFallbackTick);
            fallbackTimer.Start();

            _paintGate.AfterTwoFrames(
                _shell.CoverHost,
                () => _scheduler.Post(
                    () => HideOnce(reason),
                    DispatcherPriority.Render));
        }

        private void HideStartupCover(string reason)
        {
            // Perf B1 (audit 2026-06-04): the startup cover is already paint-gated
            // (DocumentRevealReady + double-RAF before HideStartupCover fires), so
            // the fade-out is dead cosmetic time on every startup, not a mask over
            // unpainted first paint. Zero it for the startup reveal. Scoped to THIS
            // call site only - ApplicateMotion.ModeSwitchDuration still drives the
            // in-session mode-toggle and tab/document-switch covers.
            var duration = TimeSpan.Zero;
            _cover.Hide(duration);
            ApplicateTrace.DiagMs(
                "startup-applicate-window",
                "startup-window-reveal-released",
                $"reason={reason} durationMs={duration.TotalMilliseconds:F0}");
        }

        private void OnClosed(object? sender, EventArgs e)
            => Dispose();

        private void Cleanup()
        {
            _fallbackTimer.Stop();
            ReleaseRendererSettleWait();
            _rendererSettleFallbackTimer.Stop();
            _shell.Opened -= OnStartupWindowOpened;
            _shell.SizeChanged -= OnStartupWindowSizeChanged;
            _shell.Closed -= OnClosed;
            _signals.DocumentRevealReady -= OnDocumentRevealReady;
            _signals.HeadingsChanged -= OnHeadingsChanged;
            _signals.RendererFailed -= OnRendererFailed;
            _state.PropertyChanged -= OnStatePropertyChanged;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            Cleanup();
            _fallbackTimer.Dispose();
            _rendererSettleFallbackTimer.Dispose();
            _cover.Dispose();
        }
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

    private sealed class ThemeRevealSession : IDisposable
    {
        private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(2);

        private readonly Control _coverHost;
        private readonly IApplicateThemeRevealSignals _signals;
        private readonly IApplicateDocumentRevealState _documentState;
        private readonly IApplicateAirspaceCoverPresenter _cover;
        private readonly IApplicateAirspacePaintGate _paintGate;
        private readonly IApplicateAirspaceScheduler _scheduler;
        private readonly Func<bool> _isActiveSurface;

        private string? _targetTheme;
        private ThemeVariant? _targetThemeVariant;
        private long _targetRequestId;
        private long _coverGeneration;
        private bool _covered;
        private bool _pendingShowOnBounds;
        private IApplicateAirspaceTimer? _fallbackTimer;
        private bool _disposed;

        public ThemeRevealSession(
            Control coverHost,
            IApplicateThemeRevealSignals signals,
            IApplicateDocumentRevealState documentState,
            IApplicateAirspaceCoverPresenter cover,
            IApplicateAirspacePaintGate paintGate,
            IApplicateAirspaceScheduler scheduler,
            Func<bool> isActiveSurface)
        {
            _coverHost = coverHost;
            _signals = signals;
            _documentState = documentState;
            _cover = cover;
            _paintGate = paintGate;
            _scheduler = scheduler;
            _isActiveSurface = isActiveSurface;

            _documentState.PropertyChanged += OnDocumentStatePropertyChanged;
            _documentState.ThemeTransitionStarting += OnThemeTransitionStarting;
            _signals.RendererFailed += OnRendererFailed;
            _signals.ThemeChangeSent += OnThemeChangeSent;
            _signals.ThemeApplied += OnThemeApplied;
        }

        private void OnThemeTransitionStarting(object? sender, ThemeTransitionStartingEventArgs e)
        {
            if (_disposed
                || _documentState.Document is null
                || !_isActiveSurface()
                || !_signals.HasLoadedDocumentForSource(_documentState.Document))
            {
                return;
            }

            BeginCoverSession(ToRendererTheme(e.TargetEffectiveTheme), ToThemeVariant(e.TargetEffectiveTheme));
            ShowCover();
        }

        private void OnDocumentStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_covered && !_pendingShowOnBounds)
            {
                return;
            }

            if (!_isActiveSurface())
            {
                HideCover();
            }
        }

        private void OnThemeChangeSent(object? sender, ApplicateWebThemeChangeSentEventArgs e)
        {
            if ((_covered || _pendingShowOnBounds)
                && string.Equals(e.Theme, _targetTheme, StringComparison.Ordinal))
            {
                _targetRequestId = e.RequestId;
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "theme-cover-awaiting-renderer",
                    $"theme={e.Theme} requestId={e.RequestId}");
            }
        }

        private void OnThemeApplied(object? sender, ApplicateWebThemeAppliedEventArgs e)
        {
            if (!_covered && !_pendingShowOnBounds)
            {
                return;
            }

            if (e.RequestId != _targetRequestId
                || !string.Equals(e.Theme, _targetTheme, StringComparison.Ordinal))
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "theme-cover-stale-ack",
                    $"theme={e.Theme} requestId={e.RequestId} targetTheme={_targetTheme ?? "(null)"} targetRequestId={_targetRequestId}");
                return;
            }

            HideCoverAfterPaint();
        }

        private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
            => HideCover();

        private void BeginCoverSession(string targetTheme, ThemeVariant targetThemeVariant)
        {
            _coverGeneration++;
            _targetTheme = targetTheme;
            _targetThemeVariant = targetThemeVariant;
            _targetRequestId = 0;
        }

        private void ShowCover()
        {
            if (_disposed)
            {
                return;
            }

            if (_covered && _cover.UpdateBrush(_coverHost, _targetThemeVariant))
            {
                RestartFallback();
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "theme-cover-retargeted",
                    $"theme={_targetTheme ?? "(null)"}");
                return;
            }

            if (_cover.Show(_coverHost, _targetThemeVariant))
            {
                _covered = true;
                _pendingShowOnBounds = false;
                RestartFallback();
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "theme-cover-shown",
                    $"theme={_targetTheme ?? "(null)"}");
                return;
            }

            if (!_pendingShowOnBounds)
            {
                _pendingShowOnBounds = true;
                _coverHost.LayoutUpdated += OnCoverHostLayoutUpdated;
                ApplicateTrace.DiagMs("pane-seq", "theme-cover-deferred", "reason=no-bounds");
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
            _targetTheme = null;
            _targetThemeVariant = null;
            _targetRequestId = 0;
            var duration = animated
                ? ApplicateMotion.ModeSwitchDuration(_documentState.ReadingPreferences)
                : TimeSpan.Zero;
            _cover.Hide(duration);
            ApplicateTrace.DiagMs("pane-seq", "theme-cover-hidden");
        }

        private void RestartFallback()
        {
            ReleaseFallback();
            _fallbackTimer = _scheduler.CreateTimer(FallbackTimeout, OnFallbackTick);
            _fallbackTimer.Start();
        }

        private void OnFallbackTick(object? sender, EventArgs e)
        {
            ApplicateTrace.DiagMs("pane-seq", "theme-cover-fallback");
            HideCover();
        }

        private void ReleaseFallback()
        {
            if (_fallbackTimer is null)
            {
                return;
            }

            _fallbackTimer.Stop();
            _fallbackTimer.Dispose();
            _fallbackTimer = null;
        }

        private static ThemeVariant ToThemeVariant(ThemeMode theme)
            => theme switch
            {
                ThemeMode.Dark => ThemeVariant.Dark,
                ThemeMode.ClassicWhite => AvaloniaThemeService.ClassicWhiteThemeVariant,
                _ => ThemeVariant.Light
            };

        private static string ToRendererTheme(ThemeMode theme)
            => theme switch
            {
                ThemeMode.Dark => "dark",
                ThemeMode.ClassicWhite => "classic-white",
                _ => "light"
            };

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _documentState.PropertyChanged -= OnDocumentStatePropertyChanged;
            _documentState.ThemeTransitionStarting -= OnThemeTransitionStarting;
            _signals.RendererFailed -= OnRendererFailed;
            _signals.ThemeChangeSent -= OnThemeChangeSent;
            _signals.ThemeApplied -= OnThemeApplied;
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

        public event EventHandler<ThemeTransitionStartingEventArgs>? ThemeTransitionStarting
        {
            add => _viewModel.ThemeTransitionStarting += value;
            remove => _viewModel.ThemeTransitionStarting -= value;
        }

        public MarkdownSource? Document => _viewModel.Document;

        public ReadingPreferences ReadingPreferences => _viewModel.ReadingPreferences;

        public void ClearDocumentHeadings()
            => _viewModel.UpdateDocumentHeadings(Array.Empty<DocumentHeading>());
    }

    private sealed class MainWindowStartupRevealState(MainWindowViewModel viewModel) : IApplicateStartupRevealState
    {
        private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _viewModel.PropertyChanged += value;
            remove => _viewModel.PropertyChanged -= value;
        }

        public MarkdownSource? Document => _viewModel.Document;

        public bool IsTocPreferredVisible => _viewModel.IsTocPreferredVisible;

        public bool HasDocumentHeadings => _viewModel.HasDocumentHeadings;
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

    private sealed class SharedWebViewStartupRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateStartupRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public event EventHandler? DocumentRevealReady
        {
            add => _host.View.DocumentRevealReady += value;
            remove => _host.View.DocumentRevealReady -= value;
        }

        public event EventHandler<IReadOnlyList<DocumentHeading>>? HeadingsChanged
        {
            add => _host.View.HeadingsChanged += value;
            remove => _host.View.HeadingsChanged -= value;
        }

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler? RendererSettled
        {
            add => _host.View.ModeToggleSettled += value;
            remove => _host.View.ModeToggleSettled -= value;
        }

        public void RequestRendererSettleProbe()
            => _host.View.RequestModeToggleSettleProbe();
    }

    private sealed class SharedWebViewThemeRevealSignals(IApplicateSharedWebViewHost host)
        : IApplicateThemeRevealSignals
    {
        private readonly IApplicateSharedWebViewHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
        {
            add => _host.RendererFailed += value;
            remove => _host.RendererFailed -= value;
        }

        public event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent
        {
            add => _host.View.ThemeChangeSent += value;
            remove => _host.View.ThemeChangeSent -= value;
        }

        public event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied
        {
            add => _host.View.ThemeApplied += value;
            remove => _host.View.ThemeApplied -= value;
        }

        public bool HasLoadedDocumentForSource(MarkdownSource? source)
            => _host.View.HasLoadedDocumentForSource(source);
    }

    private sealed class WindowStartupRevealShell(Window window) : IApplicateStartupRevealShell
    {
        private readonly Window _window = window ?? throw new ArgumentNullException(nameof(window));
        private readonly Dictionary<EventHandler, EventHandler<SizeChangedEventArgs>> _sizeChangedHandlers = [];

        public Control CoverHost => _window;

        public double Opacity
        {
            get => _window.Opacity;
            set => _window.Opacity = value;
        }

        public event EventHandler? Opened
        {
            add
            {
                if (value is not null)
                {
                    _window.Opened += value;
                }
            }
            remove
            {
                if (value is not null)
                {
                    _window.Opened -= value;
                }
            }
        }

        public event EventHandler? SizeChanged
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                EventHandler<SizeChangedEventArgs> handler = (sender, _) => value(sender, EventArgs.Empty);
                _sizeChangedHandlers[value] = handler;
                _window.SizeChanged += handler;
            }
            remove
            {
                if (value is not null && _sizeChangedHandlers.Remove(value, out var handler))
                {
                    _window.SizeChanged -= handler;
                }
            }
        }

        public event EventHandler? Closed
        {
            add
            {
                if (value is not null)
                {
                    _window.Closed += value;
                }
            }
            remove
            {
                if (value is not null)
                {
                    _window.Closed -= value;
                }
            }
        }
    }

    private sealed class ModeRevealCoverPresenter(ApplicateModeRevealCoverWindow cover)
        : IApplicateAirspaceCoverPresenter
    {
        private readonly ApplicateModeRevealCoverWindow _cover = cover ?? throw new ArgumentNullException(nameof(cover));

        public bool Show(Control host)
            => _cover.Show(host);

        public bool Show(Control host, ThemeVariant? themeVariant)
            => _cover.Show(host, themeVariant);

        public bool ShowStartupSplash(Control host, string? documentName)
            => _cover.ShowStartupSplash(host, documentName);

        public bool UpdateBrush(Control host, ThemeVariant? themeVariant)
            => _cover.UpdateBrush(host, themeVariant);

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

    private sealed class DispatcherAirspaceScheduler : IApplicateAirspaceScheduler
    {
        public void Post(Action action, DispatcherPriority priority)
            => Dispatcher.UIThread.Post(action, priority);

        public IApplicateAirspaceTimer CreateTimer(TimeSpan interval, EventHandler tick)
            => new DispatcherAirspaceTimer(interval, tick);
    }

    private sealed class DispatcherAirspaceTimer : IApplicateAirspaceTimer
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _tick;
        private bool _disposed;

        public DispatcherAirspaceTimer(TimeSpan interval, EventHandler tick)
        {
            _tick = tick ?? throw new ArgumentNullException(nameof(tick));
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += _tick;
        }

        public void Start()
            => _timer.Start();

        public void Stop()
            => _timer.Stop();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _timer.Stop();
            _timer.Tick -= _tick;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        private EmptyDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class SharedWebViewHostRevealIntents(IApplicateModeTransactionHost host)
    : IApplicateHostRevealIntents
{
    private readonly IApplicateModeTransactionHost _host = host ?? throw new ArgumentNullException(nameof(host));

    public event EventHandler<ApplicateRendererFailureEvent>? RendererFailed
    {
        add => _host.RendererFailed += value;
        remove => _host.RendererFailed -= value;
    }

    public event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled
    {
        add => _host.MinimapSettled += value;
        remove => _host.MinimapSettled -= value;
    }

    public event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted
    {
        add => _host.CommitCompleted += value;
        remove => _host.CommitCompleted -= value;
    }

    public event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled
    {
        add => _host.RendererSettled += value;
        remove => _host.RendererSettled -= value;
    }

    public void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode)
        => _host.SuppressNativeRendererForModeSwitch(displayedMode);

    public void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode)
        => _host.RestoreNativeRendererAfterModeSwitchSuppression(displayedMode);

    public bool RevealNativeRendererForCommittedTransaction(long transactionGeneration)
        => _host.RevealNativeWebViewForCommittedTransaction(transactionGeneration);
}

internal interface IApplicateDocumentRevealState : INotifyPropertyChanged
{
    event EventHandler? DocumentTransitionStarting;

    event EventHandler? SuppressNextDocumentReveal;

    event EventHandler<ThemeTransitionStartingEventArgs>? ThemeTransitionStarting;

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

internal interface IApplicateStartupRevealState : INotifyPropertyChanged
{
    MarkdownSource? Document { get; }

    bool IsTocPreferredVisible { get; }

    bool HasDocumentHeadings { get; }
}

internal interface IApplicateStartupRevealSignals
{
    event EventHandler? DocumentRevealReady;

    event EventHandler<IReadOnlyList<DocumentHeading>>? HeadingsChanged;

    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler? RendererSettled;

    void RequestRendererSettleProbe();
}

internal interface IApplicateHostRevealIntents
{
    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler<ApplicateMinimapSettledEventArgs>? MinimapSettled;

    event EventHandler<ApplicateCommitCompletedEventArgs>? CommitCompleted;

    event EventHandler<ApplicateRendererSettledEventArgs>? RendererSettled;

    void SuppressOutgoingNativeRenderer(ApplicateMode displayedMode);

    void RestoreOutgoingNativeRenderer(ApplicateMode displayedMode);

    bool RevealNativeRendererForCommittedTransaction(long transactionGeneration);
}

internal interface IApplicateThemeRevealSignals
{
    event EventHandler<ApplicateRendererFailureEvent>? RendererFailed;

    event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent;

    event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied;

    bool HasLoadedDocumentForSource(MarkdownSource? source);
}

internal interface IApplicateStartupRevealShell
{
    Control CoverHost { get; }

    double Opacity { get; set; }

    event EventHandler? Opened;

    event EventHandler? SizeChanged;

    event EventHandler? Closed;
}

internal interface IApplicateAirspaceCoverPresenter : IDisposable
{
    bool Show(Control host);

    bool Show(Control host, ThemeVariant? themeVariant);

    bool ShowStartupSplash(Control host, string? documentName);

    bool UpdateBrush(Control host, ThemeVariant? themeVariant);

    void Hide();

    void Hide(TimeSpan duration);
}

internal interface IApplicateAirspacePaintGate
{
    void AfterTwoFrames(Control anchor, Action action);
}

internal interface IApplicateAirspaceScheduler
{
    void Post(Action action, DispatcherPriority priority);

    IApplicateAirspaceTimer CreateTimer(TimeSpan interval, EventHandler tick);
}

internal interface IApplicateAirspaceTimer : IDisposable
{
    void Start();

    void Stop();
}
