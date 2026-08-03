using System.Collections.Specialized;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// The most-recently-USED list must record ACTIVATION, not merely first-open.
/// <para>
/// The filed defect (<c>work-items/bugs/2026-08-02-mru-records-openings-not-activations.md</c>): the
/// only automatic MRU writer fired exclusively on the open-documents collection's <c>Add</c> action,
/// and <see cref="OpenDocumentsService.OpenAsync"/> deduplicates an already-open path and returns
/// after <c>SetActive</c> WITHOUT an <c>Add</c>. <c>SetActive</c> raises only
/// <c>ActiveDocumentChanged</c>, which no MRU writer observed — so switching between already-open
/// tabs never updated the list. It recorded openings, not uses.
/// </para>
/// <para>
/// These guards drive a REAL <see cref="OpenDocumentsService"/> through the REAL production
/// activation handler (<see cref="ApplicateMainWindow.HandleActiveDocumentChangedForRecent"/>) and the
/// REAL production fold (<see cref="ApplicateMainWindow.NoteRecentDocumentCore"/>), so the activation
/// events under test are the ones the service actually raises rather than events this file
/// synthesized. The one source-text guard closes the gap those cannot see: a tested handler
/// production never calls.
/// </para>
/// </summary>
public sealed class ApplicateActivationMruFoldTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplicateActivationMruFoldTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Applicate.Tests.ActivationMru",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string WriteTemp(string fileName)
    {
        var path = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(path, $"# {fileName}");
        return path;
    }

    /// <summary>
    /// The host's recent-files wiring, reassembled from its real parts: BOTH triggers (the
    /// open-documents collection and the activation event) routed into the ONE production fold, exactly
    /// as <c>InstallActiveDocumentBridge</c> wires them. Only the mirror/save sinks are local.
    /// </summary>
    private sealed class RecentFilesProbe
    {
        public List<string> RecentPaths { get; } = new();

        public int FoldCount { get; private set; }

        public int MirrorCount { get; private set; }

        public int SaveCount { get; private set; }

        public int AddCount { get; private set; }

        /// <summary>The D12 replay marker, settable so its decline can be driven directly.</summary>
        public string? ReplayFoldInFlight { get; set; }

        public RecentFilesProbe(IOpenDocumentsService service)
        {
            void NoteRecentDocument(string? path)
            {
                FoldCount++;
                ApplicateMainWindow.NoteRecentDocumentCore(
                    RecentPaths,
                    ReplayFoldInFlight,
                    path,
                    _ => MirrorCount++);
            }

            ((INotifyCollectionChanged)service.OpenDocuments).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    AddCount++;
                }

                ApplicateMainWindow.HandleOpenDocumentsChanged(e, NoteRecentDocument, () => SaveCount++);
            };

            service.ActiveDocumentChanged += (_, _) =>
            {
                ApplicateMainWindow.HandleActiveDocumentChangedForRecent(
                    RecentPaths,
                    service.ActiveDocument?.FilePath,
                    NoteRecentDocument);
                SaveCount++;
            };
        }
    }

    /// <summary>
    /// A1 — THE falsifying guard. RED against the unfixed shape, where activation reaches no MRU
    /// writer at all and the list stays frozen in first-open order.
    /// <para>
    /// The activation is asserted to have actually happened (the service's active document really
    /// changed), so the test cannot pass vacuously on an activation that no-opped.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ActivatingAnAlreadyOpenTabFoldsItToTheHeadOfTheRecentList()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        var second = await service.OpenAsync(WriteTemp("two.md"));

        // First-open order: two.md was opened last.
        Assert.Equal(new List<string> { second.FilePath, first.FilePath }, probe.RecentPaths);

        // The tab click: ApplicateTabsView -> IOpenDocumentsService.Activate.
        service.Activate(first);

        // The activation really happened — otherwise everything below is vacuous.
        Assert.Same(first, service.ActiveDocument);

        // ...and the MRU now leads with what the user is actually looking at.
        Assert.Equal(new List<string> { first.FilePath, second.FilePath }, probe.RecentPaths);
    }

    /// <summary>
    /// A2 — the dedupe path, end to end, and the guard on the trap: re-opening an already-open path
    /// must bump it through ACTIVATION and must NOT manufacture a collection <c>Add</c>. An
    /// <c>Add</c> here would change what every other observer of that collection sees (the tabs strip
    /// rebuild, the session save's open-set) to buy an MRU bump.
    /// </summary>
    [Fact]
    public async Task ReopeningAnAlreadyOpenPathBumpsTheMruWithoutASecondCollectionAdd()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        var second = await service.OpenAsync(WriteTemp("two.md"));
        var addsAfterOpens = probe.AddCount;
        Assert.Equal(2, addsAfterOpens);
        Assert.Equal(new List<string> { second.FilePath, first.FilePath }, probe.RecentPaths);

        // OpenAsync on an already-open path: dedupes, then SetActive.
        var reopened = await service.OpenAsync(first.FilePath);

        Assert.Same(first, reopened);
        Assert.Equal(2, service.OpenDocuments.Count);
        Assert.Equal(addsAfterOpens, probe.AddCount);
        Assert.Equal(new List<string> { first.FilePath, second.FilePath }, probe.RecentPaths);
    }

    /// <summary>
    /// A3 — the activation trigger's own precondition. Activating the document that is ALREADY the
    /// most-recent entry is not a new use: it must not re-fold and must not push a redundant mirror.
    /// <para>
    /// Without this, every fresh open would mirror TWICE (once for the <c>Add</c>, once for the
    /// <c>SetActive</c> that follows it inside the same <c>OpenAsync</c>), and each mirror re-runs the
    /// view model's per-entry <c>File.Exists</c> prune and rebuilds its observable collection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ActivatingTheDocumentAlreadyAtTheHeadDoesNotRefoldOrRemirror()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        // A fresh open raises Add AND ActiveDocumentChanged; only the Add should fold.
        var only = await service.OpenAsync(WriteTemp("one.md"));

        Assert.Same(only, service.ActiveDocument);
        Assert.Equal(new List<string> { only.FilePath }, probe.RecentPaths);
        Assert.Equal(1, probe.FoldCount);
        Assert.Equal(1, probe.MirrorCount);
    }

    /// <summary>
    /// A4 — <c>ClearActive</c> raises the event with a null document. The trigger must decline, not
    /// throw and not mutate. Guards the degenerate input the handler's own precondition owns.
    /// </summary>
    [Fact]
    public async Task ClearingTheActiveDocumentNeitherFoldsNorThrows()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var only = await service.OpenAsync(WriteTemp("one.md"));
        var foldsAfterOpen = probe.FoldCount;
        var recentAfterOpen = probe.RecentPaths.ToList();

        service.ClearActive();

        Assert.Null(service.ActiveDocument);
        Assert.Equal(recentAfterOpen, probe.RecentPaths);
        Assert.Equal(foldsAfterOpen, probe.FoldCount);
        Assert.Contains(only.FilePath, probe.RecentPaths);
    }

    /// <summary>
    /// A5 — D12 clause 2 preserved across the new trigger. The restore replay's own post-restore
    /// activation must NOT resurrect an entry the user removed during the restore window. The
    /// discriminator stays the D12 marker's path IDENTITY, owned by
    /// <see cref="ApplicateMainWindow.NoteRecentDocumentCore"/> — this trigger adds no second one.
    /// </summary>
    [Fact]
    public async Task AnActivationTheReplayMarkerNamesIsDeclined()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        var second = await service.OpenAsync(WriteTemp("two.md"));

        // The user removed one.md from recents during the restore window.
        probe.RecentPaths.RemoveAll(p => string.Equals(p, first.FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new List<string> { second.FilePath }, probe.RecentPaths);

        // The restore tail now activates it, inside the replay bracket that names its path.
        probe.ReplayFoldInFlight = first.FilePath;
        try
        {
            service.Activate(first);
        }
        finally
        {
            probe.ReplayFoldInFlight = null;
        }

        Assert.Same(first, service.ActiveDocument);

        // Declined: the removed entry stayed removed.
        Assert.Equal(new List<string> { second.FilePath }, probe.RecentPaths);
    }

    /// <summary>
    /// A6 — only-forward. Folding on activation is move-to-front: it must never drop a member of the
    /// list. Pins that the new trigger reorders without losing history.
    /// </summary>
    [Fact]
    public async Task ActivationFoldingReordersWithoutDroppingAnyEntry()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var a = await service.OpenAsync(WriteTemp("a.md"));
        var b = await service.OpenAsync(WriteTemp("b.md"));
        var c = await service.OpenAsync(WriteTemp("c.md"));

        var membersAfterOpens = probe.RecentPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(3, membersAfterOpens.Count);

        service.Activate(a);
        service.Activate(b);
        service.Activate(a);

        Assert.Equal(
            membersAfterOpens,
            probe.RecentPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());

        // Most-recently-used first, by ACTIVATION: a, then b, then the untouched c.
        Assert.Equal(new List<string> { a.FilePath, b.FilePath, c.FilePath }, probe.RecentPaths);
    }

    /// <summary>
    /// A7 — the source-text guard the behavioural ones structurally cannot supply: a tested handler
    /// production never calls is invisible to every test above. Pins that the production activation
    /// subscription routes through the tested handler and folds BEFORE it persists, so the save
    /// carries the updated order rather than the previous one.
    /// </summary>
    [Fact]
    public void ProductionActiveDocumentChangedDelegatesToTheTestedHandlerBeforeSaving()
    {
        var bridge = ExtractMethodBody(
            ReadMainWindowCodeBehind(),
            "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        // The recent-files subscription is the one that follows the open-documents subscription.
        var collectionIndex = bridge.IndexOf(
            "((INotifyCollectionChanged)openDocs.OpenDocuments).CollectionChanged",
            StringComparison.Ordinal);
        Assert.True(collectionIndex >= 0, "The open-documents subscription should exist.");

        var activationIndex = bridge.IndexOf(
            "openDocs.ActiveDocumentChanged",
            collectionIndex,
            StringComparison.Ordinal);
        Assert.True(activationIndex > collectionIndex, "ActiveDocumentChanged should follow it.");

        var subscription = bridge[activationIndex..];

        var handlerCall = subscription.IndexOf(
            "HandleActiveDocumentChangedForRecent(",
            StringComparison.Ordinal);
        Assert.True(handlerCall >= 0, "The activation subscription should delegate to the tested handler.");

        var saveCall = subscription.IndexOf("SaveSession()", handlerCall, StringComparison.Ordinal);
        Assert.True(saveCall > handlerCall, "The fold must run BEFORE the persist, so the save carries it.");

        Assert.Contains("NoteRecentDocument", subscription[..saveCall], StringComparison.Ordinal);

        // The precondition lives in the tested handler, not at the call site.
        var callSite = subscription[handlerCall..saveCall];
        Assert.DoesNotContain("RecentPaths[0]", callSite, StringComparison.Ordinal);
        Assert.DoesNotContain("recentPaths[0]", callSite, StringComparison.Ordinal);
    }

    private static string ReadMainWindowCodeBehind()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "ApplicateMainWindow.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} should exist.");

        var braceStart = source.IndexOf('{', start);
        Assert.True(braceStart >= 0, $"{signature} should have a body.");

        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return source[braceStart..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} body was not closed.");
    }
}
