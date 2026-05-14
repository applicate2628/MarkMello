using MarkMello.Domain;

namespace MarkMello.Domain.Tests;

public sealed class ReadingPreferencesTests
{
    [Fact]
    public void NormalizeReturnsDefaultsForNull()
    {
        var normalized = ReadingPreferences.Normalize(null);

        Assert.Equal(ReadingPreferences.Default, normalized);
        Assert.Equal(DocumentMinimapMode.Auto, normalized.DocumentMinimapMode);
        Assert.Equal(MarkdownRendererBackend.WebView, normalized.RendererBackend);
        Assert.Equal(WidthResizerVisibility.OnHover, normalized.WidthResizerVisibility);
    }

    [Fact]
    public void NormalizeClampsAndRoundsOutOfRangeValues()
    {
        var candidate = new ReadingPreferences(
            FontFamily: (FontFamilyMode)42,
            FontSize: 200,
            LineHeight: 0.2,
            ContentWidth: 517,
            DocumentMinimapMode: (DocumentMinimapMode)42);

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(FontFamilyMode.Serif, normalized.FontFamily);
        Assert.Equal(ReadingPreferences.MaxFontSize, normalized.FontSize);
        Assert.Equal(ReadingPreferences.MinLineHeight, normalized.LineHeight);
        Assert.Equal(ReadingPreferences.MinContentWidth, normalized.ContentWidth);
        Assert.Equal(DocumentMinimapMode.Auto, normalized.DocumentMinimapMode);
        Assert.Equal(WidthResizerVisibility.OnHover, normalized.WidthResizerVisibility);
    }

    [Theory]
    [InlineData(580, ReadingPreferences.NarrowContentWidth)]
    [InlineData(720, ReadingPreferences.MediumContentWidth)]
    [InlineData(860, ReadingPreferences.WideContentWidth)]
    public void NormalizeMigratesLegacyPresetContentWidths(int legacyWidth, int expectedWidth)
    {
        var candidate = ReadingPreferences.Default with { ContentWidth = legacyWidth };

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(expectedWidth, normalized.ContentWidth);
    }

    [Theory]
    [InlineData(DocumentMinimapMode.Auto)]
    [InlineData(DocumentMinimapMode.On)]
    [InlineData(DocumentMinimapMode.Off)]
    public void NormalizePreservesSupportedDocumentMinimapModes(DocumentMinimapMode mode)
    {
        var candidate = ReadingPreferences.Default with { DocumentMinimapMode = mode };

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(mode, normalized.DocumentMinimapMode);
    }

    [Theory]
    [InlineData(MarkdownRendererBackend.Native)]
    [InlineData(MarkdownRendererBackend.WebView)]
    public void NormalizePreservesKnownRendererBackend(MarkdownRendererBackend backend)
    {
        var candidate = ReadingPreferences.Default with { RendererBackend = backend };

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(backend, normalized.RendererBackend);
    }

    [Theory]
    [InlineData(WidthResizerVisibility.Always)]
    [InlineData(WidthResizerVisibility.OnHover)]
    public void NormalizePreservesSupportedWidthResizerVisibility(WidthResizerVisibility visibility)
    {
        var candidate = ReadingPreferences.Default with { WidthResizerVisibility = visibility };

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(visibility, normalized.WidthResizerVisibility);
    }

    [Fact]
    public void NormalizeUsesOnHoverForUnknownWidthResizerVisibility()
    {
        var candidate = ReadingPreferences.Default with { WidthResizerVisibility = (WidthResizerVisibility)42 };

        var normalized = ReadingPreferences.Normalize(candidate);

        Assert.Equal(WidthResizerVisibility.OnHover, normalized.WidthResizerVisibility);
    }
}
