using MarkMello.Domain;

namespace MarkMello.Domain.Tests;

public sealed class TableCellRewriteTests
{
    [Theory]
    [InlineData("alpha", " alpha ")]
    [InlineData("  alpha   beta  ", " alpha beta ")]
    [InlineData("alpha\u00a0beta\r\ngamma\ndelta\repsilon", " alpha beta gamma delta epsilon ")]
    [InlineData("a\u0000b\u0001c\u007fd", " abcd ")]
    [InlineData("", "  ")]
    [InlineData("   ", "  ")]
    [InlineData("a\tb", " a b ")]
    [InlineData("col1\tcol2\tcol3", " col1 col2 col3 ")]
    [InlineData("a\u000Bb\u000Cc", " a b c ")]
    public void EscapeCellContentNormalizesContentEditableArtifacts(string content, string expected)
    {
        Assert.Equal(expected, TableCellRewrite.EscapeCellContent(content));
    }

    [Fact]
    public void EscapeCellContentEscapesBackslashesBeforePipesAndPadsOnce()
    {
        const string content = "a\\b|c\\";

        var escaped = TableCellRewrite.EscapeCellContent(content);

        Assert.Equal(" a\\\\b\\|c\\\\ ", escaped);
    }

    [Fact]
    public void EscapeCellContentKeepsLiteralBreakMarkupAndEntities()
    {
        var escaped = TableCellRewrite.EscapeCellContent("left\n<br>\r\n&amp;\r right");

        Assert.Equal(" left <br> &amp; right ", escaped);
        Assert.Equal(1, CountOccurrences(escaped, "<br>"));
    }

    [Fact]
    public void SpliceReplacesOnlyTheInclusiveSpan()
    {
        const string content = "before| old |after";
        var span = new TableCellSpan(Start: 7, End: 11);
        const string padded = " new ";

        var rewritten = TableCellRewrite.Splice(content, span, padded);

        Assert.Equal("before| new |after", rewritten);
        Assert.Equal(content[..span.Start], rewritten[..span.Start]);
        Assert.Equal(content[(span.End + 1)..], rewritten[(span.Start + padded.Length)..]);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
