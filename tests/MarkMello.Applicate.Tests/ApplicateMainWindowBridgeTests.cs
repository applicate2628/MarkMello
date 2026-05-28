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

    [Fact]
    public void RendererFocusedTabHotkeysUseSameOrdinalActivationAsWindowHotkeys()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var tabHotkey = ExtractMethodBody(codeBehind, "private void OnTabHotkey(object? sender, KeyEventArgs e)");
        var hostBridge = ExtractMethodBody(codeBehind, "private void InstallHostShortcutBridge(MainWindowViewModel viewModel)");
        var ordinalActivation = ExtractMethodBody(codeBehind, "private static bool TryActivateTabOrdinal(int ordinal)");
        var renderer = ReadRendererSource();

        Assert.Contains("TryActivateTabOrdinal(ordinal)", tabHotkey, StringComparison.Ordinal);
        Assert.Contains("TryReadHostShortcutTabOrdinal(combo)", hostBridge, StringComparison.Ordinal);
        Assert.Contains("TryActivateTabOrdinal(tabOrdinal.Value)", hostBridge, StringComparison.Ordinal);
        Assert.Contains("ordinal == 9", ordinalActivation, StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(target);", ordinalActivation, StringComparison.Ordinal);

        for (var ordinal = 1; ordinal <= 9; ordinal++)
        {
            Assert.Contains($"\"ctrl+{ordinal}\"", renderer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InactiveEditPreviewPrimeWaitsForVisibleViewerCommit()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var installSiblingViews = ExtractMethodBody(codeBehind, "private void InstallSiblingMountedViews(MainWindowViewModel viewModel)");
        var primeInstaller = ExtractMethodBody(codeBehind, "private void InstallInactiveEditPreviewPrime(");
        var tryPrime = ExtractMethodBody(primeInstaller, "void TryPrime()");
        var closeHandler = ExtractMethodBody(primeInstaller, "void OnPrimeClosed(object? sender, EventArgs e)");
        var commitHandler = ExtractMethodBody(primeInstaller, "void OnViewerHostCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)");

        Assert.Contains("viewerHostForMode);", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("IApplicateSharedWebViewHost? viewerCommitHost", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.CommitCompleted += OnViewerHostCommitCompleted;", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.CommitCompleted -= OnViewerHostCommitCompleted;", closeHandler, StringComparison.Ordinal);

        Assert.Contains("e.TransactionGeneration != 0", commitHandler, StringComparison.Ordinal);
        Assert.Contains("e.Mode != ApplicateMode.Viewer", commitHandler, StringComparison.Ordinal);
        Assert.Contains("QueuePrime();", commitHandler, StringComparison.Ordinal);

        var gateIndex = tryPrime.IndexOf("viewerCommitHost.View.HasLoadedDocumentForSource(document)", StringComparison.Ordinal);
        var beginLayoutIndex = tryPrime.IndexOf("BeginPrimeLayout(editWorkspaceSize)", StringComparison.Ordinal);
        Assert.True(gateIndex >= 0, "TryPrime should gate on the viewer host's current loaded document.");
        Assert.True(beginLayoutIndex > gateIndex, "Inactive prime should not begin layout before the viewer commit gate.");
        Assert.Contains("Equals(viewerCommitHost.View.ReadingPreferences, preferences)", tryPrime, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-gated\"", tryPrime, StringComparison.Ordinal);
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

    private static string ReadRendererSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "RendererWeb",
            "src",
            "renderer.ts"));

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
