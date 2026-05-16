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
            return;
        }

        if (_currentParent is null)
        {
            target.Children.Add(View);
            _currentParent = target;
            System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo first-attach");
            return;
        }

        // Single-shot reparent at app startup: View moves from warmup parent
        // to the permanent EditPreview._webSlot. EditPreview's mount happens
        // while editSlot.IsVisible=false (reader is the initial mode), so the
        // ~150ms HWND-geometry-lag window is hidden from the user — the
        // cascade from editSlot.IsVisible=false → NativeControlHost →
        // SetWindowPos(SWP_HIDEWINDOW) on the WebView2 HWND has already run.
        // BeginIntentionalReparent keeps the WebView2 controller, DOM, scroll
        // and viewport state alive across Children.Remove + Children.Add.
        System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo (one-time): warmup.Bounds={(_warmupParent is null ? "(null)" : _warmupParent.Bounds.ToString())} target.Bounds={target.Bounds}");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (View.BeginIntentionalReparent())
        {
            _currentParent.Children.Remove(View);
            target.Children.Add(View);
            _currentParent = target;
        }
        var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        System.Console.Error.WriteLine($"[mode-toggle] {System.DateTime.Now:HH:mm:ss.fff} SharedHost.AttachTo done elapsed={elapsedMs:F2}ms");
    }

    public void DetachFrom(Panel from)
    {
        // Permanent-mount architecture: DetachFrom is a no-op. The View
        // lives in EditPreview._webSlot for the lifetime of the app and
        // hides/shows via editSlot.IsVisible cascade (verified by pre-flight
        // probe — Avalonia NativeControlHost issues SetWindowPos(SWP_HIDE)
        // on parent IsVisible=false within one Loaded dispatcher tick). The
        // earlier return-to-warmup path produced the bug it claimed to
        // mitigate: every enter-edit re-reparented and paid the 154ms HWND
        // geometry-lag tax.
    }
}
