using MarkMello.Domain;
using MarkMello.Domain.Diagnostics;

namespace MarkMello.Presentation.ViewModels;

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
            string canonicalKey)
            => owner.CommitInPlaceTableCell(newBuffer, line, cellIndex, canonicalText, canonicalKey);
    }
}
