using System.Collections.Concurrent;
using System.Diagnostics;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Diagnostics;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// One UI-thread observation of the open-tab world, handed to
/// <see cref="ApplicateBackgroundTabPrefetcher.Trigger"/>. Taken by the caller
/// ON the UI thread because <c>OpenDocuments</c> is a live
/// <c>ObservableCollection</c> and <c>recentPaths</c> is the D11 owner's live
/// <c>List&lt;string&gt;</c> — enumerating either off-thread races its writer.
/// <para>
/// A plain value snapshot, deliberately NOT a reference to either live
/// collection: the pass runs on the thread pool for as long as a render takes,
/// and a stale-but-consistent list is the only thing safe to read there. Every
/// candidate is re-resolved against the live service before it is materialized,
/// so a tab closed mid-pass drops out rather than being prefetched from a stale
/// entry.
/// </para>
/// </summary>
/// <param name="OpenPaths">Open tab file paths, in tab-strip order.</param>
/// <param name="ActivePath">The active tab's path, excluded from candidates.</param>
/// <param name="RecentPaths">The D11 recent-files (MRU) order, most-recent-first,
/// read from its single writer-owner. Read-only here: decision
/// <c>2026-07-25-d11-mru-recent-files-ownership</c> clause 1 keeps the host
/// closure the ONE mutable owner, and this never writes it.</param>
internal sealed record ApplicatePrefetchSnapshot(
    IReadOnlyList<string> OpenPaths,
    string? ActivePath,
    IReadOnlyList<string> RecentPaths);

/// <summary>
/// The prefetcher's one dependency on the open-document world, inverted so the
/// policy owner below stays free of Avalonia and of <c>IOpenDocumentsService</c>
/// threading rules. The implementation
/// (<see cref="ApplicateOpenTabPrefetchDocumentSource"/>) is injected from the
/// composition site.
/// </summary>
internal interface IApplicatePrefetchDocumentSource
{
    /// <summary>
    /// Resolve <paramref name="path"/> to renderable text, loading a
    /// session-restore stub if needed. Returns <see langword="null"/> when the
    /// tab is gone or the file cannot be read — a prefetch is a pure
    /// optimisation, so an unreadable candidate is skipped, never surfaced.
    /// </summary>
    Task<MarkdownSource?> TryMaterializeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Warms the rendered-body cache for the OTHER open tabs once the active
/// document has actually painted, so the user's next tab click lands on a hit
/// instead of paying a cold render.
///
/// <para>This is the same proven path <c>Program.StartSessionStartupDocumentPreRead</c>
/// runs for the ONE startup document — read the text, then
/// <c>ApplicateRenderedBodyCache.GetOrRenderAsync</c> with a real
/// <c>RenderBodyAsync</c> delegate — applied to the other documents at a later
/// moment. Same swallow-and-trace failure handling: a missing or locked file is
/// non-fatal and the ordinary activation path re-attempts with its own typed
/// error surface.</para>
///
/// <para><b>This type is the single owner of the prefetch policy</b> — which
/// documents, in what order, how many, which are skipped, how many run at once,
/// when it stops, and what it measures. The window installs the trigger and
/// hands over a snapshot; it decides nothing.</para>
///
/// <para><b>No timers.</b> The pass is driven purely by the renderer's own state
/// events. If the active document never settles and never fails, no prefetch
/// happens — that fail-closed behaviour is correct and needs no timeout to
/// express.</para>
/// </summary>
internal sealed class ApplicateBackgroundTabPrefetcher : IDisposable
{
    /// <summary>
    /// Diagnostic group for every marker this owner emits. A dedicated group so
    /// <c>pass-end</c>'s hit/miss/render-ms numbers can be extracted on their own
    /// — they are what turns "the cache is warmer" into a measured claim, and
    /// what will size <c>ApplicateRenderedBodyCache.DefaultMaxEntries</c> instead
    /// of guessing at it.
    /// </summary>
    private const string TraceGroup = "perf-tab-prefetch";

    /// <summary>
    /// At most two renders in flight. This runs on the host process's thread
    /// pool alongside the UI thread; after first paint the UI is idle, but idle
    /// is not absent.
    /// </summary>
    internal const int DefaultMaxConcurrency = 2;

    private readonly ApplicateRenderedBodyCache _cache;
    private readonly IApplicateHtmlMarkdownRenderer _renderer;
    private readonly ISettingsStore _settings;
    private readonly IImageSourceResolver? _imageSourceResolver;
    private readonly IApplicatePrefetchDocumentSource _documents;
    private readonly int _maxCandidates;
    private readonly int _maxConcurrency;
    private readonly object _gate = new();

    private CancellationTokenSource? _pass;
    private bool _disposed;

    public ApplicateBackgroundTabPrefetcher(
        ApplicateRenderedBodyCache cache,
        IApplicateHtmlMarkdownRenderer renderer,
        ISettingsStore settings,
        IImageSourceResolver? imageSourceResolver,
        IApplicatePrefetchDocumentSource documents,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _imageSourceResolver = imageSourceResolver;
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));

        // The bound is derived from the cache's OWN capacity, never re-typed:
        // the active document's entry lives in the same cache, and a prefetcher
        // allowed to fill every slot would evict the very document it was
        // started by. maxEntries - 1 is the largest set that cannot do that.
        _maxCandidates = System.Math.Max(0, _cache.MaxEntries - 1);
        _maxConcurrency = System.Math.Max(1, maxConcurrency);
    }

    /// <summary>
    /// THE entry point. Start one prefetch pass over <paramref name="snapshot"/>'s
    /// non-active tabs. Call from the UI thread, from a renderer state event.
    ///
    /// <para>Idempotent while a pass is in flight: a second trigger is declined
    /// rather than queued, so a burst of state events cannot multiply the work.
    /// After a pass ends (or is cancelled) the next trigger re-arms, which is
    /// what keeps the cache warm as the user moves between tabs rather than only
    /// once per launch.</para>
    /// </summary>
    public void Trigger(ApplicatePrefetchSnapshot snapshot, string reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        CancellationTokenSource pass;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pass is not null)
            {
                ApplicateTrace.DiagMs(TraceGroup, "pass-declined", $"reason={reason} cause=in-flight");
                return;
            }

            pass = new CancellationTokenSource();
            _pass = pass;
        }

        // Thread-pool fire-and-forget, matching the startup prime's shape: the
        // Task is intentionally not awaited anywhere and must never propagate an
        // exception, so RunPassAsync catches everything it can reach.
        _ = Task.Run(() => RunPassAsync(snapshot, reason, pass), CancellationToken.None);
    }

    /// <summary>
    /// Stop the in-flight pass. Called when the reason it was started stops
    /// holding: the active document changed, a tab closed, the renderer failed,
    /// or the window is closing. Safe to call with no pass running.
    /// <para>
    /// Stops the pass from starting further work; it does NOT abort a render
    /// already inside the cache. That is deliberate — see the
    /// <c>CancellationToken.None</c> note at the <c>GetOrRenderAsync</c> call.
    /// At most <see cref="DefaultMaxConcurrency"/> renders run on to completion
    /// after this returns.
    /// </para>
    /// </summary>
    public void CancelInFlight(string reason)
    {
        CancellationTokenSource? pass;
        lock (_gate)
        {
            pass = _pass;
        }

        if (pass is null)
        {
            return;
        }

        ApplicateTrace.DiagMs(TraceGroup, "pass-cancel", $"reason={reason}");
        try
        {
            pass.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass completed and disposed its own source between the read
            // and the Cancel. Nothing to stop.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelInFlight("disposed");
    }

    /// <summary>
    /// The ORDER, as a pure function so it is testable without a renderer, a
    /// cache, or a UI thread.
    ///
    /// <para><b>Returns EVERY non-active open tab, untruncated.</b> The pass's
    /// budget is deliberately NOT applied here. Whether a document can be
    /// cached at all is only knowable once its text has been materialized
    /// (<see cref="ApplicateRenderedBodyCache.CanCache"/> reads the content),
    /// which is I/O this pure ordering must not do. Cutting the list to the
    /// budget here is what let an image-bearing document consume one of the
    /// pass's warm slots and then return nothing: the budget was spent on an
    /// ATTEMPT rather than on a WARM. <see cref="RunPassAsync"/> walks this
    /// list and stops when the budget has bought that many real cache
    /// entries.</para>
    ///
    /// <para><b>Order:</b> the D11 recent-files (MRU) order — the repository's
    /// accepted owner of recent-file state (decision
    /// <c>2026-07-25-d11-mru-recent-files-ownership</c>). No second ordering is
    /// invented here. Open tabs the MRU does not rank (its list is capped, so a
    /// long-lived tab can fall off) keep tab-strip order behind the ranked
    /// ones; the tab index is also the tie-break that makes the sort
    /// deterministic, since <see cref="List{T}.Sort"/> is not stable.</para>
    ///
    /// <para><b>In-session this IS activation recency.</b> The MRU has a second
    /// automatic trigger — <c>ApplicateMainWindow.HandleActiveDocumentChangedForRecent</c>,
    /// on the service's <c>ActiveDocumentChanged</c> — so a switch between
    /// already-open tabs folds move-to-front and this ordering follows which
    /// document the user actually reached for. (Previously only the collection's
    /// <c>Add</c> action wrote, and <c>OpenDocumentsService.OpenAsync</c> dedups an
    /// already-open path without an <c>Add</c>, so tab switches never reached the
    /// list at all: <c>work-items/bugs/2026-08-02-mru-records-openings-not-activations.md</c>.)</para>
    ///
    /// <para><b>REMAINING DEFECT — the FIRST pass after a session restore is
    /// still not activation-ordered.</b> d12 clause 1
    /// (<c>ApplicateMainWindow.SeedRecentPathsForRestore</c>) folds every saved
    /// open path move-to-front in tab order at restore, which leaves this
    /// ordering as exactly REVERSE TAB ORDER until the user's first tab switch
    /// re-heads it. That seeding is an accepted decision and is NOT worked
    /// around here — a prefetch-local recency list would be the second owner
    /// d11 clause 1 forbids. Filed as F1 of
    /// <c>work-items/bugs/2026-08-02-tab-prefetch-warms-the-wrong-three-documents.md</c>.</para>
    ///
    /// <para><b>Excluded:</b> the active document — it is already rendered, and
    /// it is the one entry this pass must not spend its budget on.</para>
    /// </summary>
    internal static IReadOnlyList<string> OrderCandidates(ApplicatePrefetchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < snapshot.RecentPaths.Count; index++)
        {
            var recent = snapshot.RecentPaths[index];
            if (!string.IsNullOrWhiteSpace(recent))
            {
                rank.TryAdd(recent, index);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(string Path, int Rank, int TabIndex)>();
        for (var tabIndex = 0; tabIndex < snapshot.OpenPaths.Count; tabIndex++)
        {
            var path = snapshot.OpenPaths[tabIndex];
            if (string.IsNullOrWhiteSpace(path)
                || string.Equals(path, snapshot.ActivePath, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(path))
            {
                continue;
            }

            ordered.Add((path, rank.TryGetValue(path, out var r) ? r : int.MaxValue, tabIndex));
        }

        ordered.Sort(static (left, right) => left.Rank != right.Rank
            ? left.Rank.CompareTo(right.Rank)
            : left.TabIndex.CompareTo(right.TabIndex));

        var result = new List<string>(ordered.Count);
        foreach (var candidate in ordered)
        {
            result.Add(candidate.Path);
        }

        return result;
    }

    /// <summary>
    /// One pass. Exposed to tests so the bound, the skips, and cancellation can
    /// be verified without the fire-and-forget hop in <see cref="Trigger"/>.
    /// <para>
    /// TAKES OWNERSHIP of <paramref name="pass"/> and disposes it on every exit
    /// path — success, no-candidates, cancellation, and failure. The caller that
    /// created it must not dispose it again or use it afterwards. Ownership
    /// transfers here rather than staying with <see cref="Trigger"/> because
    /// Trigger returns long before the pass ends, so it has no exit path of its
    /// own on which to release it.
    /// </para>
    /// </summary>
    internal async Task RunPassAsync(
        ApplicatePrefetchSnapshot snapshot,
        string reason,
        CancellationTokenSource pass)
    {
        ArgumentNullException.ThrowIfNull(pass);

        var startedAt = Stopwatch.GetTimestamp();
        var hits = 0;
        var misses = 0;
        var skipped = 0;
        var renderMicroseconds = 0L;

        try
        {
            if (_maxCandidates <= 0)
            {
                // A cache that can hold at most the active document's own entry
                // has no room for this pass at all.
                ApplicateTrace.DiagMs(
                    TraceGroup,
                    "pass-skipped",
                    $"reason={reason} cause=no-budget cacheMax={_cache.MaxEntries}");
                return;
            }

            var candidates = OrderCandidates(snapshot);
            if (candidates.Count == 0)
            {
                ApplicateTrace.DiagMs(TraceGroup, "pass-skipped", $"reason={reason} cause=no-candidates");
                return;
            }

            // `ordered` is the whole non-active tab set, NOT the admitted set —
            // the pass walks it until the budget has bought maxCandidates real
            // cache entries. (This field was `candidates=` while selection
            // truncated; it is renamed because its meaning changed, rather than
            // left to mean something new under the old name.)
            ApplicateTrace.DiagMs(
                TraceGroup,
                "pass-start",
                $"reason={reason} ordered={candidates.Count} maxCandidates={_maxCandidates} concurrency={_maxConcurrency}");

            var queue = new ConcurrentQueue<string>(candidates);

            // Warm slots taken or in flight. The budget counts ENTRIES THIS PASS
            // LEAVES IN THE CACHE, not attempts: a candidate the cache will
            // never store must not cost a slot that a storable one could use.
            var claimed = 0;
            var workerCount = System.Math.Min(
                _maxConcurrency,
                System.Math.Min(_maxCandidates, candidates.Count));
            var workers = new Task[workerCount];
            for (var worker = 0; worker < workerCount; worker++)
            {
                workers[worker] = Task.Run(
                    async () =>
                    {
                        while (!pass.IsCancellationRequested)
                        {
                            // Claim BEFORE dequeuing rather than counting
                            // successes afterwards. With two workers, a
                            // count-after check lets both pass it on the last
                            // slot and warm maxCandidates + 1 documents —
                            // which is exactly the overrun that evicts the
                            // active document this pass was started by.
                            if (Interlocked.Increment(ref claimed) > _maxCandidates)
                            {
                                Interlocked.Decrement(ref claimed);
                                break;
                            }

                            if (!queue.TryDequeue(out var path))
                            {
                                Interlocked.Decrement(ref claimed);
                                break;
                            }

                            var outcome = await PrefetchOneAsync(path, pass.Token).ConfigureAwait(false);
                            switch (outcome.Kind)
                            {
                                case PrefetchOutcomeKind.Hit:
                                    // Already in the cache: it OCCUPIES a slot
                                    // even though this pass did not render it,
                                    // so the claim is kept. Releasing it here
                                    // would let the pass leave maxCandidates + 1
                                    // non-active entries behind.
                                    Interlocked.Increment(ref hits);
                                    break;
                                case PrefetchOutcomeKind.Miss:
                                    Interlocked.Increment(ref misses);
                                    Interlocked.Add(ref renderMicroseconds, outcome.RenderMicroseconds);
                                    break;
                                default:
                                    // Uncacheable, vanished, cancelled or
                                    // failed — nothing was stored, so nothing
                                    // occupies a slot. Return the claim so the
                                    // next candidate gets it. Attempts stay
                                    // bounded by the open-tab count.
                                    Interlocked.Increment(ref skipped);
                                    Interlocked.Decrement(ref claimed);
                                    break;
                            }
                        }
                    },
                    CancellationToken.None);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallow and trace, exactly like the startup prime: a prefetch that
            // fails costs one cache miss the ordinary activation path already
            // handles, and this Task is never awaited by anyone who could catch.
            ApplicateTrace.Diag(TraceGroup, $"pass-failed reason={reason} ex={ex.GetType().Name} msg={ex.Message}");
        }
        finally
        {
            // Release the slot and the token source on EVERY exit path —
            // success, no-candidates, cancellation, failure — so the next
            // trigger can re-arm and no CancellationTokenSource leaks.
            lock (_gate)
            {
                if (ReferenceEquals(_pass, pass))
                {
                    _pass = null;
                }
            }

            pass.Dispose();

            ApplicateTrace.DiagMs(
                TraceGroup,
                "pass-end",
                $"reason={reason} hits={hits} misses={misses} skipped={skipped} "
                    + $"renderMs={renderMicroseconds / 1000d:F1} passMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1} "
                    + $"cacheMax={_cache.MaxEntries}");
        }
    }

    private async Task<PrefetchOutcome> PrefetchOneAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _documents.TryMaterializeAsync(path, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                ApplicateTrace.DiagMs(TraceGroup, "candidate-skipped", $"path={path} cause=unavailable");
                return PrefetchOutcome.Skipped;
            }

            // An image-bearing document is never stored by the cache, so
            // rendering one here would burn CPU and then discard the result.
            if (!_cache.CanCache(source, _imageSourceResolver))
            {
                ApplicateTrace.DiagMs(TraceGroup, "candidate-skipped", $"path={path} cause=uncacheable");
                return PrefetchOutcome.Skipped;
            }

            // The rendered BODY does not vary with ReadingPreferences —
            // ApplicateHtmlMarkdownRenderer.RenderBodyAsync takes the parameter
            // and never reads it; only the shell in RenderAsync uses it. The
            // load is still made because RenderBodyAsync's signature requires a
            // value, not because the body could go stale against it.
            var preferences = await _settings.LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);

            var renderTimestamp = 0L;
            var renderMicroseconds = 0L;
            var rendered = false;
            await _cache.GetOrRenderAsync(
                    source,
                    preferences,
                    _imageSourceResolver,
                    async renderToken =>
                    {
                        // Reached only on a genuine miss: an already-cached
                        // document returns from the cache without calling this,
                        // and a document the user's click is already rendering
                        // awaits that in-flight task instead of starting a
                        // second one. So this pass can neither double-render nor
                        // delay a click.
                        rendered = true;
                        renderTimestamp = Stopwatch.GetTimestamp();
                        try
                        {
                            return await _renderer
                                .RenderBodyAsync(source, preferences, _imageSourceResolver, renderToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            renderMicroseconds =
                                (long)Stopwatch.GetElapsedTime(renderTimestamp).TotalMicroseconds;
                        }
                    },
                    // DELIBERATELY CancellationToken.None, not the pass token.
                    //
                    // The cache coalesces on a shared in-flight task: a second
                    // caller for the same key awaits the FIRST caller's task
                    // (ApplicateRenderedBodyCache.cs:63-79). If that first
                    // caller is this prefetch and its token fires, the cache
                    // completes the shared task as CANCELLED (:87-91) — and
                    // every other waiter observes that cancellation regardless
                    // of its own token.
                    //
                    // The user's tab click is exactly such a waiter, and
                    // clicking a tab raises ActiveDocumentChanged, which is one
                    // of the signals that cancels this pass. Threading the pass
                    // token in here would therefore let a prefetch abort the
                    // very render the user is waiting on. A prefetch must never
                    // be able to fail a user render.
                    //
                    // Cancellation is honoured where it is free of that hazard:
                    // the worker loop stops DEQUEUING candidates, so at most
                    // _maxConcurrency renders (2) run on to completion after a
                    // cancel — bounded work, on documents the user has open,
                    // whose results are still cached and still useful.
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!rendered)
            {
                ApplicateTrace.DiagMs(TraceGroup, "candidate-hit", $"path={path}");
                return PrefetchOutcome.Hit;
            }

            ApplicateTrace.DiagMs(
                TraceGroup,
                "candidate-miss",
                $"path={path} renderMs={renderMicroseconds / 1000d:F1}");
            return PrefetchOutcome.Miss(renderMicroseconds);
        }
        catch (OperationCanceledException)
        {
            ApplicateTrace.DiagMs(TraceGroup, "candidate-cancelled", $"path={path}");
            return PrefetchOutcome.Skipped;
        }
        catch (Exception ex)
        {
            ApplicateTrace.Diag(TraceGroup, $"candidate-failed path={path} ex={ex.GetType().Name}");
            return PrefetchOutcome.Skipped;
        }
    }

    private enum PrefetchOutcomeKind
    {
        Skipped,
        Hit,
        Miss,
    }

    private readonly record struct PrefetchOutcome(PrefetchOutcomeKind Kind, long RenderMicroseconds)
    {
        public static PrefetchOutcome Skipped => new(PrefetchOutcomeKind.Skipped, 0);

        public static PrefetchOutcome Hit => new(PrefetchOutcomeKind.Hit, 0);

        public static PrefetchOutcome Miss(long renderMicroseconds)
            => new(PrefetchOutcomeKind.Miss, renderMicroseconds);
    }
}
