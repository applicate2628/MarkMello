namespace MarkMello.Application.Abstractions;

/// <summary>
/// Signals when the WebView-based renderer is fully ready to accept document
/// content without race conditions. Used by the document-load fast-path
/// (e.g., EarlyDocumentCache hit) to wait for shell-ready before publishing
/// Document/State changes that would otherwise trigger renderer pipeline
/// activity before the WebView2 environment + shell HTML are loaded.
/// </summary>
public interface IRendererReadinessService
{
    /// <summary>
    /// Completes when the renderer shell is loaded and ready to accept a
    /// document. Returns a completed task if already ready. Safe to call
    /// from any thread; await on UI thread when consuming the result.
    /// </summary>
    Task WaitReadyAsync(CancellationToken cancellationToken = default);
}
