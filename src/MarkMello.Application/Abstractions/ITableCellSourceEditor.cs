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

    /// <summary>
    /// Re-parses a raw source cell for its STRUCTURAL state regardless of richness:
    /// returns <c>null</c> only when the cell is absent. A rich cell (math, emphasis,
    /// link, code, <c>&lt;br&gt;</c>) comes back with <see cref="TableCellSourceSnapshot.IsPlainText"/>
    /// <c>false</c> and an empty <see cref="TableCellSourceSnapshot.Text"/> — callers on
    /// the raw path read the cell's markdown from the span, never from <c>Text</c>,
    /// because an EMPTY plain cell is also <c>Text == ""</c>.
    /// </summary>
    TableCellSourceSnapshot? ParseCell(string source, int line, int cellIndex);
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
    int ColumnCount,
    bool IsPlainText = true);
