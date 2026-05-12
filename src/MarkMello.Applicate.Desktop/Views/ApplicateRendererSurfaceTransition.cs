using Avalonia;
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
    double Opacity,
    int ZIndex,
    double TranslateX);

internal static class ApplicateRendererSurfaceTransition
{
    private const double OffscreenParkingOffset = -100000;

    public static readonly TimeSpan FadeDuration = TimeSpan.Zero;

    public static ApplicateRendererSurfaceVisualState CalculateVisualState(
        ApplicateRendererSurfaceKind surface,
        ApplicateRendererSurfaceKind active,
        ApplicateRendererSurfaceKind? pending,
        bool pendingReady)
    {
        if (pending is null)
        {
            var isActive = surface == active;
            return new ApplicateRendererSurfaceVisualState(
                isActive,
                isActive,
                isActive ? 1 : 0,
                isActive ? 2 : 0,
                0);
        }

        if (surface == pending.Value)
        {
            return new ApplicateRendererSurfaceVisualState(
                IsVisible: true,
                IsHitTestVisible: pendingReady,
                Opacity: pendingReady ? 1 : 0,
                ZIndex: pendingReady ? 3 : 0,
                TranslateX: pendingReady ? 0 : OffscreenParkingOffset);
        }

        if (surface == active)
        {
            return new ApplicateRendererSurfaceVisualState(
                IsVisible: true,
                IsHitTestVisible: !pendingReady,
                Opacity: pendingReady ? 0 : 1,
                ZIndex: pendingReady ? 1 : 2,
                TranslateX: 0);
        }

        return new ApplicateRendererSurfaceVisualState(false, false, 0, 0, 0);
    }

    public static void EnsureOpacityTransition(Control control)
    {
        control.Opacity = 0;
        control.Transitions = null;
    }

    public static void ApplyVisualState(Control control, ApplicateRendererSurfaceVisualState state)
    {
        control.IsVisible = state.IsVisible;
        control.IsHitTestVisible = state.IsHitTestVisible;
        control.Opacity = state.Opacity;
        control.SetValue(Panel.ZIndexProperty, state.ZIndex);
        control.RenderTransform = state.TranslateX == 0 ? null : new TranslateTransform(state.TranslateX, 0);
    }
}
