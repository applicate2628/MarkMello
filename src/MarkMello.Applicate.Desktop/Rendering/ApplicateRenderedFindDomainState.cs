using System.Text.Json;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateRenderedFindDomainState
{
    public const string RenderedTextDomain = "rendered-dom-v1";

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private int? _currentRenderId;
    private ApplicateRenderedFindTransferState? _transfer;
    private ApplicateRenderedFindTextIndex? _committedIndex;
    private bool _terminalUnavailable;

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
        _terminalUnavailable = false;
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
            _terminalUnavailable = false;
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

        var bounds = ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(body);
        if (!bounds.Accepted)
        {
            return RejectWithoutTransfer(bounds.Rejection?.FailureId ?? "mm-find-transfer-budget-rejected");
        }

        try
        {
            using var document = JsonDocument.Parse(body, StrictDocumentOptions);
            return ApplyProtocolMessage(document, bounds.WireUtf8Bytes);
        }
        catch (JsonException)
        {
            return RejectWithoutTransfer("mm-find-transfer-invalid");
        }
    }

    public ApplicateRenderedFindDomainApplyResult ApplyProtocolMessage(JsonDocument document, int wireUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.RootElement;
        var initialRenderId = 0;
        if (_currentRenderId is null && !TryReadRenderId(root, out initialRenderId))
        {
            return RejectWithoutTransfer("mm-find-transfer-invalid");
        }

        var renderId = _currentRenderId ?? initialRenderId;
        var validation = ApplicateRenderedFindTextProtocol.ValidateParsedMessage(
            root,
            wireUtf8Bytes,
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
                _terminalUnavailable = false;
            }

            Status = ResolveStatusWithoutTransfer();
            return new ApplicateRenderedFindDomainApplyResult(
                ApplicateRenderedFindProtocolApplyStatus.Accepted,
                Status,
                BuildLatestQueryResultOrNull(),
                null);
        }

        if (validation.Message is ApplicateRenderedFindUnavailableMessage)
        {
            _transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(renderId);
            _terminalUnavailable = true;
            Status = ApplicateRenderedFindDomainStatus.RenderedUnavailable;
            return new ApplicateRenderedFindDomainApplyResult(
                ApplicateRenderedFindProtocolApplyStatus.Accepted,
                Status,
                BuildLatestQueryResultOrNull(),
                null);
        }

        if (_currentRenderId is null)
        {
            _currentRenderId = renderId;
            _terminalUnavailable = false;
        }

        _transfer ??= ApplicateRenderedFindTextProtocol.CreateTransferState(renderId);
        if (_terminalUnavailable && validation.Message is ApplicateRenderedFindTransferMessage)
        {
            return new ApplicateRenderedFindDomainApplyResult(
                ApplicateRenderedFindProtocolApplyStatus.Stale,
                Status,
                BuildLatestQueryResultOrNull(),
                new ApplicateRenderedFindProtocolRejection(
                    "mm-find-transfer-stale",
                    _transfer.MinimumProjectionRevision,
                    renderId));
        }

        var protocolResult = _transfer.Apply(validation);
        switch (protocolResult.Status)
        {
            case ApplicateRenderedFindProtocolApplyStatus.Stale:
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Rejected:
                Status = ApplicateRenderedFindDomainStatus.RenderedRejected;
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Committed:
                _committedIndex = protocolResult.CommittedIndex;
                _terminalUnavailable = false;
                Status = ApplicateRenderedFindDomainStatus.RenderedReady;
                break;
            case ApplicateRenderedFindProtocolApplyStatus.Accepted:
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

    // Rejects the current rendered-find transfer from an already-known typed
    // ingress failure, without reparsing a body the caller has proven malformed.
    // Legacy-plaintext domains have no rendered-find state to reject (matching
    // RejectInvalidRenderedFindMessageIfCurrent's guard), so they return null.
    public ApplicateRenderedFindDomainApplyResult? RejectCurrentTransfer(string failureId)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureId);
        if (Status == ApplicateRenderedFindDomainStatus.LegacyPlaintext)
        {
            return null;
        }

        return RejectWithoutTransfer(failureId);
    }

    private ApplicateRenderedFindDomainApplyResult RejectWithoutTransfer(string failureId)
    {
        if (_currentRenderId is int currentRenderId)
        {
            _transfer ??= ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId);
        }

        var rejectionResult = _transfer?.RejectCurrentTransfer(failureId);
        Status = ApplicateRenderedFindDomainStatus.RenderedRejected;
        var rejection = rejectionResult?.Rejection ??
                        new ApplicateRenderedFindProtocolRejection(failureId);
        return new ApplicateRenderedFindDomainApplyResult(
            ApplicateRenderedFindProtocolApplyStatus.Rejected,
            Status,
            BuildLatestQueryResultOrNull(),
            rejection);
    }

    private ApplicateRenderedFindResultEnvelope BuildLatestQueryResult()
    {
        var query = LatestQuery ?? throw new InvalidOperationException("No rendered-find query is current.");
        if (_committedIndex is not null)
        {
            return CreateEnvelope(
                query.RenderId,
                query.RequestId,
                query.Query,
                ApplicateRenderedFindResultStatus.Ready,
                _committedIndex.Search(query.Query));
        }

        return Status switch
        {
            ApplicateRenderedFindDomainStatus.RenderedUnavailable =>
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

    private ApplicateRenderedFindDomainStatus ResolveStatusWithoutTransfer()
    {
        if (_terminalUnavailable)
        {
            return ApplicateRenderedFindDomainStatus.RenderedUnavailable;
        }

        return _committedIndex is null
            ? ApplicateRenderedFindDomainStatus.RenderedPending
            : ApplicateRenderedFindDomainStatus.RenderedReady;
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

    private static bool TryReadRenderId(JsonElement root, out int renderId)
    {
        renderId = 0;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("renderId", out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out renderId) &&
               renderId > 0;
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
    RenderedUnavailable,
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
