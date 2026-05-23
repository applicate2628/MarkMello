using Avalonia.Styling;
using Avalonia.Threading;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Presentation.Services;

/// <summary>
/// Применяет тему к <see cref="global::Avalonia.Application.RequestedThemeVariant"/>.
/// Маршалинг на UI-поток, потому что вызов может прийти из любого контекста.
/// </summary>
public sealed class AvaloniaThemeService : IThemeService
{
    public const string ClassicWhiteThemeVariantKey = "ClassicWhite";

    public static readonly ThemeVariant ClassicWhiteThemeVariant =
        new(ClassicWhiteThemeVariantKey, ThemeVariant.Light);

    public void Apply(ThemeMode mode, LightPaletteMode lightPalette)
    {
        var variant = mode switch
        {
            ThemeMode.Light => GetLightVariant(lightPalette),
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.ClassicWhite => ClassicWhiteThemeVariant,
            _ => ThemeVariant.Default
        };

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyCore(variant);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyCore(variant));
        }
    }


    public ThemeMode GetEffectiveTheme()
    {
        var variant = global::Avalonia.Application.Current?.ActualThemeVariant;
        if (variant == ThemeVariant.Dark)
        {
            return ThemeMode.Dark;
        }

        return Equals(variant?.Key, ClassicWhiteThemeVariantKey)
            ? ThemeMode.ClassicWhite
            : ThemeMode.Light;
    }

    private static void ApplyCore(ThemeVariant variant)
    {
        var app = global::Avalonia.Application.Current;
        if (app is not null)
        {
            app.RequestedThemeVariant = variant;
        }
    }

    private static ThemeVariant GetLightVariant(LightPaletteMode lightPalette)
        => lightPalette == LightPaletteMode.White
            ? ClassicWhiteThemeVariant
            : ThemeVariant.Light;
}
