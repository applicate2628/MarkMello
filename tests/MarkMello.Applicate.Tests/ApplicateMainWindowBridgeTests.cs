using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateMainWindowBridgeTests
{
    [Fact]
    public void ReaderModeTabSwitchPrefersLoadedOpenDocumentSource()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var readerBranch = ExtractFromMarker(bridge, "// Reader-mode tab switch");

        Assert.Contains("args.ActiveDocument.IsLoaded", readerBranch, StringComparison.Ordinal);
        Assert.Contains("ApplyOpenedDocumentInPlaceWithScroll(args.ActiveDocument);", readerBranch, StringComparison.Ordinal);

        var inPlaceIndex = readerBranch.IndexOf("ApplyOpenedDocumentInPlaceWithScroll(args.ActiveDocument);", StringComparison.Ordinal);
        var fallbackIndex = readerBranch.IndexOf("await viewModel.OpenPathAsync(newPath).ConfigureAwait(true);", StringComparison.Ordinal);
        Assert.True(fallbackIndex > inPlaceIndex, "OpenPathAsync should remain only after the loaded-source fast path.");
    }

    [Fact]
    public void ActiveDocumentBridgePersistsAndRestoresReadingProgress()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var applyHelper = ExtractMethodBody(bridge, "void ApplyOpenedDocumentInPlaceWithScroll(OpenDocument activeDocument)");

        Assert.Contains("nameof(MainWindowViewModel.ReadingProgress)", bridge, StringComparison.Ordinal);
        Assert.Contains("openDocs.UpdateState(active, active.EditorCaret, viewModel.ReadingProgress);", bridge, StringComparison.Ordinal);
        Assert.Contains("activeDocument.ScrollProgressPercent", applyHelper, StringComparison.Ordinal);
        Assert.Contains("viewModel.ReadingProgress = progress;", applyHelper, StringComparison.Ordinal);
        Assert.Contains("viewModel.ApplyOpenedDocumentInPlace(nextSource);", applyHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void EditModeHotkeyIsEdgeTriggeredSoHeldCtrlEDoesNotFloodModeSwitches()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var constructor = ExtractMethodBody(codeBehind, "public ApplicateMainWindow(");
        var keyDown = ExtractMethodBody(codeBehind, "private void OnEditModeHotkeyKeyDown(");
        var keyUp = ExtractMethodBody(codeBehind, "private void OnEditModeHotkeyKeyUp(");
        var removeBindings = ExtractMethodBody(codeBehind, "private void RemoveInheritedEditModeKeyBindings()");

        Assert.Contains("RemoveInheritedEditModeKeyBindings();", constructor, StringComparison.Ordinal);
        Assert.Contains("InstallEditModeHotkeyRepeatGate(viewModel);", constructor, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("KeyBindings.RemoveAt(index);", removeBindings, StringComparison.Ordinal);
        Assert.Contains("_editModeHotkeyDown", keyDown, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", keyDown, StringComparison.Ordinal);
        Assert.Contains("return;", keyDown, StringComparison.Ordinal);
        Assert.Contains("_editModeHotkeyDown = false;", keyUp, StringComparison.Ordinal);
    }

    private static string ReadMainWindowCodeBehind()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));

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
