using System;
using System.ComponentModel;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop;

internal sealed class ApplicateSiblingMountBridge : IDisposable
{
    private readonly INotifyPropertyChanged _vm;
    private readonly ContentControl _viewerSlot;
    private readonly ContentControl _editSlot;
    private readonly Func<bool> _getIsViewer;
    private readonly Func<bool> _getIsEditMode;
    private readonly Func<object?> _getEditorSession;
    private readonly Func<object?> _getDocument;
    private volatile bool _disposed;
    private int _reconcilePending;

    public ApplicateSiblingMountBridge(
        INotifyPropertyChanged vm,
        ContentControl viewerSlot,
        ContentControl editSlot,
        Func<bool> getIsViewer,
        Func<bool> getIsEditMode,
        Func<object?> getEditorSession,
        Func<object?> getDocument,
        object viewerContent)
    {
        _vm = vm;
        _viewerSlot = viewerSlot;
        _editSlot = editSlot;
        _getIsViewer = getIsViewer;
        _getIsEditMode = getIsEditMode;
        _getEditorSession = getEditorSession;
        _getDocument = getDocument;
        _viewerSlot.Content = viewerContent;

        _vm.PropertyChanged += OnVmPropertyChanged;
        Reconcile();
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

        ApplySlotState(_viewerSlot, viewerVisible);
        ApplySlotState(_editSlot, editVisible);

        // Sticky session: clear edit slot Content only when EditorSession
        // becomes null (document closed). Mode-toggle from edit→reader keeps
        // Content pointing at the last session, so the inner ApplicateEdit-
        // PreviewView never sees a DataContext=null event and never tears
        // down its shared-host attachment across visibility flips.
        if (session is null)
        {
            _editSlot.Content = null;
        }
        else if (!ReferenceEquals(_editSlot.Content, session))
        {
            _editSlot.Content = session;
        }
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
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
