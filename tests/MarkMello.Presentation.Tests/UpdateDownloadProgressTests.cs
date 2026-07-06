using MarkMello.Application.Updates;

namespace MarkMello.Presentation.Tests;

public sealed class UpdateDownloadProgressTests
{
    [Fact]
    public void PercentIsNullWhenTotalUnknown()
    {
        var progress = new UpdateDownloadProgress(BytesReceived: 512, TotalBytes: null);

        Assert.Null(progress.Percent);
    }

    [Fact]
    public void PercentIsNullWhenTotalIsZero()
    {
        var progress = new UpdateDownloadProgress(BytesReceived: 0, TotalBytes: 0);

        Assert.Null(progress.Percent);
    }

    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(25, 100, 25.0)]
    [InlineData(100, 100, 100.0)]
    public void PercentComputesCompletion(long received, long total, double expected)
    {
        var progress = new UpdateDownloadProgress(received, total);

        Assert.Equal(expected, progress.Percent);
    }

    [Fact]
    public void PercentClampsToHundredWhenReceivedExceedsTotal()
    {
        // Chunked / gzip transfers can deliver slightly more decoded bytes than
        // the declared Content-Length; the bar must not exceed 100.
        var progress = new UpdateDownloadProgress(BytesReceived: 120, TotalBytes: 100);

        Assert.Equal(100.0, progress.Percent);
    }
}
