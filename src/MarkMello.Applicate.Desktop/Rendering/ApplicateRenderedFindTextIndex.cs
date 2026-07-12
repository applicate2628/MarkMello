using System.Text;
using System.Threading;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateRenderedFindTextIndex
{
    public const int MaxMaterializedMatches = 5_000;

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
        => Search(query, CancellationToken.None);

    public ApplicateRenderedFindTextSearchResult Search(string query, CancellationToken cancellationToken)
        => Search<object?>(
            query,
            static (descriptor, _) => CreateMatch(descriptor),
            null,
            cancellationToken);

    internal ApplicateRenderedFindTextSearchResult Search<TState>(
        string query,
        Func<ApplicateRenderedFindTextMatchDescriptor, TState, ApplicateRenderedFindTextMatch> descriptorFactory,
        TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(descriptorFactory);
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                if (matches.Count < MaxMaterializedMatches)
                {
                    var blockLocalOffset = checked(segment.BlockLocalStart + index);
                    var descriptor = new ApplicateRenderedFindTextMatchDescriptor(
                        RenderId: _renderId,
                        ProjectionRevision: _projectionRevision,
                        BlockIndex: segment.BlockIndex,
                        SegmentOrdinal: segment.SegmentOrdinal,
                        BlockLocalOffset: blockLocalOffset,
                        Length: normalizedNeedle.Length,
                        NormalizedText: normalizedText.Substring(index, normalizedNeedle.Length),
                        Ordinal: ordinal);
                    matches.Add(descriptorFactory(descriptor, state));
                }

                index = normalizedText.IndexOf(
                    normalizedNeedle,
                    index + normalizedNeedle.Length,
                    StringComparison.Ordinal);
            }
        }

        return new ApplicateRenderedFindTextSearchResult(
            ordinal,
            ordinal > matches.Count,
            matches);
    }

    internal static ApplicateRenderedFindTextMatch CreateMatch(ApplicateRenderedFindTextMatchDescriptor descriptor)
        => new(
            MatchId: $"r{descriptor.RenderId}-p{descriptor.ProjectionRevision}-b{descriptor.BlockIndex}-s{descriptor.SegmentOrdinal}-o{descriptor.BlockLocalOffset}-l{descriptor.Length}-n{descriptor.Ordinal}",
            RenderId: descriptor.RenderId,
            ProjectionRevision: descriptor.ProjectionRevision,
            BlockIndex: descriptor.BlockIndex,
            SegmentOrdinal: descriptor.SegmentOrdinal,
            BlockLocalOffset: descriptor.BlockLocalOffset,
            Length: descriptor.Length,
            NormalizedText: descriptor.NormalizedText,
            Ordinal: descriptor.Ordinal);

    private static string NormalizeForFind(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\u0130')
            {
                builder.Append('i');
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
    bool Truncated,
    IReadOnlyList<ApplicateRenderedFindTextMatch> Matches)
{
    public static ApplicateRenderedFindTextSearchResult Empty { get; } = new(0, false, []);
}

public readonly record struct ApplicateRenderedFindTextMatchDescriptor(
    int RenderId,
    int ProjectionRevision,
    int BlockIndex,
    int SegmentOrdinal,
    int BlockLocalOffset,
    int Length,
    string NormalizedText,
    int Ordinal);

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
