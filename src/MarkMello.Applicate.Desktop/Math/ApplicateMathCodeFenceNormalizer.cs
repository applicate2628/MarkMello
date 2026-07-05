using System.Text;

namespace MarkMello.Applicate.Desktop.Math;

/// <summary>
/// Converts GitHub-style <c>```math</c> fenced blocks into <c>$$ … $$</c> display
/// math so the renderer's display-math splitter picks them up.
///
/// <para>GitHub and LLM tools (ChatGPT et al.) emit a display formula as a
/// <c>```math</c> fenced code block. Markdig has no math-fence extension, so such
/// a block falls through to a plain code block and renders as literal monospace
/// TeX source (<c>R_{dc} \approx \frac{…}</c>) instead of a formula. This
/// render-time pass rewrites ONLY the two fence delimiter lines of a block whose
/// info string is exactly <c>math</c> (case-insensitive — GitHub's convention) to
/// <c>$$</c>; the body is copied verbatim and every other language — including a
/// plain <c>```</c> block — is left untouched, so a genuine code sample is never
/// misread as math.</para>
///
/// <para>Line count is preserved (each fence line maps to exactly one <c>$$</c>
/// line), so every line-indexed consumer downstream (SourceSpan offsets,
/// TaskSourceLine) stays correct. Source line endings (LF / CRLF, possibly mixed)
/// are preserved. The file on disk is never modified — this is a pure render-time
/// normalization, so the user's markdown stays portable.</para>
/// </summary>
public static class ApplicateMathCodeFenceNormalizer
{
    private const string MathInfoString = "math";

    public static string Normalize(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)
            || (markdown.IndexOf("```", StringComparison.Ordinal) < 0
                && markdown.IndexOf("~~~", StringComparison.Ordinal) < 0))
        {
            return markdown;
        }

        var result = new StringBuilder(markdown.Length);
        var inFence = false;
        var inMathFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        foreach (var line in ReadLinesWithEndings(markdown))
        {
            var (content, ending) = SplitEnding(line);
            var trimmed = content.Trim();

            if (!inFence)
            {
                if (TryOpenFence(trimmed, out fenceChar, out fenceLength, out var info))
                {
                    inFence = true;
                    inMathFence = string.Equals(info, MathInfoString, StringComparison.OrdinalIgnoreCase);
                    result.Append(inMathFence ? "$$" : content).Append(ending);
                    continue;
                }

                result.Append(line);
                continue;
            }

            if (IsFenceClose(trimmed, fenceChar, fenceLength))
            {
                result.Append(inMathFence ? "$$" : content).Append(ending);
                inFence = false;
                inMathFence = false;
                fenceChar = '\0';
                fenceLength = 0;
                continue;
            }

            // Fence body — copy verbatim (math TeX or code, unchanged).
            result.Append(line);
        }

        return result.ToString();
    }

    /// <summary>
    /// True when <paramref name="trimmed"/> opens a fenced block: a run of ≥3
    /// identical fence chars (<c>`</c> or <c>~</c>) optionally followed by an info
    /// string. A GitHub math block's info string is exactly <c>math</c>. A
    /// backtick info string may not itself contain a backtick (CommonMark), but
    /// that edge does not affect math detection.
    /// </summary>
    private static bool TryOpenFence(string trimmed, out char fenceChar, out int fenceLength, out string info)
    {
        fenceChar = '\0';
        fenceLength = 0;
        info = string.Empty;

        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        if (marker is not ('`' or '~'))
        {
            return false;
        }

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == marker)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        fenceChar = marker;
        fenceLength = run;
        info = trimmed[run..].Trim();
        return true;
    }

    /// <summary>
    /// True when <paramref name="trimmed"/> closes a fence opened with
    /// <paramref name="fenceChar"/> × <paramref name="fenceLength"/>: a run of the
    /// SAME char at least as long as the opener, with no trailing info string
    /// (CommonMark closing-fence rule).
    /// </summary>
    private static bool IsFenceClose(string trimmed, char fenceChar, int fenceLength)
    {
        if (trimmed.Length < fenceLength)
        {
            return false;
        }

        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] != fenceChar)
            {
                return false;
            }
        }

        // All chars are the fence char and (guard above) length ≥ fenceLength.
        return true;
    }

    private static (string Content, string Ending) SplitEnding(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return (line[..^2], "\r\n");
        }

        if (line.EndsWith('\n'))
        {
            return (line[..^1], "\n");
        }

        return (line, string.Empty);
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
