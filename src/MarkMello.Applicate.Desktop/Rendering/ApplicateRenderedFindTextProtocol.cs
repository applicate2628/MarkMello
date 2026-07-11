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

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

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
            using var document = JsonDocument.Parse(body, StrictDocumentOptions);
            return ValidateParsedMessage(document.RootElement, bounds.WireUtf8Bytes, context);
        }
        catch (JsonException)
        {
            return Invalid("mm-find-transfer-invalid");
        }
    }

    // Validates one already-parsed message element against protocol schema and the
    // caller's render context. The ingress owner parses the body exactly once and
    // reuses this element, so no downstream layer reparses the raw body (M4).
    internal static ApplicateRenderedFindProtocolValidation ValidateParsedMessage(
        JsonElement root,
        int wireUtf8Bytes,
        ApplicateRenderedFindProtocolContext context)
    {
        try
        {
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
                    wireUtf8Bytes);
            }

            var parsed = type switch
            {
                "find-domain-begin" => ParseBegin(properties),
                "find-text-index-start" => ParseStart(properties),
                "find-text-index-chunk" => ParseChunk(properties),
                "find-text-index-complete" => ParseComplete(properties),
                "rendered-find-unavailable" => ParseUnavailable(properties),
                _ => null,
            };
            if (parsed is null)
            {
                return Invalid("mm-find-transfer-invalid");
            }

            return new ApplicateRenderedFindProtocolValidation(true, parsed, null, false, wireUtf8Bytes);
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

    public static ApplicateRenderedFindRoutingClassification ClassifyMessageForRouting(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        // A body is rendered-find traffic only when a top-level `type` property
        // resolves (after unescaping) to a reserved find type. Anything else -
        // including a `drop-file` whose text embeds a find marker, or malformed
        // non-find JSON - is NonProtocol and must not touch the find domain (M4).
        if (!TryGetTopLevelRenderedFindMessageType(body, out _))
        {
            return ApplicateRenderedFindRoutingClassification.NonProtocol;
        }

        if (!ValidateRawMessageBounds(body).Accepted)
        {
            return ApplicateRenderedFindRoutingClassification.Malformed;
        }

        try
        {
            using var document = JsonDocument.Parse(body, StrictDocumentOptions);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ApplicateRenderedFindRoutingClassification.Candidate
                : ApplicateRenderedFindRoutingClassification.Malformed;
        }
        catch (JsonException)
        {
            return ApplicateRenderedFindRoutingClassification.Malformed;
        }
    }

    // Bounded, string-aware top-level discriminator. Scans only depth-1 object
    // properties (respecting JSON string escapes and skipping nested containers)
    // and reports whether any top-level `type` resolves to a reserved find type.
    // A find marker inside a string value (e.g. drop-file text) is never a top
    // level type, so it cannot misclassify non-find traffic. No JsonDocument is
    // built - the strict parse happens once downstream only when this returns true.
    internal static bool TryGetTopLevelRenderedFindMessageType(string body, out string messageType)
    {
        ArgumentNullException.ThrowIfNull(body);
        messageType = string.Empty;

        var index = SkipWhitespace(body, 0);
        if (index >= body.Length || body[index] != '{')
        {
            return false;
        }

        index++;
        var expectProperty = true;
        while (true)
        {
            index = SkipWhitespace(body, index);
            if (index >= body.Length)
            {
                return false;
            }

            var current = body[index];
            if (current == '}')
            {
                return false;
            }

            if (current == ',')
            {
                if (expectProperty)
                {
                    return false;
                }

                index++;
                expectProperty = true;
                continue;
            }

            if (!expectProperty || current != '"')
            {
                return false;
            }

            if (!TryReadJsonString(body, ref index, out var name))
            {
                return false;
            }

            index = SkipWhitespace(body, index);
            if (index >= body.Length || body[index] != ':')
            {
                return false;
            }

            index = SkipWhitespace(body, index + 1);
            if (index >= body.Length)
            {
                return false;
            }

            if (string.Equals(name, "type", StringComparison.Ordinal) && body[index] == '"')
            {
                if (!TryReadJsonString(body, ref index, out var value))
                {
                    return false;
                }

                if (IsKnownMessageType(value))
                {
                    messageType = value;
                    return true;
                }
            }
            else if (!TrySkipJsonValue(body, ref index))
            {
                return false;
            }

            expectProperty = false;
        }
    }

    private static int SkipWhitespace(string body, int index)
    {
        while (index < body.Length)
        {
            var c = body[index];
            if (c is not (' ' or '\t' or '\r' or '\n'))
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static bool TryReadJsonString(string body, ref int index, out string value)
    {
        value = string.Empty;
        if (index >= body.Length || body[index] != '"')
        {
            return false;
        }

        var builder = new StringBuilder();
        index++;
        while (index < body.Length)
        {
            var c = body[index++];
            if (c == '"')
            {
                value = builder.ToString();
                return true;
            }

            if (c == '\\')
            {
                if (!TryAppendEscape(body, ref index, builder))
                {
                    return false;
                }
            }
            else if (c < ' ')
            {
                return false;
            }
            else
            {
                builder.Append(c);
            }
        }

        return false;
    }

    private static bool TryAppendEscape(string body, ref int index, StringBuilder builder)
    {
        if (index >= body.Length)
        {
            return false;
        }

        var escape = body[index++];
        switch (escape)
        {
            case '"': builder.Append('"'); return true;
            case '\\': builder.Append('\\'); return true;
            case '/': builder.Append('/'); return true;
            case 'b': builder.Append('\b'); return true;
            case 'f': builder.Append('\f'); return true;
            case 'n': builder.Append('\n'); return true;
            case 'r': builder.Append('\r'); return true;
            case 't': builder.Append('\t'); return true;
            case 'u':
                if (index + 4 > body.Length ||
                    !ushort.TryParse(
                        body.AsSpan(index, 4),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var code))
                {
                    return false;
                }

                builder.Append((char)code);
                index += 4;
                return true;
            default:
                return false;
        }
    }

    private static bool TrySkipJsonValue(string body, ref int index)
    {
        index = SkipWhitespace(body, index);
        if (index >= body.Length)
        {
            return false;
        }

        return body[index] switch
        {
            '"' => TrySkipJsonString(body, ref index),
            '{' or '[' => TrySkipJsonContainer(body, ref index),
            _ => TrySkipJsonScalar(body, ref index),
        };
    }

    private static bool TrySkipJsonString(string body, ref int index)
    {
        index++;
        while (index < body.Length)
        {
            var c = body[index++];
            if (c == '"')
            {
                return true;
            }

            if (c == '\\')
            {
                if (index >= body.Length)
                {
                    return false;
                }

                index++;
            }
            else if (c < ' ')
            {
                return false;
            }
        }

        return false;
    }

    private static bool TrySkipJsonContainer(string body, ref int index)
    {
        var depth = 0;
        while (index < body.Length)
        {
            var c = body[index];
            if (c == '"')
            {
                if (!TrySkipJsonString(body, ref index))
                {
                    return false;
                }

                continue;
            }

            if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
            }

            index++;
            if (depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipJsonScalar(string body, ref int index)
    {
        var start = index;
        while (index < body.Length)
        {
            var c = body[index];
            if (c is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')
            {
                break;
            }

            index++;
        }

        return index > start;
    }

    public static ApplicateRenderedFindTransferState CreateTransferState(
        int currentRenderId,
        int minimumProjectionRevision = 1)
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

    private static ApplicateRenderedFindProtocolMessage? ParseUnavailable(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!HasExactFields(properties, "type", "schemaVersion", "textDomain", "renderId", "reason") ||
            !TryGetEnvelope(properties, requireProjection: false, out var envelope) ||
            !TryGetRequiredString(properties, "reason", out var reason))
        {
            return null;
        }

        return new ApplicateRenderedFindUnavailableMessage(envelope.RenderId, reason);
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
            "find-text-index-complete" or
            "rendered-find-unavailable";

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

    public int MinimumProjectionRevision => _minimumProjectionRevision;

    public ApplicateRenderedFindProtocolApplyResult Apply(string body)
        => Apply(ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(_currentRenderId)));

    public ApplicateRenderedFindProtocolApplyResult Apply(ApplicateRenderedFindProtocolValidation validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
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
            if (_staging is not null &&
                validation.Message is ApplicateRenderedFindTransferMessage transferMessage &&
                transferMessage.ProjectionRevision < _staging.Start.ProjectionRevision)
            {
                return Stale();
            }

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
                start.ProjectionRevision >= _minimumProjectionRevision)
            {
                _minimumProjectionRevision = System.Math.Max(
                    _minimumProjectionRevision,
                    checked(_staging.Start.ProjectionRevision + 1));
                _staging = new TransferStaging(start, wireUtf8Bytes);
                return Accepted();
            }

            return Reject("mm-find-transfer-invalid");
        }

        if (start.ProjectionRevision < _minimumProjectionRevision)
        {
            return Reject("mm-find-transfer-invalid", start.ProjectionRevision, advanceWhenUnknown: false);
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
            return Reject("mm-find-transfer-invalid", chunk.ProjectionRevision);
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
            return Reject("mm-find-transfer-invalid", complete.ProjectionRevision);
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
        _minimumProjectionRevision = checked(complete.ProjectionRevision + 1);
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

    public ApplicateRenderedFindProtocolApplyResult RejectCurrentTransfer(string failureId)
    {
        ArgumentException.ThrowIfNullOrEmpty(failureId);
        return Reject(failureId);
    }

    private ApplicateRenderedFindProtocolApplyResult Reject(
        string failureId,
        int? rejectedProjectionRevision = null,
        bool advanceWhenUnknown = true)
    {
        if (_staging is not null)
        {
            _minimumProjectionRevision = System.Math.Max(
                _minimumProjectionRevision,
                checked(_staging.Start.ProjectionRevision + 1));
        }
        else if (rejectedProjectionRevision is int revision)
        {
            _minimumProjectionRevision = System.Math.Max(
                _minimumProjectionRevision,
                checked(revision + 1));
        }
        else if (advanceWhenUnknown)
        {
            _minimumProjectionRevision = checked(_minimumProjectionRevision + 1);
        }

        _staging = null;
        return new ApplicateRenderedFindProtocolApplyResult(
            ApplicateRenderedFindProtocolApplyStatus.Rejected,
            null,
            new ApplicateRenderedFindProtocolRejection(failureId, _minimumProjectionRevision, _currentRenderId));
    }

    private static ApplicateRenderedFindProtocolApplyResult Accepted()
        => new(ApplicateRenderedFindProtocolApplyStatus.Accepted, null, null);

    private static ApplicateRenderedFindProtocolApplyResult Stale()
        => new(
            ApplicateRenderedFindProtocolApplyStatus.Stale,
            null,
            new ApplicateRenderedFindProtocolRejection("mm-find-transfer-stale"));

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

public sealed record ApplicateRenderedFindProtocolRejection(
    string FailureId,
    int MinimumProjectionRevision = 1,
    int RenderId = 0);

public enum ApplicateRenderedFindRoutingClassification
{
    Candidate,
    NonProtocol,
    Malformed,
}

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

public sealed record ApplicateRenderedFindUnavailableMessage(int CurrentRenderId, string Reason)
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
