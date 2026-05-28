using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateViewerViewTests
{
    [Fact]
    public void ConstructsWithoutSharedHostAndExposesEmptyWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var view = new ApplicateViewerView();

            Assert.NotNull(view.WebSlotForTesting);
            Assert.False(view.IsFailureViewVisibleForTesting);
        }, CancellationToken.None);
    }

    [Fact]
    public void WebDocumentLayerExpandsToHostViewport()
    {
        var actual = ApplicateViewerView.CalculateDocumentLayerWidth(
            documentColumnWidth: 900,
            hostWidth: 1500,
            useWebRenderer: true);

        Assert.Equal(1500, actual);
    }

    [Theory]
    [InlineData(640, 20, 680)]
    [InlineData(640, -20, 600)]
    public void WidthDragKeepsCenteredColumnScaling(double startWidth, double deltaX, double expected)
    {
        var actual = ApplicateViewerView.CalculateWidthDragContentWidth(startWidth, deltaX);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WebAvailableContentWidthReservesReportedMinimapWidth()
    {
        var actual = ApplicateViewerView.CalculateAvailableContentWidth(
            boundsWidth: 1200,
            resizeReservedWidth: 168,
            documentHorizontalPadding: 144,
            useWebRenderer: true);

        Assert.Equal(856, actual);
    }

    [Theory]
    [InlineData(120, 0, 16, 800, 120)]
    [InlineData(3, 1, 20, 800, 180)]
    [InlineData(1, 2, 16, 1000, 850)]
    [InlineData(1, 2, 24, 0, 24)]
    public void WebWheelDeltaUsesRendererDeltaMode(
        double deltaY,
        int deltaMode,
        double smallChangeHeight,
        double viewportHeight,
        double expected)
    {
        Assert.Equal(expected, ApplicateViewerView.NormalizeWebWheelDeltaForTesting(
            deltaY,
            deltaMode,
            smallChangeHeight,
            viewportHeight));
    }

    [Fact]
    public void RenderedDocumentChangeDoesNotIssueDuplicateWebRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)");

        Assert.Contains("nameof(MainWindowViewModel.Document)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(MainWindowViewModel.RenderedDocument)", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerRestoresReadingProgressAfterDocumentRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var issueRender = ExtractMethodBody(codeBehind, "private void IssueRenderRequest()");
        var scrollHandler = ExtractMethodBody(codeBehind, "private void OnHostScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)");
        var renderedHandler = ExtractMethodBody(codeBehind, "private void OnHostDocumentRendered(object? sender, EventArgs e)");

        Assert.Contains("_pendingScrollRestoreProgress", issueRender, StringComparison.Ordinal);
        Assert.Contains("_pendingScrollRestoreProgress.HasValue && !_sharedHost.View.LastLayoutReadyWasCached", scrollHandler, StringComparison.Ordinal);
        Assert.Contains("!_sharedHost.View.LastLayoutReadyWasCached", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("_sharedHost.View.ScrollToProgress(restoreProgress.Value);", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ReadingProgress = restoreProgress.Value;", renderedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerVisibilityChainStillAttachesBeforeRender()
    {
        var codeBehind = ReadViewerCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnEffectiveVisibilityChanged()");

        Assert.True(
            handler.IndexOf("EnsureSharedHostMounted(force: true);", StringComparison.Ordinal)
            < handler.IndexOf("IssueRenderRequest();", StringComparison.Ordinal));
        Assert.DoesNotContain("Opacity", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerPassesContextualTransactionGenerationToSharedHost()
    {
        var codeBehind = ReadViewerCodeBehind();
        var issueRender = ExtractMethodBody(codeBehind, "private void IssueRenderRequest()");

        Assert.Contains(
            "ApplicateModeTransactionContext.GetTransactionGeneration(_webSlot)",
            issueRender,
            StringComparison.Ordinal);
        Assert.Contains(
            "transactionGeneration:",
            issueRender,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionGenerationContextInheritsToConsumerWebSlot()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        session.Dispatch(() =>
        {
            var parent = new Avalonia.Controls.Grid();
            var child = new Avalonia.Controls.Panel();
            parent.Children.Add(child);

            ApplicateModeTransactionContext.SetTransactionGeneration(parent, 123);

            Assert.Equal(123, ApplicateModeTransactionContext.GetTransactionGeneration(child));
        }, CancellationToken.None);
    }

    private static string ReadViewerCodeBehind()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Views",
            "ApplicateViewerView.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} should exist.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"{signature} should have a body.");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return source[braceStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} body was not closed.");
    }
}
