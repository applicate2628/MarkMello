using CommunityToolkit.Mvvm.ComponentModel;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Presentation.Localization;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Ленивая editor-сессия для текущего документа. Не участвует в startup path
/// и создаётся только при явном входе в edit mode.
/// </summary>
public sealed class EditorSessionViewModel : ObservableObject
{
    private const int MaxRetainedRealtimeHistoryDepth = 20;

    private readonly RenderMarkdownDocumentUseCase _renderMarkdown;
    private readonly ILocalizationService _localization;
    private string _sourceText;
    private string _lastPersistedSource;
    private string? _currentPath;
    private string _fileName;
    private double _splitRatio;
    private ReadingPreferences _readingPreferences;
    private RenderedMarkdownDocument _renderedPreview;
    private string _statusMessage;
    private readonly Stack<RealtimeInDocumentEditHistoryEntry> _realtimeUndoHistory = new();
    private readonly Stack<RealtimeInDocumentEditHistoryEntry> _realtimeRedoHistory = new();
    // True while RenderedPreview is intentionally out of step with SourceText:
    // a preview-deferred materialization (a reading-mode in-place edit lazily
    // creates the session with an Empty preview) or an ApplyInPlaceEditToBuffer
    // that moved the buffer without paying the whole-document parse. Reconciled
    // by EnsurePreviewReconciled (next Ctrl+E) or cleared by the SourceText
    // setter (any real preview rebuild).
    private bool _previewDeferred;

    public EditorSessionViewModel(
        MarkdownSource source,
        ReadingPreferences readingPreferences,
        RenderMarkdownDocumentUseCase renderMarkdown,
        IImageSourceResolver? imageSourceResolver,
        ILocalizationService? localization = null)
        : this(
            source.Path,
            source.FileName,
            source.Content,
            readingPreferences,
            renderMarkdown,
            imageSourceResolver,
            localization)
    {
        ArgumentNullException.ThrowIfNull(source);
    }

    public EditorSessionViewModel(
        string fileName,
        string initialContent,
        ReadingPreferences readingPreferences,
        RenderMarkdownDocumentUseCase renderMarkdown,
        IImageSourceResolver? imageSourceResolver,
        ILocalizationService? localization = null)
        : this(
            currentPath: null,
            fileName,
            initialContent,
            readingPreferences,
            renderMarkdown,
            imageSourceResolver,
            localization)
    {
    }

    private EditorSessionViewModel(
        string? currentPath,
        string fileName,
        string initialContent,
        ReadingPreferences readingPreferences,
        RenderMarkdownDocumentUseCase renderMarkdown,
        IImageSourceResolver? imageSourceResolver,
        ILocalizationService? localization,
        bool deferPreview = false)
    {
        ArgumentNullException.ThrowIfNull(renderMarkdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        _renderMarkdown = renderMarkdown;
        _localization = localization ?? new LocalizationService();
        ImageSourceResolver = imageSourceResolver;
        _currentPath = currentPath;
        _fileName = fileName;
        _readingPreferences = readingPreferences;
        _lastPersistedSource = initialContent ?? string.Empty;
        _sourceText = initialContent ?? string.Empty;
        // A preview-deferred session skips the synchronous whole-document parse
        // on construction (heavy-doc hazard on the zero-cost reading-edit click
        // path); the preview reconciles on the next SourceText change or Ctrl+E.
        _renderedPreview = deferPreview
            ? RenderedMarkdownDocument.Empty
            : RenderPreview(_sourceText, _currentPath);
        _previewDeferred = deferPreview;
        _statusMessage = string.Empty;
        _splitRatio = 0.5;
    }

    /// <summary>
    /// Materialize a session for a reading-mode in-place edit WITHOUT rendering
    /// the preview. The first in-place edit of a never-edited document would
    /// otherwise pay a synchronous whole-document parse on the click path; the
    /// preview is Empty until <see cref="EnsurePreviewReconciled"/> (next Ctrl+E)
    /// or the next real <see cref="SourceText"/> change rebuilds it.
    /// </summary>
    public static EditorSessionViewModel CreatePreviewDeferred(
        MarkdownSource source,
        ReadingPreferences readingPreferences,
        RenderMarkdownDocumentUseCase renderMarkdown,
        IImageSourceResolver? imageSourceResolver,
        ILocalizationService? localization = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new EditorSessionViewModel(
            source.Path,
            source.FileName,
            source.Content,
            readingPreferences,
            renderMarkdown,
            imageSourceResolver,
            localization,
            deferPreview: true);
    }

    public IImageSourceResolver? ImageSourceResolver { get; }

    private static readonly string[] LocalizedBindingPropertyNames =
    [
        nameof(EditorBoldTooltip),
        nameof(EditorCodeTooltip),
        nameof(EditorItalicTooltip),
        nameof(EditorLinkTooltip),
        nameof(EditorListTooltip),
        nameof(EditorQuoteTooltip),
        nameof(EditorSourceLabel),
    ];

    public string EditorBoldTooltip => _localization["EditorBoldTooltip"];
    public string EditorCodeTooltip => _localization["EditorCodeTooltip"];
    public string EditorItalicTooltip => _localization["EditorItalicTooltip"];
    public string EditorLinkTooltip => _localization["EditorLinkTooltip"];
    public string EditorListTooltip => _localization["EditorListTooltip"];
    public string EditorQuoteTooltip => _localization["EditorQuoteTooltip"];
    public string EditorSourceLabel => _localization["EditorSourceLabel"];

    public void RefreshLocalizedProperties()
    {
        foreach (var propertyName in LocalizedBindingPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (SetProperty(ref _sourceText, value ?? string.Empty))
            {
                RenderedPreview = RenderPreview(_sourceText, _currentPath);
                _previewDeferred = false;
                StatusMessage = string.Empty;
                RaiseDocumentMetricsChanged();
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string LastPersistedSource
    {
        get => _lastPersistedSource;
        private set
        {
            if (SetProperty(ref _lastPersistedSource, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string? CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                RenderedPreview = RenderPreview(SourceText, _currentPath);
            }
        }
    }

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public double SplitRatio
    {
        get => _splitRatio;
        set => SetProperty(ref _splitRatio, Math.Clamp(value, 0.2, 0.8));
    }

    public ReadingPreferences ReadingPreferences
    {
        get => _readingPreferences;
        private set
        {
            if (SetProperty(ref _readingPreferences, value))
            {
                OnPropertyChanged(nameof(DocumentColumnMaxWidth));
            }
        }
    }

    public double DocumentColumnMaxWidth => ReadingLayoutMetrics.GetDocumentColumnMaxWidth(ReadingPreferences);

    public RenderedMarkdownDocument RenderedPreview
    {
        get => _renderedPreview;
        private set => SetProperty(ref _renderedPreview, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsDirty => !string.Equals(SourceText, LastPersistedSource, StringComparison.Ordinal);

    internal bool CanUndoRealtimeEdits => _realtimeUndoHistory.Count > 0;

    internal bool CanRedoRealtimeEdits => _realtimeRedoHistory.Count > 0;

    public int WordCount => CountWords(SourceText);

    public int ReadTimeMinutes => Math.Max(1, (int)Math.Round(WordCount / 220.0));

    public void UpdateReadingPreferences(ReadingPreferences preferences)
    {
        ReadingPreferences = ReadingPreferences.Normalize(preferences);
    }

    public void ApplyLoadedDocument(MarkdownSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        CurrentPath = source.Path;
        FileName = source.FileName;
        LastPersistedSource = source.Content;
        SourceText = source.Content;
        StatusMessage = string.Empty;
        RaiseDocumentMetricsChanged();
    }

    public void ApplySavedDocument(MarkdownSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        CurrentPath = source.Path;
        FileName = source.FileName;
        LastPersistedSource = source.Content;
        // Do NOT overwrite SourceText. A save PERSISTS the buffer, it does not
        // RELOAD it: source.Content is the snapshot SaveEditorAsync captured
        // before its async disk write, so if the user kept typing during that
        // write the live buffer is now newer, and assigning the snapshot back
        // would silently discard those keystrokes. LastPersistedSource above keeps
        // IsDirty truthful — the buffer is dirty exactly when it moved past what
        // was persisted; when nothing was typed during the save the buffer already
        // equals source.Content so no change is visible. (Reload replaces the
        // buffer through ApplyLoadedDocument, which DOES set SourceText.)
        StatusMessage = string.Empty;
        RaiseDocumentMetricsChanged();
    }

    /// <summary>
    /// In-place edit entry shared by the task-checkbox and table-cell channels:
    /// move the buffer to <paramref name="newBuffer"/> WITHOUT touching
    /// <see cref="LastPersistedSource"/>, so the edit reads as unsaved
    /// (<see cref="IsDirty"/> true) and Ctrl+S / Discard own the write. This is
    /// the single semantic difference from the old auto-persist model, where the
    /// baseline advanced to a just-written disk snapshot. Deliberately skips the
    /// synchronous <see cref="RenderedPreview"/> rebuild — a whole-document parse
    /// does not belong on the zero-cost click path — and marks the preview
    /// deferred so <see cref="EnsurePreviewReconciled"/> rebuilds it on the next
    /// Ctrl+E (the reading surface never binds the preview, so it stays invisible
    /// until then).
    /// </summary>
    public void ApplyInPlaceEditToBuffer(string newBuffer)
    {
        if (SetProperty(ref _sourceText, newBuffer ?? string.Empty, nameof(SourceText)))
        {
            _previewDeferred = true;
            RaiseDocumentMetricsChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    internal bool ApplyRealtimeInDocumentEdit(string newBuffer, RealtimeInDocumentEditDomPatch domPatch)
    {
        ArgumentNullException.ThrowIfNull(domPatch);

        newBuffer ??= string.Empty;
        if (string.Equals(SourceText, newBuffer, StringComparison.Ordinal))
        {
            return false;
        }

        _realtimeUndoHistory.Push(new RealtimeInDocumentEditHistoryEntry(SourceText, newBuffer, domPatch));
        _realtimeRedoHistory.Clear();
        TrimRealtimeUndoHistory();
        ApplyInPlaceEditToBuffer(newBuffer);
        RaiseRealtimeHistoryAvailabilityChanged();
        return true;
    }

    internal RealtimeInDocumentEditHistoryTransition UndoRealtimeInDocumentEdit()
        => ApplyRealtimeHistoryTransition(
            _realtimeUndoHistory,
            _realtimeRedoHistory,
            expectedSource: static entry => entry.AfterSource,
            targetSource: static entry => entry.BeforeSource,
            useAfterPatchValues: false);

    internal RealtimeInDocumentEditHistoryTransition RedoRealtimeInDocumentEdit()
        => ApplyRealtimeHistoryTransition(
            _realtimeRedoHistory,
            _realtimeUndoHistory,
            expectedSource: static entry => entry.BeforeSource,
            targetSource: static entry => entry.AfterSource,
            useAfterPatchValues: true);

    internal void ClearRealtimeInDocumentEditHistory()
    {
        if (_realtimeUndoHistory.Count == 0 && _realtimeRedoHistory.Count == 0)
        {
            return;
        }

        _realtimeUndoHistory.Clear();
        _realtimeRedoHistory.Clear();
        RaiseRealtimeHistoryAvailabilityChanged();
    }

    /// <summary>
    /// Rebuild <see cref="RenderedPreview"/> from the current buffer when it was
    /// left deferred (preview-deferred materialization or an
    /// <see cref="ApplyInPlaceEditToBuffer"/> that skipped the parse). Called on
    /// entering edit mode so the preview surface shows the live buffer instead of
    /// an empty or stale render; a no-op when the preview is already current.
    /// </summary>
    public void EnsurePreviewReconciled()
    {
        if (!_previewDeferred)
        {
            return;
        }

        _previewDeferred = false;
        RenderedPreview = RenderPreview(SourceText, _currentPath);
    }

    public void DiscardChanges()
    {
        SourceText = LastPersistedSource;
        StatusMessage = string.Empty;
    }

    public void UpdateDraftFileName(string fileName)
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        FileName = fileName;
    }

    public void SetStatusMessage(string? message)
    {
        StatusMessage = message ?? string.Empty;
    }

    private RenderedMarkdownDocument RenderPreview(string markdown, string? path)
        => _renderMarkdown.Execute(markdown, ResolveBaseDirectory(path));

    private static string? ResolveBaseDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private void RaiseDocumentMetricsChanged()
    {
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(ReadTimeMinutes));
    }

    private RealtimeInDocumentEditHistoryTransition ApplyRealtimeHistoryTransition(
        Stack<RealtimeInDocumentEditHistoryEntry> sourceHistory,
        Stack<RealtimeInDocumentEditHistoryEntry> destinationHistory,
        Func<RealtimeInDocumentEditHistoryEntry, string> expectedSource,
        Func<RealtimeInDocumentEditHistoryEntry, string> targetSource,
        bool useAfterPatchValues)
    {
        if (sourceHistory.Count == 0)
        {
            return RealtimeInDocumentEditHistoryTransition.Empty;
        }

        var entry = sourceHistory.Peek();
        if (!string.Equals(SourceText, expectedSource(entry), StringComparison.Ordinal))
        {
            ClearRealtimeInDocumentEditHistory();
            return RealtimeInDocumentEditHistoryTransition.Invalidated;
        }

        sourceHistory.Pop();
        destinationHistory.Push(entry);
        var nextSource = targetSource(entry);
        ApplyInPlaceEditToBuffer(nextSource);
        RaiseRealtimeHistoryAvailabilityChanged();
        return RealtimeInDocumentEditHistoryTransition.Applied(
            nextSource,
            entry.DomPatch.CreateDirectedPatch(useAfterPatchValues));
    }

    private void TrimRealtimeUndoHistory()
    {
        if (_realtimeUndoHistory.Count <= MaxRetainedRealtimeHistoryDepth)
        {
            return;
        }

        var newestEntries = _realtimeUndoHistory.ToArray();
        _realtimeUndoHistory.Clear();
        for (var index = MaxRetainedRealtimeHistoryDepth - 1; index >= 0; index--)
        {
            _realtimeUndoHistory.Push(newestEntries[index]);
        }
    }

    private void RaiseRealtimeHistoryAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUndoRealtimeEdits));
        OnPropertyChanged(nameof(CanRedoRealtimeEdits));
    }
}

public enum RealtimeInDocumentEditDomPatchKind
{
    TaskCheckbox,
    TableCell,
}

internal sealed record RealtimeInDocumentEditDomPatch
{
    private RealtimeInDocumentEditDomPatch(
        RealtimeInDocumentEditDomPatchKind kind,
        int line,
        int cellIndex,
        bool beforeChecked,
        bool afterChecked,
        string? beforeText,
        string? beforeKey,
        string? afterText,
        string? afterKey)
    {
        Kind = kind;
        Line = line;
        CellIndex = cellIndex;
        BeforeChecked = beforeChecked;
        AfterChecked = afterChecked;
        BeforeText = beforeText;
        BeforeKey = beforeKey;
        AfterText = afterText;
        AfterKey = afterKey;
    }

    public RealtimeInDocumentEditDomPatchKind Kind { get; }

    public int Line { get; }

    public int CellIndex { get; }

    public bool BeforeChecked { get; }

    public bool AfterChecked { get; }

    public string? BeforeText { get; }

    public string? BeforeKey { get; }

    public string? AfterText { get; }

    public string? AfterKey { get; }

    internal static RealtimeInDocumentEditDomPatch ForTaskCheckbox(int line, bool beforeChecked, bool afterChecked)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        return new RealtimeInDocumentEditDomPatch(
            RealtimeInDocumentEditDomPatchKind.TaskCheckbox,
            line,
            cellIndex: -1,
            beforeChecked,
            afterChecked,
            beforeText: null,
            beforeKey: null,
            afterText: null,
            afterKey: null);
    }

    internal static RealtimeInDocumentEditDomPatch ForTableCell(
        int line,
        int cellIndex,
        string beforeText,
        string beforeKey,
        string afterText,
        string afterKey)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(cellIndex);
        ArgumentNullException.ThrowIfNull(beforeText);
        ArgumentNullException.ThrowIfNull(beforeKey);
        ArgumentNullException.ThrowIfNull(afterText);
        ArgumentNullException.ThrowIfNull(afterKey);

        return new RealtimeInDocumentEditDomPatch(
            RealtimeInDocumentEditDomPatchKind.TableCell,
            line,
            cellIndex,
            beforeChecked: false,
            afterChecked: false,
            beforeText,
            beforeKey,
            afterText,
            afterKey);
    }

    internal RealtimeInDocumentEditDirectedDomPatch CreateDirectedPatch(bool useAfterValues)
        => Kind switch
        {
            RealtimeInDocumentEditDomPatchKind.TaskCheckbox => RealtimeInDocumentEditDirectedDomPatch.ForTaskCheckbox(
                Line,
                useAfterValues ? AfterChecked : BeforeChecked),
            RealtimeInDocumentEditDomPatchKind.TableCell => RealtimeInDocumentEditDirectedDomPatch.ForTableCell(
                Line,
                CellIndex,
                useAfterValues ? AfterText! : BeforeText!,
                useAfterValues ? AfterKey! : BeforeKey!),
            _ => throw new InvalidOperationException($"Unsupported realtime edit DOM patch kind '{Kind}'."),
        };
}

public sealed record RealtimeInDocumentEditDirectedDomPatch(
    RealtimeInDocumentEditDomPatchKind Kind,
    int Line,
    int CellIndex,
    bool Checked,
    string? Text,
    string? Key)
{
    internal static RealtimeInDocumentEditDirectedDomPatch ForTaskCheckbox(int line, bool checkedValue)
        => new(RealtimeInDocumentEditDomPatchKind.TaskCheckbox, line, -1, checkedValue, null, null);

    internal static RealtimeInDocumentEditDirectedDomPatch ForTableCell(int line, int cellIndex, string text, string key)
        => new(RealtimeInDocumentEditDomPatchKind.TableCell, line, cellIndex, false, text, key);
}

internal sealed record RealtimeInDocumentEditHistoryEntry(
    string BeforeSource,
    string AfterSource,
    RealtimeInDocumentEditDomPatch DomPatch);

internal enum RealtimeInDocumentEditHistoryTransitionStatus
{
    Empty,
    Applied,
    Invalidated,
}

internal sealed record RealtimeInDocumentEditHistoryTransition(
    RealtimeInDocumentEditHistoryTransitionStatus Status,
    string? TargetSource,
    RealtimeInDocumentEditDirectedDomPatch? DomPatch)
{
    public static readonly RealtimeInDocumentEditHistoryTransition Empty = new(
        RealtimeInDocumentEditHistoryTransitionStatus.Empty,
        null,
        null);

    public static readonly RealtimeInDocumentEditHistoryTransition Invalidated = new(
        RealtimeInDocumentEditHistoryTransitionStatus.Invalidated,
        null,
        null);

    internal static RealtimeInDocumentEditHistoryTransition Applied(
        string targetSource,
        RealtimeInDocumentEditDirectedDomPatch domPatch)
        => new(RealtimeInDocumentEditHistoryTransitionStatus.Applied, targetSource, domPatch);
}
