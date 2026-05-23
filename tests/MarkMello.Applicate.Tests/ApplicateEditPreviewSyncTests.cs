using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateEditPreviewSyncTests
{
    [Fact]
    public void SyncToggleWiresToAvaloniaEditEditorScrollViewer()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateEditPreviewView.cs"));

        var ensureEditorWiring = ExtractMethodBody(codeBehind, "private void EnsureEditorWiring()");

        Assert.Contains("OfType<TextEditor>()", ensureEditorWiring, StringComparison.Ordinal);
        Assert.Contains("\"EditorTextEditor\"", ensureEditorWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("OfType<TextBox>()", ensureEditorWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EditorTextBox\"", ensureEditorWiring, StringComparison.Ordinal);
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
