using System;
using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
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
    private volatile bool _disposed;
    private int _reconcilePending;

    public ApplicateSiblingMountBridge(
        INotifyPropertyChanged vm,
        ContentControl viewerSlot,
        Panel editSlot,
        Control editContent,
        Func<bool> getIsViewer,
        Func<bool> getIsEditMode,
        Func<object?> getEditorSession,
        Func<object?> getDocument,
        object viewerContent)
    {
        _vm = vm;
        _viewerSlot = viewerSlot;
        _editSlot = editSlot;
        _editContent = editContent;
        _getIsViewer = getIsViewer;
        _getIsEditMode = getIsEditMode;
        _getEditorSession = getEditorSession;
        _getDocument = getDocument;
        _viewerSlot.Content = viewerContent;

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
    }

    internal void ForceReconcile() => MarshalReconcile();

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
            or nameof(MainWindowViewModel.Document)))
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

        // Triple-gate: viewer mode AND not editing AND document still exists.
        // The `document is not null` clause closes the close-file parasitic-
        // frame window. IsViewer is a derived property on State and fires
        // LAST in the close-file sequence; without the document gate the
        // viewer slot would flash visible on Tick 1 (IsEditMode=false) with
        // a stale document still painted.
        var viewerVisible = isViewer && !isEdit && document is not null;
        var editVisible = isViewer && isEdit && session is not null;

        ApplicateTrace.ModeToggle(
            $"Reconcile in: isViewer={isViewer} isEdit={isEdit} session={(session is not null)} document={(document is not null)} -> viewerVis={viewerVisible} editVis={editVisible}");

        ApplySlotState(_viewerSlot, viewerVisible);
        ApplySlotState(_editSlot, editVisible);

        // Drive editContent.Opacity together with editSlot.IsVisible so the
        // Avalonia chrome (source pane, toolbar) fades in/out at the same
        // pace as the native WebView2 HWND's NativeControlHost cascade.
        // The opacity transition is configured on editWorkspace at
        // construction time (Duration=MmDurationStandard, EasingStandard).
        _editContent.Opacity = editVisible ? 1.0 : 0.0;

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

    // IsHitTestVisible gates click/wheel/drag-drop independently of IsEnabled.
    // Native WebView2 HWND may receive Win32 input chain events even when
    // Avalonia thinks the parent is "disabled". Hit-test is the explicit gate.
    private static void ApplySlotState(Control slot, bool visible)
    {
        slot.IsVisible = visible;
        slot.IsEnabled = visible;
        slot.IsHitTestVisible = visible;
        slot.IsTabStop = visible;
        slot.Focusable = visible;
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
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
