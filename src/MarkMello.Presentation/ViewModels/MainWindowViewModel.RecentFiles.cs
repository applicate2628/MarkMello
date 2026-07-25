using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// D11 Recent files. The host (ApplicateMainWindow) owns the persisted recent-path list
/// (ApplicateSession.RecentPaths) and pushes it here via <see cref="SetRecentFiles"/>; this partial
/// exposes it for the welcome screen and opens a chosen entry. Missing files are pruned at set time
/// (a temporarily-unmounted path should not be evicted from storage, only hidden here).
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentFiles))]
    private ObservableCollection<RecentFileItem> _recentFiles = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>
    /// True when the host's persisted recent-path list had ANY entry on the last
    /// <see cref="SetRecentFiles"/> push, regardless of whether that entry is currently available
    /// on disk. Distinct from <see cref="HasRecentFiles"/> (the display-pruned subset): gates
    /// section/row VISIBILITY so the clear affordance stays reachable even when every stored path
    /// is temporarily unavailable
    /// (work-items/bugs/2026-07-26-recent-clear-unreachable-when-all-paths-unavailable.md).
    /// Derived from the <c>paths</c> argument <see cref="SetRecentFiles"/> already receives -- the
    /// host's existing mirror push already carries the full stored list before display-pruning --
    /// never by having the VM read storage itself (ownership decision d11).
    /// </summary>
    [ObservableProperty]
    private bool _hasStoredRecentFiles;

    public string RecentFilesHeader => _localization["WelcomeRecentHeader"];

    /// <summary>Intent-only: forget one entry. The host (subscribed) owns removal + persist.</summary>
    public event EventHandler<string>? RecentFileRemoveRequested;

    /// <summary>Intent-only: forget every entry. Same contract as <see cref="RecentFileRemoveRequested"/>.</summary>
    public event EventHandler? RecentFilesClearRequested;

    /// <summary>
    /// Replace the displayed recent list from the host's persisted paths (already most-recent-first,
    /// deduplicated). Entries whose file no longer exists are dropped from the DISPLAY only.
    /// </summary>
    public void SetRecentFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        HasStoredRecentFiles = paths.Count > 0;

        var items = new List<RecentFileItem>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            items.Add(new RecentFileItem(
                path,
                Path.GetFileName(path),
                Path.GetDirectoryName(path) ?? string.Empty));
        }

        RecentFiles = new ObservableCollection<RecentFileItem>(items);

        // F9: the Recent cascade has nothing left to show in its row list once the mirrored
        // display goes empty -- fall back to the parent app-menu column instead of closing the
        // whole menu (settled UX: remove/clear never close the menu). This is a UX preference,
        // not a reachability requirement: the clear button stays functional either way (it acts
        // on stored paths, not the display), and the AppMenu row that reopens this cascade is
        // gated on HasStoredRecentFiles, which only turns false once storage itself is empty too
        // -- see work-items/bugs/2026-07-26-recent-clear-unreachable-when-all-paths-unavailable.md.
        // Guarded on the cascade actually being open so this never fires for the welcome screen or
        // any other overlay state.
        if (RecentFiles.Count == 0 && ShellOverlay == ShellOverlayKind.AppRecent)
        {
            ShellOverlay = ShellOverlayKind.AppMenu;
        }
    }

    [RelayCommand]
    private async Task OpenRecentFileAsync(string? path)
    {
        CloseOverlayCore();

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenPathAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private void RemoveRecentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        RecentFileRemoveRequested?.Invoke(this, path);
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        RecentFilesClearRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the Recent sub-column beside the app menu, mirroring <c>OpenAppExport</c>
    /// (<c>MainWindowViewModel.Export.cs</c>). The menu row that invokes this is itself
    /// gated on <see cref="HasStoredRecentFiles"/>, so there is no separate CanExecute guard
    /// here -- matching the unconditional Settings/Updates/About menu entries rather than
    /// Export's document-dependent one.
    /// </summary>
    [RelayCommand]
    private void OpenAppRecent()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();
        ShellOverlay = ToggleAppOverlayPanel(ShellOverlayKind.AppRecent, ShellOverlayKind.AppMenu);
    }
}

/// <summary>One recent-file row: the full path (used to re-open) plus display parts.</summary>
public sealed record RecentFileItem(string Path, string FileName, string DirectoryLabel);
