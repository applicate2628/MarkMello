using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MarkMello.Domain;

/// <summary>
/// Single owner of the GFM task-marker line shape and the task-item identity
/// key. BOTH the HTML emission side (<c>data-task-key</c> attribute) and the
/// checkbox write-back verify side compute the key through this one routine over
/// the RAW document source line, so the two can never diverge (hashing a
/// rendered label instead would never match the disk line and would refuse
/// every toggle).
/// </summary>
public static class TaskListIdentity
{
    /// <summary>
    /// A GFM task marker on a single source line: leading indent, optional
    /// blockquote prefixes (<c>&gt;</c>, possibly nested), a list bullet
    /// (-, *, +, or "1." / "1)"), then "[ ]" / "[x]" / "[X]". Group 1 is
    /// everything through the opening <c>[</c>, group 2 the state char, group 3
    /// the rest (starting with <c>]</c>) — all preserved verbatim by a flip,
    /// including any trailing <c>\r</c> on CRLF files.
    /// </summary>
    public static readonly Regex TaskMarkerPattern = new(
        @"^(\s*(?:>\s*)*(?:[-*+]|\d+[.)])\s+\[)([ xX])(\].*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Identity key of a task item's RAW source line: an FNV-1a-32 hash (8-hex,
    /// lowercase) of the item's trimmed label text (everything after the
    /// <c>]</c>). The toggled state char is EXCLUDED, so flipping the checkbox
    /// keeps the key stable; an external edit that shifts lines lands the index
    /// on a different label and the key mismatch is detected. Returns
    /// <c>null</c> when the line is not a task marker.
    /// </summary>
    public static string? ComputeKey(string? rawLine)
    {
        if (rawLine is null)
        {
            return null;
        }

        var match = TaskMarkerPattern.Match(rawLine);
        if (!match.Success)
        {
            return null;
        }

        // Group 3 starts with the ']' that closes the marker; the label is what
        // follows it. Trim also removes any trailing '\r' a CRLF file leaves on
        // the split line, keeping the hash EOL-agnostic on both sides.
        var label = match.Groups[3].Value[1..].Trim();

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var ch in label)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
