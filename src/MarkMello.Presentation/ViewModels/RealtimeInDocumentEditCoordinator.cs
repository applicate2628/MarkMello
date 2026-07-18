using System;
using System.Threading;
using System.Threading.Tasks;
using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Extension seam for an in-document edit kind. Each kind owns its own
/// validation and refusal contract; the coordinator only serializes edits.
/// </summary>
internal interface IInDocumentEditKind
{
    Task ApplyAsync();

    void PublishBusy();
}

/// <summary>
/// Shared serialization point for realtime document edits. It deliberately
/// has no knowledge of checkbox or table semantics.
/// </summary>
internal sealed class RealtimeInDocumentEditCoordinator
{
    private int _isApplying;

    public async Task ApplyAsync(IInDocumentEditKind editKind)
    {
        ArgumentNullException.ThrowIfNull(editKind);

        if (Interlocked.Exchange(ref _isApplying, 1) != 0)
        {
            editKind.PublishBusy();
            return;
        }

        try
        {
            await editKind.ApplyAsync().ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref _isApplying, 0);
        }
    }
}

/// <summary>
/// Narrow bridge from edit kinds to the view-model-owned state and event
/// surfaces. It lets strategies keep validation and error mapping local while
/// preserving the existing public event contracts.
/// </summary>
internal interface IRealtimeInDocumentEditHost
{
    string? CurrentDocumentPath { get; }

    MarkdownSource? CurrentDocument { get; }

    EditorSessionViewModel? EditorSession { get; }

    void PublishTaskToggleRevert(TaskToggleRevertRequest revert);

    void PublishEditPreviewTaskToggleCommit(TaskToggleCommit commit);

    void PublishEditPreviewTaskToggleRevert(TaskToggleRevertRequest revert);

    // Reading-mode (Viewer-origin) commit: apply the flipped buffer to the
    // lazily-materialized session as an UNSAVED edit and mirror it to the shared
    // hosts + open-docs. No disk write — Ctrl+S owns persistence.
    void CommitInPlaceTaskFlip(string newBuffer, int line, bool isChecked);

    void PublishEditPreviewTableCellCommit(TableCellCommit commit);

    void RefuseTableCellEdit(int line, int cellIndex, string path, TableCellEditOrigin origin, bool busy = false);

    // Reading-mode (Viewer-origin) commit: apply the rewritten buffer as an
    // UNSAVED edit (or a no-op canonical settle when the content is unchanged)
    // and publish the canonical text/key. No disk write.
    void CommitInPlaceTableCell(
        string newBuffer,
        int line,
        int cellIndex,
        string canonicalText,
        string canonicalKey);
}
