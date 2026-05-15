using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace MarkMello.Applicate.Desktop.Views;

// Thin wrapper around Avalonia.Controls.NativeWebView that swallows the
// ArgumentException raised when ICoreWebView2Controller.MoveFocus() returns
// E_INVALIDARG (HRESULT 0x80070057). This happens on WindowBase.HandleActivated
// → FocusManager.Focus → OnGotFocus → WebView2BaseAdapter.Focus → MoveFocus,
// when the controller is in a transitional state (HWND not yet visible after
// reparent, controller mid-init, or window not active when the call lands).
// Failing MoveFocus is non-fatal for our use — focus stays on the Avalonia
// side, and the WebView regains keyboard control on the next click.
internal sealed class ApplicateNativeWebView : NativeWebView
{
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        try
        {
            base.OnGotFocus(e);
        }
        catch (ArgumentException)
        {
            // MoveFocus rejected — controller not ready. Suppress.
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        try
        {
            base.OnLostFocus(e);
        }
        catch (ArgumentException)
        {
            // Symmetry with OnGotFocus — MoveFocus on the inactivate side can fail the same way.
        }
    }
}
