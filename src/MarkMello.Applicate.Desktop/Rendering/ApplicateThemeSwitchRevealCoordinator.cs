using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.Services;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Covers the active native WebView document surface during a user theme switch
/// until the renderer confirms the matching theme has painted.
/// </summary>
internal sealed class ApplicateThemeSwitchRevealCoordinator : IDisposable
{
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(2);

    private readonly Control _coverHost;
    private readonly IApplicateSharedWebViewHost _host;
    private readonly MainWindowViewModel _viewModel;
    private readonly Func<bool> _isActiveSurface;
    private readonly ApplicateModeRevealCoverWindow _cover = new();

    private string? _targetTheme;
    private ThemeVariant? _targetThemeVariant;
    private long _targetRequestId;
    private long _coverGeneration;
    private bool _covered;
    private bool _pendingShowOnBounds;
    private DispatcherTimer? _fallbackTimer;
    private bool _disposed;

    public ApplicateThemeSwitchRevealCoordinator(
        Control coverHost,
        IApplicateSharedWebViewHost host,
        MainWindowViewModel viewModel,
        Func<bool> isActiveSurface)
    {
        _coverHost = coverHost ?? throw new ArgumentNullException(nameof(coverHost));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _isActiveSurface = isActiveSurface ?? throw new ArgumentNullException(nameof(isActiveSurface));

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ThemeTransitionStarting += OnThemeTransitionStarting;
        _host.RendererFailed += OnRendererFailed;
        _host.View.ThemeChangeSent += OnThemeChangeSent;
        _host.View.ThemeApplied += OnThemeApplied;
    }

    private void OnThemeTransitionStarting(object? sender, ThemeTransitionStartingEventArgs e)
    {
        if (_disposed
            || _viewModel.Document is null
            || !_isActiveSurface()
            || !_host.View.HasLoadedDocumentForSource(_viewModel.Document))
        {
            return;
        }

        BeginCoverSession(ToRendererTheme(e.TargetEffectiveTheme), ToThemeVariant(e.TargetEffectiveTheme));
        ShowCover();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_covered && !_pendingShowOnBounds)
        {
            return;
        }

        if (!_isActiveSurface())
        {
            HideCover();
        }
    }

    private void OnThemeChangeSent(object? sender, ApplicateWebThemeChangeSentEventArgs e)
    {
        if ((_covered || _pendingShowOnBounds)
            && string.Equals(e.Theme, _targetTheme, StringComparison.Ordinal))
        {
            _targetRequestId = e.RequestId;
            ApplicateTrace.DiagMs(
                "pane-seq",
                "theme-cover-awaiting-renderer",
                $"theme={e.Theme} requestId={e.RequestId}");
        }
    }

    private void OnThemeApplied(object? sender, ApplicateWebThemeAppliedEventArgs e)
    {
        if (!_covered && !_pendingShowOnBounds)
        {
            return;
        }

        if (e.RequestId != _targetRequestId
            || !string.Equals(e.Theme, _targetTheme, StringComparison.Ordinal))
        {
            ApplicateTrace.DiagMs(
                "pane-seq",
                "theme-cover-stale-ack",
                $"theme={e.Theme} requestId={e.RequestId} targetTheme={_targetTheme ?? "(null)"} targetRequestId={_targetRequestId}");
            return;
        }

        HideCoverAfterPaint();
    }

    private void OnRendererFailed(object? sender, ApplicateRendererFailureEvent e)
        => HideCover();

    private void BeginCoverSession(string targetTheme, ThemeVariant targetThemeVariant)
    {
        _coverGeneration++;
        _targetTheme = targetTheme;
        _targetThemeVariant = targetThemeVariant;
        _targetRequestId = 0;
    }

    private void ShowCover()
    {
        if (_disposed)
        {
            return;
        }

        if (_covered && _cover.UpdateBrush(_coverHost, _targetThemeVariant))
        {
            RestartFallback();
            ApplicateTrace.DiagMs(
                "pane-seq",
                "theme-cover-retargeted",
                $"theme={_targetTheme ?? "(null)"}");
            return;
        }

        if (_cover.Show(_coverHost, _targetThemeVariant))
        {
            _covered = true;
            _pendingShowOnBounds = false;
            RestartFallback();
            ApplicateTrace.DiagMs(
                "pane-seq",
                "theme-cover-shown",
                $"theme={_targetTheme ?? "(null)"}");
            return;
        }

        if (!_pendingShowOnBounds)
        {
            _pendingShowOnBounds = true;
            _coverHost.LayoutUpdated += OnCoverHostLayoutUpdated;
            ApplicateTrace.DiagMs("pane-seq", "theme-cover-deferred", "reason=no-bounds");
        }
    }

    private void OnCoverHostLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed || !_pendingShowOnBounds)
        {
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
            return;
        }

        if (_coverHost.Bounds.Width <= 1 || _coverHost.Bounds.Height <= 1)
        {
            return;
        }

        _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        _pendingShowOnBounds = false;
        ShowCover();
    }

    private void HideCoverAfterPaint()
    {
        if (_disposed || !_covered)
        {
            return;
        }

        var generation = _coverGeneration;
        var topLevel = TopLevel.GetTopLevel(_coverHost);
        if (topLevel is null)
        {
            Dispatcher.UIThread.Post(() => HideCoverForGeneration(generation), DispatcherPriority.Background);
            return;
        }

        topLevel.RequestAnimationFrame(_ =>
        {
            if (_disposed || generation != _coverGeneration)
            {
                return;
            }

            topLevel.RequestAnimationFrame(_ => HideCoverForGeneration(generation));
        });
    }

    private void HideCoverForGeneration(long generation)
    {
        if (_disposed || generation != _coverGeneration)
        {
            return;
        }

        HideCover(animated: true);
    }

    private void HideCover(bool animated = false)
    {
        ReleaseFallback();
        if (_pendingShowOnBounds)
        {
            _pendingShowOnBounds = false;
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        }
        if (!_covered)
        {
            return;
        }

        _covered = false;
        _targetTheme = null;
        _targetThemeVariant = null;
        _targetRequestId = 0;
        var duration = animated
            ? ApplicateMotion.ModeSwitchDuration(_viewModel.ReadingPreferences)
            : TimeSpan.Zero;
        _cover.Hide(duration);
        ApplicateTrace.DiagMs("pane-seq", "theme-cover-hidden");
    }

    private void RestartFallback()
    {
        ReleaseFallback();
        _fallbackTimer = new DispatcherTimer { Interval = FallbackTimeout };
        _fallbackTimer.Tick += OnFallbackTick;
        _fallbackTimer.Start();
    }

    private void OnFallbackTick(object? sender, EventArgs e)
    {
        ApplicateTrace.DiagMs("pane-seq", "theme-cover-fallback");
        HideCover();
    }

    private void ReleaseFallback()
    {
        if (_fallbackTimer is null)
        {
            return;
        }

        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= OnFallbackTick;
        _fallbackTimer = null;
    }

    private static ThemeVariant ToThemeVariant(ThemeMode theme)
        => theme switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.ClassicWhite => AvaloniaThemeService.ClassicWhiteThemeVariant,
            _ => ThemeVariant.Light
        };

    private static string ToRendererTheme(ThemeMode theme)
        => theme switch
        {
            ThemeMode.Dark => "dark",
            ThemeMode.ClassicWhite => "classic-white",
            _ => "light"
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ThemeTransitionStarting -= OnThemeTransitionStarting;
        _host.RendererFailed -= OnRendererFailed;
        _host.View.ThemeChangeSent -= OnThemeChangeSent;
        _host.View.ThemeApplied -= OnThemeApplied;
        if (_pendingShowOnBounds)
        {
            _coverHost.LayoutUpdated -= OnCoverHostLayoutUpdated;
        }
        ReleaseFallback();
        _cover.Dispose();
    }
}
