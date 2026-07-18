using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using MarkMello.Applicate.Desktop.Editing;
using MarkMello.Applicate.Desktop.Views;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// A0-bis / bug #22. The dirty-gate bypass was FIXED in 80994f2: ApplicateTabsView.CloseSet now
/// resolves the owning MainWindowViewModel via `TopLevel.GetTopLevel(this)?.DataContext` and routes
/// any bulk close containing the ACTIVE document through RequestBulkCloseWithDirtyCheckAsync ->
/// RunWithDirtyCheckAsync -> RequiresDirtyResolution. Because RequiresDirtyResolution was widened to
/// be mode-independent (EditorSession?.IsDirty == true), that routing AUTOMATICALLY extends bulk-close
/// protection to reading-mode dirty (a lone checkbox/cell edit) — no separate fix is needed, and a
/// non-active tab cannot itself be dirty (there is a single editor session). The mode-independent
/// prompt is covered by the VM-level test in MainWindowViewModelTests
/// (ReadingModeDirtyBulkCloseQueuesBehindPrompt).
///
/// These headless tests pin the STRUCTURAL FALLBACK: when the view is constructed bare
/// (`new ApplicateTabsView(service)`, no attached TopLevel to carry the VM DataContext), CloseSet
/// cannot resolve the owner and degrades to a direct OpenDocumentsService.Close — so a bulk close is an
/// unconditional removal and re-activating a closed document throws.
/// </summary>
public sealed class BulkCloseDirtyBypassTests : IDisposable
{
    private readonly string _tempRoot;

    public BulkCloseDirtyBypassTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "MarkMello.Applicate.Tests.BulkClose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task BulkCloseRemovesEveryDocumentWithNoArbitrationSeam()
    {
        // The bypass, structurally: the view can only reach the service, so a bulk close is an
        // unconditional removal. Nothing here can consult "is a buffer dirty?" -- that state lives
        // in the ViewModel the view cannot see.
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        var docC = await OpenTempAsync(service, "c.md");

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var view = new ApplicateTabsView(service);
            InvokePrivate(view, "CloseOthers", docB);
        }, CancellationToken.None);

        Assert.Equal(new[] { docB }, service.OpenDocuments);
        Assert.DoesNotContain(docA, service.OpenDocuments);
        Assert.DoesNotContain(docC, service.OpenDocuments);
    }

    [Fact]
    public async Task ReActivatingAClosedDocumentThrowsSoACancelledBulkCloseCannotRestoreIt()
    {
        // The "restore is doubly broken" half of the sweep's claim, verified directly: even if a
        // cancel path DID find the previous document, it could not put it back -- Activate rejects a
        // document that is no longer in the open list (OpenDocumentsService.cs:150-156). Any future
        // cancel/undo for bulk close must therefore re-OPEN, not re-activate.
        var service = new OpenDocumentsService();
        var doc = await OpenTempAsync(service, "gone.md");
        var other = await OpenTempAsync(service, "other.md");

        service.Close(doc);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Activate(doc));
        Assert.Contains("not in the open list", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { other }, service.OpenDocuments);
    }

    private async Task<OpenDocument> OpenTempAsync(OpenDocumentsService service, string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        await File.WriteAllTextAsync(path, fileName);
        return await service.OpenAsync(path);
    }

    private static void InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        _ = method!.Invoke(target, args);
    }
}
