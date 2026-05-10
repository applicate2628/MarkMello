using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Presentation.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateEditWorkspaceTemplateTests
{
    [Fact]
    public void ApplicateTemplateReplacesEditPreviewWithApplicateRenderer()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = Assert.IsType<EditWorkspaceView>(new ApplicateEditWorkspaceTemplate().Build(null));

            var previewHost = view.GetVisualDescendants().OfType<ApplicateEditPreviewView>().SingleOrDefault();
            var preview = view.GetVisualDescendants().OfType<ApplicateMarkdownDocumentView>().SingleOrDefault();

            Assert.NotNull(previewHost);
            Assert.NotNull(preview);
            Assert.DoesNotContain(view.GetVisualDescendants(), static visual => visual is MarkdownDocumentView);
        }, CancellationToken.None);
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
    [InlineData(0, 1)]
    [InlineData(120, 480)]
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
