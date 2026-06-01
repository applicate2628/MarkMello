using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;

namespace MarkMello.Presentation.Tests;

public sealed class RenderMarkdownDocumentUseCaseTests
{
    [Fact]
    public void ExecuteReusesRenderedDocumentForSameMarkdownInstance()
    {
        var renderer = new CountingMarkdownRenderer();
        var useCase = new RenderMarkdownDocumentUseCase(renderer);

        var first = useCase.Execute("# Heavy\n\nbody", @"D:\docs");
        var second = useCase.Execute("# Heavy\n\nbody", @"D:\docs");

        Assert.Same(first, second);
        Assert.Equal(1, renderer.RenderCount);
    }

    [Fact]
    public void ExecuteRerendersWhenContentChanges()
    {
        var renderer = new CountingMarkdownRenderer();
        var useCase = new RenderMarkdownDocumentUseCase(renderer);

        useCase.Execute("# Heavy\n\nbody", @"D:\docs");
        useCase.Execute("# Heavy\n\nedited", @"D:\docs");

        Assert.Equal(2, renderer.RenderCount);
    }

    private sealed class CountingMarkdownRenderer : IMarkdownDocumentRenderer
    {
        public int RenderCount { get; private set; }

        public RenderedMarkdownDocument Render(string markdown)
            => Render(markdown, baseDirectory: null);

        public RenderedMarkdownDocument Render(string markdown, string? baseDirectory)
        {
            RenderCount++;
            return new RenderedMarkdownDocument(
            [
                new MarkdownParagraphBlock(
                [
                    new MarkdownTextInline($"{RenderCount}:{markdown}")
                ])
            ],
            baseDirectory);
        }
    }
}
