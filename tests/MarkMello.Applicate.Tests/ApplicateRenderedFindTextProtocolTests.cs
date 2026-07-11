using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRenderedFindTextProtocolTests
{
    [Theory]
    [InlineData("""{"type":"\u0066ind-domain-begin","renderId":11}""")]
    [InlineData("""{"\u0074ype":"find-domain-begin","renderId":11}""")]
    [InlineData("""{ "renderId" : 11 , "type" : "find-text-index-start" }""")]
    [InlineData("""{"type":"other","type":"\u0066ind-text-index-chunk"}""")]
    [InlineData("""{"type":"find-text-index-complete","type":"other"}""")]
    public void RoutingClassifierFindsAnySemanticReservedRootType(string body)
    {
        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.Candidate,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
    }

    [Theory]
    [InlineData("""{"type":"minimap-state","note":"find-domain-begin"}""")]
    [InlineData("""{"type":"find-domain-begin-extra"}""")]
    [InlineData("""{"type":"minimap-state","nested":{"type":"find-domain-begin"}}""")]
    [InlineData("""{"type":"minimap-state","note":"\u0066ind-domain-begin"}""")]
    public void RoutingClassifierIgnoresUnknownNestedAndTypeLikeText(string body)
    {
        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.NonProtocol,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
    }

    [Theory]
    [InlineData("""{"type":"find-domain-begin",}""")]
    [InlineData("""{"type":"find-domain-begin"/*comment*/}""")]
    [InlineData("{\"type\":\"find-domain-begin\"")]
    [InlineData("""{"type":"minimap-state"} trailing""")]
    [InlineData("""{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}}}}""")]
    public void RoutingClassifierSeparatesMalformedBodiesFromValidNonProtocolJson(string body)
    {
        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.Malformed,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
    }

    [Fact]
    public void EscapedReservedDuplicateRoutesToExactParserAndIsRejected()
    {
        const string body = """{"type":"other","\u0074ype":"\u0066ind-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11}""";

        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.Candidate,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
        Assert.False(ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(11)).Accepted);
    }

    [Fact]
    public void RawMessageBoundsAcceptExactEdgesAndRejectOneOver()
    {
        var exactCodeUnits = new string('x', ApplicateRenderedFindTextProtocol.MaxMessageCodeUnits);
        var oneCodeUnitOver = exactCodeUnits + "x";
        var exactUtf8 = new string('\u00e9', ApplicateRenderedFindTextProtocol.MaxMessageUtf8Bytes / 2);
        var oneUtf8ByteOver = exactUtf8 + "x";

        Assert.True(ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(exactCodeUnits).Accepted);
        Assert.False(ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(oneCodeUnitOver).Accepted);
        Assert.True(ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(exactUtf8).Accepted);
        Assert.False(ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(oneUtf8ByteOver).Accepted);
    }

    [Theory]
    [InlineData("""{"type":"find-domain-begin","type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"extra":true}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":"1","textDomain":"rendered-dom-v1","renderId":11}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1.5,"textDomain":"rendered-dom-v1","renderId":11}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":-1}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":2147483648}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":10,"renderId":11}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":7,"renderId":11}""")]
    [InlineData("""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"plain","renderId":11}""")]
    public void ParserRejectsDuplicateUnknownWrongKindFractionalNegativeOverflowAndWrongDomain(string body)
    {
        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(currentRenderId: 11));

        Assert.False(validation.Accepted);
        Assert.NotNull(validation.Rejection);
    }

    [Fact]
    public void ParserRejectsDepthGreaterThanEight()
    {
        const string body = """
        {"type":"find-text-index-chunk","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","chunkIndex":0,"parts":[{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1,"partOffset":0,"text":"x","nested":{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}}]}
        """;

        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(currentRenderId: 11));

        Assert.False(validation.Accepted);
    }

    [Theory]
    [InlineData("""{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1,"partOffset":0,"text":"x","text":"x"}""")]
    [InlineData("""{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1,"partOffset":0,"text":"x","extra":0}""")]
    [InlineData("""{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1.5,"partOffset":0,"text":"x"}""")]
    [InlineData("""{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":2147483647,"segmentCodeUnitLength":1,"partOffset":0,"text":"x"}""")]
    public void ParserRejectsInvalidPartFieldsAndArithmetic(string part)
    {
        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            ChunkJson(11, 1, 0, part),
            new ApplicateRenderedFindProtocolContext(11));

        Assert.False(validation.Accepted);
        Assert.NotNull(validation.Rejection);
    }

    [Theory]
    [MemberData(nameof(OverBudgetStartMessages))]
    public void ParserRejectsDeclaredCountCaps(string body)
    {
        var validation = ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(currentRenderId: 11));

        Assert.False(validation.Accepted);
        Assert.Equal("mm-find-transfer-budget-rejected", validation.Rejection!.FailureId);
    }

    [Fact]
    public void ParserAcceptsExactDeclaredCountCaps()
    {
        Assert.True(ApplicateRenderedFindTextProtocol.ParseMessage(
            StartJson(
                semanticSegmentCount: ApplicateRenderedFindTextProtocol.MaxSemanticSegments,
                totalCodeUnits: ApplicateRenderedFindTextProtocol.MaxSemanticSegments,
                chunkCount: 128,
                partCount: ApplicateRenderedFindTextProtocol.MaxSemanticSegments),
            new ApplicateRenderedFindProtocolContext(11)).Accepted);

        Assert.True(ApplicateRenderedFindTextProtocol.ParseMessage(
            StartJson(
                semanticSegmentCount: 1,
                totalCodeUnits: ApplicateRenderedFindTextProtocol.MaxProjectionCodeUnits,
                chunkCount: 256,
                partCount: 256),
            new ApplicateRenderedFindProtocolContext(11)).Accepted);

        Assert.True(ApplicateRenderedFindTextProtocol.ParseMessage(
            StartJson(
                semanticSegmentCount: 1,
                totalCodeUnits: 1,
                chunkCount: 256,
                partCount: ApplicateRenderedFindTextProtocol.MaxTransferParts),
            new ApplicateRenderedFindProtocolContext(11)).Accepted);
    }

    [Fact]
    public void ParserAcceptsExactTextPartCapAndRejectsOneOver()
    {
        var exactText = new string('x', ApplicateRenderedFindTextProtocol.MaxTextPartCodeUnits);
        var exact = ApplicateRenderedFindTextProtocol.ParseMessage(
            ChunkJson(11, 1, 0, PartJson(0, 0, 0, exactText.Length, 0, exactText)),
            new ApplicateRenderedFindProtocolContext(11));
        var oneOver = ApplicateRenderedFindTextProtocol.ParseMessage(
            ChunkJson(11, 1, 0, PartJson(0, 0, 0, exactText.Length + 1, 0, exactText + "x")),
            new ApplicateRenderedFindProtocolContext(11));

        Assert.True(exact.Accepted);
        Assert.False(oneOver.Accepted);
        Assert.Equal("mm-find-transfer-budget-rejected", oneOver.Rejection!.FailureId);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("11:2")]
    public void ParserRejectsWrongTransferId(string transferId)
    {
        var body = StartJson().Replace("\"11:1\"", $"\"{transferId}\"", StringComparison.Ordinal);

        Assert.False(ApplicateRenderedFindTextProtocol.ParseMessage(
            body,
            new ApplicateRenderedFindProtocolContext(11)).Accepted);
    }

    [Fact]
    public void TransferRejectsChunkOrderGapsAndDuplicates()
    {
        var gap = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(gap.Apply(StartJson(semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)));
        AssertRejected(gap.Apply(ChunkJson(11, 1, 1, PartJson(0, 2, 0, 1, 0, "x"))));

        var duplicate = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(duplicate.Apply(StartJson(semanticSegmentCount: 2, totalCodeUnits: 2, chunkCount: 2, partCount: 2)));
        AssertAccepted(duplicate.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 0, 1, 0, "x"))));
        AssertRejected(duplicate.Apply(ChunkJson(11, 1, 0, PartJson(1, 2, 1, 1, 0, "y"))));
    }

    [Fact]
    public void TransferRejectsWrongTransferStaleRenderAndStaleProjectionRevision()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);

        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Stale,
            transfer.Apply(StartJson(renderId: 10, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)).Status);

        transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11, minimumProjectionRevision: 2);
        AssertRejected(transfer.Apply(StartJson(projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)));

        transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(transfer.Apply(StartJson(semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)));
        AssertRejected(transfer.Apply(ChunkJson(11, 2, 0, PartJson(0, 2, 0, 1, 0, "x"))));
    }

    [Fact]
    public void NewerStartAtomicallyReplacesUnfinishedStaging()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson(chunkCount: 2, partCount: 2, totalCodeUnits: 2)));
        AssertAccepted(transfer.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 0, 1, 0, "x"))));

        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 2)));
        AssertAccepted(transfer.Apply(ChunkJson(11, 2, 0, PartJson(0, 2, 0, 1, 0, "y"))));
        var commit = transfer.Apply(CompleteJson(projectionRevision: 2));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Committed, commit.Status);
        Assert.Single(commit.CommittedIndex!.Search("y").Matches);
        Assert.Empty(commit.CommittedIndex.Search("x").Matches);
    }

    [Fact]
    public void MalformedStaleRenderMessageDoesNotMutateCurrentStaging()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson()));
        var malformedStale = StartJson(renderId: 10).Replace("}", ",\"extra\":true}", StringComparison.Ordinal);

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Stale, transfer.Apply(malformedStale).Status);
        AssertAccepted(transfer.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 0, 1, 0, "x"))));
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            transfer.Apply(CompleteJson()).Status);
    }

    [Fact]
    public void DelayedOlderChunkDoesNotMutateNewerStaging()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 1)));
        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 2)));

        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Stale,
            transfer.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 0, 1, 0, "o"))).Status);

        AssertAccepted(transfer.Apply(ChunkJson(11, 2, 0, PartJson(0, 2, 0, 1, 0, "x"))));
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            transfer.Apply(CompleteJson(projectionRevision: 2)).Status);
    }

    [Fact]
    public void DelayedOlderCompleteDoesNotMutateNewerStaging()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 1)));
        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 2)));

        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Stale,
            transfer.Apply(CompleteJson(projectionRevision: 1)).Status);

        AssertAccepted(transfer.Apply(ChunkJson(11, 2, 0, PartJson(0, 2, 0, 1, 0, "x"))));
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            transfer.Apply(CompleteJson(projectionRevision: 2)).Status);
    }

    [Fact]
    public void TransferRejectsMalformedSplitsAndNonmonotonicSegments()
    {
        var splitGap = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(splitGap.Apply(StartJson(semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 2)));
        AssertRejected(splitGap.Apply(ChunkJson(
            11, 1, 0,
            PartJson(0, 2, 0, 4, 0, "ab"),
            PartJson(0, 2, 0, 4, 3, "d"))));

        var skippedOrdinal = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(skippedOrdinal.Apply(StartJson(semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)));
        AssertRejected(skippedOrdinal.Apply(ChunkJson(11, 1, 0, PartJson(1, 2, 0, 1, 0, "x"))));

        var blockOrder = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(blockOrder.Apply(StartJson(semanticSegmentCount: 2, totalCodeUnits: 2, chunkCount: 1, partCount: 2)));
        AssertRejected(blockOrder.Apply(ChunkJson(
            11, 1, 0,
            PartJson(0, 5, 10, 1, 0, "x"),
            PartJson(1, 4, 0, 1, 0, "y"))));

        var offsetOrder = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(offsetOrder.Apply(StartJson(semanticSegmentCount: 2, totalCodeUnits: 2, chunkCount: 1, partCount: 2)));
        AssertRejected(offsetOrder.Apply(ChunkJson(
            11, 1, 0,
            PartJson(0, 5, 10, 1, 0, "x"),
            PartJson(1, 5, 10, 1, 0, "y"))));
    }

    [Fact]
    public void TransferCommitsOnlyWhenDeclaredTotalsExactlyMatch()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(currentRenderId: 11);
        AssertAccepted(transfer.Apply(StartJson(semanticSegmentCount: 2, totalCodeUnits: 9, chunkCount: 1, partCount: 2)));
        AssertAccepted(transfer.Apply(ChunkJson(
            11, 1, 0,
            PartJson(0, 2, 0, 5, 0, "alpha"),
            PartJson(1, 2, 5, 4, 0, "beta"))));

        var commit = transfer.Apply(CompleteJson(semanticSegmentCount: 2, totalCodeUnits: 9, chunkCount: 1, partCount: 2));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Committed, commit.Status);
        Assert.NotNull(commit.CommittedIndex);
    }

    [Fact]
    public void TransferReconstructsMultipartSegmentsAcrossChunksBeforeAtomicCommit()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson(semanticSegmentCount: 1, totalCodeUnits: 9, chunkCount: 2, partCount: 2)));
        AssertAccepted(transfer.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 4, 9, 0, "alpha"))));
        AssertAccepted(transfer.Apply(ChunkJson(11, 1, 1, PartJson(0, 2, 4, 9, 5, "beta"))));

        var commit = transfer.Apply(CompleteJson(semanticSegmentCount: 1, totalCodeUnits: 9, chunkCount: 2, partCount: 2));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Committed, commit.Status);
        Assert.Single(commit.CommittedIndex!.Search("abet").Matches);
        Assert.Equal(8, commit.CommittedIndex.Search("abet").Matches[0].BlockLocalOffset);
    }

    [Fact]
    public void InvalidCompleteDiscardsStagingAndAllowsOnlyNewerRevisionRetry()
    {
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(StartJson()));
        AssertAccepted(transfer.Apply(ChunkJson(11, 1, 0, PartJson(0, 2, 0, 1, 0, "x"))));
        AssertRejected(transfer.Apply(CompleteJson(totalCodeUnits: 2)));

        AssertRejected(transfer.Apply(StartJson()));
        AssertAccepted(transfer.Apply(StartJson(projectionRevision: 2)));
        AssertAccepted(transfer.Apply(ChunkJson(11, 2, 0, PartJson(0, 2, 0, 1, 0, "y"))));
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            transfer.Apply(CompleteJson(projectionRevision: 2)).Status);
    }

    [Fact]
    public void TransferWireBudgetAcceptsExactEdgeAndRejectsOneOver()
    {
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Committed,
            ApplyTransferAtWireBudget(extraBytes: 0).Status);
        var oneOver = ApplyTransferAtWireBudget(extraBytes: 1);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, oneOver.Status);
        Assert.Equal("mm-find-transfer-budget-rejected", oneOver.Rejection!.FailureId);
    }

    public static IEnumerable<object[]> OverBudgetStartMessages()
    {
        yield return [StartJson(semanticSegmentCount: ApplicateRenderedFindTextProtocol.MaxSemanticSegments + 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)];
        yield return [StartJson(semanticSegmentCount: 1, totalCodeUnits: ApplicateRenderedFindTextProtocol.MaxProjectionCodeUnits + 1, chunkCount: 1, partCount: 1)];
        yield return [StartJson(semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: ApplicateRenderedFindTextProtocol.MaxTransferParts + 1)];
    }

    private static void AssertAccepted(ApplicateRenderedFindProtocolApplyResult result)
        => Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, result.Status);

    private static void AssertRejected(ApplicateRenderedFindProtocolApplyResult result)
        => Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, result.Status);

    private static ApplicateRenderedFindProtocolApplyResult ApplyTransferAtWireBudget(int extraBytes)
    {
        const int chunkCount = 256;
        var start = StartJson(
            semanticSegmentCount: chunkCount,
            totalCodeUnits: chunkCount,
            chunkCount: chunkCount,
            partCount: chunkCount);
        var complete = CompleteJson(
            semanticSegmentCount: chunkCount,
            totalCodeUnits: chunkCount,
            chunkCount: chunkCount,
            partCount: chunkCount);
        var targetChunkBytes = ApplicateRenderedFindTextProtocol.MaxTransferUtf8Bytes + extraBytes -
                               start.Length - complete.Length;
        var transfer = ApplicateRenderedFindTextProtocol.CreateTransferState(11);
        AssertAccepted(transfer.Apply(start));

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var body = ChunkJson(
                11,
                1,
                chunkIndex,
                PartJson(chunkIndex, 0, chunkIndex, 1, 0, "x"));
            var chunksRemaining = chunkCount - chunkIndex;
            var targetBodyBytes = checked((int)(targetChunkBytes / chunksRemaining));
            targetChunkBytes -= targetBodyBytes;
            Assert.InRange(targetBodyBytes, body.Length, ApplicateRenderedFindTextProtocol.MaxMessageUtf8Bytes);
            body += new string(' ', targetBodyBytes - body.Length);
            AssertAccepted(transfer.Apply(body));
        }

        Assert.Equal(0, targetChunkBytes);
        return transfer.Apply(complete);
    }

    private static string StartJson(
        int renderId = 11,
        int projectionRevision = 1,
        int semanticSegmentCount = 1,
        int totalCodeUnits = 1,
        int chunkCount = 1,
        int partCount = 1)
        => $$"""
        {"type":"find-text-index-start","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","semanticSegmentCount":{{semanticSegmentCount}},"totalCodeUnits":{{totalCodeUnits}},"chunkCount":{{chunkCount}},"partCount":{{partCount}}}
        """;

    private static string ChunkJson(int renderId = 11, int projectionRevision = 1, int chunkIndex = 0, params string[] parts)
        => $$"""
        {"type":"find-text-index-chunk","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","chunkIndex":{{chunkIndex}},"parts":[{{string.Join(",", parts)}}]}
        """;

    private static string CompleteJson(
        int renderId = 11,
        int projectionRevision = 1,
        int semanticSegmentCount = 1,
        int totalCodeUnits = 1,
        int chunkCount = 1,
        int partCount = 1)
        => $$"""
        {"type":"find-text-index-complete","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","semanticSegmentCount":{{semanticSegmentCount}},"totalCodeUnits":{{totalCodeUnits}},"chunkCount":{{chunkCount}},"partCount":{{partCount}}}
        """;

    private static string PartJson(
        int segmentOrdinal,
        int blockIndex,
        int blockLocalStart,
        int segmentCodeUnitLength,
        int partOffset,
        string text)
        => $$"""
        {"segmentOrdinal":{{segmentOrdinal}},"blockIndex":{{blockIndex}},"blockLocalStart":{{blockLocalStart}},"segmentCodeUnitLength":{{segmentCodeUnitLength}},"partOffset":{{partOffset}},"text":"{{text}}"}
        """;
}
