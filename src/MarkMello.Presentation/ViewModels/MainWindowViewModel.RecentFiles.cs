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

    public string RecentFilesHeader => _localization["WelcomeRecentHeader"];

    /// <summary>
    /// Replace the displayed recent list from the host's persisted paths (already most-recent-first,
    /// deduplicated). Entries whose file no longer exists are dropped from the DISPLAY only.
    /// </summary>
    public void SetRecentFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

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
    }

    [RelayCommand]
    private async Task OpenRecentFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await OpenPathAsync(path).ConfigureAwait(true);
    }
}

/// <summary>One recent-file row: the full path (used to re-open) plus display parts.</summary>
public sealed record RecentFileItem(string Path, string FileName, string DirectoryLabel);
