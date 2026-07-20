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
    string Key)
{
    // RAW commit: the cell was rich, so the user edited its markdown and
    // <see cref="Text"/> carries the canonical RAW cell source, not decoded plain
    // text. The renderer cannot turn markdown into HTML, so the Desktop layer
    // renders the fragment before acknowledging such a commit.
    public bool Raw { get; init; }

    // Edit-preview commits carry the raw-source splice that the source editor
    // applies. Reading-mode commits retain the positional payload and leave
    // this invalid sentinel untouched.
    public int Start { get; init; } = -1;

    public int Length { get; init; } = -1;

    public string Replacement { get; init; } = string.Empty;
}

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
        TableCellEditOrigin origin,
        bool raw = false)
        => _inDocumentEditCoordinator.ApplyAsync(
            new TableCellEditKind(
                _inDocumentEditHost,
                _tableCellSourceEditor,
                line,
                cellIndex,
                text,
                key,
                origin,
                raw));

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
        string canonicalKey,
        bool raw)
    {
        var current = _document;
        if (current is null)
        {
            return;
        }

        var currentBuffer = EditorSession?.SourceText ?? current.Content;
        MarkdownSource committed;
        if (!string.Equals(currentBuffer, newBuffer, StringComparison.Ordinal))
        {
            var domPatch = CreateTableCellDomPatch(
                currentBuffer,
                line,
                cellIndex,
                canonicalText,
                canonicalKey,
                raw);
            EnsureInPlaceEditorSession(current);
            committed = new MarkdownSource(current.Path, current.FileName, newBuffer);
            _document = committed;
            EditorSession!.ApplyRealtimeInDocumentEdit(newBuffer, domPatch);

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
            canonicalKey)
        {
            Raw = raw,
        });
    }

    private RealtimeInDocumentEditDomPatch CreateTableCellDomPatch(
        string source,
        int line,
        int cellIndex,
        string canonicalText,
        string canonicalKey,
        bool raw)
    {
        // A RAW patch addresses a rich cell, whose before-state ParsePlainCell
        // cannot express — read the structural snapshot instead and carry the
        // cell's RAW markdown, matching what a raw commit publishes.
        var before = raw
            ? _tableCellSourceEditor.ParseCell(source, line, cellIndex)
            : _tableCellSourceEditor.ParsePlainCell(source, line, cellIndex);
        if (before is not { } snapshot
            || snapshot.Span.Start < 0
            || snapshot.Span.End < snapshot.Span.Start
            || snapshot.Span.End >= source.Length)
        {
            throw new InvalidOperationException("Validated table cell is missing from the source buffer.");
        }

        var beforeRaw = source.Substring(snapshot.Span.Start, snapshot.Span.Length);
        return RealtimeInDocumentEditDomPatch.ForTableCell(
            line,
            cellIndex,
            raw ? beforeRaw.Trim() : snapshot.Text,
            TableCellIdentity.ComputeKey(beforeRaw.Trim()),
            canonicalText,
            canonicalKey,
            raw);
    }

}
