using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Application.Updates;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// A0-bis / bug #22 — the roadmap's own named probe, run against a WIRED view (the existing
/// <see cref="BulkCloseDirtyBypassTests"/> only exercises a bare, unattached view whose owner lookup
/// cannot resolve, so it pins the degraded fallback rather than the shipped path).
///
/// The claimed defect: Close All / Close Others / To-Left / To-Right on a dirty document removes the
/// tabs BEFORE the unsaved-changes prompt appears, the session is persisted empty by
/// OpenDocuments.CollectionChanged (ApplicateMainWindow.cs SaveSession), and Cancel restores nothing —
/// a restart then loses every tab.
///
/// These tests assert the USER-VISIBLE contract, not the current internal shape:
///   1. a dirty bulk close prompts,
///   2. NOTHING is removed while the prompt is open,
///   3. the session-persistence seam never observes a shrunken document set,
///   4. Cancel leaves every document open.
/// They stay meaningful under any decomposition that keeps those four promises.
/// </summary>
public sealed class BulkCloseSessionLossReproTests : IDisposable
{
    private readonly string _tempRoot;

    public BulkCloseSessionLossReproTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Applicate.Tests.BulkCloseRepro",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // methodName, anchorIndex (-1 = the method takes no argument), activeIndex.
    // Each case puts the ACTIVE (and therefore the only dirty-capable) document INSIDE the closing set.
    [Theory]
    [InlineData("CloseAll", -1, 0)]
    [InlineData("CloseOthers", 1, 0)]
    [InlineData("CloseToLeft", 2, 0)]
    [InlineData("CloseToRight", 0, 1)]
    public async Task DirtyBulkCloseRemovesNothingUntilResolvedAndCancelKeepsEveryTab(
        string methodName,
        int anchorIndex,
        int activeIndex)
    {
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        var docC = await OpenTempAsync(service, "c.md");
        var docs = new[] { docA, docB, docC };
        var allPaths = docs.Select(d => d.FilePath).ToList();

        // Mirror the production session-persistence seam verbatim: ApplicateMainWindow subscribes to
        // OpenDocuments.CollectionChanged and, on every change, snapshots
        // `openDocs.OpenDocuments.Select(d => d.FilePath)` into the session store. Recording the same
        // projection from the same event tells us exactly what SaveSession would have written to disk.
        var persistedSnapshots = new List<IReadOnlyList<string>>();
        ((INotifyCollectionChanged)service.OpenDocuments).CollectionChanged += (_, _) =>
            persistedSnapshots.Add(service.OpenDocuments.Select(d => d.FilePath).ToList());

        var vm = CreateViewModel();
        await vm.CreateNewDocumentCommand.ExecuteAsync(null);
        vm.EditorSession!.SourceText = "# unsaved buffer";
        Assert.True(vm.EditorSession.IsDirty, "precondition: the editor session must be dirty");

        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await headless.Dispatch(() =>
        {
            var view = new ApplicateTabsView(service);
            var window = new Window { Content = view, DataContext = vm };
            try
            {
                window.Show();
                service.Activate(docs[activeIndex]);
                persistedSnapshots.Clear(); // ignore the open/activate warm-up writes

                InvokePrivate(
                    view,
                    methodName,
                    anchorIndex < 0 ? Array.Empty<object?>() : new object?[] { docs[anchorIndex] });

                Dispatcher.UIThread.RunJobs();

                // (1) the close is queued behind the unsaved-changes prompt...
                Assert.True(
                    vm.IsDirtyPromptOpen,
                    $"{methodName}: a dirty bulk close must raise the unsaved-changes prompt");

                // (2) ...and NOTHING is removed while it is open.
                Assert.Equal(allPaths, service.OpenDocuments.Select(d => d.FilePath).ToList());

                // (3) the session-persistence seam never saw a shrunken set, so no empty/partial
                // OpenPaths could reach disk before the user answered.
                Assert.Empty(persistedSnapshots);

                // (4) Cancel cancels: every tab survives.
                vm.CancelDirtyPromptCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();

                Assert.False(vm.IsDirtyPromptOpen);
                Assert.Equal(allPaths, service.OpenDocuments.Select(d => d.FilePath).ToList());
                Assert.Empty(persistedSnapshots);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DirtyBulkCloseDiscardClosesTheSetAndOnlyThenPersistsTheShrunkenSession()
    {
        // The other half of the contract: the gate must DEFER the close, not cancel it. On Discard the
        // documents go, and only then may the session shrink. Without this, a "nothing is removed"
        // assertion above could be satisfied by a bulk close that silently did nothing at all.
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");

        var persistedSnapshots = new List<IReadOnlyList<string>>();
        ((INotifyCollectionChanged)service.OpenDocuments).CollectionChanged += (_, _) =>
            persistedSnapshots.Add(service.OpenDocuments.Select(d => d.FilePath).ToList());

        var vm = CreateViewModel();
        await vm.CreateNewDocumentCommand.ExecuteAsync(null);
        vm.EditorSession!.SourceText = "# unsaved buffer";

        var headless = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        ApplicateTabsView? view = null;
        Window? window = null;
        await headless.Dispatch(() =>
        {
            view = new ApplicateTabsView(service);
            window = new Window { Content = view, DataContext = vm };
            window.Show();
            service.Activate(docA);
            persistedSnapshots.Clear();

            InvokePrivate(view, "CloseOthers", new object?[] { docB });
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsDirtyPromptOpen);
            Assert.Empty(persistedSnapshots);
        }, CancellationToken.None);

        await vm.ConfirmDirtyDiscardCommand.ExecuteAsync(null);

        await headless.Dispatch(() =>
        {
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.False(vm.IsDirtyPromptOpen);
                Assert.Equal(new[] { docB }, service.OpenDocuments);
                Assert.NotEmpty(persistedSnapshots); // the shrink IS persisted, but only after Discard
                Assert.Equal(
                    new[] { docB.FilePath },
                    persistedSnapshots[^1]);
            }
            finally
            {
                window!.Close();
            }
        }, CancellationToken.None);
    }

    private async Task<OpenDocument> OpenTempAsync(OpenDocumentsService service, string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        await File.WriteAllTextAsync(path, fileName);
        return await service.OpenAsync(path);
    }

    private static MainWindowViewModel CreateViewModel()
        => new(
            new OpenDocumentUseCase(new StubLoader()),
            new SaveDocumentUseCase(new StubSaver()),
            new StubFilePicker(),
            new StubCommandLine(),
            new LocalizationService(AppLanguage.English),
            new StubSettingsStore(),
            new StubThemeService(),
            new StubStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new PlainTextRenderer()),
            new StubUpdateService(),
            new MarkdigTableCellSourceEditor());

    private static void InvokePrivate(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method!.Invoke(target, args);
    }

    private sealed class PlainTextRenderer : IMarkdownDocumentRenderer
    {
        public RenderedMarkdownDocument Render(string markdown)
            => RenderedMarkdownDocument.PlainText(markdown);
    }

    private sealed class StubLoader : IDocumentLoader
    {
        public Task<MarkdownSource> LoadAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(new MarkdownSource(path, Path.GetFileName(path), string.Empty));
    }

    private sealed class StubSaver : IDocumentSaver
    {
        public Task SaveAsync(string path, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubFilePicker : IFilePicker
    {
        public Task<string?> PickMarkdownFileAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickSaveMarkdownFileAsync(string suggestedFileName, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(FileSavePickerSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubCommandLine : ICommandLineActivation
    {
        public string? GetActivationFilePath() => null;
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public ValueTask<ReadingPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReadingPreferences.Default);

        public ValueTask SavePreferencesAsync(ReadingPreferences preferences, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<ThemeMode> LoadThemeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ThemeMode.Light);

        public ValueTask SaveThemeAsync(ThemeMode theme, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<AppLanguage> LoadLanguageAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AppLanguage.English);

        public ValueTask SaveLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<WindowPlacement?> LoadWindowPlacementAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<WindowPlacement?>(null);

        public ValueTask SaveWindowPlacementAsync(WindowPlacement? placement, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ResetAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class StubThemeService : IThemeService
    {
        public void Apply(ThemeMode mode, LightPaletteMode lightPalette)
        {
        }

        public ThemeMode GetEffectiveTheme() => ThemeMode.Light;
    }

    private sealed class StubStartupMetrics : IStartupMetrics
    {
        public void Mark(StartupStage stage)
        {
        }

        public StartupSnapshot Snapshot()
            => new(new Dictionary<StartupStage, TimeSpan>());
    }

    private sealed class StubUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<UpdateCheckResult>(new UpdateCheckResult.SourceNotConfigured("test"));

        public Task<UpdateDownloadResult> DownloadUpdateAsync(
            AppUpdatePackage package,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UpdateDownloadResult>(new UpdateDownloadResult.Failed("test"));

        public Task<UpdatePrepareResult> PrepareDownloadedUpdateAsync(
            AppUpdatePackage package,
            string downloadedFilePath,
            CancellationToken cancellationToken = default)
            => Task.FromResult<UpdatePrepareResult>(new UpdatePrepareResult.Failed("test"));
    }
}
