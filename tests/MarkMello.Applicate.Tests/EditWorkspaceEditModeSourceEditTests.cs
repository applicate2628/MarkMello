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

public sealed class EditWorkspaceEditModeSourceEditTests
{
    [Fact]
    public async Task PreviewEditSharesTheNativeUndoStackWithTypedTextAndWritesBackToTheSession()
    {
        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var session = new EditorSessionViewModel(
                "edit.md",
                "alpha",
                ReadingPreferences.Default,
                new RenderMarkdownDocumentUseCase(new PlainTextRenderer()),
                imageSourceResolver: null);
            var workspace = new EditWorkspaceView();
            var window = new Window { Content = workspace };

            try
            {
                window.Show();
                workspace.DataContext = session;
                var editor = Assert.IsType<TextEditor>(workspace.FindControl<TextEditor>("EditorTextEditor"));

                editor.Document.Insert(editor.Document.TextLength, " typed");
                Assert.Equal("alpha typed", session.SourceText);
                Assert.True(session.IsDirty);

                workspace.ApplyEditModeSourceEdit(start: 0, length: "alpha".Length, replacement: "beta");

                Assert.Equal("beta typed", editor.Document.Text);
                Assert.Equal("beta typed", session.SourceText);
                Assert.True(session.IsDirty);

                editor.Undo();
                Assert.Equal("alpha typed", editor.Document.Text);
                Assert.Equal("alpha typed", session.SourceText);

                editor.Undo();
                Assert.Equal("alpha", editor.Document.Text);
                Assert.Equal("alpha", session.SourceText);
                Assert.False(session.IsDirty);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed class PlainTextRenderer : IMarkdownDocumentRenderer
    {
        public RenderedMarkdownDocument Render(string markdown)
            => RenderedMarkdownDocument.PlainText(markdown);
    }
}
