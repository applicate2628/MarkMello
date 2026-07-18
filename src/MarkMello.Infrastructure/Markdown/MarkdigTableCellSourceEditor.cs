using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Infrastructure.Markdown;

/// <summary>
/// Raw-Markdig table-cell source adapter for Application callers.
/// </summary>
public sealed class MarkdigTableCellSourceEditor : ITableCellSourceEditor
{
    public TableCellSpan? Locate(string source, int line, int cellIndex)
        => RawTableCellLocator.Locate(source, line, cellIndex);

    public TableCellSourceSnapshot? ParsePlainCell(string source, int line, int cellIndex)
    {
        var match = RawTableCellLocator.Find(source, line, cellIndex);
        if (match is null || !TryDecodePlainText(match.Value.Cell, out var text))
        {
            return null;
        }

        return new TableCellSourceSnapshot(
            match.Value.Span,
            text,
            match.Value.TableIndex,
            match.Value.TableStartLine,
            match.Value.TableEndLine,
            match.Value.RowIndex,
            match.Value.ColumnIndex,
            match.Value.RowCount,
            match.Value.ColumnCount);
    }

    private static bool TryDecodePlainText(ContainerBlock cell, out string text)
    {
        var decoded = new StringBuilder();

        foreach (var block in cell)
        {
            if (block is not ParagraphBlock paragraph)
            {
                text = string.Empty;
                return false;
            }

            if (paragraph.Inline is null)
            {
                continue;
            }

            foreach (var inline in paragraph.Inline)
            {
                if (inline is not LiteralInline literal)
                {
                    text = string.Empty;
                    return false;
                }

                decoded.Append(literal.Content.ToString());
            }
        }

        text = decoded.ToString();
        return true;
    }
}
