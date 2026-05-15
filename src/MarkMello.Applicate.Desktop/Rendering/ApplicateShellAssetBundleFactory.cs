namespace MarkMello.Applicate.Desktop.Rendering;

// Caches the once-per-process always-on triple-asset bundle. The
// renderer-shell page always includes KaTeX + Mermaid + hljs because the
// shell stays navigated for the lifetime of the WebView and the CSP forbids
// injecting new <script> tags through the body innerHTML swap. Subsequent
// load-document calls reuse the same shell, so loading the assets once
// per process is enough.
public sealed class ApplicateShellAssetBundleFactory : IApplicateShellAssetBundleFactory, IDisposable
{
    private readonly ApplicateWebAssetEmbedder _embedder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ApplicateShellAssetBundle? _cached;

    public ApplicateShellAssetBundleFactory(ApplicateWebAssetEmbedder embedder)
    {
        _embedder = embedder;
    }

    public void Dispose() => _lock.Dispose();

    public async Task<ApplicateShellAssetBundle> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var baseAssets = await _embedder.LoadBaseBundleAsync(cancellationToken).ConfigureAwait(false);
            var mermaid = await _embedder.LoadMermaidAsync(cancellationToken).ConfigureAwait(false);
            var hljs = await _embedder.LoadHighlightAsync(cancellationToken).ConfigureAwait(false);
            _cached = new ApplicateShellAssetBundle(baseAssets, mermaid, hljs);
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }
}
