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
    public void UpdateSurfaceLivesInAppMenuNotAppSettings()
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

        Assert.Contains("Text=\"{Binding UpdatesLabel}\"", appMenu, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CheckForUpdatesCommand}\"", appMenu, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DownloadUpdateCommand}\"", appMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding UpdatesLabel}\"", appSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding CheckForUpdatesCommand}\"", appSettings, StringComparison.Ordinal);
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

        Assert.DoesNotContain("Andrey Ermolaev", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ermolaev.tech", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateAppAboutPanelView", applicateWindowCode, StringComparison.Ordinal);
        Assert.Contains("AboutNoticeLabel", aboutXaml, StringComparison.Ordinal);
    }
}
