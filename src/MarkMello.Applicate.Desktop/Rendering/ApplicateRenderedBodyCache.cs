using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

internal sealed class ApplicateRenderedBodyCache
{
    private const int DefaultMaxEntries = 4;
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = new();
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly int _maxEntries;

    public ApplicateRenderedBodyCache(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = System.Math.Max(0, maxEntries);
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

        if (_maxEntries == 0 || imageSourceResolver is not null && MayResolveImages(source.Content))
        {
            return await renderBodyAsync(cancellationToken).ConfigureAwait(false);
        }

        var key = CacheKey.Create(source);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Body;
            }
        }

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
