using System.Reflection;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Application.Abstractions;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

public sealed class ApplicateDocumentExporterTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void ApplicationContractExposesFourOperationsAndAllPhaseOneStatuses()
    {
        var operations = typeof(IDocumentExporter)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(static method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["ExportHtmlAsync", "ExportPdfAsync", "ExportPngAsync", "ShowPrintDialogAsync"],
            operations);
        Assert.Equal(
            [
                "Cancelled", "CaptureFailed", "Deferred", "Faulted", "NoDocument", "PrintReturnedFalse",
                "ProcessCrashed", "RenderIncomplete", "Success", "WriteFailed",
            ],
            Enum.GetNames<ExportStatus>().Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "HtmlExport_BarrierPrecedesCapture")]
    public async Task HtmlExportBarrierPrecedesCaptureAndStaticRendererIsNotUsed()
    {
        var exporter = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MarkMello.Applicate.Desktop", "Rendering", "ApplicateDocumentExporter.cs"));

        var barrier = exporter.IndexOf("PrepareForExportAsync", StringComparison.Ordinal);
        var capture = exporter.IndexOf("CaptureRenderedHtmlAsync", StringComparison.Ordinal);
        var save = exporter.IndexOf("_saver.SaveAsync", StringComparison.Ordinal);

        Assert.True(barrier >= 0, "HTML export must await the existing full-render barrier.");
        Assert.True(capture > barrier, "HTML capture must be requested only after the barrier.");
        Assert.True(save > capture, "The saver must receive HTML only after successful capture.");
        Assert.DoesNotContain("_renderer.RenderAsync", exporter, StringComparison.Ordinal);

        var order = new List<string>();
        var barrierGate = new TaskCompletionSource<ApplicateFullRenderResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new RecordingRenderer("unused");
        var saver = new RecordingSaver();
        var coordinator = CreateExporter(
            renderer,
            saver,
            prepareForExport: _ =>
            {
                order.Add("barrier");
                return barrierGate.Task;
            },
            captureRenderedHtml: _ =>
            {
                order.Add("capture");
                return Task.FromResult(new ApplicateRenderedHtmlCaptureResult(
                    ExportStatus.Success,
                    Html: "<!DOCTYPE html>\n<html>captured</html>"));
            });

        var export = coordinator.ExportHtmlAsync("output.html", "# source");
        Assert.Equal(["barrier"], order);
        Assert.Equal(0, saver.CallCount);

        barrierGate.SetResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed));
        var result = await export;

        Assert.Equal(ExportStatus.Success, result.Status);
        Assert.Equal(["barrier", "capture"], order);
        Assert.Equal(1, saver.CallCount);
        Assert.Equal(0, renderer.CallCount);
    }

    [Fact]
    public void PublicExporterConstructorResolvesHtmlCaptureThroughViewerHost()
    {
        var publicConstructor = Assert.Single(
            typeof(ApplicateDocumentExporter).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        var parameterTypes = publicConstructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [typeof(IDocumentSaver), typeof(IApplicateSharedWebViewHostProvider)],
            parameterTypes);

        var exporter = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MarkMello.Applicate.Desktop", "Rendering", "ApplicateDocumentExporter.cs"));
        Assert.Contains("ViewerHost.View.PrepareForExportAsync", exporter, StringComparison.Ordinal);
        Assert.Contains("ViewerHost.View.CaptureRenderedHtmlAsync", exporter, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "HtmlExport_UnsuccessfulCaptureNeverSaves")]
    public async Task HtmlExportUnsuccessfulCaptureNeverSaves()
    {
        var failures = new[]
        {
            new ApplicateRenderedHtmlCaptureResult(
                ExportStatus.CaptureFailed,
                FailureId: "HTMLX-IPC-SHAPE",
                Reason: "malformed"),
            new ApplicateRenderedHtmlCaptureResult(
                ExportStatus.ProcessCrashed,
                FailureId: "HTMLX-PROCESS-FAILED",
                Reason: "process failed"),
            new ApplicateRenderedHtmlCaptureResult(
                ExportStatus.Cancelled,
                FailureId: "HTMLX-CANCELLED",
                Reason: "cancelled"),
            new ApplicateRenderedHtmlCaptureResult(
                ExportStatus.Faulted,
                FailureId: "HTMLX-DISPOSED",
                Reason: "disposed"),
        };

        foreach (var failure in failures)
        {
            var saver = new RecordingSaver();
            var exporter = CreateExporter(
                new RecordingRenderer("unused"),
                saver,
                captureRenderedHtml: _ => Task.FromResult(failure));

            var result = await exporter.ExportHtmlAsync("output.html", "# source");

            Assert.Equal(failure.Status, result.Status);
            Assert.Equal(0, saver.CallCount);
        }
    }

    [Theory]
    [InlineData("Completed", 2, ExportStatus.RenderIncomplete, "HTMLX-BARRIER-INCOMPLETE")]
    [InlineData("RendererFailed", 0, ExportStatus.RenderIncomplete, "HTMLX-BARRIER-FAILED")]
    [InlineData("ProcessFailed", 0, ExportStatus.ProcessCrashed, "HTMLX-PROCESS-FAILED")]
    [InlineData("Cancelled", 0, ExportStatus.Cancelled, "HTMLX-CANCELLED")]
    [InlineData("Disposed", 0, ExportStatus.Faulted, "HTMLX-DISPOSED")]
    public async Task HtmlExportBarrierFailuresMapExactlyAndNeverCaptureOrSave(
        string barrierStatusName,
        int mermaidErrorCount,
        ExportStatus expectedStatus,
        string expectedFailureId)
    {
        var captureCalls = 0;
        var saver = new RecordingSaver();
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            saver,
            prepareForExport: _ => Task.FromResult(new ApplicateFullRenderResult(
                Enum.Parse<ApplicateFullRenderStatus>(barrierStatusName),
                mermaidErrorCount,
                "barrier reason")),
            captureRenderedHtml: _ =>
            {
                captureCalls++;
                return Task.FromResult(new ApplicateRenderedHtmlCaptureResult(
                    ExportStatus.Success,
                    Html: "must not capture"));
            });

        var result = await exporter.ExportHtmlAsync("output.html", "# source");

        Assert.Equal(expectedStatus, result.Status);
        Assert.StartsWith(expectedFailureId, result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, captureCalls);
        Assert.Equal(0, saver.CallCount);
    }

    [Fact(DisplayName = "Capture_DocumentSwitchFailsWithoutSave")]
    public async Task CaptureDocumentSwitchFailsWithoutSave()
    {
        var saver = new RecordingSaver();
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            saver,
            captureRenderedHtml: _ => Task.FromResult(new ApplicateRenderedHtmlCaptureResult(
                ExportStatus.CaptureFailed,
                FailureId: "HTMLX-DOCUMENT-CHANGED",
                Reason: "HTMLX-DOCUMENT-CHANGED")));

        var result = await exporter.ExportHtmlAsync("output.html", "# switched");

        Assert.Equal(ExportStatus.CaptureFailed, result.Status);
        Assert.Equal("HTMLX-DOCUMENT-CHANGED", result.Detail);
        Assert.Equal(0, saver.CallCount);
    }

    [Fact(DisplayName = "ExistingPdfPrint_PreparationContractsRemainGreen")]
    public async Task ExistingPdfPrintPreparationContractsRemainGreen()
    {
        var captureCalls = 0;
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            captureRenderedHtml: _ =>
            {
                captureCalls++;
                return Task.FromResult(new ApplicateRenderedHtmlCaptureResult(
                    ExportStatus.CaptureFailed,
                    FailureId: "must-not-run"));
            },
            exportPdf: (_, _) => Task.FromResult(new ExportResult(ExportStatus.Success)),
            showPrint: _ => Task.FromResult(new ExportResult(ExportStatus.Success)));

        Assert.Equal(ExportStatus.Success, (await exporter.ExportPdfAsync("output.pdf")).Status);
        Assert.Equal(ExportStatus.Success, (await exporter.ShowPrintDialogAsync()).Status);
        Assert.Equal(0, captureCalls);
    }

    [Fact]
    public void DesktopWiringTargetsViewerAndUsesPinnedNativePrintSurface()
    {
        var exporter = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MarkMello.Applicate.Desktop", "Rendering", "ApplicateDocumentExporter.cs"));
        var view = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MarkMello.Applicate.Desktop", "Views", "ApplicateWebMarkdownDocumentView.cs"));
        var program = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MarkMello.Applicate.Desktop", "Program.cs"));

        Assert.Contains("ViewerHost.View", exporter, StringComparison.Ordinal);
        Assert.DoesNotContain("EditPreviewHost", exporter, StringComparison.Ordinal);
        Assert.Contains("PrepareForExportAsync", view, StringComparison.Ordinal);
        Assert.Contains("_nativeUiCore.Environment.CreatePrintSettings()", view, StringComparison.Ordinal);
        Assert.Contains("ShouldPrintBackgrounds = true", view, StringComparison.Ordinal);
        Assert.Contains("ShouldPrintHeaderAndFooter = false", view, StringComparison.Ordinal);
        Assert.Contains("MarginTop = 0.4", view, StringComparison.Ordinal);
        Assert.Contains("MarginBottom = 0.4", view, StringComparison.Ordinal);
        Assert.Contains("MarginLeft = 0.4", view, StringComparison.Ordinal);
        Assert.Contains("MarginRight = 0.4", view, StringComparison.Ordinal);
        Assert.Contains(
            "core.PrintToPdfAsync(Path.GetFullPath(destinationPath), settings)",
            view,
            StringComparison.Ordinal);
        Assert.Contains("ShowPrintUI()", view, StringComparison.Ordinal);

        var exporterRegistration = program.IndexOf(
            "AddSingleton<IDocumentExporter, ApplicateDocumentExporter>()", StringComparison.Ordinal);
        var presentationRegistration = program.IndexOf("AddPresentation()", StringComparison.Ordinal);
        Assert.True(exporterRegistration >= 0);
        Assert.True(exporterRegistration < presentationRegistration);
    }

    [Fact]
    public async Task ExportHtmlWritesExactCapturedHtmlAfterBarrier()
    {
        var preferences = ReadingPreferences.Default with { FontSize = 23 };
        var resolver = new StubImageSourceResolver();
        var renderer = new RecordingRenderer("<html>exact bytes</html>");
        var saver = new RecordingSaver();
        var exporter = CreateExporter(
            renderer,
            saver,
            new MarkdownSource("C:\\docs\\input.md", "input.md", "old"),
            preferences,
            resolver);

        var result = await exporter.ExportHtmlAsync("C:\\exports\\output.html", "# current");

        Assert.Equal(ExportStatus.Success, result.Status);
        Assert.Equal("<html>exact bytes</html>", saver.Content);
        Assert.Equal("C:\\exports\\output.html", saver.Path);
        Assert.Equal("old", renderer.Source?.Content);
        Assert.Equal("C:\\docs\\input.md", renderer.Source?.Path);
        Assert.Same(preferences, renderer.Preferences);
        Assert.Same(resolver, renderer.ImageSourceResolver);
    }

    [Fact]
    public async Task ExportHtmlCancellationDoesNotRenderOrWrite()
    {
        var renderer = new RecordingRenderer("unused");
        var saver = new RecordingSaver();
        var exporter = CreateExporter(renderer, saver);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await exporter.ExportHtmlAsync("output.html", "# cancelled", cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(0, renderer.CallCount);
        Assert.Equal(0, saver.CallCount);
    }

    [Fact]
    public async Task ExportHtmlMapsRendererStageCancellationWithoutWriting()
    {
        using var cancellation = new CancellationTokenSource();
        var renderer = new RecordingRenderer("unused")
        {
            BeforeRender = token =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            },
        };
        var saver = new RecordingSaver();
        var exporter = CreateExporter(renderer, saver);

        var result = await exporter.ExportHtmlAsync(
            "output.html",
            "# cancelled while rendering",
            cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(1, renderer.CallCount);
        Assert.Equal(0, saver.CallCount);
    }

    [Fact]
    public async Task ExportHtmlMapsSaverStageCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var renderer = new RecordingRenderer("rendered");
        var saver = new RecordingSaver
        {
            BeforeSave = token =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            },
        };
        var exporter = CreateExporter(renderer, saver);

        var result = await exporter.ExportHtmlAsync(
            "output.html",
            "# cancelled while saving",
            cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(1, renderer.CallCount);
        Assert.Equal(1, saver.CallCount);
    }

    [Fact]
    public async Task ExportHtmlMapsSaverFailureWithoutSwallowingDetails()
    {
        var failure = new IOException("disk full");
        var saver = new RecordingSaver { Exception = failure };
        var exporter = CreateExporter(new RecordingRenderer("rendered"), saver);

        var result = await exporter.ExportHtmlAsync("output.html", "# source");

        Assert.Equal(ExportStatus.WriteFailed, result.Status);
        Assert.Equal("HTMLX-SAVE-FAILED: disk full", result.Detail);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task ExportHtmlMapsRendererFaultAndMissingViewerDocument()
    {
        var failure = new InvalidOperationException("render failed");
        var renderer = new RecordingRenderer("unused") { Exception = failure };
        var faulted = await CreateExporter(renderer, new RecordingSaver())
            .ExportHtmlAsync("output.html", "# source");
        var noDocument = await CreateExporter(
                new RecordingRenderer("unused"),
                new RecordingSaver(),
                hasDocument: false)
            .ExportHtmlAsync("output.html", "# source");

        Assert.Equal(ExportStatus.CaptureFailed, faulted.Status);
        Assert.Same(failure, faulted.Error);
        Assert.Equal(ExportStatus.NoDocument, noDocument.Status);
    }

    [Fact]
    public async Task ExporterDelegatesPdfAndPrintToViewerAndDefersPng()
    {
        var pdfCalled = false;
        var printCalled = false;
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            exportPdf: (path, _) =>
            {
                pdfCalled = path == "output.pdf";
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            },
            showPrint: _ =>
            {
                printCalled = true;
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            });

        Assert.Equal(ExportStatus.Success, (await exporter.ExportPdfAsync("output.pdf")).Status);
        Assert.Equal(ExportStatus.Success, (await exporter.ShowPrintDialogAsync()).Status);
        Assert.Equal(ExportStatus.Deferred, (await exporter.ExportPngAsync("output.png")).Status);
        Assert.True(pdfCalled);
        Assert.True(printCalled);
    }

    [Fact]
    public async Task ExporterReturnsNoDocumentBeforePdfOrPrintDelegates()
    {
        var delegateCalls = 0;
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            hasDocument: false,
            exportPdf: (_, _) =>
            {
                delegateCalls++;
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            },
            showPrint: _ =>
            {
                delegateCalls++;
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            });

        Assert.Equal(ExportStatus.NoDocument, (await exporter.ExportPdfAsync("output.pdf")).Status);
        Assert.Equal(ExportStatus.NoDocument, (await exporter.ShowPrintDialogAsync()).Status);
        Assert.Equal(0, delegateCalls);
    }

    [Fact]
    public async Task ExporterReturnsPreCancelledBeforePdfDelegate()
    {
        var delegateCalls = 0;
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            exportPdf: (_, _) =>
            {
                delegateCalls++;
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await exporter.ExportPdfAsync("output.pdf", cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(0, delegateCalls);
    }

    [Fact]
    public async Task ExporterReturnsPreCancelledBeforePrintDelegate()
    {
        var delegateCalls = 0;
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            showPrint: _ =>
            {
                delegateCalls++;
                return Task.FromResult(new ExportResult(ExportStatus.Success));
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await exporter.ShowPrintDialogAsync(cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(0, delegateCalls);
    }

    [Fact]
    public async Task ExporterMapsPdfDelegateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            exportPdf: (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<ExportResult>(cancellation.Token);
            });

        var result = await exporter.ExportPdfAsync("output.pdf", cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ExporterMapsPrintDelegateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            showPrint: _ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<ExportResult>(cancellation.Token);
            });

        var result = await exporter.ShowPrintDialogAsync(cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task ExporterMapsPdfDelegateFaultWithoutSwallowingDetails()
    {
        var failure = new IOException("pdf delegate failed");
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            exportPdf: (_, _) => Task.FromException<ExportResult>(failure));

        var result = await exporter.ExportPdfAsync("output.pdf");

        Assert.Equal(ExportStatus.Faulted, result.Status);
        Assert.Equal("pdf delegate failed", result.Detail);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task ExporterMapsPrintDelegateFaultWithoutSwallowingDetails()
    {
        var failure = new InvalidOperationException("print delegate failed");
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            showPrint: _ => Task.FromException<ExportResult>(failure));

        var result = await exporter.ShowPrintDialogAsync();

        Assert.Equal(ExportStatus.Faulted, result.Status);
        Assert.Equal("print delegate failed", result.Detail);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task ExporterReturnsTypedPdfResultUnchanged()
    {
        var expected = new ExportResult(ExportStatus.RenderIncomplete, "renderer incomplete");
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            exportPdf: (_, _) => Task.FromResult(expected));

        var result = await exporter.ExportPdfAsync("output.pdf");

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task ExporterReturnsTypedPrintResultUnchanged()
    {
        var expected = new ExportResult(ExportStatus.PrintReturnedFalse, "print busy");
        var exporter = CreateExporter(
            new RecordingRenderer("unused"),
            new RecordingSaver(),
            showPrint: _ => Task.FromResult(expected));

        var result = await exporter.ShowPrintDialogAsync();

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task PdfCoreRunsBarrierBeforeNativeAndMapsFalse()
    {
        var order = new List<string>();
        var result = await ApplicateWebMarkdownDocumentView.ExportPdfCoreAsync(
            "output.pdf",
            _ =>
            {
                order.Add("barrier");
                return Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed));
            },
            (path, _) =>
            {
                order.Add($"pdf:{path}");
                return Task.FromResult(false);
            },
            CancellationToken.None);

        Assert.Equal(["barrier", "pdf:output.pdf"], order);
        Assert.Equal(ExportStatus.PrintReturnedFalse, result.Status);
    }

    [Fact]
    public async Task PdfCoreMapsNativeTrueToSuccess()
    {
        var result = await ApplicateWebMarkdownDocumentView.ExportPdfCoreAsync(
            "output.pdf",
            _ => Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed)),
            (path, _) => Task.FromResult(path == "output.pdf"),
            CancellationToken.None);

        Assert.Equal(ExportStatus.Success, result.Status);
    }

    [Fact]
    public async Task PrintCoreRunsBarrierBeforeParameterlessNativeCall()
    {
        var order = new List<string>();
        var result = await ApplicateWebMarkdownDocumentView.ShowPrintDialogCoreAsync(
            _ =>
            {
                order.Add("barrier");
                return Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed));
            },
            () => order.Add("print"),
            CancellationToken.None);

        Assert.Equal(["barrier", "print"], order);
        Assert.Equal(ExportStatus.Success, result.Status);
    }

    [Theory]
    [InlineData((int)ApplicateFullRenderStatus.RendererFailed, 0, ExportStatus.RenderIncomplete)]
    [InlineData((int)ApplicateFullRenderStatus.ProcessFailed, 0, ExportStatus.ProcessCrashed)]
    [InlineData((int)ApplicateFullRenderStatus.Cancelled, 0, ExportStatus.Cancelled)]
    [InlineData((int)ApplicateFullRenderStatus.Disposed, 0, ExportStatus.Faulted)]
    [InlineData((int)ApplicateFullRenderStatus.Completed, 2, ExportStatus.RenderIncomplete)]
    public async Task PrintCoreMapsEveryNonSuccessfulBarrierTerminalWithoutOpeningUi(
        int barrierStatusValue,
        int mermaidErrorCount,
        ExportStatus expectedStatus)
    {
        var barrierStatus = (ApplicateFullRenderStatus)barrierStatusValue;
        var nativeCalls = 0;
        var result = await ApplicateWebMarkdownDocumentView.ShowPrintDialogCoreAsync(
            _ => Task.FromResult(new ApplicateFullRenderResult(barrierStatus, mermaidErrorCount, "detail")),
            () => nativeCalls++,
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, nativeCalls);
        Assert.Equal("detail", result.Detail);
    }

    [Fact]
    public async Task PrintCoreMapsNativeFaultWithoutSwallowingDetails()
    {
        var failure = new InvalidOperationException("print UI fault");
        var result = await ApplicateWebMarkdownDocumentView.ShowPrintDialogCoreAsync(
            _ => Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed)),
            () => throw failure,
            CancellationToken.None);

        Assert.Equal(ExportStatus.Faulted, result.Status);
        Assert.Equal("print UI fault", result.Detail);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public async Task PrintCoreMapsBarrierFaultWithoutOpeningUi()
    {
        var failure = new InvalidOperationException("barrier fault");
        var nativeCalls = 0;
        var result = await ApplicateWebMarkdownDocumentView.ShowPrintDialogCoreAsync(
            _ => Task.FromException<ApplicateFullRenderResult>(failure),
            () => nativeCalls++,
            CancellationToken.None);

        Assert.Equal(ExportStatus.Faulted, result.Status);
        Assert.Same(failure, result.Error);
        Assert.Equal(0, nativeCalls);
    }

    [Fact]
    public async Task PrintCoreSuppressesNativeWhenCancellationWinsAfterBarrier()
    {
        using var cancellation = new CancellationTokenSource();
        var nativeCalls = 0;
        var result = await ApplicateWebMarkdownDocumentView.ShowPrintDialogCoreAsync(
            _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed));
            },
            () => nativeCalls++,
            cancellation.Token);

        Assert.Equal(ExportStatus.Cancelled, result.Status);
        Assert.Equal(0, nativeCalls);
    }

    [Theory]
    [InlineData((int)ApplicateFullRenderStatus.RendererFailed, 0, ExportStatus.RenderIncomplete)]
    [InlineData((int)ApplicateFullRenderStatus.ProcessFailed, 0, ExportStatus.ProcessCrashed)]
    [InlineData((int)ApplicateFullRenderStatus.Cancelled, 0, ExportStatus.Cancelled)]
    [InlineData((int)ApplicateFullRenderStatus.Disposed, 0, ExportStatus.Faulted)]
    [InlineData((int)ApplicateFullRenderStatus.Completed, 2, ExportStatus.RenderIncomplete)]
    public async Task PdfCoreMapsEveryNonSuccessfulBarrierTerminal(
        int barrierStatusValue,
        int mermaidErrorCount,
        ExportStatus expectedStatus)
    {
        var barrierStatus = (ApplicateFullRenderStatus)barrierStatusValue;
        var nativeCalls = 0;
        var result = await ApplicateWebMarkdownDocumentView.ExportPdfCoreAsync(
            "output.pdf",
            _ => Task.FromResult(new ApplicateFullRenderResult(barrierStatus, mermaidErrorCount, "detail")),
            (_, _) =>
            {
                nativeCalls++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(0, nativeCalls);
        Assert.Equal("detail", result.Detail);
    }

    [Fact]
    public async Task PdfCoreMapsBarrierAndNativeExceptionsToFaulted()
    {
        var barrierFailure = new InvalidOperationException("barrier fault");
        var nativeFailure = new IOException("pdf fault");

        var barrierResult = await ApplicateWebMarkdownDocumentView.ExportPdfCoreAsync(
            "output.pdf",
            _ => Task.FromException<ApplicateFullRenderResult>(barrierFailure),
            (_, _) => Task.FromResult(true),
            CancellationToken.None);
        var nativeResult = await ApplicateWebMarkdownDocumentView.ExportPdfCoreAsync(
            "output.pdf",
            _ => Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed)),
            (_, _) => Task.FromException<bool>(nativeFailure),
            CancellationToken.None);

        Assert.Equal(ExportStatus.Faulted, barrierResult.Status);
        Assert.Same(barrierFailure, barrierResult.Error);
        Assert.Equal(ExportStatus.Faulted, nativeResult.Status);
        Assert.Same(nativeFailure, nativeResult.Error);
    }

    [Fact]
    public async Task ExportHtmlFailsAndNeverSavesWhenDocumentChangesDuringExport()
    {
        // The live reading view switches from A to B during the barrier/capture window
        // (the exporter reads the source once at start, once again before save).
        var docA = new MarkdownSource("A.md", "A.md", "aaa");
        var docB = new MarkdownSource("B.md", "B.md", "bbb");
        var reads = 0;
        var saver = new RecordingSaver();
        var exporter = new ApplicateDocumentExporter(
            saver,
            () => ++reads == 1 ? docA : docB,
            _ => Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed)),
            _ => Task.FromResult(new ApplicateRenderedHtmlCaptureResult(ExportStatus.Success, Html: "<html></html>")),
            (_, _) => Task.FromResult(new ExportResult(ExportStatus.Success)),
            _ => Task.FromResult(new ExportResult(ExportStatus.Success)));

        var result = await exporter.ExportHtmlAsync("out.html", "aaa", CancellationToken.None);

        Assert.Equal(ExportStatus.CaptureFailed, result.Status);
        Assert.StartsWith("HTMLX-EXPORT-DOCUMENT-CHANGED", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, saver.CallCount);
    }

    [Fact]
    public async Task ExportHtmlMapsNonIoSaverFailureToWriteFailedNotCaptureDelivery()
    {
        var source = new MarkdownSource("A.md", "A.md", "aaa");
        var saver = new RecordingSaver { Exception = new InvalidOperationException("gremlin") };
        var exporter = new ApplicateDocumentExporter(
            saver,
            () => source,
            _ => Task.FromResult(new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed)),
            _ => Task.FromResult(new ApplicateRenderedHtmlCaptureResult(ExportStatus.Success, Html: "<html></html>")),
            (_, _) => Task.FromResult(new ExportResult(ExportStatus.Success)),
            _ => Task.FromResult(new ExportResult(ExportStatus.Success)));

        var result = await exporter.ExportHtmlAsync("out.html", "aaa", CancellationToken.None);

        Assert.Equal(ExportStatus.WriteFailed, result.Status);
        Assert.StartsWith("HTMLX-SAVE-FAILED", result.Detail, StringComparison.Ordinal);
    }

    private static ApplicateDocumentExporter CreateExporter(
        IApplicateHtmlMarkdownRenderer renderer,
        IDocumentSaver saver,
        MarkdownSource? source = null,
        ReadingPreferences? preferences = null,
        IImageSourceResolver? resolver = null,
        bool hasDocument = true,
        Func<string, CancellationToken, Task<ExportResult>>? exportPdf = null,
        Func<CancellationToken, Task<ExportResult>>? showPrint = null,
        Func<CancellationToken, Task<ApplicateFullRenderResult>>? prepareForExport = null,
        Func<CancellationToken, Task<ApplicateRenderedHtmlCaptureResult>>? captureRenderedHtml = null)
    {
        source = hasDocument
            ? source ?? new MarkdownSource("input.md", "input.md", "old")
            : null;
        return new ApplicateDocumentExporter(
            saver,
            () => source,
            prepareForExport ?? (_ => Task.FromResult(
                new ApplicateFullRenderResult(ApplicateFullRenderStatus.Completed))),
            captureRenderedHtml ?? (async cancellationToken =>
                {
                    var rendered = await renderer.RenderAsync(
                        source!,
                        preferences ?? ReadingPreferences.Default,
                        resolver,
                        cancellationToken);
                    return new ApplicateRenderedHtmlCaptureResult(
                        ExportStatus.Success,
                        Html: rendered.Html);
                }),
            exportPdf ?? ((_, _) => Task.FromResult(new ExportResult(ExportStatus.Success))),
            showPrint ?? (_ => Task.FromResult(new ExportResult(ExportStatus.Success))));
    }

    private sealed class RecordingRenderer(string html) : IApplicateHtmlMarkdownRenderer
    {
        public int CallCount { get; private set; }
        public MarkdownSource? Source { get; private set; }
        public ReadingPreferences? Preferences { get; private set; }
        public IImageSourceResolver? ImageSourceResolver { get; private set; }
        public Exception? Exception { get; init; }
        public Action<CancellationToken>? BeforeRender { get; init; }

        public Task<ApplicateHtmlDocument> RenderAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Source = source;
            Preferences = preferences;
            ImageSourceResolver = imageSourceResolver;
            BeforeRender?.Invoke(cancellationToken);
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(new ApplicateHtmlDocument(html, string.Empty, [], []));
        }

        public Task<ApplicateRenderedBody> RenderBodyAsync(
            MarkdownSource source,
            ReadingPreferences preferences,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> RenderTableCellHtmlAsync(
            string rawCellMarkdown,
            IImageSourceResolver? imageSourceResolver,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSaver : IDocumentSaver
    {
        public int CallCount { get; private set; }
        public string? Path { get; private set; }
        public string? Content { get; private set; }
        public Exception? Exception { get; init; }
        public Action<CancellationToken>? BeforeSave { get; init; }

        public Task SaveAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Path = path;
            Content = content;
            BeforeSave?.Invoke(cancellationToken);
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }

    private sealed class StubImageSourceResolver : IImageSourceResolver
    {
        public Task<Stream?> TryOpenAsync(
            string url,
            string? baseDirectory,
            CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(null);
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MarkMello.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the MarkMello repository root.");
    }
}
