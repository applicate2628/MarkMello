using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Recent-files DELTA (P1): the VM raises intent-only events for explicit remove/clear
/// (no storage write here -- the host owns that, see ApplicateRecentFilesWiringTests) and
/// closes the app-menu overlay when a recent entry is opened.
/// </summary>
public sealed class MainWindowViewModelRecentFilesTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void RemoveRecentFileRaisesOnceWithExactPath()
    {
        var harness = CreateHarness();
        var raisedCount = 0;
        string? raisedPath = null;
        harness.ViewModel.RecentFileRemoveRequested += (_, path) =>
        {
            raisedCount++;
            raisedPath = path;
        };

        harness.ViewModel.RemoveRecentFileCommand.Execute(@"C:\docs\one.md");

        Assert.Equal(1, raisedCount);
        Assert.Equal(@"C:\docs\one.md", raisedPath);
    }

    [Fact]
    public void RemoveRecentFileWithBlankPathRaisesNothing()
    {
        var harness = CreateHarness();
        var raisedCount = 0;
        harness.ViewModel.RecentFileRemoveRequested += (_, _) => raisedCount++;

        foreach (var path in new[] { null, "", "   " })
        {
            harness.ViewModel.RemoveRecentFileCommand.Execute(path);
        }

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void ClearRecentFilesRaisesOnce()
    {
        var harness = CreateHarness();
        var raisedCount = 0;
        harness.ViewModel.RecentFilesClearRequested += (_, _) => raisedCount++;

        harness.ViewModel.ClearRecentFilesCommand.Execute(null);

        Assert.Equal(1, raisedCount);
    }

    [Fact]
    public void SetRecentFilesPrunesMissingEntriesFromDisplayOnlyWithoutMutatingInput()
    {
        var harness = CreateHarness();
        var existingPath = CreateTempFile();
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Tests.RecentFiles",
            Guid.NewGuid().ToString("N") + "-missing.md");
        var input = new List<string> { existingPath, missingPath };

        harness.ViewModel.SetRecentFiles(input);

        Assert.Equal(2, input.Count);
        Assert.Equal(existingPath, input[0]);
        Assert.Equal(missingPath, input[1]);
        var displayed = Assert.Single(harness.ViewModel.RecentFiles);
        Assert.Equal(existingPath, displayed.Path);
    }

    /// <summary>
    /// The confirmed defect (work-items/bugs/2026-07-26-recent-clear-unreachable-when-all-paths-unavailable.md):
    /// the F9 test below only ever exercised SetRecentFiles([]) -- an EMPTY input -- and never a
    /// NON-EMPTY persisted list filtered to an empty display, which is exactly what happens when
    /// every stored path is temporarily unavailable. HasStoredRecentFiles must stay true (the
    /// clear affordance's gating predicate) even though HasRecentFiles (the display subset) is
    /// false -- the two predicates must NOT collapse into one, or the section/row hides again
    /// with entries still in storage and no way to reach clear.
    /// </summary>
    [Fact]
    public void SetRecentFilesWithOnlyUnavailablePathsKeepsStoredTrueButDisplayFalse()
    {
        var harness = CreateHarness();
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Tests.RecentFiles",
            Guid.NewGuid().ToString("N") + "-missing.md");

        harness.ViewModel.SetRecentFiles([missingPath]);

        Assert.True(
            harness.ViewModel.HasStoredRecentFiles,
            "Storage has one entry (even though it is unavailable), so the clear affordance's gating predicate must be true.");
        Assert.False(harness.ViewModel.HasRecentFiles, "No entry is available on disk, so the display list must stay empty.");
        Assert.Empty(harness.ViewModel.RecentFiles);
    }

    /// <summary>
    /// Negative guard for the same fix: genuinely empty storage must still hide the section --
    /// HasStoredRecentFiles is not a permanently-true escape hatch, it tracks the actual last
    /// pushed list.
    /// </summary>
    [Fact]
    public void SetRecentFilesWithNoStoredPathsHidesTheStoredSection()
    {
        var harness = CreateHarness();

        harness.ViewModel.SetRecentFiles([]);

        Assert.False(harness.ViewModel.HasStoredRecentFiles);
        Assert.False(harness.ViewModel.HasRecentFiles);
    }

    /// <summary>
    /// End-to-end proof (not just gating) that clear actually WORKS in the defect's exact
    /// scenario: storage non-empty, display empty. Wires <see cref="MainWindowViewModel.RecentFilesClearRequested"/>
    /// with the same mirror-then-persist contract the real host (<c>ApplicateMainWindow.InstallActiveDocumentBridge</c>)
    /// uses, so this is not merely asserting the command raises an event -- it proves the
    /// simulated stored list is actually emptied and re-mirrored, and that HasStoredRecentFiles
    /// then correctly flips back to false.
    /// </summary>
    [Fact]
    public void ClearIsReachableAndFunctionalWhenStorageNonEmptyButDisplayIsEmpty()
    {
        var harness = CreateHarness();
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Tests.RecentFiles",
            Guid.NewGuid().ToString("N") + "-missing.md");
        var simulatedStorage = new List<string> { missingPath };
        harness.ViewModel.SetRecentFiles(simulatedStorage);

        Assert.True(harness.ViewModel.HasStoredRecentFiles);
        Assert.False(harness.ViewModel.HasRecentFiles);

        var persistCalls = 0;
        harness.ViewModel.RecentFilesClearRequested += (_, _) =>
        {
            simulatedStorage.Clear();
            harness.ViewModel.SetRecentFiles(simulatedStorage);
            persistCalls++;
        };

        harness.ViewModel.ClearRecentFilesCommand.Execute(null);

        Assert.Equal(1, persistCalls);
        Assert.Empty(simulatedStorage);
        Assert.False(harness.ViewModel.HasStoredRecentFiles);
        Assert.False(harness.ViewModel.HasRecentFiles);
    }

    [Fact]
    public async Task OpenRecentFileClosesAppMenuOverlayAndLoadsTheDocument()
    {
        var harness = CreateHarness();
        var path = CreateTempFile();
        harness.Loader.Sources[path] = new MarkdownSource(path, Path.GetFileName(path), "# Recent");
        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        Assert.Equal(ShellOverlayKind.AppMenu, harness.ViewModel.ShellOverlay);

        await harness.ViewModel.OpenRecentFileCommand.ExecuteAsync(path);

        Assert.Equal(ShellOverlayKind.None, harness.ViewModel.ShellOverlay);
        Assert.Equal(path, harness.ViewModel.Document?.Path);
    }

    [Fact]
    public void RemoveAndClearCommandsAreSafeWithNoSubscribers()
    {
        var harness = CreateHarness();

        var removeException = Record.Exception(
            () => harness.ViewModel.RemoveRecentFileCommand.Execute(@"C:\docs\one.md"));
        var clearException = Record.Exception(
            () => harness.ViewModel.ClearRecentFilesCommand.Execute(null));

        Assert.Null(removeException);
        Assert.Null(clearException);
    }

    /// <summary>
    /// Regression guard: <c>CloseAppOverlayCore</c> (the handler <c>OnIsEditModeChanged</c> uses
    /// to auto-close the app menu on entering edit mode) enumerates every App* ShellOverlayKind
    /// member by name. Adding AppRecent to the enum without adding it to that enumeration would
    /// leave ShellOverlay stuck at AppRecent -- silently -- the moment edit mode starts while the
    /// cascade is open, since none of Export/Settings/About/Updates exercise this path either.
    /// </summary>
    [Fact]
    public void EnteringEditModeClosesTheOpenRecentCascade()
    {
        var harness = CreateHarness();
        harness.ViewModel.OpenAppRecentCommand.Execute(null);
        Assert.Equal(ShellOverlayKind.AppRecent, harness.ViewModel.ShellOverlay);

        harness.ViewModel.IsEditMode = true;

        Assert.Equal(ShellOverlayKind.None, harness.ViewModel.ShellOverlay);
    }

    /// <summary>
    /// F9 regression guard: an adversarial gate measured that clearing the last recent entry (or
    /// removing the last remaining one) while the Recent cascade is open left ShellOverlay stuck
    /// at AppRecent -- a dead end where the cascade keeps rendering a header/divider/clear button
    /// that can no longer act, while the menu row that opened it disappears. The ratified fix
    /// falls back to the parent AppMenu column instead of closing the whole menu.
    /// </summary>
    [Fact]
    public void SetRecentFilesEmptyWhileCascadeOpenFallsBackToAppMenu()
    {
        var harness = CreateHarness();
        harness.ViewModel.OpenAppRecentCommand.Execute(null);
        Assert.Equal(ShellOverlayKind.AppRecent, harness.ViewModel.ShellOverlay);

        harness.ViewModel.SetRecentFiles([]);

        Assert.Equal(ShellOverlayKind.AppMenu, harness.ViewModel.ShellOverlay);
        Assert.True(harness.ViewModel.IsAppMenuOpen);
    }

    /// <summary>
    /// F9 negative guard: the fallback must be scoped to the cascade actually being open --
    /// SetRecentFiles is the single mirror-push entry point and fires on every host mutation,
    /// including ones with no overlay open at all (e.g. session restore). It must never move
    /// ShellOverlay in that case.
    /// </summary>
    [Fact]
    public void SetRecentFilesEmptyWhileCascadeClosedDoesNotChangeShellOverlay()
    {
        var harness = CreateHarness();
        Assert.Equal(ShellOverlayKind.None, harness.ViewModel.ShellOverlay);

        harness.ViewModel.SetRecentFiles([]);

        Assert.Equal(ShellOverlayKind.None, harness.ViewModel.ShellOverlay);
    }

    [Fact]
    public void SetRecentFilesBodyNeverReferencesHostStorage()
    {
        var source = ReadSource("src", "MarkMello.Presentation", "ViewModels", "MainWindowViewModel.RecentFiles.cs");
        var bodyStart = source.IndexOf("public void SetRecentFiles", StringComparison.Ordinal);
        Assert.True(bodyStart >= 0, "SetRecentFiles method not found in source.");
        var bodyEnd = source.IndexOf("[RelayCommand]", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyEnd > bodyStart, "Could not bound the SetRecentFiles method body.");
        var body = source[bodyStart..bodyEnd];

        Assert.DoesNotContain("sessionStore", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("recentPaths", body, StringComparison.Ordinal);
    }

    private string CreateTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MarkMello.Tests.RecentFiles");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "# temp");
        _tempFiles.Add(path);
        return path;
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(
            [AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. pathParts]));

    private static RecentFilesHarness CreateHarness()
    {
        var loader = new StubDocumentLoader();
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(new RecordingDocumentSaver()),
            new StubFilePicker(),
            new StubCommandLineActivation(),
            new LocalizationService(AppLanguage.English),
            new InMemorySettingsStore(),
            new RecordingThemeService(),
            new RecordingStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            new StubUpdateService(),
            new MarkdigTableCellSourceEditor());

        return new RecentFilesHarness(loader, viewModel);
    }

    private sealed record RecentFilesHarness(StubDocumentLoader Loader, MainWindowViewModel ViewModel);
}
