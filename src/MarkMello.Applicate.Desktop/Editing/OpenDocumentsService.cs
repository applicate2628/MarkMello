using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Application.Diagnostics;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Editing;

public sealed class OpenDocumentsService : IOpenDocumentsService, IDisposable
{
    private readonly ObservableCollection<OpenDocument> _openDocuments = new();
    private readonly SemaphoreSlim _openLock = new(initialCount: 1, maxCount: 1);

    public void Dispose() => _openLock.Dispose();

    public OpenDocumentsService()
    {
        OpenDocuments = new ReadOnlyObservableCollection<OpenDocument>(_openDocuments);
    }

    public ReadOnlyObservableCollection<OpenDocument> OpenDocuments { get; }

    public OpenDocument? ActiveDocument { get; private set; }

    public event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    public event EventHandler? DocumentModifiedChanged;

    public async Task<OpenDocument> OpenAsync(string filePath, bool activate = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must not be empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Markdown file not found.", filePath);
        }

        var normalized = Path.GetFullPath(filePath);

        // Serialize OpenAsync so two concurrent calls with the same path
        // (e.g. two rapid Drop events both posted through the bridge's
        // Dispatcher.UIThread.Post(async) lambdas) cannot both pass the
        // find-by-path check before either Add runs. Without this guard
        // a second drop of the same file becomes a duplicate tab.
        await _openLock.WaitAsync().ConfigureAwait(true);
        try
        {
            var existing = FindByPath(normalized);
            if (existing is not null)
            {
                if (activate)
                {
                    SetActive(existing);
                }
                return existing;
            }

            var sourceText = await File.ReadAllTextAsync(normalized).ConfigureAwait(true);
            var displayName = OpenDocument.DisplayNameFromPath(normalized);
            var document = new OpenDocument(normalized, displayName, sourceText);

            _openDocuments.Add(document);
            if (activate)
            {
                SetActive(document);
            }
            return document;
        }
        finally
        {
            _openLock.Release();
        }
    }

    public async Task<OpenDocument> OpenStubAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must not be empty.", nameof(filePath));
        }

        // No File.Exists / File.ReadAllText here — stubs are intentionally
        // lightweight so cold startup with N session tabs does not scale
        // with N. A missing-file check would re-introduce a per-tab
        // synchronous I/O probe; the EnsureLoadedAsync path handles
        // missing/unreadable files when the user activates the tab.
        var normalized = Path.GetFullPath(filePath);

        await _openLock.WaitAsync().ConfigureAwait(true);
        try
        {
            var existing = FindByPath(normalized);
            if (existing is not null)
            {
                return existing;
            }

            var displayName = OpenDocument.DisplayNameFromPath(normalized);
            var document = OpenDocument.CreateStub(normalized, displayName);
            _openDocuments.Add(document);
            return document;
        }
        finally
        {
            _openLock.Release();
        }
    }

    public async Task EnsureLoadedAsync(OpenDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsLoaded)
        {
            return;
        }

        // Cache hit: Program.Main pre-read this path concurrently and
        // deposited the source. Use it without touching disk again.
        if (EarlyDocumentCache.TryConsume(document.FilePath, out var cached)
            && cached is not null)
        {
            document.SourceText = cached.Content;
            document.IsLoaded = true;
            return;
        }

        // Cache miss: do the read now. ReadAllTextAsync internally
        // dispatches to the thread pool, so the UI thread is not blocked
        // for the duration of the file read.
        var content = await File.ReadAllTextAsync(document.FilePath).ConfigureAwait(true);
        document.SourceText = content;
        document.IsLoaded = true;
    }

    public void Activate(OpenDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_openDocuments.Contains(document))
        {
            throw new InvalidOperationException("Document is not in the open list.");
        }

        SetActive(document);
    }

    public void ClearActive() => SetActive(null);

    public void Close(OpenDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var index = _openDocuments.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        _openDocuments.RemoveAt(index);

        if (!ReferenceEquals(ActiveDocument, document))
        {
            return;
        }

        // Pick neighbor: previous if available, otherwise next, otherwise null.
        OpenDocument? next = null;
        if (_openDocuments.Count > 0)
        {
            var fallbackIndex = System.Math.Min(index, _openDocuments.Count - 1);
            next = _openDocuments[fallbackIndex];
        }

        SetActive(next);
    }

    public void Move(OpenDocument document, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(document);

        var oldIndex = _openDocuments.IndexOf(document);
        if (oldIndex < 0)
        {
            return;
        }

        var clamped = System.Math.Clamp(newIndex, 0, _openDocuments.Count - 1);
        if (clamped == oldIndex)
        {
            return;
        }

        _openDocuments.Move(oldIndex, clamped);
    }

    public void UpdateState(OpenDocument document, int caret, double scrollProgressPercent)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.EditorCaret = caret;
        document.ScrollProgressPercent = scrollProgressPercent;
    }

    public void UpdateSourceText(OpenDocument document, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_openDocuments.Contains(document))
        {
            throw new InvalidOperationException("Document is not in the open list.");
        }

        var wasModified = document.IsModified;
        document.SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
        document.IsModified = false;
        // Receiving externally-sourced text counts as loaded — any stub
        // that reaches here (e.g. the bridge mirror noticing VM.Document
        // content differs) becomes a regular loaded document so later
        // tab switches do not re-read from disk.
        document.IsLoaded = true;
        if (wasModified)
        {
            DocumentModifiedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetModified(OpenDocument document, bool modified)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsModified == modified)
        {
            return;
        }

        document.IsModified = modified;
        DocumentModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    private OpenDocument? FindByPath(string normalizedPath)
    {
        foreach (var doc in _openDocuments)
        {
            if (string.Equals(doc.FilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    private void SetActive(OpenDocument? document)
    {
        if (ReferenceEquals(ActiveDocument, document))
        {
            return;
        }

        ActiveDocument = document;
        ActiveDocumentChanged?.Invoke(this, new ActiveDocumentChangedEventArgs(document));
    }
}
