using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewTaskToggleTests
{
    [Fact]
    public void StaleCrossDocumentTaskToggleIsRejectedBeforeTheWriteWhenRenderGenerationDoesNotMatch()
    {
        RunOnView(view =>
        {
            var received = new List<ApplicateWebTaskToggleEventArgs>();
            view.TaskToggleRequested += (_, value) => received.Add(value);

            // A fresh view's active reveal render id is 0. A delayed checkbox
            // message stamped by an earlier document must not enter the VM write
            // path, where its line/key could collide with the active document.
            view.HandleWebMessageBody(
                """{"type":"task-toggle","line":12,"checked":true,"key":"abc123","renderId":999}""");
            Assert.Empty(received);

            // Matching numeric currency and legacy null/absent messages remain
            // compatible and reach the existing task-toggle write path.
            view.HandleWebMessageBody(
                """{"type":"task-toggle","line":12,"checked":true,"key":"abc123","renderId":0}""");
            view.HandleWebMessageBody(
                """{"type":"task-toggle","line":12,"checked":false,"key":"abc123","renderId":null}""");
            view.HandleWebMessageBody(
                """{"type":"task-toggle","line":12,"checked":true,"key":"abc123"}""");

            Assert.Equal(3, received.Count);
            Assert.True(received[0].Checked);
            Assert.False(received[1].Checked);
            Assert.True(received[2].Checked);
        });
    }

    private static void RunOnView(Action<ApplicateWebMarkdownDocumentView> body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() => body(new ApplicateWebMarkdownDocumentView(new NoopRenderer())), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private sealed class NoopRenderer : IApplicateHtmlMarkdownRenderer
    {
        public Task<ApplicateHtmlDocument> RenderAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Renderer is not exercised by task-toggle bridge tests.");

        public Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Renderer is not exercised by task-toggle bridge tests.");

        public Task<string> RenderTableCellHtmlAsync(
            string rawCellMarkdown,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Table-cell fragment rendering is not exercised by these tests.");
    }
}
