using System.Text;

namespace MarkMello.Applicate.Desktop.Math;

/// <summary>
/// Render-pipeline leniency for inline links whose destination contains raw
/// spaces. CommonMark requires such destinations to be wrapped in
/// <c>&lt;…&gt;</c>; ChatGPT/Obsidian-style documents write them bare
/// (<c>[t](./a b.md)</c>), which strict Markdig drops to literal text. This
/// pass rewrites ONLY that broken shape to <c>[t](&lt;./a b.md&gt;)</c> before
/// Markdig parses, so the link renders.
///
/// <para>Invariants (design: .scratch/plans/design-links.md, fable-PASS):
/// runs AFTER inline math is tokenized (no math text remains); NEVER changes
/// the line count (only inserts <c>&lt;</c>/<c>&gt;</c> within a line);
/// conservative — every uncertain case is left byte-identical (fenced code,
/// inline code spans, already-wrapped dests, valid title forms, footnote refs
/// <c>[^…]</c>, unclosed titles, dests carrying raw <c>&lt;</c>/<c>&gt;</c> or a
/// math token). The user's file is never touched — this transforms only the
/// string handed to the parser.</para>
/// </summary>
internal static class ApplicateLenientLinkNormalizer
{
    private const string MathTokenMarker = "@@APPLICATE_MATH_";

    public static string Normalize(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        var result = new StringBuilder(markdown.Length + 16);
        var inFence = false;
        var fenceMarker = string.Empty;

        foreach (var line in ReadLinesWithEndings(markdown))
        {
            var trimmed = line.Trim();
            if (TryToggleFence(trimmed, ref inFence, ref fenceMarker) || inFence)
            {
                result.Append(line);
                continue;
            }

            AppendNormalizedLine(result, line);
        }

        return result.ToString();
    }

    // Per-line scan: copy code spans verbatim; between them, run the link scan.
    private static void AppendNormalizedLine(StringBuilder result, string line)
    {
        var lineStart = result.Length;
        var cursor = 0;
        while (cursor < line.Length)
        {
            if (line[cursor] == '`')
            {
                var codeSpanEnd = FindCodeSpanEnd(line, cursor);
                if (codeSpanEnd is null)
                {
                    result.Append(line.AsSpan(cursor));
                    return;
                }

                result.Append(line.AsSpan(cursor, codeSpanEnd.Value - cursor));
                cursor = codeSpanEnd.Value;
                continue;
            }

            var nextCodeSpan = line.IndexOf('`', cursor);
            var segmentEnd = nextCodeSpan < 0 ? line.Length : nextCodeSpan;
            AppendNormalizedSegment(result, line, cursor, segmentEnd, lineStart);
            cursor = segmentEnd;
        }
    }

    /// <summary>
    /// Scan a plain (non-code) segment of the line for the broken-link shape and
    /// append with any needed wraps. <paramref name="lineStart"/> is where this
    /// LINE began in <paramref name="result"/>, so the opening-bracket lookback
    /// (footnote guard) can see text already appended from earlier segments.
    /// </summary>
    private static void AppendNormalizedSegment(
        StringBuilder result,
        string line,
        int start,
        int end,
        int lineStart)
    {
        var i = start;
        while (i < end)
        {
            var ch = line[i];
            if (ch == '\\' && i + 1 < end)
            {
                result.Append(ch).Append(line[i + 1]);
                i += 2;
                continue;
            }

            // Candidate: unescaped `]` immediately followed by `(` (CommonMark
            // requires adjacency), with a matching `[` earlier on the line.
            if (ch == ']' && i + 1 < end && line[i + 1] == '(')
            {
                var openBracket = FindOpeningBracket(result, lineStart);
                if (openBracket is { } bracketIndex)
                {
                    // Footnote guard: `[^…](…)` is a footnote reference followed
                    // by prose parens — wrapping would destroy the footnote.
                    var isFootnote = bracketIndex + 1 < result.Length && result[bracketIndex + 1] == '^';
                    if (!isFootnote && TryRewriteParenthesized(result, line, i, end, out var consumedTo))
                    {
                        i = consumedTo;
                        continue;
                    }
                }
            }

            result.Append(ch);
            i++;
        }
    }

    /// <summary>
    /// Index in <paramref name="result"/> of the unescaped unmatched `[` that
    /// would open a link whose `]` we just hit, or null. Scans the current
    /// line's already-appended text backwards, balancing `]`/`[`.
    /// </summary>
    private static int? FindOpeningBracket(StringBuilder result, int lineStart)
    {
        var depth = 0;
        for (var j = result.Length - 1; j >= lineStart; j--)
        {
            var c = result[j];
            if (c is not ('[' or ']'))
            {
                continue;
            }

            // Escaped bracket is literal.
            if (j > lineStart && result[j - 1] == '\\')
            {
                continue;
            }

            if (c == ']')
            {
                depth++;
                continue;
            }

            if (depth == 0)
            {
                return j;
            }

            depth--;
        }

        return null;
    }

    /// <summary>
    /// At <paramref name="closeBracket"/> = index of `]` (with `(` right after),
    /// parse the parenthesized run and decide leave/wrap. On WRAP appends
    /// `](&lt;payload&gt;…)`, on LEAVE appends the run verbatim; either way
    /// returns true with <paramref name="consumedTo"/> = index after the closing
    /// `)`. Returns false only when there is no complete `(…)` on this line
    /// (caller then appends just the `]` and moves on).
    /// </summary>
    private static bool TryRewriteParenthesized(
        StringBuilder result,
        string line,
        int closeBracket,
        int end,
        out int consumedTo)
    {
        consumedTo = closeBracket;
        var open = closeBracket + 1; // the '('

        // Phase A — find the matching ')' with escape- and QUOTE-awareness: a
        // ')' inside a quoted title must not terminate the run, and an unclosed
        // quote means we cannot classify safely -> leave (bail to plain copy).
        var depth = 1;
        var quote = '\0';
        var closeParen = -1;
        for (var k = open + 1; k < end; k++)
        {
            var c = line[k];
            if (c == '\\' && k + 1 < end)
            {
                k++;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                // Title context only plausible after whitespace; a quote glued to
                // path chars is treated as part of the path (no quote mode).
                if (k > open + 1 && (line[k - 1] == ' ' || line[k - 1] == '\t'))
                {
                    quote = c;
                }

                continue;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    closeParen = k;
                    break;
                }
            }
        }

        if (closeParen < 0)
        {
            // No complete link on this line (or an unclosed quoted title):
            // leave everything untouched — copy verbatim through the end.
            return false;
        }

        var inner = line[(open + 1)..closeParen];
        var verbatim = line[closeBracket..(closeParen + 1)];
        consumedTo = closeParen + 1;

        if (ShouldWrap(inner, out var payload, out var titleSuffix))
        {
            result.Append("](<").Append(payload).Append('>').Append(titleSuffix).Append(')');
        }
        else
        {
            result.Append(verbatim);
        }

        return true;
    }

    /// <summary>
    /// Decide whether the parenthesized content is the broken bare-space-dest
    /// shape. True → wrap <paramref name="payload"/> (with an optional preserved
    /// <paramref name="titleSuffix"/>, leading-space included); false → leave.
    /// </summary>
    private static bool ShouldWrap(string inner, out string payload, out string titleSuffix)
    {
        payload = string.Empty;
        titleSuffix = string.Empty;

        var trimmedInner = inner.Trim();
        if (trimmedInner.Length == 0 || trimmedInner[0] == '<')
        {
            return false; // empty dest, or already pointy-wrapped
        }

        // Find the first raw space/tab of the bare destination.
        var destBreak = -1;
        for (var i = 0; i < trimmedInner.Length; i++)
        {
            var c = trimmedInner[i];
            if (c == '\\' && i + 1 < trimmedInner.Length)
            {
                i++;
                continue;
            }

            if (c is ' ' or '\t')
            {
                destBreak = i;
                break;
            }
        }

        if (destBreak < 0)
        {
            return false; // space-free destination — already parses today
        }

        // Space present. A valid `dest "title"` / `dest 'title'` / `dest (title)`
        // form parses today — leave it.
        var rest = trimmedInner[destBreak..].TrimStart();
        if (IsCleanTitle(rest))
        {
            return false;
        }

        // Broken bare-space destination. Split off a trailing well-formed quoted
        // title (space-separated) so `[t](./a b.md "title")` keeps its title.
        payload = trimmedInner;
        if (TrySplitTrailingQuotedTitle(trimmedInner, out var path, out var title))
        {
            payload = path;
            titleSuffix = " " + title;
        }

        // Safety guards: a pointy dest cannot carry raw '<'/'>'; a math token in
        // a dest would leak into the URL (RestoreInlineMath never touches Url).
        if (payload.Contains('<', StringComparison.Ordinal)
            || payload.Contains('>', StringComparison.Ordinal)
            || payload.Contains(MathTokenMarker, StringComparison.Ordinal))
        {
            payload = string.Empty;
            titleSuffix = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="rest"/> is exactly one CommonMark title
    /// (<c>"…"</c>, <c>'…'</c>, or balanced <c>(…)</c>) with nothing after it.
    /// </summary>
    private static bool IsCleanTitle(string rest)
    {
        if (rest.Length < 2)
        {
            return false;
        }

        var opener = rest[0];
        if (opener is '"' or '\'')
        {
            for (var i = 1; i < rest.Length; i++)
            {
                if (rest[i] == '\\' && i + 1 < rest.Length)
                {
                    i++;
                    continue;
                }

                if (rest[i] == opener)
                {
                    return rest[(i + 1)..].Trim().Length == 0;
                }
            }

            return false;
        }

        if (opener == '(')
        {
            var depth = 0;
            for (var i = 0; i < rest.Length; i++)
            {
                if (rest[i] == '\\' && i + 1 < rest.Length)
                {
                    i++;
                    continue;
                }

                if (rest[i] == '(')
                {
                    depth++;
                }
                else if (rest[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return rest[(i + 1)..].Trim().Length == 0;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Split a trailing space-separated well-formed quoted title off a broken
    /// destination: <c>./a b.md "title"</c> → (<c>./a b.md</c>, <c>"title"</c>).
    /// </summary>
    private static bool TrySplitTrailingQuotedTitle(string content, out string path, out string title)
    {
        path = content;
        title = string.Empty;

        if (content.Length < 3)
        {
            return false;
        }

        var last = content[^1];
        if (last is not ('"' or '\''))
        {
            return false;
        }

        var openQuote = content.LastIndexOf(last, content.Length - 2);
        if (openQuote <= 0
            || (content[openQuote - 1] != ' ' && content[openQuote - 1] != '\t')
            || (openQuote > 0 && content[openQuote - 1] == '\\'))
        {
            return false;
        }

        var candidatePath = content[..openQuote].TrimEnd();
        if (candidatePath.Length == 0)
        {
            return false;
        }

        path = candidatePath;
        title = content[openQuote..];
        return true;
    }

    // --- helpers mirrored from ApplicateMarkdownDocumentRenderer (same skip
    // semantics; kept in sync). ---

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

    private static IEnumerable<string> ReadLinesWithEndings(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            yield return text[start..(index + 1)];
            start = index + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
