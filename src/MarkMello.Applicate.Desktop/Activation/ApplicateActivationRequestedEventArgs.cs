namespace MarkMello.Applicate.Desktop.Activation;

public sealed class ApplicateActivationRequestedEventArgs(IReadOnlyList<string> filePaths) : EventArgs
{
    public IReadOnlyList<string> FilePaths { get; } = filePaths;
}
