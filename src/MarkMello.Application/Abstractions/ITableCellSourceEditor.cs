using MarkMello.Domain;

namespace MarkMello.Application.Abstractions;

/// <summary>
/// Locates and validates table cells against a raw Markdown source without exposing parser types.
/// </summary>
/// <remarks>
/// Load-bearing contract, do not weaken: every returned <see cref="TableCellSpan"/> and
/// <see cref="TableCellSourceSnapshot.Span"/> indexes into EXACTLY the <c>source</c> string that was
/// passed in — the caller splices those offsets straight back into that same string. An implementation
/// backed by the math-tokenizing <c>ApplicateMarkdownDocumentRenderer</c> is therefore FORBIDDEN: its
/// <c>ProtectInlineMath</c>/<c>SplitDisplayMath</c> passes shift char offsets and make spans
/// segment-relative, so they would splice the wrong bytes. Only a RAW Markdig parse of the passed
/// source is admissible.
/// </remarks>
public interface ITableCellSourceEditor
{
    /// <summary>
    /// Locates the inclusive raw source span for a table cell, or returns <c>null</c> when absent.
    /// The returned span indexes into exactly the passed <paramref name="source"/> (see the interface remarks).
    /// </summary>
    TableCellSpan? Locate(string source, int line, int cellIndex);

    /// <summary>
    /// Re-parses a raw source cell and returns its decoded plain-text state, or <c>null</c> when absent or rich.
    /// </summary>
    TableCellSourceSnapshot? ParsePlainCell(string source, int line, int cellIndex);
}

/// <summary>
/// Parser-neutral state used to prove that a table-cell rewrite preserved the target and table shape.
/// </summary>
public readonly record struct TableCellSourceSnapshot(
    TableCellSpan Span,
    string Text,
    int TableIndex,
    int TableStartLine,
    int TableEndLine,
    int RowIndex,
    int ColumnIndex,
    int RowCount,
    int ColumnCount);
