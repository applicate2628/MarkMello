using System.Text.Json;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMinimapStateTests
{
    [Theory]
    [InlineData("dark", "<html data-theme=\"dark\">")]
    [InlineData("light", "<html data-theme=\"light\">")]
    [InlineData("classic-white", "<html data-theme=\"classic-white\">")]
    [InlineData("unknown", "<html data-theme=\"light\">")]
    public void InitialThemeIsEmbeddedBeforeNavigation(string theme, string expectedHtmlTag)
    {
        const string html = "<!doctype html>\n<html>\n<head></head><body></body></html>";

        var themed = ApplicateWebMarkdownDocumentView.ApplyInitialThemeForTesting(html, theme);

        Assert.Contains(expectedHtmlTag, themed, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialThemePreservesExistingHtmlAttributes()
    {
        const string html = "<!doctype html>\n<html data-mm-chrome=\"off\">\n<head></head><body></body></html>";

        var themed = ApplicateWebMarkdownDocumentView.ApplyInitialThemeForTesting(html, "dark");

        Assert.Contains("<html data-theme=\"dark\" data-mm-chrome=\"off\">", themed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dark", "dark", 50, 100, true)]
    [InlineData("dark", "dark", 101, 100, false)]
    [InlineData("dark", "classic-white", 50, 100, false)]
    [InlineData("dark", null, 50, 100, false)]
    public void DuplicateThemePostSuppressionOnlyDropsSameThemeWithinBurst(
        string theme,
        string? lastTheme,
        int elapsedMs,
        int duplicateWindowMs,
        bool expected)
    {
        var actual = ApplicateWebMarkdownDocumentView.IsDuplicateThemePostWithinWindow(
            theme,
            lastTheme,
            TimeSpan.FromMilliseconds(elapsedMs),
            TimeSpan.FromMilliseconds(duplicateWindowMs));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParserAcceptsVisibleStateWithFiniteReservation()
    {
        using var document = JsonDocument.Parse("""{"visible":true,"reservedWidth":168.5}""");

        var parsed = ApplicateWebMarkdownDocumentView.TryReadMinimapState(
            document.RootElement,
            out var state);

        Assert.True(parsed);
        Assert.NotNull(state);
        Assert.True(state.Visible);
        Assert.Equal(168.5, state.ReservedWidth);
    }

    [Fact]
    public void ParserAcceptsHiddenStateWithoutReservation()
    {
        using var document = JsonDocument.Parse("""{"visible":false}""");

        var parsed = ApplicateWebMarkdownDocumentView.TryReadMinimapState(
            document.RootElement,
            out var state);

        Assert.True(parsed);
        Assert.NotNull(state);
        Assert.False(state.Visible);
        Assert.Equal(0, state.ReservedWidth);
    }

    [Fact]
    public void ParserAcceptsTransactionMinimapSettledState()
    {
        using var document = JsonDocument.Parse("""{"type":"minimap-settled","transactionGeneration":42,"visible":true,"reservedWidth":168}""");

        var parsed = ApplicateWebMarkdownDocumentView.TryReadMinimapSettledState(
            document.RootElement,
            out var settled);

        Assert.True(parsed);
        Assert.NotNull(settled);
        Assert.Equal(42, settled.TransactionGeneration);
        Assert.True(settled.State.Visible);
        Assert.Equal(168, settled.State.ReservedWidth);
    }

    [Theory]
    [InlineData("""{"type":"minimap-state","transactionGeneration":42,"visible":true,"reservedWidth":168}""")]
    [InlineData("""{"type":"minimap-settled","transactionGeneration":0,"visible":true,"reservedWidth":168}""")]
    [InlineData("""{"type":"minimap-settled","transactionGeneration":"42","visible":true,"reservedWidth":168}""")]
    [InlineData("""{"type":"minimap-settled","transactionGeneration":42,"visible":true}""")]
    public void ParserRejectsMalformedTransactionMinimapSettledState(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ApplicateWebMarkdownDocumentView.TryReadMinimapSettledState(
            document.RootElement,
            out var settled);

        Assert.False(parsed);
        Assert.Null(settled);
    }

    [Fact]
    public void ParserAcceptsTaggedModeToggleSettledState()
    {
        using var document = JsonDocument.Parse("""{"type":"mode-toggle-settled","transactionGeneration":42}""");

        var parsed = ApplicateWebMarkdownDocumentView.TryReadModeToggleSettledState(
            document.RootElement,
            out var settled);

        Assert.True(parsed);
        Assert.NotNull(settled);
        Assert.True(settled.IsTransactional);
        Assert.Equal(42, settled.TransactionGeneration);
    }

    [Fact]
    public void ParserAcceptsUntaggedModeToggleSettledStateAsLegacy()
    {
        using var document = JsonDocument.Parse("""{"type":"mode-toggle-settled"}""");

        var parsed = ApplicateWebMarkdownDocumentView.TryReadModeToggleSettledState(
            document.RootElement,
            out var settled);

        Assert.True(parsed);
        Assert.NotNull(settled);
        Assert.False(settled.IsTransactional);
        Assert.Equal(0, settled.TransactionGeneration);
    }

    [Theory]
    [InlineData("""{"type":"mode-toggle-settled","transactionGeneration":0}""")]
    [InlineData("""{"type":"mode-toggle-settled","transactionGeneration":"42"}""")]
    [InlineData("""{"type":"minimap-settled","transactionGeneration":42}""")]
    public void ParserRejectsMalformedTaggedModeToggleSettledState(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ApplicateWebMarkdownDocumentView.TryReadModeToggleSettledState(
            document.RootElement,
            out var settled);

        Assert.False(parsed);
        Assert.Null(settled);
    }

    [Theory]
    [InlineData("""{"reservedWidth":168}""")]
    [InlineData("""{"visible":"true","reservedWidth":168}""")]
    [InlineData("""{"visible":true}""")]
    [InlineData("""{"visible":true,"reservedWidth":"168"}""")]
    [InlineData("""{"visible":true,"reservedWidth":-1}""")]
    [InlineData("""{"visible":true,"reservedWidth":2001}""")]
    public void ParserRejectsMalformedOrOutOfRangeState(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ApplicateWebMarkdownDocumentView.TryReadMinimapState(
            document.RootElement,
            out var state);

        Assert.False(parsed);
        Assert.Null(state);
    }

    [Theory]
    [InlineData(WidthResizerVisibility.Always, "always")]
    [InlineData(WidthResizerVisibility.OnHover, "on-hover")]
    [InlineData((WidthResizerVisibility)42, "on-hover")]
    public void RendererWidthResizerVisibilityUsesStableWireValues(
        WidthResizerVisibility visibility,
        string expected)
    {
        var actual = ApplicateWebMarkdownDocumentView.ToRendererWidthResizerVisibility(visibility);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RecognizesViewerInteractionMessage()
    {
        using var document = JsonDocument.Parse("""{"type":"viewer-interaction"}""");

        Assert.True(ApplicateWebMarkdownDocumentView.IsViewerInteractionMessage(document.RootElement));
    }

    [Fact]
    public void RecognizesLayoutReadyMessage()
    {
        using var document = JsonDocument.Parse("""{"type":"layout-ready","scrollTop":0,"scrollHeight":1200,"clientHeight":800}""");

        Assert.True(ApplicateWebMarkdownDocumentView.IsLayoutReadyMessage(document.RootElement));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void WebRendererCompletesAfterLoadedDocumentAndLayoutAreReady(
        bool hasLoadedDocument,
        bool hasLayoutReady,
        bool hasMinimapState,
        bool expected)
    {
        // hasMinimapState is retained on the call site for source-compat with
        // callers that still propagate the signal, but it is no longer part
        // of the completion gate — see ApplicateWebMarkdownDocumentViewShell-
        // ModeTests.ShouldCompleteRenderGatesOnLoadedDocumentAndLayoutReady
        // for the rationale (F-04 multi-fire + cancelled pipeline starves
        // minimapSourceReady; minimap visibility is driven by its own
        // observer chain post-completion).
        var actual = ApplicateWebMarkdownDocumentView.ShouldCompleteRenderForTesting(
            hasLoadedDocument,
            hasLayoutReady,
            hasMinimapState);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, false, true, false, false, false, 2)]
    [InlineData(false, true, true, false, false, false, 2)]
    [InlineData(false, false, false, false, false, false, 2)]
    [InlineData(false, false, true, true, false, false, 1)]
    [InlineData(false, false, true, false, true, false, 1)]
    [InlineData(false, false, true, false, false, true, 1)]
    [InlineData(false, false, true, false, false, false, 0)]
    public void BatchedWebInputUpdateAvoidsIntermediateRenders(
        bool sourceChanged,
        bool imageSourceResolverChanged,
        bool hasLoadedDocument,
        bool readingPreferencesChanged,
        bool availableContentWidthChanged,
        bool viewerChromeEnabledChanged,
        int expected)
    {
        var actual = ApplicateWebMarkdownDocumentView.DetermineInputUpdateAction(
            sourceChanged,
            imageSourceResolverChanged,
            hasLoadedDocument,
            readingPreferencesChanged,
            availableContentWidthChanged,
            viewerChromeEnabledChanged);

        Assert.Equal(expected, (int)actual);
    }

    [Theory]
    [InlineData("""{"type":"wheel","deltaY":120,"deltaMode":0}""", 120, 0)]
    [InlineData("""{"type":"wheel","deltaY":-3}""", -3, 0)]
    public void ParserAcceptsWheelMessage(string json, double expectedDeltaY, int expectedDeltaMode)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ApplicateWebMarkdownDocumentView.TryReadWheelMessage(
            document.RootElement,
            out var wheel);

        Assert.True(parsed);
        Assert.NotNull(wheel);
        Assert.Equal(expectedDeltaY, wheel.DeltaY);
        Assert.Equal(expectedDeltaMode, wheel.DeltaMode);
    }

    [Theory]
    [InlineData("""{"type":"wheel"}""")]
    [InlineData("""{"type":"wheel","deltaY":"120","deltaMode":0}""")]
    [InlineData("""{"type":"wheel","deltaY":120,"deltaMode":3}""")]
    [InlineData("""{"type":"wheel","deltaY":10001,"deltaMode":0}""")]
    public void ParserRejectsMalformedWheelMessage(string json)
    {
        using var document = JsonDocument.Parse(json);

        var parsed = ApplicateWebMarkdownDocumentView.TryReadWheelMessage(
            document.RootElement,
            out var wheel);

        Assert.False(parsed);
        Assert.Null(wheel);
    }

    [Theory]
    [InlineData("""{"type":"scroll"}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("""{}""")]
    public void RejectsNonViewerInteractionMessages(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(ApplicateWebMarkdownDocumentView.IsViewerInteractionMessage(document.RootElement));
    }

    [Theory]
    [InlineData("""{"type":"document-ready"}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("""{}""")]
    public void RejectsNonLayoutReadyMessages(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(ApplicateWebMarkdownDocumentView.IsLayoutReadyMessage(document.RootElement));
    }
}
