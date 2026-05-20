using System;
using System.IO;

namespace MarkMello.Applicate.Desktop.Editing;

/// <summary>
/// One open markdown document tracked by <see cref="OpenDocumentsService"/>.
/// Mutable to allow the host to update per-document caret position and
/// scroll progress as the user navigates without rebuilding the record.
///
/// <para>Multi-tab startup-scaling polish: a document may be created as a
/// lightweight stub (path + display name only, <see cref="IsLoaded"/> =
/// false, <see cref="SourceText"/> = "") so session-restore on cold start
/// does not pay per-tab File.ReadAllText cost. The stub materializes on
/// first activation through
/// <see cref="OpenDocumentsService.EnsureLoadedAsync"/> which fills
/// <see cref="SourceText"/> and flips <see cref="IsLoaded"/> to true. Code
/// that consumes <see cref="SourceText"/> for editing or content
/// equality (bridge edit-mode in-place apply, cross-source dedup) must
/// gate on <see cref="IsLoaded"/> first.</para>
/// </summary>
public sealed class OpenDocument
{
    public OpenDocument(string filePath, string displayName, string sourceText)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
        IsLoaded = true;
    }

    private OpenDocument(string filePath, string displayName)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        SourceText = string.Empty;
        IsLoaded = false;
    }

    public string FilePath { get; }

    public string DisplayName { get; }

    public string SourceText { get; set; }

    public int EditorCaret { get; set; }

    public double ScrollProgressPercent { get; set; }

    public bool IsModified { get; set; }

    /// <summary>
    /// True once the file's contents have been read into
    /// <see cref="SourceText"/>. Stubs created by
    /// <see cref="CreateStub(string, string)"/> for lazy session-restore
    /// flip this to true after
    /// <see cref="OpenDocumentsService.EnsureLoadedAsync"/> succeeds.
    /// </summary>
    public bool IsLoaded { get; internal set; }

    /// <summary>
    /// Create a lightweight stub representing an open tab whose contents
    /// have not yet been read from disk. The tab is visible in the strip
    /// (display name + tooltip path) but consumers that depend on
    /// <see cref="SourceText"/> must call
    /// <see cref="OpenDocumentsService.EnsureLoadedAsync"/> first.
    /// </summary>
    internal static OpenDocument CreateStub(string filePath, string displayName)
        => new(filePath, displayName);

    internal static string DisplayNameFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Untitled";
        }

        var fileName = Path.GetFileName(filePath);
        return string.IsNullOrEmpty(fileName) ? filePath : fileName;
    }
}
