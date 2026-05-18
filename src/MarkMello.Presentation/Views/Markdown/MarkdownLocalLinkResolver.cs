namespace MarkMello.Presentation.Views.Markdown;

public enum MarkdownLocalLinkKind
{
    MarkdownDocument,
    ExternalFile
}

public readonly record struct MarkdownLocalLinkTarget(MarkdownLocalLinkKind Kind, string Path);

public static class MarkdownLocalLinkResolver
{
    private static readonly string[] MarkdownLinkExtensions =
        { ".md", ".markdown", ".mdown", ".markdn", ".txt" };

    public static bool TryResolve(
        string href,
        string? sourcePath,
        Func<string, bool> fileExists,
        out MarkdownLocalLinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        target = default;
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        string candidate;
        var trimmed = href.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return false;
            }

            candidate = uri.LocalPath;
        }
        else
        {
            candidate = trimmed;
        }

        candidate = StripLocalLinkDecoration(candidate);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            candidate = Uri.UnescapeDataString(candidate);
        }
        catch (UriFormatException)
        {
            return false;
        }

        try
        {
            if (!Path.IsPathRooted(candidate))
            {
                var sourceDir = string.IsNullOrWhiteSpace(sourcePath)
                    ? null
                    : Path.GetDirectoryName(sourcePath);
                if (string.IsNullOrWhiteSpace(sourceDir))
                {
                    return false;
                }

                candidate = Path.GetFullPath(Path.Combine(sourceDir, candidate));
            }
            else
            {
                candidate = Path.GetFullPath(candidate);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }

        if (!fileExists(candidate))
        {
            return false;
        }

        var kind = IsMarkdownDocument(candidate)
            ? MarkdownLocalLinkKind.MarkdownDocument
            : MarkdownLocalLinkKind.ExternalFile;
        target = new MarkdownLocalLinkTarget(kind, candidate);
        return true;
    }

    private static bool IsMarkdownDocument(string path)
    {
        var extension = Path.GetExtension(path);
        return MarkdownLinkExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string StripLocalLinkDecoration(string candidate)
    {
        var fragmentIndex = candidate.IndexOf('#', StringComparison.Ordinal);
        var queryIndex = candidate.IndexOf('?', StringComparison.Ordinal);
        var cutIndex = fragmentIndex >= 0 && queryIndex >= 0
            ? Math.Min(fragmentIndex, queryIndex)
            : Math.Max(fragmentIndex, queryIndex);

        return cutIndex >= 0 ? candidate[..cutIndex] : candidate;
    }
}
