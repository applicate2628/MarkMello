using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkMello.Application.Updates;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using System.ComponentModel;

namespace MarkMello.Presentation.ViewModels;

public partial class MainWindowViewModel
{
    private PendingDirtyActionKind? _dirtyPromptKind;
    private SaveDocumentResult? _dirtyPromptErrorResult;
    private OpenDocumentResult? _loadErrorResult;
    private UpdateStatusSnapshot _updateStatus = UpdateStatusSnapshot.Default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemLanguageSelected))]
    [NotifyPropertyChangedFor(nameof(IsEnglishLanguageSelected))]
    [NotifyPropertyChangedFor(nameof(IsRussianLanguageSelected))]
    private AppLanguage _language = AppLanguage.System;

    public bool IsSystemLanguageSelected => Language == AppLanguage.System;

    public bool IsEnglishLanguageSelected => Language == AppLanguage.English;

    public bool IsRussianLanguageSelected => Language == AppLanguage.Russian;

    public string WordCountStatusLabel => _localization.Format("StatusWordCount", WordCount);

    public string ReadTimeStatusLabel => _localization.Format("StatusReadTime", ReadTimeMinutes);

    [RelayCommand]
    private void SelectSystemLanguage() => ApplyLanguageSelection(AppLanguage.System);

    [RelayCommand]
    private void SelectEnglishLanguage() => ApplyLanguageSelection(AppLanguage.English);

    [RelayCommand]
    private void SelectRussianLanguage() => ApplyLanguageSelection(AppLanguage.Russian);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(_localization.SelectedLanguage)
            and not nameof(_localization.EffectiveLanguage)
            and not nameof(_localization.Culture)
            and not "Item[]")
        {
            return;
        }

        RefreshLocalizedProperties();
    }

    private void ApplyLanguageSelection(AppLanguage language, bool persist = true)
    {
        var normalized = NormalizeLanguage(language);
        if (Language == normalized && _localization.SelectedLanguage == normalized)
        {
            return;
        }

        Language = normalized;
        _localization.SetLanguage(normalized);
        UpdateDraftFileName();

        if (persist)
        {
            PersistLanguage(normalized);
        }
    }

    private void PersistLanguage(AppLanguage language)
    {
        try
        {
            _settings.SaveLanguageAsync(language).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Language persistence remains best-effort for the same reason
            // as the rest of the lightweight app settings.
        }
    }

    private void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(EditToggleLabel));
        OnPropertyChanged(nameof(EditShortcutLabel));
        OnPropertyChanged(nameof(NextThemeHint));
        OnPropertyChanged(nameof(CheckForUpdatesLabel));
        OnPropertyChanged(nameof(DownloadUpdateLabel));
        OnPropertyChanged(nameof(DownloadedUpdateActionLabel));
        OnPropertyChanged(nameof(UpdateStateBadge));
        OnPropertyChanged(nameof(IsSystemLanguageSelected));
        OnPropertyChanged(nameof(IsEnglishLanguageSelected));
        OnPropertyChanged(nameof(IsRussianLanguageSelected));
        OnPropertyChanged(nameof(WordCountStatusLabel));
        OnPropertyChanged(nameof(ReadTimeStatusLabel));
        OnPropertyChanged(nameof(FontSizeLabel));
        OnPropertyChanged(nameof(LineHeightLabel));

        RefreshDirtyPromptTexts();
        RefreshLoadErrorTexts();
        RefreshUpdateStatusTexts();
    }

    private void SetDirtyPrompt(PendingDirtyActionKind kind)
    {
        _dirtyPromptKind = kind;
        _dirtyPromptErrorResult = null;
        RefreshDirtyPromptTexts();
        IsDirtyPromptOpen = true;
    }

    private void SetDirtyPromptError(SaveDocumentResult? result)
    {
        _dirtyPromptErrorResult = result;
        RefreshDirtyPromptTexts();
    }

    private void RefreshDirtyPromptTexts()
    {
        DirtyPromptTitle = _dirtyPromptKind is null
            ? string.Empty
            : _localization["DirtyPromptTitle"];

        DirtyPromptMessage = _dirtyPromptKind switch
        {
            PendingDirtyActionKind.OpenFile => _localization["DirtyPromptOpenFile"],
            PendingDirtyActionKind.CreateNewDocument => _localization["DirtyPromptCreateNewDocument"],
            PendingDirtyActionKind.CloseFile => _localization["DirtyPromptCloseFile"],
            PendingDirtyActionKind.Reload => _localization["DirtyPromptReload"],
            PendingDirtyActionKind.LeaveEditMode => _localization["DirtyPromptLeaveEditMode"],
            PendingDirtyActionKind.CloseWindow => _localization["DirtyPromptCloseWindow"],
            _ => string.Empty
        };

        DirtyPromptErrorMessage = GetSaveFailureMessage(_dirtyPromptErrorResult);
    }

    private void ClearDirtyPromptState()
    {
        _pendingDirtyAction = null;
        _dirtyPromptKind = null;
        _dirtyPromptErrorResult = null;
        IsDirtyPromptOpen = false;
        DirtyPromptTitle = string.Empty;
        DirtyPromptMessage = string.Empty;
        DirtyPromptErrorMessage = string.Empty;
    }

    private void SetLoadError(OpenDocumentResult result)
    {
        _loadErrorResult = result;
        RefreshLoadErrorTexts();
        State = ViewState.LoadError;
    }

    private void RefreshLoadErrorTexts()
    {
        ErrorTitle = _loadErrorResult switch
        {
            OpenDocumentResult.NotFound => _localization["ErrorFileNotFoundTitle"],
            OpenDocumentResult.AccessDenied => _localization["ErrorAccessDeniedTitle"],
            OpenDocumentResult.ReadError => _localization["ErrorReadFailureTitle"],
            OpenDocumentResult.UnsupportedType => _localization["ErrorUnsupportedTypeTitle"],
            _ => string.Empty
        };

        ErrorDetails = _loadErrorResult switch
        {
            OpenDocumentResult.NotFound notFound => notFound.Path,
            OpenDocumentResult.AccessDenied denied => denied.Path,
            OpenDocumentResult.ReadError read => string.Concat(read.Path, Environment.NewLine, Environment.NewLine, read.Message),
            OpenDocumentResult.UnsupportedType unsupported => _localization.Format(
                "ErrorSupportedExtensions",
                unsupported.Path,
                Environment.NewLine,
                string.Join(", ", SupportedDocumentTypes.Extensions)),
            _ => string.Empty
        };
    }

    private void ClearLoadError()
    {
        _loadErrorResult = null;
        ErrorTitle = string.Empty;
        ErrorDetails = string.Empty;
    }

    private void SetUpdateStatus(UpdateStatusSnapshot status)
    {
        _updateStatus = status;
        RefreshUpdateStatusTexts();
    }

    private void RefreshUpdateStatusTexts()
    {
        UpdateStatusTitle = _updateStatus switch
        {
            UpdateStatusSnapshot.DefaultState => _localization["UpdateDefaultTitle"],
            UpdateStatusSnapshot.CheckingState => _localization["UpdateCheckingTitle"],
            UpdateStatusSnapshot.SourceNotConfiguredState => _localization["UpdateUnavailableTitle"],
            UpdateStatusSnapshot.UnsupportedPlatformState => _localization["UpdateUnsupportedPlatformTitle"],
            UpdateStatusSnapshot.UpToDateState => _localization["UpdateUpToDateTitle"],
            UpdateStatusSnapshot.UpdateAvailableState available => _localization.Format("UpdateAvailableTitle", available.Package.ReleaseVersion),
            UpdateStatusSnapshot.CheckFailedState => _localization["UpdateCheckFailedTitle"],
            UpdateStatusSnapshot.DownloadingState downloading => _localization.Format("UpdateDownloadTitle", downloading.Package.ReleaseVersion),
            UpdateStatusSnapshot.DownloadReadyState => _localization["UpdateReadyTitle"],
            UpdateStatusSnapshot.DownloadFailedState => _localization["UpdateDownloadFailedTitle"],
            UpdateStatusSnapshot.NativeFlowStartedState => _localization["UpdateNativeFlowStartedTitle"],
            UpdateStatusSnapshot.OpenDownloadedFailedState => _localization["UpdateOpenDownloadedFailedTitle"],
            _ => _localization["UpdateDefaultTitle"]
        };

        UpdateStatusMessage = _updateStatus switch
        {
            UpdateStatusSnapshot.DefaultState => _localization["UpdateDefaultMessage"],
            UpdateStatusSnapshot.CheckingState => _localization["UpdateCheckingMessage"],
            UpdateStatusSnapshot.SourceNotConfiguredState => _localization["UpdateUnavailableMessage"],
            UpdateStatusSnapshot.UnsupportedPlatformState unsupported => _localization.Format(
                "UpdateUnsupportedPlatformMessage",
                unsupported.PlatformName,
                unsupported.ArchitectureName),
            UpdateStatusSnapshot.UpToDateState upToDate => _localization.Format(
                "UpdateUpToDateMessage",
                upToDate.CurrentVersion,
                upToDate.LatestVersion),
            UpdateStatusSnapshot.UpdateAvailableState available => _localization.Format(
                "UpdateAvailableMessage",
                available.Package.AssetName,
                available.Package.PlatformName,
                available.Package.ArchitectureName),
            UpdateStatusSnapshot.CheckFailedState failed => failed.Details,
            UpdateStatusSnapshot.DownloadingState downloading => _localization.Format(
                "UpdateDownloadMessage",
                downloading.Package.AssetName),
            UpdateStatusSnapshot.DownloadReadyState ready => GetUpdateReadyMessage(ready.Package, ready.DownloadedFilePath),
            UpdateStatusSnapshot.DownloadFailedState failed => failed.Details,
            UpdateStatusSnapshot.NativeFlowStartedState started => GetNativeFlowStartedMessage(started.Package),
            UpdateStatusSnapshot.OpenDownloadedFailedState failed => failed.Details,
            _ => _localization["UpdateDefaultMessage"]
        };
    }

    private void UpdateDraftFileName()
    {
        if (EditorSession is null || !string.IsNullOrWhiteSpace(EditorSession.CurrentPath))
        {
            return;
        }

        EditorSession.UpdateDraftFileName(GetUntitledFileName());
        RefreshDocumentSummary();
        RefreshWindowTitle();
    }

    private string GetUntitledFileName() => _localization["UntitledFileName"];

    private string GetSaveFailureMessage(SaveDocumentResult? result)
        => result switch
        {
            SaveDocumentResult.InvalidPath invalidPath => _localization.Format("SaveInvalidPath", invalidPath.Path),
            SaveDocumentResult.AccessDenied accessDenied => _localization.Format("SaveAccessDenied", accessDenied.Path),
            SaveDocumentResult.WriteError writeError => _localization.Format("SaveWriteFailure", writeError.Message),
            null => string.Empty,
            _ => _localization["SaveGenericFailure"]
        };

    private string GetUpdateReadyMessage(AppUpdatePackage package, string downloadedFilePath)
    {
        var downloadedFileName = Path.GetFileName(downloadedFilePath);

        return package.InstallAction switch
        {
            AppUpdateInstallAction.LaunchInstaller => _localization.Format("UpdateReadyLaunchInstaller", downloadedFileName),
            AppUpdateInstallAction.OpenDiskImage => _localization.Format("UpdateReadyOpenDmg", downloadedFileName),
            AppUpdateInstallAction.RevealFile => _localization.Format("UpdateReadyRevealAppImage", downloadedFileName),
            _ => _localization.Format("UpdateReadyGeneric", downloadedFileName)
        };
    }

    private string GetNativeFlowStartedMessage(AppUpdatePackage package)
        => package.InstallAction switch
        {
            AppUpdateInstallAction.LaunchInstaller => _localization["UpdateNativeFlowStartedLaunchInstaller"],
            AppUpdateInstallAction.OpenDiskImage => _localization["UpdateNativeFlowStartedOpenDmg"],
            AppUpdateInstallAction.RevealFile => _localization["UpdateNativeFlowStartedRevealAppImage"],
            _ => _localization["UpdateOpenDownloaded"]
        };

    private string NormalizeSuggestedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return GetUntitledFileName();
        }

        return SupportedDocumentTypes.IsSupportedPath(fileName)
            ? fileName
            : $"{fileName}.md";
    }

    private static AppLanguage NormalizeLanguage(AppLanguage language)
        => language switch
        {
            AppLanguage.English => AppLanguage.English,
            AppLanguage.Russian => AppLanguage.Russian,
            _ => AppLanguage.System
        };

    private abstract record UpdateStatusSnapshot
    {
        public static readonly UpdateStatusSnapshot Default = new DefaultState();

        public sealed record DefaultState : UpdateStatusSnapshot;

        public sealed record CheckingState : UpdateStatusSnapshot;

        public sealed record SourceNotConfiguredState : UpdateStatusSnapshot;

        public sealed record UnsupportedPlatformState(string PlatformName, string ArchitectureName) : UpdateStatusSnapshot;

        public sealed record UpToDateState(string CurrentVersion, string LatestVersion) : UpdateStatusSnapshot;

        public sealed record UpdateAvailableState(AppUpdatePackage Package) : UpdateStatusSnapshot;

        public sealed record CheckFailedState(string Details) : UpdateStatusSnapshot;

        public sealed record DownloadingState(AppUpdatePackage Package) : UpdateStatusSnapshot;

        public sealed record DownloadReadyState(AppUpdatePackage Package, string DownloadedFilePath) : UpdateStatusSnapshot;

        public sealed record DownloadFailedState(string Details) : UpdateStatusSnapshot;

        public sealed record NativeFlowStartedState(AppUpdatePackage Package) : UpdateStatusSnapshot;

        public sealed record OpenDownloadedFailedState(string Details) : UpdateStatusSnapshot;
    }
}
