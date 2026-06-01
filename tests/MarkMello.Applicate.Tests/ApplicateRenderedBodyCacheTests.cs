using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateRenderedBodyCacheTests
{
    [Fact]
    public async Task GetOrRenderAsyncReusesRenderedBodyForSameSourceAndPreferences()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        var calls = 0;

        var first = await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title</h1>"));
            },
            CancellationToken.None);
        var second = await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title again</h1>"));
            },
            CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal("<h1>Title</h1>", second.BodyHtml);
    }

    [Fact]
    public async Task GetOrRenderAsyncCoalescesConcurrentRendersForSameSource()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        var releaseRender = new TaskCompletionSource();
        var calls = 0;

        var first = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async _ =>
            {
                calls++;
                await releaseRender.Task;
                return Rendered("<h1>Title</h1>");
            },
            CancellationToken.None);
        var second = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Duplicate</h1>"));
            },
            CancellationToken.None);

        await Task.Delay(50);
        Assert.Equal(1, calls);

        releaseRender.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal("<h1>Title</h1>", results[1].BodyHtml);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrRenderAsyncRendersAgainWhenSourceContentChanges()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var firstSource = new MarkdownSource("doc.md", "doc.md", "# One");
        var secondSource = firstSource with { Content = "# Two" };
        var calls = 0;

        await cache.GetOrRenderAsync(
            firstSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>One</h1>"));
            },
            CancellationToken.None);
        var second = await cache.GetOrRenderAsync(
            secondSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Two</h1>"));
            },
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("<h1>Two</h1>", second.BodyHtml);
    }

    [Fact]
    public async Task GetOrRenderAsyncReusesRenderedBodyWhenReadingPreferencesChange()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        var calls = 0;

        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title</h1>"));
            },
            CancellationToken.None);
        var second = await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default with { FontSize = ReadingPreferences.Default.FontSize + 1 },
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title again</h1>"));
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("<h1>Title</h1>", second.BodyHtml);
    }

    [Fact]
    public async Task GetOrRenderAsyncKeepsPathScopedSourcesSeparate()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var firstSource = new MarkdownSource(@"C:\docs\one\doc.md", "doc.md", "[next](next.md)");
        var secondSource = new MarkdownSource(@"C:\docs\two\doc.md", "doc.md", "[next](next.md)");
        var calls = 0;

        await cache.GetOrRenderAsync(
            firstSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<a href=\"file:///C:/docs/one/next.md\">next</a>"));
            },
            CancellationToken.None);
        var second = await cache.GetOrRenderAsync(
            secondSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<a href=\"file:///C:/docs/two/next.md\">next</a>"));
            },
            CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal("<a href=\"file:///C:/docs/two/next.md\">next</a>", second.BodyHtml);
    }

    [Fact]
    public async Task GetOrRenderAsyncDoesNotCacheWhenImageResolverMayAffectOutput()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "![Alt](image.png)");
        var resolver = new FakeImageSourceResolver();
        var calls = 0;

        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered($"<img src=\"{calls}\" />"));
            },
            CancellationToken.None);
        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered($"<img src=\"{calls}\" />"));
            },
            CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrRenderAsyncCachesTextDocumentWhenImageResolverIsPresent()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title\n\nNo images here.");
        var resolver = new FakeImageSourceResolver();
        var calls = 0;

        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title</h1>"));
            },
            CancellationToken.None);
        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            resolver,
            _ =>
            {
                calls++;
                return Task.FromResult(Rendered("<h1>Title again</h1>"));
            },
            CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrRenderAsyncEvictsLeastRecentlyUsedEntry()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var first = new MarkdownSource("first.md", "first.md", "# First");
        var second = new MarkdownSource("second.md", "second.md", "# Second");
        var third = new MarkdownSource("third.md", "third.md", "# Third");
        var calls = 0;

        await Render(first);
        await Render(second);
        await Render(first);
        await Render(third);
        await Render(second);

        Assert.Equal(4, calls);

        Task<ApplicateRenderedBody> Render(MarkdownSource source)
            => cache.GetOrRenderAsync(
                source,
                ReadingPreferences.Default,
                imageSourceResolver: null,
                _ =>
                {
                    calls++;
                    return Task.FromResult(Rendered($"<h1>{source.FileName}</h1>"));
                },
                CancellationToken.None);
    }

    private static ApplicateRenderedBody Rendered(string html)
        => new(
            html,
            PlainText: string.Empty,
            Array.Empty<ApplicateHtmlHeading>(),
            Array.Empty<ApplicateHtmlBlockMarker>(),
            HasMermaidBlock: false,
            HasCodeBlockWithSyntax: false);

    private sealed class FakeImageSourceResolver : MarkMello.Application.Abstractions.IImageSourceResolver
    {
        public Task<Stream?> TryOpenAsync(string url, string? baseDirectory, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(null);
    }
}
