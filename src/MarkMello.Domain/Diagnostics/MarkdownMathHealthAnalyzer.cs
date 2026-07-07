using System;
using System.Collections.Generic;
using System.Text;

namespace MarkMello.Domain.Diagnostics;

/// <summary>
/// Kind of math-rendering defect found in a markdown document.
/// </summary>
public enum MarkdownMathDefectKind
{
    /// <summary>
    /// An inline <c>$…$</c> math span opens on one source line but is closed on
    /// a later line (the author hard-wrapped a long formula across a newline).
    /// The renderer protects inline math PER LINE, so a span that does not close
    /// on its own line is dropped and the document renders with raw <c>$</c> and
    /// mangled text. Repaired by joining the wrapped lines back into one.
    /// </summary>
    WrappedInlineMath,

    /// <summary>
    /// A display formula written with LaTeX <c>\[ … \]</c> delimiters (the format
    /// ChatGPT emits) instead of the <c>$$ … $$</c> the renderer understands. The
    /// renderer's display-math splitter only recognizes <c>$$</c>, so a
    /// <c>\[ … \]</c> block falls through to plain text and renders as literal
    /// <c>\nabla \times …</c> source. Repaired by converting the <c>\[</c> / <c>\]</c>
    /// delimiter lines to <c>$$</c> (the formula body is unchanged). Inline
    /// <c>\( … \)</c> is NOT a defect — the renderer already accepts it.
    /// </summary>
    LatexDisplayMath,
}

/// <summary>One detected (and, when repairable, repaired) math defect.</summary>
/// <param name="Kind">The defect class.</param>
/// <param name="LineNumber">1-based source line where the unclosed span starts.</param>
/// <param name="JoinedLineCount">Lines folded together to repair it (1 = none).</param>
/// <param name="Repaired">True when the analyzer produced a fix for this defect.</param>
/// <param name="Preview">Short preview of the formula (repaired when fixed), for the UI.</param>
public sealed record MarkdownMathDefect(
    MarkdownMathDefectKind Kind,
    int LineNumber,
    int JoinedLineCount,
    bool Repaired,
    string Preview);

/// <summary>Result of <see cref="MarkdownMathHealthAnalyzer.Analyze"/>.</summary>
public sealed record MarkdownMathHealthResult(
    IReadOnlyList<MarkdownMathDefect> Defects,
    string RepairedText,
    int RepairableDefectCount,
    int UnrepairableDefectCount)
{
    /// <summary>True when at least one defect was repaired in <see cref="RepairedText"/>.</summary>
    public bool HasRepairableDefects => RepairableDefectCount > 0;

    /// <summary>True when any defect (repaired or not) was found.</summary>
    public bool HasDefects => Defects.Count > 0;

    public static MarkdownMathHealthResult Clean(string text)
        => new(Array.Empty<MarkdownMathDefect>(), text, 0, 0);
}

/// <summary>
/// Detects — and repairs — markdown whose math will not render in MarkMello.
/// Two defect classes are handled:
/// <list type="bullet">
/// <item><see cref="MarkdownMathDefectKind.LatexDisplayMath"/> — a display block
/// written with LaTeX <c>\[ … \]</c> delimiters (the ChatGPT format) the renderer
/// does not recognize; converted to <c>$$ … $$</c>.</item>
/// <item><see cref="MarkdownMathDefectKind.WrappedInlineMath"/> — an inline
/// <c>$…$</c> span hard-wrapped across a source-line break; the wrapped lines are
/// joined back into one.</item>
/// </list>
/// The <c>\[ … \]</c> conversion runs first, then the wrapped-inline scan runs on
/// the converted text; both passes keep line counts (and therefore 1-based line
/// numbers) identical, so reported defect lines stay consistent. Inline
/// <c>\( … \)</c> is intentionally left untouched — the renderer already accepts it.
///
/// <para>The wrapped-inline pass MIRRORS the host renderer's per-line inline-math
/// protection
/// (<c>ApplicateMarkdownDocumentRenderer.ProtectInlineMath</c>): fenced code
/// blocks and inline code spans are skipped, <c>\$</c> is a literal dollar, a
/// line that is exactly <c>$$</c> toggles a (multi-line) display-math region,
/// and a whole-line <c>$$…$$</c> is display. Outside those, a single <c>$</c>
/// toggles an inline span; if a span is still open at end of line, the formula
/// was wrapped and the renderer drops it.</para>
///
/// <para>The repair joins the wrapped lines back into one (whitespace inside
/// <c>$…$</c> is insignificant to LaTeX/KaTeX, so the math is unchanged). Source
/// line endings (CRLF/LF, possibly mixed) are preserved: only the newline INSIDE
/// the wrapped span is removed. The analyzer is pure and operates on a string;
/// the host owns file I/O and encoding.</para>
/// </summary>
public static class MarkdownMathHealthAnalyzer
{
    public static MarkdownMathHealthResult Analyze(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return MarkdownMathHealthResult.Clean(text ?? string.Empty);
        }

        // Pass 1: convert LaTeX \[ … \] display blocks to $$ … $$.
        var display = ConvertLatexDisplayMath(text);
        // Pass 2: join hard-wrapped inline $…$ spans on the converted text.
        var inline = ScanWrappedInlineMath(display.RepairedText);

        if (display.Defects.Count == 0)
        {
            return inline; // nothing converted — return the inline scan as-is.
        }

        // Both passes preserve line count, so their 1-based line numbers refer to
        // the same lines; merge and order by line for a stable UI listing.
        var merged = new List<MarkdownMathDefect>(display.Defects.Count + inline.Defects.Count);
        merged.AddRange(display.Defects);
        merged.AddRange(inline.Defects);
        merged.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));

        return new MarkdownMathHealthResult(
            merged,
            inline.RepairedText,
            display.RepairableDefectCount + inline.RepairableDefectCount,
            display.UnrepairableDefectCount + inline.UnrepairableDefectCount);
    }

    /// <summary>
    /// Pass 1 — convert LaTeX <c>\[ … \]</c> display blocks (ChatGPT format) to the
    /// <c>$$ … $$</c> the renderer understands. Only the delimiter lines change; the
    /// formula body is copied verbatim. Fenced code and existing <c>$$ … $$</c>
    /// display regions are skipped. Inline <c>\( … \)</c> is left untouched (the
    /// renderer accepts it). Line count is preserved.
    /// </summary>
    private static MarkdownMathHealthResult ConvertLatexDisplayMath(string text)
    {
        var lines = new List<string>(text.Split('\n'));
        var output = new List<string>(lines.Count);
        var defects = new List<MarkdownMathDefect>();
        var repairable = 0;
        var unrepairable = 0;

        var inFence = false;
        var fenceMarker = string.Empty;
        var inDisplay = false;

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Fenced code block: copy verbatim, never convert.
            if (!inDisplay && TryToggleFence(trimmed, ref inFence, ref fenceMarker))
            {
                output.Add(line);
                i++;
                continue;
            }
            if (inFence)
            {
                output.Add(line);
                i++;
                continue;
            }

            // Existing $$ … $$ display region: copy verbatim.
            if (trimmed == "$$")
            {
                inDisplay = !inDisplay;
                output.Add(line);
                i++;
                continue;
            }
            if (inDisplay)
            {
                output.Add(line);
                i++;
                continue;
            }

            // Single-line \[ … \] → $$ … $$.
            if (IsSingleLineLatexDisplay(trimmed))
            {
                output.Add(ConvertLatexDelimiters(line));
                defects.Add(new MarkdownMathDefect(
                    MarkdownMathDefectKind.LatexDisplayMath,
                    i + 1,
                    1,
                    Repaired: true,
                    Preview: Preview(trimmed[2..^2])));
                repairable++;
                i++;
                continue;
            }

            // Multi-line \[ open: find its matching \] close, convert both
            // delimiter lines, copy the body verbatim.
            if (trimmed == @"\[")
            {
                var close = FindLatexDisplayClose(lines, i + 1);
                if (close is { } j)
                {
                    output.Add(line.Replace(@"\[", "$$", StringComparison.Ordinal));
                    for (var k = i + 1; k < j; k++)
                    {
                        output.Add(lines[k]);
                    }
                    output.Add(lines[j].Replace(@"\]", "$$", StringComparison.Ordinal));

                    var body = new StringBuilder();
                    for (var k = i + 1; k < j; k++)
                    {
                        if (body.Length > 0)
                        {
                            body.Append(' ');
                        }
                        body.Append(lines[k].Trim());
                    }

                    defects.Add(new MarkdownMathDefect(
                        MarkdownMathDefectKind.LatexDisplayMath,
                        i + 1,
                        j - i + 1,
                        Repaired: true,
                        Preview: Preview(body.ToString())));
                    repairable++;
                    i = j + 1;
                    continue;
                }

                // \[ with no matching \] before a boundary — report, do not guess.
                defects.Add(new MarkdownMathDefect(
                    MarkdownMathDefectKind.LatexDisplayMath,
                    i + 1,
                    1,
                    Repaired: false,
                    Preview: Preview(trimmed)));
                unrepairable++;
                output.Add(line);
                i++;
                continue;
            }

            output.Add(line);
            i++;
        }

        return new MarkdownMathHealthResult(
            defects,
            string.Join("\n", output),
            repairable,
            unrepairable);
    }

    /// <summary>
    /// A whole line that is exactly <c>\[ … \]</c> (delimiters on the same line with
    /// a non-empty body), distinct from the bare <c>\[</c> that opens a multi-line
    /// block.
    /// </summary>
    private static bool IsSingleLineLatexDisplay(string trimmed)
        => trimmed.Length > 4
            && trimmed.StartsWith(@"\[", StringComparison.Ordinal)
            && trimmed.EndsWith(@"\]", StringComparison.Ordinal);

    /// <summary>Replace the <c>\[</c> and <c>\]</c> on a single line with <c>$$</c>.</summary>
    private static string ConvertLatexDelimiters(string line)
        => line
            .Replace(@"\[", "$$", StringComparison.Ordinal)
            .Replace(@"\]", "$$", StringComparison.Ordinal);

    /// <summary>
    /// Index of the line that closes a <c>\[</c> display block (a line that is
    /// exactly <c>\]</c>), starting at <paramref name="start"/>. Returns
    /// <c>null</c> if a boundary (a new <c>\[</c>, a <c>$$</c>, a fence marker, a
    /// blank line, or end of input) is reached first — a <c>\[ … \]</c> block never
    /// spans those, so an unterminated open is reported rather than guessed.
    /// </summary>
    private static int? FindLatexDisplayClose(List<string> lines, int start)
    {
        for (var j = start; j < lines.Count; j++)
        {
            var trimmed = lines[j].Trim();
            if (trimmed == @"\]")
            {
                return j;
            }
            if (trimmed.Length == 0
                || trimmed == @"\["
                || trimmed == "$$"
                || IsFenceMarkerLine(trimmed))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Pass 2 — join hard-wrapped inline <c>$…$</c> spans. Mirrors the host
    /// renderer's per-line inline-math protection (see the type remarks).
    /// </summary>
    private static MarkdownMathHealthResult ScanWrappedInlineMath(string text)
    {
        // Split on '\n' but KEEP each line's trailing '\r' (CRLF lines), so a
        // join with '\n' reconstructs the original byte-for-byte and only the
        // removed wrap-newline changes. New joined lines carry the trailing line's
        // ending.
        var lines = new List<string>(text.Split('\n'));

        var output = new List<string>(lines.Count);
        var defects = new List<MarkdownMathDefect>();
        var repairable = 0;
        var unrepairable = 0;

        var inFence = false;
        var fenceMarker = string.Empty;
        var inDisplay = false;

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Fenced code block: copy verbatim, never scan for math.
            if (!inDisplay && TryToggleFence(trimmed, ref inFence, ref fenceMarker))
            {
                output.Add(line);
                i++;
                continue;
            }
            if (inFence)
            {
                output.Add(line);
                i++;
                continue;
            }

            // A line that is exactly "$$" toggles a multi-line display region.
            if (trimmed == "$$")
            {
                inDisplay = !inDisplay;
                output.Add(line);
                i++;
                continue;
            }
            if (inDisplay)
            {
                output.Add(line);
                i++;
                continue;
            }

            // Whole-line display math "$$ … $$" — not inline, skip the scan.
            if (trimmed.Length >= 4 && trimmed.StartsWith("$$", StringComparison.Ordinal)
                && trimmed.EndsWith("$$", StringComparison.Ordinal))
            {
                output.Add(line);
                i++;
                continue;
            }

            if (!LineEndsWithOpenInlineMath(line))
            {
                output.Add(line);
                i++;
                continue;
            }

            // Wrapped inline math: fold following lines until the span closes,
            // never crossing a blank line, a fence marker, or a "$$" toggle.
            var startLine = i + 1; // 1-based
            var startIndex = i;
            var acc = line;
            var joined = 0;
            while (LineEndsWithOpenInlineMath(acc) && i + 1 < lines.Count)
            {
                var next = lines[i + 1];
                var nextTrimmed = next.Trim();
                if (nextTrimmed.Length == 0
                    || nextTrimmed == "$$"
                    || IsFenceMarkerLine(nextTrimmed))
                {
                    break;
                }

                acc = acc.TrimEnd() + " " + next.TrimStart();
                i++;
                joined++;
            }

            if (!LineEndsWithOpenInlineMath(acc))
            {
                // The fold closed the span (a bare open line with joined == 0 is
                // still open, so reaching here means joined > 0) — a real repair.
                // Emit the joined line.
                repairable++;
                defects.Add(new MarkdownMathDefect(
                    MarkdownMathDefectKind.WrappedInlineMath,
                    startLine,
                    joined + 1,
                    Repaired: true,
                    Preview: Preview(acc)));
                output.Add(acc);
            }
            else
            {
                // Still open after folding (or nothing to fold): a genuinely
                // missing delimiter the join cannot close. Report it exactly ONCE
                // as unrepairable and leave the source lines byte-identical (like
                // the unterminated \[ case). Counting it repairable (the old bug)
                // let the banner offer a "fix" that rewrote the file by collapsing
                // the lines WITHOUT closing the math, then — because the inflated
                // repairable count drops to 0 on the re-scan of the joined text —
                // hid the still-broken document. Emitting the ORIGINAL lines keeps
                // the repair confined to spans it can actually close.
                unrepairable++;
                defects.Add(new MarkdownMathDefect(
                    MarkdownMathDefectKind.WrappedInlineMath,
                    startLine,
                    joined + 1,
                    Repaired: false,
                    Preview: Preview(acc)));
                for (var k = startIndex; k <= i; k++)
                {
                    output.Add(lines[k]);
                }
            }

            i++;
        }

        var repairedText = string.Join("\n", output);
        return new MarkdownMathHealthResult(defects, repairedText, repairable, unrepairable);
    }

    /// <summary>
    /// True when, scanning <paramref name="content"/> as one source line, an
    /// inline <c>$…$</c> span is still open at the end. Mirrors the renderer:
    /// skips inline code spans, treats <c>\$</c> as literal, and treats <c>$$</c>
    /// as a non-inline (display) delimiter rather than two toggles.
    /// </summary>
    private static bool LineEndsWithOpenInlineMath(string content)
    {
        var inMath = false;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];

            if (c == '\\')
            {
                // Escape: skip the backslash and the escaped char (\$, \\, …).
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var end = FindCodeSpanEnd(content, i);
                if (end is null)
                {
                    // Unterminated inline code: the rest of the line is code, so
                    // no further math delimiters apply.
                    break;
                }

                i = end.Value;
                continue;
            }

            if (c == '$')
            {
                if (i + 1 < content.Length && content[i + 1] == '$')
                {
                    // "$$" is display, not an inline toggle — skip both.
                    i += 2;
                    continue;
                }

                inMath = !inMath;
                i++;
                continue;
            }

            i++;
        }

        return inMath;
    }

    // --- helpers mirrored from ApplicateMarkdownDocumentRenderer (kept in sync;
    // they cannot be shared directly because that renderer lives in the desktop
    // layer which the domain cannot reference). ---

    private static int? FindCodeSpanEnd(string line, int startIndex)
    {
        var runLength = 0;
        while (startIndex + runLength < line.Length && line[startIndex + runLength] == '`')
        {
            runLength++;
        }

        var marker = new string('`', runLength);
        var closingIndex = line.IndexOf(marker, startIndex + runLength, StringComparison.Ordinal);
        return closingIndex < 0 ? null : closingIndex + runLength;
    }

    private static bool TryToggleFence(string trimmed, ref bool inFence, ref string fenceMarker)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var markerChar = trimmed[0];
        if (markerChar is not ('`' or '~'))
        {
            return false;
        }

        var markerLength = 0;
        while (markerLength < trimmed.Length && trimmed[markerLength] == markerChar)
        {
            markerLength++;
        }

        if (markerLength < 3)
        {
            return false;
        }

        var marker = new string(markerChar, markerLength);
        if (!inFence)
        {
            inFence = true;
            fenceMarker = marker;
            return true;
        }

        if (!trimmed.StartsWith(fenceMarker, StringComparison.Ordinal))
        {
            return false;
        }

        inFence = false;
        fenceMarker = string.Empty;
        return true;
    }

    private static bool IsFenceMarkerLine(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var markerChar = trimmed[0];
        if (markerChar is not ('`' or '~'))
        {
            return false;
        }

        var markerLength = 0;
        while (markerLength < trimmed.Length && trimmed[markerLength] == markerChar)
        {
            markerLength++;
        }

        return markerLength >= 3;
    }

    private static string Preview(string content)
    {
        var trimmed = content.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..117] + "…";
    }
}
