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
/// non-active tab cannot itself be dirty (there is a single editor session). The wired, shipped path
/// is covered by <see cref="BulkCloseSessionLossReproTests"/>.
///
/// RE-AIMED. These tests were originally written to CHARACTERIZE the pre-80994f2 bypass: constructed
/// bare (`new ApplicateTabsView(service)`, no attached TopLevel), CloseSet could not resolve its owner
/// and degraded to a direct OpenDocumentsService.Close, so the suite asserted that a bulk close removed
/// everything unconditionally. That made them a description of the DEFECT rather than of any intended
/// contract — after 80994f2 they were pinning the exact fail-OPEN branch that reproduces the original
/// HIGH data-loss bug, so any hardening of that branch would have been reported as a regression by the
/// very suite meant to protect it.
///
/// They are NOT deleted and the bare-view harness is NOT abandoned: an unattached view is still the
/// only way to drive owner-resolution failure, which is precisely the branch that now needs pinning.
/// Only the expectation moved, onto the two-case contract CloseSet now states explicitly:
///
///   (1) a set WITHOUT the active document closes directly — designed, no prompt is owed;
///   (2) a set WITH the active document whose owner cannot be resolved is REFUSED, loudly.
///
/// Both cases are pinned below, deliberately: a fix that refused everything would satisfy (2) while
/// silently breaking (1), and a fix that closed everything would satisfy (1) while restoring the bug.
///
/// There is no design-time/previewer context to exempt. ApplicateTabsView has no .axaml (the Avalonia
/// previewer instantiates XAML, and this control is built entirely in code), exposes no parameterless
/// constructor (only `ApplicateTabsView(IOpenDocumentsService)`), `Design.IsDesignMode` appears nowhere
/// in src/, and the single production construction site is ApplicateMainWindow.cs:854 inside the window
/// whose DataContext is the MainWindowViewModel. A bare view is therefore a test fixture only — never a
/// shipped state in which closing directly would be correct.
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
    public async Task BulkCloseOfTheActiveDocumentIsRefusedWhenTheDirtyGateOwnerCannotBeResolved()
    {
        // Case (2) — the hardening. The view can only reach the service, so it cannot consult
        // "is a buffer dirty?"; that state lives in the ViewModel this bare view cannot see. The gate
        // is OWED here (the active document is in the closing set) and is unreachable, so the close
        // must be refused outright. Previously this same setup removed both documents with no prompt,
        // no exception and no log, and the OpenDocuments.CollectionChanged -> SaveSession seam then
        // persisted the shrunken tab set — the original HIGH data-loss symptom.
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        var docC = await OpenTempAsync(service, "c.md");

        // OpenAsync activates by default, so the last-opened document is the active one and is
        // therefore inside CloseOthers(docB)'s set {docA, docC}.
        Assert.Same(docC, service.ActiveDocument);

        var originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
            await session.Dispatch(() =>
            {
                var view = new ApplicateTabsView(service);
                InvokePrivate(view, "CloseOthers", docB);
            }, CancellationToken.None);
        }
        finally
        {
            Console.SetError(originalError);
        }

        // Fail CLOSED: every document survives, including the ones with no dirty risk. Refusing the
        // whole set is intentional — a partial close would still shrink the persisted session.
        Assert.Equal(new[] { docA, docB, docC }, service.OpenDocuments);
        Assert.Same(docC, service.ActiveDocument);

        // Fail LOUD: the refusal is the one thing a silent degrade never produced. Without this the
        // guard would be indistinguishable, from outside, from a bulk close that quietly did nothing.
        var diagnostics = captured.ToString();
        Assert.Contains("[bulk-close", diagnostics, StringComparison.Ordinal);
        Assert.Contains("owner-unresolved-close-refused", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkCloseWithoutTheActiveDocumentStillClosesDirectlyWithNoOwner()
    {
        // Case (1) — the designed direct close, pinned so the hardening cannot over-reach into it.
        // Only the ACTIVE document owns an editor session, so a set that excludes it cannot hold
        // unsaved work and is owed no prompt. This must keep working even with no resolvable owner,
        // otherwise "fail closed" would have degraded into "never close anything".
        var service = new OpenDocumentsService();
        var docA = await OpenTempAsync(service, "a.md");
        var docB = await OpenTempAsync(service, "b.md");
        var docC = await OpenTempAsync(service, "c.md");

        Assert.Same(docC, service.ActiveDocument);

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
        await session.Dispatch(() =>
        {
            var view = new ApplicateTabsView(service);
            // CloseToLeft(docC) closes {docA, docB} — the active docC is NOT in the set.
            InvokePrivate(view, "CloseToLeft", docC);
        }, CancellationToken.None);

        Assert.Equal(new[] { docC }, service.OpenDocuments);
        Assert.DoesNotContain(docA, service.OpenDocuments);
        Assert.DoesNotContain(docB, service.OpenDocuments);
    }

    [Fact]
    public async Task ReActivatingAClosedDocumentThrowsSoACancelledBulkCloseCannotRestoreIt()
    {
        // The "restore is doubly broken" half of the sweep's claim, verified directly: even if a
        // cancel path DID find the previous document, it could not put it back -- Activate rejects a
        // document that is no longer in the open list (OpenDocumentsService.cs:150-156). Any future
        // cancel/undo for bulk close must therefore re-OPEN, not re-activate. This is also why the
        // guard above refuses the close outright instead of closing and compensating afterwards.
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
