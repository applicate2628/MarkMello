namespace MarkMello.Presentation.Tests;

/// <summary>
/// Recent-files DELTA (P4): the welcome-screen row gains a per-entry remove affordance and a
/// clear-all control beside the existing open affordance. Source-text checks only -- no Avalonia
/// headless host is available in this project, matching the sibling <c>ExportMenuSourceTests</c>
/// convention. P5 (REPLACED) mirrors the Export cascade instead of an inline app-menu section
/// (see <c>RecentCascadeMenuSourceTests</c>); this file keeps the welcome-only checks plus two
/// regression guards that protect the hover-reveal mechanism BOTH surfaces reuse.
/// </summary>
public sealed class RecentFilesSurfaceTests
{
    [Fact]
    public void WelcomeRecentRowExposesOpenAndRemoveBothBoundToPath()
    {
        var welcome = ReadWelcomeViewSource();

        Assert.Contains("Command=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).OpenRecentFileCommand}\"", welcome, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).RemoveRecentFileCommand}\"", welcome, StringComparison.Ordinal);

        var openIndex = welcome.IndexOf("OpenRecentFileCommand}\"", StringComparison.Ordinal);
        var openParamIndex = welcome.IndexOf("CommandParameter=\"{Binding Path}\"", openIndex, StringComparison.Ordinal);
        Assert.True(openParamIndex > openIndex, "The open row must pass CommandParameter={Binding Path}.");

        var removeIndex = welcome.IndexOf("RemoveRecentFileCommand}\"", StringComparison.Ordinal);
        var removeParamIndex = welcome.IndexOf("CommandParameter=\"{Binding Path}\"", removeIndex, StringComparison.Ordinal);
        Assert.True(removeParamIndex > removeIndex, "The remove control must pass CommandParameter={Binding Path}.");
    }

    [Fact]
    public void WelcomeClearControlIsPresentWithNoConfirmationMachinery()
    {
        var welcome = ReadWelcomeViewSource();

        Assert.Contains("Command=\"{Binding ClearRecentFilesCommand}\"", welcome, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RecentClearLabel}\"", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("Confirm", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("DirtyPrompt", welcome, StringComparison.Ordinal);
        Assert.DoesNotContain("AreYouSure", welcome, StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeRecentBlockBindsExactlyOneCollection()
    {
        var welcome = ReadWelcomeViewSource();

        var itemsSourceMatches = System.Text.RegularExpressions.Regex.Matches(welcome, "ItemsSource=\"\\{Binding ([A-Za-z]+)\\}\"");
        var match = Assert.Single(itemsSourceMatches);
        Assert.Equal("RecentFiles", match.Groups[1].Value);
    }

    [Fact]
    public void WelcomeRecentBlockStaysGatedOnHasRecentFiles()
    {
        var welcome = ReadWelcomeViewSource();

        var recentBlockIndex = welcome.IndexOf("D11 Recent files", StringComparison.Ordinal);
        Assert.True(recentBlockIndex >= 0, "The recent-files block comment should still mark the block.");
        var visibleIndex = welcome.IndexOf("IsVisible=\"{Binding HasRecentFiles}\"", recentBlockIndex, StringComparison.Ordinal);
        Assert.True(visibleIndex > recentBlockIndex, "The recent-files block must stay gated on HasRecentFiles.");
    }

    [Fact]
    public void WelcomeRemoveButtonHasLocalizedTooltipAndAccessibleName()
    {
        var welcome = ReadWelcomeViewSource();

        Assert.Contains("ToolTip.Tip=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).RecentRemoveEntryTooltip}\"", welcome, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding $parent[ItemsControl].((vm:MainWindowViewModel)DataContext).RecentRemoveEntryTooltip}\"", welcome, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding RecentClearLabel}\"", welcome, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression guard: the hover-reveal mechanism is diff-invisible to every other test here
    /// (all three selectors could be deleted from Controls.axaml and every remaining assertion
    /// in this file, and in the cascade, would still pass -- the `x` would just never appear).
    /// Pins the three P4 selectors so a future edit cannot silently drop them.
    /// </summary>
    [Fact]
    public void RecentRemoveHoverRevealSelectorsArePresentInControlsAxaml()
    {
        var controls = ReadSource("src", "MarkMello.Presentation", "Themes", "Controls.axaml");

        Assert.Contains("Selector=\"Button.mm-recent-remove\"", controls, StringComparison.Ordinal);
        Assert.Contains(
            "Selector=\"Grid.mm-recent-row:pointerover Button.mm-recent-remove\"",
            controls,
            StringComparison.Ordinal);
        Assert.Contains("Selector=\"Button.mm-recent-remove:focus\"", controls, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression guard: an Avalonia Grid/Panel/Border with no Background does not participate
    /// in hit-testing, so the pointer would pass through and ":pointerover" would never activate
    /// on Grid.mm-recent-row -- silently disabling the hover reveal without touching any
    /// selector. Both surfaces that host the row (welcome screen, app-menu cascade) must declare
    /// Background="Transparent" on that row container.
    /// </summary>
    [Fact]
    public void RecentRowContainersDeclareTransparentBackgroundForHitTesting()
    {
        var welcome = ReadWelcomeViewSource();
        var cascadePanel = ReadSource("src", "MarkMello.Presentation", "Views", "AppRecentPanelView.axaml");

        foreach (var source in new[] { welcome, cascadePanel })
        {
            var rowIndex = source.IndexOf("Classes=\"mm-recent-row\"", StringComparison.Ordinal);
            Assert.True(rowIndex >= 0, "Expected a Classes=\"mm-recent-row\" row container.");
            var backgroundIndex = source.IndexOf("Background=\"Transparent\"", rowIndex, StringComparison.Ordinal);
            Assert.True(
                backgroundIndex > rowIndex && backgroundIndex < rowIndex + 120,
                "The mm-recent-row container must declare Background=\"Transparent\" right beside its Classes, or :pointerover never hit-tests.");
        }
    }

    /// <summary>
    /// F10 regression guard: an adversarial gate measured the directory label rendering up to
    /// 296px outside its own column (identical at 1280px and 640px window width) because the
    /// row's inner container was a horizontal StackPanel, which measures children with infinite
    /// width -- so TextTrimming="CharacterEllipsis" (present on both TextBlocks) never fires. The
    /// ratified fix is shape-preserving: replace the inner StackPanel with a
    /// Grid ColumnDefinitions="Auto,*" (filename auto-sized, directory in the constrained star
    /// column) so the row stays the flat layout the user approved while the directory label
    /// actually measures under constraint. Mirrors
    /// <c>AppRecentPanelUsesTrimmingSettingLabelIdiomNotRawHorizontalTextBlocks</c> in
    /// <c>RecentCascadeMenuSourceTests</c>. Honest limit: this is a source-text assertion of
    /// SHAPE (no horizontal StackPanel, a Grid with the expected column split) -- it proves the
    /// row is structurally capable of trimming, not that a given string actually trims at
    /// runtime (that needs a rendered/measured layout pass this test project cannot host).
    /// </summary>
    [Fact]
    public void WelcomeRecentRowInnerContainerIsNotAHorizontalStackPanel()
    {
        var welcome = ReadWelcomeViewSource();

        var fileNameIndex = welcome.IndexOf("Text=\"{Binding FileName}\"", StringComparison.Ordinal);
        Assert.True(fileNameIndex >= 0, "Expected the recent-row filename TextBlock.");
        var containerStart = welcome.LastIndexOf('<', fileNameIndex);
        containerStart = welcome.LastIndexOf('<', containerStart - 1);
        var containerOpenTag = welcome[containerStart..welcome.IndexOf('>', containerStart)];

        Assert.DoesNotContain("StackPanel", containerOpenTag, StringComparison.Ordinal);
        Assert.DoesNotContain("Orientation=\"Horizontal\"", containerOpenTag, StringComparison.Ordinal);
        Assert.Contains("<Grid", containerOpenTag, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinitions=\"Auto,*\"", containerOpenTag, StringComparison.Ordinal);
    }

    private static string ReadWelcomeViewSource()
        => ReadSource("src", "MarkMello.Presentation", "Views", "WelcomeView.axaml");

    /// <summary>
    /// Every interactive control on the welcome screen must carry a style class. A class-less Button
    /// keeps the stock Fluent control theme, whose <c>:pointerover</c> state paints its own chrome over
    /// the app palette AND overrides a locally-set Foreground -- measured: the label jumped to plain
    /// black/white on hover while every neighbour used MmSurfaceHoverBrush. The clear-recent button
    /// shipped exactly that way and no test saw it, which is why this guard exists.
    /// </summary>
    [Fact]
    public void WelcomeClearControlIsStyledByClassNotByBareLocalSetters()
    {
        var welcome = ReadWelcomeViewSource();

        var clearIndex = welcome.IndexOf("Command=\"{Binding ClearRecentFilesCommand}\"", StringComparison.Ordinal);
        Assert.True(clearIndex >= 0, "Expected the welcome clear-recent button.");

        var classesIndex = welcome.LastIndexOf("Classes=\"mm-recent-clear\"", clearIndex, StringComparison.Ordinal);
        Assert.True(
            classesIndex >= 0 && clearIndex - classesIndex < 200,
            "The welcome clear-recent button must carry Classes=\"mm-recent-clear\"; without a class it "
            + "inherits the stock Fluent hover template and its local Foreground is discarded on hover.");

        var controls = ReadSource("src", "MarkMello.Presentation", "Themes", "Controls.axaml");
        Assert.Contains("Selector=\"Button.mm-recent-clear\"", controls, StringComparison.Ordinal);
        Assert.Contains(
            "Selector=\"Button.mm-recent-clear:pointerover /template/ ContentPresenter\"",
            controls,
            StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. pathParts]));
}
