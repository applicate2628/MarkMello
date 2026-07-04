using System;
using System.Threading.Tasks;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// GFM task-list checkbox write-back. When the user clicks a rendered checkbox,
/// the renderer reports the item's document source line, its identity key, and
/// the new state; this flips the <c>[ ]</c>/<c>[x]</c> marker on that line — in
/// the editor buffer while editing, or in the file (read-modify-write, then
/// reload) while reading. The marker shape and identity key are owned by
/// <see cref="TaskListIdentity"/>, shared verbatim with the HTML emission side.
/// </summary>
public partial class MainWindowViewModel
{
    // Serializes toggles: a click that arrives while a prior toggle's read →
    // write → reload is still in flight is dropped, so two overlapping writes
    // cannot clobber each other (lost update).
    private bool _isTogglingTask;

    /// <summary>
    /// Set the task marker on <paramref name="line"/> (0-based document source
    /// line) to <c>[x]</c> when <paramref name="isChecked"/>, else <c>[ ]</c> —
    /// but only when the line's identity key still equals
    /// <paramref name="expectedKey"/> (fail-closed: a null/missing key refuses).
    /// Self-contained: never throws to the caller, never writes unless the target
    /// line is verified, and in reading mode ALWAYS resyncs (reloads) afterwards
    /// so an optimistic DOM checkbox can never keep lying about the file state —
    /// on a successful flip the reload shows it, on any refusal it reverts it.
    /// </summary>
    public async Task ToggleTaskLineAsync(int line, bool isChecked, string? expectedKey)
    {
        if (_isTogglingTask)
        {
            return;
        }

        _isTogglingTask = true;
        try
        {
            if (IsEditMode && EditorSession is not null)
            {
                // The editor buffer is authoritative while editing; the user still
                // owns the save. A refused flip leaves the buffer untouched and
                // the debounced preview re-render reconciles the DOM.
                if (TryFlipMarker(EditorSession.SourceText, line, isChecked, expectedKey, out var editedBuffer))
                {
                    EditorSession.SourceText = editedBuffer;
                }

                return;
            }

            var path = CurrentDocumentPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // Read the CURRENT file from disk (not the in-memory snapshot) so a
            // whole-file write cannot silently clobber an external edit, and
            // verify identity+shape+state before touching the line.
            if (await _openDocument.ExecuteAsync(path).ConfigureAwait(true)
                is OpenDocumentResult.Success opened
                && TryFlipMarker(opened.Source.Content, line, isChecked, expectedKey, out var newContent))
            {
                // Typed failure (permissions / I/O) is a result, not an exception —
                // check explicitly. Fall through to the resync either way.
                _ = await _saveDocument.ExecuteAsync(path, newContent).ConfigureAwait(true);
            }

            // ALWAYS resync in reading mode: re-render from disk truth. On a
            // successful write this reveals the new state; on any refusal
            // (identity mismatch, drift, not a marker, failed save) it reverts
            // the optimistically-flipped DOM checkbox.
            if (CanReload())
            {
                SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // Unexpected failure: leave the document as-is; the resync above (if
            // reached) or the next render reconciles the DOM.
        }
        finally
        {
            _isTogglingTask = false;
        }
    }

    /// <summary>
    /// Flip the task marker on <paramref name="line"/> to the requested state,
    /// but only if that line currently IS a task marker in the OPPOSITE state
    /// AND its identity key equals <paramref name="expectedKey"/> (null/missing
    /// key → refuse, fail-closed). Returns false (leaving
    /// <paramref name="newContent"/> = input) otherwise. EOL-preserving: only
    /// the single state char changes.
    /// </summary>
    private static bool TryFlipMarker(
        string content,
        int line,
        bool isChecked,
        string? expectedKey,
        out string newContent)
    {
        newContent = content;
        if (string.IsNullOrEmpty(content) || line < 0 || string.IsNullOrEmpty(expectedKey))
        {
            return false;
        }

        var lines = content.Split('\n');
        if (line >= lines.Length)
        {
            return false;
        }

        var match = TaskListIdentity.TaskMarkerPattern.Match(lines[line]);
        if (!match.Success)
        {
            return false;
        }

        // Identity check: the line's label hash must still equal the key the
        // renderer emitted, otherwise the view is stale (external edit shifted
        // lines) and writing here would flip the WRONG item.
        if (!string.Equals(TaskListIdentity.ComputeKey(lines[line]), expectedKey, StringComparison.Ordinal))
        {
            return false;
        }

        var currentChecked = !string.Equals(match.Groups[2].Value, " ", StringComparison.Ordinal);
        if (currentChecked == isChecked)
        {
            return false;
        }

        lines[line] = match.Groups[1].Value + (isChecked ? "x" : " ") + match.Groups[3].Value;
        newContent = string.Join("\n", lines);
        return true;
    }
}
