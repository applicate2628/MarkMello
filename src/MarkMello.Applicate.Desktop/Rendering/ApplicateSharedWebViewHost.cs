using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Views;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <inheritdoc cref="IApplicateSharedWebViewHost"/>
public sealed class ApplicateSharedWebViewHost : IApplicateSharedWebViewHost
{
    private Panel? _warmupParent;
    private Panel? _currentParent;

    public ApplicateSharedWebViewHost(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory shellAssetFactory)
    {
        View = new ApplicateWebMarkdownDocumentView(renderer, shellAssetFactory);
    }

    public ApplicateWebMarkdownDocumentView View { get; }

    public void SetWarmupParent(Panel parent)
    {
        if (ReferenceEquals(_warmupParent, parent))
        {
            return;
        }

        _warmupParent = parent;
        if (_currentParent is null)
        {
            parent.Children.Add(View);
            _currentParent = parent;
        }
    }

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

        // (Under sibling-mount v0.3.0+, the edit slot has real bounds from app
        // startup, so the warmup pre-resize hack from v0.2.x is unnecessary.)

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

        // Return to the warmup parent rather than fully unparenting so the
        // adapter and document stay alive for the next consumer.
        if (_warmupParent is not null && !ReferenceEquals(_warmupParent, from))
        {
            using var scope = View.BeginIntentionalReparent();
            from.Children.Remove(View);
            _warmupParent.Children.Add(View);
            _currentParent = _warmupParent;
            return;
        }

        from.Children.Remove(View);
        _currentParent = null;
    }
}
