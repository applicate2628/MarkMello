using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;
using Xunit;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowViewModelTableCellTests
{
    [Fact]
    public async Task StaleDiskRefusesBeforeRawLocateReloadsAndDoesNotWrite()
    {
        const string rendered = "| A | B |\n|---|---|\n| left | right |\n";
        const string fresh = "# external edit\n\n| A | B |\n|---|---|\n| left | right |\n";
        var harness = await CreateOpenHarnessAsync(rendered);
        harness.Loader.Content = fresh;
        TableCellRefusal? refusal = null;
        string? contentAtRefusal = null;
        harness.ViewModel.TableCellEditRefused += (_, value) =>
        {
            refusal = value;
            contentAtRefusal = harness.ViewModel.Document?.Content;
        };

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("left"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(new TableCellRefusal(2, 0, harness.Path), refusal);
        Assert.Equal(rendered, contentAtRefusal);
        Assert.Equal(fresh, harness.ViewModel.Document?.Content);
        Assert.Equal(3, harness.Loader.LoadCount); // initial open, fresh gate read, reload
        Assert.Equal(0, harness.SourceEditor.LocateCount);
        Assert.Equal(0, harness.SourceEditor.ParseCount);
        Assert.Empty(harness.Saver.Saves);
    }

    [Fact]
    public async Task DuplicateValueRowShiftRefusesBeforeKeyCanSelectWrongRow()
    {
        const string rendered =
            "| Value |\n"
            + "|---|\n"
            + "| yes |\n"
            + "| yes |\n";
        const string shifted =
            "external line\n"
            + "| Value |\n"
            + "|---|\n"
            + "| yes |\n"
            + "| yes |\n";
        var harness = await CreateOpenHarnessAsync(rendered);
        harness.Loader.Content = shifted;
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "no",
            key: TableCellIdentity.ComputeKey("yes"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(shifted, harness.ViewModel.Document?.Content);
        Assert.Equal(0, harness.SourceEditor.LocateCount);
        Assert.Empty(harness.Saver.Saves);
    }

    [Fact]
    public async Task StaleCellKeyRefusesAfterLocateBeforePlainParse()
    {
        const string source = "| A | B |\n|---|---|\n| left | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("different"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(1, harness.SourceEditor.LocateCount);
        Assert.Equal(0, harness.SourceEditor.ParseCount);
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task ExistingRichTargetRefusesAtPlainCellGate()
    {
        const string source = "| A | B |\n|---|---|\n| **bold** | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("**bold**"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(1, harness.SourceEditor.LocateCount);
        Assert.Equal(1, harness.SourceEditor.ParseCount);
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task PlainInputThatBecomesRichRefusesAtRoundTripGate()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "**bold**",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(1, harness.SourceEditor.LocateCount);
        Assert.Equal(2, harness.SourceEditor.ParseCount); // fresh plain target, rich candidate
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task CandidateShapeDriftRefusesBeforeWrite()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        harness.SourceEditor.TransformSnapshot = (parseIndex, snapshot) =>
            parseIndex == 2 && snapshot is { } value
                ? value with { RowCount = value.RowCount + 1 }
                : snapshot;
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(2, harness.SourceEditor.ParseCount);
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task CandidateDecodedTextMismatchRefusesBeforeWrite()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        harness.SourceEditor.TransformSnapshot = (parseIndex, snapshot) =>
            parseIndex == 2 && snapshot is { } value
                ? value with { Text = "not the committed text" }
                : snapshot;
        var refusalCount = 0;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(1, refusalCount);
        Assert.Equal(3, harness.SourceEditor.ParseCount); // fresh, candidate, isolated expected text
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task ValidReadingEditWritesCanonicalDecodedTextAndSilentlyCommitsSnapshot()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        const string expected = "| A | B |\n|---|---|\n| a & b \\| c | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        TableCellCommit? commit = null;
        var documentNotifications = 0;
        harness.ViewModel.TableCellCommitted += (_, value) => commit = value;
        harness.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Document))
            {
                documentNotifications++;
            }
        };

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: " a\u00a0&   b | c ",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);

        var save = Assert.Single(harness.Saver.Saves);
        Assert.Equal(harness.Path, save.Path);
        Assert.Equal(expected, save.Content);
        Assert.Equal(expected, harness.ViewModel.Document?.Content);
        Assert.Equal(0, documentNotifications);
        Assert.NotNull(commit);
        Assert.Equal(expected, commit.Source.Content);
        Assert.Equal(2, commit.Line);
        Assert.Equal(0, commit.CellIndex);
        Assert.Equal("a & b | c", commit.Text);
        Assert.Equal(TableCellIdentity.ComputeKey("a & b \\| c"), commit.Key);
        Assert.Equal(1, harness.SourceEditor.LocateCount);
        Assert.Equal(3, harness.SourceEditor.ParseCount);
    }

    [Fact]
    public async Task NoNetChangeReadingEditSkipsTheDiskWriteButStillAcksCanonical()
    {
        // A blur that produces no net source change (identical bytes after
        // escaping/re-pad) must NOT rewrite the file — a read-only interaction
        // rewriting disk destroys hand-aligned padding. It still acks ok:true
        // canonical so the renderer settles the cell.
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        TableCellCommit? commit = null;
        var refusalCount = 0;
        harness.ViewModel.TableCellCommitted += (_, value) => commit = value;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "plain",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(0, refusalCount);
        Assert.NotNull(commit);
        Assert.Equal("plain", commit.Text);
        Assert.Equal(TableCellIdentity.ComputeKey("plain"), commit.Key);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
    }

    [Fact]
    public async Task EditPreviewEditChangesOnlyBufferMarksDirtyAndPublishesPreviewCommit()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        const string expected = "| A | B |\n|---|---|\n| changed | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        TableCellCommit? previewCommit = null;
        var readingCommitCount = 0;
        harness.ViewModel.EditPreviewTableCellCommitted += (_, value) => previewCommit = value;
        harness.ViewModel.TableCellCommitted += (_, _) => readingCommitCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.EditPreview);

        Assert.NotNull(harness.ViewModel.EditorSession);
        Assert.Equal(expected, harness.ViewModel.EditorSession.SourceText);
        Assert.True(harness.ViewModel.EditorSession.IsDirty);
        Assert.Equal(source, harness.ViewModel.Document?.Content);
        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(1, harness.Loader.LoadCount);
        Assert.Equal(0, readingCommitCount);
        Assert.NotNull(previewCommit);
        Assert.Equal(expected, previewCommit.Source.Content);
        Assert.Equal("changed", previewCommit.Text);
        Assert.Equal(TableCellIdentity.ComputeKey("changed"), previewCommit.Key);
    }

    [Fact]
    public async Task DormantSessionNextCtrlEDiscardAndSaveCannotRevertPersistedCell()
    {
        const string source = "| A | B |\n|---|---|\n| old | right |\n";
        const string persisted = "| A | B |\n|---|---|\n| new | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        Assert.False(harness.ViewModel.IsEditMode);
        Assert.NotNull(harness.ViewModel.EditorSession);

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "new",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(persisted, harness.ViewModel.Document?.Content);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null); // next Ctrl+E
        Assert.Equal(persisted, harness.ViewModel.EditorSession?.SourceText);
        Assert.Equal(persisted, harness.ViewModel.EditorSession?.LastPersistedSource);

        harness.ViewModel.EditorSession?.DiscardChanges();
        Assert.Equal(persisted, harness.ViewModel.EditorSession?.SourceText);

        harness.Saver.Saves.Clear();
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(harness.Saver.Saves);
        Assert.Equal(persisted, saved.Content);
    }

    [Fact]
    public async Task PersistedEditDoesNotBecomeRefusalWhenCommittedSubscriberThrows()
    {
        const string source = "| A | B |\n|---|---|\n| old | right |\n";
        const string persisted = "| A | B |\n|---|---|\n| new | right |\n";
        const string persistedAgain = "| A | B |\n|---|---|\n| again | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        var dormantSession = Assert.IsType<EditorSessionViewModel>(harness.ViewModel.EditorSession);
        var refusals = new List<TableCellRefusal>();
        harness.ViewModel.TableCellEditRefused += (_, value) => refusals.Add(value);
        EventHandler<TableCellCommit> throwingHandler = (_, _) =>
            throw new InvalidOperationException("The committed subscriber failed.");
        harness.ViewModel.TableCellCommitted += throwingHandler;

        var exception = await Record.ExceptionAsync(() => harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "new",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer));

        var save = Assert.Single(harness.Saver.Saves);
        Assert.Equal(persisted, save.Content);
        Assert.Equal(persisted, harness.ViewModel.Document?.Content);
        Assert.Equal(persisted, dormantSession.SourceText);
        Assert.Equal(persisted, dormantSession.LastPersistedSource);
        Assert.Empty(refusals);
        Assert.IsType<InvalidOperationException>(exception);

        harness.ViewModel.TableCellCommitted -= throwingHandler;
        TableCellCommit? secondCommit = null;
        harness.ViewModel.TableCellCommitted += (_, value) => secondCommit = value;
        harness.Loader.Content = persisted;

        var secondException = await Record.ExceptionAsync(() => harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "again",
            key: TableCellIdentity.ComputeKey("new"),
            origin: TableCellEditOrigin.Viewer));

        Assert.Equal(2, harness.Saver.Saves.Count);
        Assert.Equal(persistedAgain, harness.Saver.Saves[1].Content);
        Assert.NotNull(secondCommit);
        Assert.Equal(persistedAgain, secondCommit.Source.Content);
        Assert.Equal("again", secondCommit.Text);
        Assert.Equal(persistedAgain, harness.ViewModel.Document?.Content);
        Assert.Equal(persistedAgain, dormantSession.SourceText);
        Assert.Equal(persistedAgain, dormantSession.LastPersistedSource);
        Assert.Empty(refusals);
        Assert.Null(secondException);
    }

    [Fact]
    public async Task PersistedEditSettlesForOriginalPathWhenAnotherDocumentBecomesActive()
    {
        const string sourceA = "| A | B |\n|---|---|\n| old | right |\n";
        const string persistedA = "| A | B |\n|---|---|\n| new | right |\n";
        const string sourceB = "# Document B\n";
        var pathA = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "table-cell-a.md");
        var pathB = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "table-cell-b.md");
        var loader = new RecordingLoader(pathA, sourceA);
        var saver = new PersistThenGateDocumentSaver();
        var sourceEditor = new TrackingTableCellSourceEditor(new MarkdigTableCellSourceEditor());
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(saver),
            new StubFilePicker(),
            new StubCommandLineActivation(),
            new LocalizationService(AppLanguage.English),
            new InMemorySettingsStore(),
            new RecordingThemeService(),
            new RecordingStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            new StubUpdateService(),
            sourceEditor);
        await viewModel.OpenPathAsync(pathA);
        await viewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await viewModel.ToggleEditModeCommand.ExecuteAsync(null);
        var dormantSessionA = Assert.IsType<EditorSessionViewModel>(viewModel.EditorSession);
        var commits = new List<TableCellCommit>();
        var refusals = new List<TableCellRefusal>();
        viewModel.TableCellCommitted += (_, value) => commits.Add(value);
        viewModel.TableCellEditRefused += (_, value) => refusals.Add(value);

        var edit = viewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "new",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer);
        await saver.WaitForPersistedWriteAsync();

        viewModel.ApplyOpenedDocumentInPlace(new MarkdownSource(pathB, Path.GetFileName(pathB), sourceB));
        viewModel.EditorSession = dormantSessionA;
        saver.ReleaseCompletion();
        await edit;

        var save = Assert.Single(saver.Saves);
        Assert.Equal(pathA, save.Path);
        Assert.Equal(persistedA, save.Content);
        var commit = Assert.Single(commits);
        Assert.Empty(refusals);
        Assert.Equal(pathA, commit.Source.Path);
        Assert.Equal(persistedA, commit.Source.Content);
        Assert.Equal("new", commit.Text);
        Assert.Equal(pathB, viewModel.Document?.Path);
        Assert.Equal(sourceB, viewModel.Document?.Content);
        Assert.Equal(pathA, dormantSessionA.CurrentPath);
        Assert.Equal(persistedA, dormantSessionA.SourceText);
        Assert.Equal(persistedA, dormantSessionA.LastPersistedSource);
    }

    [Fact]
    public async Task OverlappingEditIsRefusedWhileFirstFreshReadOwnsTheSerializer()
    {
        const string source = "| A | B |\n|---|---|\n| old | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        harness.Loader.GateNextLoad = true;
        var refusals = new List<TableCellRefusal>();
        var commits = 0;
        harness.ViewModel.TableCellEditRefused += (_, value) => refusals.Add(value);
        harness.ViewModel.TableCellCommitted += (_, _) => commits++;

        var first = harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "first",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer);
        await harness.Loader.WaitForGatedLoadAsync();

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "second",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer);

        var refusal = Assert.Single(refusals);
        // The serializer-busy refusal is flagged busy so the renderer keeps the
        // user's typed text (a validation refusal restores; busy does not).
        Assert.Equal(new TableCellRefusal(2, 0, harness.Path, Busy: true), refusal);
        Assert.Equal(2, harness.Loader.LoadCount); // initial open plus first request only
        harness.Loader.ReleaseGatedLoad();
        await first;
        Assert.Single(harness.Saver.Saves);
        Assert.Equal(1, commits);
    }

    private static async Task<Harness> CreateOpenHarnessAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "MarkMello.Tests", "table-cell.md");
        var loader = new RecordingLoader(path, content);
        var saver = new RecordingDocumentSaver();
        var sourceEditor = new TrackingTableCellSourceEditor(new MarkdigTableCellSourceEditor());
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(saver),
            new StubFilePicker(),
            new StubCommandLineActivation(),
            new LocalizationService(AppLanguage.English),
            new InMemorySettingsStore(),
            new RecordingThemeService(),
            new RecordingStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            new StubUpdateService(),
            sourceEditor);

        await viewModel.OpenPathAsync(path);
        return new Harness(path, loader, saver, sourceEditor, viewModel);
    }

    private sealed record Harness(
        string Path,
        RecordingLoader Loader,
        RecordingDocumentSaver Saver,
        TrackingTableCellSourceEditor SourceEditor,
        MainWindowViewModel ViewModel);

    private sealed class RecordingLoader(string path, string content) : IDocumentLoader
    {
        private readonly TaskCompletionSource _gatedLoadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseGatedLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Content { get; set; } = content;

        public int LoadCount { get; private set; }

        public bool GateNextLoad { get; set; }

        public Task WaitForGatedLoadAsync() => _gatedLoadStarted.Task;

        public void ReleaseGatedLoad() => _releaseGatedLoad.TrySetResult();

        public async Task<MarkdownSource> LoadAsync(string requestedPath, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (!string.Equals(requestedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("Document was not found.", requestedPath);
            }

            if (GateNextLoad)
            {
                GateNextLoad = false;
                _gatedLoadStarted.TrySetResult();
                await _releaseGatedLoad.Task.WaitAsync(cancellationToken);
            }

            return new MarkdownSource(path, Path.GetFileName(path), Content);
        }
    }

    private sealed class PersistThenGateDocumentSaver : IDocumentSaver
    {
        private readonly TaskCompletionSource _persistedWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Path, string Content)> Saves { get; } = [];

        public Task WaitForPersistedWriteAsync() => _persistedWrite.Task;

        public void ReleaseCompletion() => _releaseCompletion.TrySetResult();

        public async Task SaveAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            Saves.Add((path, content));
            _persistedWrite.TrySetResult();
            await _releaseCompletion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TrackingTableCellSourceEditor(ITableCellSourceEditor inner) : ITableCellSourceEditor
    {
        public Func<int, TableCellSourceSnapshot?, TableCellSourceSnapshot?>? TransformSnapshot { get; set; }

        public int LocateCount { get; private set; }

        public int ParseCount { get; private set; }

        public TableCellSpan? Locate(string source, int line, int cellIndex)
        {
            LocateCount++;
            return inner.Locate(source, line, cellIndex);
        }

        public TableCellSourceSnapshot? ParsePlainCell(string source, int line, int cellIndex)
        {
            ParseCount++;
            var snapshot = inner.ParsePlainCell(source, line, cellIndex);
            return TransformSnapshot?.Invoke(ParseCount, snapshot) ?? snapshot;
        }
    }
}
