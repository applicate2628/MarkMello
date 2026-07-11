using System.Text.Json;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateRenderedFindDomainState
{
    public const string RenderedTextDomain = "rendered-dom-v1";

    private int? _currentRenderId;
    private ApplicateRenderedFindTransferState? _transfer;
    private ApplicateRenderedFindTextIndex? _committedIndex;

    private ApplicateRenderedFindDomainState()
    {
    }

    public ApplicateRenderedFindDomainStatus Status { get; private set; } =
        ApplicateRenderedFindDomainStatus.LegacyPlaintext;

    public ApplicateRenderedFindQuery? LatestQuery { get; private set; }

    public static ApplicateRenderedFindDomainState CreateLegacyPlaintext() => new();

    public void BeginRenderedRender(int renderId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderId);

        _currentRenderId = renderId;
        _transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(renderId);
        _committedIndex = null;
        LatestQuery = null;
        Status = ApplicateRenderedFindDomainStatus.RenderedAwaitingBegin;
    }

    public ApplicateRenderedFindResultEnvelope QueryRendered(int renderId, long requestId, string query)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(renderId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        ArgumentNullException.ThrowIfNull(query);

        if (_currentRenderId is int currentRenderId && renderId < currentRenderId)
        {
            return CreateEnvelope(
                renderId,
                requestId,
                query,
                ApplicateRenderedFindResultStatus.Unavailable,
                ApplicateRenderedFindTextSearchResult.Empty);
        }

        if (_currentRenderId != renderId)
        {
            _currentRenderId = renderId;
            _transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(renderId);
            _committedIndex = null;
            Status = ApplicateRenderedFindDomainStatus.RenderedPending;
        }
        else if (Status is ApplicateRenderedFindDomainStatus.LegacyPlaintext or
                 ApplicateRenderedFindDomainStatus.RenderedAwaitingBegin)
        {
            Status = ApplicateRenderedFindDomainStatus.RenderedPending;
        }

        LatestQuery = new ApplicateRenderedFindQuery(renderId, requestId, query);
        return BuildLatestQueryResult();
    }

    public ApplicateRenderedFindDomainApplyResult ApplyProtocolMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var initialRenderId = 0;
        if (_currentRenderId is null && !TryReadRenderId(body, out initialRenderId))
        {
            return RejectWithoutTransfer("mm-find-transfer-invalid");
        }

        var renderId = _currentRenderId ?? initialRenderId;
        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(renderId));
        if (!validation.Accepted)
        {
            if (validation.IsStale)
            {
                return new ApplicateRenderedFindDomainApplyResult(
                    ApplicateRenderedFindProtocolApplyStatus.Stale,
                    Status,
                    null,
                    validation.Rejection);
            }

            return RejectWithoutTransfer(validation.Rejection?.FailureId ?? "mm-find-transfer-invalid");
        }

        if (validation.Message is ApplicateRenderedFindDomainBeginMessage begin)
        {
            if (_currentRenderId is null)
            {
                _currentRenderId = begin.RenderId;
                _transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(begin.RenderId);
            }

            _committedIndex = null;
            Status = ApplicateRenderedFindDomainStatus.RenderedPending;
            return new ApplicateRenderedFindDomainApplyResult(
                ApplicateRenderedFindProtocolApplyStatus.Accepted,
                Status,
                BuildLatestQueryResultOrNull(),
                null);
        }

        if (_currentRenderId is null)
        {
            _currentRenderId = renderId;
        }

        _transfer ??= ApplicateRenderedFindTextProtocol.CreateTransferState(renderId);
        var protocolResult = _transfer.Apply(body);
        switch (protocolResult.Status)
        {
            case ApplicateRenderedFindProtocolApplyStatus.Stale:
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Rejected:
                _committedIndex = null;
                Status = ApplicateRenderedFindDomainStatus.RenderedRejected;
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Committed:
                _committedIndex = protocolResult.CommittedIndex;
                Status = ApplicateRenderedFindDomainStatus.RenderedReady;
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Accepted:
                if (validation.Message is ApplicateRenderedFindTextStartMessage)
                {
                    _committedIndex = null;
                }

                Status = ApplicateRenderedFindDomainStatus.RenderedReceiving;
                break;
            default:
                throw new InvalidOperationException("Unknown rendered-find protocol result.");
        }

        return new ApplicateRenderedFindDomainApplyResult(
            protocolResult.Status,
            Status,
            BuildLatestQueryResultOrNull(),
            protocolResult.Rejection);
    }

    private ApplicateRenderedFindDomainApplyResult RejectWithoutTransfer(string failureId)
    {
        _committedIndex = null;
        Status = ApplicateRenderedFindDomainStatus.RenderedRejected;
        var rejection = new ApplicateRenderedFindProtocolRejection(failureId);
        return new ApplicateRenderedFindDomainApplyResult(
            ApplicateRenderedFindProtocolApplyStatus.Rejected,
            Status,
            BuildLatestQueryResultOrNull(),
            rejection);
    }

    private ApplicateRenderedFindResultEnvelope BuildLatestQueryResult()
    {
        var query = LatestQuery ?? throw new InvalidOperationException("No rendered-find query is current.");
        return Status switch
        {
            ApplicateRenderedFindDomainStatus.RenderedReady when _committedIndex is not null =>
                CreateEnvelope(
                    query.RenderId,
                    query.RequestId,
                    query.Query,
                    ApplicateRenderedFindResultStatus.Ready,
                    _committedIndex.Search(query.Query)),
            ApplicateRenderedFindDomainStatus.RenderedRejected =>
                CreateEnvelope(
                    query.RenderId,
                    query.RequestId,
                    query.Query,
                    ApplicateRenderedFindResultStatus.Unavailable,
                    ApplicateRenderedFindTextSearchResult.Empty),
            _ => CreateEnvelope(
                query.RenderId,
                query.RequestId,
                query.Query,
                ApplicateRenderedFindResultStatus.Pending,
                ApplicateRenderedFindTextSearchResult.Empty),
        };
    }

    private ApplicateRenderedFindResultEnvelope? BuildLatestQueryResultOrNull()
        => LatestQuery is null ? null : BuildLatestQueryResult();

    private static ApplicateRenderedFindResultEnvelope CreateEnvelope(
        int renderId,
        long requestId,
        string query,
        ApplicateRenderedFindResultStatus status,
        ApplicateRenderedFindTextSearchResult result)
        => new(
            renderId,
            requestId,
            query,
            RenderedTextDomain,
            status,
            result.TotalCount,
            result.Matches);

    private static bool TryReadRenderId(string body, out int renderId)
    {
        renderId = 0;
        if (!ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(body).Accepted)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("renderId", out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetInt32(out renderId) &&
                   renderId > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public enum ApplicateRenderedFindDomainStatus
{
    LegacyPlaintext,
    RenderedAwaitingBegin,
    RenderedPending,
    RenderedReceiving,
    RenderedReady,
    RenderedRejected,
}

public enum ApplicateRenderedFindResultStatus
{
    Pending,
    Ready,
    Unavailable,
}

public sealed record ApplicateRenderedFindQuery(int RenderId, long RequestId, string Query);

public sealed record ApplicateRenderedFindResultEnvelope(
    int RenderId,
    long RequestId,
    string Query,
    string TextDomain,
    ApplicateRenderedFindResultStatus Status,
    int TotalCount,
    IReadOnlyList<ApplicateRenderedFindTextMatch> Matches);

public sealed record ApplicateRenderedFindDomainApplyResult(
    ApplicateRenderedFindProtocolApplyStatus ProtocolStatus,
    ApplicateRenderedFindDomainStatus StateStatus,
    ApplicateRenderedFindResultEnvelope? LatestQueryResult,
    ApplicateRenderedFindProtocolRejection? Rejection);
