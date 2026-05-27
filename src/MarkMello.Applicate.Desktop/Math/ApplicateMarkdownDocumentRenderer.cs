using System.Text;
using System.Text.RegularExpressions;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;

namespace MarkMello.Applicate.Desktop.Math;

public sealed class ApplicateMarkdownDocumentRenderer : IMarkdownDocumentRenderer
{
    private static readonly Regex InlineMathPattern = new(
        @"(?<!\\)(\$(?!\$)(.+?)(?<!\\)\$|\\\((.+?)\\\))",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex InlineMathTokenPattern = new(
        @"@@APPLICATE_MATH_(\d+)@@",
        RegexOptions.Compiled);

    private readonly IMarkdownDocumentRenderer _inner = new MarkdigMarkdownDocumentRenderer();
    private readonly Func<string, string> _normalizeTex;

    public ApplicateMarkdownDocumentRenderer()
        : this(NormalizeTexForRenderer)
    {
    }

    internal ApplicateMarkdownDocumentRenderer(Func<string, string> normalizeTex)
    {
        _normalizeTex = normalizeTex ?? throw new ArgumentNullException(nameof(normalizeTex));
    }

    public RenderedMarkdownDocument Render(string markdown) => Render(markdown, baseDirectory: null);

    public RenderedMarkdownDocument Render(string markdown, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return RenderedMarkdownDocument.Empty;
        }

        var blocks = new List<MarkdownBlock>();
        foreach (var segment in SplitDisplayMath(markdown))
        {
            if (segment.IsMath)
            {
                blocks.Add(new ApplicateMathBlock(_normalizeTex(segment.Text))
                {
                    SourceSpan = new MarkdownSourceSpan(segment.StartLine, segment.EndLine)
                });
                continue;
            }

            var protectedText = ProtectInlineMath(segment.Text, _normalizeTex, out var inlineMath);
            var rendered = _inner.Render(protectedText, baseDirectory);
            var renderedBlocks = inlineMath.Count == 0
                ? rendered.Blocks
                : RestoreInlineMath(rendered.Blocks, inlineMath);
            blocks.AddRange(OffsetSourceSpans(renderedBlocks, segment.StartLine));
        }

        return new RenderedMarkdownDocument(blocks, baseDirectory);
    }

    private static IReadOnlyList<MarkdownBlock> OffsetSourceSpans(
        IReadOnlyList<MarkdownBlock> blocks,
        int lineOffset)
        => lineOffset == 0
            ? blocks
            : blocks.Select(block => OffsetSourceSpan(block, lineOffset)).ToList();

    private static MarkdownBlock OffsetSourceSpan(MarkdownBlock block, int lineOffset)
    {
        var sourceSpan = OffsetSourceSpan(block.SourceSpan, lineOffset);
        return block switch
        {
            MarkdownQuoteBlock quote => quote with
            {
                SourceSpan = sourceSpan,
                Blocks = OffsetSourceSpans(quote.Blocks, lineOffset)
            },
            MarkdownListBlock list => list with
            {
                SourceSpan = sourceSpan,
                Items = list.Items
                    .Select(item => item with
                    {
                        Blocks = OffsetSourceSpans(item.Blocks, lineOffset)
                    })
                    .ToList()
            },
            _ => block with { SourceSpan = sourceSpan }
        };
    }

    private static MarkdownSourceSpan? OffsetSourceSpan(MarkdownSourceSpan? sourceSpan, int lineOffset)
        => sourceSpan is { } span
            ? new MarkdownSourceSpan(span.StartLine + lineOffset, span.EndLine + lineOffset)
            : null;

    private static string ProtectInlineMath(
        string markdown,
        Func<string, string> normalizeTex,
        out IReadOnlyDictionary<int, string> inlineMath)
    {
        var replacements = new Dictionary<int, string>();
        var result = new StringBuilder(markdown.Length);
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

            AppendLineWithProtectedInlineMath(result, line, replacements, normalizeTex);
        }

        inlineMath = replacements;
        return result.ToString();
    }

    private static void AppendLineWithProtectedInlineMath(
        StringBuilder result,
        string line,
        Dictionary<int, string> replacements,
        Func<string, string> normalizeTex)
    {
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
            AppendProtectedInlineMathSegment(result, line[cursor..segmentEnd], replacements, normalizeTex);
            cursor = segmentEnd;
        }
    }

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

    private static void AppendProtectedInlineMathSegment(
        StringBuilder result,
        string text,
        Dictionary<int, string> replacements,
        Func<string, string> normalizeTex)
    {
        result.Append(InlineMathPattern.Replace(text, match =>
        {
            var tex = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(tex))
            {
                return match.Value;
            }

            var index = replacements.Count;
            replacements.Add(index, normalizeTex(tex.Trim()));
            return $"@@APPLICATE_MATH_{index}@@";
        }));
    }

    private static IReadOnlyList<MarkdownBlock> RestoreInlineMath(
        IReadOnlyList<MarkdownBlock> blocks,
        IReadOnlyDictionary<int, string> inlineMath)
        => blocks.Select(block => RestoreInlineMath(block, inlineMath)).ToList();

    private static MarkdownBlock RestoreInlineMath(
        MarkdownBlock block,
        IReadOnlyDictionary<int, string> inlineMath)
        => block switch
        {
            MarkdownHeadingBlock heading => heading with { Inlines = RestoreInlineMath(heading.Inlines, inlineMath) },
            MarkdownParagraphBlock paragraph => paragraph with { Inlines = RestoreInlineMath(paragraph.Inlines, inlineMath) },
            MarkdownQuoteBlock quote => quote with { Blocks = RestoreInlineMath(quote.Blocks, inlineMath) },
            MarkdownListBlock list => list with
            {
                Items = list.Items.Select(item => item with
                {
                    Blocks = RestoreInlineMath(item.Blocks, inlineMath)
                }).ToList()
            },
            MarkdownTableBlock table => table with
            {
                Header = table.Header.Select(cell => cell with
                {
                    Inlines = RestoreInlineMath(cell.Inlines, inlineMath)
                }).ToList(),
                Rows = table.Rows.Select(row => row.Select(cell => cell with
                {
                    Inlines = RestoreInlineMath(cell.Inlines, inlineMath)
                }).ToList()).ToList()
            },
            _ => block
        };

    private static IReadOnlyList<MarkdownInline> RestoreInlineMath(
        IReadOnlyList<MarkdownInline> inlines,
        IReadOnlyDictionary<int, string> inlineMath)
    {
        var result = new List<MarkdownInline>();
        foreach (var inline in inlines)
        {
            AddRestoredInline(result, inline, inlineMath);
        }

        return result;
    }

    private static void AddRestoredInline(
        List<MarkdownInline> result,
        MarkdownInline inline,
        IReadOnlyDictionary<int, string> inlineMath)
    {
        switch (inline)
        {
            case MarkdownTextInline text:
                AddRestoredTextInline(result, text.Text, inlineMath);
                return;

            case MarkdownStrongInline strong:
                result.Add(strong with { Inlines = RestoreInlineMath(strong.Inlines, inlineMath) });
                return;

            case MarkdownEmphasisInline emphasis:
                result.Add(emphasis with { Inlines = RestoreInlineMath(emphasis.Inlines, inlineMath) });
                return;

            case MarkdownLinkInline link:
                result.Add(link with { Inlines = RestoreInlineMath(link.Inlines, inlineMath) });
                return;

            default:
                result.Add(inline);
                return;
        }
    }

    private static void AddRestoredTextInline(
        List<MarkdownInline> result,
        string text,
        IReadOnlyDictionary<int, string> inlineMath)
    {
        var cursor = 0;
        foreach (Match match in InlineMathTokenPattern.Matches(text))
        {
            if (match.Index > cursor)
            {
                result.Add(new MarkdownTextInline(text[cursor..match.Index]));
            }

            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inlineMath.TryGetValue(index, out var tex))
            {
                result.Add(new ApplicateMathInline(tex));
            }
            else
            {
                result.Add(new MarkdownTextInline(match.Value));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            result.Add(new MarkdownTextInline(text[cursor..]));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SubscriptSuperscriptCommandPattern = new(
        @"(_|\^)(\\[a-zA-Z]+)(?!\s*\{)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string NormalizeTexForRenderer(string tex)
    {
        ArgumentNullException.ThrowIfNull(tex);

        var normalized = tex
            .Replace(@"\tfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"\dfrac", @"\frac", StringComparison.Ordinal)
            .Replace(@"^{\prime}", "'", StringComparison.Ordinal)
            .Replace(@"^\prime", "'", StringComparison.Ordinal);

        // Wrap `_\command` / `^\command` into `_{\command}` / `^{\command}`
        // because CSharpMath rejects bare command tokens as subscript or
        // superscript content (it expects a single atom). The negative
        // lookahead `(?!\s*\{)` skips commands that already take an
        // explicit argument like `_\sqrt{x}` to avoid producing
        // `_{\sqrt}{x}` which would change the meaning.
        normalized = SubscriptSuperscriptCommandPattern.Replace(normalized, "$1{$2}");

        return StripUnsupportedFormattingCommands(StripUnsupportedBraceAnnotations(normalized));
    }

    private static string StripUnsupportedFormattingCommands(string tex)
    {
        var result = new StringBuilder(tex.Length);
        var index = 0;

        while (index < tex.Length)
        {
            if (TryReadSingleArgumentFormattingCommand(tex, index, @"\boldsymbol", out var body, out var nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\boxed", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathrel", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathord", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathbin", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathop", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathpunct", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathopen", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathclose", out body, out nextIndex) ||
                TryReadSingleArgumentFormattingCommand(tex, index, @"\mathinner", out body, out nextIndex))
            {
                result.Append(StripUnsupportedFormattingCommands(body));
                index = nextIndex;
                continue;
            }

            result.Append(tex[index]);
            index++;
        }

        return result.ToString();
    }

    private static bool TryReadSingleArgumentFormattingCommand(
        string tex,
        int startIndex,
        string command,
        out string body,
        out int nextIndex)
    {
        body = string.Empty;
        nextIndex = startIndex;

        if (!tex.AsSpan(startIndex).StartsWith(command, StringComparison.Ordinal))
        {
            return false;
        }

        var cursor = startIndex + command.Length;
        if (cursor < tex.Length && char.IsLetter(tex[cursor]))
        {
            return false;
        }

        cursor = SkipWhitespace(tex, cursor);
        if (cursor >= tex.Length)
        {
            nextIndex = cursor;
            return true;
        }

        if (tex[cursor] == '{')
        {
            if (!TryReadBalancedGroup(tex, cursor, out body, out nextIndex))
            {
                return false;
            }

            return true;
        }

        if (tex[cursor] == '\\')
        {
            var commandEnd = cursor + 1;
            while (commandEnd < tex.Length && char.IsLetter(tex[commandEnd]))
            {
                commandEnd++;
            }

            body = tex[cursor..commandEnd];
            nextIndex = commandEnd;
            return true;
        }

        body = tex[cursor].ToString();
        nextIndex = cursor + 1;
        return true;
    }

    private static string StripUnsupportedBraceAnnotations(string tex)
    {
        var result = new StringBuilder(tex.Length);
        var index = 0;

        while (index < tex.Length)
        {
            if (TryReadBraceAnnotation(tex, index, @"\underbrace", out var body, out var nextIndex) ||
                TryReadBraceAnnotation(tex, index, @"\overbrace", out body, out nextIndex))
            {
                result.Append(StripUnsupportedBraceAnnotations(body));
                index = nextIndex;
                continue;
            }

            result.Append(tex[index]);
            index++;
        }

        return result.ToString();
    }

    private static bool TryReadBraceAnnotation(
        string tex,
        int startIndex,
        string command,
        out string body,
        out int nextIndex)
    {
        body = string.Empty;
        nextIndex = startIndex;

        if (!tex.AsSpan(startIndex).StartsWith(command, StringComparison.Ordinal))
        {
            return false;
        }

        var cursor = startIndex + command.Length;
        if (cursor < tex.Length && char.IsLetter(tex[cursor]))
        {
            return false;
        }

        cursor = SkipWhitespace(tex, cursor);
        if (cursor >= tex.Length || tex[cursor] != '{')
        {
            return false;
        }

        if (!TryReadBalancedGroup(tex, cursor, out body, out cursor))
        {
            return false;
        }

        nextIndex = ConsumeOptionalBraceAnnotationLabel(tex, cursor);
        return true;
    }

    private static int ConsumeOptionalBraceAnnotationLabel(string tex, int index)
    {
        var cursor = SkipWhitespace(tex, index);
        if (cursor >= tex.Length || (tex[cursor] != '_' && tex[cursor] != '^'))
        {
            return index;
        }

        cursor = SkipWhitespace(tex, cursor + 1);
        if (cursor >= tex.Length)
        {
            return cursor;
        }

        if (tex[cursor] == '{' && TryReadBalancedGroup(tex, cursor, out _, out var afterGroup))
        {
            return afterGroup;
        }

        return cursor + 1;
    }

    private static bool TryReadBalancedGroup(string tex, int openBraceIndex, out string body, out int nextIndex)
    {
        body = string.Empty;
        nextIndex = openBraceIndex;

        if (openBraceIndex >= tex.Length || tex[openBraceIndex] != '{')
        {
            return false;
        }

        var depth = 0;
        for (var index = openBraceIndex; index < tex.Length; index++)
        {
            var ch = tex[index];
            if (ch == '\\')
            {
                index++;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                body = tex[(openBraceIndex + 1)..index];
                nextIndex = index + 1;
                return true;
            }
        }

        return false;
    }

    private static int SkipWhitespace(string tex, int index)
    {
        while (index < tex.Length && char.IsWhiteSpace(tex[index]))
        {
            index++;
        }

        return index;
    }

    private static IEnumerable<MarkdownSegment> SplitDisplayMath(string markdown)
    {
        var text = new StringBuilder();
        var math = new StringBuilder();
        var inMath = false;
        var inFence = false;
        var fenceMarker = string.Empty;
        int? textStartLine = null;
        var mathStartLine = 0;
        var currentLine = 0;

        foreach (var line in ReadLinesWithEndings(markdown))
        {
            var lineNumber = currentLine++;
            var trimmed = line.Trim();

            if (!inMath && TryToggleFence(trimmed, ref inFence, ref fenceMarker))
            {
                textStartLine ??= lineNumber;
                text.Append(line);
                continue;
            }

            if (!inFence && trimmed == "$$")
            {
                if (inMath)
                {
                    yield return new MarkdownSegment(
                        math.ToString().Trim(),
                        IsMath: true,
                        mathStartLine,
                        lineNumber);
                    math.Clear();
                    inMath = false;
                }
                else
                {
                    if (text.Length > 0 && textStartLine is { } startLine)
                    {
                        yield return new MarkdownSegment(
                            text.ToString(),
                            IsMath: false,
                            startLine,
                            System.Math.Max(startLine, lineNumber - 1));
                        text.Clear();
                        textStartLine = null;
                    }
                    inMath = true;
                    mathStartLine = lineNumber;
                }
                continue;
            }

            if (!inFence && TryReadSingleLineDisplayMath(trimmed, out var singleLineMath))
            {
                if (text.Length > 0 && textStartLine is { } startLine)
                {
                    yield return new MarkdownSegment(
                        text.ToString(),
                        IsMath: false,
                        startLine,
                        System.Math.Max(startLine, lineNumber - 1));
                    text.Clear();
                    textStartLine = null;
                }
                yield return new MarkdownSegment(singleLineMath, IsMath: true, lineNumber, lineNumber);
                continue;
            }

            if (!inFence && TryReadStandaloneInlineMath(trimmed, out var standaloneInlineMath))
            {
                if (text.Length > 0 && textStartLine is { } startLine)
                {
                    yield return new MarkdownSegment(
                        text.ToString(),
                        IsMath: false,
                        startLine,
                        System.Math.Max(startLine, lineNumber - 1));
                    text.Clear();
                    textStartLine = null;
                }
                yield return new MarkdownSegment(standaloneInlineMath, IsMath: true, lineNumber, lineNumber);
                continue;
            }

            if (inMath)
            {
                math.Append(line);
            }
            else
            {
                textStartLine ??= lineNumber;
                text.Append(line);
            }
        }

        if (inMath)
        {
            textStartLine ??= mathStartLine;
            text.AppendLine("$$");
            text.Append(math);
        }

        if (text.Length > 0 && textStartLine is { } finalStartLine)
        {
            yield return new MarkdownSegment(
                text.ToString(),
                IsMath: false,
                finalStartLine,
                System.Math.Max(finalStartLine, currentLine - 1));
        }
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

    private static bool TryReadSingleLineDisplayMath(string trimmed, out string tex)
    {
        tex = string.Empty;
        if (trimmed.Length <= 4 || !trimmed.StartsWith("$$", StringComparison.Ordinal) || !trimmed.EndsWith("$$", StringComparison.Ordinal))
        {
            return false;
        }

        tex = trimmed[2..^2].Trim();
        return tex.Length > 0;
    }

    private static bool TryReadStandaloneInlineMath(string trimmed, out string tex)
    {
        tex = string.Empty;
        if (trimmed.Length <= 2
            || !trimmed.StartsWith('$')
            || !trimmed.EndsWith('$')
            || trimmed.StartsWith("$$", StringComparison.Ordinal)
            || trimmed.EndsWith("$$", StringComparison.Ordinal)
            || IsEscaped(trimmed, trimmed.Length - 1))
        {
            return false;
        }

        tex = trimmed[1..^1].Trim();
        if (ContainsUnescapedDollar(tex))
        {
            tex = string.Empty;
            return false;
        }

        return tex.Length > 0;
    }

    private static bool ContainsUnescapedDollar(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '$' && !IsEscaped(text, index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
        {
            slashCount++;
        }

        return slashCount % 2 == 1;
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

    private readonly record struct MarkdownSegment(string Text, bool IsMath, int StartLine, int EndLine);
}
