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

    [Fact]
    public async Task CancellingTheCreatingCallerDoesNotCancelAnotherCallersWait()
    {
        // The creating caller "owns" the render only in the sense that it
        // happened to arrive first. Its cancellation must withdraw ITS OWN wait
        // and nothing else: a caller that joined the same key still has a
        // healthy token, and must still get the body.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        using var creatorCancellation = new CancellationTokenSource();
        var renderEntered = new TaskCompletionSource();
        var releaseRender = new TaskCompletionSource();
        var joinerStartedItsOwnRender = false;

        var creator = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async token =>
            {
                renderEntered.SetResult();
                await releaseRender.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return Rendered("<h1>Title</h1>");
            },
            creatorCancellation.Token);

        // The joiner arrives only once the render is parked inside the
        // delegate, so it can take nothing but the coalescing path. Nothing here
        // depends on a race staying observable on a fast machine.
        await renderEntered.Task;

        var joiner = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                joinerStartedItsOwnRender = true;
                return Task.FromResult(Rendered("<h1>Second render</h1>"));
            },
            CancellationToken.None);

        creatorCancellation.Cancel();
        releaseRender.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);

        var body = await joiner;
        Assert.Equal("<h1>Title</h1>", body.BodyHtml);
        Assert.False(joinerStartedItsOwnRender);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task CancellingTheOnlyCallerCancelsTheSharedRender()
    {
        // The other half of the contract: isolation must not turn into a render
        // that nobody wants running on to completion. This cache holds very few
        // entries, so an abandoned render occupies a slot that matters.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        using var cancellation = new CancellationTokenSource();
        var renderEntered = new TaskCompletionSource();
        var renderSawCancellation = new TaskCompletionSource();

        var only = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async token =>
            {
                renderEntered.SetResult();
                await ParkUntilCancelledAsync(token).ConfigureAwait(false);
                renderSawCancellation.SetResult();
                token.ThrowIfCancellationRequested();
                return Rendered("<h1>Unwanted</h1>");
            },
            cancellation.Token);

        await renderEntered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);

        // Awaited, not polled: the render is parked on its own token and can
        // resume for no other reason, so this completes if and only if the
        // shared render was really cancelled.
        await renderSawCancellation.Task;
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task CancellingOneJoinerLeavesTheRenderRunningForTheOthers()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        using var leavingCancellation = new CancellationTokenSource();
        var renderEntered = new TaskCompletionSource();
        var releaseRender = new TaskCompletionSource();

        var creator = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async token =>
            {
                renderEntered.SetResult();
                await releaseRender.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return Rendered("<h1>Title</h1>");
            },
            CancellationToken.None);

        await renderEntered.Task;

        var leaving = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => throw new InvalidOperationException("A joiner must not start its own render."),
            leavingCancellation.Token);
        var staying = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => throw new InvalidOperationException("A joiner must not start its own render."),
            CancellationToken.None);

        leavingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving);

        releaseRender.SetResult();

        Assert.Equal("<h1>Title</h1>", (await creator).BodyHtml);
        Assert.Equal("<h1>Title</h1>", (await staying).BodyHtml);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task EveryCallerWithdrawingCancelsTheRenderAndLeavesTheKeyRenderableAgain()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        using var creatorCancellation = new CancellationTokenSource();
        using var joinerCancellation = new CancellationTokenSource();
        var renderEntered = new TaskCompletionSource();
        var renderSawCancellation = new TaskCompletionSource();

        var creator = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async token =>
            {
                renderEntered.SetResult();
                await ParkUntilCancelledAsync(token).ConfigureAwait(false);
                renderSawCancellation.SetResult();
                token.ThrowIfCancellationRequested();
                return Rendered("<h1>Unwanted</h1>");
            },
            creatorCancellation.Token);

        await renderEntered.Task;

        var joiner = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => throw new InvalidOperationException("A joiner must not start its own render."),
            joinerCancellation.Token);

        creatorCancellation.Cancel();
        joinerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creator);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        await renderSawCancellation.Task;

        Assert.Equal(0, cache.InFlightCount);

        // No leaked key: the next caller renders instead of joining a render
        // that can never complete.
        var fresh = await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => Task.FromResult(Rendered("<h1>Fresh</h1>")),
            CancellationToken.None);

        Assert.Equal("<h1>Fresh</h1>", fresh.BodyHtml);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task InFlightIsDrainedAfterASuccessfulRender()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");

        await cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => Task.FromResult(Rendered("<h1>Title</h1>")),
            CancellationToken.None);

        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task AFailedRenderReachesEveryWaiterAndDrainsInFlight()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 2);
        var source = new MarkdownSource("doc.md", "doc.md", "# Title");
        var renderEntered = new TaskCompletionSource();
        var releaseRender = new TaskCompletionSource();

        var creator = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            async _ =>
            {
                renderEntered.SetResult();
                await releaseRender.Task.ConfigureAwait(false);
                throw new InvalidOperationException("render failed");
            },
            CancellationToken.None);

        await renderEntered.Task;

        var joiner = cache.GetOrRenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => throw new InvalidOperationException("A joiner must not start its own render."),
            CancellationToken.None);

        releaseRender.SetResult();

        Assert.Equal("render failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => creator)).Message);
        Assert.Equal("render failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => joiner)).Message);
        Assert.Equal(0, cache.InFlightCount);
    }

    /// <summary>
    /// Timer-free park: the render resumes only when the token it was handed
    /// actually fires. A test that wrongly expects a cancellation therefore
    /// hangs and is reported as a hang, instead of passing because some delay
    /// happened to be long enough.
    /// </summary>
    private static async Task ParkUntilCancelledAsync(CancellationToken token)
    {
        // RunContinuationsAsynchronously so this method never resumes INSIDE the
        // cancellation callback, which would make the registration's disposal a
        // self-wait.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => parked.TrySetResult());
        await parked.Task.ConfigureAwait(false);
    }

    private static ApplicateRenderedBody Rendered(string html)
        => new(
            html,
            PlainText: string.Empty,
            Array.Empty<ApplicateHtmlHeading>(),
            Array.Empty<ApplicateHtmlBlockMarker>(),
            new[] { html.Length },
            HasMermaidBlock: false,
            HasCodeBlockWithSyntax: false);

    private sealed class FakeImageSourceResolver : MarkMello.Application.Abstractions.IImageSourceResolver
    {
        public Task<Stream?> TryOpenAsync(string url, string? baseDirectory, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(null);
    }
}
