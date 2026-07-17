using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdigMarkdown = Markdig.Markdown;

namespace MarkMello.Infrastructure.Markdown;

/// <summary>
/// Inclusive character span of a table cell in a raw markdown source.
/// </summary>
/// <param name="Start">0-based index of the first character (inclusive).</param>
/// <param name="End">0-based index of the last character (inclusive).</param>
public readonly record struct RawTableCellSpan(int Start, int End)
{
    /// <summary>Number of characters in the span (<c>End - Start + 1</c>).</summary>
    public int Length => End - Start + 1;
}

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
    public static RawTableCellSpan? Locate(string source, int line, int cellIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (line < 0 || cellIndex < 0)
        {
            return null;
        }

        var document = MarkdigMarkdown.Parse(source, RawPipeline);
        foreach (var table in document.Descendants<Table>())
        {
            foreach (var child in table)
            {
                if (child is not TableRow row)
                {
                    continue;
                }

                var ordinal = 0;
                foreach (var rowChild in row)
                {
                    if (rowChild is not TableCell cell)
                    {
                        continue;
                    }

                    if (cell.Line == line && ordinal == cellIndex)
                    {
                        return new RawTableCellSpan(cell.Span.Start, cell.Span.End);
                    }

                    ordinal++;
                }
            }
        }

        return null;
    }
}
