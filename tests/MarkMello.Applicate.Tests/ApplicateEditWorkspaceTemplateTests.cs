using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateEditWorkspaceTemplateTests
{
    [Fact]
    public void ApplicateTemplateTypeRemainsStaticCompatibilityStub()
    {
        Assert.True(typeof(ApplicateEditWorkspaceTemplate).IsAbstract);
        Assert.True(typeof(ApplicateEditWorkspaceTemplate).IsSealed);
    }

    [Fact]
    public void PreviewWidthUsesActualPaneWidthWhenPaneIsNarrowerThanPreference()
    {
        var widths = ApplicateEditPreviewView.CalculatePreviewWidths(
            hostWidth: 520,
            ReadingPreferences.Default with { ContentWidth = 820 },
            new Thickness(72, 96, 72, 160));

        Assert.Equal(520, widths.WebColumnWidth);
        Assert.Equal(376, widths.NativeContentWidth);
    }

    [Fact]
    public void PreviewWidthUsesPreferenceWhenPaneIsWideEnough()
    {
        var widths = ApplicateEditPreviewView.CalculatePreviewWidths(
            hostWidth: 1200,
            ReadingPreferences.Default with { ContentWidth = 820 },
            new Thickness(72, 96, 72, 160));

        Assert.Equal(964, widths.WebColumnWidth);
        Assert.Equal(820, widths.NativeContentWidth);
    }

    [Theory]
    [InlineData(640, 900, 1200, 640)]
    [InlineData(0, 900, 1200, 900)]
    [InlineData(double.NaN, 0, 1200, 1200)]
    public void PreviewHostWidthPrefersMeasuredWebSlot(
        double slotWidth,
        double surfaceWidth,
        double controlWidth,
        double expected)
    {
        var actual = ApplicateEditPreviewView.ResolvePreviewHostWidth(
            slotWidth,
            surfaceWidth,
            controlWidth);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(120, 120)]
    [InlineData(720, 720)]
    public void WebPreviewMinHeightKeepsNativeWebViewMeasurable(double hostHeight, double expected)
    {
        Assert.Equal(expected, ApplicateEditPreviewView.CalculateWebPreviewMinHeight(hostHeight));
    }

    [Fact]
    public void WebPreviewDisablesOuterPreviewScrollViewer()
    {
        Assert.Equal(
            ScrollBarVisibility.Disabled,
            ApplicateEditPreviewView.CalculateHostVerticalScrollMode(
                useWebPreview: true,
                ScrollBarVisibility.Auto));
    }

    [Fact]
    public void NativePreviewRestoresOuterPreviewScrollViewerMode()
    {
        Assert.Equal(
            ScrollBarVisibility.Auto,
            ApplicateEditPreviewView.CalculateHostVerticalScrollMode(
                useWebPreview: false,
                ScrollBarVisibility.Auto));
    }
}
