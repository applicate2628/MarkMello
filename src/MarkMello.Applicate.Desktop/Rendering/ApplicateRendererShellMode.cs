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
        //
        // ADJ-1 (work-items/active/2026-07-25-toc-empty-on-open/design.md §0
        // FACT 1, §14, §15 -- $lead ratified won't-fix, 2026-07-26): the Table
        // of Contents has NEVER worked in legacy mode (envValue forcing this
        // to false). Legacy's QueueRender branch calls _webView.Navigate(...)
        // and sends NO IPC at all, so ensureChromeNodes (the renderer's only
        // producer of headings-updated) never runs and DocumentHeading is
        // never constructed for that mode. This is pre-existing and
        // unreported -- explicit debugging opt-out, shell mode has been the
        // default since Phase 4 -- and is accepted as won't-fix rather than
        // fixed, since legacy mode is not a supported end-user render path.
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
