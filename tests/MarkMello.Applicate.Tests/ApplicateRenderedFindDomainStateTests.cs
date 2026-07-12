using System.Reflection;
using System.Text;
using System.Text.Json;
using MarkMello.Applicate.Desktop.Rendering;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRenderedFindDomainStateTests
{
    [Fact]
    public void RenderedStatesReturnRenderedPendingReadyAndUnavailableEnvelopesWithoutPlaintext()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, state.Status);

        state.BeginRenderedRender(renderId: 11);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedAwaitingBegin, state.Status);

        var pending = state.QueryRendered(renderId: 11, requestId: 100, query: "alpha");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedPending, state.Status);

        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedPending, state.ApplyProtocolMessage(DomainBeginJson(renderId: 11)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(StartJson(renderId: 11, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);

        var receiving = state.QueryRendered(renderId: 11, requestId: 101, query: "alpha");
        AssertRenderedEnvelope(receiving, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);

        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(ChunkJson(renderId: 11, projectionRevision: 1, chunkIndex: 0, PartJson(0, 2, 7, 5, 0, "alpha"))).StateStatus);
        var committed = state.ApplyProtocolMessage(CompleteJson(renderId: 11, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, committed.StateStatus);
        Assert.NotNull(committed.LatestQueryResult);
        Assert.Equal(101, committed.LatestQueryResult.RequestId);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, committed.LatestQueryResult.Status);

        var ready = state.QueryRendered(renderId: 11, requestId: 102, query: "alpha");
        AssertRenderedEnvelope(ready, ApplicateRenderedFindResultStatus.Ready, totalCount: 1);
        var match = Assert.Single(ready.Matches);
        Assert.Equal(7, match.BlockLocalOffset);
        Assert.Equal("rendered-dom-v1", ready.TextDomain);
    }

    [Fact]
    public void QueryBeforeBeginAndBeginBeforeQueryBothEnterRenderedPending()
    {
        var queryFirst = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var pending = queryFirst.QueryRendered(renderId: 21, requestId: 1, query: "needle");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedPending, queryFirst.Status);

        var beginFirst = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var apply = beginFirst.ApplyProtocolMessage(DomainBeginJson(renderId: 22));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedPending, apply.StateStatus);
        pending = beginFirst.QueryRendered(renderId: 22, requestId: 2, query: "needle");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
    }

    [Fact]
    public void AcceptedStartBeforeBeginEstablishesCurrentRenderWithoutDiscardingStaging()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var start = state.ApplyProtocolMessage(StartJson(renderId: 23, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, start.StateStatus);

        var pending = state.QueryRendered(renderId: 23, requestId: 3, query: "alpha");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
        state.ApplyProtocolMessage(ChunkJson(renderId: 23, projectionRevision: 1, chunkIndex: 0, PartJson(0, 1, 0, 5, 0, "alpha")));
        var committed = state.ApplyProtocolMessage(CompleteJson(renderId: 23, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));

        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, committed.StateStatus);
        Assert.Equal(1, committed.LatestQueryResult?.TotalCount);
    }

    [Fact]
    public void OnlyLatestCurrentQueryIsRepublishedAndStaleQueriesDoNotReplaceIt()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 25);

        state.QueryRendered(renderId: 25, requestId: 10, query: "first");
        var latest = state.QueryRendered(renderId: 25, requestId: 11, query: "alpha");
        Assert.Equal(11, state.LatestQuery?.RequestId);
        Assert.Equal("alpha", state.LatestQuery?.Query);

        var stale = state.QueryRendered(renderId: 24, requestId: 12, query: "stale");
        Assert.Equal(ApplicateRenderedFindResultStatus.Unavailable, stale.Status);
        Assert.Equal(11, state.LatestQuery?.RequestId);
        Assert.Equal(latest.Query, state.LatestQuery?.Query);

        state.ApplyProtocolMessage(StartJson(renderId: 25, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 25, projectionRevision: 1, chunkIndex: 0, PartJson(0, 1, 0, 5, 0, "alpha")));
        var committed = state.ApplyProtocolMessage(CompleteJson(renderId: 25, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));

        Assert.Equal(11, committed.LatestQueryResult?.RequestId);
        Assert.Equal("alpha", committed.LatestQueryResult?.Query);
        Assert.Equal(1, committed.LatestQueryResult?.TotalCount);
    }

    [Fact]
    public void LatestQueryIdentityIncludesRenderedTextDomain()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();

        state.QueryRendered(renderId: 26, requestId: 13, query: "alpha");

        Assert.NotNull(state.LatestQuery);
        Assert.Equal(26, state.LatestQuery!.RenderId);
        Assert.Equal(13, state.LatestQuery.RequestId);
        Assert.Equal("alpha", state.LatestQuery.Query);
        Assert.Equal(ApplicateRenderedFindDomainState.RenderedTextDomain, state.LatestQuery.TextDomain);
    }

    [Fact]
    public void ReadyEnvelopeCarriesTruncatedExactTotalAndCappedMatches()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 27);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(
            StartJson(
                renderId: 27,
                projectionRevision: 1,
                semanticSegmentCount: 1,
                totalCodeUnits: 5_006,
                chunkCount: 1,
                partCount: 1)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(
            ChunkJson(
                renderId: 27,
                projectionRevision: 1,
                chunkIndex: 0,
                PartJson(0, 1, 0, 5_006, 0, new string('z', 5_006)))).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, state.ApplyProtocolMessage(
            CompleteJson(
                renderId: 27,
                projectionRevision: 1,
                semanticSegmentCount: 1,
                totalCodeUnits: 5_006,
                chunkCount: 1,
                partCount: 1)).StateStatus);

        var envelope = state.QueryRendered(renderId: 27, requestId: 14, query: "z");

        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, envelope.Status);
        Assert.Equal(5_006, envelope.TotalCount);
        Assert.True(envelope.Truncated);
        Assert.Equal(5_000, envelope.Matches.Count);
    }

    [Fact]
    public void ApplyProtocolMessageForHostDoesNotBuildLatestReadyResultWhenInitialRenderIdIsMissing()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        InstallReadyLatestQueryWithoutCurrentRenderForHostRejectionTest(state);
        const string body = """{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1"}""";

        using var document = JsonDocument.Parse(body);
        var result = state.ApplyProtocolMessageForHost(document, Encoding.UTF8.GetByteCount(body));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, result.ProtocolStatus);
        Assert.Null(result.LatestQueryResult);
    }

    [Fact]
    public void ApplyProtocolMessageForHostDoesNotBuildLatestReadyResultWhenParsedValidationRejects()
    {
        var state = CreateReadyRenderedDomain();
        var ready = state.QueryRendered(renderId: 11, requestId: 76, query: "x");
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, ready.Status);
        const string body = """{"type":"find-domain-begin","schemaVersion":2,"textDomain":"rendered-dom-v1","renderId":11}""";

        using var document = JsonDocument.Parse(body);
        var result = state.ApplyProtocolMessageForHost(document, Encoding.UTF8.GetByteCount(body));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, result.ProtocolStatus);
        Assert.Null(result.LatestQueryResult);
    }

    [Fact]
    public void InvalidCompleteRejectsAndNeverSearchesStagingData()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 31);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(StartJson(renderId: 31, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(ChunkJson(renderId: 31, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha"))).StateStatus);

        var duringReceive = state.QueryRendered(renderId: 31, requestId: 3, query: "alpha");
        AssertRenderedEnvelope(duringReceive, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);

        var rejected = state.ApplyProtocolMessage(CompleteJson(renderId: 31, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, rejected.StateStatus);

        var pending = state.QueryRendered(renderId: 31, requestId: 4, query: "alpha");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
    }

    [Fact]
    public void SameRenderReplacementStartKeepsSearchingPreviouslyCommittedIndex()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 35);
        state.ApplyProtocolMessage(StartJson(renderId: 35, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 35, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha")));
        state.ApplyProtocolMessage(CompleteJson(renderId: 35, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, state.QueryRendered(renderId: 35, requestId: 1, query: "alpha").Status);

        var newStart = state.ApplyProtocolMessage(StartJson(renderId: 35, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, newStart.StateStatus);
        var ready = state.QueryRendered(renderId: 35, requestId: 2, query: "alpha");

        AssertRenderedEnvelope(ready, ApplicateRenderedFindResultStatus.Ready, totalCount: 1);
    }

    [Fact]
    public void SameRenderCorruptReplacementRetainsCommittedIndexAndReportsInclusiveRevisionFloor()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 61);
        state.ApplyProtocolMessage(StartJson(renderId: 61, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 61, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha")));
        state.ApplyProtocolMessage(CompleteJson(renderId: 61, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 61, requestId: 1, query: "alpha"),
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 1);

        Assert.Equal(
            ApplicateRenderedFindDomainStatus.RenderedReceiving,
            state.ApplyProtocolMessage(StartJson(renderId: 61, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1)).StateStatus);
        var rejected = state.ApplyProtocolMessage(CompleteJson(renderId: 61, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, rejected.ProtocolStatus);
        Assert.NotNull(rejected.Rejection);
        Assert.Equal(3, rejected.Rejection!.MinimumProjectionRevision);
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 61, requestId: 2, query: "alpha"),
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 1);

        var sameRevision = state.ApplyProtocolMessage(StartJson(renderId: 61, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, sameRevision.ProtocolStatus);
        Assert.NotNull(sameRevision.Rejection);
        Assert.Equal(3, sameRevision.Rejection!.MinimumProjectionRevision);

        var retry = state.ApplyProtocolMessage(StartJson(renderId: 61, projectionRevision: 3, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, retry.ProtocolStatus);
    }

    [Fact]
    public void NewRenderInvalidatesRetainedIndexAndIgnoresStaleReplacementFailure()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 62);
        state.ApplyProtocolMessage(StartJson(renderId: 62, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 62, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha")));
        state.ApplyProtocolMessage(CompleteJson(renderId: 62, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 62, requestId: 1, query: "alpha"),
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 1);

        state.BeginRenderedRender(renderId: 63);
        var pending = state.QueryRendered(renderId: 63, requestId: 2, query: "alpha");
        var staleUnavailable = state.ApplyProtocolMessage(UnavailableJson(renderId: 62, reason: "retry-exhausted"));

        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Stale, staleUnavailable.ProtocolStatus);
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 63, requestId: 3, query: "alpha"),
            ApplicateRenderedFindResultStatus.Pending,
            totalCount: 0);
    }

    [Fact]
    public void RendererUnavailableWithoutCommittedIndexProducesTerminalUnavailable()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 71);
        state.QueryRendered(renderId: 71, requestId: 1, query: "alpha");

        var terminal = state.ApplyProtocolMessage(UnavailableJson(renderId: 71, reason: "rendered-content-unavailable"));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, terminal.ProtocolStatus);
        Assert.Null(terminal.Rejection);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedUnavailable, terminal.StateStatus);
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 71, requestId: 2, query: "alpha"),
            ApplicateRenderedFindResultStatus.Unavailable,
            totalCount: 0);
    }

    [Fact]
    public void RendererUnavailableForSameRenderRetainsCommittedIndex()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 72);
        state.ApplyProtocolMessage(StartJson(renderId: 72, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 72, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha")));
        state.ApplyProtocolMessage(CompleteJson(renderId: 72, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(StartJson(renderId: 72, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));

        var terminal = state.ApplyProtocolMessage(UnavailableJson(renderId: 72, reason: "retry-exhausted"));

        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, terminal.ProtocolStatus);
        Assert.Null(terminal.Rejection);
        AssertRenderedEnvelope(
            state.QueryRendered(renderId: 72, requestId: 1, query: "alpha"),
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 1);
    }

    [Fact]
    public void RejectionDiscardsStagingAndAllowsOnlyNewerProjectionRevisionRetry()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 41);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(StartJson(renderId: 41, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, state.ApplyProtocolMessage(CompleteJson(renderId: 41, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);

        var sameRevision = state.ApplyProtocolMessage(StartJson(renderId: 41, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, sameRevision.StateStatus);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, sameRevision.ProtocolStatus);

        var newerRevision = state.ApplyProtocolMessage(StartJson(renderId: 41, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, newerRevision.StateStatus);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, newerRevision.ProtocolStatus);
    }

    [Fact]
    public void NewRenderResetClearsReadyIndexAndStaleMessagesDoNotChangeState()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 51);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(StartJson(renderId: 51, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(ChunkJson(renderId: 51, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha"))).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, state.ApplyProtocolMessage(CompleteJson(renderId: 51, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1)).StateStatus);

        var stale = state.ApplyProtocolMessage(StartJson(renderId: 50, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Stale, stale.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, stale.StateStatus);

        state.BeginRenderedRender(renderId: 52);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedAwaitingBegin, state.Status);

        var pending = state.QueryRendered(renderId: 52, requestId: 5, query: "alpha");
        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
    }

    private static void AssertRenderedEnvelope(ApplicateRenderedFindResultEnvelope envelope, ApplicateRenderedFindResultStatus status, int totalCount)
    {
        Assert.Equal("rendered-dom-v1", envelope.TextDomain);
        Assert.Equal(status, envelope.Status);
        Assert.Equal(totalCount, envelope.TotalCount);
        if (status != ApplicateRenderedFindResultStatus.Ready)
        {
            Assert.Empty(envelope.Matches);
        }
    }

    private static string DomainBeginJson(int renderId)
        => $$"""{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}}}""";

    private static string StartJson(int renderId, int projectionRevision, int semanticSegmentCount, int totalCodeUnits, int chunkCount, int partCount)
        => $$"""{"type":"find-text-index-start","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","semanticSegmentCount":{{semanticSegmentCount}},"totalCodeUnits":{{totalCodeUnits}},"chunkCount":{{chunkCount}},"partCount":{{partCount}}}""";

    private static string ChunkJson(int renderId, int projectionRevision, int chunkIndex, params string[] parts)
        => $$"""{"type":"find-text-index-chunk","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","chunkIndex":{{chunkIndex}},"parts":[{{string.Join(",", parts)}}]}""";

    private static string CompleteJson(int renderId, int projectionRevision, int semanticSegmentCount, int totalCodeUnits, int chunkCount, int partCount)
        => $$"""{"type":"find-text-index-complete","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"projectionRevision":{{projectionRevision}},"transferId":"{{renderId}}:{{projectionRevision}}","semanticSegmentCount":{{semanticSegmentCount}},"totalCodeUnits":{{totalCodeUnits}},"chunkCount":{{chunkCount}},"partCount":{{partCount}}}""";

    private static string UnavailableJson(int renderId, string reason)
        => $$"""{"type":"rendered-find-unavailable","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":{{renderId}},"reason":"{{reason}}"}""";

    private static string PartJson(int segmentOrdinal, int blockIndex, int blockLocalStart, int segmentCodeUnitLength, int partOffset, string text)
        => $$"""{"segmentOrdinal":{{segmentOrdinal}},"blockIndex":{{blockIndex}},"blockLocalStart":{{blockLocalStart}},"segmentCodeUnitLength":{{segmentCodeUnitLength}},"partOffset":{{partOffset}},"text":"{{text}}"}""";

    private static ApplicateRenderedFindDomainState CreateReadyRenderedDomain()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 11);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(
            StartJson(renderId: 11, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, state.ApplyProtocolMessage(
            ChunkJson(renderId: 11, projectionRevision: 1, chunkIndex: 0, PartJson(0, 1, 0, 1, 0, "x"))).StateStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, state.ApplyProtocolMessage(
            CompleteJson(renderId: 11, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 1, chunkCount: 1, partCount: 1)).StateStatus);
        return state;
    }

    private static void InstallReadyLatestQueryWithoutCurrentRenderForHostRejectionTest(
        ApplicateRenderedFindDomainState state)
    {
        var type = typeof(ApplicateRenderedFindDomainState);
        SetPrivateField(
            state,
            "_committedIndex",
            ApplicateRenderedFindTextIndex.Create(
                renderId: 99,
                projectionRevision: 1,
                [new ApplicateRenderedFindTextSegment(0, 1, 0, "needle")]));
        SetPrivateField(
            state,
            "<LatestQuery>k__BackingField",
            new ApplicateRenderedFindQuery(
                RenderId: 99,
                RequestId: 88,
                Query: "needle",
                TextDomain: ApplicateRenderedFindDomainState.RenderedTextDomain));
        SetPrivateField(
            state,
            "<Status>k__BackingField",
            ApplicateRenderedFindDomainStatus.RenderedReady);
        Assert.Null(type.GetField("_currentRenderId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
    }

    private static void SetPrivateField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

}
