using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

public sealed class EditorSessionViewModelTests
{
    [Fact]
    public void SourceTextChangeMarksSessionDirtyAndUpdatesPreview()
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var session = CreateSession(path, "alpha beta");

        Assert.False(session.IsDirty);
        Assert.Equal(2, session.WordCount);
        Assert.Equal("alpha beta", ExtractPlainText(session.RenderedPreview));
        Assert.Equal(Path.GetDirectoryName(path), session.RenderedPreview.BaseDirectory);

        session.SourceText = "alpha beta gamma";

        Assert.True(session.IsDirty);
        Assert.Equal(3, session.WordCount);
        Assert.Equal("alpha beta gamma", ExtractPlainText(session.RenderedPreview));
        Assert.Equal(Path.GetDirectoryName(path), session.RenderedPreview.BaseDirectory);
    }

    [Fact]
    public void DraftSessionStartsWithoutPathAndKeepsInitialContentClean()
    {
        var session = new EditorSessionViewModel(
            "Untitled.md",
            "alpha beta",
            ReadingPreferences.Default,
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            imageSourceResolver: null);

        Assert.Null(session.CurrentPath);
        Assert.Equal("Untitled.md", session.FileName);
        Assert.Equal("alpha beta", session.SourceText);
        Assert.Equal("alpha beta", session.LastPersistedSource);
        Assert.False(session.IsDirty);
        Assert.Null(session.RenderedPreview.BaseDirectory);
    }

    [Fact]
    public void ApplySavedDocumentResetsDirtyStateAndUpdatesIdentity()
    {
        var originalPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var savedPath = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "two.md");
        var session = CreateSession(originalPath, "alpha");
        session.SourceText = "alpha updated";

        // A real save persists the CURRENT buffer, so the applied snapshot equals
        // the live SourceText; the identity (path/name) moves to the saved target
        // and the buffer stays clean because it matches what was written.
        session.ApplySavedDocument(new MarkdownSource(savedPath, "two.md", "alpha updated"));

        Assert.False(session.IsDirty);
        Assert.Equal(savedPath, session.CurrentPath);
        Assert.Equal("two.md", session.FileName);
        Assert.Equal("alpha updated", session.SourceText);
        Assert.Equal("alpha updated", session.LastPersistedSource);
        Assert.Equal(Path.GetDirectoryName(savedPath), session.RenderedPreview.BaseDirectory);
    }

    [Fact]
    public void ApplySavedDocumentKeepsEditsTypedWhileTheAsyncSaveWasInFlight()
    {
        // SaveEditorAsync snapshots SourceText, then awaits the disk write; the
        // user can keep typing during that await, moving the buffer PAST the
        // snapshot. The save then completes and applies the SNAPSHOT — it must NOT
        // roll the buffer back to the snapshot and silently discard the keystrokes
        // typed while the save was in flight.
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md");
        var session = CreateSession(path, "alpha");
        session.SourceText = "alpha";                    // buffer == what the save snapshots
        // (save starts here, snapshotting "alpha")
        session.SourceText = "alpha typed-during-save";  // user keeps typing during the await

        session.ApplySavedDocument(new MarkdownSource(path, "one.md", "alpha")); // save completes with the SNAPSHOT

        Assert.Equal("alpha typed-during-save", session.SourceText); // edits preserved, not rolled back
        Assert.Equal("alpha", session.LastPersistedSource);          // disk holds the snapshot
        Assert.True(session.IsDirty);                                // buffer moved past disk -> unsaved edits
    }

    [Fact]
    public void DiscardChangesRevertsSourceAndClearsStatusMessage()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "one.md"), "alpha");
        session.SourceText = "beta";
        session.SetStatusMessage("Couldn't save the document.");

        session.DiscardChanges();

        Assert.False(session.IsDirty);
        Assert.Equal("alpha", session.SourceText);
        Assert.False(session.HasStatusMessage);
        Assert.Equal(string.Empty, session.StatusMessage);
    }

    [Fact]
    public void ApplyInPlaceEditToBufferMovesBufferDirtyWithoutBaselineOrPreviewRender()
    {
        // The single semantic flip: an in-place edit moves the buffer WITHOUT
        // advancing the persisted baseline, so it reads as unsaved (dirty) and
        // Discard reverts it (self-inverse — nothing was persisted). The preview
        // is deferred, so the whole-document parse never runs on this path.
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "table.md");
        const string original = "| A |\n|---|\n| old |\n";
        var session = CreateSession(path, original);
        var previewBefore = session.RenderedPreview;
        const string edited = "| A |\n|---|\n| new |\n";

        session.ApplyInPlaceEditToBuffer(edited);

        Assert.Equal(edited, session.SourceText);
        Assert.Equal(original, session.LastPersistedSource); // baseline UNCHANGED
        Assert.True(session.IsDirty);
        Assert.Same(previewBefore, session.RenderedPreview); // no preview rebuild

        session.DiscardChanges();

        Assert.Equal(original, session.SourceText);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void RealtimeUndoRestoresPreEditSourceAndBaselineDirtyState()
    {
        const string original = "| Done |\n| --- |\n| [ ] task |\n";
        const string edited = "| Done |\n| --- |\n| [x] task |\n";
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo.md"), original);

        session.ApplyRealtimeInDocumentEdit(
            edited,
            RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 3, beforeChecked: false, afterChecked: true));

        Assert.True(session.IsDirty);

        var transition = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, transition.Status);
        Assert.Equal(original, session.SourceText);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void RealtimeHistoryFollowsUndoRedoOrderAndDirectsThePatch()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-order.md"), "one");
        var firstPatch = RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 1, beforeChecked: false, afterChecked: true);
        var secondPatch = RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 2, beforeChecked: true, afterChecked: false);

        Assert.True(session.ApplyRealtimeInDocumentEdit("two", firstPatch));
        Assert.True(session.ApplyRealtimeInDocumentEdit("three", secondPatch));

        var undoSecond = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, undoSecond.Status);
        Assert.Equal("two", undoSecond.TargetSource);
        Assert.Equal(RealtimeInDocumentEditDomPatchKind.TaskCheckbox, undoSecond.DomPatch!.Kind);
        Assert.Equal(2, undoSecond.DomPatch.Line);
        Assert.True(undoSecond.DomPatch.Checked);
        Assert.Equal("two", session.SourceText);
        Assert.True(session.CanUndoRealtimeEdits);
        Assert.True(session.CanRedoRealtimeEdits);

        var undoFirst = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, undoFirst.Status);
        Assert.Equal("one", session.SourceText);
        Assert.False(session.CanUndoRealtimeEdits);
        Assert.True(session.CanRedoRealtimeEdits);

        var redoFirst = session.RedoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, redoFirst.Status);
        Assert.Equal("two", redoFirst.TargetSource);
        Assert.True(redoFirst.DomPatch!.Checked);
        Assert.Equal("two", session.SourceText);
    }

    [Fact]
    public void RealtimeHistoryDropsOldestEntryPastTwentyWithoutChangingRecentLifoOrder()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-cap.md"), "state-0");

        for (var state = 1; state <= 21; state++)
        {
            Assert.True(session.ApplyRealtimeInDocumentEdit(
                $"state-{state}",
                RealtimeInDocumentEditDomPatch.ForTaskCheckbox(
                    line: state,
                    beforeChecked: state % 2 == 0,
                    afterChecked: state % 2 != 0)));
        }

        for (var expectedState = 20; expectedState >= 1; expectedState--)
        {
            var transition = session.UndoRealtimeInDocumentEdit();

            Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, transition.Status);
            Assert.Equal($"state-{expectedState}", transition.TargetSource);
            Assert.Equal($"state-{expectedState}", session.SourceText);
        }

        Assert.False(session.CanUndoRealtimeEdits);

        var emptyUndo = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Empty, emptyUndo.Status);
        Assert.Equal("state-1", session.SourceText);

        for (var expectedState = 2; expectedState <= 21; expectedState++)
        {
            var transition = session.RedoRealtimeInDocumentEdit();

            Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, transition.Status);
            Assert.Equal($"state-{expectedState}", transition.TargetSource);
            Assert.Equal($"state-{expectedState}", session.SourceText);
        }

        Assert.False(session.CanRedoRealtimeEdits);
    }

    [Fact]
    public void NewRealtimeEditAfterUndoClearsRedoButEqualSourceSettlementDoesNot()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-redo.md"), "one");
        var firstPatch = RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 1, beforeChecked: false, afterChecked: true);

        Assert.True(session.ApplyRealtimeInDocumentEdit("two", firstPatch));
        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Applied, session.UndoRealtimeInDocumentEdit().Status);
        Assert.True(session.CanRedoRealtimeEdits);

        Assert.False(session.ApplyRealtimeInDocumentEdit("one", firstPatch));
        Assert.True(session.CanRedoRealtimeEdits);

        Assert.True(session.ApplyRealtimeInDocumentEdit(
            "three",
            RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 2, beforeChecked: false, afterChecked: true)));
        Assert.False(session.CanRedoRealtimeEdits);
        Assert.Equal("three", session.SourceText);
    }

    [Fact]
    public void RealtimeHistoryFailsClosedWhenSourceNoLongerMatchesThePendingTransition()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-invalidated.md"), "one");
        Assert.True(session.ApplyRealtimeInDocumentEdit(
            "two",
            RealtimeInDocumentEditDomPatch.ForTaskCheckbox(line: 1, beforeChecked: false, afterChecked: true)));

        session.SourceText = "outside-history";

        var transition = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Invalidated, transition.Status);
        Assert.Equal("outside-history", session.SourceText);
        Assert.False(session.CanUndoRealtimeEdits);
        Assert.False(session.CanRedoRealtimeEdits);
    }

    [Fact]
    public void EmptyRealtimeHistoryReturnsEmptyWithoutChangingSource()
    {
        var session = CreateSession(Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "undo-empty.md"), "one");

        var transition = session.UndoRealtimeInDocumentEdit();

        Assert.Equal(RealtimeInDocumentEditHistoryTransitionStatus.Empty, transition.Status);
        Assert.Equal("one", session.SourceText);
        Assert.False(session.CanUndoRealtimeEdits);
        Assert.False(session.CanRedoRealtimeEdits);
    }

    [Fact]
    public void CreatePreviewDeferredStartsWithEmptyPreviewThenReconcilesOnDemand()
    {
        // A reading-mode in-place edit lazily materializes the session
        // preview-deferred so the click path never pays a whole-document parse;
        // EnsurePreviewReconciled (called on the next Ctrl+E) rebuilds it.
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "deferred.md");
        var session = EditorSessionViewModel.CreatePreviewDeferred(
            new MarkdownSource(path, Path.GetFileName(path), "alpha beta"),
            ReadingPreferences.Default,
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            imageSourceResolver: null);

        Assert.Equal("alpha beta", session.SourceText);
        Assert.False(session.IsDirty);
        Assert.Empty(session.RenderedPreview.Blocks); // preview deferred (Empty)

        session.EnsurePreviewReconciled();

        Assert.Equal("alpha beta", ExtractPlainText(session.RenderedPreview));
        Assert.Equal(Path.GetDirectoryName(path), session.RenderedPreview.BaseDirectory);
    }

    private static EditorSessionViewModel CreateSession(string path, string content)
        => new(
            new MarkdownSource(path, Path.GetFileName(path), content),
            ReadingPreferences.Default,
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            imageSourceResolver: null);

    private static string ExtractPlainText(RenderedMarkdownDocument document)
    {
        var paragraph = Assert.IsType<MarkdownParagraphBlock>(Assert.Single(document.Blocks));
        var text = Assert.IsType<MarkdownTextInline>(Assert.Single(paragraph.Inlines));
        return text.Text;
    }
}
