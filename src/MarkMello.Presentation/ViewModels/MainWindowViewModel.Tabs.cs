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
}
