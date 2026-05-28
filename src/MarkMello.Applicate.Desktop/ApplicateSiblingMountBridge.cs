using System;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateSiblingMountBridge : IDisposable
{
    private readonly INotifyPropertyChanged _vm;
    private readonly ContentControl _viewerSlot;
    private readonly Panel _editSlot;
    private readonly Control _editContent;
    private readonly Func<bool> _getIsViewer;
    private readonly Func<bool> _getIsEditMode;
    private readonly Func<object?> _getEditorSession;
    private readonly Func<object?> _getDocument;
    private readonly Func<ReadingPreferences> _getReadingPreferences;
    private readonly IApplicateModeRevealSignal? _modeRevealSignal;
    private readonly IApplicateModeTransactionHost? _transactionHost;
    private readonly Panel? _modeRevealCoverHost;
    private readonly ApplicateModeRevealCoverWindow? _modeRevealCoverWindow;
    private static readonly TimeSpan ModeRevealCoverFallbackTimeout = TimeSpan.FromMilliseconds(650);
    private volatile bool _disposed;
    private int _reconcilePending;
    private int _modeRevealCoverContinuationPending;
    private bool _desiredViewerVisible;
    private bool _desiredEditVisible;
    private bool _modeRevealPending;
    private bool _modeRevealCompleting;
    private bool _modeRevealCoverArmed;
    private DispatcherTimer? _modeRevealFallbackTimer;
    private readonly ApplicateModeTransitionController _modeTransitionController = new(ApplicateMode.Viewer);
    private long _nextModeTransactionGeneration;
    private long _activeModeTransactionGeneration;
    private ApplicateMode? _activeModeTransactionTarget;
    // Single-slot by design: MarkMello has exactly two mode hosts (Viewer/Edit),
    // and the bridge owns at most one native-reveal transaction at a time.
    // A future third mode or nested mode transaction must replace this with
    // an explicit map/stack.
    private ApplicateMode? _suppressedOutgoingMode;
    private long _suppressedOutgoingModeTransactionGeneration;
    private long _pendingModeTransactionCommitGeneration;
    private ApplicateMode _pendingModeTransactionCommitMode;
    private int _modeTransactionCommitPending;
    private bool _modeTransactionRollbackInProgress;

    public ApplicateSiblingMountBridge(
        INotifyPropertyChanged vm,
        ContentControl viewerSlot,
        Panel editSlot,
        Control editContent,
        Func<bool> getIsViewer,
        Func<bool> getIsEditMode,
        Func<object?> getEditorSession,
        Func<object?> getDocument,
        Func<ReadingPreferences> getReadingPreferences,
        object viewerContent,
        IApplicateModeRevealSignal? modeRevealSignal = null,
        IApplicateModeTransactionHost? transactionHost = null,
        Panel? modeRevealCoverHost = null)
    {
        _vm = vm;
        _viewerSlot = viewerSlot;
        _editSlot = editSlot;
        _editContent = editContent;
        _getIsViewer = getIsViewer;
        _getIsEditMode = getIsEditMode;
        _getEditorSession = getEditorSession;
        _getDocument = getDocument;
        _getReadingPreferences = getReadingPreferences;
        _modeRevealSignal = modeRevealSignal;
        _transactionHost = transactionHost;
        _modeRevealCoverHost = modeRevealCoverHost;
        _viewerSlot.Content = viewerContent;
        if (_modeRevealCoverHost is not null)
        {
            _modeRevealCoverWindow = new ApplicateModeRevealCoverWindow();
        }
        InstallModeSwitchFades(_getReadingPreferences());
        _viewerSlot.Opacity = 0.0;
        _editSlot.Opacity = 0.0;
        _editContent.Opacity = 0.0;

        // editContent is the pre-built EditWorkspaceView + EditPreviewView
        // already added to editSlot.Children by the caller (Panel children
        // realize eagerly so EditPreview.OnAttachedToVisualTree fires at
        // startup with editSlot.IsVisible=false — HWND geometry-lag invisible
        // to the user). Reconcile only updates DataContext to drive the
        // EditPreview.AttachSession lifecycle on mode toggle.

        _viewerSlot.PropertyChanged += OnSlotPropertyChanged;
        _editSlot.PropertyChanged += OnSlotPropertyChanged;
        if (_modeRevealSignal is not null)
        {
            _modeRevealSignal.RevealCompleted += OnModeRevealCompleted;
        }
        if (_transactionHost is not null)
        {
            _transactionHost.CommitCompleted += OnTransactionCommitCompleted;
            _transactionHost.MinimapSettled += OnTransactionMinimapSettled;
            _transactionHost.RendererSettled += OnTransactionRendererSettled;
            _transactionHost.RendererFailed += OnTransactionRendererFailed;
        }
        _vm.PropertyChanged += OnVmPropertyChanged;
        Reconcile();
    }

    private void OnSlotPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.BoundsProperty && e.Property != Visual.IsVisibleProperty)
        {
            return;
        }
        var slotName = ReferenceEquals(sender, _viewerSlot) ? "viewerSlot" : "editSlot";
        ApplicateTrace.ModeToggle($"{slotName}.{e.Property.Name}: {e.OldValue} -> {e.NewValue}");
        TryApplyLayoutSettledForActiveTransaction();
    }

    internal void ForceReconcile() => MarshalReconcile();

    internal void ApplyTransactionGenerationContext(ApplicateMode requestedMode, long generation)
    {
        ApplicateModeTransactionContext.SetTransactionGeneration(
            _viewerSlot,
            requestedMode == ApplicateMode.Viewer ? generation : 0);
        ApplicateModeTransactionContext.SetTransactionGeneration(
            _editSlot,
            requestedMode == ApplicateMode.Edit ? generation : 0);
    }

    internal void ClearTransactionGenerationContext()
    {
        ApplicateModeTransactionContext.SetTransactionGeneration(_viewerSlot, 0);
        ApplicateModeTransactionContext.SetTransactionGeneration(_editSlot, 0);
    }

    private void OnTransactionCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_modeTransitionController.ApplyCommitCompleted(e.TransactionGeneration, e.Mode))
        {
            QueueModeTransactionCommit(e.TransactionGeneration, e.Mode);
        }
    }

    private void OnTransactionMinimapSettled(object? sender, ApplicateMinimapSettledEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _modeTransitionController.Snapshot;
        if (_modeTransitionController.ApplyMinimapSettled(e.TransactionGeneration))
        {
            QueueModeTransactionCommit(e.TransactionGeneration, snapshot.RequestedMode);
        }
    }

    private void OnTransactionRendererSettled(object? sender, ApplicateRendererSettledEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _modeTransitionController.Snapshot;
        if (_modeTransitionController.ApplyRendererSettled(e.TransactionGeneration))
        {
            QueueModeTransactionCommit(e.TransactionGeneration, snapshot.RequestedMode);
        }
    }

    private void OnTransactionRendererFailed(object? sender, ApplicateRendererFailureEvent e)
    {
        if (_disposed || _activeModeTransactionGeneration <= 0)
        {
            return;
        }

        _modeTransitionController.ApplyRendererFailed(_activeModeTransactionGeneration);
        RollbackActiveModeTransaction(
            reason: "renderer-failure",
            committedRollbackMode: null,
            applyOutgoingSlotState: true);
        MarshalReconcile();
    }

    private void OnModeRevealCompleted(object? sender, EventArgs e)
    {
        if (_disposed || !_modeRevealPending)
        {
            return;
        }

        _modeRevealPending = false;
        _modeRevealCompleting = true;
        _modeRevealCoverArmed = false;
        ReleaseModeRevealFallback();
        MarshalReconcile();
    }

    private void OnModeRevealFallbackTick(object? sender, EventArgs e)
    {
        if (_disposed || !_modeRevealPending)
        {
            ReleaseModeRevealFallback();
            return;
        }

        ApplicateTrace.DiagMs("pane-seq", "bridge-reveal-cover-fallback");
        _modeRevealPending = false;
        _modeRevealCompleting = true;
        _modeRevealCoverArmed = false;
        ReleaseModeRevealFallback();
        MarshalReconcile();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }
        if (e.PropertyName is not (
            nameof(MainWindowViewModel.IsViewer)
            or nameof(MainWindowViewModel.IsEditMode)
            or nameof(MainWindowViewModel.EditorSession)
            or nameof(MainWindowViewModel.Document)
            or nameof(MainWindowViewModel.ReadingPreferences)))
        {
            return;
        }
        MarshalReconcile();
    }

    // VM mutations may arrive from Task continuations (ApplyLoadedDocument
    // async path). Reconcile writes Avalonia properties which assert UI-thread
    // affinity. Coalescing prevents N rapid PropertyChanged firings from
    // posting N reconciles — only one latest-state reconcile lands.
    private void MarshalReconcile()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Reconcile();
            return;
        }
        if (Interlocked.Exchange(ref _reconcilePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _reconcilePending, 0);
                if (_disposed)
                {
                    return;
                }
                Reconcile();
            }, DispatcherPriority.Default);
        }
    }

    private void Reconcile()
    {
        if (_disposed)
        {
            return;
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var isViewer = _getIsViewer();
        var isEdit = _getIsEditMode();
        var session = _getEditorSession();
        var document = _getDocument();
        var readingPreferences = _getReadingPreferences();
        InstallModeSwitchFades(readingPreferences);

        // Triple-gate: viewer mode AND not editing AND document still exists.
        // The `document is not null` clause closes the close-file parasitic-
        // frame window. IsViewer is a derived property on State and fires
        // LAST in the close-file sequence; without the document gate the
        // viewer slot would flash visible on Tick 1 (IsEditMode=false) with
        // a stale document still painted.
        var viewerVisible = isViewer && !isEdit && document is not null;
        var editVisible = isViewer && isEdit && session is not null;
        var previousViewerVisible = _desiredViewerVisible;
        var previousEditVisible = _desiredEditVisible;
        var modeSlotSwitch = (previousViewerVisible && editVisible)
            || (previousEditVisible && viewerVisible);
        var releaseCoverAfterReconcile = _modeRevealCompleting;
        if (TryReconcileTransactionalModeSwitch(
            viewerVisible,
            editVisible,
            modeSlotSwitch,
            session))
        {
            return;
        }

        ApplyModeTransactionGenerationContext(modeSlotSwitch, viewerVisible, editVisible);
        if (_modeRevealCoverArmed && !modeSlotSwitch && !_modeRevealPending)
        {
            _modeRevealCoverArmed = false;
            HideModeRevealCover();
            ApplicateTrace.DiagMs(
                "pane-seq",
                "bridge-cover-cancelled",
                $"viewerVis={viewerVisible} editVis={editVisible}");
        }

        if (viewerVisible || editVisible)
        {
            if (modeSlotSwitch && _modeRevealSignal is not null)
            {
                if (!_modeRevealCoverArmed && TryPrimeModeRevealCover())
                {
                    _modeRevealCoverArmed = true;
                    QueueModeRevealCoveredReconcile();
                    return;
                }

                _modeRevealCoverArmed = false;
                _modeRevealSignal.SuppressNativeRendererForModeSwitch();
                _modeRevealPending = true;
                RestartModeRevealFallback();
            }
        }
        else
        {
            _modeRevealPending = false;
            _modeRevealCoverArmed = false;
            HideModeRevealCover();
            ReleaseModeRevealFallback();
        }

        var keepViewerCover = _modeRevealPending && previousViewerVisible && !viewerVisible && editVisible;
        // The edit surface contains live editor chrome, the preview splitter,
        // and the editor minimap; carrying it over the reading reveal leaves
        // visible residues between frames. The shared WebView host owns the
        // reading renderer reveal, so edit -> reading does not keep edit as a
        // cover.
        var keepEditCover = false;
        var instantSlotVisuals = _modeRevealPending || _modeRevealCompleting;
        _desiredViewerVisible = viewerVisible;
        _desiredEditVisible = editVisible;

        ApplicateTrace.ModeToggle(
            $"Reconcile in: isViewer={isViewer} isEdit={isEdit} session={(session is not null)} document={(document is not null)} -> viewerVis={viewerVisible} editVis={editVisible}");
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-reconcile-enter",
            $"viewerVis={viewerVisible} editVis={editVisible} coverViewer={keepViewerCover} coverEdit={keepEditCover} revealPending={_modeRevealPending} editSlotPrev={_editSlot.IsVisible}");

        var viewerPaintVisible = (viewerVisible && !keepEditCover) || keepViewerCover;
        var editPaintVisible = (editVisible && !keepViewerCover) || keepEditCover;
        ApplySlotState(
            _viewerSlot,
            viewerVisible || keepViewerCover,
            viewerVisible && !_modeRevealPending,
            viewerPaintVisible ? 1.0 : 0.0,
            instantSlotVisuals);
        ApplySlotState(
            _editSlot,
            editVisible || keepEditCover,
            editVisible && !keepViewerCover,
            editPaintVisible ? 1.0 : 0.0,
            instantSlotVisuals);
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-edit-slot-applied",
            $"editSlotIsVisible={_editSlot.IsVisible} editSlotBounds={_editSlot.Bounds.Width:F0}x{_editSlot.Bounds.Height:F0}");

        // Read -> edit still keeps the previous viewer painted until the
        // shared WebView reports final-layout reveal. Edit -> reading does
        // not keep edit painted: the live edit layer contains the splitter
        // and preview chrome that produced the visible between-frame residue.
        ApplyOpacity(_editContent, editPaintVisible ? 1.0 : 0.0, instantSlotVisuals);

        ApplicateTrace.ModeToggle($"Bridge slots: viewerSlot.Bounds={_viewerSlot.Bounds} editSlot.Bounds={_editSlot.Bounds}");

        // Permanent mount: editSlot.Content is the pre-built EditWorkspaceView
        // (set once in ctor). On session change we only update DataContext —
        // the EditWorkspaceView + ApplicateEditPreviewView pair stays mounted,
        // the shared WebView2 HWND stays in _webSlot, no SetParent operation
        // ever fires beyond the single startup attach. ApplicateEditPreviewView
        // observes DataContext via OnDataContextChanged → AttachSession.
        var sessionChanged = !ReferenceEquals(_editContent.DataContext, session);
        if (sessionChanged)
        {
            _editContent.DataContext = session;
        }
        if (releaseCoverAfterReconcile)
        {
            HideModeRevealCover();
        }
        _modeRevealCompleting = false;
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ApplicateTrace.ModeToggle($"Reconcile out: elapsed={elapsedMs:F2}ms sessionChanged={sessionChanged}");
    }

    private bool TryReconcileTransactionalModeSwitch(
        bool viewerVisible,
        bool editVisible,
        bool modeSlotSwitch,
        object? session)
    {
        if (_transactionHost is null)
        {
            return false;
        }

        var requestedMode = viewerVisible
            ? ApplicateMode.Viewer
            : editVisible
                ? ApplicateMode.Edit
                : (ApplicateMode?)null;
        if (_modeTransactionRollbackInProgress)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "bridge-transaction-rollback-in-progress-request-ignored",
                $"requested={requestedMode?.ToString() ?? "(null)"} active={_activeModeTransactionGeneration}");
            return true;
        }

        if (_activeModeTransactionGeneration > 0)
        {
            if (requestedMode is null)
            {
                CancelModeTransaction();
                return false;
            }

            if (_activeModeTransactionTarget == requestedMode)
            {
                var activeTargetSessionChanged = !ReferenceEquals(_editContent.DataContext, session);
                if (activeTargetSessionChanged)
                {
                    _editContent.DataContext = session;
                }

                TryApplyLayoutSettledForActiveTransaction();
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-reconcile-active-target",
                    $"generation={_activeModeTransactionGeneration} target={requestedMode.Value}");
                return true;
            }

            if (_suppressedOutgoingMode == requestedMode)
            {
                RollbackActiveModeTransaction(
                    reason: "rapid-toggle",
                    committedRollbackMode: requestedMode.Value,
                    applyOutgoingSlotState: true);
                return true;
            }
        }

        var transactionActive = _modeTransitionController.Snapshot.IsSwitching
            || _activeModeTransactionGeneration > 0;
        if (!transactionActive && !modeSlotSwitch)
        {
            if (requestedMode is not null)
            {
                _modeTransitionController.ResetDisplayedMode(requestedMode.Value);
            }

            return false;
        }

        if (!transactionActive
            && modeSlotSwitch
            && !_modeRevealCoverArmed
            && TryPrimeModeRevealCover())
        {
            _modeRevealCoverArmed = true;
            QueueModeRevealCoveredReconcile();
            return true;
        }

        _modeRevealPending = false;
        _modeRevealCompleting = false;
        ReleaseModeRevealFallback();

        if (requestedMode is null)
        {
            CancelModeTransaction();
            return false;
        }

        var outgoingMode = _modeTransitionController.Snapshot.DisplayedMode;
        var generation = _modeTransitionController.RequestMode(requestedMode.Value);
        if (generation > 0)
        {
            _activeModeTransactionGeneration = generation;
            _activeModeTransactionTarget = requestedMode.Value;
            ApplyTransactionGenerationContext(requestedMode.Value, generation);
            SuppressOutgoingNativeRendererForActiveTransaction(
                generation,
                outgoingMode,
                requestedMode.Value);
        }
        else
        {
            _activeModeTransactionGeneration = 0;
            _activeModeTransactionTarget = null;
            _suppressedOutgoingModeTransactionGeneration = 0;
            _suppressedOutgoingMode = null;
            _pendingModeTransactionCommitGeneration = 0;
            _modeRevealCoverArmed = false;
            ClearTransactionGenerationContext();
            HideModeRevealCover();
        }

        _desiredViewerVisible = viewerVisible;
        _desiredEditVisible = editVisible;
        ApplyTransactionalSlotStates();

        var sessionChanged = !ReferenceEquals(_editContent.DataContext, session);
        if (sessionChanged)
        {
            _editContent.DataContext = session;
        }

        TryApplyLayoutSettledForActiveTransaction();
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-transaction-reconcile",
            $"viewerVis={viewerVisible} editVis={editVisible} generation={generation} target={requestedMode.Value}");
        return true;
    }

    private void SuppressOutgoingNativeRendererForActiveTransaction(
        long generation,
        ApplicateMode outgoingMode,
        ApplicateMode requestedMode)
    {
        if (_transactionHost is null
            || generation <= 0
            || outgoingMode == requestedMode
            || _suppressedOutgoingModeTransactionGeneration == generation)
        {
            return;
        }

        _transactionHost.SuppressNativeRendererForModeSwitch(outgoingMode);
        _suppressedOutgoingMode = outgoingMode;
        _suppressedOutgoingModeTransactionGeneration = generation;
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-transaction-outgoing-native-suppressed",
            $"generation={generation} outgoing={outgoingMode} target={requestedMode}");
    }

    private void ClearSuppressedOutgoingNativeRendererForCompletedTransaction(
        long generation,
        ApplicateMode targetMode,
        string reason)
    {
        if (generation <= 0 || _suppressedOutgoingModeTransactionGeneration != generation)
        {
            return;
        }

        var outgoingMode = _suppressedOutgoingMode;
        _suppressedOutgoingMode = null;
        _suppressedOutgoingModeTransactionGeneration = 0;
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-transaction-outgoing-native-suppression-cleared",
            $"generation={generation} outgoing={outgoingMode?.ToString() ?? "(null)"} target={targetMode} reason={reason}");
    }

    private void CancelModeTransaction()
    {
        if (_activeModeTransactionGeneration > 0)
        {
            _modeTransitionController.ApplyRendererFailed(_activeModeTransactionGeneration);
        }

        RollbackActiveModeTransaction(
            reason: "cancel",
            committedRollbackMode: _suppressedOutgoingMode,
            applyOutgoingSlotState: true);
    }

    private void RollbackActiveModeTransaction(
        string reason,
        ApplicateMode? committedRollbackMode,
        bool applyOutgoingSlotState)
    {
        if (_modeTransactionRollbackInProgress)
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "bridge-transaction-rollback-reentry-ignored",
                $"reason={reason} active={_activeModeTransactionGeneration}");
            return;
        }

        var generation = _suppressedOutgoingModeTransactionGeneration > 0
            ? _suppressedOutgoingModeTransactionGeneration
            : _activeModeTransactionGeneration;
        var outgoingMode = _suppressedOutgoingMode;
        var targetMode = _activeModeTransactionTarget;
        var restoreSucceeded = false;
        var restoreFailed = false;
        _modeTransactionRollbackInProgress = true;

        try
        {
            _activeModeTransactionGeneration = 0;
            _activeModeTransactionTarget = null;
            _pendingModeTransactionCommitGeneration = 0;
            _modeRevealCoverArmed = false;
            ClearTransactionGenerationContext();
            _suppressedOutgoingMode = null;
            _suppressedOutgoingModeTransactionGeneration = 0;

            var rollbackMode = committedRollbackMode ?? outgoingMode;
            if (rollbackMode is not null)
            {
                _modeTransitionController.ResetDisplayedMode(rollbackMode.Value);
                _desiredViewerVisible = rollbackMode.Value == ApplicateMode.Viewer;
                _desiredEditVisible = rollbackMode.Value == ApplicateMode.Edit;
                if (applyOutgoingSlotState)
                {
                    ApplyCommittedModeSlotStates(rollbackMode.Value);
                }
            }

            if (_transactionHost is null || outgoingMode is null)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-outgoing-native-restore-skipped",
                    $"generation={generation} outgoing={outgoingMode?.ToString() ?? "(null)"} target={targetMode?.ToString() ?? "(null)"} reason={reason}");
                return;
            }

            try
            {
                _transactionHost.RestoreNativeRendererAfterModeSwitchSuppression(outgoingMode.Value);
                restoreSucceeded = true;
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-outgoing-native-restored-before-cover-hide",
                    $"generation={generation} outgoing={outgoingMode.Value} target={targetMode?.ToString() ?? "(null)"} reason={reason}");
            }
            catch (Exception ex)
            {
                restoreFailed = true;
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-outgoing-native-restore-failed",
                    $"generation={generation} outgoing={outgoingMode.Value} target={targetMode?.ToString() ?? "(null)"} reason={reason} exceptionType={ex.GetType().Name}");
            }
        }
        finally
        {
            _suppressedOutgoingMode = null;
            _suppressedOutgoingModeTransactionGeneration = 0;
            _modeTransactionRollbackInProgress = false;
            if (restoreFailed)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-outgoing-native-restore-failed-but-bookkeeping-cleared",
                    $"generation={generation} outgoing={outgoingMode?.ToString() ?? "(null)"} target={targetMode?.ToString() ?? "(null)"} reason={reason}");
            }
            else if (!restoreSucceeded)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-outgoing-native-bookkeeping-cleared-without-restore",
                    $"generation={generation} outgoing={outgoingMode?.ToString() ?? "(null)"} target={targetMode?.ToString() ?? "(null)"} reason={reason}");
            }

            HideModeRevealCover();
        }
    }

    private void ApplyTransactionalSlotStates()
    {
        var viewer = _modeTransitionController.GetSlotState(ApplicateMode.Viewer);
        var edit = _modeTransitionController.GetSlotState(ApplicateMode.Edit);
        ApplySlotState(_viewerSlot, viewer.IsVisible, viewer.IsInteractive, viewer.Opacity, instant: true);
        ApplySlotState(_editSlot, edit.IsVisible, edit.IsInteractive, edit.Opacity, instant: true);
        ApplyOpacity(_editContent, edit.Opacity > 0 ? 1.0 : 0.0, instant: true);
    }

    private void TryApplyLayoutSettledForActiveTransaction()
    {
        if (_transactionHost is null)
        {
            return;
        }

        var snapshot = _modeTransitionController.Snapshot;
        if (!snapshot.IsSwitching)
        {
            return;
        }

        var target = snapshot.RequestedMode == ApplicateMode.Viewer
            ? (Control)_viewerSlot
            : _editSlot;
        if (!target.IsVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            return;
        }

        if (_modeTransitionController.ApplyLayoutSettled(snapshot.ActiveGeneration))
        {
            QueueModeTransactionCommit(snapshot.ActiveGeneration, snapshot.RequestedMode);
        }
    }

    private void QueueModeTransactionCommit(long generation, ApplicateMode mode)
    {
        if (generation <= 0)
        {
            return;
        }

        _pendingModeTransactionCommitGeneration = generation;
        _pendingModeTransactionCommitMode = mode;
        if (Interlocked.Exchange(ref _modeTransactionCommitPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(CommitQueuedModeTransaction, DispatcherPriority.Render);
    }

    private void CommitQueuedModeTransaction()
    {
        Interlocked.Exchange(ref _modeTransactionCommitPending, 0);
        if (_disposed || _transactionHost is null)
        {
            return;
        }

        var generation = _pendingModeTransactionCommitGeneration;
        var mode = _pendingModeTransactionCommitMode;
        _pendingModeTransactionCommitGeneration = 0;
        if (generation <= 0
            || generation != _activeModeTransactionGeneration
            || _activeModeTransactionTarget != mode)
        {
            if (generation > 0)
            {
                ApplicateTrace.DiagMs(
                    "pane-seq",
                    "bridge-transaction-stale-commit-discarded",
                    $"generation={generation} active={_activeModeTransactionGeneration} target={_activeModeTransactionTarget?.ToString() ?? "(null)"} requested={mode}");
            }

            return;
        }

        ApplyCommittedModeSlotStates(mode);
        if (!_transactionHost.RevealNativeWebViewForCommittedTransaction(generation))
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "bridge-transaction-native-reveal-rejected",
                $"generation={generation} mode={mode}");
            RollbackActiveModeTransaction(
                reason: "rejected-reveal",
                committedRollbackMode: _suppressedOutgoingMode,
                applyOutgoingSlotState: true);
            return;
        }

        _modeTransitionController.ResetDisplayedMode(mode);
        _activeModeTransactionGeneration = 0;
        _activeModeTransactionTarget = null;
        _desiredViewerVisible = mode == ApplicateMode.Viewer;
        _desiredEditVisible = mode == ApplicateMode.Edit;
        _modeRevealCoverArmed = false;
        ClearTransactionGenerationContext();
        ClearSuppressedOutgoingNativeRendererForCompletedTransaction(
            generation,
            mode,
            reason: "success");
        HideModeRevealCover();
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-transaction-committed",
            $"generation={generation} mode={mode}");
    }

    private void ApplyCommittedModeSlotStates(ApplicateMode mode)
    {
        var viewerVisible = mode == ApplicateMode.Viewer;
        var editVisible = mode == ApplicateMode.Edit;
        ApplySlotState(_viewerSlot, viewerVisible, viewerVisible, viewerVisible ? 1.0 : 0.0, instant: true);
        ApplySlotState(_editSlot, editVisible, editVisible, editVisible ? 1.0 : 0.0, instant: true);
        ApplyOpacity(_editContent, editVisible ? 1.0 : 0.0, instant: true);
    }

    private void ApplyModeTransactionGenerationContext(bool modeSlotSwitch, bool viewerVisible, bool editVisible)
    {
        if (modeSlotSwitch)
        {
            var requestedMode = editVisible ? ApplicateMode.Edit : ApplicateMode.Viewer;
            if (_activeModeTransactionGeneration <= 0 || _activeModeTransactionTarget != requestedMode)
            {
                _activeModeTransactionGeneration = checked(++_nextModeTransactionGeneration);
                _activeModeTransactionTarget = requestedMode;
            }

            ApplyTransactionGenerationContext(requestedMode, _activeModeTransactionGeneration);
            return;
        }

        var visibleMode = viewerVisible
            ? ApplicateMode.Viewer
            : editVisible
                ? ApplicateMode.Edit
                : (ApplicateMode?)null;
        if (visibleMode is not null && _activeModeTransactionTarget == visibleMode.Value)
        {
            return;
        }

        _activeModeTransactionGeneration = 0;
        _activeModeTransactionTarget = null;
        ClearTransactionGenerationContext();
    }

    // IsHitTestVisible gates click/wheel/drag-drop independently of IsEnabled.
    // Native WebView2 HWND may receive Win32 input chain events even when
    // Avalonia thinks the parent is "disabled". Hit-test is the explicit gate.
    private static void ApplySlotState(Control slot, bool visible, bool interactive, double opacity, bool instant)
    {
        if (visible)
        {
            ApplyOpacity(slot, opacity, instant);
            slot.IsVisible = true;
        }
        else
        {
            slot.IsVisible = false;
            ApplyOpacity(slot, 0.0, instant);
        }

        slot.IsEnabled = interactive;
        slot.IsHitTestVisible = interactive;
        slot.IsTabStop = interactive;
        slot.Focusable = interactive;
    }

    private static void ApplyOpacity(Control control, double opacity, bool instant)
    {
        if (!instant)
        {
            control.Opacity = opacity;
            return;
        }

        var transitions = control.Transitions;
        control.Transitions = null;
        try
        {
            control.Opacity = opacity;
        }
        finally
        {
            control.Transitions = transitions;
        }
    }

    private void InstallModeSwitchFades(ReadingPreferences preferences)
    {
        InstallModeSwitchFade(_viewerSlot, preferences);
        InstallModeSwitchFade(_editSlot, preferences);
        InstallModeSwitchFade(_editContent, preferences);
    }

    private static void InstallModeSwitchFade(Control control, ReadingPreferences preferences)
    {
        var duration = ApplicateMotion.ModeSwitchDuration(preferences);
        if (duration == TimeSpan.Zero)
        {
            control.Transitions = null;
            return;
        }

        control.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = ApplicateMotion.Easing
            }
        ];
    }

    private void RestartModeRevealFallback()
    {
        ReleaseModeRevealFallback();
        _modeRevealFallbackTimer = new DispatcherTimer
        {
            Interval = ModeRevealCoverFallbackTimeout
        };
        _modeRevealFallbackTimer.Tick += OnModeRevealFallbackTick;
        _modeRevealFallbackTimer.Start();
    }

    private void ReleaseModeRevealFallback()
    {
        if (_modeRevealFallbackTimer is null)
        {
            return;
        }

        _modeRevealFallbackTimer.Stop();
        _modeRevealFallbackTimer.Tick -= OnModeRevealFallbackTick;
        _modeRevealFallbackTimer = null;
    }

    private bool TryPrimeModeRevealCover()
    {
        if (_modeRevealCoverHost is null || _modeRevealCoverWindow is null)
        {
            return false;
        }

        if (!_modeRevealCoverWindow.Show(_modeRevealCoverHost))
        {
            return false;
        }

        _modeRevealCoverHost.UpdateLayout();
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-visible",
            $"surface=window hostBounds={_modeRevealCoverHost.Bounds.Width:F0}x{_modeRevealCoverHost.Bounds.Height:F0}");
        return true;
    }

    private void QueueModeRevealCoveredReconcile()
    {
        if (Interlocked.Exchange(ref _modeRevealCoverContinuationPending, 1) != 0)
        {
            return;
        }

        var topLevel = _modeRevealCoverHost is null ? null : TopLevel.GetTopLevel(_modeRevealCoverHost);
        if (topLevel is null)
        {
            ApplicateTrace.DiagMs("pane-seq", "bridge-cover-wait-frame", "path=dispatcher-fallback");
            Dispatcher.UIThread.Post(ContinueModeRevealCoveredReconcile, DispatcherPriority.Background);
            return;
        }

        ApplicateTrace.DiagMs("pane-seq", "bridge-cover-wait-frame", "path=animation-frame");
        topLevel.RequestAnimationFrame(_ =>
        {
            if (_disposed)
            {
                Interlocked.Exchange(ref _modeRevealCoverContinuationPending, 0);
                return;
            }

            ApplicateTrace.DiagMs("pane-seq", "bridge-cover-frame", "step=1");
            topLevel.RequestAnimationFrame(_ =>
            {
                ApplicateTrace.DiagMs("pane-seq", "bridge-cover-frame", "step=2");
                ContinueModeRevealCoveredReconcile();
            });
        });
    }

    private void ContinueModeRevealCoveredReconcile()
    {
        Interlocked.Exchange(ref _modeRevealCoverContinuationPending, 0);
        if (_disposed)
        {
            return;
        }

        Reconcile();
    }

    private void HideModeRevealCover()
    {
        if (_modeRevealCoverWindow is null)
        {
            return;
        }

        _modeRevealCoverWindow.Hide();
        ApplicateTrace.DiagMs("pane-seq", "bridge-cover-hidden");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (_suppressedOutgoingMode is not null || _activeModeTransactionGeneration > 0)
        {
            RollbackActiveModeTransaction(
                reason: "dispose",
                committedRollbackMode: null,
                applyOutgoingSlotState: false);
        }

        _disposed = true;
        _viewerSlot.PropertyChanged -= OnSlotPropertyChanged;
        _editSlot.PropertyChanged -= OnSlotPropertyChanged;
        if (_modeRevealSignal is not null)
        {
            _modeRevealSignal.RevealCompleted -= OnModeRevealCompleted;
        }
        if (_transactionHost is not null)
        {
            _transactionHost.CommitCompleted -= OnTransactionCommitCompleted;
            _transactionHost.MinimapSettled -= OnTransactionMinimapSettled;
            _transactionHost.RendererSettled -= OnTransactionRendererSettled;
            _transactionHost.RendererFailed -= OnTransactionRendererFailed;
        }
        ReleaseModeRevealFallback();
        HideModeRevealCover();
        _modeRevealCoverWindow?.Dispose();
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}

internal sealed class ApplicateModeTransactionContext
{
    private ApplicateModeTransactionContext()
    {
    }

    public static readonly AttachedProperty<long> TransactionGenerationProperty =
        AvaloniaProperty.RegisterAttached<ApplicateModeTransactionContext, StyledElement, long>(
            "TransactionGeneration",
            defaultValue: 0,
            inherits: true);

    public static long GetTransactionGeneration(AvaloniaObject target)
        => target.GetValue(TransactionGenerationProperty);

    public static void SetTransactionGeneration(AvaloniaObject target, long generation)
        => target.SetValue(TransactionGenerationProperty, generation > 0 ? generation : 0);
}

internal readonly record struct ApplicateModeSlotState(
    bool IsVisible,
    bool IsInteractive,
    double Opacity);

internal readonly record struct ApplicateModeTransitionSnapshot(
    ApplicateMode DisplayedMode,
    ApplicateMode RequestedMode,
    long ActiveGeneration,
    bool IsSwitching,
    bool IsReadyToCommit,
    bool IsAborted,
    bool LayoutSettled,
    bool CommitCompleted,
    bool MinimapSettled,
    bool RendererSettled);

internal sealed class ApplicateModeTransitionController
{
    private long _nextGeneration;
    private long _activeGeneration;
    private ApplicateMode _displayedMode;
    private ApplicateMode _requestedMode;
    private bool _layoutSettled;
    private bool _commitCompleted;
    private bool _minimapSettled;
    private bool _rendererSettled;
    private bool _isAborted;

    public ApplicateModeTransitionController(ApplicateMode displayedMode)
    {
        _displayedMode = displayedMode;
        _requestedMode = displayedMode;
    }

    public ApplicateModeTransitionSnapshot Snapshot => new(
        _displayedMode,
        _requestedMode,
        _activeGeneration,
        _activeGeneration > 0,
        _activeGeneration > 0 && _layoutSettled && _commitCompleted && _minimapSettled && _rendererSettled,
        _isAborted,
        _layoutSettled,
        _commitCompleted,
        _minimapSettled,
        _rendererSettled);

    public long RequestMode(ApplicateMode requestedMode)
    {
        if (_activeGeneration > 0 && requestedMode == _requestedMode)
        {
            return _activeGeneration;
        }

        _requestedMode = requestedMode;
        _isAborted = false;

        if (requestedMode == _displayedMode)
        {
            ClearActiveTransaction();
            return 0;
        }

        _activeGeneration = checked(++_nextGeneration);
        _layoutSettled = false;
        _commitCompleted = false;
        _minimapSettled = false;
        _rendererSettled = false;
        return _activeGeneration;
    }

    public void ResetDisplayedMode(ApplicateMode displayedMode)
    {
        _displayedMode = displayedMode;
        _requestedMode = displayedMode;
        _isAborted = false;
        ClearActiveTransaction();
    }

    public bool ApplyLayoutSettled(long generation)
    {
        if (!IsActiveGeneration(generation))
        {
            return false;
        }

        _layoutSettled = true;
        return TryCommit();
    }

    public bool ApplyCommitCompleted(long generation, ApplicateMode mode)
    {
        if (!IsActiveGeneration(generation) || mode != _requestedMode)
        {
            return false;
        }

        _commitCompleted = true;
        return TryCommit();
    }

    public bool ApplyMinimapSettled(long generation)
    {
        if (!IsActiveGeneration(generation))
        {
            return false;
        }

        _minimapSettled = true;
        return TryCommit();
    }

    public bool ApplyRendererSettled(long generation)
    {
        if (!IsActiveGeneration(generation))
        {
            return false;
        }

        _rendererSettled = true;
        return TryCommit();
    }

    public bool ApplyRendererFailed(long generation)
    {
        if (!IsActiveGeneration(generation))
        {
            return false;
        }

        _isAborted = true;
        ClearActiveTransaction();
        return true;
    }

    public ApplicateModeSlotState GetSlotState(ApplicateMode mode)
    {
        if (_activeGeneration > 0)
        {
            if (mode == _displayedMode)
            {
                return new ApplicateModeSlotState(
                    IsVisible: true,
                    IsInteractive: false,
                    Opacity: 1.0);
            }

            if (mode == _requestedMode)
            {
                return new ApplicateModeSlotState(
                    IsVisible: true,
                    IsInteractive: false,
                    Opacity: 0.0);
            }
        }
        else if (mode == _displayedMode)
        {
            return new ApplicateModeSlotState(
                IsVisible: true,
                IsInteractive: true,
                Opacity: 1.0);
        }

        return new ApplicateModeSlotState(
            IsVisible: false,
            IsInteractive: false,
            Opacity: 0.0);
    }

    private bool IsActiveGeneration(long generation)
        => generation > 0 && generation == _activeGeneration;

    private bool TryCommit()
    {
        if (_activeGeneration == 0
            || !_layoutSettled
            || !_commitCompleted
            || !_minimapSettled
            || !_rendererSettled)
        {
            return false;
        }

        _displayedMode = _requestedMode;
        _isAborted = false;
        ClearActiveTransaction();
        return true;
    }

    private void ClearActiveTransaction()
    {
        _activeGeneration = 0;
        _layoutSettled = false;
        _commitCompleted = false;
        _minimapSettled = false;
        _rendererSettled = false;
    }
}
