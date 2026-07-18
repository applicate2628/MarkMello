using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

public sealed record TableCellRefusal(int Line, int CellIndex, string Path, bool Busy = false);

public sealed record TableCellCommit(
    MarkdownSource Source,
    int Line,
    int CellIndex,
    string Text,
    string Key);

public enum TableCellEditOrigin
{
    Viewer,
    EditPreview,
}

/// <summary>
/// Literal plain-text table-cell write-back. This owner stays parallel to the
/// task-list write-back because the two flows have different validation and
/// acknowledgement contracts.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly ITableCellSourceEditor _tableCellSourceEditor;

    public event EventHandler<TableCellCommit>? TableCellCommitted;

    public event EventHandler<TableCellRefusal>? TableCellEditRefused;

    public event EventHandler<TableCellCommit>? EditPreviewTableCellCommitted;

    public event EventHandler<TableCellRefusal>? EditPreviewTableCellEditRefused;

    public Task SetTableCellAsync(
        int line,
        int cellIndex,
        string text,
        string? key,
        TableCellEditOrigin origin)
        => _inDocumentEditCoordinator.ApplyAsync(
            new TableCellEditKind(
                _inDocumentEditHost,
                _tableCellSourceEditor,
                line,
                cellIndex,
                text,
                key,
                origin));

    private void RefuseTableCellEdit(int line, int cellIndex, string path, TableCellEditOrigin origin, bool busy = false)
    {
        var refusal = new TableCellRefusal(line, cellIndex, path, busy);
        if (origin == TableCellEditOrigin.EditPreview)
        {
            EditPreviewTableCellEditRefused?.Invoke(this, refusal);
        }
        else
        {
            TableCellEditRefused?.Invoke(this, refusal);
        }
    }

    private void PublishTableCellCommit(TableCellCommit commit)
        => TableCellCommitted?.Invoke(this, commit);

    private void PublishEditPreviewTableCellCommit(TableCellCommit commit)
        => EditPreviewTableCellCommitted?.Invoke(this, commit);

    /// <summary>
    /// Reading-mode (Viewer-origin) commit for a validated cell rewrite: move the
    /// lazily-materialized editor session to <paramref name="newBuffer"/> as an
    /// UNSAVED edit (dirty; Ctrl+S owns the write) and patch <see cref="Document"/>
    /// silently, keeping DOM/buffer/_document in lockstep without a cold re-render.
    /// A no-net-change edit (a re-typed value that re-pads to identical bytes)
    /// still publishes the canonical commit so the renderer settles the cell, but
    /// materializes nothing and leaves the dirty state untouched.
    /// </summary>
    private void CommitInPlaceTableCell(
        string newBuffer,
        int line,
        int cellIndex,
        string canonicalText,
        string canonicalKey)
    {
        var current = _document;
        if (current is null)
        {
            return;
        }

        MarkdownSource committed;
        if (!string.Equals(current.Content, newBuffer, StringComparison.Ordinal))
        {
            EnsureInPlaceEditorSession(current);
            committed = new MarkdownSource(current.Path, current.FileName, newBuffer);
            _document = committed;
            EditorSession!.ApplyInPlaceEditToBuffer(newBuffer);

            OnPropertyChanged(nameof(WordCount));
            OnPropertyChanged(nameof(WordCountStatusLabel));
            QueueDeferredRenderedDocument(committed);
        }
        else
        {
            // Canonical settle with no net content change: keep the buffer in
            // lockstep (ApplyInPlaceEditToBuffer no-ops on an unchanged buffer, so
            // the clean/dirty state is untouched).
            committed = current;
            EditorSession?.ApplyInPlaceEditToBuffer(newBuffer);
        }

        PublishTableCellCommit(new TableCellCommit(
            committed,
            line,
            cellIndex,
            canonicalText,
            canonicalKey));
    }

}
