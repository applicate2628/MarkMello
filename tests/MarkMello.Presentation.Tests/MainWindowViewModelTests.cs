using MarkMello.Application.UseCases;
using MarkMello.Application.Updates;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;
using System.Globalization;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task ToggleEditModeCommandLazilyCreatesEditorSession()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);

        Assert.False(harness.ViewModel.IsEditMode);
        Assert.Null(harness.ViewModel.EditorSession);
        Assert.Same(harness.ViewModel, harness.ViewModel.ActiveDocumentContent);
        Assert.Contains(StartupStage.DocumentModelReady, harness.StartupMetrics.Marks);
        Assert.DoesNotContain(StartupStage.ReadableDocument, harness.StartupMetrics.Marks);

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsEditMode);
        Assert.NotNull(harness.ViewModel.EditorSession);
        Assert.Same(harness.ViewModel.EditorSession, harness.ViewModel.ActiveDocumentContent);
        Assert.Equal("Reading", harness.ViewModel.EditToggleLabel);
        Assert.Equal(1, harness.StartupMetrics.Marks.Count(stage => stage == StartupStage.EditorActivation));
    }

    [Fact]
    public async Task ToggleEditModeCommandWhenDirtyShowsPromptAndDiscardLeavesEditMode()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "changed";

        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal("one.md •", harness.ViewModel.TitleFileDisplayName);

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.Contains("reading mode", harness.ViewModel.DirtyPromptMessage, StringComparison.OrdinalIgnoreCase);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.Equal("alpha beta", harness.ViewModel.Document!.Content);
    }

    [Fact]
    public async Task OpenDroppedFileAsyncWhenEditorIsDirtyDefersNavigationUntilDiscard()
    {
        var harness = CreateHarness();
        var firstPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var secondPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "two.md");
        harness.Loader.Sources[firstPath] = CreateSource(firstPath, "first");
        harness.Loader.Sources[secondPath] = CreateSource(secondPath, "second");

        await harness.ViewModel.OpenPathAsync(firstPath);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "first changed";

        await harness.ViewModel.OpenDroppedFileAsync(secondPath);

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Equal("one.md", harness.ViewModel.FileName);
        Assert.Equal("first", harness.ViewModel.Document!.Content);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.Equal("two.md", harness.ViewModel.FileName);
        Assert.Equal("second", harness.ViewModel.Document!.Content);
    }

    [Fact]
    public void ToggleAppMenuCommandOpensMenuAndClearErrorClosesOverlay()
    {
        var harness = CreateHarness();

        harness.ViewModel.ToggleAppMenuCommand.Execute(null);

        Assert.True(harness.ViewModel.IsAppMenuOpen);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);
        Assert.True(harness.ViewModel.HasOpenOverlay);

        harness.ViewModel.ClearErrorCommand.Execute(null);

        Assert.False(harness.ViewModel.IsAppMenuOpen);
        Assert.False(harness.ViewModel.HasOpenOverlay);
    }

    [Fact]
    public async Task EnteringEditModeClosesAndHidesAppMenuOverlay()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);
        harness.ViewModel.ToggleAppMenuCommand.Execute(null);

        Assert.True(harness.ViewModel.IsAppMenuOpen);
        Assert.True(harness.ViewModel.ShowsAppMenuControl);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsEditMode);
        Assert.False(harness.ViewModel.ShowsAppMenuControl);
        Assert.False(harness.ViewModel.IsAppMenuOpen);
        Assert.False(harness.ViewModel.IsAppOverlayOpen);
    }

    [Fact]
    public async Task TableOfContentsRemainsVisibleInEditModeWhenDocumentHasHeadings()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "# Intro");

        await harness.ViewModel.OpenPathAsync(path);
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro", 0),
        ]);

        Assert.True(harness.ViewModel.IsTocVisible);

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsViewer);
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.True(harness.ViewModel.IsTocVisible);
    }

    [Fact]
    public void ScrollToHeadingCommandImmediatelyMovesActiveHeadingSelection()
    {
        var harness = CreateHarness();
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro", 0),
            new DocumentHeading("details", 2, "Details", 12),
        ]);

        harness.ViewModel.ScrollToHeadingCommand.Execute("details");

        Assert.Equal("details", harness.ViewModel.ActiveHeadingId);
    }

    [Fact]
    public void RendererActiveHeadingDoesNotOverridePendingTocClickUntilRequestedHeadingArrives()
    {
        var harness = CreateHarness();
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro", 0),
            new DocumentHeading("details", 2, "Details", 12),
        ]);

        harness.ViewModel.ScrollToHeadingCommand.Execute("details");
        harness.ViewModel.UpdateActiveHeadingFromRenderer("intro");

        Assert.Equal("details", harness.ViewModel.ActiveHeadingId);

        harness.ViewModel.UpdateActiveHeadingFromRenderer("details");
        harness.ViewModel.UpdateActiveHeadingFromRenderer("intro");

        Assert.Equal("intro", harness.ViewModel.ActiveHeadingId);
    }

    [Fact]
    public void UpdateDocumentHeadingsReplacesCollectionInsteadOfMutatingPerHeading()
    {
        var harness = CreateHarness();
        var originalCollection = harness.ViewModel.DocumentHeadings;
        var originalCollectionChanges = 0;
        originalCollection.CollectionChanged += (_, _) => originalCollectionChanges++;

        var propertyChanges = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        var headings = Enumerable.Range(1, 50)
            .Select(index => new DocumentHeading($"h{index}", 2, $"Heading {index}", 12))
            .ToArray();

        harness.ViewModel.UpdateDocumentHeadings(headings);

        Assert.NotSame(originalCollection, harness.ViewModel.DocumentHeadings);
        Assert.Equal(0, originalCollectionChanges);
        Assert.Equal(headings, harness.ViewModel.DocumentHeadings);
        Assert.Contains(nameof(MainWindowViewModel.DocumentHeadings), propertyChanges);
        Assert.Contains(nameof(MainWindowViewModel.IsTocVisible), propertyChanges);
        Assert.Contains(nameof(MainWindowViewModel.HasDocumentHeadings), propertyChanges);
    }

    [Fact]
    public async Task ReadableDocumentMetricIsMarkedOnlyAfterViewReportsRender()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);

        Assert.Contains(StartupStage.DocumentModelReady, harness.StartupMetrics.Marks);
        Assert.DoesNotContain(StartupStage.ReadableDocument, harness.StartupMetrics.Marks);

        harness.ViewModel.MarkReadableDocumentRendered();
        harness.ViewModel.MarkReadableDocumentRendered();

        Assert.Equal(1, harness.StartupMetrics.Marks.Count(stage => stage == StartupStage.ReadableDocument));
    }

    [Fact]
    public void ToggleSettingsCommandReplacesAppMenuWithReadingSettings()
    {
        var harness = CreateHarness();

        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        harness.ViewModel.ToggleSettingsCommand.Execute(null);

        Assert.False(harness.ViewModel.IsAppMenuOpen);
        Assert.True(harness.ViewModel.IsSettingsOpen);
        Assert.False(harness.ViewModel.IsAppOverlayOpen);
    }

    [Fact]
    public void OpenAppSettingsCommandSwitchesFromMenuToAppSettings()
    {
        var harness = CreateHarness();

        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        harness.ViewModel.OpenAppSettingsCommand.Execute(null);

        Assert.False(harness.ViewModel.IsAppMenuOpen);
        Assert.True(harness.ViewModel.IsAppSettingsOpen);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);

        harness.ViewModel.ReturnToAppMenuCommand.Execute(null);

        Assert.True(harness.ViewModel.IsAppMenuOpen);
        Assert.False(harness.ViewModel.IsAppSettingsOpen);
    }

    [Fact]
    public void OpenAppUpdatesCommandSwitchesFromMenuToAppUpdates()
    {
        var harness = CreateHarness();

        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        harness.ViewModel.OpenAppUpdatesCommand.Execute(null);

        Assert.False(harness.ViewModel.IsAppMenuOpen);
        Assert.True(harness.ViewModel.IsAppUpdatesOpen);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);

        harness.ViewModel.ReturnToAppMenuCommand.Execute(null);

        Assert.True(harness.ViewModel.IsAppMenuOpen);
        Assert.False(harness.ViewModel.IsAppUpdatesOpen);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);
    }

    [Fact]
    public void OpenAboutCommandSwitchesFromSettingsToAboutAndBack()
    {
        var harness = CreateHarness();

        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        harness.ViewModel.OpenAppSettingsCommand.Execute(null);
        harness.ViewModel.OpenAboutCommand.Execute(null);

        Assert.True(harness.ViewModel.IsAppAboutOpen);
        Assert.True(harness.ViewModel.IsAppOverlayOpen);
        Assert.False(harness.ViewModel.IsAppSettingsOpen);

        harness.ViewModel.ReturnToAppSettingsCommand.Execute(null);

        Assert.False(harness.ViewModel.IsAppAboutOpen);
        Assert.True(harness.ViewModel.IsAppSettingsOpen);
    }

    [Fact]
    public async Task CreateNewDocumentCommandStartsInEditModeWithUnsavedDraft()
    {
        var harness = CreateHarness();

        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsViewer);
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.Null(harness.ViewModel.Document);
        Assert.NotNull(harness.ViewModel.EditorSession);
        Assert.Null(harness.ViewModel.EditorSession.CurrentPath);
        Assert.Equal("Untitled.md", harness.ViewModel.FileName);
        Assert.Equal("Untitled.md — MarkMello", harness.ViewModel.WindowTitle);
        Assert.Contains(StartupStage.EditorActivation, harness.StartupMetrics.Marks);
        Assert.DoesNotContain(StartupStage.ReadableDocument, harness.StartupMetrics.Marks);
    }

    [Fact]
    public async Task CloseFileCommandReturnsViewingDocumentToWelcome()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsWelcome);
        Assert.False(harness.ViewModel.IsViewer);
        Assert.Null(harness.ViewModel.Document);
        Assert.Null(harness.ViewModel.EditorSession);
        Assert.Equal("MarkMello", harness.ViewModel.WindowTitle);
        Assert.False(harness.ViewModel.CloseFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task CloseFileCommandWhenDirtyDraftPromptsAndDiscardReturnsToWelcome()
    {
        var harness = CreateHarness();

        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "# Draft";

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Contains("closing the current document", harness.ViewModel.DirtyPromptMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(harness.ViewModel.IsEditMode);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsWelcome);
        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Null(harness.ViewModel.Document);
        Assert.Null(harness.ViewModel.EditorSession);
        Assert.Equal("MarkMello", harness.ViewModel.WindowTitle);
    }

    [Fact]
    public async Task CloseFileCommandWhenDirtyAndSavedPersistsThenReturnsToWelcome()
    {
        var harness = CreateHarness();
        var savedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "close-after-save.md");
        harness.FilePicker.SavePath = savedPath;

        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "first draft";

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);
        await harness.ViewModel.ConfirmDirtySaveCommand.ExecuteAsync(null);

        Assert.Equal(["Untitled.md"], harness.FilePicker.SuggestedSaveFileNames);

        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(savedPath, save.Path);
        Assert.Equal("first draft", save.Content);
        Assert.True(harness.ViewModel.IsWelcome);
        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Null(harness.ViewModel.Document);
        Assert.Null(harness.ViewModel.EditorSession);
        Assert.Equal("MarkMello", harness.ViewModel.WindowTitle);
    }

    [Fact]
    public async Task SaveCommandPersistsEditorBufferAndClearsDirtyState()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "first");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.ShowsDirtySaveButton);

        harness.ViewModel.EditorSession!.SourceText = "first updated";

        Assert.True(harness.ViewModel.ShowsDirtySaveButton);

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(path, save.Path);
        Assert.Equal("first updated", save.Content);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.False(harness.ViewModel.ShowsDirtySaveButton);
        Assert.Equal("first updated", harness.ViewModel.Document!.Content);
        Assert.Equal("one.md", harness.ViewModel.TitleFileDisplayName);
    }

    [Fact]
    public async Task SaveCommandForNewDocumentUsesSaveAsPickerAndCreatesDocumentIdentity()
    {
        var harness = CreateHarness();
        var savedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "draft.md");
        harness.FilePicker.SavePath = savedPath;

        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "first draft";

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Untitled.md"], harness.FilePicker.SuggestedSaveFileNames);

        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(savedPath, save.Path);
        Assert.Equal("first draft", save.Content);
        Assert.Equal(savedPath, harness.ViewModel.Document!.Path);
        Assert.Equal("draft.md", harness.ViewModel.FileName);
        Assert.False(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task SaveCommandWhenSavingFailsKeepsDirtyStateAndShowsStatusMessage()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "first");
        harness.DocumentSaver.NextException = new UnauthorizedAccessException("blocked");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "first updated";

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsEditMode);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal("first", harness.ViewModel.Document!.Content);
        Assert.Equal($"Access denied: {path}", harness.ViewModel.EditorSession.StatusMessage);
    }

    [Fact]
    public async Task SaveAsCommandUsesPickerPathAndUpdatesDocumentIdentity()
    {
        var harness = CreateHarness();
        var originalPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var savedAsPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "renamed.md");
        harness.Loader.Sources[originalPath] = CreateSource(originalPath, "first");
        harness.FilePicker.SavePath = savedAsPath;

        await harness.ViewModel.OpenPathAsync(originalPath);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "first updated";

        await harness.ViewModel.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal(["one.md"], harness.FilePicker.SuggestedSaveFileNames);

        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(savedAsPath, save.Path);
        Assert.Equal("first updated", harness.ViewModel.Document!.Content);
        Assert.Equal(savedAsPath, harness.ViewModel.Document.Path);
        Assert.Equal("renamed.md", harness.ViewModel.FileName);
        Assert.False(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task CheckForUpdatesCommandWhenUpdateAvailableShowsDownloadAction()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Update 1.2.3 available", harness.ViewModel.UpdateStatusTitle);
        Assert.Contains(package.AssetName, harness.ViewModel.UpdateStatusMessage, StringComparison.Ordinal);
        Assert.True(harness.ViewModel.CanDownloadAvailableUpdate);
        Assert.False(harness.ViewModel.CanOpenDownloadedUpdate);
        Assert.Equal("Available", harness.ViewModel.UpdateStateBadge);
    }

    [Fact]
    public async Task InitializeAsyncStartsUpdateCheckInBackgroundWithoutBlockingStartup()
    {
        var harness = CreateHarness();
        var pendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        harness.UpdateService.NextCheckTask = pendingCheck.Task;

        var initializeTask = harness.ViewModel.InitializeAsync();

        var completed = await Task.WhenAny(initializeTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.Same(initializeTask, completed);
        Assert.Equal(1, harness.UpdateService.CheckCallCount);
        Assert.True(harness.ViewModel.IsCheckingForUpdates);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);

        pendingCheck.SetResult(new UpdateCheckResult.UpToDate(
            "1.0.0",
            "1.0.0",
            DateTimeOffset.Parse("2026-04-19T12:00:00Z", CultureInfo.InvariantCulture),
            "https://github.com/dartdavros/MarkMello/releases/tag/v1.0.0"));
        await harness.UpdateService.LastCheckTask!;
    }

    [Fact]
    public async Task StartupUpdateCheckKeepsAppMenuBadgeStableWhilePending()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var pendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        harness.UpdateService.NextCheckTask = pendingCheck.Task;

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.IsCheckingForUpdates);
        Assert.Equal("Checking", harness.ViewModel.UpdateStateBadge);
        Assert.Equal("Manual", harness.ViewModel.AppMenuUpdateStateBadge);

        pendingCheck.SetResult(new UpdateCheckResult.UpdateAvailable(package));
        await harness.UpdateService.LastCheckTask!;

        Assert.Equal("Available", harness.ViewModel.UpdateStateBadge);
        Assert.Equal("Available", harness.ViewModel.AppMenuUpdateStateBadge);
    }

    [Fact]
    public async Task CheckForUpdatesCommandWhilePendingExposesSmoothBusyState()
    {
        var harness = CreateHarness();
        var pendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        harness.UpdateService.NextCheckTask = pendingCheck.Task;

        var checkTask = harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsCheckingForUpdates);
        Assert.True(harness.ViewModel.IsUpdateBusy);
        Assert.Equal(1.0, harness.ViewModel.UpdateBusyIndicatorOpacity);
        Assert.Equal(0.0, harness.ViewModel.CheckForUpdatesIdleLabelOpacity);
        Assert.Equal(1.0, harness.ViewModel.CheckForUpdatesBusyLabelOpacity);
        Assert.Equal("Checking...", harness.ViewModel.CheckForUpdatesBusyLabel);
        Assert.Equal("Checking...", harness.ViewModel.CheckForUpdatesLabel);

        pendingCheck.SetResult(new UpdateCheckResult.SourceNotConfigured("No release source configured."));
        await checkTask;

        Assert.False(harness.ViewModel.IsUpdateBusy);
        Assert.Equal(0.0, harness.ViewModel.UpdateBusyIndicatorOpacity);
        Assert.Equal(1.0, harness.ViewModel.CheckForUpdatesIdleLabelOpacity);
        Assert.Equal(0.0, harness.ViewModel.CheckForUpdatesBusyLabelOpacity);
    }

    [Fact]
    public async Task StartupUpdateCheckWhenUpdateAvailableShowsDismissibleNotification()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        await harness.ViewModel.InitializeAsync();
        await harness.UpdateService.LastCheckTask!;

        Assert.Equal("Update 1.2.3 available", harness.ViewModel.UpdateStatusTitle);
        Assert.True(harness.ViewModel.IsUpdateNotificationVisible);
        Assert.True(harness.ViewModel.CanDownloadAvailableUpdate);

        harness.ViewModel.DismissUpdateNotificationCommand.Execute(null);

        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);
    }

    [Fact]
    public async Task DownloadUpdateCommandWhenSuccessfulShowsNativeAction()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var downloadedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", package.AssetName);
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.UpdateService.NextDownloadResult = new UpdateDownloadResult.Success(package, downloadedPath);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await harness.ViewModel.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Equal("Update ready", harness.ViewModel.UpdateStatusTitle);
        Assert.Contains(package.AssetName, harness.ViewModel.UpdateStatusMessage, StringComparison.Ordinal);
        Assert.False(harness.ViewModel.CanDownloadAvailableUpdate);
        Assert.True(harness.ViewModel.CanOpenDownloadedUpdate);
        Assert.Equal("Launch installer", harness.ViewModel.DownloadedUpdateActionLabel);
        Assert.Equal(downloadedPath, harness.ViewModel.DownloadedUpdatePath);
        Assert.Equal("Ready", harness.ViewModel.UpdateStateBadge);
    }

    [Fact]
    public async Task DownloadUpdateCommandWhilePendingKeepsDownloadSlotVisible()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var downloadedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", package.AssetName);
        var pendingDownload = new TaskCompletionSource<UpdateDownloadResult>();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.UpdateService.NextDownloadTask = pendingDownload.Task;

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        var downloadTask = harness.ViewModel.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsDownloadingUpdate);
        Assert.True(harness.ViewModel.IsUpdateBusy);
        Assert.Equal(1.0, harness.ViewModel.UpdateBusyIndicatorOpacity);
        Assert.Equal(1.0, harness.ViewModel.DownloadUpdateActionOpacity);
        Assert.Equal(0.0, harness.ViewModel.OpenDownloadedUpdateActionOpacity);
        Assert.Equal(0.0, harness.ViewModel.DownloadUpdateIdleLabelOpacity);
        Assert.Equal(1.0, harness.ViewModel.DownloadUpdateBusyLabelOpacity);
        Assert.Equal("Downloading...", harness.ViewModel.DownloadUpdateBusyLabel);
        Assert.Equal("Downloading...", harness.ViewModel.DownloadUpdateLabel);

        pendingDownload.SetResult(new UpdateDownloadResult.Success(package, downloadedPath));
        await downloadTask;

        Assert.False(harness.ViewModel.IsUpdateBusy);
        Assert.Equal(0.0, harness.ViewModel.UpdateBusyIndicatorOpacity);
        Assert.Equal(0.0, harness.ViewModel.DownloadUpdateActionOpacity);
        Assert.Equal(1.0, harness.ViewModel.OpenDownloadedUpdateActionOpacity);
    }

    [Fact]
    public async Task OpenDownloadedUpdateCommandWhenSuccessfulUpdatesStatus()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var downloadedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", package.AssetName);
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.UpdateService.NextDownloadResult = new UpdateDownloadResult.Success(package, downloadedPath);
        harness.UpdateService.NextPrepareResult =
            new UpdatePrepareResult.Success("Installer launched. Follow the native upgrade flow.");

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await harness.ViewModel.DownloadUpdateCommand.ExecuteAsync(null);
        await harness.ViewModel.OpenDownloadedUpdateCommand.ExecuteAsync(null);

        Assert.Equal("Native update flow started", harness.ViewModel.UpdateStatusTitle);
        Assert.Equal(
            "Installer launched. Follow the native upgrade flow.",
            harness.ViewModel.UpdateStatusMessage);
    }

    [Fact]
    public async Task InitializeAsyncLoadsSavedLanguageAndLocalizesShellLabels()
    {
        var harness = CreateHarness();
        harness.Settings.Language = AppLanguage.Russian;

        await harness.ViewModel.InitializeAsync();

        Assert.True(harness.ViewModel.IsRussianLanguageSelected);
        Assert.Equal("Редактирование", harness.ViewModel.EditToggleLabel);
        Assert.Equal("Проверить", harness.ViewModel.CheckForUpdatesLabel);
        Assert.Equal("Обновления", harness.ViewModel.UpdatesLabel);
    }

    [Fact]
    public void SelectRussianLanguageCommandPersistsLanguageAndRefreshesComputedLabels()
    {
        var harness = CreateHarness();

        harness.ViewModel.SelectRussianLanguageCommand.Execute(null);

        Assert.Equal(AppLanguage.Russian, harness.Settings.Language);
        Assert.True(harness.ViewModel.IsRussianLanguageSelected);
        Assert.Equal("Проверить", harness.ViewModel.CheckForUpdatesLabel);
        Assert.Equal("Слов: 0", harness.ViewModel.WordCountStatusLabel);
    }

    [Fact]
    public void SelectedLanguageOptionPersistsLanguageAndRefreshesDropdownLabels()
    {
        var harness = CreateHarness();
        var initialOptions = harness.ViewModel.LanguageOptions;
        var russianOption = initialOptions.Single(option => option.Language == AppLanguage.Russian);

        harness.ViewModel.SelectedLanguageOption = russianOption;

        var refreshedOptions = harness.ViewModel.LanguageOptions;

        Assert.Equal(AppLanguage.Russian, harness.Settings.Language);
        Assert.Equal(AppLanguage.Russian, harness.ViewModel.SelectedLanguageOption?.Language);
        Assert.NotSame(initialOptions, refreshedOptions);
        Assert.Same(
            refreshedOptions.Single(option => option.Language == AppLanguage.Russian),
            harness.ViewModel.SelectedLanguageOption);
        Assert.Equal("Английский", refreshedOptions.Single(option => option.Language == AppLanguage.English).Label);
        Assert.Equal("Слов: 0", harness.ViewModel.WordCountStatusLabel);
    }


    [Fact]
    public void SelectedLanguageOptionRaisesTypedNotificationsForVisibleShellBindings()
    {
        var harness = CreateHarness();
        var names = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        var russianOption = harness.ViewModel.LanguageOptions.Single(option => option.Language == AppLanguage.Russian);

        harness.ViewModel.SelectedLanguageOption = russianOption;

        Assert.Contains(nameof(MainWindowViewModel.WelcomeTagline), names);
        Assert.Contains(nameof(MainWindowViewModel.AppMenuHeader), names);
        Assert.Contains(nameof(MainWindowViewModel.LanguageOptions), names);
        Assert.Contains(nameof(MainWindowViewModel.SelectedLanguageOption), names);
        Assert.DoesNotContain("Item", names);
        Assert.DoesNotContain("Item[]", names);
        Assert.Equal("Тихое место для чтения Markdown.", harness.ViewModel.WelcomeTagline);
        Assert.Equal("МЕНЮ", harness.ViewModel.AppMenuHeader);
    }

    [Fact]
    public void AlwaysOnTopSelectionUpdatesWindowBindingState()
    {
        var harness = CreateHarness();

        Assert.False(harness.ViewModel.IsAlwaysOnTop);
        Assert.True(harness.ViewModel.IsAlwaysOnTopDisabled);

        harness.ViewModel.IsAlwaysOnTop = true;

        Assert.True(harness.ViewModel.IsAlwaysOnTop);
        Assert.False(harness.ViewModel.IsAlwaysOnTopDisabled);

        harness.ViewModel.IsAlwaysOnTopDisabled = true;

        Assert.False(harness.ViewModel.IsAlwaysOnTop);
        Assert.True(harness.ViewModel.IsAlwaysOnTopDisabled);
    }

    [Fact]
    public void RendererPreferenceChangeDoesNotNotifyUnrelatedReadingSettingButtons()
    {
        var harness = CreateHarness();
        var names = new List<string?>();
        harness.ViewModel.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        harness.ViewModel.IsNativeRendererSelected = true;

        Assert.Contains(nameof(MainWindowViewModel.SelectedRendererBackend), names);
        Assert.Contains(nameof(MainWindowViewModel.IsNativeRendererSelected), names);
        Assert.Contains(nameof(MainWindowViewModel.IsWebViewRendererSelected), names);
        Assert.DoesNotContain(nameof(MainWindowViewModel.IsDocumentMinimapAutoSelected), names);
        Assert.DoesNotContain(nameof(MainWindowViewModel.IsSerifFontSelected), names);
        Assert.DoesNotContain(nameof(MainWindowViewModel.IsWideWidthSelected), names);
    }

    [Fact]
    public void ModeSwitchSmoothSettingsUpdateReadingPreferences()
    {
        var harness = CreateHarness();

        harness.ViewModel.IsModeSwitchSmoothEnabled = false;
        harness.ViewModel.ModeSwitchSmoothDurationSetting = 260;

        Assert.False(harness.ViewModel.ReadingPreferences.ModeSwitchSmoothEnabled);
        Assert.Equal(260, harness.ViewModel.ReadingPreferences.ModeSwitchSmoothDurationMs);
        Assert.False(harness.ViewModel.IsModeSwitchSmoothEnabled);
        Assert.True(harness.ViewModel.IsModeSwitchSmoothDisabled);
        Assert.Equal(260, harness.ViewModel.ModeSwitchSmoothDurationSetting);
        Assert.Equal("260 ms", harness.ViewModel.ModeSwitchSmoothDurationLabel);

        harness.ViewModel.IsModeSwitchSmoothEnabled = true;

        Assert.True(harness.ViewModel.IsModeSwitchSmoothEnabled);
        Assert.False(harness.ViewModel.IsModeSwitchSmoothDisabled);
    }

    [Fact]
    public void ModeSwitchSmoothDurationRoundsToPreferenceStep()
    {
        var harness = CreateHarness();

        harness.ViewModel.ModeSwitchSmoothDurationSetting = 173;

        Assert.Equal(180, harness.ViewModel.ReadingPreferences.ModeSwitchSmoothDurationMs);
        Assert.Equal("180 ms", harness.ViewModel.ModeSwitchSmoothDurationLabel);
    }

    [Fact]
    public void LanguageOptionsKeepsStableItemReferencesBetweenLocalizationChanges()
    {
        var harness = CreateHarness();

        var firstRead = harness.ViewModel.LanguageOptions;
        var secondRead = harness.ViewModel.LanguageOptions;

        Assert.Same(firstRead, secondRead);
        Assert.Same(
            firstRead.Single(option => option.Language == AppLanguage.System),
            harness.ViewModel.SelectedLanguageOption);
    }

    [Fact]
    public void ReadingPaletteSelectionDefaultsToWhitePalette()
    {
        var harness = CreateHarness();

        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);
    }

    [Fact]
    public void WhitePaletteSelectionPersistsLightPaletteWithoutChangingThemeMode()
    {
        var harness = CreateHarness();

        harness.ViewModel.IsWhitePaletteSelected = true;

        Assert.Equal(LightPaletteMode.White, harness.ViewModel.ReadingPreferences.LightPalette);
        Assert.Equal(ThemeMode.System, harness.Settings.Theme);
        Assert.Equal(ThemeMode.System, harness.ViewModel.Theme);
        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);
    }

    [Fact]
    public void OriginalPaletteSelectionPersistsLightPaletteWithoutChangingThemeMode()
    {
        var harness = CreateHarness();

        harness.ViewModel.IsOriginalPaletteSelected = true;

        Assert.Equal(LightPaletteMode.Original, harness.ViewModel.ReadingPreferences.LightPalette);
        Assert.Equal(ThemeMode.System, harness.Settings.Theme);
        Assert.Equal(ThemeMode.System, harness.ViewModel.Theme);
        Assert.True(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.False(harness.ViewModel.IsWhitePaletteSelected);
    }

    [Fact]
    public void WhitePaletteSelectionWhileDarkPersistsPreferenceWithoutLeavingDarkMode()
    {
        var harness = CreateHarness();

        harness.ViewModel.CycleThemeCommand.Execute(null);
        harness.ViewModel.IsWhitePaletteSelected = true;

        Assert.Equal(LightPaletteMode.White, harness.ViewModel.ReadingPreferences.LightPalette);
        Assert.Equal(ThemeMode.Dark, harness.Settings.Theme);
        Assert.Equal(ThemeMode.Dark, harness.ViewModel.Theme);
        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);
    }

    [Fact]
    public void ThemeToggleReturnsFromDarkToLightThemeWhileKeepingSelectedPalette()
    {
        var harness = CreateHarness();

        harness.ViewModel.IsWhitePaletteSelected = true;
        harness.ViewModel.CycleThemeCommand.Execute(null);

        Assert.Equal(ThemeMode.Dark, harness.Settings.Theme);
        Assert.Equal(ThemeMode.Dark, harness.ViewModel.Theme);
        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);

        harness.ViewModel.CycleThemeCommand.Execute(null);

        Assert.Equal(ThemeMode.Light, harness.Settings.Theme);
        Assert.Equal(ThemeMode.Light, harness.ViewModel.Theme);
        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);
    }

    private static MarkdownSource CreateSource(string path, string content)
        => new(path, Path.GetFileName(path), content);

    private static AppUpdatePackage CreateUpdatePackage()
        => new(
            CurrentVersion: "1.0.0",
            ReleaseVersion: "1.2.3",
            ReleaseTag: "v1.2.3",
            PublishedAt: DateTimeOffset.Parse("2026-04-19T12:00:00Z", CultureInfo.InvariantCulture),
            ReleasePageUrl: "https://github.com/dartdavros/MarkMello/releases/tag/v1.2.3",
            AssetName: "MarkMello-setup-win-x64.exe",
            DownloadUrl: "https://github.com/dartdavros/MarkMello/releases/download/v1.2.3/MarkMello-setup-win-x64.exe",
            PlatformName: "Windows",
            ArchitectureName: "x64",
            InstallAction: AppUpdateInstallAction.LaunchInstaller);

    private static TestHarness CreateHarness()
    {
        var loader = new StubDocumentLoader();
        var saver = new RecordingDocumentSaver();
        var picker = new StubFilePicker();
        var settings = new InMemorySettingsStore();
        var localization = new LocalizationService(AppLanguage.English);
        var themeService = new RecordingThemeService();
        var startupMetrics = new RecordingStartupMetrics();
        var updateService = new StubUpdateService();
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(saver),
            picker,
            new StubCommandLineActivation(),
            localization,
            settings,
            themeService,
            startupMetrics,
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            updateService);

        return new TestHarness(loader, saver, picker, settings, startupMetrics, updateService, viewModel);
    }

    private sealed record TestHarness(
        StubDocumentLoader Loader,
        RecordingDocumentSaver DocumentSaver,
        StubFilePicker FilePicker,
        InMemorySettingsStore Settings,
        RecordingStartupMetrics StartupMetrics,
        StubUpdateService UpdateService,
        MainWindowViewModel ViewModel);
}
