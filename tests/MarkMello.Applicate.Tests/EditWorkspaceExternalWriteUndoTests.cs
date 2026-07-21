using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// The two halves of the external-write contract on the source editor.
/// <para>
/// An external CONTENT EDIT of the already-open document (health repair, a
/// reading-mode table-cell commit, a realtime in-document undo) must leave the
/// user's earlier typing on the editor's native undo stack. A genuine DOCUMENT
/// SWAP (initial bind, edit-mode tab switch, reload-from-disk, discard) must
/// still reset it — otherwise Ctrl+Z walks the user backwards into a previous
/// document's content, which is worse than losing the history.
/// </para>
/// <para>
/// Both directions are pinned here because the fix for the first is exactly the
/// change that can over-reach into the second.
/// </para>
/// </summary>
public sealed class EditWorkspaceExternalWriteUndoTests
{
    [Fact]
    public async Task ExternalContentEditKeepsEarlierTypingOnTheUndoStack()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var (workspace, window, session, editor) = CreateBoundWorkspace("alpha");

            try
            {
                editor.Document.Insert(editor.Document.TextLength, " typed by user");
                Assert.Equal("alpha typed by user", session.SourceText);
                Assert.True(editor.CanUndo);

                // The health-repair writer's shape: a multi-char whole-buffer
                // assignment through the SourceText setter on the SAME session.
                session.SourceText = "alpha typed by user REPAIRED";

                Assert.Equal("alpha typed by user REPAIRED", editor.Document.Text);
                Assert.True(editor.CanUndo);

                editor.Undo();
                Assert.Equal("alpha typed by user", editor.Document.Text);
                Assert.Equal("alpha typed by user", session.SourceText);

                // ...and the user's OWN earlier keystroke is still reachable
                // underneath it: the external write must not have truncated the
                // stack, only sat on top of it.
                editor.Undo();
                Assert.Equal("alpha", editor.Document.Text);
                Assert.Equal("alpha", session.SourceText);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExternalContentEditPreservesTheCaret()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var (workspace, window, session, editor) = CreateBoundWorkspace("alpha bravo charlie");

            try
            {
                // Caret parked inside "bravo", well before the edited region.
                editor.CaretOffset = 8;

                session.SourceText = "alpha bravo charlie REPAIRED";

                Assert.Equal("alpha bravo charlie REPAIRED", editor.Document.Text);
                // A whole-Document swap resets the caret to 0; a minimal Replace
                // outside the caret's span leaves it exactly where the user left it.
                Assert.Equal(8, editor.CaretOffset);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DocumentSwapStillResetsTheUndoStack()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var (workspace, window, session, editor) = CreateBoundWorkspace("alpha");

            try
            {
                editor.Document.Insert(editor.Document.TextLength, " typed by user");
                Assert.True(editor.CanUndo);

                // An edit-mode tab switch and a reload-from-disk BOTH land here:
                // MainWindowViewModel.ApplyLoadedDocument mutates the bound
                // session in place (MainWindowViewModel.cs:2127), so the view's
                // DataContext never changes and this arrives on the same
                // PropertyChanged channel a content edit uses.
                session.ApplyLoadedDocument(
                    new MarkdownSource("other.md", "other.md", "a completely different document"));

                Assert.Equal("a completely different document", editor.Document.Text);

                // The decisive assertion: undo must NOT be able to walk back into
                // the previous document's text.
                Assert.False(editor.CanUndo);
                editor.Undo();
                Assert.Equal("a completely different document", editor.Document.Text);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DiscardStillResetsTheUndoStack()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var (workspace, window, session, editor) = CreateBoundWorkspace("alpha");

            try
            {
                editor.Document.Insert(editor.Document.TextLength, " typed by user");
                Assert.True(session.IsDirty);
                Assert.True(editor.CanUndo);

                // Discard is a deliberate "throw the buffer away", not a content
                // edit: re-offering the discarded text through Ctrl+Z would defeat
                // the command the user just confirmed.
                session.DiscardChanges();

                Assert.Equal("alpha", editor.Document.Text);
                Assert.False(session.IsDirty);
                Assert.False(editor.CanUndo);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static (EditWorkspaceView Workspace, Window Window, EditorSessionViewModel Session, TextEditor Editor)
        CreateBoundWorkspace(string initialContent)
    {
        var session = new EditorSessionViewModel(
            "edit.md",
            initialContent,
            ReadingPreferences.Default,
            new RenderMarkdownDocumentUseCase(new PlainTextRenderer()),
            imageSourceResolver: null);
        var workspace = new EditWorkspaceView();
        var window = new Window { Content = workspace };
        window.Show();
        workspace.DataContext = session;
        var editor = Assert.IsType<TextEditor>(workspace.FindControl<TextEditor>("EditorTextEditor"));
        return (workspace, window, session, editor);
    }

    private sealed class PlainTextRenderer : IMarkdownDocumentRenderer
    {
        public RenderedMarkdownDocument Render(string markdown)
            => RenderedMarkdownDocument.PlainText(markdown);
    }
}
