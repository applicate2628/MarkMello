using System.Collections.Generic;

namespace MarkMello.Applicate.Desktop.Editing;

/// <summary>
/// Serialized open-document session restored across launches: the list
/// of open document file paths plus which one is active. Per-document
/// caret and scroll state are not persisted in v0.2 to keep the surface
/// small; they reset to file-open defaults on restore.
/// </summary>
public sealed class ApplicateSession
{
    public List<string> OpenPaths { get; init; } = new();

    public string? ActivePath { get; init; }

    /// <summary>
    /// Recently opened document paths, most-recent-first, deduplicated, capped. Distinct from
    /// OpenPaths (which are the CURRENTLY-open tabs): a path stays here after its tab is closed, so
    /// the welcome screen can offer it for re-opening.
    /// </summary>
    public List<string> RecentPaths { get; init; } = new();

    public const int MaxRecentPaths = 10;

    public static ApplicateSession Empty { get; } = new();

    /// <summary>
    /// Fold a just-opened path into a recent list: move-to-front, case-insensitive dedup, cap. Pure
    /// so the maintenance logic is unit-testable independent of the store or the UI.
    /// </summary>
    public static List<string> BuildRecentPaths(IEnumerable<string> existing, string openedPath)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(openedPath))
        {
            result.Add(openedPath);
        }

        foreach (var path in existing)
        {
            if (string.IsNullOrWhiteSpace(path)
                || result.Exists(p => string.Equals(p, path, System.StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(path);
            if (result.Count >= MaxRecentPaths)
            {
                break;
            }
        }

        return result;
    }

    public string? GetStartupDocumentPath()
    {
        if (!string.IsNullOrWhiteSpace(ActivePath))
        {
            return ActivePath;
        }

        foreach (var path in OpenPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        return null;
    }
}
