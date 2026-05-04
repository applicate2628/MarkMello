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
}
