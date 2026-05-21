namespace MarkMello.Presentation.Views;

public interface ISourceLineScrollSyncPreview
{
    event EventHandler? SourceLineScrollSyncPreviewRendered;

    event EventHandler<SourceLineScrollSyncEventArgs>? PreviewSourceLineChanged;

    void ScrollToSourceLine(int sourceLine);
}

public sealed class SourceLineScrollSyncEventArgs(int sourceLine) : EventArgs
{
    public int SourceLine { get; } = sourceLine;
}
