namespace MarkMello.Applicate.Desktop.Rendering;

public static class ApplicateRendererShellMode
{
    public const string EnvironmentVariableName = "MARKMELLO_RENDERER_SHELL_MODE";

    private static readonly Lazy<bool> _isEnabled = new(ReadCurrent);

    public static bool IsEnabled => _isEnabled.Value;

    internal static bool ReadCurrent()
        => ReadFromEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    internal static bool ReadFromEnvironment(string? envValue)
    {
        // Post-Phase 4: shell-mode is the default render path. The two
        // historical shell-mode bugs (viewer-initial empty render + minimap
        // rebuild miss, see project_phase2-shellmode-bugs.md) were caused by
        // a second ApplicateWebMarkdownDocumentView instance racing the
        // shared host — Phase 4 collapsed both consumers to the single
        // shared instance, so the race is structurally impossible now.
        // Legacy Navigate path (writes a fresh document-<guid>.html per
        // switch and tears down the WebView2 DOM) is the source of the
        // "white frame" between documents (perf-audit F-02). Shell mode
        // swaps content via main.innerHTML through a load-document IPC
        // message, keeping the WebView2 HWND painting the previous frame
        // through the swap — no Navigate, no white backdrop, no per-switch
        // HTML write.
        //
        // Default: TRUE. To force legacy mode for a debugging session set
        // MARKMELLO_RENDERER_SHELL_MODE=0 (or "false"/"no"/"off").
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
