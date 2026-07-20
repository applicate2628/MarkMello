using System.Text;

namespace MarkMello.Domain;

/// <summary>
/// Inclusive character span of a table cell in a raw Markdown source.
/// </summary>
/// <param name="Start">Zero-based index of the first character.</param>
/// <param name="End">Zero-based index of the last character.</param>
public readonly record struct TableCellSpan(int Start, int End)
{
    public int Length => End - Start + 1;
}

/// <summary>
/// Encodes literal table-cell text for raw Markdown and splices it into its source span.
/// </summary>
public static class TableCellRewrite
{
    public static string EscapeCellContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = NormalizeContentEditableArtifacts(content);
        var escaped = normalized
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

        return $" {escaped} ";
    }

    /// <summary>
    /// Prepares RAW cell markdown for splicing over <paramref name="originalRawCell"/>
    /// (the located span's current bytes). The typed text IS markdown here (the user
    /// edited the cell's raw source), so NOTHING is escaped — in particular a bare
    /// <c>|</c> is deliberately left alone so the caller's re-validation REFUSES the
    /// edit instead of silently rewriting the user's markdown into <c>\|</c>. Only
    /// contenteditable artifacts (NBSP, CR/LF, control characters) are normalized.
    /// <para>
    /// The replacement REUSES the original span's own leading/trailing whitespace
    /// rather than imposing a fresh <c>" x "</c> pad: the located span includes the
    /// cell's padding for some cell contents and excludes it for others, so a fixed
    /// pad would double-space one of those cases and re-pad hand-aligned tables.
    /// </para>
    /// </summary>
    public static string NormalizeRawCellContent(string content, string originalRawCell)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(originalRawCell);

        var start = 0;
        while (start < originalRawCell.Length && char.IsWhiteSpace(originalRawCell[start]))
        {
            start++;
        }

        var end = originalRawCell.Length;
        while (end > start && char.IsWhiteSpace(originalRawCell[end - 1]))
        {
            end--;
        }

        return string.Concat(
            originalRawCell.AsSpan(0, start),
            NormalizeContentEditableArtifacts(content).AsSpan(),
            originalRawCell.AsSpan(end));
    }

    public static string Splice(string content, TableCellSpan span, string padded)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(padded);

        if (span.Start < 0 || span.End < span.Start || span.End >= content.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(span),
                span,
                "The span must identify an inclusive range within the content.");
        }

        return string.Concat(
            content.AsSpan(0, span.Start),
            padded.AsSpan(),
            content.AsSpan(span.End + 1));
    }

    private static string NormalizeContentEditableArtifacts(string content)
    {
        var normalized = new StringBuilder(content.Length);
        var hasPendingWhitespace = false;

        foreach (var character in content)
        {
            if (character is '\u00a0' or '\r' or '\n')
            {
                hasPendingWhitespace = normalized.Length > 0;
                continue;
            }

            // Whitespace is tested BEFORE the control strip: '\t' (and '\v', '\f', NEL)
            // are BOTH control and whitespace, and a tab from an Excel/TSV paste must
            // collapse to a separating space ("a\tb" -> "a b"), never be stripped
            // outright ("a\tb" -> "ab", which silently merges the two words).
            if (char.IsWhiteSpace(character))
            {
                hasPendingWhitespace = normalized.Length > 0;
                continue;
            }

            if (char.IsControl(character))
            {
                continue;
            }

            if (hasPendingWhitespace)
            {
                normalized.Append(' ');
                hasPendingWhitespace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }
}
