using System;
using Avalonia.Threading;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Background periodic update check + the unobtrusive header "update available"
/// notice. The one-shot startup check (<see cref="BeginStartupUpdateCheck"/>)
/// runs once at launch; this partial adds a recurring background check so a new
/// release surfaces while the user keeps the app open, plus a lightweight
/// title-bar text notice that is visible while a document is open (the existing
/// top-level banner is welcome-screen-only).
/// </summary>
public partial class MainWindowViewModel
{
    // Re-check GitHub Releases every few minutes. 5 min = 12 checks/hour, well
    // within the unauthenticated GitHub rate limit (~60/hour/IP) for one user.
    private static readonly TimeSpan PeriodicUpdateCheckInterval = TimeSpan.FromMinutes(5);

    private DispatcherTimer? _periodicUpdateCheckTimer;

    /// <summary>
    /// Unobtrusive header notice visibility: true whenever an update is
    /// available or downloaded-and-ready. Deliberately independent of
    /// <see cref="CanShowTopLevelUpdateNotification"/> (which restricts the big
    /// banner to the welcome screen), so the lightweight text hint also shows
    /// while a document is open — the common case for "a new version appeared".
    /// </summary>
    public bool IsHeaderUpdateNoticeVisible
        => _updateStatus is UpdateStatusSnapshot.UpdateAvailableState
            or UpdateStatusSnapshot.DownloadReadyState;

    /// <summary>
    /// The header notice text — a short, fits-in-the-title-bar label
    /// ("Update available!" / "Доступно обновление!"). The concrete version is
    /// shown in the updates panel opened on click. Empty when no update is known.
    /// </summary>
    public string HeaderUpdateNoticeText
        => _availableUpdatePackage is not null
            ? _localization["HeaderUpdateAvailable"]
            : string.Empty;

    public string HeaderUpdateNoticeTooltip => _localization["HeaderUpdateNoticeTooltip"];

    /// <summary>
    /// Start the recurring background update check. Idempotent: the timer is
    /// created at most once. Call after the window is shown; pairs with
    /// <see cref="StopPeriodicUpdateChecks"/> on close.
    /// </summary>
    public void StartPeriodicUpdateChecks()
    {
        if (_periodicUpdateCheckTimer is not null)
        {
            return;
        }

        _periodicUpdateCheckTimer = new DispatcherTimer { Interval = PeriodicUpdateCheckInterval };
        _periodicUpdateCheckTimer.Tick += OnPeriodicUpdateCheckTick;
        _periodicUpdateCheckTimer.Start();
    }

    /// <summary>
    /// Stop the recurring check and release the timer. Safe to call when it was
    /// never started.
    /// </summary>
    public void StopPeriodicUpdateChecks()
    {
        if (_periodicUpdateCheckTimer is null)
        {
            return;
        }

        _periodicUpdateCheckTimer.Stop();
        _periodicUpdateCheckTimer.Tick -= OnPeriodicUpdateCheckTick;
        _periodicUpdateCheckTimer = null;
    }

    private void OnPeriodicUpdateCheckTick(object? sender, EventArgs e) => BeginPeriodicUpdateCheck();

    private void BeginPeriodicUpdateCheck()
    {
        // Skip the tick if a check or download is already in flight — mirrors
        // BeginStartupUpdateCheck's guard so a slow/hung request can never let
        // overlapping checks pile up, and so we never re-check mid-download.
        if (!CanCheckForUpdates)
        {
            return;
        }

        // Reuse the silent startup path (no busy spinner, immediate reveal).
        _ = CheckForUpdatesForStartupAsync();
    }
}
