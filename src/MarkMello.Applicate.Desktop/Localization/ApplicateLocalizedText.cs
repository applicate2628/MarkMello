using MarkMello.Presentation.Localization;

namespace MarkMello.Applicate.Desktop.Localization;

/// <summary>
/// Localized-string access for the fork's code-behind views.
/// </summary>
/// <remarks>
/// <para>
/// The Applicate host builds most of its chrome in code rather than XAML, so it
/// cannot bind to the <c>Localization</c> resource the way the shared
/// <c>MarkMello.Presentation</c> views do. This resolver is the single owner of
/// that lookup: it reads the <see cref="ILocalizationService"/> registered as the
/// <c>Localization</c> application resource and falls back to the supplied
/// English literal when the resource is not reachable yet.
/// </para>
/// <para>
/// The fallback path is load-bearing, not defensive padding: the startup splash
/// (<c>ApplicateModeRevealCoverWindow</c>) is built before the application
/// resources are fully populated, and a missing resource there must degrade to
/// readable English rather than throw or render an empty string.
/// </para>
/// <para>
/// Resolution happens at call time, so a control keeps whatever text it was
/// built with until it is rebuilt. Call sites that must follow a live language
/// switch have to re-invoke this resolver from their own refresh path.
/// </para>
/// </remarks>
internal static class ApplicateLocalizedText
{
    /// <summary>
    /// Resolves <paramref name="resourceKey"/>, or returns
    /// <paramref name="fallback"/> when no localization service is reachable.
    /// </summary>
    internal static string Resolve(string resourceKey, string fallback)
        => TryGetService(out var localization) ? localization[resourceKey] : fallback;

    /// <summary>
    /// Resolves <paramref name="resourceKey"/> as a composite format string and
    /// fills it with <paramref name="args"/>. When no localization service is
    /// reachable, <paramref name="fallback"/> is formatted instead, using the
    /// invariant culture so the degraded path stays deterministic.
    /// </summary>
    internal static string Format(string resourceKey, string fallback, params object?[] args)
        => TryGetService(out var localization)
            ? localization.Format(resourceKey, args)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, fallback, args);

    private static bool TryGetService(out ILocalizationService localization)
    {
        var app = Avalonia.Application.Current;
        if (app is not null
            && app.TryGetResource("Localization", null, out var value)
            && value is ILocalizationService service)
        {
            localization = service;
            return true;
        }

        localization = null!;
        return false;
    }
}
