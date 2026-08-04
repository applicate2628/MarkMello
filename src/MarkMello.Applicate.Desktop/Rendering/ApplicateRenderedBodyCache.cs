using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateRenderedBodyCache
{
    private const int DefaultMaxEntries = 4;
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = new();
    private readonly Dictionary<CacheKey, InFlightRender> _inFlight = new();
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly int _maxEntries;

    public ApplicateRenderedBodyCache(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = System.Math.Max(0, maxEntries);
    }

    /// <summary>
    /// This cache's capacity, so a caller that must size its work against it
    /// reads the ONE owner instead of re-typing the constant. Read-only and
    /// additive: no eviction, coalescing, or storage behaviour depends on it
    /// being visible.
    /// <para>
    /// Used by <see cref="ApplicateBackgroundTabPrefetcher"/> to cap a prefetch
    /// pass at <c>MaxEntries - 1</c> — the active document's entry lives in this
    /// same cache, so a pass allowed to fill every slot would evict the document
    /// that started it.
    /// </para>
    /// </summary>
    public int MaxEntries => _maxEntries;

    /// <summary>
    /// How many renders are currently registered as in-flight. Exists so a test
    /// can assert the invariant DIRECTLY — a key is detached on every path,
    /// success, failure and each cancellation shape — instead of inferring it
    /// from what a later call happens to do. Assembly-internal and read-only; no
    /// caching, eviction or coalescing behaviour reads it.
    /// </summary>
    internal int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    public async Task<ApplicateRenderedBody> GetOrRenderAsync(
        MarkdownSource source,
        ReadingPreferences preferences,
        IImageSourceResolver? imageSourceResolver,
        Func<CancellationToken, Task<ApplicateRenderedBody>> renderBodyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(renderBodyAsync);

        if (!CanCache(source, imageSourceResolver))
        {
            return await renderBodyAsync(cancellationToken).ConfigureAwait(false);
        }

        var key = CacheKey.Create(source);
        InFlightRender render;
        var createdRender = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Body;
            }

            if (!_inFlight.TryGetValue(key, out render!))
            {
                render = new InFlightRender(this, key);
                _inFlight[key] = render;
                createdRender = true;
            }

            // Registered in the SAME critical section as the _inFlight lookup.
            // That is what makes InFlightRender's withdrawal arithmetic sound:
            // a caller cannot join a render between the moment its last waiter
            // leaves and the moment the key is removed.
            render.AddWaiterLocked();
        }

        try
        {
            if (createdRender)
            {
                // Started INLINE rather than through Task.Run: an async method
                // runs synchronously up to its first await, so the render still
                // begins on the calling thread and captures the same
                // synchronization context it did when the creating caller
                // awaited it directly. It is deliberately NOT awaited here —
                // the render's lifetime belongs to the cache, not to whichever
                // caller happened to arrive first.
                render.Start(renderBodyAsync);
            }

            // Every caller, creator included, observes the shared render through
            // ITS OWN token. Cancelling here ends this caller's wait and nothing
            // else; whether the render itself stops is decided by
            // InFlightRender.ReleaseWaiter, from whether anyone is still waiting.
            return await render.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            render.ReleaseWaiter();
        }
    }

    public bool CanCache(MarkdownSource source, IImageSourceResolver? imageSourceResolver)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _maxEntries > 0
            && (imageSourceResolver is null || !MayResolveImages(source.Content));
    }

    /// <summary>
    /// Renders and stores one body. Deliberately does NOT touch <c>_inFlight</c>
    /// any more: removing the key is owned in one place by
    /// <see cref="InFlightRender.Complete"/>, which has to publish "this render
    /// is over" and drop the key in the SAME critical section that decides
    /// whether the render may still be cancelled. Splitting those two across two
    /// owners is what would let a caller join a render that is already being
    /// abandoned.
    /// </summary>
    private async Task<ApplicateRenderedBody> RenderAndStoreAsync(
        CacheKey key,
        Func<CancellationToken, Task<ApplicateRenderedBody>> renderBodyAsync,
        CancellationToken cancellationToken)
    {
        var body = await renderBodyAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existingNode))
            {
                _lru.Remove(existingNode);
                _lru.AddFirst(existingNode);
                return existingNode.Value.Body;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, body));
            _lru.AddFirst(node);
            _entries[key] = node;

            while (_entries.Count > _maxEntries)
            {
                var last = _lru.Last;
                if (last is null)
                {
                    break;
                }

                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
            }
        }

        return body;
    }

    /// <summary>
    /// One shared render, plus the arithmetic that decides whose cancellation it
    /// answers to.
    /// <para>
    /// The render runs on a token this class owns, never on a caller's. A caller
    /// registers as a waiter, observes the outcome through its OWN token, and
    /// deregisters when it stops waiting. The render is cancelled when — and
    /// only when — the last waiter withdraws. So one caller can no longer end a
    /// render another caller is still waiting on, while a render nobody wants
    /// still stops instead of occupying one of this cache's very few slots.
    /// </para>
    /// <para>
    /// Every mutable field here is guarded by the owning cache's <c>_gate</c>.
    /// The waiter count and <c>_inFlight</c> membership must move together, so
    /// they share one lock rather than each having its own.
    /// </para>
    /// </summary>
    private sealed class InFlightRender : IDisposable
    {
        private readonly TaskCompletionSource<ApplicateRenderedBody> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationTokenSource _cancellation = new();
        private readonly CancellationToken _token;
        private readonly ApplicateRenderedBodyCache _owner;
        private readonly CacheKey _key;

        private int _waiters;
        private bool _renderCompleted;
        private bool _cancelling;
        private bool _disposed;

        public InFlightRender(ApplicateRenderedBodyCache owner, CacheKey key)
        {
            _owner = owner;
            _key = key;

            // Captured once. The source is disposed as soon as the render is
            // over, and its Token property throws after that.
            _token = _cancellation.Token;
        }

        public Task<ApplicateRenderedBody> Completion => _completion.Task;

        /// <summary>Call only while holding the owning cache's <c>_gate</c>.</summary>
        public void AddWaiterLocked() => _waiters++;

        public void Start(Func<CancellationToken, Task<ApplicateRenderedBody>> renderBodyAsync)
            => _ = RunAsync(renderBodyAsync);

        /// <summary>
        /// One caller has stopped waiting — because it got its result, because
        /// it failed, or because its own token fired. When the last one leaves,
        /// the render loses its reason to exist and is cancelled.
        /// </summary>
        public void ReleaseWaiter()
        {
            var cancel = false;
            lock (_owner._gate)
            {
                _waiters--;
                if (_waiters == 0 && !_renderCompleted)
                {
                    // Nobody is left to want this result. Stop the work AND drop
                    // the key in this one critical section: an arriving caller
                    // must create a fresh render rather than join one that is
                    // already being abandoned.
                    _cancelling = true;
                    cancel = true;
                    DetachLocked();
                }
            }

            if (!cancel)
            {
                return;
            }

            try
            {
                // Outside the gate on purpose: Cancel runs the render's own
                // cancellation callbacks synchronously on this thread, and
                // caller-supplied callbacks must never run under the cache lock.
                _cancellation.Cancel();
            }
            finally
            {
                lock (_owner._gate)
                {
                    _cancelling = false;
                }

                Dispose();
            }
        }

        /// <summary>
        /// Releases the cancellation source. Called on completion, not through a
        /// <c>using</c> — this render's lifetime is the render's, not any
        /// caller's scope. Idempotent and safe from either completing path:
        /// <see cref="ClaimDisposeLocked"/> lets exactly one of them through.
        /// </summary>
        public void Dispose()
        {
            bool dispose;
            lock (_owner._gate)
            {
                dispose = ClaimDisposeLocked();
            }

            if (dispose)
            {
                _cancellation.Dispose();
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task<ApplicateRenderedBody>> renderBodyAsync)
        {
            ApplicateRenderedBody? body = null;
            Exception? failure = null;
            var succeeded = false;
            var cancelled = false;

            try
            {
                body = await _owner.RenderAndStoreAsync(_key, renderBodyAsync, _token).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Detached BEFORE the outcome is published, so a caller woken by
                // the completion can never find this render still joinable.
                Complete();

                // Exhaustive by construction, and the final branch keeps it that
                // way: a waiter must never be left holding a task that no path
                // completes.
                if (succeeded)
                {
                    _completion.TrySetResult(body!);
                }
                else if (cancelled)
                {
                    _completion.TrySetCanceled(_token);
                }
                else if (failure is not null)
                {
                    _completion.TrySetException(failure);

                    // This task is the CACHE's, not any one caller's, so the
                    // cache observes it. Without this, a render that outlived
                    // every caller and then failed would reach
                    // ApplicateFatalReport's UnobservedTaskException hook on
                    // finalization and write a durable crash record for a
                    // failure nobody was waiting on. Nothing is swallowed:
                    // reading Exception only clears the unobserved flag, and
                    // every caller still attached still receives the failure.
                    _ = _completion.Task.Exception;
                }
                else
                {
                    _completion.TrySetException(
                        new InvalidOperationException("The shared render ended without an outcome."));
                    _ = _completion.Task.Exception;
                }
            }
        }

        private void Complete()
        {
            lock (_owner._gate)
            {
                _renderCompleted = true;
                DetachLocked();
            }

            Dispose();
        }

        /// <summary>
        /// The source is disposed exactly once, by whichever of the two paths
        /// finishes last: the render completing, or an in-flight
        /// <see cref="CancellationTokenSource.Cancel()"/> returning.
        /// <c>_cancelling</c> is what stops a completing render from disposing
        /// the source out from under a <c>Cancel</c> that is still executing.
        /// A render that never returns keeps its source, which is correct — the
        /// token is still live for work that is still running.
        /// </summary>
        private bool ClaimDisposeLocked()
        {
            if (_disposed || _cancelling || !_renderCompleted)
            {
                return false;
            }

            _disposed = true;
            return true;
        }

        /// <summary>
        /// Removes this render from <c>_inFlight</c> only while it is still the
        /// registered one. An abandoned render can outlive its own key — a later
        /// caller may already have registered a replacement — and must not
        /// remove someone else's.
        /// </summary>
        private void DetachLocked()
        {
            if (_owner._inFlight.TryGetValue(_key, out var current) && ReferenceEquals(current, this))
            {
                _owner._inFlight.Remove(_key);
            }
        }
    }

    private readonly record struct CacheEntry(CacheKey Key, ApplicateRenderedBody Body);

    private static bool MayResolveImages(string content)
        => content.Contains("![", StringComparison.Ordinal)
            || content.Contains("<img", StringComparison.OrdinalIgnoreCase);

    private readonly record struct CacheKey(
        string Path,
        string FileName,
        int ContentLength,
        ulong ContentHash)
    {
        public static CacheKey Create(MarkdownSource source)
            => new(
                source.Path,
                source.FileName,
                source.Content.Length,
                HashContent(source.Content));

        private static ulong HashContent(string content)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;

            foreach (var c in content)
            {
                hash ^= c;
                hash *= prime;
            }

            return hash;
        }
    }
}
