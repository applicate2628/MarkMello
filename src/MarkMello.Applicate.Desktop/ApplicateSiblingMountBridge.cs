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
    private readonly IApplicateModeRevealSession? _modeRevealSession;
    private volatile bool _disposed;
    private int _reconcilePending;
    private bool _desiredViewerVisible;
    private bool _desiredEditVisible;

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
        Func<IApplicateModeTransitionSlotAdapter, IApplicateModeRevealSession>? modeRevealSessionFactory = null)
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
        _viewerSlot.Content = viewerContent;
        _modeRevealSession = modeRevealSessionFactory?.Invoke(new ModeTransitionSlotAdapter(this));
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
        _modeRevealSession?.TryApplyLayoutSettledForActiveTransaction();
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
        if (TryReconcileTransactionalModeSwitch(
            viewerVisible,
            editVisible,
            modeSlotSwitch,
            session))
        {
            return;
        }

        _desiredViewerVisible = viewerVisible;
        _desiredEditVisible = editVisible;

        ApplicateTrace.ModeToggle(
            $"Reconcile in: isViewer={isViewer} isEdit={isEdit} session={(session is not null)} document={(document is not null)} -> viewerVis={viewerVisible} editVis={editVisible}");
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-reconcile-enter",
            $"viewerVis={viewerVisible} editVis={editVisible} editSlotPrev={_editSlot.IsVisible}");

        var viewerPaintVisible = viewerVisible;
        var editPaintVisible = editVisible;
        ApplySlotState(
            _viewerSlot,
            viewerVisible,
            viewerVisible,
            viewerPaintVisible ? 1.0 : 0.0,
            false);
        ApplySlotState(
            _editSlot,
            editVisible,
            editVisible,
            editPaintVisible ? 1.0 : 0.0,
            false);
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-edit-slot-applied",
            $"editSlotIsVisible={_editSlot.IsVisible} editSlotBounds={_editSlot.Bounds.Width:F0}x{_editSlot.Bounds.Height:F0}");

        // Edit -> reading does not keep edit painted: the live edit layer
        // contains the splitter and preview chrome that produced the visible
        // between-frame residue. The transactional mode-switch path owns any
        // cross-fade hold; this tail runs only for non-transitional reconciles.
        ApplyOpacity(_editContent, editPaintVisible ? 1.0 : 0.0, false);

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
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        ApplicateTrace.ModeToggle($"Reconcile out: elapsed={elapsedMs:F2}ms sessionChanged={sessionChanged}");
    }

    private bool TryReconcileTransactionalModeSwitch(
        bool viewerVisible,
        bool editVisible,
        bool modeSlotSwitch,
        object? session)
    {
        if (_modeRevealSession is null)
        {
            return false;
        }

        var requestedMode = viewerVisible
            ? ApplicateMode.Viewer
            : editVisible
                ? ApplicateMode.Edit
                : (ApplicateMode?)null;
        var handled = _modeRevealSession.TryReconcile(requestedMode, modeSlotSwitch);
        if (handled
            && requestedMode == ApplicateMode.Edit
            && !ReferenceEquals(_editContent.DataContext, session))
        {
            _editContent.DataContext = session;
        }

        return handled;
    }

    private void ApplyCommittedModeSlotStates(ApplicateMode mode)
    {
        _desiredViewerVisible = mode == ApplicateMode.Viewer;
        _desiredEditVisible = mode == ApplicateMode.Edit;
        var viewerVisible = mode == ApplicateMode.Viewer;
        var editVisible = mode == ApplicateMode.Edit;
        ApplySlotState(_viewerSlot, viewerVisible, viewerVisible, viewerVisible ? 1.0 : 0.0, instant: true);
        ApplySlotState(_editSlot, editVisible, editVisible, editVisible ? 1.0 : 0.0, instant: true);
        ApplyOpacity(_editContent, editVisible ? 1.0 : 0.0, instant: true);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _modeRevealSession?.Dispose();
        _disposed = true;
        _viewerSlot.PropertyChanged -= OnSlotPropertyChanged;
        _editSlot.PropertyChanged -= OnSlotPropertyChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private sealed class ModeTransitionSlotAdapter(ApplicateSiblingMountBridge bridge)
        : IApplicateModeTransitionSlotAdapter
    {
        private readonly ApplicateSiblingMountBridge _bridge = bridge;

        public void ApplyTransactionGenerationContext(ApplicateMode requestedMode, long generation)
            => _bridge.ApplyTransactionGenerationContext(requestedMode, generation);

        public void ClearTransactionGenerationContext()
            => _bridge.ClearTransactionGenerationContext();

        public void ApplyTransactionalModeState(
            ApplicateMode requestedMode,
            ApplicateModeSlotState viewer,
            ApplicateModeSlotState edit)
        {
            _bridge._desiredViewerVisible = requestedMode == ApplicateMode.Viewer;
            _bridge._desiredEditVisible = requestedMode == ApplicateMode.Edit;
            ApplySlotState(_bridge._viewerSlot, viewer.IsVisible, viewer.IsInteractive, viewer.Opacity, instant: true);
            ApplySlotState(_bridge._editSlot, edit.IsVisible, edit.IsInteractive, edit.Opacity, instant: true);
            ApplyOpacity(_bridge._editContent, edit.Opacity > 0 ? 1.0 : 0.0, instant: true);
        }

        public void ApplyCommittedModeState(ApplicateMode mode, bool applySlotState)
        {
            _bridge._desiredViewerVisible = mode == ApplicateMode.Viewer;
            _bridge._desiredEditVisible = mode == ApplicateMode.Edit;
            if (applySlotState)
            {
                _bridge.ApplyCommittedModeSlotStates(mode);
            }
        }

        public bool IsModeSlotLayoutSettled(ApplicateMode mode)
        {
            var target = mode == ApplicateMode.Viewer
                ? (Control)_bridge._viewerSlot
                : _bridge._editSlot;
            return target.IsVisible && target.Bounds.Width > 0 && target.Bounds.Height > 0;
        }

        public void ReconcileModeTransition()
            => _bridge.MarshalReconcile();
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
