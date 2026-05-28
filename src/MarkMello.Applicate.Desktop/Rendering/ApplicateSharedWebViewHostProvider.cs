using System;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateSharedWebViewHostProvider : IApplicateSharedWebViewHostProvider, IDisposable
{
    public ApplicateSharedWebViewHostProvider(
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicateShellAssetBundleFactory shellAssetFactory)
    {
        ViewerHost = new ApplicateSharedWebViewHost(renderer, shellAssetFactory);
        EditPreviewHost = new ApplicateSharedWebViewHost(renderer, shellAssetFactory);
    }

    public IApplicateSharedWebViewHost ViewerHost { get; }

    public IApplicateSharedWebViewHost EditPreviewHost { get; }

    public void Dispose()
    {
        ViewerHost.View.Dispose();
        EditPreviewHost.View.Dispose();
    }
}
