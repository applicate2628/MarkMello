using MarkMello.Application.Abstractions;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Bridges the application-layer <see cref="IRendererReadinessService"/>
/// contract to the Applicate-side WebView shell-ready signal owned by
/// <see cref="IApplicateSharedWebViewHost"/>.
///
/// <para>The cache-hit fast-path in
/// <c>MainWindowViewModel.LoadDocumentAsync</c> must await this readiness
/// signal BEFORE publishing Document / State changes; otherwise the renderer
/// pipeline starts mid-load and a later session-restoration / edit-mode
/// reconcile reparents the WebView while it is still loading the document,
/// stalling the initial-visible-ready pipeline by 10+ seconds (D-phase
/// cache-hit race).</para>
/// </summary>
public sealed class ApplicateRendererReadinessService : IRendererReadinessService
{
    private readonly IApplicateSharedWebViewHost _host;

    public ApplicateRendererReadinessService(IApplicateSharedWebViewHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc/>
    public Task WaitReadyAsync(CancellationToken cancellationToken = default)
        => _host.WaitForShellReadyAsync(cancellationToken);
}
