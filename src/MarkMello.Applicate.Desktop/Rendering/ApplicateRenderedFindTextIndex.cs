using System.Text;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateRenderedFindTextIndex
{
    private readonly int _projectionRevision;
    private readonly int _renderId;
    private readonly IReadOnlyList<ApplicateRenderedFindTextSegment> _segments;

    private ApplicateRenderedFindTextIndex(
        int renderId,
        int projectionRevision,
        IReadOnlyList<ApplicateRenderedFindTextSegment> segments)
    {
        _renderId = renderId;
        _projectionRevision = projectionRevision;
        _segments = segments;
    }

    public static ApplicateRenderedFindTextIndex Create(
        int renderId,
        int projectionRevision,
        IEnumerable<ApplicateRenderedFindTextSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new ApplicateRenderedFindTextIndex(renderId, projectionRevision, segments.ToArray());
    }

    public ApplicateRenderedFindTextSearchResult Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return ApplicateRenderedFindTextSearchResult.Empty;
        }

        var normalizedNeedle = NormalizeForFind(query);
        if (normalizedNeedle.Length == 0 || normalizedNeedle.Length != query.Length)
        {
            return ApplicateRenderedFindTextSearchResult.Empty;
        }

        var matches = new List<ApplicateRenderedFindTextMatch>();
        var ordinal = 0;
        foreach (var segment in _segments)
        {
            var text = segment.Text ?? string.Empty;
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
                var blockLocalOffset = checked(segment.BlockLocalStart + index);
                matches.Add(new ApplicateRenderedFindTextMatch(
                    MatchId: $"r{_renderId}-p{_projectionRevision}-b{segment.BlockIndex}-s{segment.SegmentOrdinal}-o{blockLocalOffset}-l{normalizedNeedle.Length}-n{ordinal}",
                    RenderId: _renderId,
                    ProjectionRevision: _projectionRevision,
                    BlockIndex: segment.BlockIndex,
                    SegmentOrdinal: segment.SegmentOrdinal,
                    BlockLocalOffset: blockLocalOffset,
                    Length: normalizedNeedle.Length,
                    NormalizedText: normalizedText.Substring(index, normalizedNeedle.Length),
                    Ordinal: ordinal));

                index = normalizedText.IndexOf(
                    normalizedNeedle,
                    index + normalizedNeedle.Length,
                    StringComparison.Ordinal);
            }
        }

        return new ApplicateRenderedFindTextSearchResult(matches.Count, matches);
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

public sealed record ApplicateRenderedFindTextSegment(
    int SegmentOrdinal,
    int BlockIndex,
    int BlockLocalStart,
    string Text);

public sealed record ApplicateRenderedFindTextSearchResult(
    int TotalCount,
    IReadOnlyList<ApplicateRenderedFindTextMatch> Matches)
{
    public static ApplicateRenderedFindTextSearchResult Empty { get; } = new(0, []);
}

public sealed record ApplicateRenderedFindTextMatch(
    string MatchId,
    int RenderId,
    int ProjectionRevision,
    int BlockIndex,
    int SegmentOrdinal,
    int BlockLocalOffset,
    int Length,
    string NormalizedText,
    int Ordinal);
