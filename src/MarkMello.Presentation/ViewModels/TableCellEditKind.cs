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
    private readonly bool _raw;

    public TableCellEditKind(
        IRealtimeInDocumentEditHost host,
        ITableCellSourceEditor tableCellSourceEditor,
        int line,
        int cellIndex,
        string text,
        string? key,
        TableCellEditOrigin origin,
        bool raw = false)
    {
        _host = host;
        _tableCellSourceEditor = tableCellSourceEditor;
        _line = line;
        _cellIndex = cellIndex;
        _text = text;
        _key = key;
        _origin = origin;
        _raw = raw;
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
                        _raw,
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
                    Raw = _raw,
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
                    _raw,
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
        _host.CommitInPlaceTableCell(newBuffer, _line, _cellIndex, canonicalText, canonicalKey, _raw);
        return Task.CompletedTask;
    }

    internal static bool TryPrepareTableCellRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        string? expectedKey,
        bool raw,
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
            raw,
            out newContent,
            out canonicalText,
            out canonicalKey,
            out _,
            out _);

    /// <summary>
    /// Prepares a validated cell rewrite. <paramref name="raw"/> selects the mode,
    /// and the mode is a RENDERER-side fact: it states whether the user was editing
    /// the cell's RENDERED text (literal mode — the cell was plain, so rendered text
    /// == source text) or its RAW markdown (raw mode — the cell was rich, so the
    /// renderer swapped in the source before handing the caret over). Both modes are
    /// independently fail-closed, so the mode only picks the escaping/validation
    /// policy and never has to be trusted for document safety:
    /// literal mode escapes <c>\</c> and <c>|</c> and then REFUSES anything that no
    /// longer reparses as plain text; raw mode escapes nothing and instead REFUSES
    /// any splice whose reparsed cell no longer covers the spliced markdown.
    /// </summary>
    internal static bool TryPrepareTableCellRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        string? expectedKey,
        bool raw,
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

        // G2 identity gate, shared by both modes: the addressed span must still hold
        // exactly the bytes whose key the emit side stamped into the DOM.
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

        return raw
            ? TryPrepareRawRewrite(
                tableCellSourceEditor,
                source,
                line,
                cellIndex,
                text,
                span,
                out newContent,
                out canonicalText,
                out canonicalKey,
                out sourceSpan,
                out replacement)
            : TryPrepareLiteralRewrite(
                tableCellSourceEditor,
                source,
                line,
                cellIndex,
                text,
                span,
                out newContent,
                out canonicalText,
                out canonicalKey,
                out sourceSpan,
                out replacement);
    }

    // Literal path — UNCHANGED behaviour for plain cells: escape the typed text as
    // literal content (G3 plain before / G4 reparse+shape / one-cell probe), so any
    // typed markdown that would turn the cell rich is refused, exactly as before.
    private static bool TryPrepareLiteralRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        TableCellSpan span,
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

    // Raw path — the typed text IS the cell's markdown, so nothing is escaped and
    // the plain-text probes do not apply. The structural guarantee comes instead
    // from COVERAGE: after splicing, the reparsed cell must still be the same cell
    // of the same table (shape) AND must cover every non-whitespace character of
    // what was spliced. That single check is what refuses a bare '|' (which would
    // split the cell, leaving the reparsed cell covering only the fragment before
    // the pipe) without silently rewriting the user's markdown behind their back.
    private static bool TryPrepareRawRewrite(
        ITableCellSourceEditor tableCellSourceEditor,
        string source,
        int line,
        int cellIndex,
        string text,
        TableCellSpan span,
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

        var original = tableCellSourceEditor.ParseCell(source, line, cellIndex);
        if (original is not { } before
            || before.Span != span
            || !TryReadSpan(source, span, out var originalRaw))
        {
            return false;
        }

        var padded = TableCellRewrite.NormalizeRawCellContent(text, originalRaw);
        var candidate = TableCellRewrite.Splice(source, span, padded);

        var reparsed = tableCellSourceEditor.ParseCell(candidate, line, cellIndex);
        if (reparsed is not { } after
            || !HasSameTableCellShape(before, after)
            || !TryReadSpan(candidate, after.Span, out var committedRaw))
        {
            return false;
        }

        // Coverage gate: the reparsed cell must hold exactly the markdown that was
        // spliced. This is what refuses a bare '|' — that would split the cell, so
        // the reparsed cell holds only the fragment before the pipe and no longer
        // trim-matches. Compared on TRIMMED text because the located span includes
        // the cell's padding for some contents and excludes it for others.
        if (!string.Equals(committedRaw.Trim(), padded.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        newContent = candidate;
        canonicalText = committedRaw.Trim();
        canonicalKey = TableCellIdentity.ComputeKey(canonicalText);
        sourceSpan = span;
        replacement = padded;
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
