using Avalonia;
using MarkMello.Domain;
using MarkMello.Presentation.Views.Markdown.Minimap;

namespace MarkMello.Presentation.Tests;

public sealed class DocumentMinimapBuildPolicyTests
{
    [Fact]
    public void ShouldShowReturnsTrueOnlyForWideScrollableLongDocument()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.Auto,
            hostWidth: 1280,
            scrollExtent: new Size(900, 2_400),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 1_600);

        Assert.True(shouldShow);
    }

    [Fact]
    public void ShouldShowReturnsFalseForNarrowHost()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.Auto,
            hostWidth: DocumentMinimapBuildPolicy.MinHostWidth - 1,
            scrollExtent: new Size(900, 2_400),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 1_600);

        Assert.False(shouldShow);
    }

    [Fact]
    public void ShouldShowReturnsFalseForShortDocument()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.Auto,
            hostWidth: 1280,
            scrollExtent: new Size(900, 1_000),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 200);

        Assert.False(shouldShow);
    }

    [Fact]
    public void ShouldShowReturnsFalseWhenDocumentDoesNotScroll()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.Auto,
            hostWidth: 1280,
            scrollExtent: new Size(900, 800),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 0);

        Assert.False(shouldShow);
    }


    [Fact]
    public void ShouldShowReturnsFalseWhenModeIsOff()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.Off,
            hostWidth: 1280,
            scrollExtent: new Size(900, 2_400),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 1_600);

        Assert.False(shouldShow);
    }

    [Fact]
    public void ShouldShowReturnsTrueForModeOnWhenDocumentScrolls()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.On,
            hostWidth: 800,
            scrollExtent: new Size(900, 1_000),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 200);

        Assert.True(shouldShow);
    }

    [Fact]
    public void ShouldShowReturnsFalseForModeOnWhenDocumentDoesNotScroll()
    {
        var shouldShow = DocumentMinimapBuildPolicy.ShouldShow(
            DocumentMinimapMode.On,
            hostWidth: 1280,
            scrollExtent: new Size(900, 800),
            scrollViewport: new Size(900, 800),
            scrollMaximumY: 0);

        Assert.False(shouldShow);
    }

    [Fact]
    public void HasLayoutMetricsChangedIgnoresScrollOnlyState()
    {
        var changed = DocumentMinimapBuildPolicy.HasLayoutMetricsChanged(
            previousExtent: new Size(900, 2_400),
            previousViewport: new Size(900, 800),
            currentExtent: new Size(900, 2_400),
            currentViewport: new Size(900, 800));

        Assert.False(changed);
    }

    [Fact]
    public void HasLayoutMetricsChangedIgnoresSubPixelNoise()
    {
        var changed = DocumentMinimapBuildPolicy.HasLayoutMetricsChanged(
            previousExtent: new Size(900, 2_400),
            previousViewport: new Size(900, 800),
            currentExtent: new Size(900.25, 2_400.25),
            currentViewport: new Size(900.25, 800.25));

        Assert.False(changed);
    }

    [Fact]
    public void HasLayoutMetricsChangedReturnsTrueForMaterialLayoutChange()
    {
        var changed = DocumentMinimapBuildPolicy.HasLayoutMetricsChanged(
            previousExtent: new Size(900, 2_400),
            previousViewport: new Size(900, 800),
            currentExtent: new Size(936, 2_560),
            currentViewport: new Size(936, 800));

        Assert.True(changed);
    }

    [Fact]
    public void AllowsDetailedMiniatureRejectsEmptySnapshot()
    {
        Assert.False(DocumentMinimapBuildPolicy.AllowsDetailedMiniature(DocumentMiniatureSnapshot.Empty));
    }

    [Fact]
    public void AllowsDetailedMiniatureAcceptsNormalSnapshot()
    {
        var snapshot = new DocumentMiniatureSnapshot(
            totalWidth: 900,
            totalHeight: DocumentMinimapBuildPolicy.MaxDetailedDocumentHeight);

        Assert.True(DocumentMinimapBuildPolicy.AllowsDetailedMiniature(snapshot));
    }

    [Fact]
    public void AllowsDetailedMiniatureRejectsExtremeDocumentHeight()
    {
        var snapshot = new DocumentMiniatureSnapshot(
            totalWidth: 900,
            totalHeight: DocumentMinimapBuildPolicy.MaxDetailedDocumentHeight + 1);

        Assert.False(DocumentMinimapBuildPolicy.AllowsDetailedMiniature(snapshot));
    }
}
