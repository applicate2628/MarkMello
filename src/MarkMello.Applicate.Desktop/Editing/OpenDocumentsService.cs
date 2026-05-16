using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

    public void Activate(OpenDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_openDocuments.Contains(document))
        {
            throw new InvalidOperationException("Document is not in the open list.");
        }

        SetActive(document);
    }

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
