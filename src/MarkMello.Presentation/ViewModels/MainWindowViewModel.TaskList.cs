using System;
using System.Threading.Tasks;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Payload of <see cref="MainWindowViewModel.TaskToggleDomRevertRequested"/>:
/// the single checkbox at <paramref name="Line"/> must be set back to
/// <paramref name="Checked"/> in the rendered DOM of the document at
/// <paramref name="Path"/> (surgical revert — no reload, no scroll motion).
/// <paramref name="Path"/> is the document-identity guard: the host applies
/// the revert only when the addressed surface still shows that document, the
/// same ownership guard the silent source swap carries (a stale in-flight
/// revert must not flip a line on a since-switched document).
/// </summary>
public sealed record TaskToggleRevertRequest(int Line, bool Checked, string Path);

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
/// <para>ONE logic: a VERIFIED successful flip needs zero re-render — the DOM
/// already shows the flipped state, so the in-memory buffer is patched to it as
/// an UNSAVED edit (<see cref="TaskToggleCommitted"/>) and nothing repaints,
/// nothing scrolls; the edit is dirty and Ctrl+S owns the write (no per-edit disk
/// read, no auto-persist, no reload branch). A refused flip (identity no longer
/// matches the in-memory line) gets a surgical single-checkbox DOM revert
/// (<see cref="TaskToggleDomRevertRequested"/>). External-edit reconciliation is
/// deferred to a save-time disk-divergence check (the per-edit disk read that
/// used to detect it was removed with the auto-persist model).</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Raised after a VERIFIED successful reading-mode flip, carrying the patched
    /// in-memory buffer (DOM == buffer == <see cref="Document"/> by construction;
    /// UNSAVED — disk still holds the pre-flip content). The host mirrors it into
    /// the shared WebView surfaces and the open-documents service WITHOUT any
    /// render request, preserving the tab dirty marker.
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
    public Task ToggleTaskLineAsync(int line, bool isChecked, string? expectedKey, TaskToggleOrigin origin)
        => _inDocumentEditCoordinator.ApplyAsync(
            new TaskCheckboxEditKind(_inDocumentEditHost, line, isChecked, expectedKey, origin));

    private void PublishTaskToggleCommitted(TaskToggleCommit commit)
        => TaskToggleCommitted?.Invoke(this, commit);

    private void PublishTaskToggleRevertRequested(TaskToggleRevertRequest revert)
        => TaskToggleDomRevertRequested?.Invoke(this, revert);

    private void PublishEditPreviewTaskToggleCommitted(TaskToggleCommit commit)
        => EditPreviewTaskToggleCommitted?.Invoke(this, commit);

    private void PublishEditPreviewTaskToggleRevertRequested(TaskToggleRevertRequest revert)
        => EditPreviewTaskToggleRevertRequested?.Invoke(this, revert);

    /// <summary>
    /// Reading-mode (Viewer-origin) commit for a verified flip: apply
    /// <paramref name="newBuffer"/> to the lazily-materialized editor session as
    /// an UNSAVED edit (dirty; Ctrl+S owns the write) and patch the
    /// <see cref="Document"/> backing field silently. The DOM already shows this
    /// state and the view dedups renders by value, so publishing through the
    /// Document setter (reference identity) would force a full cold re-render +
    /// scroll reset for nothing. Keeping <c>_document</c> == the session buffer
    /// preserves the in-memory-source invariant every downstream consumer
    /// (edit-enter, health-fix, tab-return, theme re-render) reads. The
    /// native-fallback RenderedDocument refreshes off-thread via the deferred
    /// queue; the typed commit mirrors both shared hosts + open-docs.
    /// </summary>
    private void CommitInPlaceTaskFlip(string newBuffer, int line, bool isChecked)
    {
        var current = _document;
        if (current is null)
        {
            return;
        }

        // Materialize/reuse the single dirty+buffer owner. When no session exists
        // the baseline is the current in-memory content (== the last disk load,
        // since a reading-mode edit never wrote disk), so the flip reads as dirty;
        // the session is preview-deferred to keep the whole-document parse off the
        // click path.
        EnsureInPlaceEditorSession(current);

        _document = new MarkdownSource(current.Path, current.FileName, newBuffer);
        EditorSession!.ApplyInPlaceEditToBuffer(newBuffer);

        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(WordCountStatusLabel));
        QueueDeferredRenderedDocument(_document);
        PublishTaskToggleCommitted(new TaskToggleCommit(_document, line, isChecked));
    }
}
