namespace MarkMello.Applicate.Desktop.Rendering;

public interface IApplicateShellAssetBundleFactory
{
    Task<ApplicateShellAssetBundle> GetAsync(CancellationToken cancellationToken);
}

public sealed record ApplicateShellAssetBundle(
    ApplicateWebBaseAssets Base,
    ApplicateWebMermaidAssets Mermaid,
    ApplicateWebHighlightAssets Highlight);
