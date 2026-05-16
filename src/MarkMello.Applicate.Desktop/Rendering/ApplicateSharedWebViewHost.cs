using Avalonia;
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
        View.PropertyChanged += OnViewPropertyChanged;
    }

    private static void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.BoundsProperty && e.Property != Visual.IsVisibleProperty)
        {
            return;
        }
        System.Console.Error.WriteLine(
            $"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.View.{e.Property.Name}: {e.OldValue} -> {e.NewValue}");
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
            System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo noop (already target)");
            return;
        }

        if (_currentParent is null)
        {
            target.Children.Add(View);
            _currentParent = target;
            System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo first-attach");
            return;
        }

        System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo enter: warmup.Bounds={(_warmupParent is null ? "(null)" : _warmupParent.Bounds.ToString())} target.Bounds={target.Bounds} View.Bounds={View.Bounds}");

        // Native HWND paint races outside Avalonia layout. The reparent
        // itself takes ~3-7ms but the wrapper Bounds → native HWND
        // SetWindowPos chain only converges at Avalonia's NEXT layout
        // pass — observed as 30-80ms of View.Bounds=1024×768 (warmup
        // size) after AttachTo done, until Bounds finally settles to
        // 707×927. During that window the HWND would paint the new
        // document at the WRONG size at the new parent position. The
        // Avalonia mask Border cannot cover a native HWND (Win32 child
        // window z-order). Hide the HWND outright across the reparent
        // → resize → show sequence so no stale-size paint is ever
        // visible. SetNativeWebViewVisibility maps to the upstream
        // NativeWebView.IsVisible setter, which hides the HWND via
        // SetWindowPos(SWP_HIDEWINDOW) synchronously.
        View.SetNativeWebViewVisibility(false);

        // Resize View explicitly to target bounds BEFORE reparenting.
        // Setting Width/Height directly bypasses the parent-stretch path
        // (warmup parent's Width/Height set + UpdateLayout was observed
        // not to propagate Bounds during the same dispatcher tick — the
        // Margin=-5000 offscreen anchor may freeze the layout cycle).
        // After reparent the explicit size matches the target slot, so
        // the first paint at the new parent already has correct bounds.
        if (target.Bounds.Width > 0 && target.Bounds.Height > 0)
        {
            View.Width = target.Bounds.Width;
            View.Height = target.Bounds.Height;
        }

        // Intentional reparent: BeginReparenting tells Avalonia.NativeWebView to
        // keep the native adapter alive across the detach-then-attach pair so
        // the WebView2 instance, DOM, scroll, and viewport survive.
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (View.BeginIntentionalReparent())
        {
            _currentParent.Children.Remove(View);
            target.Children.Add(View);
            _currentParent = target;
        }

        // Force a layout pass on the new parent. Even with this, Avalonia's
        // NativeControlHost wrapper Bounds → native HWND SetWindowPos chain
        // appears to lag by one dispatcher tick — observed 80-90ms between
        // AttachTo done and View.Bounds settling to target.Bounds. Reveal
        // the HWND from a Background-priority Post so the SetWindowPos that
        // resizes the HWND to the wrapper bounds has time to run before
        // the user sees the HWND on the new parent. Without this defer,
        // the HWND flashes at warmup size (1024×768) at the editSlot's
        // top-left for ~80ms, visible as a stretched-content frame.
        target.UpdateLayout();

        // Clear explicit Width/Height now that bounds are committed from
        // layout; the View should return to stretch behaviour so subsequent
        // slot resizes (window resize, splitter drag) re-flow naturally.
        View.Width = double.NaN;
        View.Height = double.NaN;

        // Reveal HWND the instant Avalonia commits the wrapper Bounds for
        // the new parent — that is the moment NativeControlHost has issued
        // SetWindowPos to resize the HWND to the new wrapper size. Doing
        // it on a Dispatcher.Post(Loaded) was 80-100ms slower because the
        // Loaded tick happens later than the bounds-commit tick. One-shot
        // subscription unhooks itself after the first qualifying change
        // so subsequent layout passes do not re-fire the show.
        if (target.Bounds.Width > 0
            && target.Bounds.Height > 0
            && View.Bounds.Width.Equals(target.Bounds.Width)
            && View.Bounds.Height.Equals(target.Bounds.Height))
        {
            // Bounds already match (rare — e.g. target same size as warmup);
            // no Bounds change will fire, so show immediately.
            View.SetNativeWebViewVisibility(true);
        }
        else
        {
            void OnBoundsCommit(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property != Visual.BoundsProperty)
                {
                    return;
                }
                var newBounds = (Rect)(e.NewValue ?? default(Rect));
                if (newBounds.Width <= 0 || newBounds.Height <= 0)
                {
                    return;
                }
                View.PropertyChanged -= OnBoundsCommit;
                View.SetNativeWebViewVisibility(true);
            }
            View.PropertyChanged += OnBoundsCommit;
        }

        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo done elapsed={elapsedMs:F2}ms View.Bounds={View.Bounds} (HWND show on next Bounds commit)");
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
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            using var scope = View.BeginIntentionalReparent();
            from.Children.Remove(View);
            _warmupParent.Children.Add(View);
            _currentParent = _warmupParent;
            var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.DetachFrom reparent-to-warmup elapsed={elapsedMs:F2}ms");
            return;
        }

        from.Children.Remove(View);
        _currentParent = null;
        System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.DetachFrom unparent");
    }
}
