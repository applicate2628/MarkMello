using System.Collections.Concurrent;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// The background tab prefetcher's POLICY: which documents it warms, in what
/// order, how many, which it declines, and when it stops. The rendering itself
/// is faked — these tests own the decisions, not the renderer.
/// </summary>
public sealed class ApplicateBackgroundTabPrefetcherTests
{
    [Fact]
    public void OrderCandidatesExcludesTheActiveDocument()
    {
        var snapshot = new ApplicatePrefetchSnapshot(
            OpenPaths: Paths("a.md", "b.md", "c.md"),
            ActivePath: "b.md",
            RecentPaths: Array.Empty<string>());

        var candidates = ApplicateBackgroundTabPrefetcher.OrderCandidates(snapshot);

        Assert.Equal(Paths("a.md", "c.md"), candidates);
    }

    [Fact]
    public void OrderCandidatesOrdersByTheRecentFilesOwner()
    {
        // The D11 recent-files (MRU) list is the repository's accepted owner of
        // recent-file state; the prefetcher reuses that order instead of
        // inventing a second one. (What that order MEANS for open tabs is F1 of
        // 2026-08-02-tab-prefetch-warms-the-wrong-three-documents — an open
        // defect against the MRU owner, not against this sort.)
        var snapshot = new ApplicatePrefetchSnapshot(
            OpenPaths: Paths("a.md", "b.md", "c.md", "d.md"),
            ActivePath: "a.md",
            RecentPaths: Paths("a.md", "d.md", "b.md", "c.md"));

        var candidates = ApplicateBackgroundTabPrefetcher.OrderCandidates(snapshot);

        Assert.Equal(Paths("d.md", "b.md", "c.md"), candidates);
    }

    [Fact]
    public void OrderCandidatesPlacesUnrankedTabsBehindRankedOnesInTabOrder()
    {
        // The MRU list is capped, so a long-lived tab can fall off it. Those
        // tabs are still candidates; they queue behind every ranked one, in
        // tab-strip order, which is also what makes the sort deterministic.
        var snapshot = new ApplicatePrefetchSnapshot(
            OpenPaths: Paths("unranked-1.md", "ranked.md", "unranked-2.md", "active.md"),
            ActivePath: "active.md",
            RecentPaths: Paths("ranked.md"));

        var candidates = ApplicateBackgroundTabPrefetcher.OrderCandidates(snapshot);

        Assert.Equal(Paths("ranked.md", "unranked-1.md", "unranked-2.md"), candidates);
    }

    [Fact]
    public void OrderCandidatesReturnsEveryNonActiveTabRatherThanTheBudget()
    {
        // Untruncated ON PURPOSE. Whether a document is cacheable is only
        // knowable after its text is materialized, so the pass — not this pure
        // sort — is what stops at the budget. A sort that cut the list to
        // MaxEntries - 1 is what let an uncacheable document eat a warm slot.
        // Six non-active tabs against a budget that is never more than three.
        var snapshot = new ApplicatePrefetchSnapshot(
            OpenPaths: Paths("active.md", "1.md", "2.md", "3.md", "4.md", "5.md", "6.md"),
            ActivePath: "active.md",
            RecentPaths: Array.Empty<string>());

        var candidates = ApplicateBackgroundTabPrefetcher.OrderCandidates(snapshot);

        Assert.Equal(Paths("1.md", "2.md", "3.md", "4.md", "5.md", "6.md"), candidates);
    }

    [Fact]
    public async Task PassPrefetchesAtMostOneFewerThanTheCacheCapacity()
    {
        // The active document's own entry lives in this same cache. A pass
        // allowed to fill all four slots would evict the document that started
        // it, which is the one entry that must survive.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("one.md", "# One");
        documents.Add("two.md", "# Two");
        documents.Add("three.md", "# Three");
        documents.Add("four.md", "# Four");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents),
            new ApplicatePrefetchSnapshot(
                OpenPaths: Paths("active.md", "one.md", "two.md", "three.md", "four.md"),
                ActivePath: "active.md",
                RecentPaths: Paths("active.md", "one.md", "two.md", "three.md", "four.md")));

        Assert.Equal(3, cache.MaxEntries - 1);
        Assert.Equal(
            Paths("one.md", "three.md", "two.md"),
            renderer.RenderedPaths().Order(StringComparer.Ordinal));
        Assert.DoesNotContain("four.md", renderer.RenderedPaths());
        Assert.DoesNotContain("active.md", renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassLeavesAWarmEntryTheNextRequestHitsWithoutRendering()
    {
        // The whole point: after the pass, the click that follows must not pay a
        // render. Asserted against the real cache, not against a counter.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("next.md", "# Next");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents),
            Snapshot("active.md", "active.md", "next.md"));

        Assert.Equal(Paths("next.md"), renderer.RenderedPaths());

        var clickRendered = false;
        var body = await cache.GetOrRenderAsync(
            new MarkdownSource("next.md", "next.md", "# Next"),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                clickRendered = true;
                return Task.FromResult(Rendered("<h1>cold</h1>"));
            },
            CancellationToken.None);

        Assert.False(clickRendered);
        Assert.Equal("<h1>next.md</h1>", body.BodyHtml);
    }

    [Fact]
    public async Task PassSkipsImageBearingDocumentsTheCacheWouldNeverStore()
    {
        // ApplicateRenderedBodyCache.CanCache returns false for a document that
        // may resolve images. Rendering one here would burn CPU and then discard
        // the result, so the candidate is declined before the render.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("with-image.md", "# Gallery\n\n![Alt](picture.png)");
        documents.Add("text-only.md", "# Text only");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents, imageSourceResolver: new FakeImageSourceResolver()),
            Snapshot("active.md", "active.md", "with-image.md", "text-only.md"));

        Assert.Equal(Paths("text-only.md"), renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassSpendsItsBudgetOnWarmsNotOnUncacheableCandidates()
    {
        // F2 of 2026-08-02-tab-prefetch-warms-the-wrong-three-documents: an
        // image-bearing document used to be admitted as one of the three
        // candidates and only then discarded by CanCache, so the pass returned
        // two warm documents instead of three. Every one of the 34 observed live
        // runs did exactly that.
        //
        // Two uncacheable documents rank AHEAD of storable ones here, so a
        // budget spent on attempts warms one document and a budget spent on
        // warms fills all three. maxConcurrency is 1 so the outcome does not
        // depend on any interleaving.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("image-1.md", "# Figure\n\n![Alt](one.png)");
        documents.Add("text-1.md", "# Text one");
        documents.Add("image-2.md", "# Figure\n\n<img src=\"two.png\">");
        documents.Add("text-2.md", "# Text two");
        documents.Add("text-3.md", "# Text three");

        await RunPassAsync(
            CreatePrefetcher(
                cache,
                renderer,
                documents,
                imageSourceResolver: new FakeImageSourceResolver(),
                maxConcurrency: 1),
            Snapshot(
                "active.md",
                "active.md",
                "image-1.md",
                "text-1.md",
                "image-2.md",
                "text-2.md",
                "text-3.md"));

        Assert.Equal(Paths("text-1.md", "text-2.md", "text-3.md"), renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassSpendsItsBudgetOnWarmsNotOnCandidatesWhoseTabsDisappeared()
    {
        // Same defect class as the uncacheable skip, and the reason the fix is
        // "a skip returns its slot" rather than "move the image check": a tab
        // closed between the snapshot and the pass stores nothing either, so it
        // must not cost a warm slot a live tab could have used.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("open-1.md", "# One");
        documents.Add("open-2.md", "# Two");
        documents.Add("open-3.md", "# Three");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents, maxConcurrency: 1),
            Snapshot(
                "active.md",
                "active.md",
                "closed-1.md",
                "open-1.md",
                "closed-2.md",
                "open-2.md",
                "open-3.md"));

        Assert.Equal(Paths("open-1.md", "open-2.md", "open-3.md"), renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassStillCannotEvictTheActiveDocumentWhenSkipsReturnTheirSlots()
    {
        // The counterweight to the two tests above. Returning a slot must not
        // become a way to overrun the budget: the pass may still leave at most
        // MaxEntries - 1 non-active entries, or the active document — already in
        // this same cache — is the one the LRU drops.
        //
        // The window is engineered rather than hoped for: both workers are held
        // inside their render until the test has seen both arrive, so the pass
        // is genuinely concurrent at the moment the budget is nearly spent,
        // which is the only state in which a count-successes-afterwards check
        // would let two workers claim the same last slot.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var activeSource = new MarkdownSource("active.md", "active.md", "# Active");
        await cache.GetOrRenderAsync(
            activeSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => Task.FromResult(Rendered("<h1>active</h1>")),
            CancellationToken.None);

        var bothWorkersRendering = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        var renderer = new RecordingRenderer(async (_, _) =>
        {
            bothWorkersRendering.Release();
            await release.Task.ConfigureAwait(false);
        });
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        var openPaths = new List<string> { "active.md" };
        for (var index = 0; index < 6; index++)
        {
            documents.Add($"doc-{index}.md", $"# Doc {index}");
            openPaths.Add($"doc-{index}.md");
        }

        var prefetcher = CreatePrefetcher(cache, renderer, documents, maxConcurrency: 2);
        var passTask = prefetcher.RunPassAsync(
            new ApplicatePrefetchSnapshot(openPaths, "active.md", openPaths),
            "test",
            new CancellationTokenSource());

        Assert.True(await bothWorkersRendering.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await bothWorkersRendering.WaitAsync(TimeSpan.FromSeconds(30)));
        release.SetResult();
        await passTask;
        prefetcher.Dispose();

        Assert.Equal(3, renderer.RenderedPaths().Length);

        // …and the document the pass was started BY is still in the cache: a
        // fourth warm would have pushed it out, which is the concrete harm the
        // MaxEntries - 1 budget exists to prevent.
        var activeReRendered = false;
        await cache.GetOrRenderAsync(
            activeSource,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                activeReRendered = true;
                return Task.FromResult(Rendered("<h1>evicted</h1>"));
            },
            CancellationToken.None);

        Assert.False(activeReRendered);
    }

    [Fact]
    public async Task PassDoesNotReRenderADocumentThatIsAlreadyCached()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var alreadyCached = new MarkdownSource("warm.md", "warm.md", "# Warm");
        await cache.GetOrRenderAsync(
            alreadyCached,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ => Task.FromResult(Rendered("<h1>already warm</h1>")),
            CancellationToken.None);

        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("warm.md", "# Warm");
        documents.Add("cold.md", "# Cold");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents),
            Snapshot("active.md", "active.md", "warm.md", "cold.md"));

        Assert.Equal(Paths("cold.md"), renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassStopsDequeuingCandidatesOnceCancelled()
    {
        // The cancellation window is engineered deterministically: both workers
        // park inside the render until the test releases them, so the third
        // candidate cannot be reached before Cancel lands. Nothing here depends
        // on a race staying observable on a fast machine.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var bothWorkersRendering = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        var renderer = new RecordingRenderer(async (_, _) =>
        {
            bothWorkersRendering.Release();
            await release.Task.ConfigureAwait(false);
        });
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("one.md", "# One");
        documents.Add("two.md", "# Two");
        documents.Add("three.md", "# Three");

        var prefetcher = CreatePrefetcher(cache, renderer, documents);
        var pass = new CancellationTokenSource();
        var passTask = prefetcher.RunPassAsync(
            Snapshot("active.md", "active.md", "one.md", "two.md", "three.md"),
            "test",
            pass);

        Assert.True(await bothWorkersRendering.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(await bothWorkersRendering.WaitAsync(TimeSpan.FromSeconds(30)));

        pass.Cancel();

        // The two parked renders are released AFTER the cancel and are expected
        // to finish: cancellation stops the pass from taking NEW work, and
        // deliberately does not abort a render already inside the cache.
        release.SetResult();
        await passTask;

        Assert.Equal(2, renderer.StartedCount);
        Assert.DoesNotContain("three.md", documents.MaterializedPaths());
    }

    [Fact]
    public async Task CancellingAPassDoesNotCancelARenderTheUserIsWaitingOn()
    {
        // The hazard this pins down: ApplicateRenderedBodyCache coalesces on a
        // SHARED in-flight task (:63-79), and completes it as CANCELLED when the
        // render's own token fires (:87-91) — which every other waiter then
        // observes, whatever its own token says. The user's tab click is such a
        // waiter, and clicking a tab is what raises ActiveDocumentChanged, one
        // of the signals that cancels a pass. So a prefetch that threaded its
        // pass token into the cache could abort the very render the user is
        // waiting on.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var prefetchRendering = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();
        var renderer = new RecordingRenderer(async (_, _) =>
        {
            prefetchRendering.Release();
            await release.Task.ConfigureAwait(false);
        });
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("contested.md", "# Contested");

        var prefetcher = CreatePrefetcher(cache, renderer, documents, maxConcurrency: 1);
        var pass = new CancellationTokenSource();
        var passTask = prefetcher.RunPassAsync(
            Snapshot("active.md", "active.md", "contested.md"),
            "test",
            pass);

        Assert.True(await prefetchRendering.WaitAsync(TimeSpan.FromSeconds(30)));

        // The user clicks the tab the prefetch is mid-render on: this joins the
        // prefetch's in-flight task rather than starting a second render.
        var userRenderStarted = false;
        var userRender = cache.GetOrRenderAsync(
            new MarkdownSource("contested.md", "contested.md", "# Contested"),
            ReadingPreferences.Default,
            imageSourceResolver: null,
            _ =>
            {
                userRenderStarted = true;
                return Task.FromResult(Rendered("<h1>second render</h1>"));
            },
            CancellationToken.None);

        pass.Cancel();
        release.SetResult();

        var body = await userRender;
        await passTask;

        Assert.False(userRenderStarted);
        Assert.Equal("<h1>contested.md</h1>", body.BodyHtml);
    }

    [Fact]
    public async Task PassKeepsAtMostTwoRendersInFlight()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 8);
        var release = new TaskCompletionSource();
        var concurrent = 0;
        var peak = 0;
        var renderer = new RecordingRenderer(async (_, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref concurrent);
        });
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        for (var index = 0; index < 6; index++)
        {
            documents.Add($"doc-{index}.md", $"# Doc {index}");
        }

        var openPaths = new List<string> { "active.md" };
        for (var index = 0; index < 6; index++)
        {
            openPaths.Add($"doc-{index}.md");
        }

        // maxConcurrency is passed EXPLICITLY and asserted against a literal, so
        // this test measures the worker-spawning logic rather than comparing the
        // policy constant against itself.
        var prefetcher = CreatePrefetcher(cache, renderer, documents, maxConcurrency: 2);
        var pass = new CancellationTokenSource();
        var passTask = prefetcher.RunPassAsync(
            new ApplicatePrefetchSnapshot(openPaths, "active.md", openPaths),
            "test",
            pass);

        // Let both workers reach the render before releasing them, then drain.
        while (Volatile.Read(ref peak) < 2)
        {
            await Task.Yield();
        }

        release.SetResult();
        await passTask;

        Assert.Equal(2, peak);
        Assert.Equal(6, renderer.RenderedPaths().Length);

        // …and the shipped default is that same 2.
        Assert.Equal(2, ApplicateBackgroundTabPrefetcher.DefaultMaxConcurrency);
    }

    [Fact]
    public async Task TriggerDeclinesASecondPassWhileOneIsInFlight()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var release = new TaskCompletionSource();
        var firstRenderStarted = new SemaphoreSlim(0);
        var renderer = new RecordingRenderer(async (_, _) =>
        {
            firstRenderStarted.Release();
            await release.Task.ConfigureAwait(false);
        });
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("one.md", "# One");

        using var prefetcher = CreatePrefetcher(cache, renderer, documents);
        var snapshot = Snapshot("active.md", "active.md", "one.md");

        prefetcher.Trigger(snapshot, "first");
        Assert.True(await firstRenderStarted.WaitAsync(TimeSpan.FromSeconds(30)));

        prefetcher.Trigger(snapshot, "second-while-in-flight");

        release.SetResult();

        // Only the first pass ever materialized the candidate; the second was
        // declined rather than queued, so a burst of state events cannot
        // multiply the work.
        while (Volatile.Read(ref renderer.CompletedCountField) == 0)
        {
            await Task.Yield();
        }

        Assert.Equal(1, renderer.StartedCount);
    }

    [Fact]
    public async Task PassWithNoOtherOpenTabsRendersNothing()
    {
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents),
            Snapshot("active.md", "active.md"));

        Assert.Empty(renderer.RenderedPaths());
    }

    [Fact]
    public async Task PassSkipsACandidateWhoseTabDisappearedAfterTheSnapshot()
    {
        // The snapshot is deliberately allowed to go stale; the document source
        // re-checks against the live service, and a vanished tab is skipped
        // rather than failing the pass.
        var cache = new ApplicateRenderedBodyCache(maxEntries: 4);
        var renderer = new RecordingRenderer();
        var documents = new FakeDocumentSource();
        documents.Add("active.md", "# Active");
        documents.Add("still-open.md", "# Still open");

        await RunPassAsync(
            CreatePrefetcher(cache, renderer, documents),
            Snapshot("active.md", "active.md", "closed.md", "still-open.md"));

        Assert.Equal(Paths("still-open.md"), renderer.RenderedPaths());
    }

    /// <summary>
    /// Identity over a <c>params</c> array. Constant array literals passed
    /// directly as arguments trip CA1861, which is an error in this test
    /// project; a params call site is exempt and keeps the expectation readable
    /// at its assertion instead of hoisted into a static field far away.
    /// </summary>
    private static string[] Paths(params string[] paths) => paths;

    private static ApplicatePrefetchSnapshot Snapshot(string activePath, params string[] openPaths)
        => new(openPaths, activePath, openPaths);

    private static ApplicateBackgroundTabPrefetcher CreatePrefetcher(
        ApplicateRenderedBodyCache cache,
        IApplicateHtmlMarkdownRenderer renderer,
        IApplicatePrefetchDocumentSource documents,
        IImageSourceResolver? imageSourceResolver = null,
        int maxConcurrency = ApplicateBackgroundTabPrefetcher.DefaultMaxConcurrency)
        => new(cache, renderer, new StubSettingsStore(), imageSourceResolver, documents, maxConcurrency);

    private static async Task RunPassAsync(
        ApplicateBackgroundTabPrefetcher prefetcher,
        ApplicatePrefetchSnapshot snapshot)
    {
        using (prefetcher)
        {
            // RunPassAsync takes ownership of the source and disposes it.
            await prefetcher.RunPassAsync(snapshot, "test", new CancellationTokenSource());
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
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

    private sealed class FakeDocumentSource : IApplicatePrefetchDocumentSource
    {
        private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _materialized = new();

        /// <summary>A stable snapshot — the live queue can still be written by a worker.</summary>
        public string[] MaterializedPaths() => _materialized.ToArray();

        public void Add(string path, string content) => _contents[path] = content;

        public Task<MarkdownSource?> TryMaterializeAsync(string path, CancellationToken cancellationToken)
        {
            _materialized.Enqueue(path);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_contents.TryGetValue(path, out var content)
                ? new MarkdownSource(path, path, content)
                : null);
        }
    }

    private sealed class RecordingRenderer : IApplicateHtmlMarkdownRenderer
    {
        private readonly Func<MarkdownSource, CancellationToken, Task>? _beforeReturn;
        private readonly ConcurrentQueue<string> _rendered = new();
        private int _startedCount;

        internal int CompletedCountField;

        public RecordingRenderer(Func<MarkdownSource, CancellationToken, Task>? beforeReturn = null)
        {
            _beforeReturn = beforeReturn;
        }

        /// <summary>A stable snapshot — the live queue can still be written by a worker.</summary>
        public string[] RenderedPaths() => _rendered.ToArray();

        public int StartedCount => Volatile.Read(ref _startedCount);

        public async Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startedCount);
            if (_beforeReturn is not null)
            {
                await _beforeReturn(source, cancellationToken).ConfigureAwait(false);
            }

            _rendered.Enqueue(source.Path);
            Interlocked.Increment(ref CompletedCountField);
            return Rendered($"<h1>{source.Path}</h1>");
        }

        // The prefetch warms the BODY cache only; reaching the shell renderer or
        // the table-cell path from here would be a defect, so both fail loudly.
        public Task<ApplicateHtmlDocument> RenderAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The prefetch must not render a document shell.");

        public Task<string> RenderTableCellHtmlAsync(
            string rawCellMarkdown,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The prefetch must not render table cells.");
    }

    private sealed class FakeImageSourceResolver : IImageSourceResolver
    {
        public Task<Stream?> TryOpenAsync(string url, string? baseDirectory, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(null);
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReadingPreferences.Default);

        public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ThemeMode.Light);

        public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AppLanguage.English);

        public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WindowPlacement?>(null);

        public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
