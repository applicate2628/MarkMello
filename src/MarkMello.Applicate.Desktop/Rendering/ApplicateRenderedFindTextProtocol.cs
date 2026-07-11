using System.Text;
using System.Text.Json;

namespace MarkMello.Applicate.Desktop.Rendering;

public static class ApplicateRenderedFindTextProtocol
{
    public const int MaxMessageUtf8Bytes = 262_144;
    public const int MaxMessageCodeUnits = 262_144;
    public const int MaxChunkParts = 4_096;
    public const int MaxTextPartCodeUnits = 65_536;
    public const int MaxProjectionCodeUnits = 16_777_216;
    public const int MaxSemanticSegments = 524_288;
    public const int MaxTransferParts = 1_048_576;
    public const long MaxTransferUtf8Bytes = 67_108_864;

    private const string TextDomain = "rendered-dom-v1";
    private const int SchemaVersion = 1;

    public static ApplicateRenderedFindProtocolValidation ValidateRawMessageBounds(string body)
    {
        if (string.IsNullOrEmpty(body) || body.Length > MaxMessageCodeUnits)
        {
            return Invalid("mm-find-transfer-budget-rejected");
        }

        try
        {
            var wireUtf8Bytes = Encoding.UTF8.GetByteCount(body);
            return wireUtf8Bytes <= MaxMessageUtf8Bytes
                ? new ApplicateRenderedFindProtocolValidation(true, null, null, false, wireUtf8Bytes)
                : Invalid("mm-find-transfer-budget-rejected");
        }
        catch (ArgumentException)
        {
            return Invalid("mm-find-transfer-budget-rejected");
        }
        catch (OverflowException)
        {
            return Invalid("mm-find-transfer-budget-rejected");
        }
    }

    public static ApplicateRenderedFindProtocolValidation ParseMessage(
        string body,
        ApplicateRenderedFindProtocolContext context)
    {
        var bounds = ValidateRawMessageBounds(body);
        if (!bounds.Accepted)
        {
            return bounds;
        }

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperties(root, out var properties) ||
                !TryGetRequiredString(properties, "type", out var type) ||
                !IsKnownMessageType(type) ||
                !TryGetNonNegativeInt(properties, "renderId", out var rootRenderId) ||
                rootRenderId == 0)
            {
                return Invalid("mm-find-transfer-invalid");
            }

            if (rootRenderId != context.CurrentRenderId)
            {
                return new ApplicateRenderedFindProtocolValidation(
                    false,
                    null,
                    new ApplicateRenderedFindProtocolRejection("mm-find-transfer-stale"),
                    true,
                    bounds.WireUtf8Bytes);
            }

            var parsed = type switch
            {
                "find-domain-begin" => ParseBegin(properties),
                "find-text-index-start" => ParseStart(properties),
                "find-text-index-chunk" => ParseChunk(properties),
                "find-text-index-complete" => ParseComplete(properties),
                _ => null,
            };
            if (parsed is null)
            {
                return Invalid("mm-find-transfer-invalid");
            }

            return new ApplicateRenderedFindProtocolValidation(true, parsed, null, false, bounds.WireUtf8Bytes);
        }
        catch (JsonException)
        {
            return Invalid("mm-find-transfer-invalid");
        }
        catch (InvalidOperationException)
        {
            return Invalid("mm-find-transfer-invalid");
        }
        catch (OverflowException)
        {
            return Invalid("mm-find-transfer-budget-rejected");
        }
        catch (RenderedFindBudgetException)
        {
            return Invalid("mm-find-transfer-budget-rejected");
        }
    }

    public static ApplicateRenderedFindTransferState CreateTransferState(
        int currentRenderId,
        int minimumProjectionRevision = 0)
        => new(currentRenderId, minimumProjectionRevision);

    private static ApplicateRenderedFindProtocolMessage? ParseBegin(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!HasExactFields(properties, "type", "schemaVersion", "textDomain", "renderId") ||
            !TryGetEnvelope(properties, requireProjection: false, out var envelope))
        {
            return null;
        }

        return new ApplicateRenderedFindDomainBeginMessage(envelope.RenderId);
    }

    private static ApplicateRenderedFindProtocolMessage? ParseStart(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!HasExactFields(
                properties,
                "type", "schemaVersion", "textDomain", "renderId", "projectionRevision", "transferId",
                "semanticSegmentCount", "totalCodeUnits", "chunkCount", "partCount") ||
            !TryGetEnvelope(properties, requireProjection: true, out var envelope) ||
            !TryGetNonNegativeInt(properties, "semanticSegmentCount", out var semanticSegmentCount) ||
            !TryGetNonNegativeInt(properties, "totalCodeUnits", out var totalCodeUnits) ||
            !TryGetNonNegativeInt(properties, "chunkCount", out var chunkCount) ||
            !TryGetNonNegativeInt(properties, "partCount", out var partCount))
        {
            return null;
        }

        if (semanticSegmentCount > MaxSemanticSegments ||
            totalCodeUnits > MaxProjectionCodeUnits ||
            partCount > MaxTransferParts ||
            chunkCount > MaxTransferParts)
        {
            throw new RenderedFindBudgetException();
        }

        var empty = semanticSegmentCount == 0 && totalCodeUnits == 0 && chunkCount == 0 && partCount == 0;
        var populated = semanticSegmentCount > 0 && totalCodeUnits > 0 && chunkCount > 0 && partCount > 0 &&
                        semanticSegmentCount <= partCount && chunkCount <= partCount;
        if (!empty && !populated)
        {
            return null;
        }

        return new ApplicateRenderedFindTextStartMessage(
            envelope.RenderId,
            envelope.ProjectionRevision,
            envelope.TransferId!,
            semanticSegmentCount,
            totalCodeUnits,
            chunkCount,
            partCount);
    }

    private static ApplicateRenderedFindProtocolMessage? ParseChunk(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!HasExactFields(
                properties,
                "type", "schemaVersion", "textDomain", "renderId", "projectionRevision", "transferId",
                "chunkIndex", "parts") ||
            !TryGetEnvelope(properties, requireProjection: true, out var envelope) ||
            !TryGetNonNegativeInt(properties, "chunkIndex", out var chunkIndex) ||
            !properties.TryGetValue("parts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<ApplicateRenderedFindTextPart>();
        foreach (var partElement in partsElement.EnumerateArray())
        {
            if (parts.Count == MaxChunkParts)
            {
                throw new RenderedFindBudgetException();
            }

            var part = ParsePart(partElement);
            if (part is null)
            {
                return null;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return new ApplicateRenderedFindTextChunkMessage(
            envelope.RenderId,
            envelope.ProjectionRevision,
            envelope.TransferId!,
            chunkIndex,
            parts);
    }

    private static ApplicateRenderedFindProtocolMessage? ParseComplete(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!HasExactFields(
                properties,
                "type", "schemaVersion", "textDomain", "renderId", "projectionRevision", "transferId",
                "semanticSegmentCount", "totalCodeUnits", "chunkCount", "partCount") ||
            !TryGetEnvelope(properties, requireProjection: true, out var envelope) ||
            !TryGetNonNegativeInt(properties, "semanticSegmentCount", out var semanticSegmentCount) ||
            !TryGetNonNegativeInt(properties, "totalCodeUnits", out var totalCodeUnits) ||
            !TryGetNonNegativeInt(properties, "chunkCount", out var chunkCount) ||
            !TryGetNonNegativeInt(properties, "partCount", out var partCount))
        {
            return null;
        }

        if (semanticSegmentCount > MaxSemanticSegments ||
            totalCodeUnits > MaxProjectionCodeUnits ||
            partCount > MaxTransferParts ||
            chunkCount > MaxTransferParts)
        {
            throw new RenderedFindBudgetException();
        }

        return new ApplicateRenderedFindTextCompleteMessage(
            envelope.RenderId,
            envelope.ProjectionRevision,
            envelope.TransferId!,
            semanticSegmentCount,
            totalCodeUnits,
            chunkCount,
            partCount);
    }

    private static ApplicateRenderedFindTextPart? ParsePart(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperties(element, out var properties) ||
            !HasExactFields(
                properties,
                "segmentOrdinal", "blockIndex", "blockLocalStart", "segmentCodeUnitLength", "partOffset", "text") ||
            !TryGetNonNegativeInt(properties, "segmentOrdinal", out var segmentOrdinal) ||
            !TryGetNonNegativeInt(properties, "blockIndex", out var blockIndex) ||
            !TryGetNonNegativeInt(properties, "blockLocalStart", out var blockLocalStart) ||
            !TryGetNonNegativeInt(properties, "segmentCodeUnitLength", out var segmentCodeUnitLength) ||
            !TryGetNonNegativeInt(properties, "partOffset", out var partOffset) ||
            !TryGetRequiredString(properties, "text", out var text) ||
            string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxTextPartCodeUnits ||
            segmentOrdinal >= MaxSemanticSegments ||
            segmentCodeUnitLength > MaxProjectionCodeUnits)
        {
            throw new RenderedFindBudgetException();
        }

        _ = checked(blockLocalStart + segmentCodeUnitLength);
        return new ApplicateRenderedFindTextPart(
            segmentOrdinal,
            blockIndex,
            blockLocalStart,
            segmentCodeUnitLength,
            partOffset,
            text);
    }

    private static bool TryGetEnvelope(
        IReadOnlyDictionary<string, JsonElement> properties,
        bool requireProjection,
        out ProtocolEnvelope envelope)
    {
        envelope = default;
        if (!TryGetRequiredString(properties, "type", out _) ||
            !TryGetNonNegativeInt(properties, "schemaVersion", out var schemaVersion) ||
            schemaVersion != SchemaVersion ||
            !TryGetRequiredString(properties, "textDomain", out var textDomain) ||
            textDomain != TextDomain ||
            !TryGetNonNegativeInt(properties, "renderId", out var renderId) ||
            renderId == 0)
        {
            return false;
        }

        if (!requireProjection)
        {
            envelope = new ProtocolEnvelope(renderId, 0, null);
            return true;
        }

        if (!TryGetNonNegativeInt(properties, "projectionRevision", out var projectionRevision) ||
            projectionRevision == 0 ||
            !TryGetRequiredString(properties, "transferId", out var transferId) ||
            transferId != $"{renderId}:{projectionRevision}")
        {
            return false;
        }

        envelope = new ProtocolEnvelope(renderId, projectionRevision, transferId);
        return true;
    }

    private static bool TryGetUniqueProperties(
        JsonElement element,
        out IReadOnlyDictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!result.TryAdd(property.Name, property.Value))
            {
                properties = result;
                return false;
            }
        }

        properties = result;
        return true;
    }

    private static bool IsKnownMessageType(string type)
        => type is "find-domain-begin" or
            "find-text-index-start" or
            "find-text-index-chunk" or
            "find-text-index-complete";

    private static bool HasExactFields(
        IReadOnlyDictionary<string, JsonElement> properties,
        params string[] fields)
    {
        if (properties.Count != fields.Length)
        {
            return false;
        }

        foreach (var field in fields)
        {
            if (!properties.ContainsKey(field))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetRequiredString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetNonNegativeInt(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out int value)
    {
        value = 0;
        return properties.TryGetValue(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value) &&
               value >= 0;
    }

    private static ApplicateRenderedFindProtocolValidation Invalid(string failureId)
        => new(false, null, new ApplicateRenderedFindProtocolRejection(failureId), false, 0);

    private readonly record struct ProtocolEnvelope(int RenderId, int ProjectionRevision, string? TransferId);

    private sealed class RenderedFindBudgetException : Exception;
}

public sealed class ApplicateRenderedFindTransferState
{
    private readonly int _currentRenderId;
    private int _minimumProjectionRevision;
    private TransferStaging? _staging;

    internal ApplicateRenderedFindTransferState(int currentRenderId, int minimumProjectionRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentRenderId);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumProjectionRevision);

        _currentRenderId = currentRenderId;
        _minimumProjectionRevision = minimumProjectionRevision;
    }

    public ApplicateRenderedFindProtocolApplyResult Apply(string body)
    {
        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(_currentRenderId));
        if (!validation.Accepted)
        {
            if (validation.IsStale)
            {
                return new ApplicateRenderedFindProtocolApplyResult(
                    ApplicateRenderedFindProtocolApplyStatus.Stale,
                    null,
                    validation.Rejection);
            }

            return Reject(validation.Rejection?.FailureId ?? "mm-find-transfer-invalid");
        }

        try
        {
            return validation.Message switch
            {
                ApplicateRenderedFindTextStartMessage start => ApplyStart(start, validation.WireUtf8Bytes),
                ApplicateRenderedFindTextChunkMessage chunk => ApplyChunk(chunk, validation.WireUtf8Bytes),
                ApplicateRenderedFindTextCompleteMessage complete => ApplyComplete(complete, validation.WireUtf8Bytes),
                _ => Reject("mm-find-transfer-invalid"),
            };
        }
        catch (OverflowException)
        {
            return Reject("mm-find-transfer-budget-rejected");
        }
    }

    private ApplicateRenderedFindProtocolApplyResult ApplyStart(
        ApplicateRenderedFindTextStartMessage start,
        int wireUtf8Bytes)
    {
        if (_staging is not null)
        {
            if (start.ProjectionRevision > _staging.Start.ProjectionRevision &&
                start.ProjectionRevision > _minimumProjectionRevision)
            {
                _minimumProjectionRevision = System.Math.Max(
                    _minimumProjectionRevision,
                    _staging.Start.ProjectionRevision);
                _staging = new TransferStaging(start, wireUtf8Bytes);
                return Accepted();
            }

            return Reject("mm-find-transfer-invalid");
        }

        if (start.ProjectionRevision <= _minimumProjectionRevision)
        {
            return Reject("mm-find-transfer-invalid");
        }

        _staging = new TransferStaging(start, wireUtf8Bytes);
        return Accepted();
    }

    private ApplicateRenderedFindProtocolApplyResult ApplyChunk(
        ApplicateRenderedFindTextChunkMessage chunk,
        int wireUtf8Bytes)
    {
        if (!TryGetMatchingStaging(chunk, out var staging) || chunk.ChunkIndex != staging.NextChunkIndex)
        {
            return Reject("mm-find-transfer-invalid");
        }

        staging.WireUtf8Bytes = checked(staging.WireUtf8Bytes + wireUtf8Bytes);
        if (staging.WireUtf8Bytes > ApplicateRenderedFindTextProtocol.MaxTransferUtf8Bytes)
        {
            return Reject("mm-find-transfer-budget-rejected");
        }

        foreach (var part in chunk.Parts)
        {
            staging.PartCount = checked(staging.PartCount + 1);
            if (staging.PartCount > staging.Start.PartCount ||
                staging.PartCount > ApplicateRenderedFindTextProtocol.MaxTransferParts)
            {
                return Reject("mm-find-transfer-budget-rejected");
            }

            if (!AppendPart(staging, part))
            {
                return Reject("mm-find-transfer-invalid");
            }
        }

        staging.NextChunkIndex = checked(staging.NextChunkIndex + 1);
        return Accepted();
    }

    private ApplicateRenderedFindProtocolApplyResult ApplyComplete(
        ApplicateRenderedFindTextCompleteMessage complete,
        int wireUtf8Bytes)
    {
        if (!TryGetMatchingStaging(complete, out var staging))
        {
            return Reject("mm-find-transfer-invalid");
        }

        staging.WireUtf8Bytes = checked(staging.WireUtf8Bytes + wireUtf8Bytes);
        if (staging.WireUtf8Bytes > ApplicateRenderedFindTextProtocol.MaxTransferUtf8Bytes)
        {
            return Reject("mm-find-transfer-budget-rejected");
        }

        if (!FinishCurrentSegment(staging) ||
            complete.SemanticSegmentCount != staging.Start.SemanticSegmentCount ||
            complete.TotalCodeUnits != staging.Start.TotalCodeUnits ||
            complete.ChunkCount != staging.Start.ChunkCount ||
            complete.PartCount != staging.Start.PartCount ||
            staging.NextChunkIndex != staging.Start.ChunkCount ||
            staging.PartCount != staging.Start.PartCount ||
            staging.Segments.Count != staging.Start.SemanticSegmentCount ||
            staging.TotalCodeUnits != staging.Start.TotalCodeUnits)
        {
            return Reject("mm-find-transfer-invalid");
        }

        var index = ApplicateRenderedFindTextIndex.Create(
            complete.RenderId,
            complete.ProjectionRevision,
            staging.Segments);
        _minimumProjectionRevision = complete.ProjectionRevision;
        _staging = null;
        return new ApplicateRenderedFindProtocolApplyResult(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            index,
            null);
    }

    private static bool AppendPart(TransferStaging staging, ApplicateRenderedFindTextPart part)
    {
        if (staging.CurrentSegment is null || part.SegmentOrdinal != staging.CurrentSegment.SegmentOrdinal)
        {
            if (!FinishCurrentSegment(staging) ||
                part.SegmentOrdinal != staging.Segments.Count ||
                part.PartOffset != 0 ||
                part.SegmentCodeUnitLength > staging.Start.TotalCodeUnits)
            {
                return false;
            }

            if (staging.Segments.Count > 0)
            {
                var previous = staging.Segments[^1];
                if (part.BlockIndex < previous.BlockIndex ||
                    (part.BlockIndex == previous.BlockIndex && part.BlockLocalStart <= previous.BlockLocalStart))
                {
                    return false;
                }
            }

            staging.CurrentSegment = new SegmentStaging(part);
        }

        var current = staging.CurrentSegment;
        if (current is null ||
            part.SegmentOrdinal != current.SegmentOrdinal ||
            part.BlockIndex != current.BlockIndex ||
            part.BlockLocalStart != current.BlockLocalStart ||
            part.SegmentCodeUnitLength != current.SegmentCodeUnitLength ||
            part.PartOffset != current.Text.Length ||
            part.Text.Length > current.SegmentCodeUnitLength - current.Text.Length)
        {
            return false;
        }

        current.Text.Append(part.Text);
        staging.TotalCodeUnits = checked(staging.TotalCodeUnits + part.Text.Length);
        return staging.TotalCodeUnits <= staging.Start.TotalCodeUnits &&
               staging.TotalCodeUnits <= ApplicateRenderedFindTextProtocol.MaxProjectionCodeUnits;
    }

    private static bool FinishCurrentSegment(TransferStaging staging)
    {
        var current = staging.CurrentSegment;
        if (current is null)
        {
            return true;
        }

        if (current.Text.Length != current.SegmentCodeUnitLength)
        {
            return false;
        }

        staging.Segments.Add(new ApplicateRenderedFindTextSegment(
            current.SegmentOrdinal,
            current.BlockIndex,
            current.BlockLocalStart,
            current.Text.ToString()));
        staging.CurrentSegment = null;
        return true;
    }

    private bool TryGetMatchingStaging(
        ApplicateRenderedFindTransferMessage message,
        out TransferStaging staging)
    {
        staging = _staging!;
        return staging is not null &&
               message.RenderId == _currentRenderId &&
               message.ProjectionRevision == staging.Start.ProjectionRevision &&
               message.TransferId == staging.Start.TransferId;
    }

    private ApplicateRenderedFindProtocolApplyResult Reject(string failureId)
    {
        if (_staging is not null)
        {
            _minimumProjectionRevision = System.Math.Max(
                _minimumProjectionRevision,
                _staging.Start.ProjectionRevision);
        }

        _staging = null;
        return new ApplicateRenderedFindProtocolApplyResult(
            ApplicateRenderedFindProtocolApplyStatus.Rejected,
            null,
            new ApplicateRenderedFindProtocolRejection(failureId));
    }

    private static ApplicateRenderedFindProtocolApplyResult Accepted()
        => new(ApplicateRenderedFindProtocolApplyStatus.Accepted, null, null);

    private sealed class TransferStaging(ApplicateRenderedFindTextStartMessage start, long wireUtf8Bytes)
    {
        public ApplicateRenderedFindTextStartMessage Start { get; } = start;
        public long WireUtf8Bytes { get; set; } = wireUtf8Bytes;
        public int NextChunkIndex { get; set; }
        public int PartCount { get; set; }
        public int TotalCodeUnits { get; set; }
        public List<ApplicateRenderedFindTextSegment> Segments { get; } = [];
        public SegmentStaging? CurrentSegment { get; set; }
    }

    private sealed class SegmentStaging(ApplicateRenderedFindTextPart firstPart)
    {
        public int SegmentOrdinal { get; } = firstPart.SegmentOrdinal;
        public int BlockIndex { get; } = firstPart.BlockIndex;
        public int BlockLocalStart { get; } = firstPart.BlockLocalStart;
        public int SegmentCodeUnitLength { get; } = firstPart.SegmentCodeUnitLength;
        public StringBuilder Text { get; } = new(firstPart.Text.Length);
    }
}

public readonly record struct ApplicateRenderedFindProtocolContext
{
    public ApplicateRenderedFindProtocolContext(int currentRenderId)
    {
        CurrentRenderId = currentRenderId;
    }

    public int CurrentRenderId { get; }
}

public sealed record ApplicateRenderedFindProtocolValidation(
    bool Accepted,
    ApplicateRenderedFindProtocolMessage? Message,
    ApplicateRenderedFindProtocolRejection? Rejection,
    bool IsStale,
    int WireUtf8Bytes);

public sealed record ApplicateRenderedFindProtocolRejection(string FailureId);

public enum ApplicateRenderedFindProtocolApplyStatus
{
    Accepted,
    Committed,
    Rejected,
    Stale,
}

public sealed record ApplicateRenderedFindProtocolApplyResult(
    ApplicateRenderedFindProtocolApplyStatus Status,
    ApplicateRenderedFindTextIndex? CommittedIndex,
    ApplicateRenderedFindProtocolRejection? Rejection);

public abstract record ApplicateRenderedFindProtocolMessage(int RenderId);

public abstract record ApplicateRenderedFindTransferMessage(
    int RenderId,
    int ProjectionRevision,
    string TransferId) : ApplicateRenderedFindProtocolMessage(RenderId);

public sealed record ApplicateRenderedFindDomainBeginMessage(int CurrentRenderId)
    : ApplicateRenderedFindProtocolMessage(CurrentRenderId);

public sealed record ApplicateRenderedFindTextStartMessage(
    int CurrentRenderId,
    int CurrentProjectionRevision,
    string CurrentTransferId,
    int SemanticSegmentCount,
    int TotalCodeUnits,
    int ChunkCount,
    int PartCount)
    : ApplicateRenderedFindTransferMessage(CurrentRenderId, CurrentProjectionRevision, CurrentTransferId);

public sealed record ApplicateRenderedFindTextChunkMessage(
    int CurrentRenderId,
    int CurrentProjectionRevision,
    string CurrentTransferId,
    int ChunkIndex,
    IReadOnlyList<ApplicateRenderedFindTextPart> Parts)
    : ApplicateRenderedFindTransferMessage(CurrentRenderId, CurrentProjectionRevision, CurrentTransferId);

public sealed record ApplicateRenderedFindTextCompleteMessage(
    int CurrentRenderId,
    int CurrentProjectionRevision,
    string CurrentTransferId,
    int SemanticSegmentCount,
    int TotalCodeUnits,
    int ChunkCount,
    int PartCount)
    : ApplicateRenderedFindTransferMessage(CurrentRenderId, CurrentProjectionRevision, CurrentTransferId);

public sealed record ApplicateRenderedFindTextPart(
    int SegmentOrdinal,
    int BlockIndex,
    int BlockLocalStart,
    int SegmentCodeUnitLength,
    int PartOffset,
    string Text);
