using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Views;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Owns the single application-wide WebView2-backed document view.
///
/// To keep the underlying native HWND warm without ever showing its load state
/// to the user, the host parks the view in an offscreen "warmup" panel
/// supplied via <see cref="SetWarmupParent"/>. While parked the view has real
/// bounds so the renderer initialises correctly; only <c>Margin</c> pushes the
/// HWND offscreen (verified empirically by the scratch smoke at
/// <c>.scratch/webview-smoke/run.out.txt</c> — MarkMello renderer gates fired
/// with a 640x360 viewport while parked at <c>Margin=-5000</c>, no visible
/// leak per <c>.scratch/webview-smoke/visual-offscreen-screen.png</c>).
///
/// When a consumer (viewer surface, edit-mode preview) wants to show the
/// rendered document it calls <see cref="AttachTo"/>; the view is reparented
/// into the consumer panel via
/// <see cref="Avalonia.Controls.NativeWebView.BeginReparenting"/> so the
/// adapter, DOM, scroll, and viewport survive. <see cref="DetachFrom"/>
/// returns the view to the warmup panel rather than destroying it, keeping
/// it warm for the next consumer.
/// </summary>
public interface IApplicateSharedWebViewHost
{
    /// <summary>
    /// The shared WebView. Consumers subscribe to its events directly and must
    /// unsubscribe on detach. Never null after host construction.
    /// </summary>
    ApplicateWebMarkdownDocumentView View { get; }

    /// <summary>
    /// Register the offscreen warmup panel. Called once at app startup by the
    /// fork-owned main window. The view is mounted into this panel
    /// immediately so its WebView2 adapter can initialise without showing the
    /// load state to the user.
    /// </summary>
    void SetWarmupParent(Panel parent);

    /// <summary>
    /// Reparent the shared view into <paramref name="target"/>. If currently
    /// attached to the warmup parent or another consumer panel, the detach +
    /// new attach happen inside a single intentional-reparent scope so the
    /// underlying native adapter survives. A no-op if already attached to
    /// <paramref name="target"/>.
    /// </summary>
    void AttachTo(Panel target);

    /// <summary>
    /// Return the shared view to the warmup panel if it currently sits in
    /// <paramref name="from"/>. Safe to call on a non-matching panel.
    /// </summary>
    void DetachFrom(Panel from);
}
