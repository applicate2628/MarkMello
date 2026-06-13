using System.Threading;
using System.Threading.Tasks;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Settings;

/// <summary>
/// Applicate-side decorator over <see cref="ISettingsStore"/> that coerces
/// <see cref="ReadingPreferences.RendererBackend"/> from
/// <see cref="MarkdownRendererBackend.Native"/> to
/// <see cref="MarkdownRendererBackend.WebView"/> at both the load and save
/// boundaries.
///
/// Why this exists (design D8 + plan Phase 3):
/// existing Applicate users may have a settings file written before the
/// Applicate fork pinned the renderer to WebView. Those files persist
/// <c>"RendererBackend": "Native"</c>, but the Applicate-side native renderer
/// is removed in Phase 5; an unconverted disk value would otherwise drive the
/// renderer-toggle and `InstallApplicateRendererPolicy` paths in surprising
/// ways. Coercing at the store boundary keeps every downstream consumer (view
/// models, prewarm pipeline, settings panel) on a single normalized value.
///
/// Why coerce on Save as well as Load:
/// the decorator wraps the inner store rather than the JSON file directly, so
/// other consumers (tests, future Applicate features) could call
/// <see cref="ISettingsStore.SavePreferencesAsync"/> through this interface
/// with a stale <see cref="MarkdownRendererBackend.Native"/> value. Coercing
/// on save means the next disk write persists
/// <see cref="MarkdownRendererBackend.WebView"/> too, which avoids re-running
/// the load-side coercion every session and keeps the on-disk JSON consistent
/// with what the rest of Applicate is seeing in memory. All other preference
/// fields (font size, line height, content width, minimap mode, width-resizer
/// visibility, light-palette mode) pass through untouched.
///
/// All other <see cref="ISettingsStore"/> members (theme, language, window
/// placement) forward directly to the inner store. The decorator does not own
/// any state of its own.
/// </summary>
public sealed class ApplicateRendererCoercingSettingsStore : ISettingsStore
{
    private readonly ISettingsStore _inner;

    public ApplicateRendererCoercingSettingsStore(ISettingsStore inner)
    {
        System.ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public async ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        var preferences = await _inner.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
        return CoerceRendererBackend(preferences);
    }

    public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
    {
        var coerced = CoerceRendererBackend(preferences);
        return _inner.SavePreferencesAsync(coerced, cancellationToken);
    }

    public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
        => _inner.LoadThemeAsync(cancellationToken);

    public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
        => _inner.SaveThemeAsync(theme, cancellationToken);

    public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
        => _inner.LoadLanguageAsync(cancellationToken);

    public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
        => _inner.SaveLanguageAsync(language, cancellationToken);

    public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
        => _inner.LoadWindowPlacementAsync(cancellationToken);

    public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken cancellationToken = default)
        => _inner.SaveWindowPlacementAsync(placement, cancellationToken);

    public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        => _inner.ResetAsync(cancellationToken);

    /// <summary>
    /// Returns <paramref name="preferences"/> unchanged when
    /// <see cref="ReadingPreferences.RendererBackend"/> is already
    /// <see cref="MarkdownRendererBackend.WebView"/> (the common case). When
    /// the backend is <see cref="MarkdownRendererBackend.Native"/>, returns a
    /// copy with <c>RendererBackend = WebView</c> and every other field
    /// preserved via the record's <c>with</c> expression.
    /// </summary>
    private static ReadingPreferences CoerceRendererBackend(ReadingPreferences preferences)
    {
        if (preferences.RendererBackend == MarkdownRendererBackend.Native)
        {
            return preferences with { RendererBackend = MarkdownRendererBackend.WebView };
        }

        return preferences;
    }
}
