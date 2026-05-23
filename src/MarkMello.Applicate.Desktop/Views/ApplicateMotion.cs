using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Code-only access to the app's motion design tokens.
/// Single source of truth is <c>Themes/Motion.axaml</c>; this helper
/// reads from <see cref="Application.Resources"/> so XAML and C# always
/// agree on timing/easing. Fallbacks are baked in for unit-test
/// scenarios where the resource dictionary is not loaded.
///
/// Tokens are semantic, not literal:
///   <see cref="Fast"/>     — hover/press/quick feedback
///   <see cref="Standard"/> — popup fade, mode toggle, opacity transitions
///   <see cref="Slow"/>     — large panel transitions (settings, about)
///   <see cref="Easing"/>   — shared decelerating curve for appearing content
///
/// Retune the entire app's pacing by editing the four values in Motion.axaml.
/// </summary>
internal static class ApplicateMotion
{
    public static TimeSpan Fast => Resolve("MmDurationFast", TimeSpan.FromMilliseconds(120));

    public static TimeSpan Standard => Resolve("MmDurationStandard", TimeSpan.FromMilliseconds(180));

    public static TimeSpan Slow => Resolve("MmDurationSlow", TimeSpan.FromMilliseconds(280));

    public static Easing Easing => ResolveEasing("MmEasingStandard");

    public static TimeSpan ModeSwitchDuration(ReadingPreferences preferences)
    {
        var normalized = ReadingPreferences.Normalize(preferences);
        return normalized.ModeSwitchSmoothEnabled
            ? TimeSpan.FromMilliseconds(normalized.ModeSwitchSmoothDurationMs)
            : TimeSpan.Zero;
    }

    private static TimeSpan Resolve(string key, TimeSpan fallback)
    {
        if (Avalonia.Application.Current is { } app
            && app.TryGetResource(key, ThemeVariant.Default, out var value)
            && value is TimeSpan ts)
        {
            return ts;
        }
        return fallback;
    }

    private static Easing ResolveEasing(string key)
    {
        if (Avalonia.Application.Current is { } app
            && app.TryGetResource(key, ThemeVariant.Default, out var value)
            && value is Easing fromResources)
        {
            return fromResources;
        }
        return new CubicEaseOut();
    }
}
