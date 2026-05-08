using System.Text;
using SysMath = System.Math;

namespace MarkMello.Applicate.Desktop.Math;

public static class ApplicateMathLineBreaker
{
    private static readonly string[] BreakCommands =
    [
        @"\tanh",
        @"\sinh",
        @"\cosh",
        @"\cdot",
        @"\times",
        @"\sqrt",
        @"\frac",
        @"\tan",
        @"\sin",
        @"\cos",
        @"\log",
        @"\exp",
        @"\pm",
        @"\mp",
        @"\le",
        @"\ge"
    ];

    public static IReadOnlyList<string> SplitIntoChunks(string tex)
    {
        if (string.IsNullOrWhiteSpace(tex))
        {
            return [];
        }

        var chunks = new List<string>();
        var depth = 0;
        var segmentStart = 0;

        for (var index = 0; index < tex.Length; index++)
        {
            var current = tex[index];
            if (current == '\\')
            {
                if (depth == 0 && TryGetBreakCommand(tex, index, out var commandLength))
                {
                    AddChunk(tex, segmentStart, index, chunks);
                    segmentStart = index;
                    index += commandLength - 1;
                }
                else if (index + 1 < tex.Length)
                {
                    index++;
                }

                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current == '}')
            {
                depth = SysMath.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 && IsBreakCharacter(current) && index > segmentStart)
            {
                AddChunk(tex, segmentStart, index, chunks);
                segmentStart = index;
            }
        }

        AddChunk(tex, segmentStart, tex.Length, chunks);
        return chunks;
    }

    public static IReadOnlyList<string> WrapIntoRows(
        string tex,
        double maxWidth,
        Func<string, double> measureWidth)
    {
        ArgumentNullException.ThrowIfNull(measureWidth);

        var chunks = SplitIntoChunks(tex);
        if (chunks.Count == 0)
        {
            return [];
        }

        if (double.IsNaN(maxWidth) || double.IsInfinity(maxWidth) || maxWidth <= 0)
        {
            return [NormalizeSpaces(string.Join(" ", chunks))];
        }

        var rows = new List<string>();
        var current = new StringBuilder();

        foreach (var chunk in chunks)
        {
            if (current.Length == 0)
            {
                current.Append(chunk);
                continue;
            }

            var candidate = $"{current} {chunk}";
            if (measureWidth(candidate) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            rows.Add(NormalizeSpaces(current.ToString()));
            current.Clear();
            current.Append(chunk);
        }

        if (current.Length > 0)
        {
            rows.Add(NormalizeSpaces(current.ToString()));
        }

        return rows;
    }

    private static bool IsBreakCharacter(char value)
        => value is '=' or '+' or '-' or '/' or ',' or ';';

    private static bool TryGetBreakCommand(string tex, int index, out int commandLength)
    {
        foreach (var command in BreakCommands)
        {
            if (tex.AsSpan(index).StartsWith(command.AsSpan(), StringComparison.Ordinal))
            {
                commandLength = command.Length;
                return true;
            }
        }

        commandLength = 0;
        return false;
    }

    private static void AddChunk(string tex, int start, int end, List<string> chunks)
    {
        if (end <= start)
        {
            return;
        }

        var chunk = NormalizeSpaces(tex[start..end]);
        if (chunk.Length > 0)
        {
            chunks.Add(chunk);
        }
    }

    private static string NormalizeSpaces(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
