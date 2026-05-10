using System.Text.RegularExpressions;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateWebAssetEmbedder
{
    private static readonly Regex CssUrlRegex = new(
        """url\((?<quote>['"]?)(?<path>fonts/[^)'"]+)\k<quote>\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _assetsRoot;

    public ApplicateWebAssetEmbedder()
        : this(Path.Combine(AppContext.BaseDirectory, "RendererWeb", "assets"))
    {
    }

    internal ApplicateWebAssetEmbedder(string assetsRoot)
    {
        _assetsRoot = assetsRoot;
    }

    public string AssetsRoot => _assetsRoot;

    public async Task<ApplicateWebAssetBundle> LoadBundleAsync(CancellationToken cancellationToken)
    {
        var rendererCss = await ReadTextAssetAsync("renderer.css", cancellationToken).ConfigureAwait(false);
        var rendererScript = await ReadTextAssetAsync("renderer.js", cancellationToken).ConfigureAwait(false);
        var katexCss = await ReadTextAssetAsync("katex/katex.min.css", cancellationToken).ConfigureAwait(false);
        var katexScript = await ReadTextAssetAsync("katex/katex.min.js", cancellationToken).ConfigureAwait(false);

        return new ApplicateWebAssetBundle(
            rendererCss,
            await InlineKatexFontsAsync(katexCss, cancellationToken).ConfigureAwait(false),
            katexScript,
            rendererScript);
    }

    public async Task<string> ReadTextAssetAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = ResolveKnownAssetPath(relativePath);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadBinaryAssetAsync(string relativePath, CancellationToken cancellationToken)
    {
        var path = ResolveKnownAssetPath(relativePath);
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveKnownAssetPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new ArgumentException("Asset path must be a safe relative path.", nameof(relativePath));
        }

        var root = Path.GetFullPath(_assetsRoot);
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Asset path escapes the renderer asset root.", nameof(relativePath));
        }

        return candidate;
    }

    private async Task<string> InlineKatexFontsAsync(string css, CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in CssUrlRegex.Matches(css))
        {
            var path = match.Groups["path"].Value.Replace('\\', '/');
            if (replacements.ContainsKey(path))
            {
                continue;
            }

            var bytes = await ReadBinaryAssetAsync($"katex/{path}", cancellationToken).ConfigureAwait(false);
            replacements[path] = $"data:{GetFontMimeType(path)};base64,{Convert.ToBase64String(bytes)}";
        }

        return CssUrlRegex.Replace(
            css,
            match =>
            {
                var path = match.Groups["path"].Value.Replace('\\', '/');
                return replacements.TryGetValue(path, out var dataUri)
                    ? $"url({dataUri})"
                    : match.Value;
            });
    }

    private static string GetFontMimeType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream"
        };
    }
}

public sealed record ApplicateWebAssetBundle(
    string RendererCss,
    string KatexCss,
    string KatexScript,
    string RendererScript)
{
    public static ApplicateWebAssetBundle Empty { get; } = new(
        RendererCss: string.Empty,
        KatexCss: string.Empty,
        KatexScript: string.Empty,
        RendererScript: string.Empty);
}
