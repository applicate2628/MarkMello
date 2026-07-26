using System.Collections.Specialized;
using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Which open-documents changes are allowed to write the most-recently-USED list.
/// <para>
/// The filed defect (<c>work-items/bugs/2026-07-26-tab-reorder-rewrites-recent-order.md</c>): the fold
/// guard was <c>e.NewItems is not null</c>, and <c>ObservableCollection.Move</c> populates BOTH
/// <c>OldItems</c> and <c>NewItems</c> — so dragging a tab, a pure layout gesture, folded that document
/// to the head of the recent list and the same handler's <c>SaveSession()</c> persisted it.
/// </para>
/// <para>
/// These guards drive a REAL <see cref="OpenDocumentsService"/> through the REAL production handler
/// (<see cref="ApplicateMainWindow.HandleOpenDocumentsChanged"/>) and the REAL production fold
/// (<see cref="ApplicateMainWindow.NoteRecentDocumentCore"/>), so the
/// <see cref="NotifyCollectionChangedEventArgs"/> under test are the ones the framework actually
/// raises rather than args this file synthesized. The one source-text guard below closes the gap
/// those cannot see: a tested handler production never calls.
/// </para>
/// </summary>
public sealed class ApplicateOpenDocumentsMruFoldTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplicateOpenDocumentsMruFoldTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "MarkMello.Applicate.Tests.MruFold",
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
    /// The host's recent-files wiring, reassembled from its real parts. Only the two injected
    /// delegates are local: the fold is the production core and the handler is the production static.
    /// </summary>
    private sealed class RecentFilesProbe
    {
        public List<string> RecentPaths { get; } = new();

        public int FoldCount { get; private set; }

        public int SaveCount { get; private set; }

        public List<NotifyCollectionChangedEventArgs> Events { get; } = new();

        public RecentFilesProbe(IOpenDocumentsService service)
        {
            ((INotifyCollectionChanged)service.OpenDocuments).CollectionChanged += (_, e) =>
            {
                Events.Add(e);
                ApplicateMainWindow.HandleOpenDocumentsChanged(
                    e,
                    path => ApplicateMainWindow.NoteRecentDocumentCore(
                        RecentPaths,
                        replayFoldInFlight: null,
                        path,
                        _ => FoldCount++),
                    () => SaveCount++);
            };
        }
    }

    /// <summary>
    /// R1 — THE falsifying guard. RED against the old <c>e.NewItems is not null</c> predicate, which
    /// folds the dragged document to the head of the recent list.
    /// <para>
    /// The reorder is asserted to have actually happened (tab order flipped), so the test cannot pass
    /// vacuously on a <c>Move</c> that no-opped, and <c>SaveCount</c> is asserted to have advanced,
    /// because tab ORDER is persisted state — withholding the fold must not also withhold the save.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReorderingTabsLeavesTheRecentFileOrderUnchanged()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        var second = await service.OpenAsync(WriteTemp("two.md"));

        // Most-recently-used first: two.md was opened last.
        var recentBeforeReorder = probe.RecentPaths.ToList();
        Assert.Equal(new List<string> { second.FilePath, first.FilePath }, recentBeforeReorder);
        var foldsBeforeReorder = probe.FoldCount;
        var savesBeforeReorder = probe.SaveCount;

        // The tab-strip drag: ApplicateTabsView.OnTabPointerReleased -> IOpenDocumentsService.Move.
        service.Move(first, 1);

        // The reorder really happened — otherwise everything below is vacuous.
        Assert.Equal(
            new List<string> { second.FilePath, first.FilePath },
            service.OpenDocuments.Select(d => d.FilePath).ToList());

        // ...and it did not touch the recent list. A reorder is not a use.
        Assert.Equal(recentBeforeReorder, probe.RecentPaths);
        Assert.Equal(foldsBeforeReorder, probe.FoldCount);

        // ...but the new tab order still reaches the session store.
        Assert.Equal(savesBeforeReorder + 1, probe.SaveCount);
    }

    /// <summary>
    /// R2 — the other half of the specification. Without it a predicate that rejects EVERYTHING passes
    /// R1: a genuine open must still fold move-to-front and still mirror.
    /// </summary>
    [Fact]
    public async Task OpeningADocumentStillFoldsItToTheHeadOfTheRecentList()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        Assert.Equal(new List<string> { first.FilePath }, probe.RecentPaths);

        var second = await service.OpenAsync(WriteTemp("two.md"));
        Assert.Equal(new List<string> { second.FilePath, first.FilePath }, probe.RecentPaths);

        Assert.Equal(2, probe.FoldCount);

        // NOT a fold: re-opening an already-open document deduplicates inside the service and
        // produces no add at all, so it never reaches this handler. Pre-existing behaviour, named
        // explicitly by d12's "scope of never dropped" so it is not misread as coverage.
        await service.OpenAsync(first.FilePath);
        Assert.Equal(new List<string> { second.FilePath, first.FilePath }, probe.RecentPaths);
        Assert.Equal(2, probe.FoldCount);
    }

    /// <summary>
    /// R3 — a session-restore stub add is an <c>Add</c> too, so the fold reaches it. Pins that the
    /// narrowed predicate did not silently exclude the restore path, which is the other producer of
    /// <c>Add</c> on this collection.
    /// </summary>
    [Fact]
    public async Task AStubAddFoldsLikeAnyOtherOpen()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var stub = await service.OpenStubAsync(WriteTemp("stub.md"));

        Assert.Equal(new List<string> { stub.FilePath }, probe.RecentPaths);
        Assert.Equal(1, probe.FoldCount);
    }

    /// <summary>
    /// R4 — closing a tab is a <c>Remove</c>: it must neither fold nor evict, and must still persist
    /// the shrunken open set. Correct before this change too; covered because the enumeration lists
    /// <c>Remove</c> as reachable and an unguarded reachable action is how the class returns.
    /// </summary>
    [Fact]
    public async Task ClosingATabLeavesTheRecentListUntouchedButStillPersists()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));
        var second = await service.OpenAsync(WriteTemp("two.md"));
        var recentBeforeClose = probe.RecentPaths.ToList();
        var foldsBeforeClose = probe.FoldCount;
        var savesBeforeClose = probe.SaveCount;

        service.Close(first);

        Assert.Equal(new List<string> { second.FilePath }, service.OpenDocuments.Select(d => d.FilePath).ToList());
        Assert.Equal(recentBeforeClose, probe.RecentPaths);
        Assert.Equal(foldsBeforeClose, probe.FoldCount);
        Assert.Equal(savesBeforeClose + 1, probe.SaveCount);
    }

    /// <summary>
    /// R5 — the enumeration itself, as runtime evidence rather than as a claim about documented .NET
    /// behaviour. Exercises every mutation site <see cref="OpenDocumentsService"/> has and records what
    /// each one actually raises.
    /// <para>
    /// The load-bearing assertion is the <c>Move</c> row: <c>NewItems</c> is NOT null there, which is
    /// precisely why the old shape-based predicate folded a reorder. It also pins that no
    /// <c>Replace</c> or <c>Reset</c> is reachable — the two remaining actions, whose sources
    /// (<c>SetItem</c> via the indexer, <c>ClearItems</c> via <c>Clear()</c>) have no call site.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheOpenDocumentsCollectionRaisesOnlyAddRemoveAndMoveAndMoveCarriesNewItems()
    {
        var service = new OpenDocumentsService();
        var probe = new RecentFilesProbe(service);

        var first = await service.OpenAsync(WriteTemp("one.md"));   // Add  (OpenAsync)
        await service.OpenStubAsync(WriteTemp("two.md"));           // Add  (OpenStubAsync)
        service.Move(first, 1);                                     // Move
        service.Close(first);                                       // Remove

        Assert.Equal(
            new[]
            {
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Move,
                NotifyCollectionChangedAction.Remove,
            },
            probe.Events.Select(e => e.Action).ToArray());

        var add = probe.Events[0];
        Assert.NotNull(add.NewItems);
        Assert.Null(add.OldItems);

        // The root cause, measured: Move carries BOTH lists.
        var move = probe.Events[2];
        Assert.NotNull(move.NewItems);
        Assert.NotNull(move.OldItems);

        var remove = probe.Events[3];
        Assert.Null(remove.NewItems);
        Assert.NotNull(remove.OldItems);
    }

    /// <summary>
    /// R6 — the source-text guard the behavioural ones structurally cannot supply: a tested handler
    /// production never calls is invisible to every test above. Also pins that the fold predicate
    /// really MOVED out of the subscription rather than being duplicated there.
    /// </summary>
    [Fact]
    public void ProductionCollectionChangedDelegatesToTheTestedHandler()
    {
        var bridge = ExtractMethodBody(
            ReadMainWindowCodeBehind(),
            "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        var subscriptionIndex = bridge.IndexOf(
            "((INotifyCollectionChanged)openDocs.OpenDocuments).CollectionChanged",
            StringComparison.Ordinal);
        Assert.True(subscriptionIndex >= 0, "The open-documents subscription should exist.");

        // Bounded by the ActiveDocumentChanged subscription that follows it.
        var nextSubscription = bridge.IndexOf(
            "openDocs.ActiveDocumentChanged",
            subscriptionIndex,
            StringComparison.Ordinal);
        Assert.True(nextSubscription > subscriptionIndex, "ActiveDocumentChanged should follow it.");
        var subscription = bridge[subscriptionIndex..nextSubscription];

        Assert.Contains("HandleOpenDocumentsChanged(", subscription, StringComparison.Ordinal);
        Assert.Contains("NoteRecentDocument", subscription, StringComparison.Ordinal);
        Assert.Contains("SaveSession", subscription, StringComparison.Ordinal);

        // The predicate lives in the tested handler, not at the call site.
        Assert.DoesNotContain("NewItems", subscription, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyCollectionChangedAction", subscription, StringComparison.Ordinal);
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
