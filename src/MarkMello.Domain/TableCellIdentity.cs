using System;
using System.Globalization;

namespace MarkMello.Domain;

/// <summary>
/// Single owner of the editable-table cell identity key. BOTH the HTML emission
/// side (<c>data-mm-cell-key</c> attribute) and the write-back verify side compute
/// the key through this one routine over the cell's RAW source text, so the two can
/// never diverge. Parallel sibling of <see cref="TaskListIdentity"/> (same
/// FNV-1a-32 shape) — deliberately NOT consolidated: a task marker has a line
/// regex, a table cell's identity is simply its whole trimmed raw text.
/// </summary>
public static class TableCellIdentity
{
    /// <summary>
    /// Identity key of a table cell's RAW source text: an FNV-1a-32 hash (8-hex,
    /// lowercase) of the trimmed text. Trimming makes the key independent of the
    /// cell's surrounding padding, so the emit side (which hashes the padded raw
    /// span, e.g. <c>' A '</c>) and the write-back side (which re-derives the same
    /// padded raw span from a fresh parse) produce the same key.
    /// </summary>
    public static string ComputeKey(string rawCellText)
    {
        ArgumentNullException.ThrowIfNull(rawCellText);

        var text = rawCellText.Trim();

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
