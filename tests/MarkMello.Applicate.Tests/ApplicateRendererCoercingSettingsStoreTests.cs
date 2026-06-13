using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Settings;
using MarkMello.Domain;
using MarkMello.Infrastructure.Settings;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Tests for the Phase 3 design D8 decorator. The decorator wraps an
/// <see cref="ISettingsStore"/> and coerces
/// <see cref="MarkdownRendererBackend.Native"/> to
/// <see cref="MarkdownRendererBackend.WebView"/> at both load and save. Other
/// preference fields and the non-preference ISettingsStore methods pass
/// through unchanged.
/// </summary>
public sealed class ApplicateRendererCoercingSettingsStoreTests
{
    [Fact]
    public async Task LoadWhenRendererBackendIsNativeCoercesToWebView()
    {
        var native = new ReadingPreferences(
            FontFamilyMode.Serif,
            18,
            1.7,
            ReadingPreferences.MediumContentWidth,
            DocumentMinimapMode.Auto,
            MarkdownRendererBackend.Native,
            WidthResizerVisibility.OnHover,
            LightPaletteMode.Original);
        var inner = new RecordingSettingsStore(native);
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);

        var loaded = await decorator.LoadPreferencesAsync();

        Assert.Equal(MarkdownRendererBackend.WebView, loaded.RendererBackend);
    }

    [Fact]
    public async Task LoadWhenRendererBackendIsWebViewPassesThrough()
    {
        var webView = ReadingPreferences.Default;
        Assert.Equal(MarkdownRendererBackend.WebView, webView.RendererBackend);
        var inner = new RecordingSettingsStore(webView);
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);

        var loaded = await decorator.LoadPreferencesAsync();

        Assert.Equal(webView, loaded);
        Assert.Equal(MarkdownRendererBackend.WebView, loaded.RendererBackend);
    }

    [Fact]
    public async Task LoadPreservesOtherPreferenceFields()
    {
        var customNative = new ReadingPreferences(
            FontFamilyMode.Mono,
            22,
            1.9,
            ReadingPreferences.WideContentWidth,
            DocumentMinimapMode.On,
            MarkdownRendererBackend.Native,
            WidthResizerVisibility.Always,
            LightPaletteMode.White);
        var inner = new RecordingSettingsStore(customNative);
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);

        var loaded = await decorator.LoadPreferencesAsync();

        Assert.Equal(MarkdownRendererBackend.WebView, loaded.RendererBackend);
        Assert.Equal(FontFamilyMode.Mono, loaded.FontFamily);
        Assert.Equal(22, loaded.FontSize);
        Assert.Equal(1.9, loaded.LineHeight);
        Assert.Equal(ReadingPreferences.WideContentWidth, loaded.ContentWidth);
        Assert.Equal(DocumentMinimapMode.On, loaded.DocumentMinimapMode);
        Assert.Equal(WidthResizerVisibility.Always, loaded.WidthResizerVisibility);
        Assert.Equal(LightPaletteMode.White, loaded.LightPalette);
    }

    [Fact]
    public async Task SaveWithNativeBackendPersistsWebViewToInnerStore()
    {
        var inner = new RecordingSettingsStore(ReadingPreferences.Default);
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);
        var nativeSave = ReadingPreferences.Default with
        {
            RendererBackend = MarkdownRendererBackend.Native,
            FontSize = 20
        };

        await decorator.SavePreferencesAsync(nativeSave);

        Assert.NotNull(inner.LastSavedPreferences);
        Assert.Equal(MarkdownRendererBackend.WebView, inner.LastSavedPreferences!.RendererBackend);
        Assert.Equal(20, inner.LastSavedPreferences!.FontSize);
    }

    [Fact]
    public async Task SaveWithWebViewBackendPassesThroughUnchanged()
    {
        var inner = new RecordingSettingsStore(ReadingPreferences.Default);
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);
        var webViewSave = ReadingPreferences.Default with { FontSize = 22 };
        Assert.Equal(MarkdownRendererBackend.WebView, webViewSave.RendererBackend);

        await decorator.SavePreferencesAsync(webViewSave);

        Assert.Equal(webViewSave, inner.LastSavedPreferences);
    }

    [Fact]
    public async Task NonPreferenceMethodsForwardWithoutCoercion()
    {
        var inner = new RecordingSettingsStore(ReadingPreferences.Default);
        inner.SeedTheme = ThemeMode.Dark;
        inner.SeedLanguage = AppLanguage.Russian;
        var seededPlacement = new WindowPlacement(10, 20, 800, 600, IsMaximized: false);
        inner.SeedWindowPlacement = seededPlacement;
        var decorator = new ApplicateRendererCoercingSettingsStore(inner);

        Assert.Equal(ThemeMode.Dark, await decorator.LoadThemeAsync());
        Assert.Equal(AppLanguage.Russian, await decorator.LoadLanguageAsync());
        Assert.Equal(seededPlacement, await decorator.LoadWindowPlacementAsync());

        await decorator.SaveThemeAsync(ThemeMode.Light);
        await decorator.SaveLanguageAsync(AppLanguage.English);
        var newPlacement = new WindowPlacement(5, 5, 1024, 768, IsMaximized: true);
        await decorator.SaveWindowPlacementAsync(newPlacement);

        Assert.Equal(ThemeMode.Light, inner.LastSavedTheme);
        Assert.Equal(AppLanguage.English, inner.LastSavedLanguage);
        Assert.Equal(newPlacement, inner.LastSavedWindowPlacement);

        await decorator.ResetAsync();

        Assert.True(inner.ResetCalled);
    }

    /// <summary>
    /// End-to-end check that the decorator+JsonSettingsStore pair coerces a
    /// real on-disk <c>"RendererBackend": "Native"</c> file: the load yields
    /// WebView, and the subsequent save persists WebView so the next session
    /// reads the canonical value without going through the load-side
    /// coercion. This is the synthetic-fixture mode of the design D8 manual
    /// visual check (production users may or may not have a `Native` value
    /// in their real settings.json — this test always exercises the path).
    /// </summary>
    [Fact]
    public async Task LoadAndResaveAgainstJsonSettingsStoreCoercesAndPersistsWebView()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Applicate.Tests.RendererCoerce",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        try
        {
            const string nativeJson = """
            {
              "theme": "Dark",
              "preferences": {
                "fontFamily": "Serif",
                "fontSize": 18,
                "lineHeight": 1.7,
                "contentWidth": 820,
                "documentMinimapMode": "Auto",
                "rendererBackend": "Native",
                "widthResizerVisibility": "OnHover",
                "lightPalette": "Original"
              }
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), nativeJson);

            var inner = new JsonSettingsStore(rootDirectory);
            var decorator = new ApplicateRendererCoercingSettingsStore(inner);

            var loaded = await decorator.LoadPreferencesAsync();
            Assert.Equal(MarkdownRendererBackend.WebView, loaded.RendererBackend);

            await decorator.SavePreferencesAsync(loaded);

            var rereadStore = new JsonSettingsStore(rootDirectory);
            var rereadPreferences = await rereadStore.LoadPreferencesAsync();
            Assert.Equal(MarkdownRendererBackend.WebView, rereadPreferences.RendererBackend);

            var rawAfterSave = await File.ReadAllTextAsync(Path.Combine(rootDirectory, "settings.json"));
            Assert.DoesNotContain("Native", rawAfterSave);
        }
        finally
        {
            try { Directory.Delete(rootDirectory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Test-double <see cref="ISettingsStore"/> recording the last save and
    /// returning a configurable seed on each load. Construction takes the
    /// initial preferences seed; other surfaces are pre-seedable through the
    /// Seed* properties.
    /// </summary>
    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public ReadingPreferences SeedPreferences { get; set; }
        public ThemeMode SeedTheme { get; set; } = ThemeMode.System;
        public AppLanguage SeedLanguage { get; set; } = AppLanguage.System;
        public WindowPlacement? SeedWindowPlacement { get; set; }

        public ReadingPreferences? LastSavedPreferences { get; private set; }
        public ThemeMode? LastSavedTheme { get; private set; }
        public AppLanguage? LastSavedLanguage { get; private set; }
        public WindowPlacement? LastSavedWindowPlacement { get; private set; }
        public bool ResetCalled { get; private set; }

        public RecordingSettingsStore(ReadingPreferences seedPreferences)
        {
            SeedPreferences = seedPreferences;
        }

        public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SeedPreferences);

        public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
        {
            LastSavedPreferences = preferences;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SeedTheme);

        public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
        {
            LastSavedTheme = theme;
            return ValueTask.CompletedTask;
        }

        public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SeedLanguage);

        public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
        {
            LastSavedLanguage = language;
            return ValueTask.CompletedTask;
        }

        public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SeedWindowPlacement);

        public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken cancellationToken = default)
        {
            LastSavedWindowPlacement = placement;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
        {
            ResetCalled = true;
            return ValueTask.CompletedTask;
        }
    }
}
