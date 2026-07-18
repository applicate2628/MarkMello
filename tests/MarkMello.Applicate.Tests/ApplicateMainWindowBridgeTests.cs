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
    public void EditModeTabSwitchRoutesThroughDirtyGateWithCancelRevert()
    {
        // Audit Critical #1: the edit-mode tab switch must run through the
        // unsaved-changes prompt and revert the tab strip on Cancel. Locks the
        // branch so an upstream merge cannot silently drop the gate.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var editBranch = ExtractFromMarker(bridge, "// Audit Critical #1");

        Assert.Contains("RequestDocumentSwitchWithDirtyCheckAsync", editBranch, StringComparison.Ordinal);
        Assert.Contains("onCancel:", editBranch, StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(previous);", editBranch, StringComparison.Ordinal);
        Assert.Contains("inVmMirror = true;", editBranch, StringComparison.Ordinal);
        // Save resolution re-assert: the queued action must land the service on
        // the switch target even if the suppressed mirror ran in between.
        Assert.Contains("openDocs.Activate(target);", editBranch, StringComparison.Ordinal);

        // Ordering invariant (fable acceptance must-fix): the suppression flag
        // must NOT be cleared synchronously inside the switch action — the Save
        // resolution's Document-mirror lambda is POSTED before the queued switch
        // runs, so a synchronous clear would let the drained mirror re-activate
        // the OLD tab (tabs/editor split-brain). The clear + target re-assert
        // must live inside ONE posted reconciler that drains AFTER the mirror.
        var applyIndex = editBranch.IndexOf("ApplyOpenedDocumentInPlaceWithScroll(target);", StringComparison.Ordinal);
        var postIndex = editBranch.IndexOf("Dispatcher.UIThread.Post", StringComparison.Ordinal);
        var clearIndex = editBranch.IndexOf("pendingDirtySwitchTarget = null;", StringComparison.Ordinal);
        // Posts drain only after the synchronous action, so posting BEFORE the
        // apply keeps FIFO (mirror -> reconciler) AND releases the flag even if
        // the apply throws (fable re-acceptance hardening).
        Assert.True(postIndex >= 0 && applyIndex > postIndex, "The reconciler must be posted before the in-place apply (throw-safe, same FIFO).");
        Assert.True(clearIndex > postIndex, "The suppression flag may only be cleared inside the posted reconciler.");
    }

    [Fact]
    public void ReaderDirtyTabSwitchUsesVmOwnedRoutingAcrossTabAndHotkeyEntryPoints()
    {
        // R1 (data-loss): tab-strip clicks, tab-list selection, and Ctrl+1..9 all
        // call IOpenDocumentsService.Activate directly. The bridge must decide
        // whether the existing dirty-switch transaction applies; duplicating the
        // old edit-only formula here left a dirty reader session unprotected.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var tabs = ReadApplicateTabsViewSource();
        var tabList = ExtractMethodBody(tabs, "private void OpenTabList()");
        var hotkey = ExtractMethodBody(codeBehind, "private static bool TryActivateTabOrdinal(int ordinal)");

        Assert.Contains("private void ActivateIfClickOnly(OpenDocument doc)", tabs, StringComparison.Ordinal);
        Assert.Contains("_openDocsService.Activate(doc);", tabs, StringComparison.Ordinal);
        Assert.Contains("_openDocsService.Activate(target);", tabList, StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(target);", hotkey, StringComparison.Ordinal);
        Assert.Contains("if (viewModel.NeedsDirtySwitchRouting)", bridge, StringComparison.Ordinal);
        var readerRoute = bridge.IndexOf("if (viewModel.NeedsDirtySwitchRouting)", StringComparison.Ordinal);
        var transaction = bridge.IndexOf("RequestDocumentSwitchWithDirtyCheckAsync", readerRoute, StringComparison.Ordinal);
        Assert.True(transaction > readerRoute, "A dirty reader must enter the existing dirty-switch transaction.");
        Assert.Contains("onCancel:", bridge[readerRoute..], StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(previous);", bridge[readerRoute..], StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(target);", bridge[readerRoute..], StringComparison.Ordinal);
        Assert.Contains("pendingDirtySwitchTarget", bridge[readerRoute..], StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(args.ActiveDocument, openDocs.ActiveDocument)", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (viewModel.IsEditMode && viewModel.EditorSession is not null)",
            bridge,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EditModeFailedStubLoadBailsWithoutOverwritingDraft()
    {
        // Audit H2: a failed stub load must NOT publish an empty buffer into the
        // editor (a later Ctrl+S would truncate the real file) and must NOT route
        // through OpenPathAsync (its failure path destroys the session).
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var guard = ExtractFromMarker(bridge, "// Audit H2 guard");

        Assert.Contains("if (!target.IsLoaded)", guard, StringComparison.Ordinal);
        Assert.Contains("NotifyActiveTabLoadFailed", guard, StringComparison.Ordinal);

        var bailIndex = guard.IndexOf("NotifyActiveTabLoadFailed", StringComparison.Ordinal);
        var gateIndex = guard.IndexOf("RequestDocumentSwitchWithDirtyCheckAsync", StringComparison.Ordinal);
        Assert.True(bailIndex >= 0 && gateIndex > bailIndex, "The H2 bail must run before the dirty-gated apply.");
    }

    [Fact]
    public void TaskToggleCommitPatchesEditHostDomBeforeSilentSwap()
    {
        // fable re-acceptance must-fix: the edit-preview host is a DISTINCT
        // WebView whose primed DOM never saw the click; a bare silent swap
        // would declare "already rendered" content it never rendered, and the
        // next Ctrl+E would serve a lying pre-toggle checkbox. The wiring must
        // patch its checkbox surgically BEFORE the swap.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var taskHandler = ExtractFromMarker(bridge, "viewModel.TaskToggleCommitted");
        var mirror = ExtractMethodBody(bridge, "void MirrorReadingInPlaceEdit(");

        Assert.Contains("host => host.View.SetTaskCheckboxState(commit.Line, commit.Checked, commit.Source.Path)", taskHandler, StringComparison.Ordinal);
        Assert.Contains("applyViewerDomPatch: false", taskHandler, StringComparison.Ordinal);
        var editPatchIndex = mirror.IndexOf("patchHost(channelEditHost);", StringComparison.Ordinal);
        var editSwapIndex = mirror.IndexOf("channelEditHost.CommitInPlaceSourceSwap(source);", StringComparison.Ordinal);
        Assert.True(editPatchIndex >= 0, "The edit host must receive the surgical checkbox patch.");
        Assert.True(editSwapIndex > editPatchIndex, "The silent swap may run only AFTER the edit host's DOM was patched.");
    }

    [Fact]
    public void HistoryUndoPatchesBothHostsBeforeSourceSwap()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var historyHandler = ExtractFromMarker(bridge, "viewModel.InPlaceEditHistoryTransitioned");

        Assert.Contains("MirrorReadingInPlaceEdit(", historyHandler, StringComparison.Ordinal);
        Assert.Contains("transition.Source,", historyHandler, StringComparison.Ordinal);
        Assert.Contains("applyViewerDomPatch: true", historyHandler, StringComparison.Ordinal);

        var mirror = ExtractMethodBody(bridge, "void MirrorReadingInPlaceEdit(");
        var viewerPatchIndex = mirror.IndexOf("patchHost(channelViewerHost);", StringComparison.Ordinal);
        var viewerSwapIndex = mirror.IndexOf("channelViewerHost?.CommitInPlaceSourceSwap(source);", StringComparison.Ordinal);
        var editPatchIndex = mirror.IndexOf("patchHost(channelEditHost);", StringComparison.Ordinal);
        var editSwapIndex = mirror.IndexOf("channelEditHost.CommitInPlaceSourceSwap(source);", StringComparison.Ordinal);

        Assert.True(viewerPatchIndex >= 0, "History must patch the viewer before its silent source swap.");
        Assert.True(viewerSwapIndex > viewerPatchIndex, "The viewer source swap must follow its history DOM patch.");
        Assert.True(editPatchIndex >= 0, "History must patch the distinct edit-preview host before its silent source swap.");
        Assert.True(viewerSwapIndex > editPatchIndex, "Both history DOM patches must complete before either host silently swaps source.");
        Assert.True(editSwapIndex > editPatchIndex, "The edit-preview source swap must follow its history DOM patch.");
        Assert.Contains("openDocs.UpdateSourceText(mirrored, source.Content, preserveModified: true);", mirror, StringComparison.Ordinal);

        var historyPatch = ExtractMethodBody(bridge, "void ApplyHistoryDomPatch(");
        Assert.Contains("RealtimeInDocumentEditDomPatchKind.TaskCheckbox", historyPatch, StringComparison.Ordinal);
        Assert.Contains("RealtimeInDocumentEditDomPatchKind.TableCell", historyPatch, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingUndoRedoHotkeysAreModeGatedAndKeepEditModeNative()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var hostBridge = ExtractMethodBody(codeBehind, "private void InstallHostShortcutBridge(MainWindowViewModel viewModel)");
        var markup = ReadMainWindowMarkup();

        Assert.Contains("\"ctrl+z\" when !viewModel.IsEditMode => viewModel.UndoRealtimeInDocumentEditCommand", hostBridge, StringComparison.Ordinal);
        Assert.Contains("\"ctrl+y\" when !viewModel.IsEditMode => viewModel.RedoRealtimeInDocumentEditCommand", hostBridge, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Ctrl+Z\" Command=\"{Binding UndoRealtimeInDocumentEditCommand}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Gesture=\"Ctrl+Y\" Command=\"{Binding RedoRealtimeInDocumentEditCommand}\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingModeCommitMirrorsPreserveTheTabDirtyMarker()
    {
        // R1: the reading-mode toggle/cell commit carries UNSAVED content, so the
        // open-docs text sync must NOT clear the tab dirty marker (which VM.IsDirty
        // just lit). Both reading-commit mirror handlers must pass
        // preserveModified: true, and the bare (dirty-clearing) form must be gone.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var mirror = ExtractMethodBody(bridge, "void MirrorReadingInPlaceEdit(");

        var preserving = System.Text.RegularExpressions.Regex.Matches(
            mirror,
            @"openDocs\.UpdateSourceText\(mirrored, source\.Content, preserveModified: true\);");
        Assert.Single(preserving); // shared reading-mode mirror helper
        Assert.DoesNotContain(
            "openDocs.UpdateSourceText(mirrored, source.Content);",
            mirror,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingModeDiscardRepairsTheDistinctPrimedEditHost()
    {
        // R2 (data-loss): reading commits patch then silently swap the DISTINCT
        // primed edit host. Discard must inverse that convergence from the
        // persisted source; otherwise its stale DOM can be served on Ctrl+E.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var webView = ReadApplicateWebMarkdownDocumentViewSource();

        Assert.Contains("viewModel.InPlaceEditDiscarded", bridge, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(channelEditHost, channelViewerHost)", bridge, StringComparison.Ordinal);
        Assert.Contains("channelEditHost.View.RefreshInPlaceSource(source);", bridge, StringComparison.Ordinal);

        var refresh = ExtractMethodBody(
            webView,
            "internal void RefreshInPlaceSource(MarkdownSource source)");
        Assert.Contains("_hasLoadedDocument", refresh, StringComparison.Ordinal);
        Assert.Contains("Source = source;", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void TableCellIngressStampsOriginFromTheEditedSurface()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var siblingViews = ExtractMethodBody(codeBehind, "private void InstallSiblingMountedViews(");

        Assert.Contains(
            "viewModel.SetTableCellAsync(e.Line, e.CellIndex, e.Text, e.Key, TableCellEditOrigin.Viewer)",
            siblingViews,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.SetTableCellAsync(e.Line, e.CellIndex, e.Text, e.Key, TableCellEditOrigin.EditPreview)",
            siblingViews,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TableCellReadingCommitPatchesNonOriginEditHostBeforeSilentSwap()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var tableHandler = ExtractFromMarker(bridge, "viewModel.TableCellCommitted");
        var mirror = ExtractMethodBody(bridge, "void MirrorReadingInPlaceEdit(");

        Assert.Contains("host => host.View.SetTableCellText(commit.Line, commit.CellIndex, commit.Text, commit.Key, commit.Source.Path)", tableHandler, StringComparison.Ordinal);
        Assert.Contains("applyViewerDomPatch: true", tableHandler, StringComparison.Ordinal);
        var viewerPatch = mirror.IndexOf("patchHost(channelViewerHost);", StringComparison.Ordinal);
        var viewerSwap = mirror.IndexOf("channelViewerHost?.CommitInPlaceSourceSwap(source);", StringComparison.Ordinal);
        var editPatch = mirror.IndexOf("patchHost(channelEditHost);", StringComparison.Ordinal);
        var editSwap = mirror.IndexOf("channelEditHost.CommitInPlaceSourceSwap(source);", StringComparison.Ordinal);
        Assert.True(viewerPatch >= 0, "The origin viewer must receive its canonical acknowledgement.");
        Assert.True(viewerSwap > viewerPatch, "The viewer source swap must follow its canonical acknowledgement.");
        Assert.True(editPatch >= 0, "The distinct primed edit host must receive the canonical cell patch.");
        Assert.True(editSwap > editPatch, "The non-origin silent swap may run only after its DOM patch.");
    }

    [Fact]
    public void TableCellFailuresAndEditPreviewCommitsRouteOnlyToTheirOriginHost()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        Assert.Contains(
            "channelViewerHost?.View.RejectTableCellEdit(refusal.Line, refusal.CellIndex, refusal.Path, refusal.Busy);",
            bridge,
            StringComparison.Ordinal);
        var previewPatch = bridge.IndexOf(
            "channelEditHost?.View.SetTableCellText(commit.Line, commit.CellIndex, commit.Text, commit.Key, commit.Source.Path);",
            StringComparison.Ordinal);
        var previewSwap = previewPatch < 0
            ? -1
            : bridge.IndexOf("channelEditHost?.CommitInPlaceSourceSwap(commit.Source);", previewPatch, StringComparison.Ordinal);
        Assert.True(previewPatch >= 0, "The edit-preview origin must receive its canonical acknowledgement.");
        Assert.True(previewSwap > previewPatch, "The edit-preview swap must follow its canonical acknowledgement.");
        Assert.Contains(
            "channelEditHost?.View.RejectTableCellEdit(refusal.Line, refusal.CellIndex, refusal.Path, refusal.Busy);",
            bridge,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewCommitsEnterTheEditorBeforeTheirSilentSourceSwap()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var taskHandler = ExtractFromMarker(bridge, "viewModel.EditPreviewTaskToggleCommitted");
        var tableHandler = ExtractFromMarker(bridge, "viewModel.EditPreviewTableCellCommitted");

        // Fail closed: the silent Source swap is gated on ApplyEditModeSourceEdit
        // succeeding, so the preview never advances ahead of the editor buffer; on
        // failure the optimistic DOM flip is reverted instead.
        Assert.Contains("_editWorkspaceView?.ApplyEditModeSourceEdit(commit.Start, commit.Length, commit.Replacement) == true", taskHandler, StringComparison.Ordinal);
        Assert.Contains("_editWorkspaceView?.ApplyEditModeSourceEdit(commit.Start, commit.Length, commit.Replacement) == true", tableHandler, StringComparison.Ordinal);
        Assert.Contains("SetTaskCheckboxState(commit.Line, !commit.Checked", taskHandler, StringComparison.Ordinal);
        Assert.Contains("RejectTableCellEdit(commit.Line, commit.CellIndex", tableHandler, StringComparison.Ordinal);
        Assert.True(
            taskHandler.IndexOf("_editWorkspaceView?.ApplyEditModeSourceEdit", StringComparison.Ordinal)
            < taskHandler.IndexOf("channelEditHost?.CommitInPlaceSourceSwap", StringComparison.Ordinal));
        Assert.True(
            tableHandler.IndexOf("_editWorkspaceView?.ApplyEditModeSourceEdit", StringComparison.Ordinal)
            < tableHandler.IndexOf("channelEditHost?.CommitInPlaceSourceSwap", StringComparison.Ordinal));
    }

    [Fact]
    public void DocumentMirrorSuppressesForeignActivationDuringPendingDirtySwitch()
    {
        // fable review blocker A: while a dirty-prompted switch is pending, a
        // Save resolution publishes the OLD document; the mirror must not
        // re-activate it (tabs/editor split-brain) — only the pending target may.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        Assert.Contains("pendingDirtySwitchTarget is null", bridge, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(known, pendingDirtySwitchTarget)", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationDuringOpenDirtyPromptRevertsTabStrip()
    {
        // fable review blocker B: the prompt scrim does NOT cover the tab strip;
        // an activation arriving while the prompt is open must snap the strip
        // back to the document the editor holds.
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        Assert.Contains("if (viewModel.IsDirtyPromptOpen)", bridge, StringComparison.Ordinal);
        Assert.Contains("openDocs.Activate(editorDoc);", bridge, StringComparison.Ordinal);
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
        var compositor = ReadAirspaceCompositorSource();
        var hostAdapters = ReadAirspaceCompositorHostAdaptersSource();
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
        Assert.Contains("_airspaceCompositor.RegisterStartupSession(", gate, StringComparison.Ordinal);
        Assert.Contains("this,", gate, StringComparison.Ordinal);
        Assert.Contains("viewerHost,", gate, StringComparison.Ordinal);
        Assert.Contains("viewModel", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("var startupCover = new ApplicateModeRevealCoverWindow();", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("new DispatcherTimer", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("Opened += OnStartupWindowOpened;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeChanged += OnStartupWindowSizeChanged;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("startupViewerHost.View.DocumentRevealReady += OnDocumentRevealReady;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("startupViewerHost.View.HeadingsChanged += OnHeadingsChanged;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("startupViewerHost.View.ModeToggleSettled += OnRendererSettled;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("startupViewerHost.RendererFailed += OnRendererFailed;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("viewModel.PropertyChanged += OnViewModelPropertyChanged;", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("startupCover.Hide(duration);", gate, StringComparison.Ordinal);

        Assert.Contains("RegisterStartupSession", compositor, StringComparison.Ordinal);
        Assert.Contains("ShowStartupSplash", compositor, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateSharedWebViewHost.", compositor, StringComparison.Ordinal);
        Assert.Contains("ApplicateSharedWebViewHost.ShouldSkipRendererFrameWait(", hostAdapters, StringComparison.Ordinal);
        Assert.Contains("HostRendererSettleFallbackTimeout", hostAdapters, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-cover-shown\"", compositor, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-renderer-settle-armed\"", compositor, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-renderer-settle-complete\"", compositor, StringComparison.Ordinal);
        Assert.Contains("\"startup-window-reveal-released\"", compositor, StringComparison.Ordinal);
        Assert.Contains("durationMs={duration.TotalMilliseconds:F0}", compositor, StringComparison.Ordinal);
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
        var removedReaderField = "_viewerThemeSwitchReveal" + "Coordinator";
        var removedEditField = "_editThemeSwitchReveal" + "Coordinator";
        var removedType = "ApplicateThemeSwitchReveal" + "Coordinator";

        Assert.Contains("bool skipInitialViewerDocumentSwitchCover = false", codeBehind, StringComparison.Ordinal);
        Assert.Contains("var airspaceCompositor = new ApplicateAirspaceCompositor(siblingPanel, viewModel);", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("_airspaceCompositor = airspaceCompositor;", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("airspaceCompositor.RegisterDocumentSession(", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("viewerHostForMode,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("ApplicateMode.Viewer,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("() => viewModel.IsViewer && !viewModel.IsEditMode", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("skipInitialCoverSession: skipInitialViewerDocumentSwitchCover", installSiblingViews, StringComparison.Ordinal);

        Assert.Contains("editHost,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("ApplicateMode.Edit,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("() => viewModel.IsViewer && viewModel.IsEditMode", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("clearHeadingsOnRendererFailure: false", installSiblingViews, StringComparison.Ordinal);

        Assert.Contains("airspaceCompositor.RegisterThemeSession(", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("viewerHostForMode,", installSiblingViews, StringComparison.Ordinal);
        Assert.Contains("editHost,", installSiblingViews, StringComparison.Ordinal);
        Assert.DoesNotContain(removedReaderField, codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain(removedEditField, codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("new " + removedType + "(", codeBehind, StringComparison.Ordinal);

        Assert.Contains("_airspaceCompositor?.Dispose();", disposeHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ThemeSwitchReveal" + "Coordinator?.Dispose();", disposeHandler, StringComparison.Ordinal);
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

    private static string ReadApplicateTabsViewSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateTabsView.cs"));

    private static string ReadApplicateWebMarkdownDocumentViewSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateWebMarkdownDocumentView.cs"));

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

    private static string ReadMainWindowMarkup()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

    private static string ReadAirspaceCompositorSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "ApplicateAirspaceCompositor.cs"));

    private static string ReadAirspaceCompositorHostAdaptersSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Rendering",
            "ApplicateAirspaceCompositor.HostAdapters.cs"));

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
