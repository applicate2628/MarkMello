namespace MarkMello.Application.Abstractions;

public enum ExportStatus
{
    Success,
    Cancelled,
    CaptureFailed,
    NoDocument,
    RenderIncomplete,
    WriteFailed,
    PrintReturnedFalse,
    ProcessCrashed,
    Faulted,
    Deferred,
}

public sealed record ExportResult(
    ExportStatus Status,
    string? Detail = null,
    Exception? Error = null);

public interface IDocumentExporter
{
    Task<ExportResult> ExportHtmlAsync(
        string destinationPath,
        string markdownSource,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportPdfAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportPngAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ShowPrintDialogAsync(CancellationToken cancellationToken = default);
}
