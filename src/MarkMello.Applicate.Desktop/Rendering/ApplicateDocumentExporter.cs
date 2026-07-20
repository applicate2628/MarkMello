using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Views;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateDocumentExporter : IDocumentExporter
{
    private readonly IDocumentSaver _saver;
    private readonly Func<MarkdownSource?> _source;
    private readonly Func<CancellationToken, Task<ApplicateFullRenderResult>> _prepareForExport;
    private readonly Func<CancellationToken, Task<ApplicateRenderedHtmlCaptureResult>> _captureRenderedHtml;
    private readonly Func<string, CancellationToken, Task<ExportResult>> _exportPdf;
    private readonly Func<CancellationToken, Task<ExportResult>> _showPrint;

    public ApplicateDocumentExporter(
        IDocumentSaver saver,
        IApplicateSharedWebViewHostProvider hostProvider)
        : this(
            saver,
            () => hostProvider.ViewerHost.View.Source,
            cancellationToken => hostProvider.ViewerHost.View.PrepareForExportAsync(cancellationToken),
            cancellationToken => hostProvider.ViewerHost.View.CaptureRenderedHtmlAsync(cancellationToken),
            (path, cancellationToken) => hostProvider.ViewerHost.View.ExportPdfAsync(path, cancellationToken),
            cancellationToken => hostProvider.ViewerHost.View.ShowPrintDialogAsync(cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(hostProvider);
    }

    internal ApplicateDocumentExporter(
        IDocumentSaver saver,
        Func<MarkdownSource?> source,
        Func<CancellationToken, Task<ApplicateFullRenderResult>> prepareForExport,
        Func<CancellationToken, Task<ApplicateRenderedHtmlCaptureResult>> captureRenderedHtml,
        Func<string, CancellationToken, Task<ExportResult>> exportPdf,
        Func<CancellationToken, Task<ExportResult>> showPrint)
    {
        ArgumentNullException.ThrowIfNull(saver);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepareForExport);
        ArgumentNullException.ThrowIfNull(captureRenderedHtml);
        ArgumentNullException.ThrowIfNull(exportPdf);
        ArgumentNullException.ThrowIfNull(showPrint);

        _saver = saver;
        _source = source;
        _prepareForExport = prepareForExport;
        _captureRenderedHtml = captureRenderedHtml;
        _exportPdf = exportPdf;
        _showPrint = showPrint;
    }

    public async Task<ExportResult> ExportHtmlAsync(
        string destinationPath,
        string markdownSource,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ExportResult(ExportStatus.Cancelled);
        }

        var currentSource = _source();
        if (currentSource is null)
        {
            return new ExportResult(ExportStatus.NoDocument);
        }

        try
        {
            _ = markdownSource;
            // WebView2 message delivery (barrier + capture) is a COM call that MUST run on
            // the UI thread. Keep the continuation on the captured UI context: a
            // ConfigureAwait(false) here would resume the capture call on a threadpool
            // thread, and the native post then throws COMException (HTMLX-CAPTURE-DELIVERY).
            var barrier = await _prepareForExport(cancellationToken).ConfigureAwait(true);
            var barrierFailure = MapHtmlBarrierFailure(barrier);
            if (barrierFailure is not null)
            {
                return barrierFailure;
            }

            var capture = await _captureRenderedHtml(cancellationToken).ConfigureAwait(true);
            if (capture.Status != ExportStatus.Success)
            {
                return new ExportResult(
                    capture.Status,
                    FormatFailureDetail(capture.FailureId, capture.Reason));
            }

            if (string.IsNullOrEmpty(capture.Html))
            {
                return new ExportResult(
                    ExportStatus.CaptureFailed,
                    "HTMLX-IPC-SHAPE: Captured HTML was empty.");
            }

            // Document-identity guard: if the live reading view switched to a DIFFERENT
            // document during the barrier/capture window, the captured HTML is for that
            // other document — never write it to the destination chosen for the original
            // one (silent wrong-content save). The renderer's own capture identity check
            // only covers the capture-request→settle window, not the full export span.
            var latestSource = _source();
            if (latestSource is null
                || !string.Equals(latestSource.Path, currentSource.Path, StringComparison.Ordinal)
                || !string.Equals(latestSource.FileName, currentSource.FileName, StringComparison.Ordinal))
            {
                return new ExportResult(
                    ExportStatus.CaptureFailed,
                    "HTMLX-EXPORT-DOCUMENT-CHANGED: the document changed during export; nothing was saved.");
            }

            try
            {
                await _saver.SaveAsync(destinationPath, capture.Html, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ExportResult(ExportStatus.Cancelled);
            }
            catch (Exception saveEx)
            {
                // ANY saver failure is a WRITE failure. Without this, a saver throwing an
                // exception outside {IOException, UnauthorizedAccessException,
                // ArgumentException} falls to the outer catch and is mis-reported as
                // HTMLX-CAPTURE-DELIVERY even though capture already succeeded.
                return new ExportResult(ExportStatus.WriteFailed, $"HTMLX-SAVE-FAILED: {saveEx.Message}", saveEx);
            }

            return new ExportResult(ExportStatus.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExportResult(ExportStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return ex is IOException or UnauthorizedAccessException or ArgumentException
                ? new ExportResult(ExportStatus.WriteFailed, $"HTMLX-SAVE-FAILED: {ex.Message}", ex)
                : new ExportResult(
                    ExportStatus.CaptureFailed,
                    $"HTMLX-CAPTURE-DELIVERY: {ex.Message}",
                    ex);
        }
    }

    public Task<ExportResult> ExportPdfAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
        => ExecuteViewerOperationAsync(
            () => _exportPdf(destinationPath, cancellationToken),
            cancellationToken);

    /// <summary>
    /// PNG export is not implemented and is not planned -- the feature was
    /// cancelled, so there is no phase that delivers it. The method and its
    /// <see cref="ExportStatus.Deferred"/> verdict stay so the
    /// <c>IDocumentExporter</c> contract remains total and any caller reaching
    /// this leg gets a typed refusal instead of a crash; no menu exposes it.
    /// </summary>
    public Task<ExportResult> ExportPngAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ExportResult(
            ExportStatus.Deferred,
            "PNG export is not implemented and is not planned."));

    public Task<ExportResult> ShowPrintDialogAsync(CancellationToken cancellationToken = default)
        => ExecuteViewerOperationAsync(
            () => _showPrint(cancellationToken),
            cancellationToken);

    private async Task<ExportResult> ExecuteViewerOperationAsync(
        Func<Task<ExportResult>> operation,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ExportResult(ExportStatus.Cancelled);
        }

        if (_source() is null)
        {
            return new ExportResult(ExportStatus.NoDocument);
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ExportResult(ExportStatus.Cancelled);
        }
        catch (Exception ex)
        {
            return new ExportResult(ExportStatus.Faulted, ex.Message, ex);
        }
    }

    private static ExportResult? MapHtmlBarrierFailure(ApplicateFullRenderResult barrier)
        => barrier.Status switch
        {
            ApplicateFullRenderStatus.Completed when barrier.MermaidErrorCount == 0 => null,
            ApplicateFullRenderStatus.Completed => new ExportResult(
                ExportStatus.RenderIncomplete,
                $"HTMLX-BARRIER-INCOMPLETE: {barrier.Reason ?? $"{barrier.MermaidErrorCount} Mermaid diagram(s) failed to render."}"),
            ApplicateFullRenderStatus.RendererFailed => new ExportResult(
                ExportStatus.RenderIncomplete,
                $"HTMLX-BARRIER-FAILED: {barrier.Reason}"),
            ApplicateFullRenderStatus.ProcessFailed => new ExportResult(
                ExportStatus.ProcessCrashed,
                $"HTMLX-PROCESS-FAILED: {barrier.Reason}"),
            ApplicateFullRenderStatus.Cancelled => new ExportResult(
                ExportStatus.Cancelled,
                "HTMLX-CANCELLED"),
            ApplicateFullRenderStatus.Disposed => new ExportResult(
                ExportStatus.Faulted,
                "HTMLX-DISPOSED"),
            _ => new ExportResult(
                ExportStatus.RenderIncomplete,
                "HTMLX-BARRIER-FAILED: unknown barrier result"),
        };

    private static string? FormatFailureDetail(string? failureId, string? reason)
    {
        if (string.IsNullOrEmpty(failureId))
        {
            return reason;
        }

        return string.IsNullOrEmpty(reason) || reason.StartsWith(failureId, StringComparison.Ordinal)
            ? reason ?? failureId
            : $"{failureId}: {reason}";
    }
}
