using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkMello.Application.UseCases;
using MarkMello.Domain;
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
    /// host's airspace compositor can suppress its cover for that
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

        // Re-analyze the LIVE text at fix-time so an edited buffer (or a doc
        // changed since the banner appeared) is repaired correctly, never a
        // stale snapshot. R3: branch on SESSION existence, not IsEditMode. Post
        // the reading-mode dirty flip a session can exist while reading; its
        // buffer (not disk) is the live text, and the repair must dirty the
        // buffer instead of disk-writing + reloading — the widened dirty gate
        // would otherwise fire a Save/Discard prompt in the middle of the fix.
        var hasSession = EditorSession is not null;
        var liveText = hasSession ? EditorSession!.SourceText : Document?.Content;
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
            if (hasSession)
            {
                // A session owns the buffer (edit mode, or a reading-mode dirty
                // doc): push the repaired text into it as an unsaved edit; the
                // user keeps control of saving (the dirty flow owns the write).
                //
                // The plain setter is deliberate, not a bypass of the edit-mode
                // single-writer rule. It is the CONTENT-EDIT channel:
                // EditWorkspaceView mirrors it onto the LIVE TextEditor as one
                // minimal Document.Replace, so in edit mode the repair is a
                // single undoable step that leaves the user's earlier typing,
                // caret and scroll intact. Undoability here is the editor
                // owner's contract, not this call site's.
                //
                // Do NOT "tidy" this into ApplyLoadedDocument or DiscardChanges:
                // those bump DocumentGeneration, which declares a buffer
                // REPLACEMENT and makes the editor discard the entire undo stack
                // — the defect fixed in 07934af. Routing through
                // EditWorkspaceView.ApplyEditModeSourceEdit would be worse: it
                // is a ViewModel -> View reach, it wants a directed span this
                // whole-document repair does not have, and no such view exists
                // on the reading-mode-dirty path this same branch serves.
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
                var saveResult = await _saveDocument
                    .ExecuteAsync(path, result.RepairedText)
                    .ConfigureAwait(true);
                if (saveResult is not SaveDocumentResult.Success)
                {
                    // The write failed (I/O, permissions, invalid path). ExecuteAsync
                    // maps those to a typed failure instead of throwing, so ignoring
                    // its result would treat a failed save as success — dismissing
                    // the banner and reloading a file that still holds the UNREPAIRED
                    // content. Bail so the banner stays up (the document was not
                    // fixed) and nothing reloads, exactly like the backup-throw path
                    // in the catch below.
                    return;
                }
            }

            IsDocumentHealthDismissed = true;
            RaiseDocumentHealthBindings();

            // A reading-mode session owns the repaired buffer, but the
            // viewer is bound to Document rather than that buffer. Republish the
            // same source there so the visible document reflects the repair; an
            // edit-mode session's preview already observes its buffer directly.
            if (hasSession && !IsEditMode && _document is { } current)
            {
                _pendingDeferredRenderedDocument = null;
                SuppressNextDocumentReveal?.Invoke(this, EventArgs.Empty);
                Document = new MarkdownSource(current.Path, current.FileName, result.RepairedText);
                RenderedDocument = _renderMarkdown.Execute(
                    result.RepairedText,
                    baseDirectory: TryGetDirectory(current.Path));
            }

            // Reload from disk so the repaired document renders (viewer path).
            // Only when no session owns the buffer — otherwise the buffer push
            // owns the repair and a reload would fight it.
            if (!hasSession && CanReload())
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
