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
        ILocalizationService? localization)
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
        _renderedPreview = RenderPreview(_sourceText, _currentPath);
        _statusMessage = string.Empty;
        _splitRatio = 0.5;
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
    /// In-place task-toggle entry: the document on disk moved to
    /// <paramref name="persistedContent"/> while this session holds the
    /// (possibly dormant) buffer. The persisted baseline follows the disk so
    /// <see cref="IsDirty"/> stays truthful and <see cref="DiscardChanges"/>
    /// targets the REAL disk state instead of silently reverting a persisted
    /// toggle; the buffer becomes <paramref name="sourceText"/> (the same flip
    /// applied when the line was still intact, or the unchanged buffer when
    /// the flip was refused). Deliberately skips the synchronous
    /// <see cref="RenderedPreview"/> rebuild — a whole-document parse does not
    /// belong on the zero-cost click path; the native-fallback preview
    /// reconciles on the next SourceText change or document load.
    /// </summary>
    public void ApplyPersistedTaskFlip(string sourceText, string persistedContent)
    {
        var textChanged = SetProperty(ref _sourceText, sourceText ?? string.Empty, nameof(SourceText));
        LastPersistedSource = persistedContent;
        if (textChanged)
        {
            RaiseDocumentMetricsChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
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
}
