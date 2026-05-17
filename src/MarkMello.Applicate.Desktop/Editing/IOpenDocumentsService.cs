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

    void Activate(OpenDocument document);

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
}
