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

        var unavailable = state.QueryRendered(renderId: 31, requestId: 4, query: "alpha");
        AssertRenderedEnvelope(unavailable, ApplicateRenderedFindResultStatus.Unavailable, totalCount: 0);
    }

    [Fact]
    public void NewerProjectionStartStopsSearchingPreviouslyCommittedIndex()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId: 35);
        state.ApplyProtocolMessage(StartJson(renderId: 35, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        state.ApplyProtocolMessage(ChunkJson(renderId: 35, projectionRevision: 1, chunkIndex: 0, PartJson(0, 4, 0, 5, 0, "alpha")));
        state.ApplyProtocolMessage(CompleteJson(renderId: 35, projectionRevision: 1, semanticSegmentCount: 1, totalCodeUnits: 5, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, state.QueryRendered(renderId: 35, requestId: 1, query: "alpha").Status);

        var newStart = state.ApplyProtocolMessage(StartJson(renderId: 35, projectionRevision: 2, semanticSegmentCount: 1, totalCodeUnits: 4, chunkCount: 1, partCount: 1));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, newStart.StateStatus);
        var pending = state.QueryRendered(renderId: 35, requestId: 2, query: "alpha");

        AssertRenderedEnvelope(pending, ApplicateRenderedFindResultStatus.Pending, totalCount: 0);
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

    private static string PartJson(int segmentOrdinal, int blockIndex, int blockLocalStart, int segmentCodeUnitLength, int partOffset, string text)
        => $$"""{"segmentOrdinal":{{segmentOrdinal}},"blockIndex":{{blockIndex}},"blockLocalStart":{{blockLocalStart}},"segmentCodeUnitLength":{{segmentCodeUnitLength}},"partOffset":{{partOffset}},"text":"{{text}}"}""";
}
