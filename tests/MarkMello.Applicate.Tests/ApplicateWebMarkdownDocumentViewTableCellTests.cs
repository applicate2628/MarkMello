using System.Reflection;
using System.Text.Json;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewTableCellTests
{
    private static readonly string[] SuccessKeys = ["cellIndex", "key", "line", "ok", "text", "type"];
    private static readonly string[] FailureKeys = ["cellIndex", "line", "ok", "type"];
    private static readonly string[] BusyFailureKeys = ["cellIndex", "line", "ok", "reason", "type"];

    [Fact]
    public void TableCellEditIngressRaisesExactEventArgsAndRejectsMalformedPayloads()
    {
        RunOnView(view =>
        {
            var received = new List<ApplicateWebTableCellEditEventArgs>();
            view.TableCellEditRequested += (_, value) => received.Add(value);

            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"text":"plain text","key":"abc123"}""");
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"key":"missing-text"}""");
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":"12","cellIndex":3,"text":"wrong-line-kind","key":null}""");

            var edit = Assert.Single(received);
            Assert.Equal(12, edit.Line);
            Assert.Equal(3, edit.CellIndex);
            Assert.Equal("plain text", edit.Text);
            Assert.Equal("abc123", edit.Key);
        });
    }

    [Fact]
    public void RealHostBuildersSerializeCanonicalSuccessAndExactFailureOmission()
    {
        var success = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedSuccessMessage(
            line: 12,
            cellIndex: 3,
            text: "canonical",
            key: "new-key"));
        Assert.Equal(
            SuccessKeys,
            success.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal("table-cell-updated", success.GetProperty("type").GetString());
        Assert.True(success.GetProperty("ok").GetBoolean());
        Assert.Equal("canonical", success.GetProperty("text").GetString());
        Assert.Equal("new-key", success.GetProperty("key").GetString());

        var failure = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedFailureMessage(
            line: 12,
            cellIndex: 3));
        Assert.Equal(
            FailureKeys,
            failure.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal("table-cell-updated", failure.GetProperty("type").GetString());
        Assert.False(failure.GetProperty("ok").GetBoolean());
        Assert.False(failure.TryGetProperty("text", out _));
        Assert.False(failure.TryGetProperty("key", out _));
        Assert.False(failure.TryGetProperty("reason", out _));

        var busyFailure = Serialize(ApplicateWebMarkdownDocumentView.BuildTableCellUpdatedFailureMessage(
            line: 12,
            cellIndex: 3,
            busy: true));
        Assert.Equal(
            BusyFailureKeys,
            busyFailure.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.False(busyFailure.GetProperty("ok").GetBoolean());
        Assert.Equal("busy", busyFailure.GetProperty("reason").GetString());
        Assert.False(busyFailure.TryGetProperty("text", out _));
        Assert.False(busyFailure.TryGetProperty("key", out _));
    }

    [Fact]
    public void StaleCrossDocumentEditIsRejectedBeforeTheWriteWhenRenderGenerationDoesNotMatch()
    {
        RunOnView(view =>
        {
            var received = new List<ApplicateWebTableCellEditEventArgs>();
            view.TableCellEditRequested += (_, value) => received.Add(value);

            // A fresh view's active reveal render id is 0. This edit was STAMPED
            // with a different render generation (999) — it was posted while an
            // earlier document was displayed. Its (line, index, key) can collide
            // with an unrelated cell in the now-active document, so it must be
            // dropped before the write path ever runs: no event, hence no write.
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"text":"stale write into B","key":"abc123","renderId":999}""");
            Assert.Empty(received);

            // A matching render generation (0), a currency-free null, and an
            // absent renderId all reach the write path.
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"text":"matching","key":"abc123","renderId":0}""");
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"text":"null-currency","key":"abc123","renderId":null}""");
            view.HandleWebMessageBody(
                """{"type":"table-cell-edit","line":12,"cellIndex":3,"text":"absent-currency","key":"abc123"}""");

            Assert.Equal(3, received.Count);
            Assert.Equal("matching", received[0].Text);
            Assert.Equal("null-currency", received[1].Text);
            Assert.Equal("absent-currency", received[2].Text);
        });
    }

    [Fact]
    public void TableCellHelpersGuardDocumentIdentityBeforePosting()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MarkMello.Applicate.Desktop", "Views", "ApplicateWebMarkdownDocumentView.cs"));
        var success = ExtractMethodBody(
            source,
            "internal void SetTableCellText(int line, int cellIndex, string text, string key, string expectedPath)");
        var failure = ExtractMethodBody(
            source,
            "internal void RejectTableCellEdit(int line, int cellIndex, string expectedPath, bool busy = false)");

        AssertIdentityGuardPrecedesPost(success, "BuildTableCellUpdatedSuccessMessage");
        AssertIdentityGuardPrecedesPost(failure, "BuildTableCellUpdatedFailureMessage");
        Assert.Contains(
            "internal void SetTableCellText(int line, int cellIndex, string text, string key)",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertIdentityGuardPrecedesPost(string method, string builder)
    {
        var loadedGuard = method.IndexOf("!_hasLoadedDocument", StringComparison.Ordinal);
        var pathGuard = method.IndexOf("Source?.Path", StringComparison.Ordinal);
        var post = method.IndexOf(builder, StringComparison.Ordinal);
        Assert.True(loadedGuard >= 0, "The helper must refuse an unloaded host.");
        Assert.True(pathGuard > loadedGuard, "The current Source path must participate in the guard.");
        Assert.True(post > pathGuard, "No acknowledgement may post before document identity is verified.");
    }

    private static JsonElement Serialize(object message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message));
        return document.RootElement.Clone();
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
            => throw new NotSupportedException("Renderer is not exercised by table-cell bridge tests.");

        public Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Renderer is not exercised by table-cell bridge tests.");
    }
}
