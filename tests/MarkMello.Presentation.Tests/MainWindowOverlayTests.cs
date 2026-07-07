using Avalonia.Controls;
using MarkMello.Presentation.Views;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowOverlayTests
{
    [Fact]
    public void OverlayPopupInteractionSourceIncludesComboBoxItem()
    {
        Assert.True(MainWindow.IsOverlayPopupInteractionSource(new ComboBoxItem()));
    }

    [Fact]
    public void OverlayPopupInteractionSourceIgnoresRegularControls()
    {
        Assert.False(MainWindow.IsOverlayPopupInteractionSource(new Button()));
    }

    [Theory]
    [InlineData("AppMenuPanel")]
    [InlineData("SettingsPanel")]
    public void MainOverlaysUseNonTopmostPlatformPopupWindows(string panelName)
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        var popupStart = xaml.IndexOf($"<Popup Name=\"{panelName}\"", StringComparison.Ordinal);
        Assert.True(popupStart >= 0, $"{panelName} should be a Popup so it can render above NativeWebView.");

        var popupEnd = xaml.IndexOf('>', popupStart);
        Assert.True(popupEnd > popupStart, $"{panelName} popup declaration should be complete.");

        var declaration = xaml[popupStart..popupEnd];
        Assert.Contains("Topmost=\"False\"", declaration, StringComparison.Ordinal);
        Assert.Contains("ShouldUseOverlayLayer=\"False\"", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowClosesOverlayOnWindowDeactivation()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml.cs"));

        Assert.Contains("Deactivated += OnWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Deactivated -= OnWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void OnWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.CloseOverlayCommand.Execute(null)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TopChromeHoverIsScopedToTitleBarHotZone()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("PointerEntered=\"OnTopChromePointerEntered\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PointerExited=\"OnTopChromePointerExited\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Window.mm-top-chrome-hover Border.mm-top-chrome", xaml, StringComparison.Ordinal);
        Assert.Contains("Window.mm-top-chrome-hover StackPanel.mm-titlebar-actions", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Window:pointerover Border.mm-top-chrome", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Window:pointerover StackPanel.mm-titlebar-actions", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TopChromeHoverClassIsUpdatedByCodeBehind()
    {
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml.cs"));

        Assert.Contains("private bool _isTopChromeHovering", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void OnTopChromePointerEntered", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void OnTopChromePointerExited", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"mm-top-chrome-hover\", _isTopChromeHovering)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void EditModeTopbarExposesSaveCommands()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("Command=\"{Binding SaveCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SaveAsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowsDirtySaveButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding SaveTooltip}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip.Tip=\"{Binding SaveAsTooltip}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSurfaceUsesAppMenuEntryAndDedicatedPanel()
    {
        var appMenu = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "AppMenuPanelView.axaml"));
        var appSettings = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "AppSettingsPanelView.axaml"));
        var readingSettings = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "ReadingSettingsPanelView.axaml"));
        var controlsTheme = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Themes",
            "Controls.axaml"));
        var appUpdates = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "AppUpdatesPanelView.axaml"));

        Assert.Contains("Text=\"{Binding UpdatesLabel}\"", appMenu, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenAppUpdatesCommand}\"", appMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("<Expander", appMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding CheckForUpdatesCommand}\"", appMenu, StringComparison.Ordinal);
        var appSettingsIndex = appMenu.IndexOf("Command=\"{Binding OpenAppSettingsCommand}\"", StringComparison.Ordinal);
        var appUpdatesIndex = appMenu.IndexOf("Command=\"{Binding OpenAppUpdatesCommand}\"", StringComparison.Ordinal);
        Assert.True(appSettingsIndex >= 0, "Settings should stay in the app menu.");
        Assert.True(appUpdatesIndex > appSettingsIndex, "Updates should be the last app menu item after Settings.");
        Assert.Contains("Text=\"{Binding ReadingPaletteLabel}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsOriginalPaletteSelected}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsWhitePaletteSelected}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ReadingModeSmoothLabel}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsModeSwitchSmoothEnabled}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsModeSwitchSmoothDisabled}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ReadingModeSmoothOff}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"1\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"3\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding ModeSwitchSmoothDurationSetting, Mode=TwoWay}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ModeSwitchSmoothDurationLabel}\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Slider.mm-settings-slider:disabled", controlsTheme, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Opacity\" Value=\"0.45\" />", controlsTheme, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CheckForUpdatesCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DownloadUpdateCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenDownloadedUpdateCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UpdatesHeader}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AppMenuUpdateStateBadge}\"", appMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding UpdateStateBadge}\"", appMenu, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"216\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("RowDefinitions=\"Auto,Auto,Auto\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"Auto,156\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpdateStatusContent\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding DownloadProgressPercent}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding IsUpdateProgressIndeterminate}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"{Binding UpdateBusyIndicatorOpacity}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding CheckForUpdatesLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding DownloadUpdateLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"1\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"True\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("RenderTransform=\"translate(0px,0px) scale(1)\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Transitions=\"{x:Null}\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckForUpdatesIdleLabelOpacity", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckForUpdatesBusyLabelOpacity", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadUpdateIdleLabelOpacity", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadUpdateBusyLabelOpacity", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("TransformOperationsTransition Property=\"RenderTransform\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"300\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("RowDefinitions=\"*,Auto,104\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ThemeOptions}\"", appSettings, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AlwaysOnTopLabel}\"", appSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsAlwaysOnTop}\"", appSettings, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsAlwaysOnTopDisabled}\"", appSettings, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ResetSettingsLabel}\"", appSettings, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ResetSettingsCommand}\"", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding UpdatesLabel}\"", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding CheckForUpdatesCommand}\"", appSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowUsesSharedAppOverlayPopupForUpdatesPanel()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("<Popup Name=\"AppMenuPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsAppOverlayOpen}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup Name=\"AppUpdatesPanel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup Name=\"AppSettingsPanel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup Name=\"AppAboutPanel\"", xaml, StringComparison.Ordinal);

        // Perf F1 (2026-06-04): app overlay child views are hydrated lazily from
        // code-behind (kept out of InitializeComponent so cold start stays lean).
        // Updates still has a dedicated view, but it is swapped inside the shared
        // AppMenuPanel popup host so menu -> updates does not close one Popup and
        // open another.
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml.cs"));

        Assert.Contains("SyncAppOverlayPopupContent", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "ShellOverlayKind.AppUpdates => _appUpdatesPanelView ??= new AppUpdatesPanelView()",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingSettingsUsesRadioSegmentsForExclusiveChoices()
    {
        var readingSettings = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "ReadingSettingsPanelView.axaml"));
        var controlsTheme = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Themes",
            "Controls.axaml"));

        Assert.DoesNotContain("<ToggleButton Classes=\"mm-segmented-item\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("<RadioButton Classes=\"mm-segmented-item\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("GroupName=\"ReadingPalette\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("GroupName=\"ReadingSmooth\"", readingSettings, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"RadioButton.mm-segmented-item\"", controlsTheme, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"RadioButton.mm-segmented-item:checked", controlsTheme, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowContainsTopLevelUpdateNotification()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("IsVisible=\"{Binding IsUpdateNotificationVisible}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DismissUpdateNotificationCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DownloadUpdateCommand}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowShellRootDrawsVisibleFrameBorder()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("<Border Name=\"ShellRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource MmBorderBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowUsesStartupShellFade()
    {
        var xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "MainWindow.axaml.cs"));

        Assert.Contains("Name=\"ShellRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DoubleTransition Property=\"Opacity\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrepareShellForStartup", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RevealShellAfterStartupAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShellRoot.Opacity = 0", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShellRoot.Opacity = 1", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutPanelUsesRepositoryNoticeInsteadOfPersonalAuthorCredit()
    {
        var aboutXaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "AppAboutPanelView.axaml"));
        var applicateWindowCode = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));
        var applicateProject = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "MarkMello.Applicate.Desktop.csproj"));

        Assert.DoesNotContain("Andrey Ermolaev", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ermolaev.tech", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateAppAboutPanelView", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AboutForkLabel}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AboutForkAuthor}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"{Binding AboutRepositoryUrl}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnAboutLinkClick\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("MarkMelloForkAuthor", applicateProject, StringComparison.Ordinal);
        Assert.Contains("MarkMelloRepositoryUrl", applicateProject, StringComparison.Ordinal);
        Assert.Contains("AboutNoticeLabel", aboutXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicateTocColumnAllowsHiddenStateToCollapseFully()
    {
        var applicateWindowCode = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));

        var tocColumnStart = applicateWindowCode.IndexOf(
            "var tocColumn = new ColumnDefinition",
            StringComparison.Ordinal);
        var splitterColumnStart = applicateWindowCode.IndexOf(
            "var splitterColumn = new ColumnDefinition",
            StringComparison.Ordinal);

        Assert.True(tocColumnStart >= 0, "TOC column declaration should exist.");
        Assert.True(splitterColumnStart > tocColumnStart, "Splitter column should follow TOC column.");

        var tocColumnBlock = applicateWindowCode[tocColumnStart..splitterColumnStart];
        Assert.Contains("MinWidth = 0", tocColumnBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicateTocSplitterKeepsDraggingHighlightWhileDragged()
    {
        var applicateWindowCode = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));

        Assert.Contains("AttachSplitterDraggingHighlight(tocSplitter);", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("splitter.DragStarted +=", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("splitter.DragCompleted +=", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("splitter.PointerCaptureLost +=", applicateWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("InputElement.PointerPressedEvent", applicateWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("InputElement.PointerReleasedEvent", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("control.Classes.Set(\"dragging\", isDragging);", applicateWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicateDocumentSwitchCoverDoesNotCoverTableOfContentsColumn()
    {
        var applicateWindowCode = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));

        var compositorStart = applicateWindowCode.IndexOf(
            "new ApplicateAirspaceCompositor(",
            StringComparison.Ordinal);

        Assert.True(compositorStart >= 0, "document-switch reveal compositor should be wired.");
        var compositorBlock = applicateWindowCode[compositorStart..];
        Assert.Contains("new ApplicateAirspaceCompositor(siblingPanel, viewModel)", compositorBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_tocContentGrid,", compositorBlock, StringComparison.Ordinal);
    }
}
