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

    public static ApplicateSession Empty { get; } = new();

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
