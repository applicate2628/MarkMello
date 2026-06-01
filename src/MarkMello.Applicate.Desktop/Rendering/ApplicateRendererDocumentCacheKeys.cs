namespace MarkMello.Applicate.Desktop.Rendering;

internal static class ApplicateRendererDocumentCacheKeys
{
    public static string Create(string theme, string documentIdentity, string suffix)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(documentIdentity);
        ArgumentNullException.ThrowIfNull(suffix);

        return $"{theme}|{CreateIdentityHash(documentIdentity)}|{suffix}";
    }

    public static string CreateSuffix(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var hash = 2166136261u;
        foreach (var c in html)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return $"{html.Length}|{hash:x8}";
    }

    private static string CreateIdentityHash(string documentIdentity)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var c in documentIdentity)
        {
            hash ^= c;
            hash *= prime;
        }

        return $"{hash:x16}";
    }
}
