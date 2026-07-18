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
    public async Task ReadingCellEditFlipsInMemoryBufferWithoutDiskReadOrReload()
    {
        // P1: the reading-mode cell edit resolves the IN-MEMORY buffer and never
        // re-reads disk, so an external edit on disk is NOT detected here
        // (last-writer-wins; the save-time disk-divergence check is the scheduled
        // follow-up). The rewrite lands as an unsaved edit — no reload branch.
        const string rendered = "| A | B |\n|---|---|\n| left | right |\n";
        const string fresh = "# external edit\n\n| A | B |\n|---|---|\n| left | right |\n";
        const string expected = "| A | B |\n|---|---|\n| changed | right |\n";
        var harness = await CreateOpenHarnessAsync(rendered);
        harness.Loader.Content = fresh; // external edit — invisible to the in-memory flip
        TableCellCommit? commit = null;
        var refusalCount = 0;
        harness.ViewModel.TableCellCommitted += (_, value) => commit = value;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("left"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(0, refusalCount);
        Assert.NotNull(commit);
        Assert.Equal("changed", commit.Text);
        Assert.Equal(expected, harness.ViewModel.Document?.Content);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal(1, harness.Loader.LoadCount); // only the initial open — no disk re-read
        Assert.Empty(harness.Saver.Saves);
    }

    [Fact]
    public async Task ReadingCellEditAddressesTheInMemoryLineNotAShiftedDiskRow()
    {
        // P1: the edit is addressed by LINE against the in-memory buffer, so a
        // disk row-shift (external edit) cannot select the wrong row here — there
        // is no disk read. Line 2's cell is rewritten as an unsaved edit.
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
        const string expected =
            "| Value |\n"
            + "|---|\n"
            + "| no |\n"
            + "| yes |\n";
        var harness = await CreateOpenHarnessAsync(rendered);
        harness.Loader.Content = shifted;
        var refusalCount = 0;
        TableCellCommit? commit = null;
        harness.ViewModel.TableCellEditRefused += (_, _) => refusalCount++;
        harness.ViewModel.TableCellCommitted += (_, value) => commit = value;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "no",
            key: TableCellIdentity.ComputeKey("yes"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Equal(0, refusalCount);
        Assert.NotNull(commit);
        Assert.Equal(expected, harness.ViewModel.Document?.Content);
        Assert.Equal(1, harness.Loader.LoadCount); // no disk re-read
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
    public async Task ValidReadingEditWritesCanonicalDecodedTextIntoTheDirtyBufferSilently()
    {
        // P1: a validated reading-mode cell edit rewrites the in-memory buffer to
        // the canonical decoded text as an UNSAVED edit \u2014 no disk write, no
        // Document re-render (silent backing-field patch), and the canonical
        // text/key are published so the renderer settles the cell.
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

        Assert.Empty(harness.Saver.Saves); // no auto-persist
        Assert.Equal(expected, harness.ViewModel.Document?.Content);
        Assert.True(harness.ViewModel.IsDirty);
        Assert.Equal(0, documentNotifications);
        Assert.NotNull(commit);
        Assert.Equal(expected, commit.Source.Content);
        Assert.Equal(2, commit.Line);
        Assert.Equal(0, commit.CellIndex);
        Assert.Equal("a & b | c", commit.Text);
        Assert.Equal(TableCellIdentity.ComputeKey("a & b \\| c"), commit.Key);
        Assert.Equal(1, harness.SourceEditor.LocateCount);
        Assert.Equal(4, harness.SourceEditor.ParseCount); // validation plus the pre-edit history patch snapshot
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
        Assert.False(harness.ViewModel.IsDirty); // a no-net-change settle never dirties
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
    public async Task DormantSessionReadingCellEditIsUnsavedAndSavePersists()
    {
        // P1 inversion: a reading-mode cell edit on a dormant session is UNSAVED —
        // the buffer moves, the baseline does not (no disk write). The next Ctrl+E
        // surfaces the unsaved buffer; Ctrl+S persists it and clears the dirty
        // state (Discard would revert it — see the R2 discard-revert test).
        const string source = "| A | B |\n|---|---|\n| old | right |\n";
        const string edited = "| A | B |\n|---|---|\n| new | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null);
        Assert.False(harness.ViewModel.IsEditMode);
        var session = Assert.IsType<EditorSessionViewModel>(harness.ViewModel.EditorSession);

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "new",
            key: TableCellIdentity.ComputeKey("old"),
            origin: TableCellEditOrigin.Viewer);

        Assert.Empty(harness.Saver.Saves);
        Assert.Equal(edited, harness.ViewModel.Document?.Content);
        Assert.Equal(edited, session.SourceText);
        Assert.Equal(source, session.LastPersistedSource); // baseline UNCHANGED
        Assert.True(session.IsDirty);

        await harness.ViewModel.ToggleEditModeCommand.ExecuteAsync(null); // next Ctrl+E
        Assert.Equal(edited, session.SourceText);

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);
        var saved = Assert.Single(harness.Saver.Saves);
        Assert.Equal(edited, saved.Content);
        Assert.False(session.IsDirty);
        Assert.Equal(edited, session.LastPersistedSource);
    }

    [Fact]
    public async Task ReadingCellEditDoesNotBecomeRefusalWhenCommittedSubscriberThrows()
    {
        // The two-phase contract survives P1: the buffer + _document settle BEFORE
        // the (throwing) commit publish, so a failing subscriber propagates its
        // exception and is NOT swallowed into a refusal, and the serializer is
        // released (coordinator finally) so a later edit still runs.
        const string source = "| A | B |\n|---|---|\n| old | right |\n";
        const string edited = "| A | B |\n|---|---|\n| new | right |\n";
        const string editedAgain = "| A | B |\n|---|---|\n| again | right |\n";
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

        Assert.Empty(harness.Saver.Saves); // no auto-persist
        Assert.Equal(edited, harness.ViewModel.Document?.Content);
        Assert.Equal(edited, dormantSession.SourceText);
        Assert.Equal(source, dormantSession.LastPersistedSource); // baseline unchanged
        Assert.True(dormantSession.IsDirty);
        Assert.Empty(refusals);
        Assert.IsType<InvalidOperationException>(exception);

        harness.ViewModel.TableCellCommitted -= throwingHandler;
        TableCellCommit? secondCommit = null;
        harness.ViewModel.TableCellCommitted += (_, value) => secondCommit = value;

        var secondException = await Record.ExceptionAsync(() => harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "again",
            key: TableCellIdentity.ComputeKey("new"),
            origin: TableCellEditOrigin.Viewer));

        Assert.Empty(harness.Saver.Saves);
        Assert.NotNull(secondCommit);
        Assert.Equal(editedAgain, secondCommit.Source.Content);
        Assert.Equal("again", secondCommit.Text);
        Assert.Equal(editedAgain, harness.ViewModel.Document?.Content);
        Assert.Equal(editedAgain, dormantSession.SourceText);
        Assert.Equal(source, dormantSession.LastPersistedSource); // baseline never advanced (no save)
        Assert.Empty(refusals);
        Assert.Null(secondException);
    }

    [Fact]
    public async Task ReadingCellUndoRestoresCanonicalValueDocumentAndBaselineDirtyState()
    {
        const string source = "| A | B |\n|---|---|\n| plain | right |\n";
        const string edited = "| A | B |\n|---|---|\n| changed | right |\n";
        var harness = await CreateOpenHarnessAsync(source);
        InPlaceEditHistoryTransition? transition = null;
        harness.ViewModel.InPlaceEditHistoryTransitioned += (_, value) => transition = value;

        await harness.ViewModel.SetTableCellAsync(
            line: 2,
            cellIndex: 0,
            text: "changed",
            key: TableCellIdentity.ComputeKey("plain"),
            origin: TableCellEditOrigin.Viewer);
        Assert.Equal(edited, harness.ViewModel.Document!.Content);
        Assert.True(harness.ViewModel.IsDirty);

        harness.ViewModel.UndoRealtimeInDocumentEditCommand.Execute(null);

        var applied = Assert.IsType<InPlaceEditHistoryTransition>(transition);
        Assert.Equal(source, applied.Source.Content);
        Assert.Equal(RealtimeInDocumentEditDomPatchKind.TableCell, applied.DomPatch.Kind);
        Assert.Equal(2, applied.DomPatch.Line);
        Assert.Equal(0, applied.DomPatch.CellIndex);
        Assert.Equal("plain", applied.DomPatch.Text);
        Assert.Equal(TableCellIdentity.ComputeKey("plain"), applied.DomPatch.Key);
        Assert.Equal(source, harness.ViewModel.Document!.Content);
        Assert.Equal(source, harness.ViewModel.EditorSession!.SourceText);
        Assert.False(harness.ViewModel.IsDirty);
        Assert.Empty(harness.Saver.Saves);
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
        public string Content { get; set; } = content;

        public int LoadCount { get; private set; }

        public Task<MarkdownSource> LoadAsync(string requestedPath, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (!string.Equals(requestedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileNotFoundException("Document was not found.", requestedPath);
            }

            return Task.FromResult(new MarkdownSource(path, Path.GetFileName(path), Content));
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
