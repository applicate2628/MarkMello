namespace MarkMello.Applicate.Desktop.Rendering;

// FAILED EXPERIMENT — DO NOT ENABLE, DO NOT REVISIT (marked 2026-07-13).
// Document virtualization (flag-ON, MARKMELLO_VIRTUALIZATION=1) is abandoned.
// Fatal, STRUCTURAL defect: cumulative deep-scroll-restore drift. Restoring a
// deep scroll position after a tab switch lands ~1384px off and COMPOUNDS
// (runtime-measured runningDelta=9137px). Root: virtualization estimates the
// heights of un-realized sections, so the exact cumulative height above a deep
// anchor is unknowable and the restore error feeds back through the next store.
// This is incompatible with pixel-exact scroll restoration; the shipped
// flag-OFF path (whole document in DOM) has no such drift. Freezes also
// undercut the perf rationale. Keep this flag DEFAULT OFF permanently.
// Full record: VIRTUALIZATION-EXPERIMENT-FAILED.md at the repo root.
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
