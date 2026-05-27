using System;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    private readonly Panel? _modeRevealCoverHost;
    private readonly Popup? _modeRevealCoverPopup;
    private readonly Image? _modeRevealCover;
    private static readonly TimeSpan ModeRevealCoverFallbackTimeout = TimeSpan.FromMilliseconds(650);
    private volatile bool _disposed;
    private int _reconcilePending;
    private int _modeRevealCoverContinuationPending;
    private bool _desiredViewerVisible;
    private bool _desiredEditVisible;
    private bool _modeRevealPending;
    private bool _modeRevealCompleting;
    private bool _modeRevealCoverArmed;
    private Bitmap? _modeRevealCoverBitmap;
    private DispatcherTimer? _modeRevealFallbackTimer;

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
        _modeRevealCoverHost = modeRevealCoverHost;
        _viewerSlot.Content = viewerContent;
        if (_modeRevealCoverHost is not null)
        {
            _modeRevealCover = new Image
            {
                ClipToBounds = true,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
                IsVisible = false,
                Opacity = 1.0,
                Stretch = Stretch.Fill,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            _modeRevealCoverPopup = new Popup
            {
                PlacementTarget = _modeRevealCoverHost,
                Placement = PlacementMode.Center,
                ShouldUseOverlayLayer = false,
                IsLightDismissEnabled = false,
                OverlayDismissEventPassThrough = true,
                Topmost = false,
                Focusable = false,
                Child = _modeRevealCover
            };
            _modeRevealCoverHost.Children.Add(_modeRevealCoverPopup);
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
    }

    internal void ForceReconcile() => MarshalReconcile();

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
        if (_modeRevealCoverHost is null || _modeRevealCover is null || _modeRevealCoverPopup is null)
        {
            return false;
        }

        var bitmap = ApplicateModeTransitionCapture.TryCapture(_modeRevealCoverHost);
        if (bitmap is null)
        {
            return false;
        }

        var oldBitmap = _modeRevealCoverBitmap;
        _modeRevealCoverBitmap = bitmap;
        _modeRevealCover.Transitions = null;
        _modeRevealCover.Source = bitmap;
        _modeRevealCover.Width = _modeRevealCoverHost.Bounds.Width;
        _modeRevealCover.Height = _modeRevealCoverHost.Bounds.Height;
        _modeRevealCover.Opacity = 1.0;
        _modeRevealCover.IsVisible = true;
        _modeRevealCoverPopup.PlacementTarget = _modeRevealCoverHost;
        _modeRevealCoverPopup.IsOpen = true;
        _modeRevealCoverHost.UpdateLayout();
        oldBitmap?.Dispose();
        ApplicateTrace.DiagMs(
            "pane-seq",
            "bridge-cover-visible",
            $"surface=popup popupOpen={_modeRevealCoverPopup.IsOpen} hostBounds={_modeRevealCoverHost.Bounds.Width:F0}x{_modeRevealCoverHost.Bounds.Height:F0}");
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
        if (_modeRevealCover is null)
        {
            return;
        }

        if (_modeRevealCoverPopup is not null)
        {
            _modeRevealCoverPopup.IsOpen = false;
        }

        _modeRevealCover.IsVisible = false;
        _modeRevealCover.Source = null;
        _modeRevealCoverBitmap?.Dispose();
        _modeRevealCoverBitmap = null;
        ApplicateTrace.DiagMs("pane-seq", "bridge-cover-hidden");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _viewerSlot.PropertyChanged -= OnSlotPropertyChanged;
        _editSlot.PropertyChanged -= OnSlotPropertyChanged;
        if (_modeRevealSignal is not null)
        {
            _modeRevealSignal.RevealCompleted -= OnModeRevealCompleted;
        }
        ReleaseModeRevealFallback();
        HideModeRevealCover();
        if (_modeRevealCoverPopup is not null)
        {
            _modeRevealCoverPopup.IsOpen = false;
            _modeRevealCoverPopup.Child = null;
        }
        if (_modeRevealCoverHost is not null && _modeRevealCoverPopup is not null)
        {
            _modeRevealCoverHost.Children.Remove(_modeRevealCoverPopup);
        }
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
