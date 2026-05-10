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
        var delimiterDepth = 0;
        var segmentStart = 0;

        for (var index = 0; index < tex.Length; index++)
        {
            var current = tex[index];
            if (current == '\\')
            {
                if (TryReadCommand(tex, index, out var command, out var commandLength))
                {
                    if (command == @"\left")
                    {
                        delimiterDepth++;
                        index += commandLength - 1;
                        continue;
                    }

                    if (command == @"\right")
                    {
                        delimiterDepth = SysMath.Max(0, delimiterDepth - 1);
                        index += commandLength - 1;
                        continue;
                    }

                    if (depth == 0 && delimiterDepth == 0 && IsBreakCommand(command))
                    {
                        AddChunk(tex, segmentStart, index, chunks);
                        segmentStart = index;
                    }

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

            if (depth == 0 && delimiterDepth == 0 && IsBreakCharacter(current) && index > segmentStart)
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

    private static bool IsBreakCommand(string command)
        => BreakCommands.Contains(command, StringComparer.Ordinal);

    private static bool TryReadCommand(string tex, int index, out string command, out int commandLength)
    {
        command = string.Empty;
        commandLength = 0;

        if (index >= tex.Length || tex[index] != '\\')
        {
            return false;
        }

        var cursor = index + 1;
        while (cursor < tex.Length && char.IsLetter(tex[cursor]))
        {
            cursor++;
        }

        if (cursor == index + 1)
        {
            return false;
        }

        command = tex[index..cursor];
        commandLength = command.Length;
        return true;
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
