using MarkMello.Presentation.Editing;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Payload rules for a file dropped onto the source editor. These lived as
/// private members of a desktop view wired to a control that had been renamed
/// away, so they were both unreachable at runtime and untestable.
/// </summary>
public sealed class EditorDropInsertTests
{
    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.MARKDOWN")]
    [InlineData("notes.txt")]
    [InlineData("shot.png")]
    [InlineData("shot.JPEG")]
    [InlineData("shot.svg")]
    public void InsertableExtensionsAreAccepted(string path)
        => Assert.True(EditorDropInsert.IsInsertableFile(path));

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("report.pdf")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherFilesAreIgnored(string? path)
        => Assert.False(EditorDropInsert.IsInsertableFile(path));

    [Fact]
    public async Task MarkdownDropBecomesALinkRelativeToTheHostDocument()
    {
        var root = CreateTempDirectory();
        try
        {
            var host = Path.Combine(root, "docs", "host.md");
            Directory.CreateDirectory(Path.GetDirectoryName(host)!);
            var dropped = Path.Combine(root, "docs", "chapter.md");

            var text = await EditorDropInsert.BuildInsertTextAsync(dropped, host);

            Assert.Equal("[chapter](chapter.md)", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImageDropIsCopiedNextToTheDocumentAndReferencedRelatively()
    {
        var root = CreateTempDirectory();
        try
        {
            var host = Path.Combine(root, "host.md");
            await File.WriteAllTextAsync(host, "body");
            var source = Path.Combine(root, "source", "diagram.png");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, [1, 2, 3]);

            var text = await EditorDropInsert.BuildInsertTextAsync(source, host);

            Assert.Equal("![diagram](images/diagram.png)", text);
            var copied = Path.Combine(root, "images", "diagram.png");
            Assert.True(File.Exists(copied), "the dropped image should be copied beside the document");
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(copied));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ADifferentImageWithTheSameNameDoesNotOverwriteTheExistingOne()
    {
        var root = CreateTempDirectory();
        try
        {
            var host = Path.Combine(root, "host.md");
            await File.WriteAllTextAsync(host, "body");
            var existing = Path.Combine(root, "images", "diagram.png");
            Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
            await File.WriteAllBytesAsync(existing, [9, 9, 9]);

            var source = Path.Combine(root, "source", "diagram.png");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, [1, 2, 3]);

            var text = await EditorDropInsert.BuildInsertTextAsync(source, host);

            Assert.Equal("![diagram](images/diagram-1.png)", text);
            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(existing));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReDroppingTheSameImageReusesTheExistingCopy()
    {
        var root = CreateTempDirectory();
        try
        {
            var host = Path.Combine(root, "host.md");
            await File.WriteAllTextAsync(host, "body");
            var source = Path.Combine(root, "source", "diagram.png");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllBytesAsync(source, [1, 2, 3]);

            await EditorDropInsert.BuildInsertTextAsync(source, host);
            var second = await EditorDropInsert.BuildInsertTextAsync(source, host);

            Assert.Equal("![diagram](images/diagram.png)", second);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "images")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnImageDroppedOnAnUnsavedDocumentIsInlinedAsADataUri()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "diagram.png");
            await File.WriteAllBytesAsync(source, [1, 2, 3]);

            var text = await EditorDropInsert.BuildInsertTextAsync(source, currentDocumentPath: null);

            Assert.Equal("![diagram](data:image/png;base64,AQID)", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("plain.md", "plain.md")]
    [InlineData("with space.md", "<with space.md>")]
    [InlineData("paren(1).md", "<paren(1).md>")]
    public void TargetsThatWouldTerminateTheLinkEarlyAreBracketed(string target, string expected)
        => Assert.Equal(expected, EditorDropInsert.EncodeMarkdownLinkTarget(target));

    [Theory]
    // Caret mid-line: padded on both sides so the insert owns its own line.
    [InlineData("abcdef", 3, "\nX\n")]
    // Caret already at a line boundary: no padding accumulates.
    [InlineData("abc\ndef", 4, "X\n")]
    // Caret at end of text: treated as end-of-line on the trailing side.
    [InlineData("abc", 3, "\nX")]
    [InlineData("", 0, "X")]
    public void CaretInsertIsPaddedOntoItsOwnLine(string current, int caret, string expected)
        => Assert.Equal(expected, EditorDropInsert.BuildCaretInsertText(current, caret, "X"));

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(99, 5)]
    [InlineData(2, 2)]
    public void AnOutOfRangeCaretAppendsAtTheEnd(int caret, int expected)
        => Assert.Equal(expected, EditorDropInsert.ClampCaret("alpha", caret));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Presentation.Tests.DropInsert",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
