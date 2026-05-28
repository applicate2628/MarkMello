using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateEditPreviewSyncTests
{
    [Fact]
    public void SyncToggleWiresToAvaloniaEditEditorScrollViewer()
    {
        var codeBehind = ReadEditPreviewCodeBehind();

        var ensureEditorWiring = ExtractMethodBody(codeBehind, "private void EnsureEditorWiring()");

        Assert.Contains("OfType<TextEditor>()", ensureEditorWiring, StringComparison.Ordinal);
        Assert.Contains("\"EditorTextEditor\"", ensureEditorWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("OfType<TextBox>()", ensureEditorWiring, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EditorTextBox\"", ensureEditorWiring, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewSubscribesToRendererHeadingMessages()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var wireSharedHostEvents = ExtractMethodBody(codeBehind, "private void WireSharedHostEvents()");
        var unwireSharedHostEvents = ExtractMethodBody(codeBehind, "private void UnwireSharedHostEvents()");

        Assert.Contains("HeadingsChanged += OnSharedHeadingsChanged", wireSharedHostEvents, StringComparison.Ordinal);
        Assert.Contains("ActiveHeadingChanged += OnSharedActiveHeadingChanged", wireSharedHostEvents, StringComparison.Ordinal);
        Assert.Contains("HeadingsChanged -= OnSharedHeadingsChanged", unwireSharedHostEvents, StringComparison.Ordinal);
        Assert.Contains("ActiveHeadingChanged -= OnSharedActiveHeadingChanged", unwireSharedHostEvents, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewForwardsShellTocScrollRequestsToRenderer()
    {
        var codeBehind = ReadEditPreviewCodeBehind();

        Assert.Contains("ScrollToHeadingRequested += OnViewModelScrollToHeadingRequested", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ScrollToHeadingRequested -= OnViewModelScrollToHeadingRequested", codeBehind, StringComparison.Ordinal);

        var handler = ExtractMethodBody(codeBehind, "private void OnViewModelScrollToHeadingRequested(object? sender, string id)");
        Assert.Contains("_isAttachedToHost", handler, StringComparison.Ordinal);
        Assert.Contains("_sharedHost.View.ScrollToHeading(id)", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewPreservesReadingProgressAcrossPreviewRerender()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var applyRender = ExtractMethodBody(codeBehind, "private void ApplyWebPreviewSource()");
        var scrollHandler = ExtractMethodBody(codeBehind, "private void OnSharedScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)");
        var renderedHandler = ExtractMethodBody(codeBehind, "private void OnSharedDocumentRendered(object? sender, EventArgs e)");

        Assert.Contains("_pendingScrollRestoreProgress", applyRender, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ReadingProgress = e.ProgressPercent;", scrollHandler, StringComparison.Ordinal);
        Assert.Contains("_pendingScrollRestoreProgress.HasValue && !_sharedHost.View.LastLayoutReadyWasCached", scrollHandler, StringComparison.Ordinal);
        Assert.Contains("!_sharedHost.View.LastLayoutReadyWasCached", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("_sharedHost.View.ScrollToProgress(restoreProgress.Value);", renderedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewVisibilityChainStillAttachesAndQueuesRender()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnEffectiveVisibilityChanged()");

        Assert.True(
            handler.IndexOf("_sharedHost.AttachTo(_webSlot, intent);", StringComparison.Ordinal)
            < handler.IndexOf("QueueWebPreviewRender(immediate: true);", StringComparison.Ordinal));
        Assert.DoesNotContain("Opacity", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewWaitsForValidWebSlotBoundsBeforeTransactionRender()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var applyRender = ExtractMethodBody(codeBehind, "private void ApplyWebPreviewSource()");

        Assert.True(
            applyRender.IndexOf("if (!_hasValidSlotBounds)", StringComparison.Ordinal)
            < applyRender.IndexOf("transactionGeneration:", StringComparison.Ordinal));
        Assert.Contains(
            "ApplicateModeTransactionContext.GetTransactionGeneration(_webSlot)",
            applyRender,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EditPreviewReevaluatesEffectiveVisibilityWhenInactivePrimeEnds()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var endPrime = ExtractMethodBody(codeBehind, "internal void EndInactivePrimeVisibility()");

        Assert.Contains("_inactivePrimeVisibilityDepth--", endPrime, StringComparison.Ordinal);
        Assert.Contains("if (_inactivePrimeVisibilityDepth == 0)", endPrime, StringComparison.Ordinal);
        Assert.Contains("OnEffectiveVisibilityChanged();", endPrime, StringComparison.Ordinal);
    }

    private static string ReadEditPreviewCodeBehind()
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
            "ApplicateEditPreviewView.cs"));

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
