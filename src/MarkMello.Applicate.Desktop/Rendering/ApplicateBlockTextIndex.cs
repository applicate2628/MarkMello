using System.Text;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateBlockTextIndex
{
    private readonly IReadOnlyList<ApplicateHtmlBlockMarker> _blocks;

    private ApplicateBlockTextIndex(IReadOnlyList<ApplicateHtmlBlockMarker> blocks)
    {
        _blocks = blocks;
    }

    public static ApplicateBlockTextIndex Create(IEnumerable<ApplicateHtmlBlockMarker> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return new ApplicateBlockTextIndex(blocks.ToArray());
    }

    public ApplicateBlockTextSearchResult Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return ApplicateBlockTextSearchResult.Empty;
        }

        var normalizedNeedle = NormalizeForFind(query);
        if (normalizedNeedle.Length == 0 || normalizedNeedle.Length != query.Length)
        {
            return ApplicateBlockTextSearchResult.Empty;
        }

        var matches = new List<ApplicateBlockTextMatch>();
        var ordinal = 0;
        foreach (var block in _blocks)
        {
            var text = block.PlainText ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            var normalizedText = NormalizeForFind(text);
            if (normalizedText.Length != text.Length || normalizedText.Length < normalizedNeedle.Length)
            {
                continue;
            }

            var index = normalizedText.IndexOf(normalizedNeedle, StringComparison.Ordinal);
            while (index >= 0)
            {
                ordinal++;
                matches.Add(new ApplicateBlockTextMatch(
                    MatchId: $"b{block.BlockIndex}-o{index}-l{normalizedNeedle.Length}-n{ordinal}",
                    BlockIndex: block.BlockIndex,
                    StartBlockIndex: null,
                    EndBlockIndex: null,
                    BlockLocalOffset: index,
                    Length: normalizedNeedle.Length,
                    NormalizedText: normalizedText.Substring(index, normalizedNeedle.Length),
                    Ordinal: ordinal));

                index = normalizedText.IndexOf(
                    normalizedNeedle,
                    index + normalizedNeedle.Length,
                    StringComparison.Ordinal);
            }
        }

        return new ApplicateBlockTextSearchResult(matches.Count, matches);
    }

    private static string NormalizeForFind(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\u0130')
            {
                builder.Append('i');
                builder.Append('\u0307');
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}

public sealed record ApplicateBlockTextSearchResult(
    int TotalCount,
    IReadOnlyList<ApplicateBlockTextMatch> Matches)
{
    public static ApplicateBlockTextSearchResult Empty { get; } = new(0, []);
}

public sealed record ApplicateBlockTextMatch(
    string MatchId,
    int BlockIndex,
    int? StartBlockIndex,
    int? EndBlockIndex,
    int BlockLocalOffset,
    int Length,
    string NormalizedText,
    int Ordinal);
