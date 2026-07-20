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

        return CreateSnapshot(match.Value, text, isPlainText: true);
    }

    public TableCellSourceSnapshot? ParseCell(string source, int line, int cellIndex)
    {
        var match = RawTableCellLocator.Find(source, line, cellIndex);
        if (match is null)
        {
            return null;
        }

        // A rich cell keeps its structural coordinates and reports IsPlainText=false;
        // its markdown is read from the span by the caller, never from Text.
        var isPlainText = TryDecodePlainText(match.Value.Cell, out var text);
        return CreateSnapshot(match.Value, isPlainText ? text : string.Empty, isPlainText);
    }

    private static TableCellSourceSnapshot CreateSnapshot(
        RawTableCellMatch match,
        string text,
        bool isPlainText)
        => new(
            match.Span,
            text,
            match.TableIndex,
            match.TableStartLine,
            match.TableEndLine,
            match.RowIndex,
            match.ColumnIndex,
            match.RowCount,
            match.ColumnCount,
            isPlainText);

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
