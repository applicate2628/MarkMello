using Avalonia;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Verifies the canonical layout helpers introduced by the F-01/F-02/F-06
/// fixes — both consumer surfaces (viewer + edit-preview) must read the same
/// values, and the headless test environment must resolve sensible defaults
/// when the Avalonia application resources are not yet loaded.
/// </summary>
public sealed class ApplicateDocumentLayoutTests
{
    [Fact]
    public void GetWebSlotScrollBarGutterFallsBackToDefaultWhenResourceUnavailable()
    {
        // F-01 fix: the canonical gutter width comes from ScrollBarSize in
        // Themes/ApplicateScrollBars.axaml. In headless tests the theme is
        // not loaded, so the helper must fall back to DefaultScrollBarSize
        // (the same numeric value as the resource).
        var gutter = ApplicateDocumentLayout.GetWebSlotScrollBarGutter();

        Assert.Equal(0, gutter.Left);
        Assert.Equal(0, gutter.Top);
        Assert.Equal(ApplicateDocumentLayout.DefaultScrollBarSize, gutter.Right);
        Assert.Equal(0, gutter.Bottom);
    }

    [Fact]
    public void CalculatePreviewDocumentPaddingSplitsCanonicalHorizontalPaddingSymmetrically()
    {
        // F-02 fix: edit-preview reads the same canonical horizontal padding
        // the viewer uses, then splits it symmetrically between left/right
        // so the rendered column stays centered.
        var preferences = ReadingPreferences.Default with { ContentWidth = ReadingPreferences.MediumContentWidth };
        var horizontalPadding = ReadingLayoutMetrics.GetDocumentHorizontalPadding(preferences);

        var padding = ApplicateDocumentLayout.CalculatePreviewDocumentPadding(
            preferences,
            verticalTop: 96,
            verticalBottom: 160);

        Assert.Equal(horizontalPadding / 2.0, padding.Left);
        Assert.Equal(horizontalPadding / 2.0, padding.Right);
        Assert.Equal(96, padding.Top);
        Assert.Equal(160, padding.Bottom);
    }

    [Fact]
    public void MinManualContentWidthIsTheCanonicalFloorViewerAliasMirrors()
    {
        // F-06 fix: the lower-layer renderer view used to reference the
        // viewer slot's constant; the canonical owner is now
        // ApplicateDocumentLayout. Verify the alias still resolves to the
        // same numeric value so existing call sites (test seam + renderer
        // wire payload) read one shared source of truth.
        Assert.Equal(
            ApplicateDocumentLayout.MinManualContentWidth,
            ApplicateViewerView.MinManualContentWidth);
    }
}
