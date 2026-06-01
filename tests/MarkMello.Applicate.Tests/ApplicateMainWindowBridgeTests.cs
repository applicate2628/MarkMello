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
    public void SessionRestorePrefersCommandLineActivationBeforeViewModelDocumentExists()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var restore = ExtractFromMarker(bridge, "var argvPath =");

        var activationIndex = restore.IndexOf(
            "App.Services?.GetService<ICommandLineActivation>()?.GetActivationFilePath()",
            StringComparison.Ordinal);
        var fallbackIndex = restore.IndexOf("argvPath = viewModel.Document?.Path;", StringComparison.Ordinal);
        var preferredIndex = restore.IndexOf("var preferredActivePath = !string.IsNullOrWhiteSpace(argvPath)", StringComparison.Ordinal);
        var startupFallbackIndex = restore.IndexOf("saved.GetStartupDocumentPath()", StringComparison.Ordinal);

        Assert.True(activationIndex >= 0, "Startup restore should read the command-line activation path directly.");
        Assert.True(fallbackIndex > activationIndex, "ViewModel.Document should only be a fallback after direct activation lookup.");
        Assert.True(preferredIndex > fallbackIndex, "The preferred active path should be computed after argv fallback is resolved.");
        Assert.True(startupFallbackIndex > preferredIndex, "Session restore should use the session-owned startup path fallback.");
    }

    [Fact]
    public void SessionRestoreDoesNotDuplicateViewModelOpenForAlreadyOpeningPath()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var restoreApply = ExtractFromMarker(bridge, "var startupLoadIsPending =");

        var pendingIndex = restoreApply.IndexOf("var startupLoadIsPending =", StringComparison.Ordinal);
        var openingPathIndex = restoreApply.IndexOf("viewModel.IsOpeningPath(toActivate.FilePath)", StringComparison.Ordinal);
        var guardIndex = restoreApply.IndexOf("&& !startupLoadIsPending", StringComparison.Ordinal);
        var openPathIndex = restoreApply.IndexOf("await viewModel.OpenPathAsync(toActivate.FilePath).ConfigureAwait(true);", StringComparison.Ordinal);

        Assert.True(pendingIndex >= 0, "Startup restore should detect an already pending ViewModel open.");
        Assert.True(openingPathIndex > pendingIndex, "Pending detection should be based on the ViewModel opening path.");
        Assert.True(guardIndex > openingPathIndex, "The duplicate-open guard should be part of the restore apply condition.");
        Assert.True(openPathIndex > guardIndex, "OpenPathAsync should remain only after the pending-load guard.");
    }

    [Fact]
    public void StartupArgvDocumentKeepsWindowCoveredUntilViewerRevealReady()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var constructor = ExtractMethodBody(codeBehind, "public ApplicateMainWindow(");
        var shouldHold = ExtractMethodBody(codeBehind, "private static bool ShouldHoldStartupDocumentReveal()");
        var gate = ExtractMethodBody(codeBehind, "private void InstallStartupDocumentRevealGate(MainWindowViewModel viewModel)");

        Assert.Contains("ShouldHoldStartupDocumentReveal()", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity = 0;", constructor, StringComparison.Ordinal);
        Assert.Contains("InstallStartupDocumentRevealGate(viewModel);", constructor, StringComparison.Ordinal);
        Assert.Contains("skipInitialViewerDocumentSwitchCover: holdStartupDocumentReveal", constructor, StringComparison.Ordinal);
        Assert.Contains("GetService<ICommandLineActivation>()?.GetActivationFilePath()", shouldHold, StringComparison.Ordinal);
        Assert.Contains("GetService<IApplicateSessionStore>()", shouldHold, StringComparison.Ordinal);
        Assert.Contains("sessionStore.LoadAsync().AsTask().GetAwaiter().GetResult()", shouldHold, StringComparison.Ordinal);
        Assert.Contains("session.GetStartupDocumentPath()", shouldHold, StringComparison.Ordinal);
        Assert.Contains("System.IO.File.Exists(restoredStartupPath)", shouldHold, StringComparison.Ordinal);
        Assert.Contains("var startupCover = new ApplicateModeRevealCoverWindow();", gate, StringComparison.Ordinal);
        Assert.Contains("var startupWindowOpened = false;", gate, StringComparison.Ordinal);
        Assert.Contains("Opened += OnStartupWindowOpened;", gate, StringComparison.Ordinal);
        Assert.Contains("SizeChanged += OnStartupWindowSizeChanged;", gate, StringComparison.Ordinal);
        Assert.Contains("startupWindowOpened = true;", gate, StringComparison.Ordinal);
        Assert.Contains("if (!startupWindowOpened)", gate, StringComparison.Ordinal);
        Assert.Contains("startupCover.ShowStartupSplash(this, viewModel.Document?.FileName)", gate, StringComparison.Ordinal);
        Assert.Contains("startupViewerHost.View.DocumentRevealReady += OnDocumentRevealReady;", gate, StringComparison.Ordinal);
        Assert.Contains("startupViewerHost.View.HeadingsChanged += OnHeadingsChanged;", gate, StringComparison.Ordinal);
        Assert.Contains("startupViewerHost.View.ModeToggleSettled += OnRendererSettled;", gate, StringComparison.Ordinal);
        Assert.Contains("startupViewerHost.View.RequestModeToggleSettleProbe();", gate, StringComparison.Ordinal);
        Assert.Contains("startupViewerHost.RendererFailed += OnRendererFailed;", gate, StringComparison.Ordinal);
        Assert.Contains("viewModel.PropertyChanged += OnViewModelPropertyChanged;", gate, StringComparison.Ordinal);
        Assert.Contains("ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(", gate, StringComparison.Ordinal);
        Assert.Contains("ApplicateSharedWebViewHost.RendererSettleFallbackTimeout", gate, StringComparison.Ordinal);
        Assert.Contains("headingsReady = !waitForHeadings || headings.Count > 0;", gate, StringComparison.Ordinal);
        Assert.Contains("TryRelease(\"headings-reported\");", gate, StringComparison.Ordinal);
        Assert.Contains("ArmRendererSettle(reason);", gate, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-renderer-settle-armed\"", gate, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-renderer-settle-complete\"", gate, StringComparison.Ordinal);
        Assert.Contains("ReleaseRendererSettleWait();", gate, StringComparison.Ordinal);
        Assert.Contains("ReleaseAfterPaint(reason);", gate, StringComparison.Ordinal);
        Assert.Contains("topLevel.RequestAnimationFrame", gate, StringComparison.Ordinal);
        Assert.Contains("new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }", gate, StringComparison.Ordinal);
        Assert.Contains("new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) }", gate, StringComparison.Ordinal);
        Assert.Contains("Opacity = 1;", gate, StringComparison.Ordinal);
        Assert.Contains("? TimeSpan.Zero", gate, StringComparison.Ordinal);
        Assert.Contains(": ApplicateMotion.ModeSwitchDuration(viewModel.ReadingPreferences);", gate, StringComparison.Ordinal);
        Assert.Contains("startupCover.Hide(duration);", gate, StringComparison.Ordinal);
        Assert.Contains("durationMs={duration.TotalMilliseconds:F0}", gate, StringComparison.Ordinal);
        Assert.Contains("startup-window-reveal-released", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupPrewarmPrioritizesVisibleViewerBeforeEditPreview()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var preWarm = ExtractMethodBody(codeBehind, "private void InstallSharedWebViewPreWarm()");
        var deferred = ExtractMethodBody(codeBehind, "private void InstallDeferredSecondaryWebViewPreWarm(");

        Assert.Contains("GetService<IApplicateSharedWebViewHostProvider>()", preWarm, StringComparison.Ordinal);
        Assert.Contains("_ = provider.ViewerHost.PreWarmShellAsync();", preWarm, StringComparison.Ordinal);
        Assert.Contains("InstallDeferredSecondaryWebViewPreWarm(provider.ViewerHost, provider.EditPreviewHost);", preWarm, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var sharedHost in EnumerateSharedWebViewHosts())", preWarm, StringComparison.Ordinal);

        Assert.Contains("visibleHost.View.DocumentRevealReady += onPrimaryDocumentRevealReady;", deferred, StringComparison.Ordinal);
        Assert.Contains("visibleHost.View.ProgressiveAppendCompleted += onPrimaryProgressiveAppendCompleted;", deferred, StringComparison.Ordinal);
        Assert.Contains("visibleHost.View.HasPendingProgressiveAppend", deferred, StringComparison.Ordinal);
        Assert.Contains("visibleHost.RendererFailed += onPrimaryRendererFailed;", deferred, StringComparison.Ordinal);
        Assert.Contains("SecondaryWebViewPreWarmFallbackDelay", deferred, StringComparison.Ordinal);
        Assert.Contains("SecondaryWebViewPreWarmDelay", deferred, StringComparison.Ordinal);
        Assert.Contains("\"secondary-shell-prewarm-deferred\"", deferred, StringComparison.Ordinal);
        Assert.Contains("\"secondary-shell-prewarm-wait-progressive\"", deferred, StringComparison.Ordinal);
        Assert.Contains("\"visible-progressive-append-ready\"", deferred, StringComparison.Ordinal);
        Assert.Contains("\"secondary-shell-prewarm-start\"", deferred, StringComparison.Ordinal);
        Assert.Contains("InstallWarmupPanelForHost(secondaryHost, index: 1);", deferred, StringComparison.Ordinal);
        Assert.Contains("secondaryHost.PreWarmShellAsync()", deferred, StringComparison.Ordinal);

        var warmup = ExtractMethodBody(codeBehind, "private void InstallSharedWebViewWarmupPanel()");
        Assert.Contains("InstallWarmupPanelForHost(provider.ViewerHost, index: 0);", warmup, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.EditPreviewHost", warmup, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateSharedWebViewHosts()", warmup, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentSwitchCoverIsInstalledForReaderAndEditSurfaces()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var installSiblingViews = ExtractMethodBody(codeBehind, "private void InstallSiblingMountedViews(");
        var disposeHandler = ExtractMethodBody(codeBehind, "private void OnApplicateMainWindowClosed(object? sender, EventArgs e)");

        Assert.Contains("bool skipInitialViewerDocumentSwitchCover = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewerDocumentSwitchRevealCoordinator = new ApplicateDocumentSwitchRevealCoordinator(", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("viewerHostForMode,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("ApplicateMode.Viewer,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("() => viewModel.IsViewer && !viewModel.IsEditMode", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("skipInitialCoverSession: skipInitialViewerDocumentSwitchCover", installSiblingViews, StringComparison.Ordinal);

        Assert.Contains("_editDocumentSwitchRevealCoordinator = new ApplicateDocumentSwitchRevealCoordinator(", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("editHost,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("ApplicateMode.Edit,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("() => viewModel.IsViewer && viewModel.IsEditMode", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("clearHeadingsOnRendererFailure: false", installSiblingViews, StringComparison.Ordinal);

        Assert.Contains("_viewerThemeSwitchRevealCoordinator = new ApplicateThemeSwitchRevealCoordinator(", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("_editThemeSwitchRevealCoordinator = new ApplicateThemeSwitchRevealCoordinator(", installSiblingViews, StringComparison.Ordinal);

        Assert.Contains("_viewerDocumentSwitchRevealCoordinator?.Dispose();", disposeHandler, StringComparison.Ordinal);
        Assert.Contains("_editDocumentSwitchRevealCoordinator?.Dispose();", disposeHandler, StringComparison.Ordinal);
        Assert.Contains("_viewerThemeSwitchRevealCoordinator?.Dispose();", disposeHandler, StringComparison.Ordinal);
        Assert.Contains("_editThemeSwitchRevealCoordinator?.Dispose();", disposeHandler, StringComparison.Ordinal);
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
        var installSiblingViews = ExtractMethodBody(codeBehind, "private void InstallSiblingMountedViews(");
        var primeInstaller = ExtractMethodBody(codeBehind, "private void InstallInactiveEditPreviewPrime(");
        var tryPrime = ExtractMethodBody(primeInstaller, "void TryPrime()");
        var closeHandler = ExtractMethodBody(primeInstaller, "void OnPrimeClosed(object? sender, EventArgs e)");
        var commitHandler = ExtractMethodBody(primeInstaller, "void OnViewerHostCommitCompleted(object? sender, ApplicateCommitCompletedEventArgs e)");
        var revealHandler = ExtractMethodBody(primeInstaller, "void OnViewerDocumentRevealReady(object? sender, EventArgs e)");
        var sizeOnlySkip = ExtractMethodBody(primeInstaller, "bool TrySkipViewportOnlyPrime(");

        Assert.Contains("viewerHostForMode,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("editHost);", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("IApplicateSharedWebViewHost? viewerCommitHost", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IApplicateSharedWebViewHost? editPreviewHost", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.CommitCompleted += OnViewerHostCommitCompleted;", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.View.DocumentRevealReady += OnViewerDocumentRevealReady;", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.View.ProgressiveAppendCompleted += OnViewerProgressiveAppendCompleted;", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.CommitCompleted -= OnViewerHostCommitCompleted;", closeHandler, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.View.DocumentRevealReady -= OnViewerDocumentRevealReady;", closeHandler, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.View.ProgressiveAppendCompleted -= OnViewerProgressiveAppendCompleted;", closeHandler, StringComparison.Ordinal);

        Assert.Contains("e.TransactionGeneration != 0", commitHandler, StringComparison.Ordinal);
        Assert.Contains("e.Mode != ApplicateMode.Viewer", commitHandler, StringComparison.Ordinal);
        Assert.Contains("QueuePrime();", commitHandler, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost.View.HasLoadedDocumentForSource(document)", revealHandler, StringComparison.Ordinal);
        Assert.Contains("revealReadyDocument = document;", revealHandler, StringComparison.Ordinal);
        Assert.Contains("void OnViewerProgressiveAppendCompleted(object? sender, EventArgs e)", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("=> QueuePrime();", primeInstaller, StringComparison.Ordinal);

        var gateIndex = tryPrime.IndexOf("viewerCommitHost.View.HasLoadedDocumentForSource(document)", StringComparison.Ordinal);
        var sharedHostGateIndex = tryPrime.IndexOf("ReferenceEquals(viewerCommitHost, editPreviewHost)", StringComparison.Ordinal);
        var activeViewerSkipIndex = tryPrime.IndexOf("editpreview-inactive-prime-skipped-active-viewer", StringComparison.Ordinal);
        var sizeOnlySkipIndex = tryPrime.IndexOf("TrySkipViewportOnlyPrime(document, preferences, viewportSize)", StringComparison.Ordinal);
        var revealGateIndex = tryPrime.IndexOf("IsViewerRevealReadyForPrime(document, preferences)", StringComparison.Ordinal);
        var progressiveGateIndex = tryPrime.IndexOf("IsViewerProgressiveAppendPendingForPrime(document)", StringComparison.Ordinal);
        var delayedHeavyIndex = tryPrime.IndexOf("ScheduleDelayedHeavyPrime(document, preferences, viewportSize);", StringComparison.Ordinal);
        var beginLayoutIndex = tryPrime.IndexOf("BeginPrimeLayout(editWorkspaceSize)", StringComparison.Ordinal);
        Assert.True(gateIndex >= 0, "TryPrime should gate on the viewer host's current loaded document.");
        Assert.True(sharedHostGateIndex > gateIndex, "Inactive prime should skip the active-viewer path only when reader and preview share one host.");
        Assert.True(activeViewerSkipIndex > sharedHostGateIndex, "Inactive prime should not steal a fallback shared WebView from the active viewer.");
        Assert.True(sizeOnlySkipIndex > activeViewerSkipIndex, "A resize-only re-prime should be skipped only after the active-viewer ownership gate.");
        Assert.True(revealGateIndex > sizeOnlySkipIndex, "Heavy edit-preview prime should wait for the viewer reveal-ready gate after the resize-only reuse gate.");
        Assert.True(progressiveGateIndex > revealGateIndex, "Heavy edit-preview prime should wait for visible progressive append before arming its delay.");
        Assert.True(delayedHeavyIndex > progressiveGateIndex, "Heavy edit-preview prime should delay only after visible progressive append is complete.");
        Assert.True(beginLayoutIndex > activeViewerSkipIndex, "Inactive prime should not begin layout before the active-viewer ownership gate.");
        Assert.Contains("Equals(viewerCommitHost.View.ReadingPreferences, preferences)", tryPrime, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-gated\"", tryPrime, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-gated-progressive\"", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("viewerCommitHost?.View.HasPendingProgressiveAppend", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-skipped-active-viewer\"", tryPrime, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-skipped-size-only\"", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("ApplicateEditPreviewView.CreateWebPreviewPreferences(preferences)", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("editPreviewHost.View.HasLoadedDocumentForSource(document)", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("Equals(editPreviewHost.View.ReadingPreferences, previewPreferences)", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("primedDocument = document;", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("primedPreferences = preferences;", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("primedViewportSize = viewportSize;", sizeOnlySkip, StringComparison.Ordinal);
        Assert.Contains("InactiveEditPrimeHeavyDelay", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(300)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("InactiveEditPrimeVeryHeavyDelay", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(1200)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResolveInactiveEditPrimeDelay(document.Content.Length)", primeInstaller, StringComparison.Ordinal);
        Assert.Contains("\"editpreview-inactive-prime-delayed-heavy\"", primeInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("\"editpreview-inactive-prime-skipped-heavy\"", tryPrime, StringComparison.Ordinal);
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
