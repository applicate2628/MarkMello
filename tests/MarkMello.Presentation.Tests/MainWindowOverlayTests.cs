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
    [InlineData("AppSettingsPanel")]
    [InlineData("AppAboutPanel")]
    [InlineData("AppUpdatesPanel")]
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
        Assert.Contains("Command=\"{Binding CheckForUpdatesCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DownloadUpdateCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenDownloadedUpdateCommand}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UpdatesHeader}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"216\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("RowDefinitions=\"Auto,Auto,Auto\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"Auto,156\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UpdateStatusContent\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding IsUpdateBusy}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Opacity=\"{Binding UpdateBusyIndicatorOpacity}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CheckForUpdatesIdleLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CheckForUpdatesBusyLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DownloadUpdateIdleLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DownloadUpdateBusyLabel}\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"300\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("RowDefinitions=\"*,Auto,104\"", appUpdates, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ThemeOptions}\"", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding UpdatesLabel}\"", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding CheckForUpdatesCommand}\"", appSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowContainsDedicatedUpdatesPanel()
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

        Assert.Contains("<Popup Name=\"AppUpdatesPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsOpen=\"{Binding IsAppUpdatesOpen}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<views:AppUpdatesPanelView />", xaml, StringComparison.Ordinal);
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
}
