using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkMello.Domain;
using MarkdigMarkdown = Markdig.Markdown;

namespace MarkMello.Infrastructure.Markdown;

/// <summary>
/// Write-back span owner for editable table cells. Given the ORIGINAL disk source
/// and a <c>(line, cellIndex)</c> coordinate, re-parses with RAW Markdig — never
/// the math-tokenizing <c>ApplicateMarkdownDocumentRenderer</c>, whose char offsets
/// are shifted and segment-relative — and returns the cell's inclusive original
/// span (the splice target). The re-parse uses the same advanced-extensions
/// pipeline and the same cell-ordinal walk as the emit side, so the span this
/// returns is over the exact bytes whose <see cref="MarkMello.Domain.TableCellIdentity"/>
/// key was emitted for a plain cell.
/// </summary>
public static class RawTableCellLocator
{
    private static readonly MarkdownPipeline RawPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <summary>
    /// Locates the cell at row <paramref name="line"/> (0-based document-absolute)
    /// and ordinal <paramref name="cellIndex"/> (0-based within the row) and returns
    /// its inclusive span, or <c>null</c> when no such cell exists. Walks
    /// <c>Descendants&lt;Table&gt;</c> so nested tables are reachable.
    /// </summary>
    public static TableCellSpan? Locate(string source, int line, int cellIndex)
        => Find(source, line, cellIndex)?.Span;

    /// <summary>
    /// Runs the single raw-Markdig coordinate walk used by both the public span locator and
    /// the Application adapter. Parser details remain internal to Infrastructure.
    /// </summary>
    internal static RawTableCellMatch? Find(string source, int line, int cellIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (line < 0 || cellIndex < 0)
        {
            return null;
        }

        var document = MarkdigMarkdown.Parse(source, RawPipeline);
        var tableIndex = 0;
        foreach (var table in document.Descendants<Table>())
        {
            var rows = table.OfType<TableRow>().ToArray();
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = rows[rowIndex];
                var ordinal = 0;
                foreach (var rowChild in row)
                {
                    if (rowChild is not TableCell cell)
                    {
                        continue;
                    }

                    if (cell.Line == line && ordinal == cellIndex)
                    {
                        return new RawTableCellMatch(
                            new TableCellSpan(cell.Span.Start, cell.Span.End),
                            cell,
                            tableIndex,
                            table.Line,
                            rows[^1].Line,
                            rowIndex,
                            ordinal,
                            rows.Length,
                            table.ColumnDefinitions.Count);
                    }

                    ordinal++;
                }
            }

            tableIndex++;
        }

        return null;
    }
}

internal readonly record struct RawTableCellMatch(
    TableCellSpan Span,
    TableCell Cell,
    int TableIndex,
    int TableStartLine,
    int TableEndLine,
    int RowIndex,
    int ColumnIndex,
    int RowCount,
    int ColumnCount);
