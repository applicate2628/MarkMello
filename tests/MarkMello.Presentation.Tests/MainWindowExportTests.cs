using MarkMello.Application.Abstractions;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;
using MarkMello.Presentation.Localization;
using MarkMello.Presentation.ViewModels;

namespace MarkMello.Presentation.Tests;

public sealed class MainWindowExportTests
{
    [Theory]
    [InlineData("pdf", ".pdf", "*.pdf", "Export PDF", "PDF documents")]
    [InlineData("html", ".html", "*.html", "Export HTML", "HTML documents")]
    public async Task FileExportUsesFormatSpecificSaveSpec(
        string format,
        string extension,
        string pattern,
        string title,
        string filterName)
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", $"document{extension}");

        if (format == "pdf")
        {
            await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);
        }
        else
        {
            await harness.ViewModel.ExportHtmlCommand.ExecuteAsync(null);
        }

        var spec = Assert.Single(harness.FilePicker.SaveSpecs);
        Assert.Equal(title, spec.Title);
        Assert.Equal(extension, spec.DefaultExtension);
        Assert.Equal(filterName, spec.FileTypeName);
        Assert.Equal([pattern], spec.Patterns);
        Assert.EndsWith(extension, spec.SuggestedFileName, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(harness.Exporter.Calls);
        Assert.Equal(format, call.Kind);
        Assert.Equal(harness.FilePicker.GenericSavePath, call.Path);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    public async Task PickerCancellationIsSilentAndDoesNotInvokeExporter(string format)
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = null;

        if (format == "pdf")
        {
            await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);
        }
        else
        {
            await harness.ViewModel.ExportHtmlCommand.ExecuteAsync(null);
        }

        Assert.Empty(harness.Exporter.Calls);
        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
        Assert.Empty(harness.ViewModel.ErrorTitle);
        Assert.Empty(harness.ViewModel.ErrorDetails);
    }

    [Fact]
    public async Task HtmlExportPassesExactCurrentMarkdownSource()
    {
        const string markdown = "# Exact\n\n$E = mc^2$\n";
        var harness = await CreateHarnessWithDocumentAsync(markdown);
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "exact.html");

        await harness.ViewModel.ExportHtmlCommand.ExecuteAsync(null);

        var call = Assert.Single(harness.Exporter.Calls);
        Assert.Equal("html", call.Kind);
        Assert.Equal(markdown, call.MarkdownSource);
        Assert.EndsWith("exact.html", call.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrintClosesOverlayAndNeverOpensPicker()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.ViewModel.ToggleAppMenuCommand.Execute(null);
        harness.ViewModel.OpenAppExportCommand.Execute(null);
        Assert.Equal(ShellOverlayKind.AppExport, harness.ViewModel.ShellOverlay);

        await harness.ViewModel.PrintCommand.ExecuteAsync(null);

        Assert.Equal(ShellOverlayKind.None, harness.ViewModel.ShellOverlay);
        Assert.Empty(harness.FilePicker.SaveSpecs);
        Assert.Equal("print", Assert.Single(harness.Exporter.Calls).Kind);
    }

    [Theory]
    [InlineData(ExportStatus.NoDocument)]
    [InlineData(ExportStatus.RenderIncomplete)]
    [InlineData(ExportStatus.PrintReturnedFalse)]
    [InlineData(ExportStatus.WriteFailed)]
    [InlineData(ExportStatus.Cancelled)]
    [InlineData(ExportStatus.ProcessCrashed)]
    [InlineData(ExportStatus.Faulted)]
    [InlineData(ExportStatus.Deferred)]
    public async Task InvokedExporterFailureIsSurfacedWithTypedStatusAndDetail(ExportStatus status)
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "failure.pdf");
        harness.Exporter.NextResult = new ExportResult(status, "detail-X");

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        // THE point of the fix: a failed EXPORT must not replace the document
        // view with the load-error screen. The file loaded fine; only the save
        // leg failed, so the rendered document (and the reading position) stays.
        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
        Assert.True(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Equal("Export failed", harness.ViewModel.ExportFailureNoticeTitle);
        Assert.Contains(
            status.ToString(),
            harness.ViewModel.ExportFailureNoticeDetails,
            StringComparison.Ordinal);
        Assert.Contains("detail-X", harness.ViewModel.ExportFailureNoticeDetails, StringComparison.Ordinal);

        // The load-error surface is a different owner and must stay untouched.
        Assert.Empty(harness.ViewModel.ErrorTitle);
        Assert.Empty(harness.ViewModel.ErrorDetails);
    }

    [Fact]
    public async Task ExportFailureFallsBackToExceptionMessageWhenDetailIsBlank()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "failure.pdf");
        harness.Exporter.NextResult = new ExportResult(
            ExportStatus.WriteFailed,
            "   ",
            new IOException("destination-locked"));

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
        Assert.Contains(
            "destination-locked",
            harness.ViewModel.ExportFailureNoticeDetails,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulExportShowsNoFailureNotice()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "ok.pdf");

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
        Assert.False(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Empty(harness.ViewModel.ExportFailureNoticeTitle);
        Assert.Empty(harness.ViewModel.ExportFailureNoticeDetails);
    }

    [Fact]
    public async Task ExportFailureNoticeIsRetiredByExplicitDismiss()
    {
        var harness = await FailExportAsync();

        harness.ViewModel.DismissExportFailureNoticeCommand.Execute(null);

        Assert.False(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Empty(harness.ViewModel.ExportFailureNoticeDetails);
        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
    }

    [Fact]
    public async Task ExportFailureNoticeIsRetiredByStartingAnotherExport()
    {
        var harness = await FailExportAsync();
        harness.Exporter.NextResult = new ExportResult(ExportStatus.Success);

        await harness.ViewModel.ExportHtmlCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
    }

    [Fact]
    public async Task ExportFailureNoticeIsRetiredBySwitchingDocument()
    {
        var harness = await FailExportAsync();

        const string otherPath = "C:\\docs\\other.md";
        harness.Loader.Sources[otherPath] = new MarkdownSource(otherPath, "other.md", "# Other");
        await harness.ViewModel.OpenPathAsync(otherPath);

        Assert.False(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Equal(ViewState.Viewing, harness.ViewModel.State);
    }

    [Fact]
    public async Task ExportFailureNoticeIsRetiredByClosingDocument()
    {
        var harness = await FailExportAsync();

        harness.ViewModel.CloseFileCommand.Execute(null);

        Assert.False(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Equal(ViewState.NoDocument, harness.ViewModel.State);
    }

    [Fact]
    public async Task ExportFailureNoticeRaisesItsOwnBindingsOnVisibilityChange()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "failure.pdf");
        harness.Exporter.NextResult = new ExportResult(ExportStatus.WriteFailed, "detail-X");

        var raised = new List<string>();
        harness.ViewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        Assert.Contains(nameof(MainWindowViewModel.IsExportFailureNoticeVisible), raised);
        Assert.Contains(nameof(MainWindowViewModel.ExportFailureNoticeTitle), raised);
        Assert.Contains(nameof(MainWindowViewModel.ExportFailureNoticeDetails), raised);
    }

    [Fact]
    public async Task ExportFailureNoticeTextFollowsLanguageSwitch()
    {
        var harness = await FailExportAsync();
        Assert.Equal("Export failed", harness.ViewModel.ExportFailureNoticeTitle);

        harness.ViewModel.SelectRussianLanguageCommand.Execute(null);

        Assert.Equal("Ошибка экспорта", harness.ViewModel.ExportFailureNoticeTitle);
        Assert.Contains(
            "detail-X",
            harness.ViewModel.ExportFailureNoticeDetails,
            StringComparison.Ordinal);
        Assert.Equal("Скрыть", harness.ViewModel.ExportFailureNoticeDismissLabel);
    }

    private static async Task<ExportHarness> FailExportAsync()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "failure.pdf");
        harness.Exporter.NextResult = new ExportResult(ExportStatus.WriteFailed, "detail-X");

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        Assert.True(harness.ViewModel.IsExportFailureNoticeVisible);
        return harness;
    }

    [Fact]
    public async Task ExportCommandsSerializeAcrossFormatsAndRestoreCanExecute()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "busy.pdf");
        var gate = new TaskCompletionSource<ExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Exporter.NextTask = gate.Task;

        var first = harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);
        await harness.Exporter.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(harness.ViewModel.IsExportBusy);
        Assert.False(harness.ViewModel.ExportPdfCommand.CanExecute(null));
        Assert.False(harness.ViewModel.ExportHtmlCommand.CanExecute(null));
        Assert.False(harness.ViewModel.PrintCommand.CanExecute(null));

        await harness.ViewModel.ExportHtmlCommand.ExecuteAsync(null);
        await harness.ViewModel.PrintCommand.ExecuteAsync(null);
        Assert.Single(harness.Exporter.Calls);

        gate.SetResult(new ExportResult(ExportStatus.Success));
        await first;

        Assert.False(harness.ViewModel.IsExportBusy);
        Assert.True(harness.ViewModel.ExportPdfCommand.CanExecute(null));
        Assert.True(harness.ViewModel.ExportHtmlCommand.CanExecute(null));
        Assert.True(harness.ViewModel.PrintCommand.CanExecute(null));
    }

    /// <summary>
    /// The root cause in one assertion. PrepareForExportAsync registers its
    /// cancellation callback ONLY when <c>cancellationToken.CanBeCanceled</c>, so
    /// the menu path's former <c>CancellationToken.None</c> registered nothing and
    /// left the renderer barrier with no bound whatsoever. A commit that reasons
    /// "an unsettleable render is user-cancellable" is relying on exactly this.
    /// </summary>
    [Fact]
    public async Task MenuExportHandsTheExporterATokenThatCanActuallyBeCancelled()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "token.pdf");

        await harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);

        Assert.True(harness.Exporter.LastToken.CanBeCanceled);
    }

    /// <summary>
    /// The filed defect end to end: an export whose barrier never settles on its
    /// own. Pins BOTH directions -- the latch must still hold (and still disable
    /// the export actions) while the export is genuinely in flight, and it must
    /// release once the user cancels. Also pins that the panel carrying the cancel
    /// affordance stays reachable while busy, since a cancel the user cannot reach
    /// is not a bound at all.
    /// </summary>
    [Fact]
    public async Task CancellingANeverSettlingExportReleasesTheLatchAndReportsCancelled()
    {
        var harness = await CreateHarnessWithDocumentAsync();
        harness.FilePicker.GenericSavePath = Path.Combine("exports", "hung.pdf");
        var neverSettles = new TaskCompletionSource<ExportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Exporter.NextTask = neverSettles.Task;

        var export = harness.ViewModel.ExportPdfCommand.ExecuteAsync(null);
        await harness.Exporter.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Still latched during a genuine in-flight export: no re-entrancy hole.
        Assert.True(harness.ViewModel.IsExportBusy);
        AssertExportCommandsCanExecute(harness.ViewModel, expected: false);
        // ...but the escape hatch is reachable, which is the whole point.
        Assert.True(harness.ViewModel.OpenAppExportCommand.CanExecute(null));
        Assert.True(harness.ViewModel.CancelExportCommand.CanExecute(null));

        harness.ViewModel.CancelExportCommand.Execute(null);
        await export.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(harness.ViewModel.IsExportBusy);
        AssertExportCommandsCanExecute(harness.ViewModel, expected: true);
        Assert.False(harness.ViewModel.CancelExportCommand.CanExecute(null));
        // Only-forward: a cancelled export still REPORTS. Releasing the latch must
        // not buy silence -- the user is told nothing was saved.
        Assert.True(harness.ViewModel.IsExportFailureNoticeVisible);
        Assert.Contains("cancelled", harness.ViewModel.ExportFailureNoticeGuidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelExportIsInertWhenNoExportIsRunning()
    {
        var harness = CreateHarness();

        Assert.False(harness.ViewModel.CancelExportCommand.CanExecute(null));
        harness.ViewModel.CancelExportCommand.Execute(null);

        Assert.False(harness.ViewModel.IsExportBusy);
    }

    [Fact]
    public async Task ExportCommandsTrackDocumentAndReadingModeState()
    {
        var harness = CreateHarness();
        AssertExportCommandsCanExecute(harness.ViewModel, expected: false);

        await OpenDocumentAsync(harness, "# Open");
        AssertExportCommandsCanExecute(harness.ViewModel, expected: true);

        harness.ViewModel.IsEditMode = true;
        Assert.False(harness.ViewModel.ShowsAppMenuControl);
        AssertExportCommandsCanExecute(harness.ViewModel, expected: false);

        harness.ViewModel.IsEditMode = false;
        AssertExportCommandsCanExecute(harness.ViewModel, expected: true);

        harness.ViewModel.CloseFileCommand.Execute(null);
        AssertExportCommandsCanExecute(harness.ViewModel, expected: false);
    }

    private static void AssertExportCommandsCanExecute(MainWindowViewModel viewModel, bool expected)
    {
        Assert.Equal(expected, viewModel.ExportPdfCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.ExportHtmlCommand.CanExecute(null));
        Assert.Equal(expected, viewModel.PrintCommand.CanExecute(null));
    }

    private static async Task<ExportHarness> CreateHarnessWithDocumentAsync(string markdown = "# Document")
    {
        var harness = CreateHarness();
        await OpenDocumentAsync(harness, markdown);
        return harness;
    }

    private static async Task OpenDocumentAsync(ExportHarness harness, string markdown)
    {
        const string path = "C:\\docs\\document.md";
        harness.Loader.Sources[path] = new MarkdownSource(path, "document.md", markdown);
        await harness.ViewModel.OpenPathAsync(path);
    }

    private static ExportHarness CreateHarness()
    {
        var loader = new StubDocumentLoader();
        var picker = new StubFilePicker();
        var exporter = new RecordingDocumentExporter();
        var viewModel = new MainWindowViewModel(
            new OpenDocumentUseCase(loader),
            new SaveDocumentUseCase(new RecordingDocumentSaver()),
            picker,
            new StubCommandLineActivation(),
            new LocalizationService(AppLanguage.English),
            new InMemorySettingsStore(),
            new RecordingThemeService(),
            new RecordingStartupMetrics(),
            new RenderMarkdownDocumentUseCase(new TestMarkdownRenderer()),
            new StubUpdateService(),
            new MarkdigTableCellSourceEditor(),
            documentExporter: exporter);

        return new ExportHarness(loader, picker, exporter, viewModel);
    }

    private sealed record ExportHarness(
        StubDocumentLoader Loader,
        StubFilePicker FilePicker,
        RecordingDocumentExporter Exporter,
        MainWindowViewModel ViewModel);

    private sealed class RecordingDocumentExporter : IDocumentExporter
    {
        public List<ExportCall> Calls { get; } = [];
        public ExportResult NextResult { get; set; } = new(ExportStatus.Success);
        public Task<ExportResult>? NextTask { get; set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource CallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExportResult> ExportHtmlAsync(
            string destinationPath,
            string markdownSource,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("html", destinationPath, markdownSource), cancellationToken);

        public Task<ExportResult> ExportPdfAsync(
            string destinationPath,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("pdf", destinationPath, null), cancellationToken);

        public Task<ExportResult> ExportPngAsync(
            string destinationPath,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("png", destinationPath, null), cancellationToken);

        public Task<ExportResult> ShowPrintDialogAsync(CancellationToken cancellationToken = default)
            => Record(new ExportCall("print", null, null), cancellationToken);

        private Task<ExportResult> Record(ExportCall call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            LastToken = cancellationToken;
            CallStarted.TrySetResult();
            if (NextTask is null)
            {
                return Task.FromResult(NextResult);
            }

            // Mirror ExportPdfCoreAsync/ShowPrintDialogCoreAsync: a cancelled
            // export surfaces as an ExportStatus.Cancelled RESULT rather than a
            // thrown OperationCanceledException. Honouring the token here is what
            // lets a gated task stand in for a renderer barrier that never settles
            // on its own -- the shape of the filed defect.
            var settled = new TaskCompletionSource<ExportResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(
                () => settled.TrySetResult(new ExportResult(ExportStatus.Cancelled)));
            _ = NextTask.ContinueWith(
                completed =>
                {
                    registration.Dispose();
                    settled.TrySetResult(completed.Result);
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            return settled.Task;
        }
    }

    private sealed record ExportCall(string Kind, string? Path, string? MarkdownSource);
}
