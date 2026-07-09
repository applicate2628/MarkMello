namespace MarkMello.Applicate.Desktop.Rendering;

public static class ApplicateVirtualizationMode
{
    public const string EnvironmentVariableName = "MARKMELLO_VIRTUALIZATION";

    private static readonly Lazy<bool> _isEnabled = new(ReadCurrent);

    public static bool IsEnabled => _isEnabled.Value;

    internal static bool ReadCurrent()
        => ReadFromEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    internal static bool ReadFromEnvironment(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return false;
        }

        return envValue.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" => true,
            _ => false,
        };
    }
}
