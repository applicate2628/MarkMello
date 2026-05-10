namespace MarkMello.Applicate.Desktop.Rendering;

public static class ApplicateWebResourcePolicy
{
    public static bool IsAllowedNavigation(string? url)
    {
        if (!TryCreateAbsoluteUri(url, out var uri))
        {
            return false;
        }

        return IsAboutBlank(uri);
    }

    public static bool IsAllowedInitialDocumentNavigation(string? url, string? generatedDocumentRoot = null)
    {
        if (!TryCreateAbsoluteUri(url, out var uri))
        {
            return false;
        }

        return IsAboutBlank(uri)
            || IsGeneratedHtmlDataDocument(uri)
            || IsGeneratedDocumentBase(uri)
            || IsGeneratedDocumentFile(uri, generatedDocumentRoot);
    }

    public static bool IsAllowedResource(string? url)
    {
        if (!TryCreateAbsoluteUri(url, out var uri))
        {
            return false;
        }

        return IsAllowedDataImageOrFont(uri);
    }

    private static bool TryCreateAbsoluteUri(string? url, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out uri!);
    }

    private static bool IsAboutBlank(Uri uri)
        => uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
            && uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedHtmlDataDocument(Uri uri)
        => uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            && uri.AbsoluteUri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedDocumentBase(Uri uri)
        => uri.Scheme.Equals("applicate-renderer", StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("document", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedDocumentFile(Uri uri, string? generatedDocumentRoot)
    {
        if (!uri.IsFile || string.IsNullOrWhiteSpace(generatedDocumentRoot))
        {
            return false;
        }

        var root = Path.GetFullPath(generatedDocumentRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        var path = Path.GetFullPath(uri.LocalPath);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedDataImageOrFont(Uri uri)
    {
        if (!uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = uri.AbsoluteUri;
        return value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:font/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:application/font-woff", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }
}
