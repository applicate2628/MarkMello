namespace MarkMello.Presentation.Views;

public interface ISourceLineScrollSyncPreview
{
    event EventHandler? SourceLineScrollSyncPreviewRendered;

    event EventHandler<SourceLineScrollSyncEventArgs>? PreviewSourceLineChanged;

    /// <summary>
    /// Whether the line-based scroll sync is enabled (the preview's sync
    /// toggle). The sync loop's owner checks this before forwarding either
    /// direction.
    /// </summary>
    bool SyncEnabled { get; }

    void ScrollToSourceLine(int sourceLine);
}

public sealed class SourceLineScrollSyncEventArgs(int sourceLine) : EventArgs
{
    public int SourceLine { get; } = sourceLine;
}
