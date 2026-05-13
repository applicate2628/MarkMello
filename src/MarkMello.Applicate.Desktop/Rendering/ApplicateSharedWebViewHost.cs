using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Views;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <inheritdoc cref="IApplicateSharedWebViewHost"/>
public sealed class ApplicateSharedWebViewHost : IApplicateSharedWebViewHost
{
    private Panel? _currentParent;

    public ApplicateSharedWebViewHost(IApplicateHtmlMarkdownRenderer renderer)
    {
        View = new ApplicateWebMarkdownDocumentView(renderer);
    }

    public ApplicateWebMarkdownDocumentView View { get; }

    public void AttachTo(Panel target)
    {
        if (ReferenceEquals(_currentParent, target))
        {
            return;
        }

        if (_currentParent is null)
        {
            target.Children.Add(View);
            _currentParent = target;
            return;
        }

        // Intentional reparent: BeginReparenting tells Avalonia.NativeWebView to
        // keep the native adapter alive across the detach-then-attach pair so
        // the WebView2 instance, DOM, scroll, and viewport survive. Verified by
        // Codex scratch smoke (adapterCreated=1, adapterDestroyed=0 after Grid
        // move; see .scratch/webview-smoke/run.out.txt).
        using var scope = View.BeginIntentionalReparent();
        _currentParent.Children.Remove(View);
        target.Children.Add(View);
        _currentParent = target;
    }

    public void DetachFrom(Panel from)
    {
        if (!ReferenceEquals(_currentParent, from))
        {
            return;
        }

        _currentParent.Children.Remove(View);
        _currentParent = null;
    }
}
