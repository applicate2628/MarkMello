using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateEditPreviewSyncTests
{
    [Fact]
    public void PercentScrollForwardersAreRetired()
    {
        // ONE sync contract (fable design design-editpreview-sync.md): the
        // percent-of-scroll-range forwarders are DELETED — percent mapping is
        // wrong by construction for non-uniform rendered heights. The ⇅ toggle
        // now only gates the line-based loop owned by EditWorkspaceView.
        var codeBehind = ReadEditPreviewCodeBehind();

        Assert.DoesNotContain("ForwardEditorScrollToPreview", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ForwardPreviewScrollToEditor", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureEditorWiring", codeBehind, StringComparison.Ordinal);
        Assert.Contains("public bool SyncEnabled => _syncEnabled;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_syncEnabled = true;", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorSyncResolvesPreviewStructurallyAndWritesAnchorOffset()
    {
        // C1: Applicate injects a typed preview sync mount point instead of
        // letting the edit view rediscover fork-owned preview structure. The
        // native fallback remains for non-Applicate use. C3 (editor side): the
        // editor write must use the 38%-anchor offset mapper, not ScrollToLine
        // (middle + 30% dead-zone).
        var workspace = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MarkMello.Presentation", "Views", "EditWorkspaceView.axaml.cs"));

        Assert.Contains("UseResolvedPreviewSourceLineSync", workspace, StringComparison.Ordinal);
        Assert.Contains("_hasResolvedPreviewSourceLineSync", workspace, StringComparison.Ordinal);
        Assert.Contains("ResolvePreviewSourceLineSync()", workspace, StringComparison.Ordinal);
        // The native fallback must stay a STRUCTURAL type-scan (the named-Border
        // path was silently deleted by an upstream merge once; a name-only lookup
        // would re-create exactly that fragility) — fable gate B1.
        Assert.Contains("GetVisualDescendants().OfType<ISourceLineScrollSyncPreview>().FirstOrDefault()", workspace, StringComparison.Ordinal);

        var scrollEditor = ExtractMethodBody(workspace, "private void ScrollEditorToSourceLine(int sourceLine)");
        Assert.Contains("TryGetEditorVerticalOffsetForSourceLine", scrollEditor, StringComparison.Ordinal);
        Assert.Contains("SetSynchronizedVerticalOffset", scrollEditor, StringComparison.Ordinal);
        Assert.DoesNotContain(".ScrollToLine(", scrollEditor, StringComparison.Ordinal);

        // The ⇅ gate is honored by both loop legs.
        Assert.Contains("_previewSourceLineSync is { SyncEnabled: false }", workspace, StringComparison.Ordinal);
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
    public void EditPreviewDefersLargeTocHeadingUpdatesBehindRendererReveal()
    {
        var codeBehind = ReadEditPreviewCodeBehind();
        var handler = ExtractMethodBody(codeBehind, "private void OnSharedHeadingsChanged(");
        var unwireSharedHostEvents = ExtractMethodBody(codeBehind, "private void UnwireSharedHostEvents()");
        var rendered = ExtractMethodBody(codeBehind, "private void OnSharedDocumentRendered(object? sender, EventArgs e)");

        Assert.Contains("_headingUpdater.Apply(", handler, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_viewModel, viewModel)", handler, StringComparison.Ordinal);
        Assert.Contains("_headingUpdater.FlushPending();", rendered, StringComparison.Ordinal);
        Assert.Contains("_headingUpdater.Invalidate();", unwireSharedHostEvents, StringComparison.Ordinal);
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
    public void EditPreviewRestoreIsLineBasedNotPercent()
    {
        // ONE 38%-anchor line contract (design-editpreview-rerender-restore.md):
        // the percent ScrollToProgress restore is DELETED on this surface — the
        // rendered event drives the editor->preview line re-assert instead.
        // Only the ReadingProgress stomp-guard remains (bool, not percent value).
        var codeBehind = ReadEditPreviewCodeBehind();
        var applyRender = ExtractMethodBody(codeBehind, "private void ApplyWebPreviewSource()");
        var scrollHandler = ExtractMethodBody(codeBehind, "private void OnSharedScrollStateChanged(object? sender, ApplicateWebDocumentScrollEventArgs e)");
        var renderedHandler = ExtractMethodBody(codeBehind, "private void OnSharedDocumentRendered(object? sender, EventArgs e)");

        Assert.Contains("_awaitingRenderRestore = !_sharedHost.View.HasLoadedDocumentForSource(source);", applyRender, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ReadingProgress = e.ProgressPercent;", scrollHandler, StringComparison.Ordinal);
        Assert.Contains("_awaitingRenderRestore && !_sharedHost.View.LastLayoutReadyWasCached", scrollHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollToProgress", renderedHandler, StringComparison.Ordinal);
        Assert.Contains("SourceLineScrollSyncPreviewRendered?.Invoke(this, EventArgs.Empty);", renderedHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void EditSurfaceKeepsPerPaneStateWithoutEntrySeed()
    {
        // User-chosen model: each pane keeps ITS OWN state across mode
        // switches; there is NO cross-mode entry seed dragging the edit
        // surface to the reading anchor. The rendered-event re-assert stays
        // unconditional (editor owns the position while editing).
        var workspace = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MarkMello.Presentation", "Views", "EditWorkspaceView.axaml.cs"));

        Assert.DoesNotContain("TryApplyEditEntrySeed", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadingAnchorSourceLine", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Offset.Y: 0", workspace, StringComparison.Ordinal);
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
