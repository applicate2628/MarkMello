using CommunityToolkit.Mvvm.Input;
using MarkMello.Application.Abstractions;

namespace MarkMello.Presentation.ViewModels;

public partial class MainWindowViewModel
{
    private int _exportOperationActive;

    public bool IsExportBusy => Volatile.Read(ref _exportOperationActive) != 0;

    private bool CanExportDocument()
        => _documentExporter is not null
           && Document is not null
           && State == ViewState.Viewing
           && ShowsAppMenuControl
           && !IsExportBusy;

    [RelayCommand(CanExecute = nameof(CanExportDocument))]
    private void OpenAppExport()
    {
        if (!CanExportDocument())
        {
            CloseAppOverlayCore();
            return;
        }

        MarkSecondaryFeaturesReady();
        ShellOverlay = ShellOverlayKind.AppExport;
    }

    [RelayCommand(CanExecute = nameof(CanExportDocument))]
    private Task ExportPdfAsync()
        => RunExportOperationAsync(async (document, cancellationToken) =>
        {
            var path = await PickExportPathAsync(
                    document,
                    ".pdf",
                    ExportPdfDialogTitle,
                    PdfDocuments,
                    cancellationToken)
                .ConfigureAwait(true);
            return path is null
                ? null
                : await _documentExporter!.ExportPdfAsync(path, cancellationToken).ConfigureAwait(true);
        });

    [RelayCommand(CanExecute = nameof(CanExportDocument))]
    private Task ExportHtmlAsync()
        => RunExportOperationAsync(async (document, cancellationToken) =>
        {
            var path = await PickExportPathAsync(
                    document,
                    ".html",
                    ExportHtmlDialogTitle,
                    HtmlDocuments,
                    cancellationToken)
                .ConfigureAwait(true);
            return path is null
                ? null
                : await _documentExporter!
                    .ExportHtmlAsync(path, document.Content, cancellationToken)
                    .ConfigureAwait(true);
        });

    [RelayCommand(CanExecute = nameof(CanExportDocument))]
    private Task PrintAsync()
        => RunExportOperationAsync(async (_, cancellationToken) =>
            await _documentExporter!.ShowPrintDialogAsync(cancellationToken).ConfigureAwait(true));

    private async Task RunExportOperationAsync(
        Func<MarkMello.Domain.MarkdownSource, CancellationToken, Task<ExportResult?>> operation,
        CancellationToken cancellationToken = default)
    {
        var document = Document;
        if (document is null || !CanExportDocument())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _exportOperationActive, 1, 0) != 0)
        {
            return;
        }

        CloseAppOverlayCore();
        NotifyExportStateChanged();

        try
        {
            var result = await operation(document, cancellationToken).ConfigureAwait(true);
            if (result is not null && result.Status != ExportStatus.Success)
            {
                PresentExportFailure(result);
            }
        }
        finally
        {
            Volatile.Write(ref _exportOperationActive, 0);
            NotifyExportStateChanged();
        }
    }

    private Task<string?> PickExportPathAsync(
        MarkMello.Domain.MarkdownSource document,
        string extension,
        string title,
        string fileTypeName,
        CancellationToken cancellationToken)
    {
        var baseName = Path.GetFileNameWithoutExtension(document.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = Path.GetFileNameWithoutExtension(_localization["UntitledFileName"]);
        }

        return _filePicker.PickSaveFileAsync(
            new FileSavePickerSpec(
                title,
                $"{baseName}{extension}",
                extension,
                fileTypeName,
                [$"*{extension}"]),
            cancellationToken);
    }

    private void PresentExportFailure(ExportResult result)
    {
        var detail = !string.IsNullOrWhiteSpace(result.Detail)
            ? result.Detail
            : result.Error?.Message ?? string.Empty;

        ErrorTitle = ExportFailedTitle;
        ErrorDetails = _localization.Format("ExportFailureDetailsFormat", result.Status, detail);
        State = ViewState.LoadError;
        RefreshWindowTitle();
        UpdateCommandStates();
    }

    private void NotifyExportStateChanged()
    {
        OnPropertyChanged(nameof(IsExportBusy));
        UpdateCommandStates();
    }
}
