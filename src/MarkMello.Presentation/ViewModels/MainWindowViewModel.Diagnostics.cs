using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkMello.Domain.Diagnostics;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Document-health surface: on open, the host scans the loaded markdown for
/// inline math hard-wrapped across a source-line break (the renderer drops such
/// spans). When repairable defects are found a banner offers a one-click
/// "fix &amp; save". The repair is the pure <see cref="MarkdownMathHealthAnalyzer"/>
/// join; the original is backed up to a sidecar <c>.bak</c> before the in-place
/// write, then the document reloads.
/// </summary>
public partial class MainWindowViewModel
{
    private MarkdownMathHealthResult? _documentHealth;

    /// <summary>
    /// Raised right before the health fix reloads the repaired document, so the
    /// host's document-switch reveal coordinator can suppress its cover for that
    /// one reload. The fix is a same-document content update (same path), so the
    /// switch cover would flash a "disappear/reappear" — the same flicker the
    /// live-edit path already avoids. A real tab switch / F5 is unaffected.
    /// </summary>
    public event EventHandler? SuppressNextDocumentReveal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentHealthBannerVisible))]
    private bool _isDocumentHealthDismissed;

    [ObservableProperty]
    private bool _isApplyingDocumentHealthFix;

    public bool HasDocumentMathDefects => _documentHealth?.HasRepairableDefects == true;

    public int DocumentMathDefectCount => _documentHealth?.RepairableDefectCount ?? 0;

    public bool IsDocumentHealthBannerVisible
        => HasDocumentMathDefects && !IsDocumentHealthDismissed;

    public string DocumentHealthBannerText
        => _localization.Format("DocumentHealthBanner", DocumentMathDefectCount);

    public string DocumentHealthApplyLabel => _localization["DocumentHealthApply"];

    public string DocumentHealthDismissLabel => _localization["DocumentHealthDismiss"];

    /// <summary>
    /// Re-scan the currently loaded document for repairable math defects and
    /// refresh the banner. Called by the host whenever the active document
    /// changes (open / tab-switch / reload).
    /// </summary>
    public void AnalyzeCurrentDocumentHealth()
    {
        var text = Document?.Content;
        _documentHealth = string.IsNullOrEmpty(text)
            ? null
            : MarkdownMathHealthAnalyzer.Analyze(text);
        IsDocumentHealthDismissed = false;
        RaiseDocumentHealthBindings();
    }

    private void RaiseDocumentHealthBindings()
    {
        OnPropertyChanged(nameof(HasDocumentMathDefects));
        OnPropertyChanged(nameof(DocumentMathDefectCount));
        OnPropertyChanged(nameof(IsDocumentHealthBannerVisible));
        OnPropertyChanged(nameof(DocumentHealthBannerText));
    }

    [RelayCommand]
    private void DismissDocumentHealthBanner() => IsDocumentHealthDismissed = true;

    [RelayCommand]
    private async Task ApplyDocumentHealthFixAsync()
    {
        if (IsApplyingDocumentHealthFix)
        {
            return;
        }

        // Re-analyze the LIVE text at fix-time so an edit-mode buffer (or a doc
        // changed since the banner appeared) is repaired correctly, never a
        // stale snapshot.
        var editing = IsEditMode && EditorSession is not null;
        var liveText = editing ? EditorSession!.SourceText : Document?.Content;
        if (string.IsNullOrEmpty(liveText))
        {
            return;
        }

        var result = MarkdownMathHealthAnalyzer.Analyze(liveText);
        if (!result.HasRepairableDefects)
        {
            _documentHealth = result;
            IsDocumentHealthDismissed = true;
            RaiseDocumentHealthBindings();
            return;
        }

        IsApplyingDocumentHealthFix = true;
        try
        {
            if (editing)
            {
                // In edit mode push the repaired text into the editor buffer; the
                // user keeps control of saving (the dirty flow owns the write).
                EditorSession!.SourceText = result.RepairedText;
            }
            else
            {
                var path = CurrentDocumentPath;
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                // Back up the original next to the file BEFORE overwriting, so the
                // repair is reversible without relying on version control.
                await _saveDocument.SaveBackupAsync(path + ".bak", liveText).ConfigureAwait(true);
                await _saveDocument.ExecuteAsync(path, result.RepairedText).ConfigureAwait(true);
            }

            IsDocumentHealthDismissed = true;
            RaiseDocumentHealthBindings();

            // Reload from disk so the repaired document renders (viewer path).
            if (!editing && CanReload())
            {
                // Same-path content update: suppress the document-switch cover for
                // this one reload so the repair lands without a reveal flicker.
                SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);
                await ReloadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // Write/backup failed (I/O, permissions). Leave the original intact
            // (the saver writes atomically) and keep the banner so the user can
            // retry; do not reload a half-written document.
        }
        finally
        {
            IsApplyingDocumentHealthFix = false;
        }
    }
}
