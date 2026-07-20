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

        Assert.Equal(ViewState.LoadError, harness.ViewModel.State);
        Assert.Equal("Export failed", harness.ViewModel.ErrorTitle);
        Assert.Contains(status.ToString(), harness.ViewModel.ErrorDetails, StringComparison.Ordinal);
        Assert.Contains("detail-X", harness.ViewModel.ErrorDetails, StringComparison.Ordinal);
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
        public TaskCompletionSource CallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExportResult> ExportHtmlAsync(
            string destinationPath,
            string markdownSource,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("html", destinationPath, markdownSource));

        public Task<ExportResult> ExportPdfAsync(
            string destinationPath,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("pdf", destinationPath, null));

        public Task<ExportResult> ExportPngAsync(
            string destinationPath,
            CancellationToken cancellationToken = default)
            => Record(new ExportCall("png", destinationPath, null));

        public Task<ExportResult> ShowPrintDialogAsync(CancellationToken cancellationToken = default)
            => Record(new ExportCall("print", null, null));

        private Task<ExportResult> Record(ExportCall call)
        {
            Calls.Add(call);
            CallStarted.TrySetResult();
            return NextTask ?? Task.FromResult(NextResult);
        }
    }

    private sealed record ExportCall(string Kind, string? Path, string? MarkdownSource);
}
