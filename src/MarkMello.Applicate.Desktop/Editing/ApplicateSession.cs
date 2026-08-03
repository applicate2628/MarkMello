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
    /// Recently USED document paths, most-recent-first, deduplicated, capped. "Used" is opened OR
    /// activated: switching to an already-open tab re-heads its entry, so this tracks what the user
    /// last reached for rather than what they first opened. Distinct from OpenPaths (which are the
    /// CURRENTLY-open tabs): a path stays here after its tab is closed, so the welcome screen can
    /// offer it for re-opening.
    /// </summary>
    public List<string> RecentPaths { get; init; } = new();

    public const int MaxRecentPaths = 10;

    /// <summary>
    /// A FRESH empty session per call -- deliberately not a cached singleton. `init` protects the
    /// list REFERENCE, never its CONTENTS, so a shared instance would put a process-global mutable
    /// `List&lt;string&gt;` behind every "no saved session" route in the store. One innocuous-looking
    /// simplification at a consumer (`var recentPaths = saved.RecentPaths;` instead of a fresh list)
    /// would then let a Clear/AddRange refill the global with one document's paths, and every later
    /// empty load in the process -- including at startup -- would hand out that pollution, with no
    /// exception, no log, and no failing test.
    /// <para>
    /// Reference identity is explicitly NOT a provenance signal: `IApplicateSessionStore.LoadAsync`
    /// (d13 clause 3) forbids consumers re-deriving "was it observed" by comparing against this
    /// value, which is precisely what makes per-call construction safe. Do not re-cache it.
    /// </para>
    /// </summary>
    public static ApplicateSession Empty => new();

    /// <summary>
    /// Fold a just-used path into a recent list: move-to-front, case-insensitive dedup, cap. Pure
    /// so the maintenance logic is unit-testable independent of the store or the UI. WHICH events
    /// count as a use (an open, an activation) is the caller's decision, never this fold's.
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
