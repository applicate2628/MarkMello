namespace MarkMello.Domain;

/// <summary>
/// Пользовательские настройки чтения документа. Применяются live, не требуют перезапуска.
/// </summary>
/// <param name="FontFamily">Семейство шрифта (Serif/Sans/Mono).</param>
/// <param name="FontSize">Базовый размер шрифта в пикселях.</param>
/// <param name="LineHeight">Межстрочный интервал (множитель к размеру шрифта).</param>
/// <param name="ContentWidth">Максимальная полезная ширина текста документа в пикселях.</param>
/// <param name="DocumentMinimapMode">Режим отображения миникарты документа.</param>
/// <param name="RendererBackend">Механизм рендеринга Markdown-документа.</param>
/// <param name="WidthResizerVisibility">Когда показывать визуальную линию изменения ширины.</param>
/// <param name="LightPalette">Вариант светлой палитры документа.</param>
/// <param name="ModeSwitchSmoothEnabled">Включена ли плавная смена режимов чтение/редактирование.</param>
/// <param name="ModeSwitchSmoothDurationMs">Длительность плавной смены режимов в миллисекундах.</param>
public sealed record ReadingPreferences(
    FontFamilyMode FontFamily,
    int FontSize,
    double LineHeight,
    int ContentWidth,
    DocumentMinimapMode DocumentMinimapMode = DocumentMinimapMode.Auto,
    MarkdownRendererBackend RendererBackend = MarkdownRendererBackend.WebView,
    WidthResizerVisibility WidthResizerVisibility = WidthResizerVisibility.OnHover,
    LightPaletteMode LightPalette = LightPaletteMode.White,
    bool ModeSwitchSmoothEnabled = true,
    int ModeSwitchSmoothDurationMs = ReadingPreferences.DefaultModeSwitchSmoothDurationMs)
{
    public const int MinFontSize = 14;
    public const int MaxFontSize = 24;
    public const double MinLineHeight = 1.0;
    public const double MaxLineHeight = 3.0;
    public const double LineHeightStep = 0.05;
    public const int NarrowContentWidth = 640;
    public const int MediumContentWidth = 820;
    public const int WideContentWidth = 1080;
    // Manual resizer widths persist exactly, so the persistable range spans the
    // resizer's own drag range (down to 320 px) up to a generous sanity cap; the
    // render path re-clamps to the live viewport. NarrowContentWidth (640) stays
    // the Narrow PRESET, not the floor.
    public const int MinContentWidth = 320;
    public const int MaxContentWidth = 8192;
    public const int ContentWidthStep = 20;
    public const int MinModeSwitchSmoothDurationMs = 0;
    public const int MaxModeSwitchSmoothDurationMs = 600;
    public const int ModeSwitchSmoothDurationStepMs = 20;
    public const int DefaultModeSwitchSmoothDurationMs = 180;

    private const int LegacyNarrowContentWidth = 580;
    private const int LegacyMediumContentWidth = 720;
    private const int LegacyWideContentWidth = 860;

    /// <summary>
    /// Безопасные значения по умолчанию. Используются при отсутствии или повреждении сохранённых настроек.
    /// </summary>
    public static ReadingPreferences Default { get; } = new(
        FontFamily: FontFamilyMode.Serif,
        FontSize: 18,
        LineHeight: 1.7,
        ContentWidth: MediumContentWidth,
        DocumentMinimapMode: DocumentMinimapMode.Auto,
        RendererBackend: MarkdownRendererBackend.WebView,
        WidthResizerVisibility: WidthResizerVisibility.OnHover,
        LightPalette: LightPaletteMode.White,
        ModeSwitchSmoothEnabled: true,
        ModeSwitchSmoothDurationMs: DefaultModeSwitchSmoothDurationMs);

    /// <summary>
    /// Нормализует пользовательские настройки до безопасного и предсказуемого диапазона.
    /// Используется для live-обновлений и восстановления из persistence.
    /// </summary>
    public static ReadingPreferences Normalize(ReadingPreferences? preferences)
    {
        if (preferences is null)
        {
            return Default;
        }

        var fontFamily = Enum.IsDefined(preferences.FontFamily)
            ? preferences.FontFamily
            : Default.FontFamily;

        var fontSize = Math.Clamp(preferences.FontSize, MinFontSize, MaxFontSize);
        var lineHeight = NormalizeLineHeight(preferences.LineHeight);
        var contentWidth = NormalizeContentWidth(preferences.ContentWidth);
        var documentMinimapMode = Enum.IsDefined(preferences.DocumentMinimapMode)
            ? preferences.DocumentMinimapMode
            : Default.DocumentMinimapMode;
        var rendererBackend = Enum.IsDefined(preferences.RendererBackend)
            ? preferences.RendererBackend
            : Default.RendererBackend;
        var widthResizerVisibility = Enum.IsDefined(preferences.WidthResizerVisibility)
            ? preferences.WidthResizerVisibility
            : Default.WidthResizerVisibility;
        var lightPalette = Enum.IsDefined(preferences.LightPalette)
            ? preferences.LightPalette
            : Default.LightPalette;
        var modeSwitchSmoothDurationMs = NormalizeModeSwitchSmoothDuration(preferences.ModeSwitchSmoothDurationMs);

        return new ReadingPreferences(
            fontFamily,
            fontSize,
            lineHeight,
            contentWidth,
            documentMinimapMode,
            rendererBackend,
            widthResizerVisibility,
            lightPalette,
            preferences.ModeSwitchSmoothEnabled,
            modeSwitchSmoothDurationMs);
    }

    public ReadingPreferences Normalize() => Normalize(this);

    private static double NormalizeLineHeight(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return Default.LineHeight;
        }

        var clamped = Math.Clamp(value, MinLineHeight, MaxLineHeight);
        var rounded = Math.Round(clamped / LineHeightStep, MidpointRounding.AwayFromZero) * LineHeightStep;
        return Math.Round(rounded, 2, MidpointRounding.AwayFromZero);
    }

    private static int NormalizeContentWidth(int value)
    {
        var migrated = value switch
        {
            LegacyNarrowContentWidth => NarrowContentWidth,
            LegacyMediumContentWidth => MediumContentWidth,
            LegacyWideContentWidth => WideContentWidth,
            _ => value
        };

        // Persist the manual resizer width EXACTLY — only clamp to a sane range, do
        // not snap to a step grid (the user expects the column where they left it).
        return Math.Clamp(migrated, MinContentWidth, MaxContentWidth);
    }

    private static int NormalizeModeSwitchSmoothDuration(int value)
    {
        var clamped = Math.Clamp(value, MinModeSwitchSmoothDurationMs, MaxModeSwitchSmoothDurationMs);
        var rounded = (int)Math.Round(
            clamped / (double)ModeSwitchSmoothDurationStepMs,
            MidpointRounding.AwayFromZero) * ModeSwitchSmoothDurationStepMs;
        return Math.Clamp(rounded, MinModeSwitchSmoothDurationMs, MaxModeSwitchSmoothDurationMs);
    }
}
