using System;
using System.IO;

namespace MarkMello.Applicate.Desktop.Editing;

/// <summary>
/// One open markdown document tracked by <see cref="OpenDocumentsService"/>.
/// Mutable to allow the host to update per-document caret position and
/// scroll progress as the user navigates without rebuilding the record.
/// </summary>
public sealed class OpenDocument
{
    public OpenDocument(string filePath, string displayName, string sourceText)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
    }

    public string FilePath { get; }

    public string DisplayName { get; }

    public string SourceText { get; set; }

    public int EditorCaret { get; set; }

    public double ScrollProgressPercent { get; set; }

    public bool IsModified { get; set; }

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
