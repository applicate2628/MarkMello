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
