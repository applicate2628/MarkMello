using CommunityToolkit.Mvvm.Input;
using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Payload of <see cref="MainWindowViewModel.InPlaceEditHistoryTransitioned"/>:
/// the session-owned source transition and its directed, already-validated DOM
/// patch. The desktop bridge applies the patch before silently swapping source
/// on each host, so an undo or redo does not require a cold document render.
/// </summary>
public sealed record InPlaceEditHistoryTransition(
    MarkdownSource Source,
    RealtimeInDocumentEditDirectedDomPatch DomPatch);

public partial class MainWindowViewModel
{
    private readonly RealtimeInDocumentEditCoordinator _inDocumentEditCoordinator;
    private readonly IRealtimeInDocumentEditHost _inDocumentEditHost;

    /// <summary>
    /// Lazily materialize the single dirty+buffer owner for a reading-mode
    /// in-place edit, reusing an existing (possibly dormant) session when there is
    /// one. The session is created preview-DEFERRED: the first in-place edit of a
    /// never-edited document must not pay a synchronous whole-document parse on the
    /// zero-cost click path (heavy-doc hazard); the preview reconciles on the next
    /// Ctrl+E. <paramref name="baseline"/> supplies the persisted baseline (the
    /// current in-memory content == last disk load), so the edit that follows reads
    /// as dirty.
    /// </summary>
    private void EnsureInPlaceEditorSession(MarkdownSource baseline)
    {
        if (EditorSession is not null)
        {
            return;
        }

        EditorSession = EditorSessionViewModel.CreatePreviewDeferred(
            baseline,
            ReadingPreferences,
            _renderMarkdown,
            _imageSourceResolver,
            _localization);

        if (!_editorActivationMarked)
        {
            _editorActivationMarked = true;
            _startupMetrics.Mark(StartupStage.EditorActivation);
        }

        EditorSession.UpdateReadingPreferences(ReadingPreferences);
    }

    /// <summary>
    /// Raised after a reading-mode realtime history transition has moved the
    /// session buffer and the ViewModel's silent document backing field. The
    /// desktop bridge owns applying the directed patch and source swap to both
    /// WebView hosts plus the open-document mirror.
    /// </summary>
    public event EventHandler<InPlaceEditHistoryTransition>? InPlaceEditHistoryTransitioned;

    [RelayCommand(CanExecute = nameof(CanUndoRealtimeInDocumentEdit))]
    private void UndoRealtimeInDocumentEdit()
    {
        if (IsEditMode)
        {
            return;
        }

        ApplyRealtimeInDocumentEditHistoryTransition(undo: true);
    }

    private bool CanUndoRealtimeInDocumentEdit()
        => !IsEditMode && EditorSession?.CanUndoRealtimeEdits == true;

    [RelayCommand(CanExecute = nameof(CanRedoRealtimeInDocumentEdit))]
    private void RedoRealtimeInDocumentEdit()
    {
        if (IsEditMode)
        {
            return;
        }

        ApplyRealtimeInDocumentEditHistoryTransition(undo: false);
    }

    private bool CanRedoRealtimeInDocumentEdit()
        => !IsEditMode && EditorSession?.CanRedoRealtimeEdits == true;

    private void ApplyRealtimeInDocumentEditHistoryTransition(bool undo)
    {
        if (EditorSession is not { } session || _document is not { } current)
        {
            return;
        }

        var transition = undo
            ? session.UndoRealtimeInDocumentEdit()
            : session.RedoRealtimeInDocumentEdit();
        if (transition.Status != RealtimeInDocumentEditHistoryTransitionStatus.Applied
            || transition.TargetSource is null
            || transition.DomPatch is null)
        {
            return;
        }

        var source = new MarkdownSource(current.Path, current.FileName, transition.TargetSource);
        _document = source;
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(WordCountStatusLabel));
        QueueDeferredRenderedDocument(source);
        PublishInPlaceEditHistoryTransitioned(
            new InPlaceEditHistoryTransition(source, transition.DomPatch));
    }

    private void PublishInPlaceEditHistoryTransitioned(InPlaceEditHistoryTransition transition)
        => InPlaceEditHistoryTransitioned?.Invoke(this, transition);

    private sealed class InDocumentEditHost(MainWindowViewModel owner) : IRealtimeInDocumentEditHost
    {
        public string? CurrentDocumentPath => owner.CurrentDocumentPath;

        public MarkdownSource? CurrentDocument => owner.Document;

        public EditorSessionViewModel? EditorSession => owner.EditorSession;

        public void PublishTaskToggleRevert(TaskToggleRevertRequest revert)
            => owner.PublishTaskToggleRevertRequested(revert);

        public void PublishEditPreviewTaskToggleCommit(TaskToggleCommit commit)
            => owner.PublishEditPreviewTaskToggleCommitted(commit);

        public void PublishEditPreviewTaskToggleRevert(TaskToggleRevertRequest revert)
            => owner.PublishEditPreviewTaskToggleRevertRequested(revert);

        public void CommitInPlaceTaskFlip(string newBuffer, int line, bool isChecked)
            => owner.CommitInPlaceTaskFlip(newBuffer, line, isChecked);

        public void PublishEditPreviewTableCellCommit(TableCellCommit commit)
            => owner.PublishEditPreviewTableCellCommit(commit);

        public void RefuseTableCellEdit(
            int line,
            int cellIndex,
            string path,
            TableCellEditOrigin origin,
            bool busy = false)
            => owner.RefuseTableCellEdit(line, cellIndex, path, origin, busy);

        public void CommitInPlaceTableCell(
            string newBuffer,
            int line,
            int cellIndex,
            string canonicalText,
            string canonicalKey,
            bool raw)
            => owner.CommitInPlaceTableCell(newBuffer, line, cellIndex, canonicalText, canonicalKey, raw);
    }
}
