using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Recent-files DELTA (P5, REPLACED): the app-menu second surface is a cascade sub-column
/// mirroring the Export cascade exactly (<c>ExportMenuSourceTests</c> is the template this file
/// follows), not the originally-planned inline section -- the user saw the shipped inline
/// version and asked for it to open as its own "Недавние ▸" sub-panel, the same shape as
/// "Export ▸". Source-text checks only, matching the sibling convention (no Avalonia headless
/// host in this project). Localization completeness for the new keys this phase adds
/// (AppMenuRecentHint, AppRecentHeader, OverlayCloseRecent) is covered by the existing
/// <c>RecentFilesLocalizationTests</c> array rather than duplicated here.
/// </summary>
public sealed class RecentCascadeMenuSourceTests
{
    /// <summary>
    /// UPDATED for the 2026-07-26 fix
    /// (work-items/bugs/2026-07-26-recent-clear-unreachable-when-all-paths-unavailable.md): this
    /// row is the ONLY route into the AppRecent cascade that hosts "Clear recent files", so it
    /// (and its divider) must gate on HasStoredRecentFiles ("is anything stored") rather than the
    /// old HasRecentFiles ("is anything displayable") -- otherwise a fully-unavailable stored list
    /// hides the row and the clear affordance behind it becomes unreachable.
    /// </summary>
    [Fact]
    public void AppMenuRowAndDividerOpenRecentCascadeGatedOnHasStoredRecentFiles()
    {
        var appMenu = ReadSource("src", "MarkMello.Presentation", "Views", "AppMenuPanelView.axaml");

        Assert.Contains("Command=\"{Binding OpenAppRecentCommand}\"", appMenu, StringComparison.Ordinal);

        var rowIndex = appMenu.IndexOf("Command=\"{Binding OpenAppRecentCommand}\"", StringComparison.Ordinal);
        var buttonStart = appMenu.LastIndexOf("<Button", rowIndex, StringComparison.Ordinal);
        Assert.True(buttonStart >= 0, "Could not find the enclosing <Button> for the Recent row.");
        var buttonEnd = appMenu.IndexOf('>', rowIndex);
        var buttonOpenTag = appMenu[buttonStart..buttonEnd];

        Assert.Contains("IsVisible=\"{Binding HasStoredRecentFiles}\"", buttonOpenTag, StringComparison.Ordinal);
        Assert.DoesNotContain("IsVisible=\"{Binding HasRecentFiles}\"", buttonOpenTag, StringComparison.Ordinal);

        var dividerIndex = appMenu.IndexOf("mm-setting-divider", buttonEnd, StringComparison.Ordinal);
        Assert.True(dividerIndex > buttonEnd, "Expected the divider following the Recent row.");
        var dividerVisibleIndex = appMenu.IndexOf("IsVisible=\"{Binding HasStoredRecentFiles}\"", dividerIndex, StringComparison.Ordinal);
        Assert.True(
            dividerVisibleIndex > dividerIndex && dividerVisibleIndex < dividerIndex + 100,
            "The divider following the Recent row must gate on the same HasStoredRecentFiles "
            + "predicate as the row, so a zero-STORED list leaves no orphan divider gap.");
    }

    /// <summary>
    /// F2 regression guard: an adversarial gate on the (since-reset) inline P5 measured that a
    /// horizontal StackPanel of raw TextBlocks never trims -- it measures children with infinite
    /// width, so long filenames/paths get hard-clipped by this panel's ClipToBounds instead of
    /// showing an ellipsis. The cascade must use the mm-setting-label/mm-setting-hint idiom
    /// (proven safe: it is what Export/Settings/Updates already ship) in a vertical layout, not
    /// that shape.
    /// </summary>
    [Fact]
    public void AppRecentPanelUsesTrimmingSettingLabelIdiomNotRawHorizontalTextBlocks()
    {
        var panel = ReadSource("src", "MarkMello.Presentation", "Views", "AppRecentPanelView.axaml");

        Assert.Contains("Classes=\"mm-setting-label\"", panel, StringComparison.Ordinal);
        Assert.Contains("Classes=\"mm-setting-hint\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Orientation=\"Horizontal\"", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRecentPanelHasOpenAndRemoveBothBoundToPathUsingMenuItemButtons()
    {
        var panel = ReadSource("src", "MarkMello.Presentation", "Views", "AppRecentPanelView.axaml");

        Assert.Contains("Command=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).OpenRecentFileCommand}\"", panel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).RemoveRecentFileCommand}\"", panel, StringComparison.Ordinal);
        Assert.Contains("Classes=\"mm-menu-item\"", panel, StringComparison.Ordinal);

        var openIndex = panel.IndexOf("OpenRecentFileCommand}\"", StringComparison.Ordinal);
        var openParamIndex = panel.IndexOf("CommandParameter=\"{Binding Path}\"", openIndex, StringComparison.Ordinal);
        Assert.True(openParamIndex > openIndex, "The open row must pass CommandParameter={Binding Path}.");

        var removeIndex = panel.IndexOf("RemoveRecentFileCommand}\"", StringComparison.Ordinal);
        var removeParamIndex = panel.IndexOf("CommandParameter=\"{Binding Path}\"", removeIndex, StringComparison.Ordinal);
        Assert.True(removeParamIndex > removeIndex, "The remove control must pass CommandParameter={Binding Path}.");
    }

    [Fact]
    public void AppRecentPanelClearControlPresentWithNoConfirmationMachinery()
    {
        var panel = ReadSource("src", "MarkMello.Presentation", "Views", "AppRecentPanelView.axaml");

        Assert.Contains("Command=\"{Binding ClearRecentFilesCommand}\"", panel, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RecentClearLabel}\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("DirtyPrompt", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("AreYouSure", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRecentPanelBindsExactlyOneCollectionNamedRecentFiles()
    {
        var panel = ReadSource("src", "MarkMello.Presentation", "Views", "AppRecentPanelView.axaml");

        var itemsSourceMatches = System.Text.RegularExpressions.Regex.Matches(panel, "ItemsSource=\"\\{Binding ([A-Za-z]+)\\}\"");
        var match = Assert.Single(itemsSourceMatches);
        Assert.Equal("RecentFiles", match.Groups[1].Value);
    }

    /// <summary>
    /// Replaces the old (reset) T5.5 <c>NoCascadeOverlayKindOrDedicatedAppRecentCommandWasIntroduced</c>,
    /// which asserted the exact opposite of what the user now wants. Pins that the cascade
    /// actually EXISTS and is wired end-to-end: the ShellOverlayKind member, the panel view, the
    /// window host's content switch, and the menu row that opens it.
    /// </summary>
    [Fact]
    public void AppRecentOverlayIsMappedByWindowHost()
    {
        Assert.Equal("AppRecent", ShellOverlayKind.AppRecent.ToString());

        var codeBehind = ReadSource("src", "MarkMello.Presentation", "Views", "MainWindow.axaml.cs");
        Assert.Contains("ShellOverlayKind.AppRecent =>", codeBehind, StringComparison.Ordinal);
        Assert.Contains("new AppRecentPanelView()", codeBehind, StringComparison.Ordinal);

        var appMenu = ReadSource("src", "MarkMello.Presentation", "Views", "AppMenuPanelView.axaml");
        Assert.Contains("Command=\"{Binding OpenAppRecentCommand}\"", appMenu, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. pathParts]));
}
