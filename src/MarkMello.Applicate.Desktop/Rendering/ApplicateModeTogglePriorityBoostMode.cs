namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Opt-out gate for the mode-toggle renderer priority-boost WORKAROUND.
///
/// <para>WORKAROUND, not a fix. The mode-toggle ~25 s freeze root cause is
/// Chromium's teardown/demotion of the hidden renderer to the Idle priority
/// class under heavy ambient CPU load, which starves the incoming renderer of
/// a timeslice while the host waits on its settle ACK. That root is unfixed;
/// all four clean app-side levers were exhausted
/// (<c>work-items/bugs/2026-07-21-mode-toggle-circular-hidden-renderer-wait.md</c>).
/// This gate turns on a bounded, event-driven <c>SetPriorityClass</c> bump of
/// the app's renderer children for the duration of a mode-toggle reveal-wait
/// window, restored on every exit path.</para>
///
/// <para>Default: ENABLED (the user selected this WORKAROUND as the shipped
/// response). The opt-out is retained because the feature deliberately changes
/// OS scheduling preference and uses native process introspection. Set
/// <c>MARKMELLO_MODE_TOGGLE_PRIORITY_BOOST=0</c> (or <c>false</c>/<c>no</c>/
/// <c>off</c>) to restore the shipped unbumped reveal.</para>
///
/// <para>Parsed once at composition, mirroring the directly testable precedent
/// in <see cref="ApplicateRendererShellMode"/>. Config is resolved at the
/// composition root (the main window) and the resolved scope is injected down;
/// no lower module reads this gate.</para>
/// </summary>
public static class ApplicateModeTogglePriorityBoostMode
{
    public const string EnvironmentVariableName = "MARKMELLO_MODE_TOGGLE_PRIORITY_BOOST";

    private static readonly Lazy<bool> _isEnabled = new(ReadCurrent);

    public static bool IsEnabled => _isEnabled.Value;

    internal static bool ReadCurrent()
        => ReadFromEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    internal static bool ReadFromEnvironment(string? envValue)
    {
        // Default: TRUE. Only the four explicit false forms disable; unknown
        // non-empty values stay enabled.
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return true;
        }

        return envValue.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "no" or "off" => false,
            _ => true,
        };
    }
}
