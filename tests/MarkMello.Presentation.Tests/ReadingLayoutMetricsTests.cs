using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

public sealed class ReadingLayoutMetricsTests
{
    [Fact]
    public void GetDocumentColumnMaxWidthAddsDocumentHorizontalPaddingToUsefulContentWidth()
    {
        var preferences = ReadingPreferences.Default with { ContentWidth = ReadingPreferences.WideContentWidth };

        var maxWidth = ReadingLayoutMetrics.GetDocumentColumnMaxWidth(preferences);

        Assert.Equal(1224d, maxWidth);
    }

    [Fact]
    public void GetDocumentHorizontalPaddingMatchesCanonicalConstantWhenContentWidthIsNormalized()
    {
        // F-02 fix: when ContentWidth is one of the named presets the
        // formula collapses to the canonical horizontal padding constant.
        var preferences = ReadingPreferences.Default with { ContentWidth = ReadingPreferences.MediumContentWidth };

        var horizontalPadding = ReadingLayoutMetrics.GetDocumentHorizontalPadding(preferences);

        Assert.Equal(ReadingLayoutMetrics.DocumentHorizontalPadding, horizontalPadding);
    }

    [Fact]
    public void GetDocumentHorizontalPaddingCompensatesForUnnormalizedContentWidth()
    {
        // F-02 fix: when the active ContentWidth differs from the
        // Normalize(...) result, the formula widens the padding so the
        // rendered column still reaches the canonical max width.
        var preferences = ReadingPreferences.Default with { ContentWidth = 580 };
        // Normalize migrates the legacy 580 -> 640 (NarrowContentWidth); manual
        // widths now persist exactly, so use a legacy value that still re-maps.
        var normalized = ReadingPreferences.Normalize(preferences);
        Assert.Equal(ReadingPreferences.NarrowContentWidth, normalized.ContentWidth);

        var horizontalPadding = ReadingLayoutMetrics.GetDocumentHorizontalPadding(preferences);

        Assert.Equal(
            ReadingPreferences.NarrowContentWidth + ReadingLayoutMetrics.DocumentHorizontalPadding - 580d,
            horizontalPadding);
    }
}
