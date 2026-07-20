using System.Reflection;
using MarkMello.Domain;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

public sealed class ExportMenuSourceTests
{
    private static readonly string[] LocalizationProperties =
    [
        "AppMenuExportLabel",
        "AppMenuExportHint",
        "AppMenuPrintLabel",
        "AppMenuPrintHint",
        "AppExportHeader",
        "ExportPdfLabel",
        "ExportPdfHint",
        "ExportHtmlLabel",
        "ExportHtmlHint",
        "OverlayCloseExport",
        "ExportPdfDialogTitle",
        "ExportHtmlDialogTitle",
        "PdfDocuments",
        "HtmlDocuments",
        "ExportFailedTitle",
        "ExportFailureDetailsFormat",
    ];

    [Fact]
    public void ExportPanelHasPdfAndHtmlOnlyWhilePrintIsTopLevel()
    {
        var exportPanel = ReadSource("src", "MarkMello.Presentation", "Views", "AppExportPanelView.axaml");
        var appMenu = ReadSource("src", "MarkMello.Presentation", "Views", "AppMenuPanelView.axaml");

        Assert.Contains("Command=\"{Binding ExportPdfCommand}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ExportHtmlCommand}\"", exportPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("Png", exportPanel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrintCommand", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenAppExportCommand}\"", appMenu, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PrintCommand}\"", appMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportPngCommand", appMenu, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportPanelUsesNativeButtonsWithLocalizedAccessibleLabels()
    {
        var exportPanel = ReadSource("src", "MarkMello.Presentation", "Views", "AppExportPanelView.axaml");

        Assert.Contains("<Button Classes=\"mm-menu-item\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ExportPdfLabel}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ExportHtmlLabel}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding ExportPdfLabel}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding ExportHtmlLabel}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ReturnToAppMenuCommand}\"", exportPanel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CloseOverlayCommand}\"", exportPanel, StringComparison.Ordinal);
    }

    [Fact]
    public void AppExportOverlayIsMappedByWindowHost()
    {
        Assert.Equal("AppExport", ShellOverlayKind.AppExport.ToString());
        var codeBehind = ReadSource("src", "MarkMello.Presentation", "Views", "MainWindow.axaml.cs");
        Assert.Contains("ShellOverlayKind.AppExport =>", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new AppExportPanelView()", codeBehind, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Russian)]
    public void ExportLocalizationIsCompleteAcrossDictionaryViewModelAndRefreshList(AppLanguage language)
    {
        var localization = new LocalizationService(language);
        var viewModelType = typeof(MainWindowViewModel);
        var localizedNamesField = viewModelType.GetField(
            "LocalizedBindingPropertyNames",
            BindingFlags.Static | BindingFlags.NonPublic);
        var localizedNames = Assert.IsType<string[]>(localizedNamesField?.GetValue(null));

        foreach (var propertyName in LocalizationProperties)
        {
            Assert.DoesNotContain("[[", localization[propertyName], StringComparison.Ordinal);
            var property = viewModelType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property);
            Assert.True(property!.CanRead);
            Assert.False(property.CanWrite);
            Assert.Contains(propertyName, localizedNames);
        }
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. pathParts]));
}
