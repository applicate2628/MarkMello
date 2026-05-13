using Avalonia.Controls;
using MarkMello.Applicate.Desktop.Views;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Owns the single application-wide WebView2-backed document view. The view is
/// created once and reparented between consumers (viewer surface, edit-mode
/// preview) via <see cref="Avalonia.Controls.NativeWebView.BeginReparenting"/>
/// so it stays warm across mode switches — no recreate, no reload, no flicker.
/// </summary>
public interface IApplicateSharedWebViewHost
{
    /// <summary>
    /// The shared WebView. Consumers subscribe to its events directly and must
    /// unsubscribe on detach. Never null after host construction.
    /// </summary>
    ApplicateWebMarkdownDocumentView View { get; }

    /// <summary>
    /// Reparent the shared view into <paramref name="target"/>. If currently
    /// attached to another panel, that detach + the new attach happen inside a
    /// single intentional-reparent scope so the underlying native adapter
    /// survives. A no-op if already attached to <paramref name="target"/>.
    /// </summary>
    void AttachTo(Panel target);

    /// <summary>
    /// Detach the shared view from <paramref name="from"/> only if it is the
    /// current parent. Used when a consumer is unmounted but no other consumer
    /// is taking over. Safe to call on a non-matching parent.
    /// </summary>
    void DetachFrom(Panel from);
}
