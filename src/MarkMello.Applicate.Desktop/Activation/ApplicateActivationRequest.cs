namespace MarkMello.Applicate.Desktop.Activation;

internal sealed record ApplicateActivationRequest(bool ShutdownRequested, IReadOnlyList<string> FilePaths)
{
    public static ApplicateActivationRequest Shutdown { get; } =
        new(ShutdownRequested: true, Array.Empty<string>());

    public static ApplicateActivationRequest Open(IReadOnlyList<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        return new ApplicateActivationRequest(ShutdownRequested: false, filePaths);
    }
}
