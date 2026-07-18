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
    private bool _isEditingCell;

    public event EventHandler<TableCellCommit>? TableCellCommitted;

    public event EventHandler<TableCellRefusal>? TableCellEditRefused;

    public event EventHandler<TableCellCommit>? EditPreviewTableCellCommitted;

    public event EventHandler<TableCellRefusal>? EditPreviewTableCellEditRefused;

    public async Task SetTableCellAsync(
        int line,
        int cellIndex,
        string text,
        string? key,
        TableCellEditOrigin origin)
    {
        if (_isEditingCell)
        {
            // Serializer in flight: a BUSY refusal must not make the renderer
            // discard the user's typed text (it re-blurs to retry once free).
            RefuseTableCellEdit(line, cellIndex, CurrentDocumentPath ?? string.Empty, origin, busy: true);
            return;
        }

        _isEditingCell = true;
        try
        {
            MarkdownSource persistedSource;
            string canonicalText;
            string canonicalKey;
            try
            {
                if (origin == TableCellEditOrigin.EditPreview)
                {
                    if (EditorSession is not { } session
                        || !TryPrepareTableCellRewrite(
                            session.SourceText,
                            line,
                            cellIndex,
                            text,
                            key,
                            out var editedBuffer,
                            out var editCanonicalText,
                            out var editCanonicalKey))
                    {
                        RefuseTableCellEdit(line, cellIndex, CurrentDocumentPath ?? string.Empty, origin);
                        return;
                    }

                    var source = new MarkdownSource(
                        session.CurrentPath ?? string.Empty,
                        session.FileName,
                        editedBuffer);
                    EditPreviewTableCellCommitted?.Invoke(this, new TableCellCommit(
                        source,
                        line,
                        cellIndex,
                        editCanonicalText,
                        editCanonicalKey));
                    session.SourceText = editedBuffer;
                    return;
                }

                var path = CurrentDocumentPath;
                if (string.IsNullOrEmpty(path))
                {
                    RefuseTableCellEdit(line, cellIndex, string.Empty, origin);
                    return;
                }

                if (await _openDocument.ExecuteAsync(path).ConfigureAwait(true)
                    is not OpenDocumentResult.Success opened)
                {
                    RefuseTableCellEdit(line, cellIndex, path, origin);
                    return;
                }

                // G1: a whole-file rewrite may start only from the exact source that
                // produced the rendered snapshot. This precedes every cell/key probe.
                if (!string.Equals(opened.Source.Content, Document?.Content, StringComparison.Ordinal))
                {
                    RefuseTableCellEdit(line, cellIndex, path, origin);
                    if (CanReload())
                    {
                        await ReloadAsync().ConfigureAwait(true);
                    }

                    return;
                }

                if (!TryPrepareTableCellRewrite(
                        opened.Source.Content,
                        line,
                        cellIndex,
                        text,
                        key,
                        out var newContent,
                        out canonicalText,
                        out canonicalKey))
                {
                    RefuseTableCellEdit(line, cellIndex, path, origin);
                    return;
                }

                if (string.Equals(newContent, opened.Source.Content, StringComparison.Ordinal))
                {
                    // No net source change (an edit that normalizes back to the
                    // exact bytes, or a stray no-op blur). Acknowledge the canonical
                    // text WITHOUT a disk write: a no-op write rewrites the file on
                    // read-only interaction and re-pads the span, destroying
                    // hand-aligned cell padding for zero user edit.
                    persistedSource = opened.Source;
                }
                else if (await _saveDocument.ExecuteAsync(path, newContent).ConfigureAwait(true)
                    is SaveDocumentResult.Success saved)
                {
                    persistedSource = saved.Source;
                }
                else
                {
                    RefuseTableCellEdit(line, cellIndex, path, origin);
                    return;
                }
            }
            catch (Exception)
            {
                RefuseTableCellEdit(line, cellIndex, CurrentDocumentPath ?? string.Empty, origin);
                return;
            }

            // A successful persistence is irreversible. Post-save settlement and
            // its observers must not be reclassified as a refusal if they throw.
            CommitTableCellSnapshot(
                persistedSource,
                line,
                cellIndex,
                text,
                key,
                canonicalText,
                canonicalKey);
        }
        finally
        {
            _isEditingCell = false;
        }
    }

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

    private bool TryPrepareTableCellRewrite(
        string source,
        int line,
        int cellIndex,
        string text,
        string? expectedKey,
        out string newContent,
        out string canonicalText,
        out string canonicalKey)
    {
        newContent = source;
        canonicalText = string.Empty;
        canonicalKey = string.Empty;

        // G2: the raw span is re-derived from this exact fresh source and its
        // trimmed identity must still match the renderer request.
        var located = _tableCellSourceEditor.Locate(source, line, cellIndex);
        if (located is not { } span
            || string.IsNullOrEmpty(expectedKey)
            || !TryReadSpan(source, span, out var rawCell)
            || !string.Equals(
                TableCellIdentity.ComputeKey(rawCell.Trim()),
                expectedKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        // G3: a cell that is already rich is outside the literal-plain-text
        // contract. The parser-neutral snapshot also captures the G4 baseline.
        var original = _tableCellSourceEditor.ParsePlainCell(source, line, cellIndex);
        if (original is not { } before || before.Span != span)
        {
            return false;
        }

        var escaped = TableCellRewrite.EscapeCellContent(text);
        var candidate = TableCellRewrite.Splice(source, span, escaped);

        // G4: re-parse the in-memory candidate before any write. The same table,
        // row, column, and shape must survive, and the target must remain plain.
        var reparsed = _tableCellSourceEditor.ParsePlainCell(candidate, line, cellIndex);
        if (reparsed is not { } after || !HasSameTableCellShape(before, after))
        {
            return false;
        }

        // Decode the committed input through the same raw parser in an isolated
        // one-cell table. This keeps normalization/escaping single-owned by
        // TableCellRewrite while independently proving the target context decoded
        // to exactly that normalized literal text.
        var expectedProbe = "| Value |\n| --- |\n|" + escaped + "|\n";
        var expected = _tableCellSourceEditor.ParsePlainCell(expectedProbe, line: 2, cellIndex: 0);
        if (expected is not { } expectedPlain
            || !string.Equals(after.Text, expectedPlain.Text, StringComparison.Ordinal)
            || !TryReadSpan(candidate, after.Span, out var committedRaw))
        {
            return false;
        }

        newContent = candidate;
        canonicalText = after.Text;
        canonicalKey = TableCellIdentity.ComputeKey(committedRaw.Trim());
        return true;
    }

    private void CommitTableCellSnapshot(
        MarkdownSource persistedSource,
        int line,
        int cellIndex,
        string requestedText,
        string? expectedKey,
        string canonicalText,
        string canonicalKey)
    {
        var current = _document;
        var isCurrentDocument = current is not null
                                && string.Equals(
                                    current.Path,
                                    persistedSource.Path,
                                    StringComparison.OrdinalIgnoreCase);
        var committedSource = isCurrentDocument
            ? new MarkdownSource(current!.Path, current.FileName, persistedSource.Content)
            : persistedSource;

        if (isCurrentDocument)
        {
            _document = committedSource;
        }

        if (EditorSession is { } session
            && string.Equals(
                session.CurrentPath,
                persistedSource.Path,
                StringComparison.OrdinalIgnoreCase))
        {
            var updatedSession = TryPrepareTableCellRewrite(
                session.SourceText,
                line,
                cellIndex,
                requestedText,
                expectedKey,
                out var sessionText,
                out _,
                out _);
            session.ApplyPersistedTableCellEdit(
                updatedSession ? sessionText : session.SourceText,
                persistedSource.Content);
        }

        if (isCurrentDocument)
        {
            OnPropertyChanged(nameof(WordCount));
            OnPropertyChanged(nameof(WordCountStatusLabel));
            QueueDeferredRenderedDocument(committedSource);
        }

        PublishTableCellCommit(new TableCellCommit(
            committedSource,
            line,
            cellIndex,
            canonicalText,
            canonicalKey));
    }

    private static bool HasSameTableCellShape(
        TableCellSourceSnapshot before,
        TableCellSourceSnapshot after)
        => before.TableIndex == after.TableIndex
           && before.TableStartLine == after.TableStartLine
           && before.TableEndLine == after.TableEndLine
           && before.RowIndex == after.RowIndex
           && before.ColumnIndex == after.ColumnIndex
           && before.RowCount == after.RowCount
           && before.ColumnCount == after.ColumnCount;

    private static bool TryReadSpan(string source, TableCellSpan span, out string value)
    {
        if (span.Start < 0 || span.End < span.Start || span.End >= source.Length)
        {
            value = string.Empty;
            return false;
        }

        value = source.Substring(span.Start, span.Length);
        return true;
    }
}
