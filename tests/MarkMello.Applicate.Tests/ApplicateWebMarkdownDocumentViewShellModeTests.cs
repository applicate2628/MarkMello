using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMarkdownDocumentViewShellModeTests
{
    [Fact]
    public void ShouldCompleteRenderRequiresAllThreeSignalsInShellMode()
    {
        // The state machine that gates DocumentRendered is the same in shell
        // and legacy modes. This test pins the contract.
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(false, false, false));
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, false, false));
        Assert.False(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, true, false));
        Assert.True(ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(true, true, true));
    }
}
