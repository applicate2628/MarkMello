using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

/// <summary>
/// Document-load currency: a load that was superseded while it awaited must NOT publish.
///
/// Runtime-proven defect (.scratch/bug9/PROOF-site4-trace.txt): a reload suspended on a 7MB disk read
/// while a tab switch won and applied; the superseded reload then resumed and published over it —
/// last-completer-wins. Reachable by an ordinary flow: press F5, then click another tab.
///
/// The window is engineered deterministic through GatedDocumentLoader, never raced (race-window
/// assertion discipline).
/// </summary>
public class DocumentLoadCurrencyTests
{
    [Fact]
    public async Task SupersededLoadDoesNotPublishOverAnInPlaceApply()
    {
        var (vm, loader) = Create();
        var stale = Source("stale.md", "STALE");
        var winner = Source("winner.md", "WINNER");
        loader.Sources["stale.md"] = stale;
        loader.GatedPaths.Add("stale.md");

        var load = vm.OpenPathAsync("stale.md");          // parks inside LoadDocumentAsync
        Assert.True(loader.IsParked("stale.md"));

        vm.ApplyOpenedDocumentInPlace(winner);            // the tab switch wins, exactly as in the trace
        Assert.Equal("winner.md", vm.Document?.Path);

        loader.Release("stale.md");                       // superseded load resumes
        await load;

        Assert.Equal("winner.md", vm.Document?.Path);     // must NOT have been clobbered
        Assert.Equal("WINNER", vm.Document?.Content);
    }

    [Fact]
    public async Task NewestRequestWinsWhenTheOlderLoadFinishesFirst()
    {
        var (vm, loader) = Create();
        loader.Sources["a.md"] = Source("a.md", "A");
        loader.Sources["b.md"] = Source("b.md", "B");
        loader.GatedPaths.Add("a.md");
        loader.GatedPaths.Add("b.md");

        var a = vm.OpenPathAsync("a.md");
        var b = vm.OpenPathAsync("b.md");                 // supersedes A

        loader.Release("a.md");                           // older completes FIRST
        await a;
        loader.Release("b.md");
        await b;

        Assert.Equal("b.md", vm.Document?.Path);          // newest REQUEST wins, not first completer
    }

    [Fact]
    public async Task NewestRequestWinsWhenTheNewerLoadFinishesFirst()
    {
        var (vm, loader) = Create();
        loader.Sources["a.md"] = Source("a.md", "A");
        loader.Sources["b.md"] = Source("b.md", "B");
        loader.GatedPaths.Add("a.md");
        loader.GatedPaths.Add("b.md");

        var a = vm.OpenPathAsync("a.md");
        var b = vm.OpenPathAsync("b.md");

        loader.Release("b.md");                           // newer completes first
        await b;
        loader.Release("a.md");                           // stale resumes last
        await a;

        Assert.Equal("b.md", vm.Document?.Path);          // stale must not clobber on the way out
    }

    [Fact]
    public async Task SupersededFailureDoesNotPublishAnErrorOverTheWinner()
    {
        var (vm, loader) = Create();
        var winner = Source("winner.md", "WINNER");
        loader.Sources["doomed.md"] = Source("doomed.md", "x");
        loader.GatedPaths.Add("doomed.md");

        var load = vm.OpenPathAsync("doomed.md");
        vm.ApplyOpenedDocumentInPlace(winner);
        loader.ReleaseWithFailure("doomed.md", new IOException("gone"));
        await load;

        // A superseded FAILURE must not nuke the winner's state either: publishing LoadError for a
        // document the user already left is the same harm as a stale success.
        Assert.Equal("winner.md", vm.Document?.Path);
        Assert.Equal(ViewState.Viewing, vm.State);
    }

    [Fact]
    public async Task SoloLoadStillPublishes()
    {
        // The no-regression anchor: with nothing superseding it, an ordinary load must publish
        // exactly as before. LoadDocumentAsync is THE path for startup/open/reload/drop/restore —
        // a guard that misfires here breaks opening documents outright.
        var (vm, loader) = Create();
        loader.Sources["solo.md"] = Source("solo.md", "SOLO");

        await vm.OpenPathAsync("solo.md");

        Assert.Equal("solo.md", vm.Document?.Path);
        Assert.Equal("SOLO", vm.Document?.Content);
        Assert.Equal(ViewState.Viewing, vm.State);
    }

    [Fact]
    public async Task ReloadingTheSamePathStillPublishesFreshContent()
    {
        // Pins the currency SEMANTICS: "reload the same path" is legitimate — its own ticket is the
        // latest, so it must publish. A path-equality guard would have vetoed this.
        var (vm, loader) = Create();
        loader.Sources["p.md"] = Source("p.md", "OLD");
        await vm.OpenPathAsync("p.md");

        loader.Sources["p.md"] = Source("p.md", "NEW");
        await vm.OpenPathAsync("p.md");

        Assert.Equal("NEW", vm.Document?.Content);
    }

    // ---- FALSIFICATION PROBES: the two angles SPLIT on whether close/create must bump the epoch.
    // fable said exclude (those instances are runtime-unproven); sol said include (they mutate
    // current-document identity). Neither is evidence. These decide it with data: if a load in
    // flight republishes over a close/create, the gap is REAL.

    [Fact]
    public async Task ClosingWhileALoadIsInFlightDoesNotResurrectTheDocument()
    {
        var (vm, loader) = Create();
        loader.Sources["doomed.md"] = Source("doomed.md", "DOOMED");
        loader.GatedPaths.Add("doomed.md");

        var load = vm.OpenPathAsync("doomed.md");
        await vm.CloseFileCommand.ExecuteAsync(null);     // user closes while the read is in flight
        loader.Release("doomed.md");
        await load;

        Assert.Null(vm.Document);                          // the closed document must NOT come back
    }

    [Fact]
    public async Task CreatingANewDocumentWhileALoadIsInFlightIsNotClobbered()
    {
        var (vm, loader) = Create();
        loader.Sources["old.md"] = Source("old.md", "OLD");
        loader.GatedPaths.Add("old.md");

        var load = vm.OpenPathAsync("old.md");
        await vm.CreateNewDocumentCommand.ExecuteAsync(null);
        loader.Release("old.md");
        await load;

        Assert.Null(vm.Document);                          // the new blank document must survive
    }

    private static MarkdownSource Source(string path, string content) => new(path, path, content);

    private static (MainWindowViewModel Vm, GatedDocumentLoader Loader) Create()
    {
        var loader = new GatedDocumentLoader();
        var vm = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(new RecordingDocumentSaver()),
            new StubFilePicker(),
            new StubCommandLineActivation(),
            new LocalizationService(AppLanguage.English),
            new InMemorySettingsStore(),
            new RecordingThemeService(),
            new RecordingStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            new StubUpdateService());
        return (vm, loader);
    }
}
