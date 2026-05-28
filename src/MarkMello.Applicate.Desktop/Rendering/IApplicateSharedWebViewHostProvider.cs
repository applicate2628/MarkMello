namespace MarkMello.Applicate.Desktop.Rendering;

public interface IApplicateSharedWebViewHostProvider
{
    IApplicateSharedWebViewHost ViewerHost { get; }

    IApplicateSharedWebViewHost EditPreviewHost { get; }
}
