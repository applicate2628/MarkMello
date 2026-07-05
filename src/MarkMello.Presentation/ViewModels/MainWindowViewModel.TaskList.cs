using System;
using System.Threading.Tasks;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Payload of <see cref="MainWindowViewModel.TaskToggleDomRevertRequested"/>:
/// the single checkbox at <paramref name="Line"/> must be set back to
/// <paramref name="Checked"/> in the rendered DOM (surgical revert — no reload,
/// no scroll motion).
/// </summary>
public sealed record TaskToggleRevertRequest(int Line, bool Checked);

/// <summary>
/// Payload of <see cref="MainWindowViewModel.TaskToggleCommitted"/>: the
/// patched in-memory source plus the flipped line/state. The line/state pair
/// lets a surface whose DOM did NOT receive the user's click (the off-screen
/// edit-preview host — a distinct WebView) patch its one checkbox surgically
/// BEFORE the silent source swap, so the swap's premise ("the DOM already
/// shows this content") holds on every surface.
/// </summary>
public sealed record TaskToggleCommit(MarkdownSource Source, int Line, bool Checked);

/// <summary>
/// The surface whose DOM received the task-checkbox click. The channel's leg
/// is selected by THIS, not by the current mode: both legs' correctness
/// premise is "the clicked surface's DOM already shows the flipped state"
/// (the renderer's optimistic flip), and a toggle message that crosses a
/// Ctrl+E boundary in flight must still run the leg of the surface that was
/// actually clicked — the mode at dispatch time is the wrong discriminator.
/// </summary>
public enum TaskToggleOrigin
{
    Viewer,
    EditPreview,
}

/// <summary>
/// GFM task-list checkbox write-back — the in-place update channel.
///
/// <para>ONE logic (design: .scratch/plans/design-checkbox-scrolljump.md,
/// fable-recommended Candidate C): a VERIFIED successful flip needs zero
/// re-render — the file provably matches the optimistic DOM, so the in-memory
/// snapshots are silently patched to the same state (<see cref="TaskToggleCommitted"/>)
/// and nothing repaints, nothing scrolls. A refusal with UNCHANGED disk content
/// gets a surgical single-checkbox DOM revert
/// (<see cref="TaskToggleDomRevertRequested"/>). A full reload happens ONLY when
/// the disk genuinely differs from the rendered snapshot (external edit) — the
/// one case where a truthful full re-render is required.</para>
/// </summary>
public partial class MainWindowViewModel
{
    // Serializes toggles: a click that arrives while a prior toggle's read →
    // write is still in flight is dropped, so two overlapping writes cannot
    // clobber each other (lost update).
    private bool _isTogglingTask;

    /// <summary>
    /// Raised after a VERIFIED successful flip, carrying the patched in-memory
    /// source (file == DOM == snapshot by construction). The host mirrors it
    /// into the shared WebView surfaces and the open-documents service WITHOUT
    /// any render request.
    /// </summary>
    public event EventHandler<TaskToggleCommit>? TaskToggleCommitted;

    /// <summary>
    /// Raised when a toggle was refused while the disk still matches the
    /// rendered snapshot — the optimistically-flipped checkbox must be set back
    /// surgically (a value-equal reload would no-op and leave the DOM lying).
    /// </summary>
    public event EventHandler<TaskToggleRevertRequest>? TaskToggleDomRevertRequested;

    /// <summary>
    /// Edit-mode counterpart of <see cref="TaskToggleCommitted"/>: the click
    /// happened in the edit-preview DOM (already optimistically flipped) and
    /// the flip landed in the editor buffer as an unsaved edit. The host moves
    /// the edit-preview surface's Source to the flipped buffer BEFORE the
    /// debounced live-edit re-render runs, so that render dedups to a
    /// value-equal no-op — zero repaint, zero scroll motion, exactly like
    /// reading mode. Disk, viewer snapshot, and the open-docs mirror are NOT
    /// touched: the user still owns the save.
    /// </summary>
    public event EventHandler<TaskToggleCommit>? EditPreviewTaskToggleCommitted;

    /// <summary>
    /// Edit-mode counterpart of <see cref="TaskToggleDomRevertRequested"/>: a
    /// refused flip leaves the buffer unchanged, so NO re-render will run and
    /// the optimistic DOM flip in the edit-preview would keep lying without a
    /// surgical single-checkbox revert.
    /// </summary>
    public event EventHandler<TaskToggleRevertRequest>? EditPreviewTaskToggleRevertRequested;

    /// <summary>
    /// Set the task marker on <paramref name="line"/> (0-based document source
    /// line) to <c>[x]</c> when <paramref name="isChecked"/>, else <c>[ ]</c> —
    /// only when the line's identity key still equals <paramref name="expectedKey"/>
    /// (fail-closed: null/missing refuses). The leg is selected by
    /// <paramref name="origin"/> — the surface that was clicked — never by the
    /// current mode (see <see cref="TaskToggleOrigin"/>). Never throws to the
    /// caller.
    /// </summary>
    public async Task ToggleTaskLineAsync(int line, bool isChecked, string? expectedKey, TaskToggleOrigin origin)
    {
        if (_isTogglingTask)
        {
            return;
        }

        _isTogglingTask = true;
        try
        {
            if (origin == TaskToggleOrigin.EditPreview)
            {
                // The editor buffer is authoritative for edit-surface clicks;
                // the user still owns the save. This holds even when the mode
                // already flipped back to reading while the message was in
                // flight: the flip lands in the (now dormant) buffer and the
                // dirty flow owns it from there.
                if (EditorSession is not { } session)
                {
                    // No live session to receive the flip — put the clicked
                    // surface's checkbox back to its pre-click state.
                    EditPreviewTaskToggleRevertRequested?.Invoke(
                        this, new TaskToggleRevertRequest(line, !isChecked));
                    return;
                }

                if (TryFlipMarker(session.SourceText, line, isChecked, expectedKey, out var editedBuffer))
                {
                    // Same in-place channel as reading mode: swap the
                    // edit-preview host's Source to the flipped buffer FIRST
                    // (the DOM already shows it — the click's optimistic flip),
                    // THEN publish the buffer. The debounced live-edit render
                    // that follows sees a value-equal source and dedups to a
                    // no-op, so nothing repaints and the scroll never moves.
                    // Built from the same session fields the preview render
                    // pipeline uses, so record equality holds by construction.
                    EditPreviewTaskToggleCommitted?.Invoke(this, new TaskToggleCommit(
                        new MarkdownSource(session.CurrentPath ?? string.Empty, session.FileName, editedBuffer),
                        line,
                        isChecked));
                    session.SourceText = editedBuffer;
                    return;
                }

                // Refused flip (stale key / not a marker / already in the
                // requested state): the buffer is unchanged, so no re-render
                // will run — revert the ONE optimistically-flipped checkbox to
                // the buffer's actual state or the DOM keeps lying.
                EditPreviewTaskToggleRevertRequested?.Invoke(
                    this,
                    new TaskToggleRevertRequest(line, ReadDiskCheckedState(session.SourceText, line, !isChecked)));
                return;
            }

            var path = CurrentDocumentPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // Fresh disk read: a whole-file write must never clobber an
            // external edit, and identity+shape+state are verified against
            // what is REALLY on disk.
            if (await _openDocument.ExecuteAsync(path).ConfigureAwait(true)
                is not OpenDocumentResult.Success opened)
            {
                // Disk unreadable: nothing safe to write; put the checkbox
                // back to its pre-click state.
                TaskToggleDomRevertRequested?.Invoke(this, new TaskToggleRevertRequest(line, !isChecked));
                return;
            }

            if (TryFlipMarker(opened.Source.Content, line, isChecked, expectedKey, out var newContent))
            {
                // Typed result CHECKED (a save failure is a result, not an
                // exception): only a verified write commits the snapshots.
                if (await _saveDocument.ExecuteAsync(path, newContent).ConfigureAwait(true)
                    is SaveDocumentResult.Success)
                {
                    CommitTaskToggleSnapshot(path, newContent, line, isChecked);
                    return;
                }

                // Save failed → disk still holds the OLD state → the reload
                // would publish value-equal content and no-op; the surgical
                // revert is the only mechanism that actually reverts here.
                TaskToggleDomRevertRequested?.Invoke(this, new TaskToggleRevertRequest(line, !isChecked));
                return;
            }

            // Flip refused. Split by cause:
            if (string.Equals(opened.Source.Content, Document?.Content, StringComparison.Ordinal))
            {
                // Disk == rendered snapshot: the refusal is local (null key /
                // not a marker / already in the requested state). Revert the
                // one checkbox to the ACTUAL disk state — no reload, no jump.
                TaskToggleDomRevertRequested?.Invoke(
                    this,
                    new TaskToggleRevertRequest(line, ReadDiskCheckedState(opened.Source.Content, line, !isChecked)));
                return;
            }

            // Disk != snapshot: an external edit changed the document under
            // the view — the ONLY case that warrants a full truthful reload.
            if (CanReload())
            {
                SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // Unexpected failure: leave the document as-is; the next real
            // render reconciles the DOM.
        }
        finally
        {
            _isTogglingTask = false;
        }
    }

    /// <summary>
    /// Move every in-memory snapshot to the just-written content WITHOUT any
    /// render request: the DOM already shows this state and the view dedups
    /// renders by value, so publishing through the Document setter (reference
    /// identity) would trigger a full cold re-render + scroll reset for
    /// nothing. The backing field is patched silently; downstream consumers
    /// (edit-enter, health-fix, tab-return, theme re-render) all see the
    /// flipped content. The native-fallback RenderedDocument refreshes
    /// off-thread via the existing deferred queue.
    /// </summary>
    private void CommitTaskToggleSnapshot(string path, string newContent, int line, bool isChecked)
    {
        var current = _document;
        if (current is null || !string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _document = new MarkdownSource(current.Path, current.FileName, newContent);

        // A dormant EditorSession (edit mode entered earlier, currently reading)
        // still holds the PRE-toggle buffer. Left stale, the next Ctrl+E renders
        // the old text: a value-different render lands on the just-hidden
        // renderer (10s+ hidden-HWND message drain) AND visually reverts the
        // checkbox. Flip the session buffer through the same verified marker
        // path — a diverged line (unsaved edit on it) keeps the user's text;
        // the dirty flow owns it. Either way the session's persisted baseline
        // MUST follow the disk we just wrote: leaving it at the pre-toggle
        // content makes a byte-identical buffer read as dirty, and Discard/Save
        // would silently revert the persisted toggle. ApplyPersistedTaskFlip
        // also skips the session's synchronous whole-document preview rebuild —
        // that parse does not belong on the zero-cost click path.
        if (EditorSession is { } session)
        {
            var flipped = TryFlipMarker(
                session.SourceText,
                line,
                isChecked,
                TaskListIdentity.ComputeKey(newContent.Split('\n')[line]),
                out var sessionText);
            session.ApplyPersistedTaskFlip(flipped ? sessionText : session.SourceText, newContent);
        }

        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(WordCountStatusLabel));
        QueueDeferredRenderedDocument(_document);
        TaskToggleCommitted?.Invoke(this, new TaskToggleCommit(_document, line, isChecked));
    }

    /// <summary>
    /// The checked state the disk ACTUALLY holds at <paramref name="line"/>,
    /// or <paramref name="fallback"/> when the line is not a task marker.
    /// </summary>
    private static bool ReadDiskCheckedState(string content, int line, bool fallback)
    {
        var lines = content.Split('\n');
        if (line < 0 || line >= lines.Length)
        {
            return fallback;
        }

        var match = TaskListIdentity.TaskMarkerPattern.Match(lines[line]);
        return match.Success
            ? !string.Equals(match.Groups[2].Value, " ", StringComparison.Ordinal)
            : fallback;
    }

    /// <summary>
    /// Flip the task marker on <paramref name="line"/> to the requested state,
    /// but only if that line currently IS a task marker in the OPPOSITE state
    /// AND its identity key equals <paramref name="expectedKey"/> (null/missing
    /// key → refuse, fail-closed). EOL-preserving: only the single state char
    /// changes.
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
        // renderer emitted, otherwise the view is stale and writing here would
        // flip the WRONG item.
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
