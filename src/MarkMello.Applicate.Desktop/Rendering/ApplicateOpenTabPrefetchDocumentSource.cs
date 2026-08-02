using System.IO;
using Avalonia.Threading;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

/// <summary>
/// Adapts <see cref="IOpenDocumentsService"/> to
/// <see cref="IApplicatePrefetchDocumentSource"/>. Thin by design: it owns no
/// prefetch policy — no ordering, no bound, no skip rules — only the thread
/// affinity the open-document model requires.
///
/// <para><b>Why the dispatcher hop.</b> <see cref="OpenDocument.SourceText"/> and
/// <see cref="OpenDocument.IsLoaded"/> are plain mutable properties that the UI
/// thread reads and writes (tab activation, the edit-mode mirror,
/// <see cref="IOpenDocumentsService.EnsureLoadedAsync"/>'s own post-await
/// re-check). A prefetch running on the thread pool must not become a second
/// concurrent writer of them, so the whole lookup-and-load runs on the UI
/// thread. That does NOT put file I/O on the UI thread:
/// <c>EnsureLoadedAsync</c> awaits <c>File.ReadAllTextAsync</c>, which does the
/// read on the thread pool and only resumes here.</para>
///
/// <para>The hop is posted at <see cref="DispatcherPriority.Background"/>, below
/// input and render, so a warm-up can never sit in front of the user.</para>
/// </summary>
internal sealed class ApplicateOpenTabPrefetchDocumentSource : IApplicatePrefetchDocumentSource
{
    private readonly IOpenDocumentsService _openDocuments;

    public ApplicateOpenTabPrefetchDocumentSource(IOpenDocumentsService openDocuments)
    {
        _openDocuments = openDocuments ?? throw new ArgumentNullException(nameof(openDocuments));
    }

    public Task<MarkdownSource?> TryMaterializeAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult<MarkdownSource?>(null);
        }

        return OnUiThreadAsync(() => MaterializeOnUiThreadAsync(path, cancellationToken), cancellationToken);
    }

    private async Task<MarkdownSource?> MaterializeOnUiThreadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var document = FindByPath(path);
        if (document is null)
        {
            // The tab was closed between the snapshot and now. Not an error —
            // the snapshot is deliberately allowed to go stale, and this is the
            // re-check that makes that safe.
            return null;
        }

        // Session-restored non-active tabs are STUBS: no text at all. This is
        // the existing loader, which consumes the early-document cache before
        // touching disk. The stub design avoids that I/O at STARTUP; this runs
        // after the active document has painted, which is exactly when paying
        // it is free.
        await _openDocuments.EnsureLoadedAsync(document).ConfigureAwait(true);

        cancellationToken.ThrowIfCancellationRequested();

        if (!document.IsLoaded)
        {
            return null;
        }

        return new MarkdownSource(
            Path: document.FilePath,
            FileName: Path.GetFileName(document.FilePath),
            Content: document.SourceText);
    }

    private OpenDocument? FindByPath(string path)
    {
        foreach (var document in _openDocuments.OpenDocuments)
        {
            if (string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>
    /// Run <paramref name="work"/> on the UI thread and await its result.
    /// Hand-rolled over <c>Dispatcher.Post</c> + a
    /// <see cref="TaskCompletionSource{TResult}"/> rather than an
    /// <c>InvokeAsync</c> overload so the async-returning shape is explicit at
    /// the call site instead of resting on overload resolution between
    /// <c>Func&lt;TResult&gt;</c> and <c>Func&lt;Task&lt;TResult&gt;&gt;</c>.
    /// </summary>
    private static Task<MarkdownSource?> OnUiThreadAsync(
        Func<Task<MarkdownSource?>> work,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return work();
        }

        var completion = new TaskCompletionSource<MarkdownSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    completion.TrySetResult(await work().ConfigureAwait(true));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            DispatcherPriority.Background);

        return completion.Task;
    }
}
