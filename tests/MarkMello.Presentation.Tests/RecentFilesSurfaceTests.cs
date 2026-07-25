namespace MarkMello.Presentation.Tests;

/// <summary>
/// Recent-files DELTA (P4): the welcome-screen row gains a per-entry remove affordance and a
/// clear-all control beside the existing open affordance. Source-text checks only -- no Avalonia
/// headless host is available in this project, matching the sibling <c>ExportMenuSourceTests</c>
/// convention. P5 extends this file with the app-menu inline section's equivalent checks.
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

    private static string ReadWelcomeViewSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Presentation",
            "Views",
            "WelcomeView.axaml"));
}
