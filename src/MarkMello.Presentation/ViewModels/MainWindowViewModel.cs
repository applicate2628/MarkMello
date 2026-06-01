using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkMello.Application.Abstractions;
using MarkMello.Application.Diagnostics;
using MarkMello.Application.Updates;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Presentation.Diagnostics;
using MarkMello.Presentation.Localization;
using System.Reflection;
using System.ComponentModel;

namespace MarkMello.Presentation.ViewModels;

public sealed class ThemeTransitionStartingEventArgs(ThemeMode targetEffectiveTheme) : EventArgs
{
    public ThemeMode TargetEffectiveTheme { get; } = targetEffectiveTheme;
}

/// <summary>
/// View model главного окна. Отвечает за state machine (NoDocument/Viewing/LoadError),
/// тему, reading preferences, команды open/reload, lazy edit mode и dirty/save flow.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly OpenDocumentUseCase _openDocument;
    private readonly SaveDocumentUseCase _saveDocument;
    private readonly IFilePicker _filePicker;
    private readonly ICommandLineActivation _commandLine;
    private readonly ILocalizationService _localization;
    private readonly ISettingsStore _settings;
    private readonly IThemeService _themeService;
    private readonly IStartupMetrics _startupMetrics;
    private readonly RenderMarkdownDocumentUseCase _renderMarkdown;
    private readonly IUpdateService _updateService;
    private readonly IImageSourceResolver? _imageSourceResolver;
    private readonly IRendererReadinessService? _rendererReadiness;

    private bool _documentModelReadyMarked;
    private bool _readableDocumentMarked;
    private bool _secondaryFeaturesMarked;
    private bool _editorActivationMarked;
    private string? _currentPath;
    private readonly object _openingPathsGate = new();
    private readonly Dictionary<string, int> _openingPathCounts = new(StringComparer.OrdinalIgnoreCase);
    private Func<Task>? _pendingDirtyAction;
    private readonly bool _showCustomTitleBar = OperatingSystem.IsWindows();
    private readonly string _aboutVersion;
    private readonly string _aboutLicense = "GPLv3";
    private readonly string _aboutForkAuthor;
    private readonly string _aboutRepositoryUrl;
    private AppUpdatePackage? _availableUpdatePackage;
    private bool _isUpdateNotificationDismissed;
    private LightPaletteMode _selectedLightPalette = LightPaletteMode.White;
    private ReadingPreferences _lastNotifiedReadingPreferences = ReadingPreferences.Default;

    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised immediately BEFORE a document load mutates
    /// <see cref="Document"/>. The Applicate document-switch reveal coordinator
    /// subscribes to raise the active surface's transition cover FIRST, so the
    /// synchronous teardown that follows happens UNDER the cover instead of as
    /// a visible staged teardown. Presentation-layer event so the VM keeps no
    /// dependency on the Applicate-side coordinator.
    /// </summary>
    public event EventHandler? DocumentTransitionStarting;

    /// <summary>
    /// Raised immediately BEFORE a user-driven theme/palette change mutates
    /// Avalonia's effective theme. Applicate uses it to cover the native
    /// WebView until the renderer acks the matching theme paint.
    /// </summary>
    public event EventHandler<ThemeTransitionStartingEventArgs>? ThemeTransitionStarting;

    public MainWindowViewModel(
        OpenDocumentUseCase openDocument,
        SaveDocumentUseCase saveDocument,
        IFilePicker filePicker,
        ICommandLineActivation commandLine,
        ILocalizationService localization,
        ISettingsStore settings,
        IThemeService themeService,
        IStartupMetrics startupMetrics,
        RenderMarkdownDocumentUseCase renderMarkdown,
        IUpdateService updateService,
        IImageSourceResolver? imageSourceResolver = null,
        IRendererReadinessService? rendererReadiness = null)
    {
        _openDocument = openDocument;
        _saveDocument = saveDocument;
        _filePicker = filePicker;
        _commandLine = commandLine;
        _localization = localization;
        _settings = settings;
        _themeService = themeService;
        _startupMetrics = startupMetrics;
        _renderMarkdown = renderMarkdown;
        _updateService = updateService;
        _imageSourceResolver = imageSourceResolver;
        _rendererReadiness = rendererReadiness;
        _aboutVersion = GetProductVersion();
        _aboutForkAuthor = GetAssemblyMetadata("MarkMelloForkAuthor") ?? string.Empty;
        _aboutRepositoryUrl = GetRepositoryUrl();
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshUpdateStatusTexts();
    }

    public IImageSourceResolver? ImageSourceResolver => _imageSourceResolver;

    public string this[string key] => _localization[key];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome))]
    [NotifyPropertyChangedFor(nameof(IsViewer))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    private ViewState _state = ViewState.NoDocument;

    [ObservableProperty]
    private MarkdownSource? _document;

    [ObservableProperty]
    private string _windowTitle = "MarkMello";

    [ObservableProperty]
    private bool _isDragHovering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppMenuOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppAboutOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppUpdatesOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(HasOpenOverlay))]
    private ShellOverlayKind _shellOverlay = ShellOverlayKind.None;

    [ObservableProperty]
    private double _readingProgress;

    [ObservableProperty]
    private ThemeMode _theme = ThemeMode.System;

    [ObservableProperty]
    private ReadingPreferences _readingPreferences = ReadingPreferences.Default;

    [ObservableProperty]
    private RenderedMarkdownDocument _renderedDocument = RenderedMarkdownDocument.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMoonThemeIcon))]
    [NotifyPropertyChangedFor(nameof(ShowsSunThemeIcon))]
    [NotifyPropertyChangedFor(nameof(NextThemeHint))]
    private ThemeMode _effectiveTheme = ThemeMode.Light;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocumentContent))]
    [NotifyPropertyChangedFor(nameof(EditToggleLabel))]
    [NotifyPropertyChangedFor(nameof(EditShortcutLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsEditPencilIcon))]
    [NotifyPropertyChangedFor(nameof(ShowsReadEyeIcon))]
    [NotifyPropertyChangedFor(nameof(ShowsAppMenuControl))]
    [NotifyPropertyChangedFor(nameof(IsAppMenuOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppSettingsOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppAboutOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppUpdatesOpen))]
    [NotifyPropertyChangedFor(nameof(IsAppOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(HasOpenOverlay))]
    [NotifyPropertyChangedFor(nameof(ShowsDirtySaveButton))]
    private bool _isEditMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocumentContent))]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyPropertyChangedFor(nameof(ShowsDirtySaveButton))]
    private EditorSessionViewModel? _editorSession;

    [ObservableProperty]
    private bool _isDirtyPromptOpen;

    [ObservableProperty]
    private string _dirtyPromptTitle = string.Empty;

    [ObservableProperty]
    private string _dirtyPromptMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirtyPromptError))]
    private string _dirtyPromptErrorMessage = string.Empty;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorDetails = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAlwaysOnTopDisabled))]
    private bool _isAlwaysOnTop;

    [ObservableProperty]
    private string _updateStatusTitle = string.Empty;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    [ObservableProperty]
    private string? _downloadedUpdatePath;

    public object ActiveDocumentContent => IsEditMode && EditorSession is not null ? EditorSession : this;

    public string FileName => EditorSession?.FileName ?? Document?.FileName ?? string.Empty;

    public string TitleFileDisplayName => string.IsNullOrWhiteSpace(FileName)
        ? string.Empty
        : FileName + (IsDirty ? " •" : string.Empty);

    public bool HasDocumentTitle => State == ViewState.Viewing && !string.IsNullOrWhiteSpace(FileName);

    public bool IsWelcome => State == ViewState.NoDocument;

    public bool IsViewer => State == ViewState.Viewing;

    public bool IsError => State == ViewState.LoadError;

    public bool IsDirty => EditorSession?.IsDirty == true;

    public bool ShowsDirtySaveButton => IsEditMode && IsDirty;

    public bool ShowCustomTitleBar => _showCustomTitleBar;

    public bool IsSettingsOpen => ShellOverlay == ShellOverlayKind.ReadingSettings;

    public bool ShowsAppMenuControl => !IsEditMode;

    public bool IsAppMenuOpen => ShowsAppMenuControl && ShellOverlay == ShellOverlayKind.AppMenu;

    public bool IsAppSettingsOpen => ShowsAppMenuControl && ShellOverlay == ShellOverlayKind.AppSettings;

    public bool IsAppAboutOpen => ShowsAppMenuControl && ShellOverlay == ShellOverlayKind.AppAbout;

    public bool IsAppUpdatesOpen => ShowsAppMenuControl && ShellOverlay == ShellOverlayKind.AppUpdates;

    public bool IsAppOverlayOpen => ShowsAppMenuControl
        && ShellOverlay is
            ShellOverlayKind.AppMenu
            or ShellOverlayKind.AppSettings
            or ShellOverlayKind.AppAbout
            or ShellOverlayKind.AppUpdates;

    public bool HasOpenOverlay => IsSettingsOpen || IsAppOverlayOpen;

    public bool ShowsReadingStatus => IsViewer && !IsEditMode;

    public bool ShowsMoonThemeIcon => EffectiveTheme != ThemeMode.Dark;

    public bool ShowsSunThemeIcon => EffectiveTheme == ThemeMode.Dark;

    public bool IsOriginalPaletteSelected
    {
        get => _selectedLightPalette != LightPaletteMode.White;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsOriginalPaletteSelected));
                return;
            }

            ApplyLightPalette(LightPaletteMode.Original);
        }
    }

    public bool IsWhitePaletteSelected
    {
        get => _selectedLightPalette == LightPaletteMode.White;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsWhitePaletteSelected));
                return;
            }

            ApplyLightPalette(LightPaletteMode.White);
        }
    }

    public bool ShowsEditPencilIcon => !IsEditMode;

    public bool ShowsReadEyeIcon => IsEditMode;

    public bool ShowsEditToggle => State == ViewState.Viewing && Document is not null;

    public string EditToggleLabel => IsEditMode ? _localization["ModeReading"] : _localization["ModeEdit"];

    public string EditShortcutLabel => IsEditMode ? _localization["ModeReadShortcut"] : _localization["ModeEditShortcut"];

    public string AboutVersion => _aboutVersion;

    public string AboutLicense => _aboutLicense;

    public bool HasAboutForkInfo => !string.IsNullOrWhiteSpace(AboutForkAuthor);

    public string AboutForkAuthor => _aboutForkAuthor;

    public string AboutRepositoryUrl => _aboutRepositoryUrl;

    public bool HasDirtyPromptError => !string.IsNullOrWhiteSpace(DirtyPromptErrorMessage);

    public bool CanCheckForUpdates => !IsCheckingForUpdates && !IsDownloadingUpdate;

    public bool CanDownloadAvailableUpdate
        => _availableUpdatePackage is not null
           && string.IsNullOrWhiteSpace(DownloadedUpdatePath)
           && !IsCheckingForUpdates
           && !IsDownloadingUpdate;

    public bool CanOpenDownloadedUpdate
        => _availableUpdatePackage is not null
           && !string.IsNullOrWhiteSpace(DownloadedUpdatePath)
           && !IsCheckingForUpdates
           && !IsDownloadingUpdate;

    public bool IsUpdateBusy => IsCheckingForUpdates || IsDownloadingUpdate;

    public double UpdateBusyIndicatorOpacity => IsUpdateBusy ? 1.0 : 0.0;

    public double CheckForUpdatesIdleLabelOpacity => IsCheckingForUpdates ? 0.0 : 1.0;

    public double CheckForUpdatesBusyLabelOpacity => IsCheckingForUpdates ? 1.0 : 0.0;

    public double DownloadUpdateIdleLabelOpacity => IsDownloadingUpdate ? 0.0 : 1.0;

    public double DownloadUpdateBusyLabelOpacity => IsDownloadingUpdate ? 1.0 : 0.0;

    public double DownloadUpdateActionOpacity => CanDownloadAvailableUpdate || IsDownloadingUpdate ? 1.0 : 0.0;

    public double OpenDownloadedUpdateActionOpacity => CanOpenDownloadedUpdate ? 1.0 : 0.0;

    public bool IsUpdateNotificationVisible
        => !_isUpdateNotificationDismissed
           && _updateStatus is UpdateStatusSnapshot.UpdateAvailableState or UpdateStatusSnapshot.DownloadReadyState;

    public bool IsAlwaysOnTopDisabled
    {
        get => !IsAlwaysOnTop;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsAlwaysOnTopDisabled));
                return;
            }

            IsAlwaysOnTop = false;
        }
    }

    public string CheckForUpdatesLabel => IsCheckingForUpdates ? _localization["UpdateChecking"] : _localization["UpdateCheckNow"];

    public string CheckForUpdatesIdleLabel => _localization["UpdateCheckNow"];

    public string CheckForUpdatesBusyLabel => _localization["UpdateChecking"];

    public string DownloadUpdateLabel => IsDownloadingUpdate ? _localization["UpdateDownloading"] : _localization["UpdateDownload"];

    public string DownloadUpdateIdleLabel => _localization["UpdateDownload"];

    public string DownloadUpdateBusyLabel => _localization["UpdateDownloading"];

    public string DownloadedUpdateActionLabel
        => _availableUpdatePackage?.InstallAction switch
        {
            AppUpdateInstallAction.LaunchInstaller => _localization["UpdateLaunchInstaller"],
            AppUpdateInstallAction.OpenDiskImage => _localization["UpdateOpenDmg"],
            AppUpdateInstallAction.RevealFile => _localization["UpdateRevealAppImage"],
            _ => _localization["UpdateOpenDownloaded"]
        };

    public string UpdateStateBadge
        => IsCheckingForUpdates
            ? _localization["UpdateBadgeChecking"]
            : IsDownloadingUpdate
                ? _localization["UpdateBadgeDownloading"]
                : CanOpenDownloadedUpdate
                    ? _localization["UpdateBadgeReady"]
                    : CanDownloadAvailableUpdate
                        ? _localization["UpdateBadgeAvailable"]
                        : _localization["UpdateBadgeManual"];

    public string AppMenuUpdateStateBadge
        => IsDownloadingUpdate
            ? _localization["UpdateBadgeDownloading"]
            : CanOpenDownloadedUpdate
                ? _localization["UpdateBadgeReady"]
                : CanDownloadAvailableUpdate
                    ? _localization["UpdateBadgeAvailable"]
                    : _localization["UpdateBadgeManual"];

    public FontFamilyMode SelectedFontFamilyMode
    {
        get => ReadingPreferences.FontFamily;
        set
        {
            if (ReadingPreferences.FontFamily == value)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { FontFamily = value });
        }
    }

    public double FontSizeSetting
    {
        get => ReadingPreferences.FontSize;
        set
        {
            var fontSize = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            if (ReadingPreferences.FontSize == fontSize)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { FontSize = fontSize });
        }
    }

    public double LineHeightSetting
    {
        get => ReadingPreferences.LineHeight;
        set
        {
            var normalized = Math.Round(
                value / ReadingPreferences.LineHeightStep,
                MidpointRounding.AwayFromZero) * ReadingPreferences.LineHeightStep;

            if (Math.Abs(ReadingPreferences.LineHeight - normalized) < 0.0001)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { LineHeight = normalized });
        }
    }

    public double DocumentColumnMaxWidth => ReadingLayoutMetrics.GetDocumentColumnMaxWidth(ReadingPreferences);

    public double ContentWidthSetting
    {
        get => ReadingPreferences.ContentWidth;
        set
        {
            var contentWidth = (int)Math.Round(
                value / ReadingPreferences.ContentWidthStep,
                MidpointRounding.AwayFromZero) * ReadingPreferences.ContentWidthStep;

            if (ReadingPreferences.ContentWidth == contentWidth)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { ContentWidth = contentWidth });
        }
    }

    public string FontSizeLabel => $"{ReadingPreferences.FontSize}px";

    public string LineHeightLabel => ReadingPreferences.LineHeight.ToString("0.00", _localization.Culture);

    public bool IsSerifFontSelected
    {
        get => ReadingPreferences.FontFamily == FontFamilyMode.Serif;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsSerifFontSelected));
                return;
            }

            SelectedFontFamilyMode = FontFamilyMode.Serif;
        }
    }

    public bool IsSansFontSelected
    {
        get => ReadingPreferences.FontFamily == FontFamilyMode.Sans;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsSansFontSelected));
                return;
            }

            SelectedFontFamilyMode = FontFamilyMode.Sans;
        }
    }

    public bool IsMonoFontSelected
    {
        get => ReadingPreferences.FontFamily == FontFamilyMode.Mono;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsMonoFontSelected));
                return;
            }

            SelectedFontFamilyMode = FontFamilyMode.Mono;
        }
    }

    public bool IsNarrowWidthSelected
    {
        get => ReadingPreferences.ContentWidth == ReadingPreferences.NarrowContentWidth;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsNarrowWidthSelected));
                return;
            }

            ContentWidthSetting = ReadingPreferences.NarrowContentWidth;
        }
    }

    public bool IsMediumWidthSelected
    {
        get => ReadingPreferences.ContentWidth == ReadingPreferences.MediumContentWidth;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsMediumWidthSelected));
                return;
            }

            ContentWidthSetting = ReadingPreferences.MediumContentWidth;
        }
    }

    public bool IsWideWidthSelected
    {
        get => ReadingPreferences.ContentWidth == ReadingPreferences.WideContentWidth;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsWideWidthSelected));
                return;
            }

            ContentWidthSetting = ReadingPreferences.WideContentWidth;
        }
    }

    public WidthResizerVisibility SelectedWidthResizerVisibility
    {
        get => ReadingPreferences.WidthResizerVisibility;
        set
        {
            if (ReadingPreferences.WidthResizerVisibility == value)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { WidthResizerVisibility = value });
        }
    }

    public bool IsWidthResizerAlwaysSelected
    {
        get => ReadingPreferences.WidthResizerVisibility == WidthResizerVisibility.Always;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsWidthResizerAlwaysSelected));
                return;
            }

            SelectedWidthResizerVisibility = WidthResizerVisibility.Always;
        }
    }

    public bool IsWidthResizerOnHoverSelected
    {
        get => ReadingPreferences.WidthResizerVisibility == WidthResizerVisibility.OnHover;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsWidthResizerOnHoverSelected));
                return;
            }

            SelectedWidthResizerVisibility = WidthResizerVisibility.OnHover;
        }
    }

    public bool IsModeSwitchSmoothEnabled
    {
        get => ReadingPreferences.ModeSwitchSmoothEnabled;
        set
        {
            if (ReadingPreferences.ModeSwitchSmoothEnabled == value)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { ModeSwitchSmoothEnabled = value });
        }
    }

    public bool IsModeSwitchSmoothDisabled
    {
        get => !ReadingPreferences.ModeSwitchSmoothEnabled;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsModeSwitchSmoothDisabled));
                return;
            }

            IsModeSwitchSmoothEnabled = false;
        }
    }

    public double ModeSwitchSmoothDurationSetting
    {
        get => ReadingPreferences.ModeSwitchSmoothDurationMs;
        set
        {
            var durationMs = (int)Math.Round(
                value / ReadingPreferences.ModeSwitchSmoothDurationStepMs,
                MidpointRounding.AwayFromZero) * ReadingPreferences.ModeSwitchSmoothDurationStepMs;

            if (ReadingPreferences.ModeSwitchSmoothDurationMs == durationMs)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { ModeSwitchSmoothDurationMs = durationMs });
        }
    }

    public string ModeSwitchSmoothDurationLabel => $"{ReadingPreferences.ModeSwitchSmoothDurationMs} ms";

    public DocumentMinimapMode SelectedDocumentMinimapMode
    {
        get => ReadingPreferences.DocumentMinimapMode;
        set
        {
            if (ReadingPreferences.DocumentMinimapMode == value)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { DocumentMinimapMode = value });
        }
    }

    public bool IsDocumentMinimapAutoSelected
    {
        get => ReadingPreferences.DocumentMinimapMode == DocumentMinimapMode.Auto;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsDocumentMinimapAutoSelected));
                return;
            }

            SelectedDocumentMinimapMode = DocumentMinimapMode.Auto;
        }
    }

    public bool IsDocumentMinimapOnSelected
    {
        get => ReadingPreferences.DocumentMinimapMode == DocumentMinimapMode.On;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsDocumentMinimapOnSelected));
                return;
            }

            SelectedDocumentMinimapMode = DocumentMinimapMode.On;
        }
    }

    public bool IsDocumentMinimapOffSelected
    {
        get => ReadingPreferences.DocumentMinimapMode == DocumentMinimapMode.Off;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsDocumentMinimapOffSelected));
                return;
            }

            SelectedDocumentMinimapMode = DocumentMinimapMode.Off;
        }
    }

    public MarkdownRendererBackend SelectedRendererBackend
    {
        get => ReadingPreferences.RendererBackend;
        set
        {
            if (ReadingPreferences.RendererBackend == value)
            {
                return;
            }

            ApplyReadingPreferences(ReadingPreferences with { RendererBackend = value });
        }
    }

    public bool IsNativeRendererSelected
    {
        get => ReadingPreferences.RendererBackend == MarkdownRendererBackend.Native;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsNativeRendererSelected));
                return;
            }

            SelectedRendererBackend = MarkdownRendererBackend.Native;
        }
    }

    public bool IsWebViewRendererSelected
    {
        get => ReadingPreferences.RendererBackend == MarkdownRendererBackend.WebView;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsWebViewRendererSelected));
                return;
            }

            SelectedRendererBackend = MarkdownRendererBackend.WebView;
        }
    }

    public int WordCount => EditorSession?.WordCount ?? CountWords(Document?.Content);

    public int ReadTimeMinutes => Math.Max(1, (int)Math.Round(WordCount / 220.0));

    public string NextThemeHint => EffectiveTheme != ThemeMode.Dark
        ? _localization["ThemeSwitchToDark"]
        : _localization["ThemeSwitchToLight"];

    public async Task InitializeAsync()
    {
        ReadingPreferences = await _settings.LoadPreferencesAsync().ConfigureAwait(true);
        _selectedLightPalette = ReadingPreferences.LightPalette;

        var savedLanguage = await _settings.LoadLanguageAsync().ConfigureAwait(true);
        ApplyLanguageSelection(savedLanguage, persist: false);

        var savedTheme = await _settings.LoadThemeAsync().ConfigureAwait(true);
        ApplyTheme(savedTheme);

        var path = _commandLine.GetActivationFilePath();
        if (!string.IsNullOrEmpty(path))
        {
            await OpenPathAsync(path).ConfigureAwait(true);
        }

        BeginStartupUpdateCheck();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        CloseOverlayCore();
        await RunWithDirtyCheckAsync(PendingDirtyActionKind.OpenFile, OpenFileCoreAsync).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateNewDocumentAsync()
    {
        CloseOverlayCore();
        await RunWithDirtyCheckAsync(
                PendingDirtyActionKind.CreateNewDocument,
                CreateNewDocumentCoreAsync)
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanCloseFile))]
    private async Task CloseFileAsync()
    {
        CloseOverlayCore();
        await RunWithDirtyCheckAsync(
                PendingDirtyActionKind.CloseFile,
                CloseFileCoreAsync)
            .ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanReload))]
    private async Task ReloadAsync()
    {
        var path = CurrentDocumentPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var preserveEditMode = IsEditMode;
        await RunWithDirtyCheckAsync(
            PendingDirtyActionKind.Reload,
            () => LoadDocumentAsync(path, preserveEditModeAfterLoad: preserveEditMode))
            .ConfigureAwait(true);
    }

    private bool CanReload() => !string.IsNullOrEmpty(CurrentDocumentPath);

    private bool CanCloseFile() => Document is not null || EditorSession is not null;

    [RelayCommand(CanExecute = nameof(CanToggleEditMode))]
    private async Task ToggleEditModeAsync()
    {
        if (IsEditMode)
        {
            await RunWithDirtyCheckAsync(
                PendingDirtyActionKind.LeaveEditMode,
                ExitEditModeCoreAsync)
                .ConfigureAwait(true);
            return;
        }

        EnterEditModeCore();
    }

    private bool CanToggleEditMode() => State == ViewState.Viewing && Document is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var outcome = await SaveEditorAsync(promptForPathWhenMissing: true, forceSaveAs: false).ConfigureAwait(true);
        if (outcome.Cancelled)
        {
            return;
        }

        if (outcome.Result is not SaveDocumentResult.Success success)
        {
            EditorSession?.SetStatusMessage(GetSaveFailureMessage(outcome.Result));
            return;
        }

        ApplySavedDocument(success.Source);
    }

    private bool CanSave() => IsEditMode && EditorSession is not null;

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private async Task SaveAsAsync()
    {
        var outcome = await SaveEditorAsync(promptForPathWhenMissing: true, forceSaveAs: true).ConfigureAwait(true);
        if (outcome.Cancelled)
        {
            return;
        }

        if (outcome.Result is not SaveDocumentResult.Success success)
        {
            EditorSession?.SetStatusMessage(GetSaveFailureMessage(outcome.Result));
            return;
        }

        ApplySavedDocument(success.Source);
    }

    private bool CanSaveAs() => IsEditMode && EditorSession is not null;

    [RelayCommand]
    private async Task ConfirmDirtySaveAsync()
    {
        if (_pendingDirtyAction is null)
        {
            return;
        }

        SetDirtyPromptError(null);

        var outcome = await SaveEditorAsync(promptForPathWhenMissing: true, forceSaveAs: false).ConfigureAwait(true);
        if (outcome.Cancelled)
        {
            return;
        }

        if (outcome.Result is not SaveDocumentResult.Success success)
        {
            SetDirtyPromptError(outcome.Result);
            return;
        }

        ApplySavedDocument(success.Source);
        await ContinuePendingDirtyActionAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ConfirmDirtyDiscardAsync()
    {
        DiscardEditorChanges();
        await ContinuePendingDirtyActionAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelDirtyPrompt()
    {
        ClearDirtyPrompt();
    }

    [RelayCommand]
    private void CycleTheme()
    {
        var next = EffectiveTheme != ThemeMode.Dark
            ? ThemeMode.Dark
            : ThemeMode.Light;

        ApplyThemeSelection(next);
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        MarkSecondaryFeaturesReady();

        ShellOverlay = IsSettingsOpen
            ? ShellOverlayKind.None
            : ShellOverlayKind.ReadingSettings;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        if (IsSettingsOpen)
        {
            ShellOverlay = ShellOverlayKind.None;
        }
    }

    [RelayCommand]
    private void ToggleAppMenu()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = IsAppOverlayOpen
            ? ShellOverlayKind.None
            : ShellOverlayKind.AppMenu;
    }

    [RelayCommand]
    private void OpenAppSettings()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = ShellOverlayKind.AppSettings;
    }

    [RelayCommand]
    private void OpenAbout()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = ShellOverlayKind.AppAbout;
    }

    [RelayCommand]
    private void OpenAppUpdates()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = ShellOverlayKind.AppUpdates;
    }

    [RelayCommand]
    private void ReturnToAppMenu()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = ShellOverlayKind.AppMenu;
    }

    [RelayCommand]
    private void ReturnToAppSettings()
    {
        if (!ShowsAppMenuControl)
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();

        ShellOverlay = ShellOverlayKind.AppSettings;
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        CloseOverlayCore();
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        IsDownloadingUpdate = false;
        _availableUpdatePackage = null;
        DownloadedUpdatePath = null;
        SetUpdateStatus(new UpdateStatusSnapshot.CheckingState());
        UpdateCommandStates();

        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(true);
            switch (result)
            {
                case UpdateCheckResult.SourceNotConfigured:
                    SetUpdateStatus(new UpdateStatusSnapshot.SourceNotConfiguredState());
                    break;

                case UpdateCheckResult.UnsupportedPlatform unsupportedPlatform:
                    SetUpdateStatus(new UpdateStatusSnapshot.UnsupportedPlatformState(
                        unsupportedPlatform.PlatformName,
                        unsupportedPlatform.ArchitectureName));
                    break;

                case UpdateCheckResult.UpToDate upToDate:
                    SetUpdateStatus(new UpdateStatusSnapshot.UpToDateState(
                        upToDate.CurrentVersion,
                        upToDate.LatestVersion));
                    break;

                case UpdateCheckResult.UpdateAvailable updateAvailable:
                    _availableUpdatePackage = updateAvailable.Package;
                    SetUpdateStatus(new UpdateStatusSnapshot.UpdateAvailableState(updateAvailable.Package));
                    break;

                case UpdateCheckResult.Failed failed:
                    SetUpdateStatus(new UpdateStatusSnapshot.CheckFailedState(failed.Message));
                    break;
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            UpdateCommandStates();
        }
    }

    private void BeginStartupUpdateCheck()
    {
        if (!CanCheckForUpdates)
        {
            return;
        }

        _ = CheckForUpdatesForStartupAsync();
    }

    private async Task CheckForUpdatesForStartupAsync()
    {
        try
        {
            await CheckForUpdatesAsync().ConfigureAwait(true);
        }
        catch (System.Exception ex)
        {
            IsCheckingForUpdates = false;
            SetUpdateStatus(new UpdateStatusSnapshot.CheckFailedState(ex.Message));
            UpdateCommandStates();
        }
    }

    [RelayCommand]
    private void DismissUpdateNotification()
    {
        _isUpdateNotificationDismissed = true;
        OnPropertyChanged(nameof(IsUpdateNotificationVisible));
    }

    [RelayCommand(CanExecute = nameof(CanDownloadAvailableUpdate))]
    private async Task DownloadUpdateAsync()
    {
        if (_availableUpdatePackage is null)
        {
            return;
        }

        IsDownloadingUpdate = true;
        SetUpdateStatus(new UpdateStatusSnapshot.DownloadingState(_availableUpdatePackage));
        UpdateCommandStates();

        try
        {
            var result = await _updateService
                .DownloadUpdateAsync(_availableUpdatePackage)
                .ConfigureAwait(true);

            switch (result)
            {
                case UpdateDownloadResult.Success success:
                    _availableUpdatePackage = success.Package;
                    DownloadedUpdatePath = success.DownloadedFilePath;
                    SetUpdateStatus(new UpdateStatusSnapshot.DownloadReadyState(success.Package, success.DownloadedFilePath));
                    break;

                case UpdateDownloadResult.Failed failed:
                    DownloadedUpdatePath = null;
                    SetUpdateStatus(new UpdateStatusSnapshot.DownloadFailedState(failed.Message));
                    break;
            }
        }
        finally
        {
            IsDownloadingUpdate = false;
            UpdateCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenDownloadedUpdate))]
    private async Task OpenDownloadedUpdateAsync()
    {
        if (_availableUpdatePackage is null || string.IsNullOrWhiteSpace(DownloadedUpdatePath))
        {
            return;
        }

        var result = await _updateService
            .PrepareDownloadedUpdateAsync(_availableUpdatePackage, DownloadedUpdatePath)
            .ConfigureAwait(true);

        switch (result)
        {
            case UpdatePrepareResult.Success:
                SetUpdateStatus(new UpdateStatusSnapshot.NativeFlowStartedState(_availableUpdatePackage));
                break;

            case UpdatePrepareResult.Failed failed:
                SetUpdateStatus(new UpdateStatusSnapshot.OpenDownloadedFailedState(failed.Message));
                break;
        }

        UpdateCommandStates();
    }

    [RelayCommand]
    private void ClearError()
    {
        if (IsDirtyPromptOpen)
        {
            CancelDirtyPrompt();
            return;
        }

        if (HasOpenOverlay)
        {
            CloseOverlayCore();
            return;
        }

        if (State == ViewState.LoadError)
        {
            State = Document is null ? ViewState.NoDocument : ViewState.Viewing;
            ClearLoadError();
            RefreshWindowTitle();
        }
    }

    public async Task OpenDroppedFileAsync(string path)
        => await RunWithDirtyCheckAsync(
            PendingDirtyActionKind.OpenFile,
            () => LoadDocumentAsync(path, preserveEditModeAfterLoad: false))
            .ConfigureAwait(true);

    public async Task OpenPathAsync(string path)
        => await LoadDocumentAsync(path, preserveEditModeAfterLoad: false).ConfigureAwait(true);

    public bool IsOpeningPath(string? path)
    {
        var key = NormalizeOpeningPath(path);
        if (key is null)
        {
            return false;
        }

        lock (_openingPathsGate)
        {
            return _openingPathCounts.ContainsKey(key);
        }
    }

    public bool TryQueueCloseRequest()
    {
        if (IsDirtyPromptOpen)
        {
            return true;
        }

        if (!RequiresDirtyResolution)
        {
            return false;
        }

        QueueDirtyAction(
            PendingDirtyActionKind.CloseWindow,
            () =>
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            });

        return true;
    }

    partial void OnDocumentChanged(MarkdownSource? value)
    {
        // C3 (atomic transition): clear the TOC only when there is no document
        // (close, or a load failure that nulls Document). On a viewer->viewer
        // switch Document stays non-null, so old headings remain until the new
        // list replaces them atomically (UpdateDocumentHeadings) -- no empty
        // phase, so DocumentHeadings.Count never hits 0 and IsTocVisible never
        // drops to false: the TOC column repaints its rows in place instead of
        // collapsing+re-expanding. A renderer crash mid-render (Document stays
        // non-null) is covered by the coordinator's OnRendererFailed clear.
        if (value is null)
        {
            ClearDocumentHeadings();
        }
        else
        {
            _pendingScrollToHeadingId = null;
        }
        RefreshDocumentSummary();
        OnPropertyChanged(nameof(ShowsEditToggle));
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    partial void OnStateChanged(ViewState value)
    {
        OnPropertyChanged(nameof(HasDocumentTitle));
        OnPropertyChanged(nameof(ShowsReadingStatus));
        OnPropertyChanged(nameof(ShowsEditToggle));
        // IsTocVisible depends on IsViewer (= State == Viewing); refresh
        // the composite predicate when State transitions.
        OnPropertyChanged(nameof(IsTocVisible));
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        if (value)
        {
            CloseAppOverlayCore();
        }

        OnPropertyChanged(nameof(EditToggleLabel));
        OnPropertyChanged(nameof(EditShortcutLabel));
        OnPropertyChanged(nameof(ShowsEditPencilIcon));
        OnPropertyChanged(nameof(ShowsReadEyeIcon));
        OnPropertyChanged(nameof(ShowsReadingStatus));
        OnPropertyChanged(nameof(ShowsAppMenuControl));
        OnPropertyChanged(nameof(IsAppMenuOpen));
        OnPropertyChanged(nameof(IsAppSettingsOpen));
        OnPropertyChanged(nameof(IsAppAboutOpen));
        OnPropertyChanged(nameof(IsAppUpdatesOpen));
        OnPropertyChanged(nameof(IsAppOverlayOpen));
        OnPropertyChanged(nameof(HasOpenOverlay));
        OnPropertyChanged(nameof(ActiveDocumentContent));
        // IsTocVisible no longer hides edit mode, but mode transitions can
        // still change chrome around the TOC; refresh the composite predicate
        // so bindings stay in sync.
        OnPropertyChanged(nameof(IsTocVisible));
        UpdateCommandStates();
    }

    partial void OnEditorSessionChanging(EditorSessionViewModel? oldValue, EditorSessionViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnEditorSessionPropertyChanged;
        }
    }

    partial void OnEditorSessionChanged(EditorSessionViewModel? value)
    {
        if (value is not null)
        {
            value.PropertyChanged += OnEditorSessionPropertyChanged;
            value.UpdateReadingPreferences(ReadingPreferences);
            _currentPath = value.CurrentPath;
        }

        RefreshDocumentSummary();
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    partial void OnReadingPreferencesChanged(ReadingPreferences value)
    {
        var oldValue = _lastNotifiedReadingPreferences;
        _lastNotifiedReadingPreferences = value;

        // F-03/F-12 fix: propagate ReadingPreferences directly to the
        // editor session. The old _documentReadingPreferences ghost copy
        // (minimap-stripped variant for the deleted native renderer) is
        // gone -- the WebView consumes ReadingPreferences directly.
        if (value != oldValue)
        {
            EditorSession?.UpdateReadingPreferences(value);
        }

        NotifyReadingPreferenceDependentBindings(oldValue, value);
    }

    private void NotifyReadingPreferenceDependentBindings(
        ReadingPreferences oldValue,
        ReadingPreferences value)
    {
        if (oldValue.FontFamily != value.FontFamily)
        {
            OnPropertyChanged(nameof(SelectedFontFamilyMode));
            OnPropertyChanged(nameof(IsSerifFontSelected));
            OnPropertyChanged(nameof(IsSansFontSelected));
            OnPropertyChanged(nameof(IsMonoFontSelected));
        }

        if (Math.Abs(oldValue.FontSize - value.FontSize) > 0.0001)
        {
            OnPropertyChanged(nameof(FontSizeSetting));
            OnPropertyChanged(nameof(FontSizeLabel));
        }

        if (Math.Abs(oldValue.LineHeight - value.LineHeight) > 0.0001)
        {
            OnPropertyChanged(nameof(LineHeightSetting));
            OnPropertyChanged(nameof(LineHeightLabel));
        }

        if (Math.Abs(oldValue.ContentWidth - value.ContentWidth) > 0.0001)
        {
            OnPropertyChanged(nameof(ContentWidthSetting));
            OnPropertyChanged(nameof(DocumentColumnMaxWidth));
            OnPropertyChanged(nameof(IsNarrowWidthSelected));
            OnPropertyChanged(nameof(IsMediumWidthSelected));
            OnPropertyChanged(nameof(IsWideWidthSelected));
        }

        if (oldValue.WidthResizerVisibility != value.WidthResizerVisibility)
        {
            OnPropertyChanged(nameof(SelectedWidthResizerVisibility));
            OnPropertyChanged(nameof(IsWidthResizerAlwaysSelected));
            OnPropertyChanged(nameof(IsWidthResizerOnHoverSelected));
        }

        if (oldValue.ModeSwitchSmoothEnabled != value.ModeSwitchSmoothEnabled)
        {
            OnPropertyChanged(nameof(IsModeSwitchSmoothEnabled));
            OnPropertyChanged(nameof(IsModeSwitchSmoothDisabled));
        }

        if (oldValue.ModeSwitchSmoothDurationMs != value.ModeSwitchSmoothDurationMs)
        {
            OnPropertyChanged(nameof(ModeSwitchSmoothDurationSetting));
            OnPropertyChanged(nameof(ModeSwitchSmoothDurationLabel));
        }

        if (oldValue.DocumentMinimapMode != value.DocumentMinimapMode)
        {
            OnPropertyChanged(nameof(SelectedDocumentMinimapMode));
            OnPropertyChanged(nameof(IsDocumentMinimapAutoSelected));
            OnPropertyChanged(nameof(IsDocumentMinimapOnSelected));
            OnPropertyChanged(nameof(IsDocumentMinimapOffSelected));
        }

        if (oldValue.RendererBackend != value.RendererBackend)
        {
            OnPropertyChanged(nameof(SelectedRendererBackend));
            OnPropertyChanged(nameof(IsNativeRendererSelected));
            OnPropertyChanged(nameof(IsWebViewRendererSelected));
        }

        if (oldValue.LightPalette != value.LightPalette)
        {
            _selectedLightPalette = value.LightPalette;
            OnPropertyChanged(nameof(IsOriginalPaletteSelected));
            OnPropertyChanged(nameof(IsWhitePaletteSelected));
        }
    }

    private async Task OpenFileCoreAsync()
    {
        var path = await _filePicker.PickMarkdownFileAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        await LoadDocumentAsync(path, preserveEditModeAfterLoad: false).ConfigureAwait(true);
    }

    private Task CreateNewDocumentCoreAsync()
    {
        CreateNewDocumentCore();
        return Task.CompletedTask;
    }

    private void CreateNewDocumentCore()
    {
        Document = null;
        RenderedDocument = RenderedMarkdownDocument.Empty;
        _currentPath = null;
        State = ViewState.Viewing;
        ReadingProgress = 0;
        ClearLoadError();
        CloseOverlayCore();
        EditorSession = new EditorSessionViewModel(
            GetUntitledFileName(),
            string.Empty,
            ReadingPreferences,
            _renderMarkdown,
            _imageSourceResolver,
            _localization);

        if (!_editorActivationMarked)
        {
            _editorActivationMarked = true;
            _startupMetrics.Mark(StartupStage.EditorActivation);
        }

        EditorSession.UpdateReadingPreferences(ReadingPreferences);
        EditorSession.SetStatusMessage(string.Empty);
        IsEditMode = true;
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private Task CloseFileCoreAsync()
    {
        CloseFileCore();
        return Task.CompletedTask;
    }

    private void CloseFileCore()
    {
        CloseOverlayCore();
        IsEditMode = false;
        EditorSession = null;
        Document = null;
        RenderedDocument = RenderedMarkdownDocument.Empty;
        _currentPath = null;
        State = ViewState.NoDocument;
        ReadingProgress = 0;
        ClearLoadError();
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private void EnterEditModeCore()
    {
        if (Document is null)
        {
            return;
        }

        if (EditorSession is null)
        {
            EditorSession = new EditorSessionViewModel(
                Document,
                ReadingPreferences,
                _renderMarkdown,
                _imageSourceResolver,
                _localization);
        }

        if (!_editorActivationMarked)
        {
            _editorActivationMarked = true;
            _startupMetrics.Mark(StartupStage.EditorActivation);
        }

        EditorSession.UpdateReadingPreferences(ReadingPreferences);
        EditorSession.SetStatusMessage(string.Empty);
        IsEditMode = true;
    }

    private Task ExitEditModeCoreAsync()
    {
        IsEditMode = false;
        EditorSession?.SetStatusMessage(string.Empty);
        return Task.CompletedTask;
    }

    private async Task LoadDocumentAsync(string path, bool preserveEditModeAfterLoad)
    {
        var openingPath = NormalizeOpeningPath(path);
        TrackOpeningPath(openingPath);
        try
        {
            // PE r2 §2 item D — consume the EarlyDocumentCache entry if Program.Main
            // pre-read this path on a thread-pool task. The pre-read starts right
            // after singleInstance.StartListening() and typically completes before
            // the VM async path reaches here, overlapping the file read + parse cost
            // with Avalonia init / window-open (saves ~150-250 ms per PE r2 §1 P2).
            //
            // On hit: skip _openDocument.ExecuteAsync entirely and dispatch the
            // cached source through the existing ApplyOpenResult / ApplyLoadedDocument
            // pipeline so all downstream invariants (state machine, edit-mode
            // preservation, startup metrics, bridge reconcile gating) stay
            // identical to the cold path.
            //
            // On miss: fall through to _openDocument.ExecuteAsync (FileDocumentLoader)
            // which retains the full typed-error handling for I/O / parse failures.
            if (!string.IsNullOrWhiteSpace(path)
                && EarlyDocumentCache.TryConsume(path, out var cached)
                && cached is not null)
            {
                StartupDiag.DiagMs("startup-pre-window", "perf-doc cache-hit", $"path={cached.Path}");

                // Renderer-readiness rendezvous (D-phase race fix). The cache-hit
                // completes the file-I/O cost in ~0 ms, so without an explicit
                // rendezvous Document/State get published BEFORE the WebView2
                // environment finishes initialising and the shell HTML loads. The
                // renderer pipeline then starts mid-load; a moment later
                // session-restoration triggers an edit-mode reconcile that
                // reparents the WebView into the edit slot, interrupting the
                // in-flight initial-visible-ready pass and stalling
                // first-paint by 10+ s (smoke d_fix_clean3, .scratch/startup-perf/).
                //
                // The previous Dispatcher.UIThread.InvokeAsync(..., Loaded) yield
                // was a layout-rendezvous that incidentally paid for a slice of
                // shell-init on slow paths but did NOT actually gate on shell
                // readiness, so it left the race window open whenever the cache
                // hit fired before EnvironmentRequested. Replacing with the
                // explicit shell-ready signal owned by the WebView host closes
                // both the WebView2-environment race and the in-flight-render
                // reparent race in one rendezvous.
                //
                // _rendererReadiness is null in test contexts that construct the
                // VM directly without the desktop DI graph; in that case fall back
                // to a single dispatcher yield so behavior matches the previous
                // path closely enough for VM unit tests.
                StartupDiag.DiagMs("startup-pre-window", "perf-doc cache-hit-wait-renderer", $"path={cached.Path}");
                if (_rendererReadiness is not null)
                {
                    await _rendererReadiness.WaitReadyAsync().ConfigureAwait(true);
                }
                else
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => { },
                        DispatcherPriority.Background);
                }
                StartupDiag.DiagMs("startup-pre-window", "perf-doc cache-hit-renderer-ready", $"path={cached.Path}");
                ApplyOpenResult(new OpenDocumentResult.Success(cached), preserveEditModeAfterLoad);
                return;
            }

            StartupDiag.DiagMs("startup-pre-window", "perf-doc cache-miss", $"path={path}");
            var result = await _openDocument.ExecuteAsync(path).ConfigureAwait(true);
            ApplyOpenResult(result, preserveEditModeAfterLoad);
        }
        finally
        {
            UntrackOpeningPath(openingPath);
        }
    }

    private static string? NormalizeOpeningPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void TrackOpeningPath(string? path)
    {
        if (path is null)
        {
            return;
        }

        lock (_openingPathsGate)
        {
            _openingPathCounts.TryGetValue(path, out var count);
            _openingPathCounts[path] = count + 1;
        }
    }

    private void UntrackOpeningPath(string? path)
    {
        if (path is null)
        {
            return;
        }

        lock (_openingPathsGate)
        {
            if (!_openingPathCounts.TryGetValue(path, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _openingPathCounts.Remove(path);
                return;
            }

            _openingPathCounts[path] = count - 1;
        }
    }

    private void ApplyOpenResult(OpenDocumentResult result, bool preserveEditModeAfterLoad)
    {
        switch (result)
        {
            case OpenDocumentResult.Success success:
                ApplyLoadedDocument(success.Source, preserveEditModeAfterLoad);
                break;

            case OpenDocumentResult.NotFound:
            case OpenDocumentResult.AccessDenied:
            case OpenDocumentResult.ReadError:
            case OpenDocumentResult.UnsupportedType:
                FailOpenResult(result);
                break;
        }
    }

    /// <summary>
    /// Apply an already-loaded markdown source in place without going through
    /// the async file read pipeline. Use for tab-switch flows where the open-
    /// documents service already cached the source text and we want to refresh
    /// every downstream consumer (Document, RenderedDocument, viewer surface,
    /// edit session) without taking the user out of their current edit-mode
    /// state. Without this entry point the only public path was OpenPathAsync,
    /// which re-reads the file from disk AND forces IsEditMode=false (line
    /// 1335) — that flash dropped the user back into reader mode for one
    /// dispatcher tick before the caller could re-toggle.
    /// </summary>
    public void ApplyOpenedDocumentInPlace(MarkdownSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ApplyLoadedDocument(source, preserveEditModeAfterLoad: IsEditMode);
    }

    private void ApplyLoadedDocument(MarkdownSource source, bool preserveEditModeAfterLoad)
    {
        // PE r2 E1: publish State BEFORE Document so the sibling-mount bridge
        // sees a single Reconcile with viewerVis=true on the Document write,
        // not a cascade of viewerVis=false→true straddling the State flip.
        // The bridge's triple-gate (isViewer && !isEdit && document is not null)
        // makes the intermediate State=Viewing reconcile safe — Document is
        // still the previous value (null on cold load, or old doc on re-open)
        // so viewerVisible evaluates false. The next assignment then flips
        // Document and we land at viewerVis=true in one transition.
        //
        // Close-path symmetry (see CloseFileCore): Document is nulled BEFORE
        // State drops to NoDocument so the same gate hides the viewer cleanly
        // without a stale-document flash. Load-path symmetry mirrors that:
        // Document is set AFTER State rises to Viewing so the viewer becomes
        // visible only when the document is real.
        State = ViewState.Viewing;
        // Cover-first (atomic teardown): raise the transition cover BEFORE the
        // Document swap below, so the WebView document swap happens under the
        // cover instead of as a visible staged teardown. The TOC keeps its old
        // rows until UpdateDocumentHeadings replaces them with the new model.
        DocumentTransitionStarting?.Invoke(this, EventArgs.Empty);
        Document = source;
        RenderedDocument = _renderMarkdown.Execute(
            source.Content,
            baseDirectory: TryGetDirectory(source.Path));
        _currentPath = source.Path;
        ReadingProgress = 0;
        ClearLoadError();

        if (preserveEditModeAfterLoad)
        {
            if (EditorSession is null)
            {
                EditorSession = new EditorSessionViewModel(
                    source,
                    ReadingPreferences,
                    _renderMarkdown,
                    _imageSourceResolver,
                    _localization);
            }
            else
            {
                EditorSession.ApplyLoadedDocument(source);
            }

            IsEditMode = true;
        }
        else
        {
            IsEditMode = false;
            EditorSession = null;
        }

        if (!_documentModelReadyMarked)
        {
            _documentModelReadyMarked = true;
            _startupMetrics.Mark(StartupStage.DocumentModelReady);
        }

        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private void MarkSecondaryFeaturesReady()
    {
        if (_secondaryFeaturesMarked)
        {
            return;
        }

        _secondaryFeaturesMarked = true;
        _startupMetrics.Mark(StartupStage.SecondaryFeatures);
    }

    public void MarkReadableDocumentRendered()
    {
        if (_readableDocumentMarked || State != ViewState.Viewing || RenderedDocument.Blocks.Count == 0)
        {
            return;
        }

        _readableDocumentMarked = true;
        _startupMetrics.Mark(StartupStage.ReadableDocument);
    }

    private void ApplySavedDocument(MarkdownSource source)
    {
        Document = source;
        RenderedDocument = _renderMarkdown.Execute(
            source.Content,
            baseDirectory: TryGetDirectory(source.Path));
        _currentPath = source.Path;

        if (EditorSession is null)
        {
            EditorSession = new EditorSessionViewModel(
                source,
                ReadingPreferences,
                _renderMarkdown,
                _imageSourceResolver,
                _localization);
        }
        else
        {
            EditorSession.ApplySavedDocument(source);
        }

        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private void FailOpenResult(OpenDocumentResult result)
    {
        CloseOverlayCore();
        IsEditMode = false;
        EditorSession = null;
        SetLoadError(result);
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private async Task RunWithDirtyCheckAsync(PendingDirtyActionKind kind, Func<Task> action)
    {
        if (IsDirtyPromptOpen)
        {
            return;
        }

        if (!RequiresDirtyResolution)
        {
            await action().ConfigureAwait(true);
            return;
        }

        QueueDirtyAction(kind, action);
    }

    private bool RequiresDirtyResolution => IsEditMode && EditorSession?.IsDirty == true;

    private void QueueDirtyAction(PendingDirtyActionKind kind, Func<Task> action)
    {
        if (IsDirtyPromptOpen)
        {
            return;
        }

        _pendingDirtyAction = action;
        SetDirtyPrompt(kind);
    }

    private async Task ContinuePendingDirtyActionAsync()
    {
        var pendingAction = _pendingDirtyAction;
        ClearDirtyPrompt();
        if (pendingAction is null)
        {
            return;
        }

        await pendingAction().ConfigureAwait(true);
    }

    private void ClearDirtyPrompt()
    {
        ClearDirtyPromptState();
    }

    private async Task<SaveExecutionOutcome> SaveEditorAsync(bool promptForPathWhenMissing, bool forceSaveAs)
    {
        if (EditorSession is null)
        {
            return new SaveExecutionOutcome(false, new SaveDocumentResult.InvalidPath(string.Empty));
        }

        var targetPath = forceSaveAs ? null : EditorSession.CurrentPath;
        if (string.IsNullOrWhiteSpace(targetPath) && promptForPathWhenMissing)
        {
            targetPath = await PickSavePathAsync(EditorSession.FileName).ConfigureAwait(true);
        }
        else if (forceSaveAs)
        {
            targetPath = await PickSavePathAsync(EditorSession.FileName).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new SaveExecutionOutcome(true, null);
        }

        var result = await _saveDocument.ExecuteAsync(targetPath, EditorSession.SourceText).ConfigureAwait(true);
        return new SaveExecutionOutcome(false, result);
    }

    private async Task<string?> PickSavePathAsync(string? currentFileName)
        => await _filePicker
            .PickSaveMarkdownFileAsync(NormalizeSuggestedFileName(currentFileName))
            .ConfigureAwait(true);

    private void DiscardEditorChanges()
    {
        if (EditorSession is null)
        {
            return;
        }

        EditorSession.DiscardChanges();
        RefreshDocumentSummary();
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private void ApplyTheme(ThemeMode mode)
    {
        Theme = mode;
        _themeService.Apply(mode, ReadingPreferences.LightPalette);
        EffectiveTheme = _themeService.GetEffectiveTheme();
    }

    private void ApplyLightPalette(LightPaletteMode palette)
    {
        RaiseThemeTransitionStartingIfEffectiveThemeWillChange(Theme, palette);
        _selectedLightPalette = palette;
        ApplyReadingPreferences(ReadingPreferences with { LightPalette = palette });
        _themeService.Apply(Theme, palette);
        EffectiveTheme = _themeService.GetEffectiveTheme();
        OnPropertyChanged(nameof(IsOriginalPaletteSelected));
        OnPropertyChanged(nameof(IsWhitePaletteSelected));
    }

    private void RaiseThemeTransitionStartingIfEffectiveThemeWillChange(
        ThemeMode theme,
        LightPaletteMode lightPalette)
    {
        var target = ResolveEffectiveTheme(theme, lightPalette);
        if (target == ThemeMode.System || target == EffectiveTheme)
        {
            return;
        }

        ThemeTransitionStarting?.Invoke(this, new ThemeTransitionStartingEventArgs(target));
    }

    private static ThemeMode ResolveEffectiveTheme(ThemeMode theme, LightPaletteMode lightPalette)
        => theme switch
        {
            ThemeMode.Dark => ThemeMode.Dark,
            ThemeMode.Light => lightPalette == LightPaletteMode.White
                ? ThemeMode.ClassicWhite
                : ThemeMode.Light,
            ThemeMode.ClassicWhite => ThemeMode.ClassicWhite,
            _ => ThemeMode.System
        };

    private void ApplyReadingPreferences(ReadingPreferences preferences)
    {
        var normalized = ReadingPreferences.Normalize(preferences);
        if (normalized == ReadingPreferences)
        {
            return;
        }

        ReadingPreferences = normalized;
        PersistReadingPreferences(normalized);
    }

    private void PersistReadingPreferences(ReadingPreferences preferences)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _settings.SavePreferencesAsync(preferences).ConfigureAwait(false);
            }
            catch
            {
                // Persistence remains best-effort; failed saving of reading
                // preferences must never interrupt the viewer or editor loop.
            }
        });
    }

    private void OnEditorSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (EditorSession is null)
        {
            return;
        }

        if (e.PropertyName == nameof(EditorSessionViewModel.CurrentPath))
        {
            _currentPath = EditorSession.CurrentPath;
        }

        if (e.PropertyName is nameof(EditorSessionViewModel.SourceText)
            or nameof(EditorSessionViewModel.LastPersistedSource)
            or nameof(EditorSessionViewModel.FileName)
            or nameof(EditorSessionViewModel.CurrentPath))
        {
            RefreshDocumentSummary();
            RefreshWindowTitle();
            UpdateCommandStates();
        }
    }

    private void RefreshDocumentSummary()
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(TitleFileDisplayName));
        OnPropertyChanged(nameof(HasDocumentTitle));
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(ReadTimeMinutes));
        OnPropertyChanged(nameof(WordCountStatusLabel));
        OnPropertyChanged(nameof(ReadTimeStatusLabel));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ShowsDirtySaveButton));
    }

    private void RefreshWindowTitle()
    {
        if (State != ViewState.Viewing)
        {
            WindowTitle = "MarkMello";
            return;
        }

        WindowTitle = string.IsNullOrWhiteSpace(FileName)
            ? "MarkMello"
            : $"{TitleFileDisplayName} — MarkMello";
    }

    private void UpdateCommandStates()
    {
        ReloadCommand.NotifyCanExecuteChanged();
        CloseFileCommand.NotifyCanExecuteChanged();
        ToggleEditModeCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        OpenDownloadedUpdateCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadAvailableUpdate));
        OnPropertyChanged(nameof(CanOpenDownloadedUpdate));
        OnPropertyChanged(nameof(IsUpdateBusy));
        OnPropertyChanged(nameof(UpdateBusyIndicatorOpacity));
        OnPropertyChanged(nameof(CheckForUpdatesIdleLabelOpacity));
        OnPropertyChanged(nameof(CheckForUpdatesBusyLabelOpacity));
        OnPropertyChanged(nameof(DownloadUpdateIdleLabelOpacity));
        OnPropertyChanged(nameof(DownloadUpdateBusyLabelOpacity));
        OnPropertyChanged(nameof(DownloadUpdateActionOpacity));
        OnPropertyChanged(nameof(OpenDownloadedUpdateActionOpacity));
        OnPropertyChanged(nameof(CheckForUpdatesLabel));
        OnPropertyChanged(nameof(CheckForUpdatesIdleLabel));
        OnPropertyChanged(nameof(CheckForUpdatesBusyLabel));
        OnPropertyChanged(nameof(DownloadUpdateLabel));
        OnPropertyChanged(nameof(DownloadUpdateIdleLabel));
        OnPropertyChanged(nameof(DownloadUpdateBusyLabel));
        OnPropertyChanged(nameof(DownloadedUpdateActionLabel));
        OnPropertyChanged(nameof(UpdateStateBadge));
        OnPropertyChanged(nameof(AppMenuUpdateStateBadge));
    }

    private static string GetProductVersion()
    {
        var assembly = GetProductAssembly();

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var buildMetadataIndex = informationalVersion.IndexOf('+');
            return buildMetadataIndex >= 0
                ? informationalVersion[..buildMetadataIndex]
                : informationalVersion;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "1.0.0"
            : $"{version.Major}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
    }

    private static string GetRepositoryUrl()
    {
        var explicitUrl = GetAssemblyMetadata("MarkMelloRepositoryUrl");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        var owner = GetAssemblyMetadata("MarkMelloReleaseOwner");
        var repo = GetAssemblyMetadata("MarkMelloReleaseRepo");
        return string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)
            ? "https://github.com/applicate2628/MarkMello"
            : $"https://github.com/{owner}/{repo}";
    }

    private static string? GetAssemblyMetadata(string key)
        => GetProductAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals(key, StringComparison.Ordinal))
            ?.Value;

    private static Assembly GetProductAssembly()
        => Assembly.GetEntryAssembly() ?? typeof(MainWindowViewModel).Assembly;

    private void CloseOverlayCore()
    {
        ShellOverlay = ShellOverlayKind.None;
    }

    private void CloseAppOverlayCore()
    {
        if (ShellOverlay is
            ShellOverlayKind.AppMenu
            or ShellOverlayKind.AppSettings
            or ShellOverlayKind.AppAbout
            or ShellOverlayKind.AppUpdates)
        {
            ShellOverlay = ShellOverlayKind.None;
        }
    }

    private string? CurrentDocumentPath => EditorSession?.CurrentPath ?? _currentPath ?? Document?.Path;

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

    private static string? TryGetDirectory(string? path)
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

    private enum PendingDirtyActionKind
    {
        OpenFile,
        CreateNewDocument,
        CloseFile,
        Reload,
        LeaveEditMode,
        CloseWindow
    }

    private readonly record struct SaveExecutionOutcome(bool Cancelled, SaveDocumentResult? Result);
}
