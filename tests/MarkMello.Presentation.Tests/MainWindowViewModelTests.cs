using MarkMello.Application.Abstractions;
using MarkMello.Application.Diagnostics;
using MarkMello.Application.UseCases;
using MarkMello.Application.Updates;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;
using System.Diagnostics;
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
    public async Task TaskToggleSuccessMarksDirtyDeferredWithoutDiskWriteOrReRender()
    {
        // P1 in-place channel: a verified reading-mode flip patches
        // Document.Content in place and raises TaskToggleCommitted WITHOUT any
        // disk write and WITHOUT publishing PropertyChanged(Document) (no
        // re-render, no scroll motion). The edit is UNSAVED — a session is
        // materialized dirty and Ctrl+S owns the persistence.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n- [x] beta\n");
        await harness.ViewModel.OpenPathAsync(path);

        var documentChanges = 0;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document))
            {
                documentChanges++;
            }
        };
        TaskToggleCommit? committed = null;
        harness.ViewModel.TaskToggleCommitted += (_, commit) => committed = commit;
        var reverts = new List<TaskToggleRevertRequest>();
        harness.ViewModel.TaskToggleDomRevertRequested += (_, r) => reverts.Add(r);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        Assert.Empty(harness.DocumentSaver.Saves); // no auto-persist
        Assert.Equal("- [x] alpha\n- [x] beta\n", harness.ViewModel.Document!.Content);
        Assert.NotNull(committed);
        Assert.Equal(0, documentChanges);
        Assert.Empty(reverts);
        Assert.True(harness.ViewModel.IsDirty); // dirty, waiting on Ctrl+S
        Assert.NotNull(harness.ViewModel.EditorSession);
        Assert.Equal("- [x] alpha\n- [x] beta\n", harness.ViewModel.EditorSession!.SourceText);

        // Ctrl+S now persists exactly the flipped content.
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);
        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal("- [x] alpha\n- [x] beta\n", save.Content);
        Assert.False(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task TaskToggleSaveFailureSurfacesAtCtrlSNotOnTheFlip()
    {
        // P1: the flip never writes disk, so a disk failure can no longer happen
        // on the toggle — the flip commits into the dirty buffer. The save
        // failure moves to the Ctrl+S path, where the document stays dirty and a
        // status message is shown (the flip itself is untouched).
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);

        TaskToggleCommit? committed = null;
        harness.ViewModel.TaskToggleCommitted += (_, commit) => committed = commit;
        var reverts = new List<TaskToggleRevertRequest>();
        harness.ViewModel.TaskToggleDomRevertRequested += (_, r) => reverts.Add(r);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        Assert.NotNull(committed); // flip committed into the buffer
        Assert.Empty(reverts);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Empty(harness.DocumentSaver.Saves);

        harness.DocumentSaver.NextException = new IOException("disk full");
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsDirty); // stays dirty on save failure
        Assert.Equal("- [x] alpha\n", harness.ViewModel.EditorSession!.SourceText);
        Assert.False(string.IsNullOrEmpty(harness.ViewModel.EditorSession.StatusMessage));
    }

    [Fact]
    public async Task TaskToggleAlreadyInStateRevertsToBufferStateWithoutSave()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [x] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);

        var reverts = new List<TaskToggleRevertRequest>();
        harness.ViewModel.TaskToggleDomRevertRequested += (_, r) => reverts.Add(r);

        // Request the state it already has: the in-memory buffer already matches,
        // so the flip is refused and the one checkbox is restored surgically.
        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [x] alpha"), TaskToggleOrigin.Viewer);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.False(harness.ViewModel.IsDirty); // a refusal never dirties
        var revert = Assert.Single(reverts);
        Assert.True(revert.Checked); // actual in-memory state
        Assert.Equal(path, revert.Path); // carries the document-identity guard
    }

    [Fact]
    public async Task TaskToggleFlipsInMemoryBufferWithoutDiskReadOrReload()
    {
        // P1: the reading-mode flip resolves the IN-MEMORY buffer and never
        // re-reads disk, so an external edit on disk is NOT detected here
        // (last-writer-wins; the save-time disk-divergence check is the scheduled
        // follow-up). The line addressed in the rendered buffer is flipped as an
        // unsaved edit — no reload branch remains.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n- [ ] beta\n");
        await harness.ViewModel.OpenPathAsync(path);

        // External edit shifts disk lines — the in-memory buffer is unchanged.
        harness.Loader.Sources[path] = CreateSource(path, "intro\n- [ ] alpha\n- [ ] beta\n");

        var reverts = new List<TaskToggleRevertRequest>();
        harness.ViewModel.TaskToggleDomRevertRequested += (_, r) => reverts.Add(r);

        await harness.ViewModel.ToggleTaskLineAsync(1, true, TaskListIdentity.ComputeKey("- [ ] beta"), TaskToggleOrigin.Viewer);

        Assert.Empty(harness.DocumentSaver.Saves); // no disk write
        Assert.Empty(reverts);                     // in-memory line matched -> flipped
        Assert.Equal("- [ ] alpha\n- [x] beta\n", harness.ViewModel.Document!.Content);
        Assert.True(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task TaskToggleDormantSessionFlipMarksDirtyAndDiscardReverts()
    {
        // P1 inversion: a reading-mode toggle on a clean dormant session flips
        // the buffer as an UNSAVED edit — the baseline does NOT advance (no disk
        // write), so the session is truthfully dirty and Discard reverts the flip.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null); // create session
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null); // back to reading, session dormant
        Assert.False(harness.ViewModel.IsEditMode);
        var session = harness.ViewModel.EditorSession;
        Assert.NotNull(session);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal("- [x] alpha\n", session!.SourceText);
        Assert.Equal("- [ ] alpha\n", session.LastPersistedSource); // baseline unchanged
        Assert.True(session.IsDirty);

        // Discard reverts the unsaved flip (self-inverse — nothing was persisted).
        session.DiscardChanges();
        Assert.Equal("- [ ] alpha\n", session.SourceText);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task TaskToggleDormantDivergedBufferKeepsUserEditsAndDoesNotAdvanceBaseline()
    {
        // The toggled line is intact (flip lands) while another line carries an
        // unsaved user edit: the edit survives, and P1 does NOT advance the
        // baseline (no disk write), so it stays at the original disk content and
        // the session remains truthfully dirty.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\nbeta\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        var session = harness.ViewModel.EditorSession!;
        session.SourceText = "- [ ] alpha\nbeta edited\n"; // diverged dormant buffer

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal("- [x] alpha\nbeta edited\n", session.SourceText); // flip + user edit kept
        Assert.Equal("- [ ] alpha\nbeta\n", session.LastPersistedSource); // baseline == original disk
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task EditPreviewTogglePublishesDirectedSourceEditWithoutMutatingSession()
    {
        // The edit-preview strategy validates and publishes a directed source
        // replacement, but the edit workspace owns the actual buffer write.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        var session = harness.ViewModel.EditorSession!;

        TaskToggleCommit? committed = null;
        string? bufferAtCommitTime = null;
        harness.ViewModel.EditPreviewTaskToggleCommitted += (_, commit) =>
        {
            committed = commit;
            bufferAtCommitTime = session.SourceText;
        };

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.EditPreview);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.NotNull(committed);
        Assert.Equal("- [x] alpha\n", committed!.Source.Content);
        Assert.Equal("- [ ] alpha\n", bufferAtCommitTime);
        Assert.Equal("- [ ] alpha\n", session.SourceText);
        Assert.False(session.IsDirty);
        Assert.Equal(3, committed.Start);
        Assert.Equal(1, committed.Length);
        Assert.Equal("x", committed.Replacement);
    }

    [Fact]
    public async Task EditPreviewToggleRefusalRevertsToBufferStateWithoutTouchingBuffer()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [x] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        var session = harness.ViewModel.EditorSession!;

        var reverts = new List<TaskToggleRevertRequest>();
        harness.ViewModel.EditPreviewTaskToggleRevertRequested += (_, r) => reverts.Add(r);

        // Stale key (external view) -> refusal; the optimistic DOM flip must be
        // reverted to the BUFFER's actual state.
        await harness.ViewModel.ToggleTaskLineAsync(0, false, "deadbeef", TaskToggleOrigin.EditPreview);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal("- [x] alpha\n", session.SourceText);
        var revert = Assert.Single(reverts);
        Assert.True(revert.Checked); // buffer really holds [x]
        Assert.Equal(path, revert.Path); // carries the document-identity guard
    }

    [Fact]
    public async Task ViewerOriginToggleWhileInEditModeStillRunsReadingLeg()
    {
        // Acceptance major #5: the leg follows the CLICKED surface, not the
        // mode at dispatch time. A viewer click racing past Ctrl+E still runs
        // the reading leg — flipping the buffer as an unsaved edit.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.IsEditMode);
        var session = harness.ViewModel.EditorSession!;

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        // P1: the reading (Viewer) leg flips the buffer as an UNSAVED edit; no
        // disk write, and the session becomes dirty until the user saves.
        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal("- [x] alpha\n", session.SourceText);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task EditPreviewOriginToggleAfterExitPublishesDirectedEditWithoutMutatingDormantBuffer()
    {
        // A late edit-preview click still validates against the dormant buffer,
        // but the source editor remains the only writer. The desktop bridge
        // receives this directed span only while the edit workspace exists.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null); // dormant
        Assert.False(harness.ViewModel.IsEditMode);
        var session = harness.ViewModel.EditorSession!;
        TaskToggleCommit? committed = null;
        harness.ViewModel.EditPreviewTaskToggleCommitted += (_, value) => committed = value;

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.EditPreview);

        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal("- [ ] alpha\n", session.SourceText);
        Assert.False(session.IsDirty);
        Assert.Equal("- [ ] alpha\n", harness.ViewModel.Document!.Content); // viewer snapshot untouched
        Assert.NotNull(committed);
        Assert.Equal("- [x] alpha\n", committed!.Source.Content);
        Assert.Equal(3, committed.Start);
        Assert.Equal(1, committed.Length);
        Assert.Equal("x", committed.Replacement);
    }

    [Fact]
    public async Task RequestDocumentSwitchWithDirtyEditorQueuesPromptAndCancelPreservesDraft()
    {
        // Audit Critical #1 gate: a dirty editor must NOT be overwritten by a
        // tab switch; Cancel keeps the draft and fires the revert callback.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "draft text";

        var switchRan = 0;
        var cancelRan = 0;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () =>
            {
                switchRan++;
                return Task.CompletedTask;
            },
            onCancel: () => cancelRan++);

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Equal(0, switchRan);
        Assert.Equal("draft text", harness.ViewModel.EditorSession!.SourceText);

        harness.ViewModel.CancelDirtyPromptCommand.Execute(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Equal(0, switchRan);
        Assert.Equal(1, cancelRan);
        Assert.Equal("draft text", harness.ViewModel.EditorSession!.SourceText);
    }

    [Fact]
    public async Task RequestDocumentSwitchDirtyDiscardRunsSwitchWithoutCancelCallback()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "draft text";

        var switchRan = 0;
        var cancelRan = 0;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () =>
            {
                switchRan++;
                return Task.CompletedTask;
            },
            onCancel: () => cancelRan++);
        Assert.True(harness.ViewModel.IsDirtyPromptOpen);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Equal(1, switchRan);
        Assert.Equal(0, cancelRan);
    }

    [Fact]
    public async Task RequestDocumentSwitchWithCleanEditorRunsImmediately()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        var switchRan = 0;
        var cancelRan = 0;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () =>
            {
                switchRan++;
                return Task.CompletedTask;
            },
            onCancel: () => cancelRan++);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.Equal(1, switchRan);
        Assert.Equal(0, cancelRan);
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
    public async Task OpeningDifferentDocumentSelectsFirstNewHeadingUntilRendererReportsActiveHeading()
    {
        var harness = CreateHarness();
        var firstPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "toc-first.md");
        var secondPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "toc-second.md");
        harness.Loader.Sources[firstPath] = CreateSource(firstPath, "# First");
        harness.Loader.Sources[secondPath] = CreateSource(secondPath, "# Second");

        await harness.ViewModel.OpenPathAsync(firstPath);
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("first", 1, "First", 0),
        ]);
        harness.ViewModel.ScrollToHeadingCommand.Execute("first");

        await harness.ViewModel.OpenPathAsync(secondPath);

        Assert.Single(harness.ViewModel.DocumentHeadings);
        Assert.Equal("first", harness.ViewModel.DocumentHeadings[0].Id);
        Assert.True(harness.ViewModel.HasDocumentHeadings);
        Assert.True(harness.ViewModel.IsTocVisible);
        Assert.Equal("first", harness.ViewModel.ActiveHeadingId);

        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("second", 1, "Second", 0),
        ]);

        Assert.Single(harness.ViewModel.DocumentHeadings);
        Assert.Equal("second", harness.ViewModel.DocumentHeadings[0].Id);
        Assert.True(harness.ViewModel.IsTocVisible);
        Assert.Equal("second", harness.ViewModel.ActiveHeadingId);

        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("second", 1, "Second", 0),
            new DocumentHeading("details", 2, "Details", 12),
        ]);
        harness.ViewModel.UpdateActiveHeadingFromRenderer("details");

        Assert.Equal("details", harness.ViewModel.ActiveHeadingId);
    }

    [Fact]
    public void UpdateDocumentHeadingsKeepsActiveHeadingWhenReplacementStillContainsIt()
    {
        var harness = CreateHarness();
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro", 0),
            new DocumentHeading("details", 2, "Details", 12),
        ]);
        harness.ViewModel.ScrollToHeadingCommand.Execute("details");

        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro updated", 0),
            new DocumentHeading("details", 2, "Details updated", 12),
        ]);

        Assert.Equal("details", harness.ViewModel.ActiveHeadingId);
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
    public async Task ClosingFromEditModeClearsIsEditModeBeforeNullingDocument()
    {
        // Pins the ordering that makes the close-promote tail dead code
        // (ApplicateMainWindow.cs:2657/:2674-2677). That tail snapshots
        // wasInEditMode and force-restores edit mode after an await, but
        // CloseFileCore sets IsEditMode=false BEFORE Document=null, so the
        // posted mirror lambda always reads IsEditMode==false and the restore
        // never fires. If a future edit reorders these two writes, the tail
        // would resurrect a superseded intent -- this test fails first.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "editclose.md");
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.IsEditMode);

        bool? isEditModeWhenDocumentCleared = null;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document)
                && harness.ViewModel.Document is null
                && isEditModeWhenDocumentCleared is null)
            {
                isEditModeWhenDocumentCleared = harness.ViewModel.IsEditMode;
            }
        };

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);

        Assert.False(isEditModeWhenDocumentCleared);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.Null(harness.ViewModel.Document);
    }

    [Fact]
    public async Task BulkCloseWhenActiveDirtyQueuesBehindPromptAndCancelClosesNothing()
    {
        // A0-bis: the removal must live INSIDE the gated closure so Cancel closes nothing. Without
        // the fix the four bulk closes removed the active dirty doc directly, before any prompt.
        var harness = CreateHarness();
        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "# unsaved bulk";

        var closed = false;
        await harness.ViewModel.RequestBulkCloseWithDirtyCheckAsync(() =>
        {
            closed = true;
            return Task.CompletedTask;
        });

        // Dirty active -> queued behind the prompt; the close closure has NOT run yet.
        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        Assert.False(closed);

        harness.ViewModel.CancelDirtyPromptCommand.Execute(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.False(closed); // Cancel closed nothing
    }

    [Fact]
    public async Task BulkCloseWhenActiveDirtyRunsTheCloseOnDiscard()
    {
        var harness = CreateHarness();
        await harness.ViewModel.CreateNewDocumentCommand.ExecuteAsync(null);
        harness.ViewModel.EditorSession!.SourceText = "# unsaved bulk";

        var closed = false;
        await harness.ViewModel.RequestBulkCloseWithDirtyCheckAsync(() =>
        {
            closed = true;
            return Task.CompletedTask;
        });
        Assert.False(closed);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.True(closed); // Discard proceeded with the bulk close
    }

    [Fact]
    public async Task BulkCloseWhenNotDirtyRunsImmediatelyWithNoPrompt()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "clean.md");
        harness.Loader.Sources[path] = CreateSource(path, "clean body");
        await harness.ViewModel.OpenPathAsync(path); // viewer, not dirty

        var closed = false;
        await harness.ViewModel.RequestBulkCloseWithDirtyCheckAsync(() =>
        {
            closed = true;
            return Task.CompletedTask;
        });

        Assert.False(harness.ViewModel.IsDirtyPromptOpen);
        Assert.True(closed); // clean -> ran immediately
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

    // G6 (design work-items/active/2026-07-25-toc-empty-on-open/design.md §6,
    // I2 "no collapse flash on viewer->viewer"): OnDocumentChanged clears the
    // TOC ONLY when Document becomes null. This half proves the clear DOES
    // fire on close (Document -> null); the non-null-switch half is already
    // pinned by OpeningDifferentDocumentSelectsFirstNewHeadingUntilRendererReportsActiveHeading
    // above, which asserts the PREVIOUS document's headings survive a
    // viewer->viewer OpenPathAsync untouched. This file is read-only for the
    // toc-empty-on-open fix (Must-NOT-touch list) -- this test is a
    // regression-safety net for the design's re-emit path, which routes back
    // through this exact clear/replace contract via UpdateDocumentHeadings.
    [Fact]
    public async Task ClosingTheActiveDocumentClearsDocumentHeadings()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "toc-close.md");
        harness.Loader.Sources[path] = CreateSource(path, "# Intro");

        await harness.ViewModel.OpenPathAsync(path);
        harness.ViewModel.UpdateDocumentHeadings([
            new DocumentHeading("intro", 1, "Intro", 0),
        ]);
        Assert.True(harness.ViewModel.HasDocumentHeadings);

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);

        Assert.Empty(harness.ViewModel.DocumentHeadings);
        Assert.False(harness.ViewModel.HasDocumentHeadings);
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
        Assert.Equal("Checking...", harness.ViewModel.CheckForUpdatesBusyLabel);
        Assert.Equal("Checking...", harness.ViewModel.CheckForUpdatesLabel);

        pendingCheck.SetResult(new UpdateCheckResult.SourceNotConfigured("No release source configured."));
        await checkTask;

        Assert.False(harness.ViewModel.IsUpdateBusy);
        Assert.Equal(0.0, harness.ViewModel.UpdateBusyIndicatorOpacity);
        Assert.Equal("Check now", harness.ViewModel.CheckForUpdatesLabel);
    }

    [Fact]
    public async Task CheckForUpdatesCommandRevealsResultImmediatelyWithoutBusyDwellTimer()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        var stopwatch = Stopwatch.StartNew();
        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        stopwatch.Stop();

        // A completed check reveals its ViewModel state at once — no control-flow dwell
        // withholds it. The removed MinUpdateBusyMilliseconds floor (600 ms) held these
        // back; re-introducing any such delay blows the budget below (mutation check).
        // This asserts the ABSENCE of a delay: pass ≈ 0 ms, the 600 ms mutation ≈ 600 ms,
        // so the 300 ms threshold is deterministic in both directions (no flaky window).
        Assert.False(harness.ViewModel.IsCheckingForUpdates);
        Assert.False(harness.ViewModel.IsUpdateBusy);
        Assert.Equal("Update 1.2.3 available", harness.ViewModel.UpdateStatusTitle);
        Assert.Equal("Available", harness.ViewModel.UpdateStateBadge);
        Assert.True(harness.ViewModel.IsHeaderUpdateNoticeVisible);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 300,
            $"Expected the update check to reveal its result without an artificial busy dwell, but the command took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task RecheckingAfterAvailableUpdateKeepsDownloadActionVisibleWhileBusy()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var pendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        harness.UpdateService.NextCheckTask = pendingCheck.Task;
        var checkTask = harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsCheckingForUpdates);
        Assert.False(harness.ViewModel.CanDownloadAvailableUpdate);
        Assert.Equal(1.0, harness.ViewModel.DownloadUpdateActionOpacity);
        Assert.Equal(0.0, harness.ViewModel.OpenDownloadedUpdateActionOpacity);

        pendingCheck.SetResult(new UpdateCheckResult.UpdateAvailable(package));
        await checkTask;

        Assert.False(harness.ViewModel.IsUpdateBusy);
        Assert.True(harness.ViewModel.CanDownloadAvailableUpdate);
        Assert.Equal(1.0, harness.ViewModel.DownloadUpdateActionOpacity);
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
    public async Task UpdateNotificationStaysOffDocumentSurfacesAndReturnsOnlyOnWelcome()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var package = CreateUpdatePackage();
        var changedProperties = new List<string?>();
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.ViewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsUpdateNotificationVisible);
        Assert.True(harness.ViewModel.CanShowTopLevelUpdateNotification);

        await harness.ViewModel.OpenPathAsync(path);

        Assert.True(harness.ViewModel.IsViewer);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.False(harness.ViewModel.CanShowTopLevelUpdateNotification);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);
        Assert.Contains(nameof(MainWindowViewModel.IsUpdateNotificationVisible), changedProperties);

        changedProperties.Clear();

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsEditMode);
        Assert.False(harness.ViewModel.ShowsAppMenuControl);
        Assert.False(harness.ViewModel.CanShowTopLevelUpdateNotification);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);

        changedProperties.Clear();

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsEditMode);
        Assert.True(harness.ViewModel.ShowsAppMenuControl);
        Assert.True(harness.ViewModel.IsViewer);
        Assert.False(harness.ViewModel.CanShowTopLevelUpdateNotification);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);

        await harness.ViewModel.CloseFileCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsWelcome);
        Assert.True(harness.ViewModel.CanShowTopLevelUpdateNotification);
        Assert.True(harness.ViewModel.IsUpdateNotificationVisible);
        Assert.Contains(nameof(MainWindowViewModel.IsUpdateNotificationVisible), changedProperties);
    }

    [Fact]
    public void HeaderUpdateNoticeHiddenByDefault()
    {
        var harness = CreateHarness();

        Assert.False(harness.ViewModel.IsHeaderUpdateNoticeVisible);
        Assert.Equal(string.Empty, harness.ViewModel.HeaderUpdateNoticeText);
    }

    [Fact]
    public async Task HeaderUpdateNoticeStaysVisibleWhileReadingUnlikeTopLevelBanner()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "header-notice.md");
        var package = CreateUpdatePackage();
        harness.Loader.Sources[path] = CreateSource(path, "alpha beta");
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsHeaderUpdateNoticeVisible);
        Assert.Equal("Update available!", harness.ViewModel.HeaderUpdateNoticeText);

        await harness.ViewModel.OpenPathAsync(path);

        // The welcome-screen-only top-level banner hides while reading...
        Assert.True(harness.ViewModel.IsViewer);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);
        // ...but the unobtrusive header notice stays visible with a document open.
        Assert.True(harness.ViewModel.IsHeaderUpdateNoticeVisible);
        Assert.Equal("Update available!", harness.ViewModel.HeaderUpdateNoticeText);
    }

    [Fact]
    public async Task InitializeAsyncReaderStartupCacheHitWaitsForPublishGateButNotRendererShell()
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", $"startup-{Guid.NewGuid():N}.md");
        var readiness = new ManualRendererReadinessService();
        var harness = CreateHarness(rendererReadiness: readiness);
        harness.CommandLine.ActivationPath = path;
        EarlyDocumentCache.Deposit(path, CreateSource(path, "# Startup\n\nbody"));

        var initializeTask = harness.ViewModel.InitializeAsync();

        Assert.False(initializeTask.IsCompleted);
        Assert.True(harness.ViewModel.IsOpeningPath(path));
        Assert.Equal(1, readiness.StartupDocumentPublishWaitCallCount);
        Assert.Equal(0, readiness.WaitCallCount);

        readiness.SetStartupDocumentPublishReady();
        await initializeTask;

        Assert.False(harness.ViewModel.IsOpeningPath(path));
        Assert.Equal(path, harness.ViewModel.Document?.Path);
        Assert.Equal(0, readiness.WaitCallCount);
    }

    [Fact]
    public async Task InitializeAsyncReaderStartupCacheHitDefersNativeRenderUntilViewReportsReadable()
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", $"startup-{Guid.NewGuid():N}.md");
        var readiness = new ManualRendererReadinessService();
        using var renderer = new BlockingMarkdownRenderer();
        var harness = CreateHarness(rendererReadiness: readiness, markdownRenderer: renderer);
        harness.CommandLine.ActivationPath = path;
        EarlyDocumentCache.Deposit(path, CreateSource(path, "# Startup\n\nbody"));

        var initializeTask = harness.ViewModel.InitializeAsync();

        Assert.False(initializeTask.IsCompleted);
        Assert.Equal(1, readiness.StartupDocumentPublishWaitCallCount);

        var releaseGateTask = Task.Run(readiness.SetStartupDocumentPublishReady);
        try
        {
            await releaseGateTask.WaitAsync(TimeSpan.FromSeconds(1));
            await initializeTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(path, harness.ViewModel.Document?.Path);
            Assert.Empty(harness.ViewModel.RenderedDocument.Blocks);
            await Task.Delay(100);
            Assert.Equal(0, renderer.RenderCallCount);

            harness.ViewModel.MarkReadableDocumentRendered();
            await renderer.RenderStarted.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            renderer.Release();
            await initializeTask.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, renderer.RenderCallCount);
    }

    [Fact]
    public async Task ReloadCacheHitInEditModeStillWaitsForRendererBeforePublishingDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", $"reload-{Guid.NewGuid():N}.md");
        var readiness = new ManualRendererReadinessService();
        var harness = CreateHarness(rendererReadiness: readiness);
        harness.Loader.Sources[path] = CreateSource(path, "# Initial\n\nbody");

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        EarlyDocumentCache.Deposit(path, CreateSource(path, "# Reloaded\n\nbody"));

        var reloadTask = harness.ViewModel.ReloadCommand.ExecuteAsync(null);

        Assert.False(reloadTask.IsCompleted);
        Assert.True(harness.ViewModel.IsOpeningPath(path));
        Assert.Equal(0, readiness.StartupDocumentPublishWaitCallCount);
        Assert.Equal(1, readiness.WaitCallCount);

        readiness.SetReady();
        await reloadTask;

        Assert.False(harness.ViewModel.IsOpeningPath(path));
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.Equal("# Reloaded\n\nbody", harness.ViewModel.Document?.Content);
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
    public async Task CheckForUpdatesCommandWhenSameReleaseAlreadyDownloadedKeepsNativeAction()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var downloadedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", package.AssetName);
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.UpdateService.NextDownloadResult = new UpdateDownloadResult.Success(package, downloadedPath);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await harness.ViewModel.DownloadUpdateCommand.ExecuteAsync(null);

        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(downloadedPath, harness.ViewModel.DownloadedUpdatePath);
        Assert.Equal("Update ready", harness.ViewModel.UpdateStatusTitle);
        Assert.False(harness.ViewModel.CanDownloadAvailableUpdate);
        Assert.True(harness.ViewModel.CanOpenDownloadedUpdate);
        Assert.Equal("Ready", harness.ViewModel.UpdateStateBadge);
    }

    [Fact]
    public async Task CheckForUpdatesCommandWhenCheckFailsKeepsAlreadyDownloadedPackage()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        var downloadedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", package.AssetName);
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        harness.UpdateService.NextDownloadResult = new UpdateDownloadResult.Success(package, downloadedPath);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        await harness.ViewModel.DownloadUpdateCommand.ExecuteAsync(null);

        // A later check FAILS (the periodic 5-min tick hitting a network blip). The completed
        // download must survive — symmetric with the same-release preservation above.
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.Failed("network unreachable");
        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(downloadedPath, harness.ViewModel.DownloadedUpdatePath);
        Assert.Equal("Update ready", harness.ViewModel.UpdateStatusTitle);
        Assert.True(harness.ViewModel.CanOpenDownloadedUpdate);
        Assert.Equal("Ready", harness.ViewModel.UpdateStateBadge);
    }

    [Fact]
    public async Task DismissedUpdateNotificationStaysDismissedForSameReleaseAndReturnsForNewer()
    {
        var harness = CreateHarness();
        var package = CreateUpdatePackage();
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);

        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.IsUpdateNotificationVisible);

        harness.ViewModel.DismissUpdateNotificationCommand.Execute(null);
        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);

        // The periodic re-check returns the SAME release: a dismissed banner must NOT resurrect.
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(package);
        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsUpdateNotificationVisible);

        // A genuinely NEWER release is a different dismissal subject and must show again.
        var newer = package with { ReleaseVersion = "2.0.0", ReleaseTag = "v2.0.0" };
        harness.UpdateService.NextCheckResult = new UpdateCheckResult.UpdateAvailable(newer);
        await harness.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsUpdateNotificationVisible);
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
    public async Task ResetSettingsCommandRestoresLiveAndPersistedDefaults()
    {
        var harness = CreateHarness();
        var names = new List<string?>();
        ThemeTransitionStartingEventArgs? transition = null;
        harness.ViewModel.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        harness.ViewModel.ThemeTransitionStarting += (_, e) => transition = e;

        harness.ViewModel.SelectedLanguageOption =
            harness.ViewModel.LanguageOptions.Single(option => option.Language == AppLanguage.Russian);
        harness.ViewModel.CycleThemeCommand.Execute(null);
        harness.ViewModel.IsAlwaysOnTop = true;
        harness.ViewModel.LineHeightSetting = 2.75;
        harness.ViewModel.IsOriginalPaletteSelected = true;
        harness.Settings.WindowPlacement = new WindowPlacement(120, 80, 900, 700, IsMaximized: true);

        await harness.ViewModel.ResetSettingsCommand.ExecuteAsync(null);

        Assert.Equal(ReadingPreferences.Default, harness.ViewModel.ReadingPreferences);
        Assert.Equal(ReadingPreferences.Default, harness.Settings.Preferences);
        Assert.Equal(AppLanguage.System, harness.ViewModel.SelectedLanguageOption?.Language);
        Assert.Equal(AppLanguage.System, harness.Settings.Language);
        Assert.Equal(ThemeMode.Light, harness.ViewModel.Theme);
        Assert.Equal(ThemeMode.Light, harness.Settings.Theme);
        Assert.Equal(ThemeMode.ClassicWhite, harness.ViewModel.EffectiveTheme);
        Assert.Equal(ThemeMode.ClassicWhite, transition?.TargetEffectiveTheme);
        Assert.False(harness.ViewModel.IsOriginalPaletteSelected);
        Assert.True(harness.ViewModel.IsWhitePaletteSelected);
        Assert.False(harness.ViewModel.IsAlwaysOnTop);
        Assert.True(harness.ViewModel.IsAlwaysOnTopDisabled);
        Assert.Null(harness.Settings.WindowPlacement);
        Assert.Contains(nameof(MainWindowViewModel.LineHeightSetting), names);
        Assert.Contains(nameof(MainWindowViewModel.SelectedLanguageOption), names);

        harness.ViewModel.CycleThemeCommand.Execute(null);

        Assert.Equal(ThemeMode.Dark, harness.ViewModel.Theme);
        Assert.Equal(ThemeMode.Dark, harness.Settings.Theme);
        Assert.Equal(ThemeMode.Dark, harness.ViewModel.EffectiveTheme);
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
    public void LineHeightSettingUsesOneToThreeRange()
    {
        var harness = CreateHarness();

        harness.ViewModel.LineHeightSetting = 0.25;

        Assert.Equal(ReadingPreferences.MinLineHeight, harness.ViewModel.ReadingPreferences.LineHeight);
        Assert.Equal(1.0, harness.ViewModel.LineHeightSetting);
        Assert.Equal("1.00", harness.ViewModel.LineHeightLabel);

        harness.ViewModel.LineHeightSetting = 3.4;

        Assert.Equal(ReadingPreferences.MaxLineHeight, harness.ViewModel.ReadingPreferences.LineHeight);
        Assert.Equal(3.0, harness.ViewModel.LineHeightSetting);
        Assert.Equal("3.00", harness.ViewModel.LineHeightLabel);
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

    [Fact]
    public void ThemeToggleRaisesTransitionBeforeEffectiveThemeMutates()
    {
        var harness = CreateHarness();
        ThemeTransitionStartingEventArgs? transition = null;
        harness.ViewModel.ThemeTransitionStarting += (_, e) =>
        {
            transition = e;
            Assert.Equal(ThemeMode.Light, harness.ViewModel.EffectiveTheme);
        };

        harness.ViewModel.CycleThemeCommand.Execute(null);

        Assert.NotNull(transition);
        Assert.Equal(ThemeMode.Dark, transition.TargetEffectiveTheme);
        Assert.Equal(ThemeMode.Dark, harness.ViewModel.EffectiveTheme);
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

    [Fact]
    public async Task ReadingModeDirtyHealthFixRepublishesRepairedTextWithoutSavingOrReloading()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "health.md");
        const string original = "Note $a =\nb$ tail\n- [ ] task\n";
        const string expected = "Note $a = b$ tail\n- [x] task\n";
        harness.Loader.Sources[path] = CreateSource(path, original);

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleTaskLineAsync(
            2,
            true,
            TaskListIdentity.ComputeKey("- [ ] task"),
            TaskToggleOrigin.Viewer);
        harness.ViewModel.AnalyzeCurrentDocumentHealth();
        Assert.True(harness.ViewModel.IsDirty);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.True(harness.ViewModel.IsDocumentHealthBannerVisible);

        await harness.ViewModel.ApplyDocumentHealthFixCommand.ExecuteAsync(null);

        Assert.Equal(expected, harness.ViewModel.EditorSession!.SourceText);
        Assert.Equal(expected, harness.ViewModel.Document!.Content);
        var paragraph = Assert.IsType<MarkdownParagraphBlock>(Assert.Single(harness.ViewModel.RenderedDocument.Blocks));
        var text = Assert.IsType<MarkdownTextInline>(Assert.Single(paragraph.Inlines));
        Assert.Equal(expected, text.Text);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal(1, harness.Loader.LoadCount);
        Assert.False(harness.ViewModel.IsDocumentHealthBannerVisible);
    }

    /// <summary>
    /// The health repair must reach the source editor on the CONTENT-EDIT channel,
    /// never the buffer-REPLACEMENT one. <see cref="EditorSessionViewModel.DocumentGeneration"/>
    /// is not a proxy for that: it is the exact discriminator
    /// <c>EditWorkspaceView.OnBoundSessionPropertyChanged</c> reads to choose between a
    /// surgical <c>Document.Replace</c> (undo stack survives) and a whole-<c>TextDocument</c>
    /// swap (undo stack discarded). Both writes raise the same <c>SourceText</c> event, so
    /// the counter is the only thing separating them.
    /// <para>
    /// This pins the ViewModel half. The editor half — generation unchanged therefore the
    /// user's earlier typing stays on the native undo stack — is pinned by
    /// <c>EditWorkspaceExternalWriteUndoTests.ExternalContentEditKeepsEarlierTypingOnTheUndoStack</c>,
    /// which lives in the Applicate suite because headless Avalonia is referenced only there.
    /// The two compose into "a health repair is undoable"; neither half proves it alone.
    /// </para>
    /// <para>
    /// Falsified by rewriting the repair's edit-mode branch to any buffer-replacing call
    /// (<c>ApplyLoadedDocument</c>, <c>DiscardChanges</c>) — the shape a future refactor is
    /// most likely to reach for, and the one that silently restores the destroyed-undo defect
    /// fixed in <c>07934af</c> while every other health-repair test keeps passing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EditModeHealthFixLandsOnTheContentEditChannelNotABufferReplacement()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "health-edit.md");
        // A hard-wrapped inline $…$ span — a repairable math defect.
        const string original = "Note $a =\nb$ tail\n";
        const string repaired = "Note $a = b$ tail\n";
        harness.Loader.Sources[path] = CreateSource(path, original);

        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        harness.ViewModel.AnalyzeCurrentDocumentHealth();
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.True(harness.ViewModel.IsDocumentHealthBannerVisible);
        var session = harness.ViewModel.EditorSession!;
        var generationBefore = session.DocumentGeneration;

        await harness.ViewModel.ApplyDocumentHealthFixCommand.ExecuteAsync(null);

        // The repair landed in the buffer...
        Assert.Equal(repaired, session.SourceText);
        // ...as an EDIT of the open document, not a replacement of it.
        Assert.Equal(generationBefore, session.DocumentGeneration);
        // Edit mode owns the buffer, so the repair must not touch disk or reload:
        // the user still owns the save, and a reload would fight the buffer push.
        Assert.Empty(harness.DocumentSaver.Saves);
        Assert.Equal(1, harness.Loader.LoadCount);
        Assert.True(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task DocumentHealthFixThatFailsToWriteKeepsBannerAndDoesNotClaimSuccess()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "health.md");
        // A hard-wrapped inline $…$ span — a repairable math defect.
        harness.Loader.Sources[path] = CreateSource(path, "Note $a =\nb$ tail\n");

        await harness.ViewModel.OpenPathAsync(path);
        harness.ViewModel.AnalyzeCurrentDocumentHealth();
        Assert.True(harness.ViewModel.IsDocumentHealthBannerVisible);

        // Backup succeeds; the main document write fails (disk full / permissions).
        harness.DocumentSaver.ThrowFor = savePath =>
            savePath.EndsWith(".bak", StringComparison.Ordinal)
                ? null
                : new IOException("disk full");

        await harness.ViewModel.ApplyDocumentHealthFixCommand.ExecuteAsync(null);

        // A failed write must NOT be reported as success: the banner stays up so
        // the user knows the document was not repaired, and the main file was
        // never written (only the ".bak" backup is recorded).
        Assert.True(harness.ViewModel.IsDocumentHealthBannerVisible);
        Assert.DoesNotContain(harness.DocumentSaver.Saves, save => save.Path == path);
    }

    [Fact]
    public async Task ReadingModeDirtyDiscardRevertsDocumentToPersistedBaseline()
    {
        // R2 (data-loss): a reading-mode toggle patches Document to the UNSAVED
        // content. Discarding must revert Document to the persisted baseline (the
        // Document->openDocs mirror then re-syncs the tab text + clears the dirty
        // marker); otherwise a tab switch-back would render the discarded edit as
        // truth.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal("- [x] alpha\n", harness.ViewModel.Document!.Content);

        // A tab switch routes through the widened dirty gate and queues the
        // (no-op) switch behind the prompt; Discard resolves it.
        var switched = false;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () => { switched = true; return Task.CompletedTask; },
            () => { });
        Assert.True(harness.ViewModel.IsDirtyPromptOpen);

        await harness.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        Assert.True(switched);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.Equal("- [ ] alpha\n", harness.ViewModel.Document!.Content); // reverted, not the discarded edit
        Assert.Equal("- [ ] alpha\n", harness.ViewModel.EditorSession!.SourceText);
    }

    [Fact]
    public async Task ReadingModeDirtyDocumentSwitchCancelPreservesTheDirtyBuffer()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        var switchRan = false;
        var cancelRan = false;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () => { switchRan = true; return Task.CompletedTask; },
            () => cancelRan = true);

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        harness.ViewModel.CancelDirtyPromptCommand.Execute(null);

        Assert.True(cancelRan);
        Assert.False(switchRan);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal("- [x] alpha\n", harness.ViewModel.EditorSession!.SourceText);
        Assert.Equal("- [x] alpha\n", harness.ViewModel.Document!.Content);
    }

    [Fact]
    public async Task ReadingModeDirtyDocumentSwitchSavePersistsThenRunsTheQueuedSwitch()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);

        var switchRan = false;
        await harness.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(
            () => { switchRan = true; return Task.CompletedTask; },
            () => { });

        Assert.True(harness.ViewModel.IsDirtyPromptOpen);
        await harness.ViewModel.ConfirmDirtySaveCommand.ExecuteAsync(null);

        var save = Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(path, save.Path);
        Assert.Equal("- [x] alpha\n", save.Content);
        Assert.True(switchRan);
        Assert.False(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task ReadingModeSaveSkipsDocumentRepublishToPreserveScroll()
    {
        // P2: a reading-mode Ctrl+S persists content the viewer ALREADY shows, so
        // ApplySavedDocument must NOT re-publish Document (reference identity ->
        // cold re-render -> scroll reset) nor re-run the whole-document
        // RenderedDocument parse; it only advances the baseline (clears dirty).
        // (The GUI scroll-preservation itself is a user-owned P3 runtime gate.)
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        Assert.True(harness.ViewModel.IsDirty);

        var documentBefore = harness.ViewModel.Document;
        var renderedBefore = harness.ViewModel.RenderedDocument;
        var documentChangesDuringSave = 0;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document))
            {
                documentChangesDuringSave++;
            }
        };

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Single(harness.DocumentSaver.Saves);
        Assert.Equal(0, documentChangesDuringSave);                      // no cold re-render
        Assert.Same(documentBefore, harness.ViewModel.Document);         // same Document reference
        Assert.Same(renderedBefore, harness.ViewModel.RenderedDocument); // no whole-doc re-parse
        Assert.False(harness.ViewModel.IsDirty);                         // baseline advanced, dot clears
    }

    [Fact]
    public async Task ReadingModeDirtyBulkCloseQueuesBehindPrompt()
    {
        // R4: the bulk-close dirty gate (RequestBulkCloseWithDirtyCheckAsync ->
        // RequiresDirtyResolution) is mode-independent after P1, so a lone
        // reading-mode checkbox edit (dirty, IsEditMode false) prompts before a
        // bulk close instead of silently dropping the unsaved edit.
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "tasks.md");
        harness.Loader.Sources[path] = CreateSource(path, "- [ ] alpha\n");
        await harness.ViewModel.OpenPathAsync(path);

        await harness.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.False(harness.ViewModel.IsEditMode);

        var closed = false;
        await harness.ViewModel.RequestBulkCloseWithDirtyCheckAsync(() => { closed = true; return Task.CompletedTask; });

        Assert.True(harness.ViewModel.IsDirtyPromptOpen); // widened gate prompts in reading mode
        Assert.False(closed);                              // close deferred behind the prompt
    }

    [Fact]
    public async Task ReadingModeTaskUndoRestoresDocumentSessionAndBaselineDirtyState()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-task.md");
        const string source = "- [ ] alpha\n";
        harness.Loader.Sources[path] = CreateSource(path, source);
        await harness.ViewModel.OpenPathAsync(path);
        InPlaceEditHistoryTransition? transition = null;
        var documentNotifications = 0;
        harness.ViewModel.InPlaceEditHistoryTransitioned += (_, value) => transition = value;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document))
            {
                documentNotifications++;
            }
        };

        await harness.ViewModel.ToggleTaskLineAsync(
            0,
            true,
            TaskListIdentity.ComputeKey("- [ ] alpha"),
            TaskToggleOrigin.Viewer);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.True(harness.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);

        var applied = Assert.IsType<InPlaceEditHistoryTransition>(transition);
        Assert.Equal(source, applied.Source.Content);
        Assert.Equal(RealtimeInDocumentEditDomPatchKind.TaskCheckbox, applied.DomPatch.Kind);
        Assert.False(applied.DomPatch.Checked);
        Assert.Equal(source, harness.ViewModel.Document!.Content);
        Assert.Equal(source, harness.ViewModel.EditorSession!.SourceText);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.Equal(0, documentNotifications);
        Assert.Empty(harness.DocumentSaver.Saves);
    }

    [Fact]
    public async Task ReadingModeTaskHistoryTraversesTwoEditsAndClearsRedoAfterNewEdit()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-two-tasks.md");
        const string source = "- [ ] alpha\n- [ ] beta\n";
        const string firstEdit = "- [x] alpha\n- [ ] beta\n";
        const string secondEdit = "- [x] alpha\n- [x] beta\n";
        const string replacementEdit = "- [ ] alpha\n- [x] beta\n";
        harness.Loader.Sources[path] = CreateSource(path, source);
        await harness.ViewModel.OpenPathAsync(path);

        await harness.ViewModel.ToggleTaskLineAsync(
            0,
            true,
            TaskListIdentity.ComputeKey("- [ ] alpha"),
            TaskToggleOrigin.Viewer);
        await harness.ViewModel.ToggleTaskLineAsync(
            1,
            true,
            TaskListIdentity.ComputeKey("- [ ] beta"),
            TaskToggleOrigin.Viewer);
        Assert.Equal(secondEdit, harness.ViewModel.Document!.Content);

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);
        Assert.Equal(firstEdit, harness.ViewModel.Document!.Content);
        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);
        Assert.Equal(source, harness.ViewModel.Document!.Content);
        harness.ViewModel.RedoRealtimeInDocumentEditCommand.Execute(null);
        Assert.Equal(firstEdit, harness.ViewModel.Document!.Content);

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);
        Assert.True(harness.ViewModel.RedoRealtimeInDocumentEditCommand.CanExecute(null));
        await harness.ViewModel.ToggleTaskLineAsync(
            1,
            true,
            TaskListIdentity.ComputeKey("- [ ] beta"),
            TaskToggleOrigin.Viewer);

        Assert.Equal(replacementEdit, harness.ViewModel.Document!.Content);
        Assert.False(harness.ViewModel.RedoRealtimeInDocumentEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReadingModeHistorySurvivesSaveAndUndoUsesTheNewPersistedBaseline()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-save.md");
        const string source = "- [ ] alpha\n";
        const string edited = "- [x] alpha\n";
        harness.Loader.Sources[path] = CreateSource(path, source);
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleTaskLineAsync(
            0,
            true,
            TaskListIdentity.ComputeKey("- [ ] alpha"),
            TaskToggleOrigin.Viewer);

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(edited, harness.ViewModel.EditorSession!.LastPersistedSource);
        Assert.True(harness.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));
        Assert.False(harness.ViewModel.IsDirty);

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);

        Assert.Equal(source, harness.ViewModel.Document!.Content);
        Assert.True(harness.ViewModel.IsDirty);

        harness.ViewModel.RedoRealtimeInDocumentEditCommand.Execute(null);

        Assert.Equal(edited, harness.ViewModel.Document!.Content);
        Assert.False(harness.ViewModel.IsDirty);
    }

    [Fact]
    public async Task EnteringEditModeClearsRealtimeHistoryAndEditModeCannotExecuteIt()
    {
        var harness = CreateHarness();
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-edit-mode.md");
        const string source = "- [ ] alpha\n";
        harness.Loader.Sources[path] = CreateSource(path, source);
        await harness.ViewModel.OpenPathAsync(path);
        await harness.ViewModel.ToggleTaskLineAsync(
            0,
            true,
            TaskListIdentity.ComputeKey("- [ ] alpha"),
            TaskToggleOrigin.Viewer);
        Assert.True(harness.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));
        var transitionCount = 0;
        harness.ViewModel.InPlaceEditHistoryTransitioned += (_, _) => transitionCount++;

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        Assert.True(harness.ViewModel.IsEditMode);
        Assert.False(harness.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);

        Assert.Equal(0, transitionCount);
        Assert.Equal("- [x] alpha\n", harness.ViewModel.EditorSession!.SourceText);
    }

    [Fact]
    public async Task ReplacementReloadDiscardAndCloseClearRealtimeHistory()
    {
        var replacement = CreateHarness();
        var replacementPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-replace.md");
        replacement.Loader.Sources[replacementPath] = CreateSource(replacementPath, "- [ ] alpha\n");
        await replacement.ViewModel.OpenPathAsync(replacementPath);
        await replacement.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        replacement.ViewModel.ApplyOpenedDocumentInPlace(new MarkdownSource(replacementPath, "replacement.md", "replacement"));
        Assert.Null(replacement.ViewModel.EditorSession);
        Assert.False(replacement.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));

        var reload = CreateHarness();
        var reloadPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-reload.md");
        reload.Loader.Sources[reloadPath] = CreateSource(reloadPath, "- [ ] alpha\n");
        await reload.ViewModel.OpenPathAsync(reloadPath);
        await reload.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        await reload.ViewModel.SaveCommand.ExecuteAsync(null);
        await reload.ViewModel.ReloadCommand.ExecuteAsync(null);
        Assert.Null(reload.ViewModel.EditorSession);
        Assert.False(reload.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));

        var discard = CreateHarness();
        var discardPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-discard.md");
        discard.Loader.Sources[discardPath] = CreateSource(discardPath, "- [ ] alpha\n");
        await discard.ViewModel.OpenPathAsync(discardPath);
        await discard.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        await discard.ViewModel.RequestDocumentSwitchWithDirtyCheckAsync(() => Task.CompletedTask, () => { });
        await discard.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);
        Assert.False(discard.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));

        var close = CreateHarness();
        var closePath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-close.md");
        close.Loader.Sources[closePath] = CreateSource(closePath, "- [ ] alpha\n");
        await close.ViewModel.OpenPathAsync(closePath);
        await close.ViewModel.ToggleTaskLineAsync(0, true, TaskListIdentity.ComputeKey("- [ ] alpha"), TaskToggleOrigin.Viewer);
        await close.ViewModel.CloseFileCommand.ExecuteAsync(null);
        await close.ViewModel.ConfirmDirtyDiscardCommand.ExecuteAsync(null);
        Assert.Null(close.ViewModel.EditorSession);
        Assert.False(close.ViewModel.UndoRealtimeInDocumentEditCommand.CanExecute(null));
    }

    private static TestHarness CreateHarness(
        IRendererReadinessService? rendererReadiness = null,
        IMarkdownDocumentRenderer? markdownRenderer = null)
    {
        var loader = new StubDocumentLoader();
        var saver = new RecordingDocumentSaver();
        var picker = new StubFilePicker();
        var commandLine = new StubCommandLineActivation();
        var settings = new InMemorySettingsStore();
        var localization = new LocalizationService(AppLanguage.English);
        var themeService = new RecordingThemeService();
        var startupMetrics = new RecordingStartupMetrics();
        var updateService = new StubUpdateService();
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(saver),
            picker,
            commandLine,
            localization,
            settings,
            themeService,
            startupMetrics,
            new RenderMarkdownDocumentUseCase(markdownRenderer ?? new TestMarkdownRenderer()),
            updateService,
            new MarkdigTableCellSourceEditor(),
            rendererReadiness: rendererReadiness);

        return new TestHarness(loader, saver, picker, commandLine, settings, startupMetrics, updateService, viewModel);
    }

    private sealed record TestHarness(
        StubDocumentLoader Loader,
        RecordingDocumentSaver DocumentSaver,
        StubFilePicker FilePicker,
        StubCommandLineActivation CommandLine,
        InMemorySettingsStore Settings,
        RecordingStartupMetrics StartupMetrics,
        StubUpdateService UpdateService,
        MainWindowViewModel ViewModel);
}
