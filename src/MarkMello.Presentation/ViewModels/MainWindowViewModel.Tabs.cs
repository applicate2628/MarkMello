namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Tab-switch support surface for the desktop tab bridge.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Surface a status message when an activated tab's file could not be
    /// loaded and the bridge bailed out instead of publishing an empty buffer
    /// into the editor (which a later Ctrl+S would write back, truncating the
    /// real file on disk — audit H2). The current editor session is kept.
    /// </summary>
    public void NotifyActiveTabLoadFailed(string fileName)
        => EditorSession?.SetStatusMessage(_localization.Format("TabLoadFailed", fileName));

    /// <summary>
    /// Source line currently at the READING viewport's 38% anchor — recorded
    /// live from the renderer's preview-source-line channel (viewer and
    /// edit-preview surfaces both keep it fresh). The edit-entry seed reads it
    /// so the editor opens at the reading position instead of the document
    /// start. Null = never scrolled (seed 0 = top, correct). Cleared on every
    /// document swap alongside ReadingProgress.
    /// </summary>
    public int? ReadingAnchorSourceLine { get; set; }
}
