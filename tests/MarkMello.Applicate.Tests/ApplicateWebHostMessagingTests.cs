using System;
using System.IO;
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
    public void TransactionalRenderDefersLivePreferencesUntilModeSettleProbe()
    {
        var hostSource = File.ReadAllText(SharedWebViewHostSourcePath);
        var viewSource = File.ReadAllText(WebDocumentViewSourcePath);

        Assert.Contains("deferLivePreferencesUntilModeSettleProbe: transactionGeneration > 0", hostSource, StringComparison.Ordinal);
        Assert.Contains("bool deferLivePreferencesUntilModeSettleProbe = false", viewSource, StringComparison.Ordinal);
        Assert.Contains("&& !deferLivePreferencesUntilModeSettleProbe", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalAttachPreparesRendererBeforeReparentResize()
    {
        var source = File.ReadAllText(SharedWebViewHostSourcePath);

        var prepare = source.IndexOf(
            "View.PrepareNativeRendererForReveal(CurrentModeSwitchDuration())",
            StringComparison.Ordinal);
        var park = source.IndexOf("View.ParkNativeWebViewForReparent();", StringComparison.Ordinal);

        Assert.True(prepare >= 0, "Transactional attach should prepare the renderer before the native resize/reparent.");
        Assert.True(park > prepare, "The renderer prepare message must be sent before ParkNativeWebViewForReparent.");
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
}
