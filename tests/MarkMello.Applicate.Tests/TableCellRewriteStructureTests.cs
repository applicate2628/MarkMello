using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;
using Xunit;
using MarkdigMarkdown = Markdig.Markdown;
using NeutralTableCellSpan = MarkMello.Domain.TableCellSpan;

namespace MarkMello.Applicate.Tests;

public sealed class TableCellRewriteStructureTests
{
    private const string ThreeColumnLf =
        "| Left | Middle | Right |\n"
        + "| :--- | ---: | :---: |\n"
        + "| one | target | three |\n";

    private const string ThreeColumnCrlf =
        "| Left | Middle | Right |\r\n"
        + "| :--- | ---: | :---: |\r\n"
        + "| one | target | three |\r\n";

    private const string OneColumnLf =
        "| Only |\n"
        + "| :---: |\n"
        + "| target |\n";

    private static readonly MarkdownPipeline RawPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static TheoryData<string, string, int, int, string> RewriteCorpus => new()
    {
        { "pipe", ThreeColumnLf, 2, 1, "left|right" },
        { "escaped pipe", ThreeColumnLf, 2, 1, "left\\|right" },
        { "double slash", ThreeColumnLf, 2, 1, "left\\\\right" },
        { "terminal slash", ThreeColumnLf, 2, 1, "trailing\\" },
        { "literal break", ThreeColumnLf, 2, 1, "left<br>right" },
        { "entities", ThreeColumnLf, 2, 1, "&amp; &copy;" },
        { "controls", ThreeColumnLf, 2, 1, "a\u0001b\u007fc" },
        { "empty", ThreeColumnLf, 2, 1, string.Empty },
        { "edge spaces", ThreeColumnLf, 2, 1, "  left   right  " },
        { "CRLF", ThreeColumnCrlf, 2, 1, "left\r\nright" },
        { "first target", ThreeColumnLf, 2, 0, "first|cell" },
        { "last target", ThreeColumnLf, 2, 2, "last\\" },
        { "one column", OneColumnLf, 2, 0, "only|cell" }
    };

    [Theory]
    [MemberData(nameof(RewriteCorpus))]
    public void RewritePreservesRawMarkdigTableStructure(
        string caseName,
        string source,
        int targetLine,
        int targetCellIndex,
        string content)
    {
        _ = caseName;
        var before = ParseShape(source);
        var rawSpan = RawTableCellLocator.Locate(source, targetLine, targetCellIndex);
        Assert.NotNull(rawSpan);

        var padded = TableCellRewrite.EscapeCellContent(content);
        var span = new NeutralTableCellSpan(rawSpan.Value.Start, rawSpan.Value.End);
        var rewritten = TableCellRewrite.Splice(source, span, padded);
        var after = ParseShape(rewritten);

        Assert.Equal(before.ColumnCount, after.ColumnCount);
        Assert.Equal(before.RowCellCounts, after.RowCellCounts);
        Assert.Equal(before.HeaderRows, after.HeaderRows);
        Assert.Equal(before.Alignments, after.Alignments);
        Assert.Equal(GetLine(source, 1), GetLine(rewritten, 1));
        Assert.Equal(GetOuterDelimiterPairs(source), GetOuterDelimiterPairs(rewritten));
    }

    private static TableShape ParseShape(string source)
    {
        var document = MarkdigMarkdown.Parse(source, RawPipeline);
        var table = Assert.Single(document.Descendants<Table>());
        var rows = table.OfType<TableRow>().ToArray();
        var alignments = table.ColumnDefinitions?
            .Select(definition => definition.Alignment.ToString())
            .ToArray()
            ?? [];

        return new TableShape(
            alignments.Length,
            rows.Select(row => row.OfType<TableCell>().Count()).ToArray(),
            rows.Select(row => row.IsHeader).ToArray(),
            alignments);
    }

    private static string GetLine(string source, int lineIndex) => source.Split('\n')[lineIndex];

    private static string[] GetOuterDelimiterPairs(string source) => source
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r'))
        .Select(line => $"{line[0]}{line[^1]}")
        .ToArray();

    private sealed record TableShape(
        int ColumnCount,
        int[] RowCellCounts,
        bool[] HeaderRows,
        string?[] Alignments);
}
