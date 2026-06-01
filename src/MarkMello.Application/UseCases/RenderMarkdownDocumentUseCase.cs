using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Application.UseCases;

/// <summary>
/// Изолирует markdown pipeline от presentation layer и гарантирует безопасный fallback.
/// Parse/render ошибки не должны ломать viewer path.
/// </summary>
public sealed class RenderMarkdownDocumentUseCase
{
    private const int DefaultMaxEntries = 8;

    private readonly IMarkdownDocumentRenderer _renderer;
    private readonly object _gate = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly int _maxEntries;

    public RenderMarkdownDocumentUseCase(IMarkdownDocumentRenderer renderer)
        : this(renderer, DefaultMaxEntries)
    {
    }

    internal RenderMarkdownDocumentUseCase(IMarkdownDocumentRenderer renderer, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
        _maxEntries = Math.Max(0, maxEntries);
    }

    public RenderedMarkdownDocument Execute(string markdown)
        => Execute(markdown, baseDirectory: null);

    public RenderedMarkdownDocument Execute(string markdown, string? baseDirectory)
    {
        markdown ??= string.Empty;
        if (TryGetCached(markdown, baseDirectory, out var cached))
        {
            return cached;
        }

        try
        {
            var rendered = _renderer.Render(markdown, baseDirectory);
            Store(markdown, baseDirectory, rendered);
            return rendered;
        }
        catch
        {
            var fallback = RenderedMarkdownDocument.PlainText(markdown);
            return baseDirectory is null ? fallback : fallback with { BaseDirectory = baseDirectory };
        }
    }

    private bool TryGetCached(string markdown, string? baseDirectory, out RenderedMarkdownDocument rendered)
    {
        lock (_gate)
        {
            for (var node = _lru.First; node is not null; node = node.Next)
            {
                if (ReferenceEquals(node.Value.Markdown, markdown)
                    && string.Equals(node.Value.BaseDirectory, baseDirectory, StringComparison.Ordinal))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    rendered = node.Value.Document;
                    return true;
                }
            }
        }

        rendered = RenderedMarkdownDocument.Empty;
        return false;
    }

    private void Store(string markdown, string? baseDirectory, RenderedMarkdownDocument rendered)
    {
        if (_maxEntries <= 0)
        {
            return;
        }

        lock (_gate)
        {
            for (var existing = _lru.First; existing is not null; existing = existing.Next)
            {
                if (ReferenceEquals(existing.Value.Markdown, markdown)
                    && string.Equals(existing.Value.BaseDirectory, baseDirectory, StringComparison.Ordinal))
                {
                    existing.Value = existing.Value with { Document = rendered };
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                    return;
                }
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(markdown, baseDirectory, rendered));
            _lru.AddFirst(node);

            while (_lru.Count > _maxEntries)
            {
                var last = _lru.Last;
                if (last is null)
                {
                    break;
                }

                _lru.RemoveLast();
            }
        }
    }

    private readonly record struct CacheEntry(
        string Markdown,
        string? BaseDirectory,
        RenderedMarkdownDocument Document);
}
