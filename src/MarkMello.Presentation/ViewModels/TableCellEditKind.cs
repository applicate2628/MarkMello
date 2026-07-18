using System;
using System.Threading.Tasks;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Table-cell edit strategy. It owns the G2/G3/G4 validation sequence and
/// preserves the two-phase persistence/settlement exception boundary.
/// </summary>
internal sealed class TableCellEditKind : IInDocumentEditKind
{
    private readonly IRealtimeInDocumentEditHost _host;
    private readonly ITableCellSourceEditor _tableCellSourceEditor;
    private readonly int _line;
    private readonly int _cellIndex;
    private readonly string _text;
    private readonly string? _key;
    private readonly TableCellEditOrigin _origin;

    public TableCellEditKind(
        IRealtimeInDocumentEditHost host,
        ITableCellSourceEditor tableCellSourceEditor,
        int line,
        int cellIndex,
        string text,
        string? key,
        TableCellEditOrigin origin)
    {
        _host = host;
        _tableCellSourceEditor = tableCellSourceEditor;
        _line = line;
        _cellIndex = cellIndex;
        _text = text;
        _key = key;
        _origin = origin;
    }

    public void PublishBusy()
        => _host.RefuseTableCellEdit(
            _line,
            _cellIndex,
            _host.CurrentDocumentPath ?? string.Empty,
            _origin,
            busy: true);

    public Task ApplyAsync()
    {
        var newBuffer = string.Empty;
        var canonicalText = string.Empty;
        var canonicalKey = string.Empty;
        try
        {
            if (_origin == TableCellEditOrigin.EditPreview)
            {
                if (_host.EditorSession is not { } session
                    || !TryPrepareTableCellRewrite(
                        _tableCellSourceEditor,
                        session.SourceText,
                        _line,
                        _cellIndex,
                        _text,
                        _key,
                        out var editedBuffer,
                        out var editCanonicalText,
                        out var editCanonicalKey,
                        out var editSpan,
                        out var editReplacement))
                {
                    _host.RefuseTableCellEdit(
                        _line,
                        _cellIndex,
                        _host.CurrentDocumentPath ?? string.Empty,
                        _origin);
                    return Task.CompletedTask;
                }

                var source = new MarkdownSource(
                    session.CurrentPath ?? string.Empty,
                    session.FileName,
                    editedBuffer);
                _host.PublishEditPreviewTableCellCommit(new TableCellCommit(
                    source,
                    _line,
                    _cellIndex,
                    editCanonicalText,
                    editCanonicalKey)
                {
                    Start = editSpan.Start,
                    Length = editSpan.Length,
                    Replacement = editReplacement,
                });
                return Task.CompletedTask;
            }

            // Reading-mode (Viewer) leg: rewrite the in-memory source (session
            // buffer when a session exists, else the rendered document) as an
            // UNSAVED edit. No per-edit disk read, no disk write, no reload
            // branch. Validation (G2 key / G3 plain / G4 reparse+shape / one-cell
            // probe) is unchanged; it just runs on the in-memory source.
            var readingSource = _host.EditorSession?.SourceText ?? _host.CurrentDocument?.Content;
            if (readingSource is null
                || !TryPrepareTableCellRewrite(
                    _tableCellSourceEditor,
                    readingSource,
                    _line,
                    _cellIndex,
                    _text,
                    _key,
                    out newBuffer,
                    out canonicalText,
                    out canonicalKey))
            {
                _host.RefuseTableCellEdit(
                    _line,
                    _cellIndex,
                    _host.CurrentDocumentPath ?? string.Empty,
                    _origin);
                return Task.CompletedTask;
            }
        }
        catch (Exception)
        {
            _host.RefuseTableCellEdit(
                _line,
                _cellIndex,
                _host.CurrentDocumentPath ?? string.Empty,
                _origin);
            return Task.CompletedTask;
        }

        // Keep the commit outside the catch: the buffer mutation + publish is the
        // irreversible settlement, and a later observer failure must not become a
        // refusal (a validation refusal restores the DOM; a settled edit must not).
        _host.CommitInPlaceTableCell(newBuffer, _line, _cellIndex, canonicalText, canonicalKey);
        return Task.CompletedTask;
    }

    internal static bool TryPrepareTableCellRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        string? expectedKey,
        out string newContent,
        out string canonicalText,
        out string canonicalKey)
        => TryPrepareTableCellRewrite(
            tableCellSourceEditor,
            source,
            line,
            cellIndex,
            text,
            expectedKey,
            out newContent,
            out canonicalText,
            out canonicalKey,
            out _,
            out _);

    internal static bool TryPrepareTableCellRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        string? expectedKey,
        out string newContent,
        out string canonicalText,
        out string canonicalKey,
        out TableCellSpan sourceSpan,
        out string replacement)
    {
        newContent = source;
        canonicalText = string.Empty;
        canonicalKey = string.Empty;
        sourceSpan = default;
        replacement = string.Empty;

        var located = tableCellSourceEditor.Locate(source, line, cellIndex);
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

        var original = tableCellSourceEditor.ParsePlainCell(source, line, cellIndex);
        if (original is not { } before || before.Span != span)
        {
            return false;
        }

        var escaped = TableCellRewrite.EscapeCellContent(text);
        var candidate = TableCellRewrite.Splice(source, span, escaped);

        var reparsed = tableCellSourceEditor.ParsePlainCell(candidate, line, cellIndex);
        if (reparsed is not { } after || !HasSameTableCellShape(before, after))
        {
            return false;
        }

        var expectedProbe = "| Value |\n| --- |\n|" + escaped + "|\n";
        var expected = tableCellSourceEditor.ParsePlainCell(expectedProbe, line: 2, cellIndex: 0);
        if (expected is not { } expectedPlain
            || !string.Equals(after.Text, expectedPlain.Text, StringComparison.Ordinal)
            || !TryReadSpan(candidate, after.Span, out var committedRaw))
        {
            return false;
        }

        newContent = candidate;
        canonicalText = after.Text;
        canonicalKey = TableCellIdentity.ComputeKey(committedRaw.Trim());
        sourceSpan = span;
        replacement = escaped;
        return true;
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
