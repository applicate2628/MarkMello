namespace MarkMello.Application.Abstractions;

/// <summary>
/// Signals when the WebView-based renderer and Applicate shell are ready enough
/// for document-load fast paths (e.g., EarlyDocumentCache hit) to publish
/// Document/State changes without racing startup slot installation or the
/// renderer shell.
/// </summary>
public interface IRendererReadinessService
{
    /// <summary>
    /// Completes when the desktop shell has installed the viewer/edit slots and
    /// startup reveal gate. Reader startup cache hits can publish after this
    /// point while the WebView shell continues warming in parallel.
    /// </summary>
    Task WaitStartupDocumentPublishReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes when the renderer shell is loaded and ready to accept a
    /// document. Edit-preserving cache hits use this stronger gate to avoid
    /// WebView reparent races while edit mode is active or being restored.
    /// </summary>
    Task WaitReadyAsync(CancellationToken cancellationToken = default);
}
