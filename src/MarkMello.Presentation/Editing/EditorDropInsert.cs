using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MarkMello.Presentation.Editing;

/// <summary>
/// Builds the markdown snippet a file dropped onto the source editor should
/// insert at the caret: a relative link for a markdown/text file, an
/// <c>![alt](images/...)</c> reference for an image (copied next to the host
/// document), or a base64 data URI when the host document has no directory yet.
/// </summary>
/// <remarks>
/// Deliberately free of Avalonia types so the payload rules are unit-testable
/// without a headless UI session. The drag/drop plumbing (resolving the dropped
/// path from <c>DragEventArgs</c>, the caret, and the single-writer document
/// write) belongs to <see cref="Views.EditWorkspaceView"/>, which owns the
/// source <c>TextEditor</c>.
///
/// Previously these rules lived as private members of the Applicate edit-PREVIEW
/// view, wired to a control that view did not own and which had been renamed out
/// from under it (f1d18a9), so the whole feature was unreachable and untestable.
/// </remarks>
public static class EditorDropInsert
{
    private static readonly string[] MarkdownInsertExtensions =
        [".md", ".markdown", ".mdown", ".markdn", ".txt"];

    private static readonly string[] ImageInsertExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    /// <summary>Extensions a drop on the source editor inserts rather than ignores.</summary>
    public static IReadOnlyList<string> InsertableExtensions { get; } =
        [.. MarkdownInsertExtensions, .. ImageInsertExtensions];

    /// <summary>True when a drop of <paramref name="path"/> should insert at the caret.</summary>
    public static bool IsInsertableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return MarkdownInsertExtensions.Contains(ext) || ImageInsertExtensions.Contains(ext);
    }

    /// <summary>
    /// Builds the snippet for <paramref name="sourcePath"/> relative to the host
    /// document at <paramref name="currentDocumentPath"/>. Returns <c>null</c>
    /// when the extension is not insertable.
    /// </summary>
    public static Task<string?> BuildInsertTextAsync(string sourcePath, string? currentDocumentPath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ImageInsertExtensions.Contains(ext))
        {
            return BuildImageInsertAsync(sourcePath, currentDocumentPath, ext)!;
        }

        if (MarkdownInsertExtensions.Contains(ext))
        {
            var displayName = Path.GetFileNameWithoutExtension(sourcePath);
            var target = BuildRelativeLinkTarget(sourcePath, currentDocumentPath);
            return Task.FromResult<string?>($"[{displayName}]({target})");
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Clamps a caret offset into <paramref name="currentText"/>; an out-of-range
    /// caret appends at the end rather than throwing.
    /// </summary>
    public static int ClampCaret(string currentText, int caret)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        return caret < 0 || caret > currentText.Length ? currentText.Length : caret;
    }

    /// <summary>
    /// Pads <paramref name="insertText"/> with the newlines needed for it to sit
    /// on its own source line at <paramref name="caret"/>. A caret already at a
    /// line boundary adds nothing, so a drop into empty space does not accumulate
    /// blank lines.
    /// </summary>
    public static string BuildCaretInsertText(string currentText, int caret, string insertText)
    {
        ArgumentNullException.ThrowIfNull(currentText);
        ArgumentNullException.ThrowIfNull(insertText);

        caret = ClampCaret(currentText, caret);
        var charBefore = caret > 0 ? currentText[caret - 1] : '\n';
        var charAfter = caret < currentText.Length ? currentText[caret] : '\n';
        var leading = charBefore == '\n' ? string.Empty : "\n";
        var trailing = charAfter == '\n' ? string.Empty : "\n";
        return leading + insertText + trailing;
    }

    private static string BuildRelativeLinkTarget(string sourcePath, string? currentDocumentPath)
    {
        var hostDir = string.IsNullOrWhiteSpace(currentDocumentPath)
            ? null
            : Path.GetDirectoryName(currentDocumentPath);

        if (string.IsNullOrWhiteSpace(hostDir))
        {
            return EncodeMarkdownLinkTarget(sourcePath);
        }

        try
        {
            var relative = Path.GetRelativePath(hostDir, sourcePath).Replace('\\', '/');
            return EncodeMarkdownLinkTarget(relative);
        }
        catch (ArgumentException)
        {
            return EncodeMarkdownLinkTarget(sourcePath);
        }
    }

    /// <summary>
    /// Wraps a link target in angle brackets when it holds characters that would
    /// otherwise terminate the markdown target early.
    /// </summary>
    public static string EncodeMarkdownLinkTarget(string target)
    {
        if (target.Contains(' ') || target.Contains('(') || target.Contains(')'))
        {
            return "<" + target + ">";
        }

        return target;
    }

    private static async Task<string> BuildImageInsertAsync(
        string sourcePath,
        string? currentDocumentPath,
        string ext)
    {
        var altText = Path.GetFileNameWithoutExtension(sourcePath);
        var documentDirectory = string.IsNullOrWhiteSpace(currentDocumentPath)
            ? null
            : Path.GetDirectoryName(currentDocumentPath);

        if (!string.IsNullOrWhiteSpace(documentDirectory) && Directory.Exists(documentDirectory))
        {
            var imagesDir = Path.Combine(documentDirectory, "images");
            Directory.CreateDirectory(imagesDir);

            var fileName = Path.GetFileName(sourcePath);
            var targetPath = Path.Combine(imagesDir, fileName);
            var sourceBytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true);

            targetPath = await ReserveTargetPathAsync(targetPath, sourceBytes).ConfigureAwait(true);

            if (!File.Exists(targetPath))
            {
                await File.WriteAllBytesAsync(targetPath, sourceBytes).ConfigureAwait(true);
            }

            var relative = "images/" + Path.GetFileName(targetPath).Replace('\\', '/');
            return $"![{altText}]({EncodeMarkdownLinkTarget(relative)})";
        }

        // No host directory (unsaved document): inline the bytes so the drop is
        // still lossless rather than pointing at a path the document cannot reach.
        var bytes = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(true);
        var base64 = Convert.ToBase64String(bytes);
        var mime = MimeTypeFromExtension(ext);
        return $"![{altText}](data:{mime};base64,{base64})";
    }

    /// <summary>
    /// Picks the copy target under <c>images/</c>: reuses an existing file with
    /// identical bytes, otherwise suffixes <c>-1</c>, <c>-2</c>, ... so a
    /// same-named but different image never overwrites one already referenced.
    /// </summary>
    private static async Task<string> ReserveTargetPathAsync(string desiredPath, byte[] sourceBytes)
    {
        if (File.Exists(desiredPath))
        {
            var existing = await File.ReadAllBytesAsync(desiredPath).ConfigureAwait(true);
            if (existing.AsSpan().SequenceEqual(sourceBytes))
            {
                return desiredPath;
            }

            var directory = Path.GetDirectoryName(desiredPath)!;
            var nameOnly = Path.GetFileNameWithoutExtension(desiredPath);
            var extension = Path.GetExtension(desiredPath);
            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, $"{nameOnly}-{i}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                var candidateBytes = await File.ReadAllBytesAsync(candidate).ConfigureAwait(true);
                if (candidateBytes.AsSpan().SequenceEqual(sourceBytes))
                {
                    return candidate;
                }
            }
        }

        return desiredPath;
    }

    private static string MimeTypeFromExtension(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
