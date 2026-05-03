namespace MarkMello.Application.Abstractions;

/// <summary>
/// Diagnostic startup mode used by CI to launch the real desktop application,
/// wait until the first window initializes, and terminate with a deterministic exit code.
/// Disabled by default and activated only through explicit command-line arguments or environment variables.
/// </summary>
public sealed record StartupSmokeTestOptions(bool IsEnabled, TimeSpan ExitAfterOpenDelay)
{
    public static StartupSmokeTestOptions Disabled { get; } = new(false, TimeSpan.Zero);
}
