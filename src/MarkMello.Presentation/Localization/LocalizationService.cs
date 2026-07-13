using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MarkMello.Domain;

namespace MarkMello.Presentation.Localization;

public sealed class LocalizationService : ObservableObject, ILocalizationService
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["WelcomeTagline"] = "A quiet place to read Markdown.",
        ["WelcomeCreateMd"] = "Create MD",
        ["WelcomeOpenFile"] = "Open file...",
        ["WelcomeDropHint"] = "or drop a .md file anywhere",
        ["TitleBarMinimize"] = "Minimize",
        ["TitleBarMaximize"] = "Maximize",
        ["TitleBarRestore"] = "Restore",
        ["TitleBarClose"] = "Close",
        ["AppMenuTooltip"] = "App menu",
        ["ThemeSwitchToDark"] = "Switch to dark theme",
        ["ThemeSwitchToLight"] = "Switch to original theme",
        ["EditToggleTooltip"] = "Toggle edit mode (Ctrl+E)",
        ["SaveTooltip"] = "Save (Ctrl+S)",
        ["SaveAsTooltip"] = "Save as (Ctrl+Shift+S)",
        ["ReadingSettingsTooltip"] = "Reading preferences (Ctrl+,)",
        ["OverlayCloseMenu"] = "Close menu",
        ["OverlayBackToMenu"] = "Back to menu",
        ["OverlayCloseSettings"] = "Close settings",
        ["OverlayBackToSettings"] = "Back to settings",
        ["OverlayCloseAbout"] = "Close about",
        ["OverlayCloseUpdates"] = "Close updates",
        ["AppMenuHeader"] = "MENU",
        ["AppMenuOpenFileLabel"] = "Open file",
        ["AppMenuOpenFileHint"] = "Pick a Markdown document",
        ["AppMenuCloseFileLabel"] = "Close file",
        ["AppMenuCloseFileHint"] = "Return to the welcome screen",
        ["AppMenuSettingsLabel"] = "Settings",
        ["AppMenuSettingsHint"] = "Language and app preferences",
        ["AppMenuTocLabel"] = "Table of contents",
        ["AppMenuTocHint"] = "Show or hide the document outline",
        ["TocPanelHeader"] = "Contents",
        ["TocPanelEmpty"] = "No headings in this document.",
        ["FindBarTooltip"] = "Find in document (Ctrl+F)",
        ["MetaCurrent"] = "Current",
        ["MetaOpen"] = "Open",
        ["MetaReset"] = "Reset",
        ["MetaToggle"] = "Toggle",
        ["AppSettingsHeader"] = "SETTINGS",
        ["LanguageLabel"] = "Language",
        ["LanguageHint"] = "Shell and dialogs",
        ["LanguageSystem"] = "System",
        ["LanguageEnglish"] = "English",
        ["LanguageRussian"] = "Russian",
        ["AlwaysOnTopLabel"] = "Always on top",
        ["AlwaysOnTopHint"] = "Pin this window",
        ["AlwaysOnTopOn"] = "On",
        ["AlwaysOnTopOff"] = "Off",
        ["ResetSettingsLabel"] = "Reset settings",
        ["ResetSettingsHint"] = "Restore defaults",
        ["UpdatesHeader"] = "UPDATES",
        ["UpdatesLabel"] = "Updates",
        ["UpdatesHint"] = "GitHub release checks",
        ["UpdateNotificationDismiss"] = "Dismiss update notification",
        ["AboutLabel"] = "About",
        ["AboutHint"] = "Version and product info",
        ["AboutHeader"] = "ABOUT",
        ["AboutVersionLabel"] = "Version",
        ["AboutVersionHint"] = "Current product build",
        ["AboutLicenseLabel"] = "License",
        ["AboutLicenseHint"] = "Project license",
        ["AboutForkLabel"] = "Fork",
        ["AboutRepositoryLabel"] = "Repository",
        ["AboutNoticeLabel"] = "Notices",
        ["AboutNoticeHint"] = "Copyright and attribution details are kept in NOTICE.md.",
        ["AboutCreditsLabel"] = "Credits",
        ["AboutCreatedByPrefix"] = "Created by ",
        ["AboutCreditsPeriod"] = ".",
        ["ReadingHeader"] = "READING",
        ["ReadingPaletteLabel"] = "Palette",
        ["ReadingPaletteHint"] = "Light mode",
        ["ReadingPaletteOriginal"] = "Orig",
        ["ReadingPaletteWhite"] = "White",
        ["ReadingFontLabel"] = "Font",
        ["ReadingFontHint"] = "Document typeface",
        ["ReadingFontSerif"] = "Serif",
        ["ReadingFontSans"] = "Sans",
        ["ReadingFontMono"] = "Mono",
        ["ReadingSizeLabel"] = "Size",
        ["ReadingSizeHint"] = "Base font size",
        ["ReadingLineHeightLabel"] = "Line height",
        ["ReadingLineHeightHint"] = "Reading comfort",
        ["ReadingWidthLabel"] = "Width",
        ["ReadingWidthHint"] = "Measure of a line",
        ["ReadingWidthNarrow"] = "Narrow",
        ["ReadingWidthMedium"] = "Medium",
        ["ReadingWidthWide"] = "Wide",
        ["ReadingResizerLabel"] = "Resizer",
        ["ReadingResizerHint"] = "Width handle",
        ["ReadingResizerAlways"] = "Always",
        ["ReadingResizerOnHover"] = "On hover",
        ["ReadingMinimapLabel"] = "Minimap",
        ["ReadingMinimapHint"] = "Document overview",
        ["ReadingMinimapAuto"] = "Auto",
        ["ReadingMinimapOn"] = "On",
        ["ReadingMinimapOff"] = "Off",
        ["ReadingModeSmoothLabel"] = "Smooth",
        ["ReadingModeSmoothHint"] = "Mode switch",
        ["ReadingModeSmoothOn"] = "On",
        ["ReadingModeSmoothOff"] = "Off",
        ["ReadingModeSmoothDurationLabel"] = "Duration",
        ["ReadingModeSmoothDurationHint"] = "Milliseconds",
        ["ReadingRendererLabel"] = "Renderer",
        ["ReadingRendererHint"] = "Markdown surface",
        ["ReadingRendererNative"] = "Native",
        ["ReadingRendererWebView"] = "WebView",
        ["StatusWordCount"] = "Words: {0:N0}",
        ["StatusReadTime"] = "Read time: {0} min",
        ["StatusOpen"] = "open",
        ["StatusPrefs"] = "prefs",
        ["DragDropHint"] = "Drop your Markdown file to open",
        ["DirtyPromptCancel"] = "Cancel",
        ["DirtyPromptDiscard"] = "Discard",
        ["DirtyPromptSave"] = "Save",
        ["LoadErrorOpenAnotherFile"] = "Open another file",
        ["LoadErrorTryAgain"] = "Try again",
        ["LoadErrorPress"] = "Press ",
        ["LoadErrorToDismiss"] = " to dismiss",
        ["MmRendererFailureRetry"] = "Retry",
        ["MmRendererFailureCopyDiagnostics"] = "Copy diagnostics",
        ["MmRendererFailureTitle"] = "Could not display the document",
        ["MmRendererFailureTitleRuntime"] = "WebView2 Runtime is unavailable",
        ["MmRendererFailureTitleStaleNavigation"] = "Loading canceled",
        ["MmRendererFailureDetailRuntime"] = "Microsoft Edge WebView2 Runtime is required to display the document. Install it and restart the application.",
        ["MmRendererFailureDetailStaleNavigation"] = "Opening the document was interrupted by a newer navigation.",
        ["MmRendererFailureDetailRender"] = "An error occurred while preparing the preview. Try again or copy the diagnostics for a report.",
        ["EditorBoldTooltip"] = "Bold",
        ["EditorItalicTooltip"] = "Italic",
        ["EditorCodeTooltip"] = "Code",
        ["EditorLinkTooltip"] = "Link",
        ["EditorListTooltip"] = "List",
        ["EditorQuoteTooltip"] = "Quote",
        ["EditorSourceLabel"] = "SOURCE",
        ["ModeReading"] = "Reading",
        ["ModeEdit"] = "Edit",
        ["ModeReadShortcut"] = "read",
        ["ModeEditShortcut"] = "edit",
        ["UpdateCheckNow"] = "Check now",
        ["UpdateChecking"] = "Checking...",
        ["UpdateDownload"] = "Download update",
        ["UpdateDownloading"] = "Downloading...",
        ["UpdateOpenDownloaded"] = "Open update",
        ["UpdateLaunchInstaller"] = "Launch installer",
        ["UpdateOpenDmg"] = "Open DMG",
        ["UpdateRevealAppImage"] = "Reveal AppImage",
        ["UpdateBadgeManual"] = "Manual",
        ["UpdateBadgeAvailable"] = "Available",
        ["UpdateBadgeReady"] = "Ready",
        ["UpdateBadgeChecking"] = "Checking",
        ["UpdateBadgeDownloading"] = "Downloading",
        ["UpdateDefaultTitle"] = "Updates",
        ["UpdateDefaultMessage"] = "MarkMello checks GitHub Releases quietly after startup.",
        ["UpdateCheckingTitle"] = "Checking GitHub Releases",
        ["UpdateCheckingMessage"] = "Looking for a newer packaged build for this device.",
        ["UpdateUnavailableTitle"] = "Updates unavailable",
        ["UpdateUnavailableMessage"] = "This build has no GitHub Releases source configured yet.",
        ["UpdateUnsupportedPlatformTitle"] = "No packaged update for this runtime",
        ["UpdateUnsupportedPlatformMessage"] = "{0} {1} is not in the current release matrix.",
        ["UpdateUpToDateTitle"] = "You're up to date",
        ["UpdateUpToDateMessage"] = "Current build {0} already matches the latest published release ({1}).",
        ["UpdateAvailableTitle"] = "Update {0} available",
        ["HeaderUpdateAvailable"] = "Update available!",
        ["TabCloseToLeft"] = "Close to the Left",
        ["TabCloseToRight"] = "Close to the Right",
        ["TabLoadFailed"] = "Could not load \"{0}\" — keeping your current document.",
        ["HeaderUpdateNoticeTooltip"] = "Open updates",
        ["UpdateAvailableMessage"] = "{0} is ready for {1} {2}.",
        ["UpdateCheckFailedTitle"] = "Couldn't check for updates",
        ["UpdateDownloadTitle"] = "Downloading {0}",
        ["UpdateDownloadMessage"] = "Saving {0} from GitHub Releases.",
        ["UpdateReadyTitle"] = "Update ready",
        ["UpdateReadyLaunchInstaller"] = "{0} downloaded. Launch the installer to continue the native Windows upgrade flow.",
        ["UpdateReadyOpenDmg"] = "{0} downloaded. Open the DMG to continue with the native macOS install flow.",
        ["UpdateReadyRevealAppImage"] = "{0} downloaded. Reveal the AppImage, then replace your previous binary when you're ready.",
        ["UpdateReadyGeneric"] = "{0} downloaded.",
        ["UpdateDownloadFailedTitle"] = "Download failed",
        ["UpdateNativeFlowStartedTitle"] = "Native update flow started",
        ["UpdateNativeFlowStartedLaunchInstaller"] = "Installer launched. Follow the native upgrade flow.",
        ["UpdateNativeFlowStartedOpenDmg"] = "DMG opened. Continue with the native macOS install flow.",
        ["UpdateNativeFlowStartedRevealAppImage"] = "The AppImage was revealed in your file manager.",
        ["UpdateOpenDownloadedFailedTitle"] = "Couldn't open the downloaded update",
        ["DocumentHealthBanner"] = "{0} broken formula(s) detected",
        ["DocumentHealthApply"] = "Fix & save",
        ["DocumentHealthDismiss"] = "Dismiss",
        ["ErrorFileNotFoundTitle"] = "Couldn't find that file",
        ["ErrorAccessDeniedTitle"] = "Access denied",
        ["ErrorReadFailureTitle"] = "Couldn't read the file",
        ["ErrorUnsupportedTypeTitle"] = "Unsupported file type",
        ["ErrorSupportedExtensions"] = "{0}{1}{1}Supported extensions: {2}",
        ["DirtyPromptTitle"] = "Unsaved changes",
        ["DirtyPromptOpenFile"] = "Save your changes before opening another document?",
        ["DirtyPromptCreateNewDocument"] = "Save your changes before creating a new document?",
        ["DirtyPromptCloseFile"] = "Save your changes before closing the current document?",
        ["DirtyPromptReload"] = "Save your changes before reloading the current document?",
        ["DirtyPromptLeaveEditMode"] = "Save your changes before returning to reading mode?",
        ["DirtyPromptCloseWindow"] = "Save your changes before closing MarkMello?",
        ["DirtyPromptContinue"] = "Save your changes before continuing?",
        ["SaveInvalidPath"] = "Couldn't save to this path: {0}",
        ["SaveAccessDenied"] = "Access denied: {0}",
        ["SaveWriteFailure"] = "Couldn't save the document: {0}",
        ["SaveGenericFailure"] = "Couldn't save the document.",
        ["OpenDialogTitle"] = "Open Markdown file",
        ["SaveDialogTitle"] = "Save Markdown file",
        ["MarkdownDocuments"] = "Markdown documents",
        ["UntitledFileName"] = "Untitled.md"
    };

    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        ["WelcomeTagline"] = "Тихое место для чтения Markdown.",
        ["WelcomeCreateMd"] = "Создать MD",
        ["WelcomeOpenFile"] = "Открыть файл...",
        ["WelcomeDropHint"] = "или перетащите сюда .md файл",
        ["TitleBarMinimize"] = "Свернуть",
        ["TitleBarMaximize"] = "Развернуть",
        ["TitleBarRestore"] = "Восстановить",
        ["TitleBarClose"] = "Закрыть",
        ["AppMenuTooltip"] = "Меню приложения",
        ["ThemeSwitchToDark"] = "Переключить на тёмную тему",
        ["ThemeSwitchToLight"] = "Переключить на Original",
        ["EditToggleTooltip"] = "Переключить режим редактирования (Ctrl+E)",
        ["SaveTooltip"] = "Сохранить (Ctrl+S)",
        ["SaveAsTooltip"] = "Сохранить как (Ctrl+Shift+S)",
        ["ReadingSettingsTooltip"] = "Параметры чтения (Ctrl+,)",
        ["OverlayCloseMenu"] = "Закрыть меню",
        ["OverlayBackToMenu"] = "Назад в меню",
        ["OverlayCloseSettings"] = "Закрыть настройки",
        ["OverlayBackToSettings"] = "Назад к настройкам",
        ["OverlayCloseAbout"] = "Закрыть раздел «О приложении»",
        ["OverlayCloseUpdates"] = "Закрыть обновления",
        ["AppMenuHeader"] = "МЕНЮ",
        ["AppMenuOpenFileLabel"] = "Открыть файл",
        ["AppMenuOpenFileHint"] = "Выбрать Markdown-документ",
        ["AppMenuCloseFileLabel"] = "Закрыть файл",
        ["AppMenuCloseFileHint"] = "Вернуться на экран приветствия",
        ["AppMenuSettingsLabel"] = "Настройки",
        ["AppMenuSettingsHint"] = "Язык и параметры приложения",
        ["AppMenuTocLabel"] = "Оглавление",
        ["AppMenuTocHint"] = "Показать или скрыть оглавление документа",
        ["TocPanelHeader"] = "Оглавление",
        ["TocPanelEmpty"] = "В документе нет заголовков.",
        ["FindBarTooltip"] = "Поиск по документу (Ctrl+F)",
        ["MetaCurrent"] = "Текущий",
        ["MetaOpen"] = "Открыть",
        ["MetaReset"] = "Сброс",
        ["MetaToggle"] = "Вкл/Выкл",
        ["AppSettingsHeader"] = "НАСТРОЙКИ",
        ["LanguageLabel"] = "Язык",
        ["LanguageHint"] = "Оболочка и диалоги",
        ["LanguageSystem"] = "Системный",
        ["LanguageEnglish"] = "Английский",
        ["LanguageRussian"] = "Русский",
        ["AlwaysOnTopLabel"] = "Поверх окон",
        ["AlwaysOnTopHint"] = "Закрепить окно сверху",
        ["AlwaysOnTopOn"] = "Вкл",
        ["AlwaysOnTopOff"] = "Выкл",
        ["ResetSettingsLabel"] = "Сбросить настройки",
        ["ResetSettingsHint"] = "Вернуть значения по умолчанию",
        ["UpdatesHeader"] = "ОБНОВЛЕНИЯ",
        ["UpdatesLabel"] = "Обновления",
        ["UpdatesHint"] = "Проверка GitHub Releases",
        ["UpdateNotificationDismiss"] = "Закрыть уведомление об обновлении",
        ["AboutLabel"] = "О приложении",
        ["AboutHint"] = "Версия и сведения о продукте",
        ["AboutHeader"] = "О ПРИЛОЖЕНИИ",
        ["AboutVersionLabel"] = "Версия",
        ["AboutVersionHint"] = "Текущая сборка продукта",
        ["AboutLicenseLabel"] = "Лицензия",
        ["AboutLicenseHint"] = "Лицензия проекта",
        ["AboutForkLabel"] = "Форк",
        ["AboutRepositoryLabel"] = "Репозиторий",
        ["AboutNoticeLabel"] = "Уведомления",
        ["AboutNoticeHint"] = "Сведения об авторских правах и атрибуции находятся в NOTICE.md.",
        ["AboutCreditsLabel"] = "Авторы",
        ["AboutCreatedByPrefix"] = "Создано ",
        ["AboutCreditsPeriod"] = ".",
        ["ReadingHeader"] = "ЧТЕНИЕ",
        ["ReadingPaletteLabel"] = "Палитра",
        ["ReadingPaletteHint"] = "Светлая тема",
        ["ReadingPaletteOriginal"] = "Orig",
        ["ReadingPaletteWhite"] = "White",
        ["ReadingFontLabel"] = "Шрифт",
        ["ReadingFontHint"] = "Гарнитура документа",
        ["ReadingFontSerif"] = "Сериф",
        ["ReadingFontSans"] = "Гротеск",
        ["ReadingFontMono"] = "Моно",
        ["ReadingSizeLabel"] = "Размер",
        ["ReadingSizeHint"] = "Базовый размер шрифта",
        ["ReadingLineHeightLabel"] = "Высота строки",
        ["ReadingLineHeightHint"] = "Комфорт чтения",
        ["ReadingWidthLabel"] = "Ширина",
        ["ReadingWidthHint"] = "Длина строки",
        ["ReadingWidthNarrow"] = "Узкая",
        ["ReadingWidthMedium"] = "Средняя",
        ["ReadingWidthWide"] = "Широкая",
        ["ReadingResizerLabel"] = "Ресайзер",
        ["ReadingResizerHint"] = "Ручка ширины",
        ["ReadingResizerAlways"] = "Всегда",
        ["ReadingResizerOnHover"] = "При наведении",
        ["ReadingMinimapLabel"] = "Миникарта",
        ["ReadingMinimapHint"] = "Обзор документа",
        ["ReadingMinimapAuto"] = "Авто",
        ["ReadingMinimapOn"] = "Вкл",
        ["ReadingMinimapOff"] = "Выкл",
        ["ReadingModeSmoothLabel"] = "Плавность",
        ["ReadingModeSmoothHint"] = "Смена режима",
        ["ReadingModeSmoothOn"] = "Вкл",
        ["ReadingModeSmoothOff"] = "Выкл",
        ["ReadingModeSmoothDurationLabel"] = "Длительность",
        ["ReadingModeSmoothDurationHint"] = "Миллисекунды",
        ["ReadingRendererLabel"] = "Рендерер",
        ["ReadingRendererHint"] = "Поверхность Markdown",
        ["ReadingRendererNative"] = "Native",
        ["ReadingRendererWebView"] = "WebView",
        ["StatusWordCount"] = "Слов: {0:N0}",
        ["StatusReadTime"] = "Чтение: {0} мин",
        ["StatusOpen"] = "открыть",
        ["StatusPrefs"] = "настройки",
        ["DragDropHint"] = "Перетащите Markdown-файл, чтобы открыть его",
        ["DirtyPromptCancel"] = "Отмена",
        ["DirtyPromptDiscard"] = "Не сохранять",
        ["DirtyPromptSave"] = "Сохранить",
        ["LoadErrorOpenAnotherFile"] = "Открыть другой файл",
        ["LoadErrorTryAgain"] = "Повторить",
        ["LoadErrorPress"] = "Нажмите ",
        ["LoadErrorToDismiss"] = " чтобы закрыть",
        ["MmRendererFailureRetry"] = "Повторить",
        ["MmRendererFailureCopyDiagnostics"] = "Скопировать диагностику",
        ["MmRendererFailureTitle"] = "Не удалось отобразить документ",
        ["MmRendererFailureTitleRuntime"] = "Среда WebView2 недоступна",
        ["MmRendererFailureTitleStaleNavigation"] = "Загрузка отменена",
        ["MmRendererFailureDetailRuntime"] = "Для отображения документа требуется Microsoft Edge WebView2 Runtime. Установите его и перезапустите приложение.",
        ["MmRendererFailureDetailStaleNavigation"] = "Открытие документа было прервано более новой навигацией.",
        ["MmRendererFailureDetailRender"] = "Произошла ошибка при подготовке предпросмотра. Попробуйте повторить или скопируйте диагностику для отчёта.",
        ["EditorBoldTooltip"] = "Жирный",
        ["EditorItalicTooltip"] = "Курсив",
        ["EditorCodeTooltip"] = "Код",
        ["EditorLinkTooltip"] = "Ссылка",
        ["EditorListTooltip"] = "Список",
        ["EditorQuoteTooltip"] = "Цитата",
        ["EditorSourceLabel"] = "ИСХОДНИК",
        ["ModeReading"] = "Чтение",
        ["ModeEdit"] = "Редактирование",
        ["ModeReadShortcut"] = "читать",
        ["ModeEditShortcut"] = "править",
        ["UpdateCheckNow"] = "Проверить",
        ["UpdateChecking"] = "Проверка...",
        ["UpdateDownload"] = "Скачать обновление",
        ["UpdateDownloading"] = "Загрузка...",
        ["UpdateOpenDownloaded"] = "Открыть обновление",
        ["UpdateLaunchInstaller"] = "Запустить установщик",
        ["UpdateOpenDmg"] = "Открыть DMG",
        ["UpdateRevealAppImage"] = "Показать AppImage",
        ["UpdateBadgeManual"] = "Вручную",
        ["UpdateBadgeAvailable"] = "Доступно",
        ["UpdateBadgeReady"] = "Готово",
        ["UpdateBadgeChecking"] = "Проверка",
        ["UpdateBadgeDownloading"] = "Загрузка",
        ["UpdateDefaultTitle"] = "Обновления",
        ["UpdateDefaultMessage"] = "MarkMello тихо проверяет GitHub Releases после запуска.",
        ["UpdateCheckingTitle"] = "Проверка GitHub Releases",
        ["UpdateCheckingMessage"] = "Ищем более новую сборку для этого устройства.",
        ["UpdateUnavailableTitle"] = "Обновления недоступны",
        ["UpdateUnavailableMessage"] = "Для этой сборки пока не настроен источник GitHub Releases.",
        ["UpdateUnsupportedPlatformTitle"] = "Для этой среды нет пакетного обновления",
        ["UpdateUnsupportedPlatformMessage"] = "{0} {1} отсутствует в текущей матрице релизов.",
        ["UpdateUpToDateTitle"] = "У вас актуальная версия",
        ["UpdateUpToDateMessage"] = "Текущая сборка {0} уже совпадает с последним опубликованным релизом ({1}).",
        ["UpdateAvailableTitle"] = "Доступно обновление {0}",
        ["HeaderUpdateAvailable"] = "Доступно обновление!",
        ["TabCloseToLeft"] = "Закрыть слева",
        ["TabCloseToRight"] = "Закрыть справа",
        ["TabLoadFailed"] = "Не удалось загрузить \"{0}\" — текущий документ сохранён.",
        ["HeaderUpdateNoticeTooltip"] = "Открыть обновления",
        ["UpdateAvailableMessage"] = "{0} готов для {1} {2}.",
        ["UpdateCheckFailedTitle"] = "Не удалось проверить обновления",
        ["UpdateDownloadTitle"] = "Загрузка {0}",
        ["UpdateDownloadMessage"] = "Сохраняем {0} из GitHub Releases.",
        ["UpdateReadyTitle"] = "Обновление готово",
        ["UpdateReadyLaunchInstaller"] = "{0} загружен. Запустите установщик, чтобы продолжить нативное обновление Windows.",
        ["UpdateReadyOpenDmg"] = "{0} загружен. Откройте DMG, чтобы продолжить нативную установку на macOS.",
        ["UpdateReadyRevealAppImage"] = "{0} загружен. Покажите AppImage и замените предыдущий бинарник, когда будете готовы.",
        ["UpdateReadyGeneric"] = "{0} загружен.",
        ["UpdateDownloadFailedTitle"] = "Ошибка загрузки",
        ["UpdateNativeFlowStartedTitle"] = "Запущен нативный сценарий обновления",
        ["UpdateNativeFlowStartedLaunchInstaller"] = "Установщик запущен. Продолжайте обновление через нативный сценарий.",
        ["UpdateNativeFlowStartedOpenDmg"] = "DMG открыт. Продолжайте установку через нативный сценарий macOS.",
        ["UpdateNativeFlowStartedRevealAppImage"] = "AppImage показан в файловом менеджере.",
        ["UpdateOpenDownloadedFailedTitle"] = "Не удалось открыть загруженное обновление",
        ["DocumentHealthBanner"] = "Найдено битых формул: {0}",
        ["DocumentHealthApply"] = "Починить и сохранить",
        ["DocumentHealthDismiss"] = "Скрыть",
        ["ErrorFileNotFoundTitle"] = "Не удалось найти файл",
        ["ErrorAccessDeniedTitle"] = "Доступ запрещён",
        ["ErrorReadFailureTitle"] = "Не удалось прочитать файл",
        ["ErrorUnsupportedTypeTitle"] = "Неподдерживаемый тип файла",
        ["ErrorSupportedExtensions"] = "{0}{1}{1}Поддерживаемые расширения: {2}",
        ["DirtyPromptTitle"] = "Есть несохранённые изменения",
        ["DirtyPromptOpenFile"] = "Сохранить изменения перед открытием другого документа?",
        ["DirtyPromptCreateNewDocument"] = "Сохранить изменения перед созданием нового документа?",
        ["DirtyPromptCloseFile"] = "Сохранить изменения перед закрытием текущего документа?",
        ["DirtyPromptReload"] = "Сохранить изменения перед перезагрузкой текущего документа?",
        ["DirtyPromptLeaveEditMode"] = "Сохранить изменения перед возвратом в режим чтения?",
        ["DirtyPromptCloseWindow"] = "Сохранить изменения перед закрытием MarkMello?",
        ["DirtyPromptContinue"] = "Сохранить изменения перед продолжением?",
        ["SaveInvalidPath"] = "Не удалось сохранить по этому пути: {0}",
        ["SaveAccessDenied"] = "Доступ запрещён: {0}",
        ["SaveWriteFailure"] = "Не удалось сохранить документ: {0}",
        ["SaveGenericFailure"] = "Не удалось сохранить документ.",
        ["OpenDialogTitle"] = "Открыть Markdown-файл",
        ["SaveDialogTitle"] = "Сохранить Markdown-файл",
        ["MarkdownDocuments"] = "Markdown-документы",
        ["UntitledFileName"] = "Безымянный.md"
    };

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");

    private AppLanguage _selectedLanguage;
    private AppLanguage _effectiveLanguage;
    private CultureInfo _culture = EnglishCulture;

    public LocalizationService()
        : this(AppLanguage.System)
    {
    }

    public LocalizationService(AppLanguage initialLanguage)
    {
        SetLanguage(initialLanguage);
    }

    public AppLanguage SelectedLanguage => _selectedLanguage;

    public AppLanguage EffectiveLanguage => _effectiveLanguage;

    public CultureInfo Culture => _culture;

    public string this[string key] => ResolveString(key);

    public string Format(string key, params object?[] args)
        => string.Format(_culture, ResolveString(key), args);

    public void SetLanguage(AppLanguage language)
    {
        var normalized = NormalizeLanguage(language);
        var effective = ResolveEffectiveLanguage(normalized);
        var culture = ResolveCulture(effective);

        var selectedChanged = _selectedLanguage != normalized;
        var effectiveChanged = _effectiveLanguage != effective;
        var cultureChanged = !_culture.Equals(culture);
        if (!selectedChanged && !effectiveChanged && !cultureChanged)
        {
            return;
        }

        _selectedLanguage = normalized;
        _effectiveLanguage = effective;
        _culture = culture;

        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(EffectiveLanguage));
        OnPropertyChanged(nameof(Culture));
        NotifyLocalizedTextChanged();
    }

    private void NotifyLocalizedTextChanged()
    {
        // Avalonia indexer bindings may subscribe to either the CLR indexer
        // property name (Item) or the common WPF-style indexer marker (Item[]).
        // Raising both keeps every active shell/view binding refreshed when the
        // language changes. The empty name is the standard full-refresh signal.
        OnPropertyChanged("Item");
        OnPropertyChanged("Item[]");
        OnPropertyChanged(string.Empty);
    }

    private string ResolveString(string key)
    {
        var primary = _effectiveLanguage == AppLanguage.Russian ? Russian : English;
        if (primary.TryGetValue(key, out var value))
        {
            return value;
        }

        if (English.TryGetValue(key, out value))
        {
            return value;
        }

        return $"[[{key}]]";
    }

    private static AppLanguage NormalizeLanguage(AppLanguage language)
        => language switch
        {
            AppLanguage.English => AppLanguage.English,
            AppLanguage.Russian => AppLanguage.Russian,
            _ => AppLanguage.System
        };

    private static AppLanguage ResolveEffectiveLanguage(AppLanguage selectedLanguage)
    {
        if (selectedLanguage is AppLanguage.English or AppLanguage.Russian)
        {
            return selectedLanguage;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Russian
            : AppLanguage.English;
    }

    private static CultureInfo ResolveCulture(AppLanguage language)
        => language == AppLanguage.Russian ? RussianCulture : EnglishCulture;
}
