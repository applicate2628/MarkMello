using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebHostMessagingTests
{
    private static readonly string WebDocumentViewSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Views",
        "ApplicateWebMarkdownDocumentView.cs");

    private static readonly string SharedWebViewHostSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateSharedWebViewHost.cs");

    private static readonly string RendererSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "RendererWeb",
        "src",
        "renderer.ts");

    private static readonly string AirspaceCompositorSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateAirspaceCompositor.cs");

    private static readonly string AirspaceCompositorHostAdaptersSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateAirspaceCompositor.HostAdapters.cs");

    private static readonly string MainWindowSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "ApplicateMainWindow.cs");

    private static readonly string DeletedThemeRevealSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Rendering",
        "ApplicateThemeSwitchReveal" + "Coordinator.cs");

    [Fact]
    public void HostMessagesPreferNativeWebView2ChannelBeforeInvokeScriptFallback()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);

        var serializer = source.IndexOf("var payload = JsonSerializer.Serialize(message);", StringComparison.Ordinal);
        var nativePost = source.IndexOf("TryPostRendererMessageNative(payload)", StringComparison.Ordinal);
        var fallback = source.IndexOf("InvokeRendererAsync($\"window.postMessage({payload},'*');\")", StringComparison.Ordinal);

        Assert.True(serializer >= 0, "PostRendererMessage should serialize the host payload once.");
        Assert.True(nativePost > serializer, "PostRendererMessage should try the native WebView2 message channel first.");
        Assert.True(fallback > nativePost, "InvokeScript/window.postMessage should remain only as a fallback.");
        Assert.Contains("PostWebMessageAsJson", source, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2PostWebMessageAsJsonDelegate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTypedObjectForIUnknown", source, StringComparison.Ordinal);
        Assert.Contains("IWindowsWebView2PlatformHandle", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererListensForNativeWebView2HostMessages()
    {
        var source = File.ReadAllText(RendererSourcePath);

        Assert.Contains("chrome?.webview?.addEventListener", source, StringComparison.Ordinal);
        Assert.Contains("(event) => handleHostMessage(event.data)", source, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"message\", (event) => handleHostMessage(event.data));", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebMessageIngressClassifiesOnceAndReusesTypedOrGenericPayloads()
    {
        var domain = CreateReadyRenderedDomain();

        using var scroll = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            domain,
            """{"type":"scroll","ratio":0.5}""");
        Assert.Equal(ApplicateWebMessageIngressKind.Generic, scroll.Kind);
        Assert.NotNull(scroll.GenericDocument);
        Assert.Equal("scroll", scroll.GenericDocument!.RootElement.GetProperty("type").GetString());

        using var drop = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            domain,
            DropFileJson(new string('x', 1024 * 1024)));
        Assert.Equal(ApplicateWebMessageIngressKind.Generic, drop.Kind);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, domain.Status);

        using var begin = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            BegunRenderedDomain(11),
            """{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11}""");
        Assert.Equal(ApplicateWebMessageIngressKind.RenderedFind, begin.Kind);
        Assert.Null(begin.GenericDocument);

        using var whitespace = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(domain, "  \r\n");
        Assert.Equal(ApplicateWebMessageIngressKind.Ignore, whitespace.Kind);

        using var malformed = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            domain,
            """{"type":"minimap-state""");
        Assert.Equal(ApplicateWebMessageIngressKind.Ignore, malformed.Kind);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, domain.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, domain.QueryRendered(11, 3, "x").Status);
    }

    [Fact]
    public void DeeplyNestedNonFindMessageRoutesToGenericAtDefaultParseDepth()
    {
        // Nested deeper than the bounded find protocol depth (8) but within the JSON
        // default depth (64). Shipped generic dispatch parsed with the default depth
        // (JsonDocument.Parse(body)); the ingress must keep that depth for non-find
        // traffic instead of dropping it under the find protocol's tighter ceiling.
        var domain = CreateReadyRenderedDomain();
        var body = NestedNonFindJson(depth: 20);

        using var ingress = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(domain, body);

        Assert.Equal(ApplicateWebMessageIngressKind.Generic, ingress.Kind);
        Assert.NotNull(ingress.GenericDocument);
        Assert.Equal(
            "minimap-state",
            ingress.GenericDocument!.RootElement.GetProperty("type").GetString());
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, domain.Status);
    }

    [Fact]
    public void OversizedNonFindDropFileBypassesFindBoundsInBothDomainStates()
    {
        var body = DropFileJson(new string('x', 1024 * 1024));
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var rendered = CreateReadyRenderedDomain();

        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            legacy,
            body,
            out var legacyResult));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            rendered,
            body,
            out var renderedResult));

        Assert.Null(legacyResult);
        Assert.Null(renderedResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, rendered.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, rendered.QueryRendered(11, 2, "x").Status);
    }

    [Fact]
    public void DropFileTextContainingFindMarkerBypassesFindBoundsAndPreservesReadyState()
    {
        var body = DropFileJson("before \\\"type\\\":\\\"find-text-index-chunk\\\" after " + new string('x', 1024 * 1024));
        var rendered = CreateReadyRenderedDomain();

        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            rendered,
            body,
            out var result));

        Assert.Null(result);
        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.NonProtocol,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, rendered.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, rendered.QueryRendered(11, 2, "x").Status);
    }

    [Fact]
    public void OversizedRecognizableFindMessageRejectsCurrentRenderedDomainClosed()
    {
        var body = new string(' ', ApplicateRenderedFindTextProtocol.MaxMessageCodeUnits) +
                   """{"\u0074ype":"\u0066ind-domain-begin","renderId":11}""";
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var rendered = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        rendered.BeginRenderedRender(11);

        var legacyResult = ApplicateWebMarkdownDocumentView.RejectInvalidRenderedFindMessageIfCurrent(legacy, body);
        var renderedResult = ApplicateWebMarkdownDocumentView.RejectInvalidRenderedFindMessageIfCurrent(rendered, body);

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.NotNull(renderedResult);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, renderedResult!.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, rendered.Status);
    }

    [Fact]
    public void MalformedGenericJsonDoesNotPoisonCommittedRenderedState()
    {
        const string malformedGeneric = """{"type":"minimap-state""";
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var receiving = CreateReceivingRenderedDomain();
        var ready = CreateReadyRenderedDomain();

        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.NonProtocol,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(malformedGeneric));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            legacy,
            malformedGeneric,
            out var legacyResult));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            receiving,
            malformedGeneric,
            out var receivingResult));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            ready,
            malformedGeneric,
            out var readyResult));

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.Null(receivingResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, receiving.Status);
        Assert.Null(readyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, ready.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, ready.QueryRendered(11, 2, "x").Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t")]
    public void EmptyOrWhitespaceInboundBodyIsIgnoredWithoutRenderedStateMutation(string body)
    {
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var receiving = CreateReceivingRenderedDomain();
        var ready = CreateReadyRenderedDomain();

        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            legacy,
            body,
            out var legacyResult));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            receiving,
            body,
            out var receivingResult));
        Assert.False(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            ready,
            body,
            out var readyResult));

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.Null(receivingResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, receiving.Status);
        Assert.Null(readyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReady, ready.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, ready.QueryRendered(11, 2, "x").Status);
    }

    [Theory]
    [InlineData("scroll")]
    [InlineData("find-domain-begin")]
    [InlineData("find-text-index-start")]
    [InlineData("find-text-index-chunk")]
    [InlineData("find-text-index-complete")]
    public void WebMessageIngressParsesEachMessageBodyAtMostOnce(string scenario)
    {
        Assert.Equal(1, CountIngressRouteParsesForScenario(scenario));
    }

    [Theory]
    [InlineData("scroll")]
    [InlineData("find-domain-begin")]
    public void WebMessageIngressClassifiesTopLevelShapeExactlyOnce(string scenario)
    {
        var (domain, body) = BuildIngressScenario(scenario);

        Assert.Equal(1, CountIngressDiscriminatorScans(domain, body, out _));
    }

    [Fact]
    public void OversizedNonFindDropFileClassifiesTopLevelShapeExactlyOnce()
    {
        var domain = CreateReadyRenderedDomain();
        var body = DropFileJson(new string('x', 1024 * 1024));

        var scans = CountIngressDiscriminatorScans(domain, body, out var kind);

        Assert.Equal(1, scans);
        Assert.Equal(ApplicateWebMessageIngressKind.Generic, kind);
    }

    [Fact]
    public void WebMessageIngressThreadsAcceptedRawBoundsWithoutSecondValidation()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var ingressClassifier = ExtractMethodBody(
            source,
            "internal static ApplicateWebMessageIngress ClassifyWebMessageIngress(",
            occurrence: 2);

        Assert.DoesNotContain(
            "ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(body)",
            ingressClassifier,
            StringComparison.Ordinal);
        Assert.Contains("bounds.WireUtf8Bytes", ingressClassifier, StringComparison.Ordinal);
        Assert.Contains("out var bounds", ingressClassifier, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedRecognizableFindRejectsCurrentTransferWithoutReparsing()
    {
        // Within bounds, recognizable find type, but truncated JSON. The single
        // ingress parse proves it malformed; the current transfer is rejected
        // fail-closed from that known failure. The ingress parse runs exactly once,
        // and RejectCurrentTransfer takes no body so it cannot reparse.
        var rendered = CreateReceivingRenderedDomain();
        const string body = """{"type":"find-text-index-chunk","renderId":11,""";
        var parseCount = 0;

        using var ingress = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            rendered,
            body,
            (candidate, options) =>
            {
                parseCount++;
                return JsonDocument.Parse(candidate, options);
            },
            RecognizesRenderedFind);

        Assert.Equal(ApplicateWebMessageIngressKind.RenderedFind, ingress.Kind);
        Assert.Equal(1, parseCount);
        Assert.Equal(
            ApplicateRenderedFindProtocolApplyStatus.Rejected,
            ingress.RenderedFindResult!.ProtocolStatus);
        Assert.Equal("mm-find-transfer-invalid", ingress.RenderedFindResult.Rejection!.FailureId);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, rendered.Status);
    }

    [Fact]
    public void RejectCurrentTransferClosesRenderedStateWithoutABody()
    {
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var receiving = CreateReceivingRenderedDomain();

        var legacyResult = legacy.RejectCurrentTransfer("mm-find-transfer-invalid");
        var receivingResult = receiving.RejectCurrentTransfer("mm-find-transfer-invalid");

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.NotNull(receivingResult);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, receivingResult!.ProtocolStatus);
        Assert.Equal("mm-find-transfer-invalid", receivingResult.Rejection!.FailureId);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, receiving.Status);
    }

    [Fact]
    public void ValidUnrelatedMessageClassifiesAsNonProtocolWithoutMutatingRenderedState()
    {
        const string body = """{"type":"minimap-state","note":"find-domain-begin"}""";
        var rendered = CreateReceivingRenderedDomain();

        Assert.Equal(
            ApplicateRenderedFindRoutingClassification.NonProtocol,
            ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body));
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedReceiving, rendered.Status);
    }

    [Fact]
    public void RenderSchedulingSelectsFindDomainBeforePostingCurrentDocument()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var queueRender = ExtractMethodBody(source, "private void QueueRender(");
        var shellRender = ExtractMethodBody(source, "private async Task QueueRenderShellAsync(");
        var domainReset = ExtractMethodBody(source, "private void ResetCurrentFindDomainForRender(");
        var reset = queueRender.IndexOf("ResetCurrentFindDomainForRender(renderId)", StringComparison.Ordinal);
        var queueShell = queueRender.IndexOf("QueueRenderShellAsync", StringComparison.Ordinal);
        var setIndex = shellRender.IndexOf("SetCurrentFindTextIndex(renderId, body.Blocks)", StringComparison.Ordinal);
        var postLoad = shellRender.IndexOf("PostRendererMessage(rendererMessage)", StringComparison.Ordinal);

        Assert.True(reset >= 0, "Every new render should reset stale find transfer/query ownership.");
        Assert.True(queueShell > reset, "Flag-ON awaiting state must exist before the asynchronous load-document path starts.");
        Assert.True(setIndex >= 0 && postLoad > setIndex, "The selected domain must be established before load-document is posted.");
        Assert.Contains("ApplicateVirtualizationMode.IsEnabled", domainReset, StringComparison.Ordinal);
        Assert.Contains("_renderedFindDomain.BeginRenderedRender(checked((int)renderId))", domainReset, StringComparison.Ordinal);
        Assert.Contains("ApplicateRenderedFindDomainState.CreateLegacyPlaintext()", domainReset, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedFindQueriesNeverFallBackToLegacyPlaintextResults()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var queryHandler = ExtractMethodBody(source, "private void HandleFindQueryMessage(");
        var renderedResults = ExtractMethodBody(source, "private static object BuildRenderedFindResultsMessage(");

        Assert.Contains("ApplicateRenderedFindDomainState.RenderedTextDomain", queryHandler, StringComparison.Ordinal);
        Assert.Contains("_renderedFindDomain.BeginRenderedQuery", queryHandler, StringComparison.Ordinal);
        Assert.Contains("PostLatestRenderedFindResultAsync", queryHandler, StringComparison.Ordinal);
        Assert.Contains("_currentFindTextIndex.Search(query)", queryHandler, StringComparison.Ordinal);
        Assert.Contains("textDomain = envelope.TextDomain", renderedResults, StringComparison.Ordinal);
        Assert.Contains("status = ToWireStatus(envelope.Status)", renderedResults, StringComparison.Ordinal);
        Assert.Contains("envelope.Status == ApplicateRenderedFindResultStatus.Ready", renderedResults, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedFindReadyPayloadCarriesTruncationExactTotalAndCappedMatches()
    {
        var envelope = CreateRenderedEnvelope(
            requestId: 21,
            query: "needle",
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 5_006,
            truncated: true,
            matches: CreateRenderedMatches(count: 5_000, normalizedText: "needle"));

        var payload = ApplicateWebMarkdownDocumentView.BuildRenderedFindResultsPayload(envelope);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("find-results", root.GetProperty("type").GetString());
        Assert.Equal(21, root.GetProperty("requestId").GetInt64());
        Assert.Equal("needle", root.GetProperty("query").GetString());
        Assert.Equal("rendered-dom-v1", root.GetProperty("textDomain").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal(5_006, root.GetProperty("totalCount").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(5_000, root.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void RenderedFindReadyPayloadUsesDocumentedSenderOwnedCeiling()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);

        Assert.Contains("sender-owned find-results safety ceiling", source, StringComparison.Ordinal);
        Assert.Contains("not the inbound rendered-find transfer contract", source, StringComparison.Ordinal);
        Assert.Contains("MaxRenderedFindResultsUtf8Bytes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderedFindReadyPayloadStaysReadyBelowSenderOwnedUtf8Ceiling()
    {
        var envelope = CreateRenderedEnvelope(
            requestId: 22,
            query: "needle",
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 5_000,
            truncated: false,
            matches: CreateRenderedMatches(
                count: 5_000,
                normalizedText: new string('a', 128)));

        var payload = ApplicateWebMarkdownDocumentView.BuildRenderedFindResultsPayload(envelope);
        var wireBytes = Encoding.UTF8.GetByteCount(payload);

        Assert.InRange(wireBytes, 1, ApplicateWebMarkdownDocumentView.MaxRenderedFindResultsUtf8Bytes);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("find-results", root.GetProperty("type").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal(5_000, root.GetProperty("totalCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(5_000, root.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void RenderedFindReadyPayloadFallsBackUnavailableAboveSenderOwnedUtf8Ceiling()
    {
        var envelope = CreateRenderedEnvelope(
            requestId: 23,
            query: "needle",
            ApplicateRenderedFindResultStatus.Ready,
            totalCount: 5_000,
            truncated: false,
            matches: CreateRenderedMatches(
                count: 5_000,
                normalizedText: new string('\u044f', 512)));

        var payload = ApplicateWebMarkdownDocumentView.BuildRenderedFindResultsPayload(envelope);
        var wireBytes = Encoding.UTF8.GetByteCount(payload);

        Assert.InRange(wireBytes, 1, ApplicateWebMarkdownDocumentView.MaxRenderedFindResultsUtf8Bytes);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("find-results", root.GetProperty("type").GetString());
        Assert.Equal("unavailable", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Empty(root.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public void RenderedFindBoundaryMeasurementsCoverHandlerAndPostContinuation()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var queryHandler = ExtractMethodBody(source, "private void HandleFindQueryMessage(");
        var postIfCurrent = ExtractMethodBody(source, "private async Task<double> PostIfCurrentAsync(");

        var enqueueStart = queryHandler.IndexOf("var enqueueStart = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
        var requestIdValidation = queryHandler.IndexOf("root.TryGetProperty(\"requestId\"", StringComparison.Ordinal);
        var queryWork = queryHandler.IndexOf("_renderedFindDomain.BeginRenderedQuery", StringComparison.Ordinal);
        var submit = queryHandler.IndexOf("PostLatestRenderedFindResultAsync(work, enqueueStart)", StringComparison.Ordinal);

        Assert.True(enqueueStart >= 0, "find-query handler should start enqueue timing at handler entry.");
        Assert.True(enqueueStart < requestIdValidation, "enqueue timing should include request validation.");
        Assert.True(requestIdValidation < queryWork, "validation should happen before query-work creation.");
        Assert.True(queryWork < submit, "query work should be created before worker scheduling.");

        var uiContinuation = postIfCurrent.IndexOf("await _postOnUiAsync(() =>", StringComparison.Ordinal);
        var postStart = postIfCurrent.IndexOf("var postStart = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
        var latestCheck = postIfCurrent.IndexOf("if (work.UpdatesLatest && !IsCurrent", StringComparison.Ordinal);
        var postCall = postIfCurrent.IndexOf("postAsync(envelope).GetAwaiter().GetResult();", StringComparison.Ordinal);
        var elapsed = postIfCurrent.IndexOf("postMs = Stopwatch.GetElapsedTime(postStart).TotalMilliseconds;", StringComparison.Ordinal);

        Assert.True(uiContinuation >= 0, "post timing should run inside the UI continuation.");
        Assert.True(uiContinuation < postStart, "post timing should start at UI continuation entry.");
        Assert.True(postStart < latestCheck, "post timing should include the latest-query recheck.");
        Assert.True(latestCheck < postCall, "latest-query recheck should happen before renderer post.");
        Assert.True(postCall < elapsed, "post timing should include renderer post return.");
    }

    [Fact]
    public async Task RenderedFindAsyncSearchDoesNotPostSupersededQuery()
    {
        var firstSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = new List<ApplicateRenderedFindResultEnvelope>();
        var coordinator = new ApplicateRenderedFindQueryCoordinator(
            buildReadyEnvelope: (work, cancellationToken) =>
            {
                if (work.Identity.RequestId == 31)
                {
                    firstSearchStarted.SetResult();
                    releaseFirstSearch.Task.GetAwaiter().GetResult();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return work.CreateReadyEnvelope(new ApplicateRenderedFindTextSearchResult(
                    1,
                    false,
                    [CreateRenderedMatch(work.Identity.RequestId, ordinal: 1, normalizedText: work.Query.Query)]));
            },
            runReadySearchAsync: static (build, cancellationToken) => Task.Run(() => build(cancellationToken), cancellationToken),
            postOnUiAsync: action =>
            {
                action();
                return Task.CompletedTask;
            });

        var first = coordinator.SubmitAsync(CreateReadyWork(requestId: 31, query: "first"), PostAsync);
        await firstSearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.SubmitAsync(CreateReadyWork(requestId: 32, query: "second"), PostAsync);
        releaseFirstSearch.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(posts, post => post.RequestId == 31);
        var post = Assert.Single(posts);
        Assert.Equal(32, post.RequestId);
        Assert.Equal("second", post.Query);

        Task PostAsync(ApplicateRenderedFindResultEnvelope envelope)
        {
            posts.Add(envelope);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void RenderedFindProtocolReturnPathsUseHostLatestBoundary()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var messageHandler = ExtractMethodBody(source, "private void OnWebMessageReceived(");
        var latestPoster = ExtractMethodBody(source, "private System.Threading.Tasks.Task PostLatestRenderedFindResultAsync(");

        Assert.Contains(
            "deferRenderedFindLatestResult: true",
            messageHandler,
            StringComparison.Ordinal);
        Assert.Contains("_renderedFindQueryCoordinator.SubmitAsync", latestPoster, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PostLatestRenderedFindResult(ingress.RenderedFindResult?.LatestQueryResult)",
            messageHandler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalRenderDefersLivePreferencesUntilModeSettleProbe()
    {
        var hostSource = File.ReadAllText(SharedWebViewHostSourcePath);
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);

        Assert.Contains("deferLivePreferencesUntilModeSettleProbe: transactionGeneration > 0", hostSource, StringComparison.Ordinal);
        Assert.Contains("internal ApplicateWebInputUpdateAction UpdateInputs", viewSource, StringComparison.Ordinal);
        Assert.Contains("bool deferLivePreferencesUntilModeSettleProbe = false", viewSource, StringComparison.Ordinal);
        Assert.Contains("&& !deferLivePreferencesUntilModeSettleProbe", viewSource, StringComparison.Ordinal);
        Assert.Contains("return action;", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalSettleProbeFastPathKeepsRendererAckBoundary()
    {
        var source = File.ReadAllText(SharedWebViewHostSourcePath);
        var compositorSource = File.ReadAllText(AirspaceCompositorSourcePath);
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var rendererSource = File.ReadAllText(RendererSourcePath);
        var requestRender = source[
            source.IndexOf("private void RequestRender(", StringComparison.Ordinal)..
            source.IndexOf("public void RetryRender()", StringComparison.Ordinal)];
        var commit = source[
            source.IndexOf("private void Commit()", StringComparison.Ordinal)..
            source.IndexOf("public bool RevealNativeWebViewForCommittedTransaction", StringComparison.Ordinal)];

        Assert.Contains("View.UpdateInputs(", requestRender, StringComparison.Ordinal);
        Assert.Contains("ShouldSkipRendererFrameSettleForTransaction(transactionGeneration)", requestRender, StringComparison.Ordinal);
        Assert.Contains("=> transactionGeneration > 0", source, StringComparison.Ordinal);
        Assert.Contains("TransactionRendererSettleProbeReady?.Invoke", commit, StringComparison.Ordinal);
        Assert.Contains("_activeTransactionSkipsRendererFrameSettle", commit, StringComparison.Ordinal);
        Assert.Contains("_hostRevealIntents.RequestTransactionRendererSettleProbe", compositorSource, StringComparison.Ordinal);
        Assert.Contains("e.SkipFrameWait", compositorSource, StringComparison.Ordinal);
        Assert.Contains("skipFrameWait={skipFrameWait}", viewSource, StringComparison.Ordinal);
        Assert.Contains("skipFrameWait", rendererSource, StringComparison.Ordinal);
        Assert.Contains("mm-mode-settle-frame-wait-skipped", rendererSource, StringComparison.Ordinal);
        Assert.DoesNotContain("host-transaction-renderer-settled-fastpath", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalRenderLoadDocumentSkipsRendererLayoutFrameWait()
    {
        var hostSource = File.ReadAllText(SharedWebViewHostSourcePath);
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var rendererSource = File.ReadAllText(RendererSourcePath);

        Assert.Contains("ShouldSkipRendererFrameWait(source, transactionGeneration)", hostSource, StringComparison.Ordinal);
        Assert.Contains("skipFrameWaitUntilRenderReady: skipRendererFrameWait", hostSource, StringComparison.Ordinal);
        Assert.Contains("bool skipFrameWaitUntilRenderReady = false", viewSource, StringComparison.Ordinal);
        Assert.Contains("QueueRender(skipFrameWaitUntilRenderReady)", viewSource, StringComparison.Ordinal);
        Assert.Contains("skipFrameWait = skipFrameWaitUntilRenderReady", viewSource, StringComparison.Ordinal);
        Assert.Contains("skipFrameWait?: boolean", rendererSource, StringComparison.Ordinal);
        Assert.Contains("mm-layout-ready-frame-wait-skipped", rendererSource, StringComparison.Ordinal);
        Assert.Contains("scheduleLayoutReady(skipFrameWait === true)", rendererSource, StringComparison.Ordinal);
    }

    [Fact]
    public void VeryHeavyRenderLoadDocumentSkipsRendererLayoutFrameWait()
    {
        var hostSource = File.ReadAllText(SharedWebViewHostSourcePath);

        Assert.Contains("RendererFrameWaitSkipDocumentContentLength = 1024 * 1024", hostSource, StringComparison.Ordinal);
        Assert.Contains("source?.Content.Length > RendererFrameWaitSkipDocumentContentLength", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalAttachLeavesRendererRevealToBridgeCover()
    {
        var source = File.ReadAllText(SharedWebViewHostSourcePath);
        var compositorSource = File.ReadAllText(AirspaceCompositorSourcePath);

        var attach = source[
            source.IndexOf("public void AttachTo(", StringComparison.Ordinal)..
            source.IndexOf("private void RequestRender(", StringComparison.Ordinal)];
        var onAttachStarting = ExtractMethodBody(compositorSource, "private void OnAttachStarting(");

        Assert.Contains("var transactionalAttach", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("View.PrepareNativeRendererForReveal", attach, StringComparison.Ordinal);
        Assert.Contains("HostAttachStarting?.Invoke", attach, StringComparison.Ordinal);
        Assert.Contains("_hostRevealIntents.ParkNativeWebViewForReparent();", onAttachStarting, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererSuppressesResizeReactionsBetweenRevealPrepareAndSettleProbe()
    {
        var source = File.ReadAllText(RendererSourcePath);

        Assert.Contains("modeRevealPrepared", source, StringComparison.Ordinal);
        Assert.Contains("if (modeRevealPrepared)", source, StringComparison.Ordinal);
        Assert.Contains("modeRevealPrepared = false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellReadyCancellationDoesNotPoisonSharedLatch()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);

        Assert.DoesNotContain("TrySetCanceled", source, StringComparison.Ordinal);
        Assert.Contains("_shellReady.Task.WaitAsync(cancellationToken)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellReadyReentrantCallsWaitForPendingDocumentReady()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var ensureShellReady = ExtractMethodBody(source, "internal async Task EnsureShellReadyAsync(");
        var reentrantBranch = ExtractFromMarker(ensureShellReady, "if (_shellNavigated)");

        Assert.Contains("if (_shellReady is not null)", reentrantBranch, StringComparison.Ordinal);
        Assert.Contains("await _shellReady.Task.WaitAsync(cancellationToken).ConfigureAwait(true);", reentrantBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("if (_shellNavigated)\r\n        {\r\n            return;\r\n        }", ensureShellReady, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellPrewarmUsesPerViewGeneratedShellFile()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var navigateShell = ExtractMethodBody(source, "private async Task NavigateToShellAsync(");

        Assert.Contains("Interlocked.Increment(ref s_shellDocumentSequence)", source, StringComparison.Ordinal);
        Assert.Contains("renderer-shell-{_shellDocumentId}.html", navigateShell, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.Combine(folder, \"renderer-shell.html\")", navigateShell, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRenderReusesRenderedBodyCacheBeforePostingLoadDocument()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var cacheLookup = source.IndexOf("_renderedBodyCache", StringComparison.Ordinal);
        var renderBody = source.IndexOf(".RenderBodyAsync(source, readingPreferences, imageSourceResolver, ct)", StringComparison.Ordinal);
        var postLoad = source.IndexOf("PostRendererMessage(rendererMessage)", StringComparison.Ordinal);

        Assert.True(cacheLookup >= 0, "Shell render should keep a rendered-body cache before load-document IPC.");
        Assert.True(renderBody > cacheLookup, "Markdown-to-HTML rendering should run behind the rendered-body cache.");
        Assert.True(postLoad > renderBody, "The cached or freshly rendered body should be resolved before load-document IPC.");
        Assert.Contains("render-body-cache-hit", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRenderCanRestoreRendererDocumentCacheByKeyBeforePostingFullHtmlFallback()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var rendererSource = File.ReadAllText(RendererSourcePath);

        Assert.Contains("_postedRendererDocumentCacheKeys", source, StringComparison.Ordinal);
        Assert.Contains("_pendingRendererCacheFallbackLoads[renderId] = fullLoadDocumentMessage;", source, StringComparison.Ordinal);
        Assert.Contains("type = \"load-cached-document\"", source, StringComparison.Ordinal);
        Assert.Contains("HandleDocumentCacheMissMessage", source, StringComparison.Ordinal);
        Assert.Contains("type == \"document-cache-miss\"", source, StringComparison.Ordinal);
        Assert.Contains("PostRendererMessage(fallbackLoad)", source, StringComparison.Ordinal);

        Assert.Contains("\"load-cached-document\"", rendererSource, StringComparison.Ordinal);
        Assert.Contains("notifyDocumentCacheMiss", rendererSource, StringComparison.Ordinal);
        Assert.Contains("mm-load-document-cache-miss", rendererSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererDocumentCacheKeyIncludesDocumentIdentity()
    {
        var suffix = ApplicateRendererDocumentCacheKeys.CreateSuffix("<h1>Same rendered body</h1>");

        var first = ApplicateRendererDocumentCacheKeys.Create("classic-white", @"D:\docs\first.md", suffix);
        var second = ApplicateRendererDocumentCacheKeys.Create("classic-white", @"D:\docs\second.md", suffix);

        Assert.NotEqual(first, second);
        Assert.StartsWith("classic-white|", first, StringComparison.Ordinal);
        Assert.EndsWith("|" + suffix, first, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerAndEditPreviewHostsShareRenderedBodyCache()
    {
        var providerSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "ApplicateSharedWebViewHostProvider.cs"));
        var hostSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "ApplicateSharedWebViewHost.cs"));
        var programSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Program.cs"));

        Assert.Contains("collection.AddSingleton<ApplicateRenderedBodyCache>();", programSource, StringComparison.Ordinal);
        Assert.Contains("ApplicateRenderedBodyCache renderedBodyCache", providerSource, StringComparison.Ordinal);
        Assert.Contains("ViewerHost = new ApplicateSharedWebViewHost(renderer, shellAssetFactory, renderedBodyCache);", providerSource, StringComparison.Ordinal);
        Assert.Contains("EditPreviewHost = new ApplicateSharedWebViewHost(renderer, shellAssetFactory, renderedBodyCache);", providerSource, StringComparison.Ordinal);
        Assert.Contains("new ApplicateWebMarkdownDocumentView(renderer, shellAssetFactory, renderedBodyCache)", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramPrimesActiveDocumentRenderedBodyCacheAfterPreRead()
    {
        var programSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Program.cs"));

        var preReadCall = programSource.IndexOf("StartActiveDocumentPreRead(args, services);", StringComparison.Ordinal);
        var primeMethod = programSource.IndexOf("PrimeActiveDocumentRenderedBodyCacheAsync", StringComparison.Ordinal);
        var cacheResolve = programSource.IndexOf("GetRequiredService<ApplicateRenderedBodyCache>()", StringComparison.Ordinal);
        var rendererResolve = programSource.IndexOf("GetRequiredService<IApplicateHtmlMarkdownRenderer>()", StringComparison.Ordinal);
        var cacheRender = programSource.IndexOf(".GetOrRenderAsync(", StringComparison.Ordinal);
        var nativePrime = programSource.IndexOf("PrimeActiveDocumentNativeModelCache", StringComparison.Ordinal);

        Assert.True(preReadCall >= 0, "Active-document pre-read should receive the DI provider.");
        Assert.True(primeMethod > preReadCall, "Program should prime the rendered-body cache from the pre-read source.");
        Assert.True(cacheResolve > primeMethod, "Body prime should use the shared rendered-body cache.");
        Assert.True(rendererResolve > primeMethod, "Body prime should use the shared HTML renderer.");
        Assert.True(cacheRender > primeMethod, "Body prime should populate through the cache owner.");
        Assert.Equal(-1, nativePrime);
    }

    [Fact]
    public void ProgramPrimesRestoredStartupDocumentBodyCacheOnlyWhenArgvDoesNotWin()
    {
        var programSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Program.cs"));

        var sessionCall = programSource.IndexOf("StartSessionStartupDocumentPreRead(args, services);", StringComparison.Ordinal);
        var sessionMethod = programSource.IndexOf("private static void StartSessionStartupDocumentPreRead(string[] args, IServiceProvider services)", StringComparison.Ordinal);
        var argvSkip = programSource.IndexOf("perf-session-prefetch skipped reason=argv-doc", sessionMethod, StringComparison.Ordinal);
        var sessionPrime = programSource.IndexOf("PrimeActiveDocumentRenderedBodyCacheAsync(services, source, CancellationToken.None)", sessionMethod, StringComparison.Ordinal);

        Assert.True(sessionCall >= 0, "Session startup pre-read should receive argv plus the DI provider.");
        Assert.True(sessionMethod > sessionCall, "Session startup pre-read method should stay after the Program.Main call.");
        Assert.True(argvSkip > sessionMethod, "Session pre-read should skip when argv already selects the startup document.");
        Assert.True(sessionPrime > argvSkip, "Restored-session startup documents should prime the rendered-body cache.");
        Assert.DoesNotContain("PrimeActiveDocumentNativeModelCache", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRenderShellUsesChunkedProgressiveAppendForVeryHeavyDocuments()
    {
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);

        Assert.Contains("TryCreateProgressiveRenderBody(body,", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("!ViewerChromeEnabled", viewSource, StringComparison.Ordinal);
        Assert.Contains("ProgressiveAppendChunkTargetHtmlLength", viewSource, StringComparison.Ordinal);
        Assert.Contains("cacheKey = (string?)null", viewSource, StringComparison.Ordinal);
        Assert.Contains("type = \"append-document\"", viewSource, StringComparison.Ordinal);
        Assert.Contains("chunk={index + 1}/{appendChunks.Count}", viewSource, StringComparison.Ordinal);
        Assert.Contains("isFinal", viewSource, StringComparison.Ordinal);
        Assert.Contains("DocumentRevealReady += OnProgressiveDocumentRevealReady;", viewSource, StringComparison.Ordinal);
        Assert.Contains("public event EventHandler? ProgressiveAppendCompleted;", viewSource, StringComparison.Ordinal);
        Assert.Contains("public bool HasPendingProgressiveAppend => _progressiveAppendPending;", viewSource, StringComparison.Ordinal);
        Assert.Contains("CompleteProgressiveAppend(renderId);", viewSource, StringComparison.Ordinal);
        Assert.Contains("cacheKey = isFinal ? rendererCacheKey : null", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentSwitchCoverWaitsForPostReadyRevealSignal()
    {
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var compositorSource = File.ReadAllText(AirspaceCompositorSourcePath);
        var compositorHostAdaptersSource = File.ReadAllText(AirspaceCompositorHostAdaptersSourcePath);
        var rendererSource = File.ReadAllText(RendererSourcePath);
        var completeLayoutReady = ExtractMethodBody(viewSource, "private void CompleteLayoutReady()");
        var completeDocumentRenderVisualReady = ExtractMethodBody(viewSource, "private void CompleteDocumentRenderVisualReady()");

        Assert.Contains("public event EventHandler? DocumentRevealReady;", viewSource, StringComparison.Ordinal);
        Assert.Contains("\"post-ready-enhancements-complete\"", viewSource, StringComparison.Ordinal);
        Assert.Contains("CompleteDocumentRevealReady()", viewSource, StringComparison.Ordinal);
        Assert.Contains("DocumentRevealReady?.Invoke", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RevealNativeDocument(TimeSpan.Zero);", completeLayoutReady, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentRendered?.Invoke", completeLayoutReady, StringComparison.Ordinal);
        Assert.Contains("_postReadyEnhancementsComplete", completeDocumentRenderVisualReady, StringComparison.Ordinal);
        Assert.Contains("DocumentRenderVisualReady?.Invoke", completeDocumentRenderVisualReady, StringComparison.Ordinal);
        Assert.Contains("DocumentRendered?.Invoke", completeDocumentRenderVisualReady, StringComparison.Ordinal);

        Assert.Contains("_host.View.DocumentRevealReady += value;", compositorHostAdaptersSource, StringComparison.Ordinal);
        Assert.Contains("_signals.DocumentRevealReady += OnDocumentRevealReady;", compositorSource, StringComparison.Ordinal);
        Assert.Contains("ApplicateMode _mode", compositorSource, StringComparison.Ordinal);
        Assert.Contains("e.Mode != _mode", compositorSource, StringComparison.Ordinal);
        Assert.Contains("clearHeadingsOnRendererFailure", compositorSource, StringComparison.Ordinal);
        Assert.Contains("skipInitialCoverSession", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_skipNextCoverSession", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_skipNextDocumentChangeCover", compositorSource, StringComparison.Ordinal);
        Assert.Contains("\"doc-switch-cover-skipped\"", compositorSource, StringComparison.Ordinal);
        Assert.Contains("\"doc-switch-cover-shown\"", compositorSource, StringComparison.Ordinal);
        Assert.Contains("\"doc-switch-cover-hidden\"", compositorSource, StringComparison.Ordinal);
        Assert.Contains("\"doc-switch-cover-deferred\"", compositorSource, StringComparison.Ordinal);
        Assert.Contains("\"doc-switch-cover-fallback\"", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_commitCompletedForCover", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_documentRevealReadyForCover", compositorSource, StringComparison.Ordinal);
        Assert.Contains("TryHideCoverAfterCommitAndRevealReady()", compositorSource, StringComparison.Ordinal);
        Assert.Contains("ApplicateMotion.ModeSwitchDuration(_documentState.ReadingPreferences)", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_cover.Hide(duration)", compositorSource, StringComparison.Ordinal);

        Assert.Contains("postReadyEnhancementsCompleted", rendererSource, StringComparison.Ordinal);
        Assert.Contains("post-ready-enhancements-complete", rendererSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererRevealShieldMessagesAreCompositorOwned()
    {
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var compositorSource = File.ReadAllText(AirspaceCompositorSourcePath);
        var hostIntentsSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "IApplicateHostRevealIntents.cs"));

        Assert.DoesNotContain("mode-reveal-prepare", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("mode-reveal-start", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("document-reveal-prepare", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("document-reveal-start", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareNativeDocumentReveal(TimeSpan.Zero);", viewSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RevealNativeDocument(TimeSpan.Zero);", viewSource, StringComparison.Ordinal);

        Assert.Contains("PrepareModeRendererReveal", compositorSource, StringComparison.Ordinal);
        Assert.Contains("StartModeRendererReveal", compositorSource, StringComparison.Ordinal);
        Assert.Contains("PrepareDocumentRendererReveal", compositorSource, StringComparison.Ordinal);
        Assert.Contains("StartDocumentRendererReveal", compositorSource, StringComparison.Ordinal);
        Assert.Contains("mode-reveal-prepare", hostIntentsSource, StringComparison.Ordinal);
        Assert.Contains("mode-reveal-start", hostIntentsSource, StringComparison.Ordinal);
        Assert.Contains("document-reveal-prepare", hostIntentsSource, StringComparison.Ordinal);
        Assert.Contains("document-reveal-start", hostIntentsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutReadyCanRecoverDroppedDocumentReadyForActiveRender()
    {
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var layoutReadyHandler = ExtractFromMarker(viewSource, "if (IsLayoutReadyMessage(document.RootElement))");

        Assert.Contains("layout-ready-promoted-loaded", layoutReadyHandler, StringComparison.Ordinal);
        Assert.Contains("if (!_hasLoadedDocument && _activeRevealRenderId > 0)", layoutReadyHandler, StringComparison.Ordinal);

        var promoteIndex = layoutReadyHandler.IndexOf("_hasLoadedDocument = true;", StringComparison.Ordinal);
        var awaitIndex = layoutReadyHandler.IndexOf("BeginAwaitingLayoutReady();", StringComparison.Ordinal);
        var layoutIndex = layoutReadyHandler.IndexOf("_hasLayoutReady = true;", StringComparison.Ordinal);
        var completeIndex = layoutReadyHandler.IndexOf("CompleteLayoutReady();", StringComparison.Ordinal);
        Assert.True(promoteIndex >= 0, "layout-ready should promote active renders to loaded when document-ready was dropped.");
        Assert.True(awaitIndex > promoteIndex, "promotion should restore the layout-ready await gate.");
        Assert.True(layoutIndex > awaitIndex, "layout-ready should set layout after restoring loaded/awaiting state.");
        Assert.True(completeIndex > layoutIndex, "completion should run after both loaded and layout are true.");
    }

    [Fact]
    public void ThemeSwitchCoverWaitsForMatchingRendererPaintAck()
    {
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);
        var rendererSource = File.ReadAllText(RendererSourcePath);
        var removedType = "ApplicateThemeSwitchReveal" + "Coordinator";

        Assert.Contains("public event EventHandler<ApplicateWebThemeChangeSentEventArgs>? ThemeChangeSent;", viewSource, StringComparison.Ordinal);
        Assert.Contains("public event EventHandler<ApplicateWebThemeAppliedEventArgs>? ThemeApplied;", viewSource, StringComparison.Ordinal);
        Assert.Contains("var requestId = ++_themeRequestSequence;", viewSource, StringComparison.Ordinal);
        Assert.Contains("PostRendererMessage(new { type = \"theme\", theme, requestId });", viewSource, StringComparison.Ordinal);

        Assert.Contains("| { type: \"theme-applied\"; theme: RendererTheme; requestId: number }", rendererSource, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame(() => window.requestAnimationFrame(postAck));", rendererSource, StringComparison.Ordinal);
        Assert.Contains("postHostMessage({ type: \"theme-applied\", theme, requestId });", rendererSource, StringComparison.Ordinal);
        Assert.Contains("themeAppliedAckGeneration", rendererSource, StringComparison.Ordinal);

        var compositorSource = File.ReadAllText(AirspaceCompositorSourcePath);
        var mainWindowSource = File.ReadAllText(MainWindowSourcePath);

        Assert.False(File.Exists(DeletedThemeRevealSourcePath));
        Assert.Contains("RegisterThemeSession", compositorSource, StringComparison.Ordinal);
        Assert.Contains("ThemeRevealSession", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_documentState.ThemeTransitionStarting += OnThemeTransitionStarting;", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_signals.ThemeChangeSent += OnThemeChangeSent;", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_signals.ThemeApplied += OnThemeApplied;", compositorSource, StringComparison.Ordinal);
        Assert.Contains("e.RequestId != _targetRequestId", compositorSource, StringComparison.Ordinal);
        Assert.Contains("_paintGate.AfterTwoFrames", compositorSource, StringComparison.Ordinal);
        Assert.DoesNotContain(removedType, compositorSource, StringComparison.Ordinal);
        Assert.DoesNotContain(removedType, mainWindowSource, StringComparison.Ordinal);
    }

    private static string DropFileJson(string text)
        => JsonSerializer.Serialize(new { type = "drop-file", name = "dropped.md", text });

    // A non-find object whose "node" value nests `depth` levels deep: past the find
    // protocol depth (8) yet within the default JSON depth (64).
    private static string NestedNonFindJson(int depth)
    {
        var builder = new StringBuilder("""{"type":"minimap-state","node":""");
        for (var level = 0; level < depth; level++)
        {
            builder.Append("""{"node":""");
        }

        builder.Append('0');
        builder.Append('}', depth);
        builder.Append('}');
        return builder.ToString();
    }

    private static int CountIngressRouteParsesForScenario(string scenario)
    {
        var (domain, body) = BuildIngressScenario(scenario);
        var parseCount = 0;
        using var ingress = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            domain,
            body,
            (candidate, options) =>
            {
                parseCount++;
                return JsonDocument.Parse(candidate, options);
            },
            RecognizesRenderedFind);
        return parseCount;
    }

    private static int CountIngressDiscriminatorScans(
        ApplicateRenderedFindDomainState domain,
        string body,
        out ApplicateWebMessageIngressKind kind)
    {
        var classifyCount = 0;
        using var ingress = ApplicateWebMarkdownDocumentView.ClassifyWebMessageIngress(
            domain,
            body,
            static (candidate, options) => JsonDocument.Parse(candidate, options),
            candidate =>
            {
                classifyCount++;
                return RecognizesRenderedFind(candidate);
            });
        kind = ingress.Kind;
        return classifyCount;
    }

    private static bool RecognizesRenderedFind(string body)
        => ApplicateRenderedFindTextProtocol.TryGetTopLevelRenderedFindMessageType(body, out _);

    private static (ApplicateRenderedFindDomainState Domain, string Body) BuildIngressScenario(string scenario)
        => scenario switch
        {
            "scroll" => (CreateReadyRenderedDomain(), """{"type":"scroll","ratio":0.5,"renderId":11}"""),
            "find-domain-begin" => (
                BegunRenderedDomain(11),
                """{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11}"""),
            "find-text-index-start" => (
                BegunRenderedDomain(11),
                """{"type":"find-text-index-start","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","semanticSegmentCount":1,"totalCodeUnits":1,"chunkCount":1,"partCount":1}"""),
            "find-text-index-chunk" => (
                CreateReceivingRenderedDomain(),
                """{"type":"find-text-index-chunk","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","chunkIndex":0,"parts":[{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1,"partOffset":0,"text":"x"}]}"""),
            "find-text-index-complete" => (
                CreateReceivingRenderedDomain(),
                """{"type":"find-text-index-complete","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","semanticSegmentCount":1,"totalCodeUnits":1,"chunkCount":1,"partCount":1}"""),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

    private static ApplicateRenderedFindDomainState BegunRenderedDomain(int renderId)
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(renderId);
        return state;
    }

    private static ApplicateRenderedFindDomainState CreateReceivingRenderedDomain()
    {
        var state = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        state.BeginRenderedRender(11);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, state.ApplyProtocolMessage(
            """{"type":"find-domain-begin","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11}""").ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, state.ApplyProtocolMessage(
            """{"type":"find-text-index-start","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","semanticSegmentCount":1,"totalCodeUnits":1,"chunkCount":1,"partCount":1}""").ProtocolStatus);
        return state;
    }

    private static ApplicateRenderedFindDomainState CreateReadyRenderedDomain()
    {
        var state = CreateReceivingRenderedDomain();
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Accepted, state.ApplyProtocolMessage(
            """{"type":"find-text-index-chunk","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","chunkIndex":0,"parts":[{"segmentOrdinal":0,"blockIndex":0,"blockLocalStart":0,"segmentCodeUnitLength":1,"partOffset":0,"text":"x"}]}""").ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Committed, state.ApplyProtocolMessage(
            """{"type":"find-text-index-complete","schemaVersion":1,"textDomain":"rendered-dom-v1","renderId":11,"projectionRevision":1,"transferId":"11:1","semanticSegmentCount":1,"totalCodeUnits":1,"chunkCount":1,"partCount":1}""").ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindResultStatus.Ready, state.QueryRendered(11, 1, "x").Status);
        return state;
    }

    private static ApplicateRenderedFindResultEnvelope CreateRenderedEnvelope(
        long requestId,
        string query,
        ApplicateRenderedFindResultStatus status,
        int totalCount,
        bool truncated,
        IReadOnlyList<ApplicateRenderedFindTextMatch> matches)
        => new(
            RenderId: 11,
            RequestId: requestId,
            Query: query,
            TextDomain: ApplicateRenderedFindDomainState.RenderedTextDomain,
            Status: status,
            TotalCount: totalCount,
            Truncated: truncated,
            Matches: matches);

    private static ApplicateRenderedFindTextMatch[] CreateRenderedMatches(
        int count,
        string normalizedText)
    {
        var matches = new ApplicateRenderedFindTextMatch[count];
        for (var index = 0; index < count; index++)
        {
            matches[index] = CreateRenderedMatch(seed: index + 1, ordinal: index + 1, normalizedText);
        }

        return matches;
    }

    private static ApplicateRenderedFindTextMatch CreateRenderedMatch(
        long seed,
        int ordinal,
        string normalizedText)
        => new(
            MatchId: $"r11-p1-b{seed}-s{seed}-o{seed}-l{normalizedText.Length}-n{ordinal}",
            RenderId: 11,
            ProjectionRevision: 1,
            BlockIndex: checked((int)seed),
            SegmentOrdinal: checked((int)seed),
            BlockLocalOffset: checked((int)seed),
            Length: normalizedText.Length,
            NormalizedText: normalizedText,
            Ordinal: ordinal);

    private static ApplicateRenderedFindQueryWork CreateReadyWork(long requestId, string query)
        => new(
            new ApplicateRenderedFindQuery(
                RenderId: 11,
                RequestId: requestId,
                Query: query,
                TextDomain: ApplicateRenderedFindDomainState.RenderedTextDomain),
            ApplicateRenderedFindResultStatus.Ready,
            ApplicateRenderedFindTextIndex.Create(
                renderId: 11,
                projectionRevision: 1,
                [new ApplicateRenderedFindTextSegment(0, 0, 0, query)]),
            UpdatesLatest: true);

    private static string ExtractFromMarker(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} should exist.");
        return source[start..];
    }

    private static string ExtractMethodBody(string source, string signature)
        => ExtractMethodBody(source, signature, occurrence: 1);

    private static string ExtractMethodBody(string source, string signature, int occurrence)
    {
        Assert.True(occurrence > 0, "Occurrence is one-based.");
        var start = -1;
        for (var index = 0; index < occurrence; index++)
        {
            start = source.IndexOf(signature, start + 1, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{signature} occurrence {occurrence} should exist.");
        }

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"{signature} should have a body.");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return source[braceStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} body was not closed.");
    }
}
