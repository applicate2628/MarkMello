using CommunityToolkit.Mvvm.Input;
using MarkMello.Application.Abstractions;

namespace MarkMello.Presentation.ViewModels;

public partial class MainWindowViewModel
{
    private int _exportOperationActive;

    // The last failed export verdict, or null when there is nothing to report.
    // The RESULT is stored rather than a pre-formatted string so a language
    // switch re-formats the message from the same evidence -- the idiom
    // _loadErrorResult/RefreshLoadErrorTexts already uses for load failures.
    //
    // Deliberately NOT ViewState: a failed export is not a failed LOAD. The
    // document is rendered and readable; only the save/print leg failed, so the
    // report is a dismissible notice laid over the still-live document view and
    // State stays Viewing. Setting State = LoadError here (the old behaviour)
    // replaced the whole document with the "could not load this file" screen and
    // cost the user their reading position for a locked destination file.
    private ExportResult? _exportFailure;

    // Which document the failure belongs to. The notice reports "exporting THIS
    // document failed", so it is scoped to that document by reference identity:
    // opening/switching/closing a document retires it without any timer.
    private MarkMello.Domain.MarkdownSource? _exportFailureDocument;

    public bool IsExportBusy => Volatile.Read(ref _exportOperationActive) != 0;

    public bool IsExportFailureNoticeVisible
        => _exportFailure is not null && ReferenceEquals(Document, _exportFailureDocument);

    public string ExportFailureNoticeTitle
        => IsExportFailureNoticeVisible ? ExportFailedTitle : string.Empty;

    // The message the user actually reads: what to DO about this failure, in
    // their language. The raw status id stays in ExportFailureNoticeDetails as a
    // demoted, copyable diagnostic -- it is the only thing worth quoting in a bug
    // report, but it is not an explanation and must not serve as the message.
    public string ExportFailureNoticeGuidance
        => IsExportFailureNoticeVisible
            ? _localization[GuidanceKeyFor(_exportFailure!.Status)]
            : string.Empty;

    public string ExportFailureNoticeDetails
        => IsExportFailureNoticeVisible
            ? _localization.Format(
                "ExportFailureDetailsFormat",
                _exportFailure!.Status,
                DiagnosticDetailOf(_exportFailure))
            : string.Empty;

    public string ExportFailureNoticeDiagnosticLabel => _localization["ExportFailureDiagnosticLabel"];

    public string ExportFailureNoticeDismissLabel => _localization["ExportFailureDismiss"];

    // Status -> guidance key. Follows the RefreshLoadErrorTexts idiom (typed
    // result switched to a localization key) rather than inventing a second
    // mapping mechanism. Every arm is a key present in BOTH dictionaries; the
    // default arm keeps the switch total if ExportStatus ever gains a member.
    private static string GuidanceKeyFor(ExportStatus status)
        => status switch
        {
            ExportStatus.PrintReturnedFalse => "ExportFailureGuidancePrintReturnedFalse",
            ExportStatus.WriteFailed => "ExportFailureGuidanceWriteFailed",
            ExportStatus.RenderIncomplete => "ExportFailureGuidanceRenderIncomplete",
            ExportStatus.CaptureFailed => "ExportFailureGuidanceCaptureFailed",
            ExportStatus.ProcessCrashed => "ExportFailureGuidanceProcessCrashed",
            ExportStatus.NoDocument => "ExportFailureGuidanceNoDocument",
            ExportStatus.Faulted => "ExportFailureGuidanceFaulted",
            ExportStatus.Deferred => "ExportFailureGuidanceDeferred",
            ExportStatus.Cancelled => "ExportFailureGuidanceCancelled",
            _ => "ExportFailureGuidanceDefault",
        };

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
        ShellOverlay = ToggleAppOverlayPanel(ShellOverlayKind.AppExport, ShellOverlayKind.AppMenu);
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
        // A fresh attempt supersedes the previous verdict: retire the old notice
        // before running so the user never sees a stale failure next to a
        // succeeding export.
        SetExportFailure(null);
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

    private static string DiagnosticDetailOf(ExportResult result)
        => !string.IsNullOrWhiteSpace(result.Detail)
            ? result.Detail
            : result.Error?.Message ?? string.Empty;

    /// <summary>
    /// Report a failed export without tearing down the document view: the
    /// document stays rendered at its current scroll position and State stays
    /// <see cref="ViewState.Viewing"/>. The typed <see cref="ExportStatus"/> and
    /// the failure detail are kept verbatim -- those ids (HTMLX-*,
    /// PrintReturnedFalse, WriteFailed, ...) are the only diagnosis a user can
    /// report back, so the notice must never degrade to a vague "export failed".
    /// </summary>
    private void PresentExportFailure(ExportResult result) => SetExportFailure(result);

    /// <summary>
    /// Explicit dismissal. The notice is also retired, without any timer, when a
    /// new export starts (<see cref="RunExportOperationAsync"/>) and when the
    /// active document changes (<see cref="OnDocumentChanged"/>).
    /// </summary>
    [RelayCommand]
    private void DismissExportFailureNotice() => SetExportFailure(null);

    private void SetExportFailure(ExportResult? result)
    {
        if (_exportFailure is null && result is null)
        {
            return;
        }

        _exportFailure = result;
        _exportFailureDocument = result is null ? null : Document;
        RaiseExportFailureNoticeBindings();
    }

    private void RaiseExportFailureNoticeBindings()
    {
        OnPropertyChanged(nameof(IsExportFailureNoticeVisible));
        OnPropertyChanged(nameof(ExportFailureNoticeTitle));
        // Guidance is status-DEPENDENT, so unlike the two status-independent
        // labels it must be raised here and not only on a language switch.
        OnPropertyChanged(nameof(ExportFailureNoticeGuidance));
        OnPropertyChanged(nameof(ExportFailureNoticeDetails));
    }

    private void NotifyExportStateChanged()
    {
        OnPropertyChanged(nameof(IsExportBusy));
        UpdateCommandStates();
    }
}
