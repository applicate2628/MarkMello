using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Presentation.Editing;
using MarkMello.Presentation.ViewModels;
using MarkMello.Presentation.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Guards the source-editor file-drop insert.
///
/// The feature was dead from f1d18a9 (2026-05-21) until this suite existed: the
/// drop wiring lived in the edit-PREVIEW view and searched the whole visual tree
/// for a <c>TextBox</c> named "EditorTextBox", while the pane had become a
/// <c>TextEditor</c> named "EditorTextEditor". The lookup missed on both name and
/// type, returned null, and silently disabled the feature with nothing failing.
/// </summary>
public sealed class EditWorkspaceDropInsertTests
{
    [Fact]
    public async Task DropWiringLandsOnTheNamedSourceEditor()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var workspace = new EditWorkspaceView();
            var window = new Window { Content = workspace };

            try
            {
                window.Show();
                workspace.DataContext = CreateSession("alpha");

                // The anchor itself: a rename here is what broke the feature before.
                var editor = Assert.IsType<TextEditor>(workspace.FindControl<TextEditor>("EditorTextEditor"));

                // The host Window in this test does not set DragDrop.AllowDrop, and
                // the property's inherited default is false — so a true here can only
                // come from the view's own wiring having resolved this editor.
                Assert.True(
                    DragDrop.GetAllowDrop(editor),
                    "The source editor is not wired as a drop target: EditWorkspaceView "
                    + "did not resolve the control named 'EditorTextEditor'. Dropping a "
                    + "file on the editor will silently do nothing.");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DropInsertIsUndoableAndReachesTheSession()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var session = CreateSession("alpha");
            var workspace = new EditWorkspaceView();
            var window = new Window { Content = workspace };

            try
            {
                window.Show();
                workspace.DataContext = session;
                var editor = Assert.IsType<TextEditor>(workspace.FindControl<TextEditor>("EditorTextEditor"));

                // Exactly the composition InsertAtEditorCaret performs on drop.
                var snippet = "![diagram](images/diagram.png)";
                editor.CaretOffset = "alpha".Length;
                var caret = EditorDropInsert.ClampCaret(editor.Document.Text, editor.CaretOffset);
                var finalText = EditorDropInsert.BuildCaretInsertText(editor.Document.Text, caret, snippet);

                Assert.True(workspace.ApplyEditModeSourceEdit(caret, 0, finalText));

                Assert.Equal("alpha\n" + snippet, editor.Document.Text);
                // Single-writer: the editor's own change event mirrors into the session.
                Assert.Equal("alpha\n" + snippet, session.SourceText);

                // The insert sits on the editor's native undo stack, so the user can
                // take it back. Assigning session.SourceText instead would round-trip
                // through the whole-Document swap and discard the undo history.
                Assert.True(editor.CanUndo);
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

    private static EditorSessionViewModel CreateSession(string text)
        => new(
            "edit.md",
            text,
            ReadingPreferences.Default,
            new RenderMarkdownDocumentUseCase(new PlainTextRenderer()),
            imageSourceResolver: null);

    private sealed class PlainTextRenderer : IMarkdownDocumentRenderer
    {
        public RenderedMarkdownDocument Render(string markdown)
            => RenderedMarkdownDocument.PlainText(markdown);
    }
}
