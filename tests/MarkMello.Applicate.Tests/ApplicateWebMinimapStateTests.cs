using System.Text.Json;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateWebMinimapStateTests
{
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

    [Theory]
    [InlineData("""{"type":"scroll"}""")]
    [InlineData("""{"type":42}""")]
    [InlineData("""{}""")]
    public void RejectsNonViewerInteractionMessages(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.False(ApplicateWebMarkdownDocumentView.IsViewerInteractionMessage(document.RootElement));
    }
}
