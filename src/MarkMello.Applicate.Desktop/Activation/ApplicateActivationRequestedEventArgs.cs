namespace MarkMello.Applicate.Desktop.Activation;

public sealed class ApplicateActivationRequestedEventArgs(
    IReadOnlyList<string> filePaths,
    bool shutdownRequested = false) : EventArgs
{
    public IReadOnlyList<string> FilePaths { get; } = filePaths;

    public bool ShutdownRequested { get; } = shutdownRequested;
}
