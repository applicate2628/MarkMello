using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace MarkMello.Applicate.Desktop.Editing;

public sealed class ActiveDocumentChangedEventArgs(OpenDocument? activeDocument) : EventArgs
{
    public OpenDocument? ActiveDocument { get; } = activeDocument;
}

/// <summary>
/// Tracks all open markdown documents within a single MarkMello session
/// and exposes the currently active one. UI tabs strip and welcome screen
/// observe <see cref="OpenDocuments"/> and <see cref="ActiveDocumentChanged"/>;
/// the file-open command, command-line arguments, drop handler, and tab
/// activation calls all flow through this single service so the model
/// stays the source of truth.
/// </summary>
public interface IOpenDocumentsService
{
    ReadOnlyObservableCollection<OpenDocument> OpenDocuments { get; }

    OpenDocument? ActiveDocument { get; }

    event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    /// <summary>
    /// Open a markdown file and add it to <see cref="OpenDocuments"/>. By
    /// default the newly opened document also becomes <see cref="ActiveDocument"/>.
    /// Pass <paramref name="activate"/> = false for batch restore scenarios where
    /// the caller will pick the final active document explicitly after the loop.
    /// </summary>
    Task<OpenDocument> OpenAsync(string filePath, bool activate = true);

    /// <summary>
    /// Add a path to <see cref="OpenDocuments"/> as a STUB tab — the file
    /// is not read, <see cref="OpenDocument.SourceText"/> stays empty,
    /// and <see cref="OpenDocument.IsLoaded"/> is false. Used by
    /// session-restore for non-active tabs so cold startup does not pay
    /// per-tab File I/O cost. The stub materializes on first activation
    /// via <see cref="EnsureLoadedAsync"/>. Returns the existing
    /// <see cref="OpenDocument"/> when the path is already known (loaded
    /// or stub); does NOT change <see cref="ActiveDocument"/>.
    /// </summary>
    Task<OpenDocument> OpenStubAsync(string filePath);

    /// <summary>
    /// Ensure <paramref name="document"/>'s contents are loaded into
    /// <see cref="OpenDocument.SourceText"/>. No-op when already loaded.
    /// On miss, reads from the early-document cache first (deposited by
    /// <c>Program.Main</c> for known session paths) and falls back to a
    /// synchronous-on-thread-pool File.ReadAllText. Flips
    /// <see cref="OpenDocument.IsLoaded"/> to true on success. Used by
    /// the active-document bridge before any code path that needs the
    /// text (edit-mode in-place apply, cross-source content match).
    /// </summary>
    Task EnsureLoadedAsync(OpenDocument document);

    void Activate(OpenDocument document);

    /// <summary>
    /// Clear the active document (no open file is active) WITHOUT closing
    /// anything — used when a session-only untitled document owns the window, so
    /// the tabs strip shows no highlighted file tab. Fires
    /// <see cref="ActiveDocumentChanged"/> with a null document; no-op when the
    /// active document is already null.
    /// </summary>
    void ClearActive();

    void Close(OpenDocument document);

    /// <summary>
    /// Moves <paramref name="document"/> to a new position in <see cref="OpenDocuments"/>.
    /// Out-of-range targets are clamped into the valid index range. No-op when the
    /// document is already at <paramref name="newIndex"/>. The active document
    /// reference is preserved.
    /// </summary>
    void Move(OpenDocument document, int newIndex);

    void UpdateState(OpenDocument document, int caret, double scrollProgressPercent);

    void UpdateSourceText(OpenDocument document, string sourceText);

    /// <summary>
    /// Set the per-document modified (dirty) flag and notify observers (the tabs
    /// strip, which paints a dirty marker). No-op and no event when unchanged.
    /// </summary>
    void SetModified(OpenDocument document, bool modified);

    /// <summary>
    /// Raised when an open document's <see cref="OpenDocument.IsModified"/>
    /// flag changes, so the tabs strip can refresh its dirty markers live.
    /// </summary>
    event EventHandler? DocumentModifiedChanged;
}
