using MarkMello.Application.Abstractions;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Bridges the application-layer <see cref="IRendererReadinessService"/>
/// contract to Applicate-side startup and WebView shell readiness signals.
///
/// <para>Reader startup cache hits only need the Applicate window structure to
/// be installed before Document/State publication; the WebView render path can
/// then wait for shell-ready internally. Edit-preserving cache hits still need
/// the stronger shell-ready rendezvous to avoid the D-phase reparent race.</para>
/// </summary>
public sealed class ApplicateRendererReadinessService : IRendererReadinessService
{
    private readonly IApplicateSharedWebViewHost _host;
    private readonly TaskCompletionSource _startupDocumentPublishReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ApplicateRendererReadinessService(IApplicateSharedWebViewHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <inheritdoc/>
    public Task WaitStartupDocumentPublishReadyAsync(CancellationToken cancellationToken = default)
        => _startupDocumentPublishReady.Task.WaitAsync(cancellationToken);

    public void MarkStartupDocumentPublishReady()
        => _startupDocumentPublishReady.TrySetResult();

    /// <inheritdoc/>
    public Task WaitReadyAsync(CancellationToken cancellationToken = default)
        => _host.WaitForShellReadyAsync(cancellationToken);
}
