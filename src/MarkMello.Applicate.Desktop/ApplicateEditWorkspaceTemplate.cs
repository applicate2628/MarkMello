// REMOVED in 2026-05-16 permanent-mount refactor.
//
// Previously matched on `EditorSessionViewModel` and built EditWorkspaceView
// + ApplicateEditPreviewView per-session — every Ctrl+E recreated the pair
// and forced a Win32 SetParent on the shared WebView2 HWND, producing the
// 154ms HWND-geometry-lag class of bug (verified [hwnd-probe] at 15:24:17).
//
// The build path now lives directly in ApplicateMainWindow.InstallSibling-
// MountedViews — one pre-built instance, DataContext-driven session swap,
// no template re-materialization, no reparent on toggle.
//
// File kept as an empty stub for git history continuity; remove on next
// merge sweep.

namespace MarkMello.Applicate.Desktop;

internal static class ApplicateEditWorkspaceTemplate
{
}
