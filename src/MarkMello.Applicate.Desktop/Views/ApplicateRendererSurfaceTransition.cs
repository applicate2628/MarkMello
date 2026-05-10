using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MarkMello.Applicate.Desktop.Views;

internal enum ApplicateRendererSurfaceKind
{
    Native,
    WebView
}

internal readonly record struct ApplicateRendererSurfaceVisualState(
    bool IsVisible,
    bool IsHitTestVisible,
    double Opacity);

internal static class ApplicateRendererSurfaceTransition
{
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);

    public static ApplicateRendererSurfaceVisualState CalculateVisualState(
        ApplicateRendererSurfaceKind surface,
        ApplicateRendererSurfaceKind active,
        ApplicateRendererSurfaceKind? pending,
        bool pendingReady)
    {
        if (pending is null)
        {
            var isActive = surface == active;
            return new ApplicateRendererSurfaceVisualState(isActive, isActive, isActive ? 1 : 0);
        }

        if (surface == pending.Value)
        {
            return new ApplicateRendererSurfaceVisualState(
                IsVisible: true,
                IsHitTestVisible: pendingReady,
                Opacity: pendingReady ? 1 : 0);
        }

        if (surface == active)
        {
            return new ApplicateRendererSurfaceVisualState(
                IsVisible: true,
                IsHitTestVisible: !pendingReady,
                Opacity: pendingReady ? 0 : 1);
        }

        return new ApplicateRendererSurfaceVisualState(false, false, 0);
    }

    public static void EnsureOpacityTransition(Control control)
    {
        if (control.Transitions is not null)
        {
            return;
        }

        control.Opacity = 0;
        control.Transitions =
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = FadeDuration
            }
        ];
    }

    public static void ApplyVisualState(Control control, ApplicateRendererSurfaceVisualState state)
    {
        control.IsVisible = state.IsVisible;
        control.IsHitTestVisible = state.IsHitTestVisible;
        control.Opacity = state.Opacity;
    }
}
