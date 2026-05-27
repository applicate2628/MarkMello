using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewAirspaceTests
{
    private static readonly string ViewSourcePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src",
        "MarkMello.Applicate.Desktop",
        "Views",
        "ApplicateWebMarkdownDocumentView.cs");

    [Fact]
    public void NativeWebViewHideKeepsAvaloniaLayoutVisible()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "internal void SetNativeWebViewVisibility(bool isVisible)",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("SetNativeWebViewWindowVisibility", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_webView.IsVisible = isVisible", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewHiddenPaintPrepaintsOffscreen()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "internal void PrepareNativeWebViewForHiddenPaint()",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("SyncNativeWebViewWindowSize(handle)", method, StringComparison.Ordinal);
        Assert.Contains("TryCaptureNativeWebViewPlacement", method, StringComparison.Ordinal);
        Assert.Contains("CalculateNativeOffscreenX(handle, placement.Width)", method, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.SwpNoCopyBits", method, StringComparison.Ordinal);
        Assert.Contains("SetNativeWebViewTreeVisibility(handle, isVisible: true)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewReparentParkUsesVirtualScreenCoordinates()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "internal void ParkNativeWebViewForReparent()",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("TryCaptureNativeWebViewPlacement", method, StringComparison.Ordinal);
        Assert.Contains("CalculateNativeOffscreenX(handle, placement.Width)", method, StringComparison.Ordinal);
        Assert.Contains("SetNativeWebViewTreeVisibility(handle, isVisible: false)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewHiddenPaintRestoreInvalidatesCopiedBackingStore()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "internal void CompleteNativeWebViewHiddenPaint()",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("SetWindowPos", method, StringComparison.Ordinal);
        Assert.Contains("SetNativeWebViewTreeVisibility(handle, isVisible: true)", method, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.SwpNoCopyBits", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewTreeVisibilityTogglesRootAndDescendants()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "private static void SetNativeWebViewTreeVisibility(IntPtr root, bool isVisible)",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("EnumerateNativeDescendants(root)", method, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(root, NativeMethods.SwShow)", method, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(child, NativeMethods.SwShow)", method, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(windows[index], NativeMethods.SwHide)", method, StringComparison.Ordinal);
        Assert.Contains("ShowWindow(root, NativeMethods.SwHide)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewPlacementUsesParentClientCoordinates()
    {
        var source = File.ReadAllText(ViewSourcePath);

        Assert.Contains("GetParent", source, StringComparison.Ordinal);
        Assert.Contains("GetWindowRect", source, StringComparison.Ordinal);
        Assert.Contains("ScreenToClient", source, StringComparison.Ordinal);
        Assert.Contains("NativeWindowPlacement(topLeft.X, topLeft.Y, width, height)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeWebViewOffscreenCoordinateAccountsForParentScreenPosition()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var methodStart = source.IndexOf(
            "private static int CalculateNativeOffscreenX(IntPtr handle, int width)",
            StringComparison.Ordinal);
        var method = ExtractMethodBody(source, methodStart);

        Assert.Contains("GetSystemMetrics(NativeMethods.SmXVirtualScreen)", method, StringComparison.Ordinal);
        Assert.Contains("GetParent(handle)", method, StringComparison.Ordinal);
        Assert.Contains("GetWindowRect(parent, out var parentRect)", method, StringComparison.Ordinal);
        Assert.Contains("virtualLeft - parentRect.Left - width - NativeOffscreenMargin", method, StringComparison.Ordinal);
    }


    [Fact]
    public void ModeSettleProbeCarriesReadingPreferencesPayload()
    {
        var source = File.ReadAllText(ViewSourcePath);
        var requestMethodStart = source.IndexOf(
            "internal void RequestModeToggleSettleProbe()",
            StringComparison.Ordinal);
        var requestMethod = ExtractMethodBody(source, requestMethodStart);
        var builderMethodStart = source.IndexOf(
            "private object BuildReadingPreferencesMessage(string type)",
            StringComparison.Ordinal);
        var builderMethod = ExtractMethodBody(source, builderMethodStart);

        Assert.Contains("BuildReadingPreferencesMessage(\"mode-settle-probe\")", requestMethod, StringComparison.Ordinal);
        Assert.Contains("viewerChromeEnabled", builderMethod, StringComparison.Ordinal);
        Assert.Contains("minimapMode", builderMethod, StringComparison.Ordinal);
        Assert.Contains("maxWidth", builderMethod, StringComparison.Ordinal);
        Assert.Contains("viewportWidth", builderMethod, StringComparison.Ordinal);
        Assert.Contains("viewportHeight", builderMethod, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, int methodStart)
    {
        Assert.True(methodStart >= 0);

        var bodyStart = source.IndexOf('{', methodStart);
        Assert.True(bodyStart >= 0);

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[bodyStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException("Could not find method body.");
    }
}
