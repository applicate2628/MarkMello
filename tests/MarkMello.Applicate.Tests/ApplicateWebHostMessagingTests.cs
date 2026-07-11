using System;
using System.IO;
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
    public void RenderedFindProtocolIsBoundedAndRoutedBeforeGenericMessageParsing()
    {
        var source = File.ReadAllText(WebDocumentViewSourcePath);
        var handler = ExtractMethodBody(source, "private void OnWebMessageReceived(");
        var renderedRoute = handler.IndexOf("TryHandleRenderedFindProtocolMessage(body)", StringComparison.Ordinal);
        var genericParse = handler.IndexOf("JsonDocument.Parse(body)", StringComparison.Ordinal);
        var rawBounds = handler.IndexOf("TryRejectInvalidRawRenderedFindMessage", StringComparison.Ordinal);
        var protocolRoute = ExtractMethodBody(source, "private bool TryHandleRenderedFindProtocolMessage(");
        var rawBoundsRoute = ExtractMethodBody(source, "internal static bool TryRejectInvalidRawRenderedFindMessage(");

        Assert.True(renderedRoute >= 0, "Known rendered-find messages should have a dedicated host route.");
        Assert.True(rawBounds >= 0 && renderedRoute > rawBounds, "The callback-wide raw bound must run before routing or parsing.");
        Assert.True(genericParse > renderedRoute, "The strict rendered-find route must run before generic host parsing.");
        Assert.DoesNotContain("IsNullOrWhiteSpace", handler, StringComparison.Ordinal);
        Assert.Contains("ApplicateRenderedFindTextProtocol.ValidateRawMessageBounds(body)", rawBoundsRoute, StringComparison.Ordinal);
        Assert.Contains("ApplicateRenderedFindTextProtocol.ClassifyMessageForRouting(body)", protocolRoute, StringComparison.Ordinal);
        Assert.Contains("ApplicateRenderedFindRoutingClassification.Malformed", protocolRoute, StringComparison.Ordinal);
        Assert.Contains("_renderedFindDomain.ApplyProtocolMessage(body)", protocolRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateTrace", protocolRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", protocolRoute, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedInboundBodyDropsInLegacyAndFailsCurrentRenderedDomainClosed()
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
    public void MalformedBoundedBodyClearsReceivingAndReadyRenderedStateButDropsInLegacy()
    {
        const string malformedGeneric = """{"type":"minimap-state""";
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var receiving = CreateReceivingRenderedDomain();
        var ready = CreateReadyRenderedDomain();

        var legacyResult = ApplicateWebMarkdownDocumentView.RejectInvalidRenderedFindMessageIfCurrent(legacy, malformedGeneric);
        var receivingResult = ApplicateWebMarkdownDocumentView.RejectInvalidRenderedFindMessageIfCurrent(receiving, malformedGeneric);
        var readyResult = ApplicateWebMarkdownDocumentView.RejectInvalidRenderedFindMessageIfCurrent(ready, malformedGeneric);

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, receivingResult!.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, receiving.Status);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, readyResult!.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, ready.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Unavailable, readyResult.LatestQueryResult!.Status);
        Assert.Empty(readyResult.LatestQueryResult.Matches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t")]
    public void EmptyOrWhitespaceInboundBodyClearsRenderedStateButDropsInLegacy(string body)
    {
        var legacy = ApplicateRenderedFindDomainState.CreateLegacyPlaintext();
        var receiving = CreateReceivingRenderedDomain();
        var ready = CreateReadyRenderedDomain();

        Assert.True(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            legacy,
            body,
            out var legacyResult));
        Assert.True(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            receiving,
            body,
            out var receivingResult));
        Assert.True(ApplicateWebMarkdownDocumentView.TryRejectInvalidRawRenderedFindMessage(
            ready,
            body,
            out var readyResult));

        Assert.Null(legacyResult);
        Assert.Equal(ApplicateRenderedFindDomainStatus.LegacyPlaintext, legacy.Status);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, receivingResult!.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, receiving.Status);
        Assert.Equal(ApplicateRenderedFindProtocolApplyStatus.Rejected, readyResult!.ProtocolStatus);
        Assert.Equal(ApplicateRenderedFindDomainStatus.RenderedRejected, ready.Status);
        Assert.Equal(ApplicateRenderedFindResultStatus.Unavailable, readyResult.LatestQueryResult!.Status);
        Assert.Empty(readyResult.LatestQueryResult.Matches);
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
        var renderedResults = ExtractMethodBody(source, "private void PostRenderedFindResults(");

        Assert.Contains("ApplicateRenderedFindDomainState.RenderedTextDomain", queryHandler, StringComparison.Ordinal);
        Assert.Contains("_renderedFindDomain.QueryRendered", queryHandler, StringComparison.Ordinal);
        Assert.Contains("PostRenderedFindResults", queryHandler, StringComparison.Ordinal);
        Assert.Contains("_currentFindTextIndex.Search(query)", queryHandler, StringComparison.Ordinal);
        Assert.Contains("textDomain = envelope.TextDomain", renderedResults, StringComparison.Ordinal);
        Assert.Contains("status = ToWireStatus(envelope.Status)", renderedResults, StringComparison.Ordinal);
        Assert.Contains("envelope.Status == ApplicateRenderedFindResultStatus.Ready", renderedResults, StringComparison.Ordinal);
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

    private static string ExtractFromMarker(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{marker} should exist.");
        return source[start..];
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} should exist.");

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
