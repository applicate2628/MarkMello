using MarkMello.Applicate.Desktop;
using MarkMello.Applicate.Desktop.Editing;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Recent-files DELTA (P2): the host's two testable seams (<see
/// cref="ApplicateMainWindow.RemoveRecentPathAndCommit"/> and <see
/// cref="ApplicateMainWindow.ClearRecentPathsAndCommit"/>) plus source-text coverage for the
/// wiring inside <c>InstallActiveDocumentBridge</c> that the seams cannot reach (whether the
/// VM events are actually subscribed, and whether <c>SaveSession</c> is really the persist
/// argument -- see plan.md &#167;D).
/// </summary>
public sealed class ApplicateRecentFilesWiringTests
{
    [Fact]
    public void RemoveRecentPathAndCommitDropsExactEntryPreservingOrder()
    {
        var recentPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md", @"C:\a\three.md" };

        ApplicateMainWindow.RemoveRecentPathAndCommit(
            recentPaths,
            @"C:\a\two.md",
            _ => { },
            () => { });

        Assert.Equal(new List<string> { @"C:\a\one.md", @"C:\a\three.md" }, recentPaths);
    }

    [Fact]
    public void RemoveRecentPathAndCommitIsCaseInsensitive()
    {
        var recentPaths = new List<string> { @"C:\A\ONE.MD", @"C:\a\two.md" };

        ApplicateMainWindow.RemoveRecentPathAndCommit(
            recentPaths,
            @"c:\a\one.md",
            _ => { },
            () => { });

        Assert.Equal(new List<string> { @"C:\a\two.md" }, recentPaths);
    }

    [Fact]
    public void RemoveRecentPathAndCommitWithAbsentPathIsNoOpButStillCommitsOnce()
    {
        var recentPaths = new List<string> { @"C:\a\one.md" };
        var mirrorCount = 0;
        var persistCount = 0;

        var exception = Record.Exception(() => ApplicateMainWindow.RemoveRecentPathAndCommit(
            recentPaths,
            @"C:\a\absent.md",
            _ => mirrorCount++,
            () => persistCount++));

        Assert.Null(exception);
        Assert.Equal(new List<string> { @"C:\a\one.md" }, recentPaths);
        Assert.Equal(1, mirrorCount);
        Assert.Equal(1, persistCount);
    }

    [Fact]
    public void ClearRecentPathsAndCommitEmptiesTheList()
    {
        var recentPaths = new List<string> { @"C:\a\one.md", @"C:\a\two.md" };
        var mirrorCount = 0;
        var persistCount = 0;

        ApplicateMainWindow.ClearRecentPathsAndCommit(
            recentPaths,
            _ => mirrorCount++,
            () => persistCount++);

        Assert.Empty(recentPaths);
        Assert.Equal(1, mirrorCount);
        Assert.Equal(1, persistCount);
    }

    [Fact]
    public void BothMutationsMirrorOnceThenPersistOnceWithTheMutatedListVisibleToMirror()
    {
        AssertMirrorThenPersistWithMutatedListVisible(
            (paths, mirror, persist) =>
                ApplicateMainWindow.RemoveRecentPathAndCommit(paths, @"C:\a\one.md", mirror, persist),
            new List<string> { @"C:\a\one.md", @"C:\a\two.md" });

        AssertMirrorThenPersistWithMutatedListVisible(
            (paths, mirror, persist) => ApplicateMainWindow.ClearRecentPathsAndCommit(paths, mirror, persist),
            new List<string> { @"C:\a\one.md", @"C:\a\two.md" });
    }

    private static void AssertMirrorThenPersistWithMutatedListVisible(
        Action<List<string>, Action<IReadOnlyList<string>>, Action> invoke,
        List<string> initial)
    {
        var recentPaths = new List<string>(initial);
        var order = new List<string>();
        IReadOnlyList<string>? seenByMirror = null;
        var mirrorCalls = 0;
        var persistCalls = 0;

        invoke(
            recentPaths,
            list =>
            {
                mirrorCalls++;
                order.Add("mirror");
                seenByMirror = list.ToList();
            },
            () =>
            {
                persistCalls++;
                order.Add("persist");
            });

        Assert.Equal(1, mirrorCalls);
        Assert.Equal(1, persistCalls);
        Assert.Equal(new List<string> { "mirror", "persist" }, order);
        Assert.NotNull(seenByMirror);
        Assert.Equal(recentPaths, seenByMirror);
    }

    [Fact]
    public void HostWiringReferencesSaveSessionAndSetRecentFilesNotTheFoldDirectly()
    {
        var handlers = ExtractRecentFilesHandlersRegion(out _, out _);

        Assert.Contains("SaveSession", handlers, StringComparison.Ordinal);
        Assert.Contains("viewModel.SetRecentFiles", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildRecentPaths", handlers, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentFilesHandlersNeverConstructANewApplicateSession()
    {
        var handlers = ExtractRecentFilesHandlersRegion(out var codeBehind, out _);
        var removeHelper = ExtractMethodBody(codeBehind, "internal static void RemoveRecentPathAndCommit(");
        var clearHelper = ExtractMethodBody(codeBehind, "internal static void ClearRecentPathsAndCommit(");

        Assert.DoesNotContain("new ApplicateSession", handlers, StringComparison.Ordinal);
        Assert.DoesNotContain("new ApplicateSession", removeHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("new ApplicateSession", clearHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentFilesHelpersAndHandlersContainNoTimeBasedLogic()
    {
        var handlers = ExtractRecentFilesHandlersRegion(out var codeBehind, out _);
        var removeHelper = ExtractMethodBody(codeBehind, "internal static void RemoveRecentPathAndCommit(");
        var clearHelper = ExtractMethodBody(codeBehind, "internal static void ClearRecentPathsAndCommit(");
        // S9 (D12): the three new statics inherit the same absolute no-timers law. It matters most for
        // NoteRecentDocumentCore: d12 clause 4 forbids an isRestoring-style TIME window as the
        // restore-fold discriminator, because a genuine user open does reach the collection during the
        // restore and a clock would silently discard it. The discriminator is a path IDENTITY.
        var seedHelper = ExtractMethodBody(codeBehind, "internal static void SeedRecentPathsForRestore(");
        var coreHelper = ExtractMethodBody(codeBehind, "internal static void NoteRecentDocumentCore(");
        var unusableHelper = ExtractMethodBody(codeBehind, "internal static bool IsUnusablePersistedPath(");
        // S5 (D13): this test enumerates its regions EXPLICITLY, so D13's new surface is uncovered by
        // construction until it is named here. The composer is a new pure static in the same helper
        // band; SaveSession and the store's LoadAsync are the other two bodies D13 rewrites.
        var saveSession = ExtractMethodBody(codeBehind, "void SaveSession()");
        var composer = ExtractMethodBody(codeBehind, "internal static ApplicateSession? ComposeSessionSnapshot(");
        var storeLoad = ExtractMethodBody(
            ReadSessionStoreSource(),
            "public ValueTask<ApplicateSession?> LoadAsync(CancellationToken cancellationToken = default)");

        foreach (var region in new[]
        {
            handlers, removeHelper, clearHelper, seedHelper, coreHelper, unusableHelper,
            saveSession, composer, storeLoad,
        })
        {
            Assert.DoesNotContain("Timer", region, StringComparison.Ordinal);
            Assert.DoesNotContain("DispatcherTimer", region, StringComparison.Ordinal);
            Assert.DoesNotContain("Task.Delay", region, StringComparison.Ordinal);
            Assert.DoesNotContain("Delay(", region, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // D12 (decision 2026-07-26-d12-restore-fold-precedence): the restore window's fold precedence.
    // Behavioural guards against the three new pure statics.
    // ---------------------------------------------------------------------------------------------

    private const string A = @"C:\a\one.md";
    private const string B = @"C:\a\two.md";
    private const string C = @"C:\a\three.md";
    private const string Z = @"C:\a\zed.md";
    private const string X = @"C:\a\recent.md";

    /// <summary>B1 -- the filed defect, made executable.</summary>
    [Fact]
    public void RestoreDrivenAddDoesNotResurrectAUserRemovedRecentEntry()
    {
        var recentPaths = new List<string>();
        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string> { A, B },
            new List<string> { A, B },
            argvPath: null);
        // The persisted MRU says A was used last, and both paths are open, so the seed must preserve
        // [A,B]. This asserted [B,A] until B12 below: the old tab-order fold INVERTED the persisted
        // recency here. Still an exact literal list -- the value moved, the strength did not.
        Assert.Equal(new List<string> { A, B }, recentPaths);

        // The user removes A while the restore is still in flight (the persist is muted, so this
        // lives only in memory until the post-restore convergence save).
        ApplicateMainWindow.RemoveRecentPathAndCommit(recentPaths, A, _ => { }, () => { });
        Assert.Equal(new List<string> { B }, recentPaths);

        // The replay then reaches A. Its add MUST NOT fold A back in -- the convergence save would
        // otherwise persist the resurrected list and the removal would survive nothing.
        var mirrorCalls = 0;
        ApplicateMainWindow.NoteRecentDocumentCore(
            recentPaths,
            replayFoldInFlight: A,
            path: A,
            _ => mirrorCalls++);

        Assert.Equal(new List<string> { B }, recentPaths);
        Assert.Equal(0, mirrorCalls);
    }

    /// <summary>B2 -- F1 made executable: identity, not clock.</summary>
    [Fact]
    public void UserOpenOfAPathOutsideTheRestoreSetStillFoldsDuringRestore()
    {
        var recentPaths = new List<string> { A, B };
        var mirrorCalls = 0;

        // The replay currently has A in hand; the USER opens C. C is a live user open and must reach
        // the MRU. A time-window guard ("we are restoring, drop everything") would silently eat it.
        ApplicateMainWindow.NoteRecentDocumentCore(
            recentPaths,
            replayFoldInFlight: A,
            path: C,
            _ => mirrorCalls++);

        Assert.Equal(new List<string> { C, A, B }, recentPaths);
        Assert.Equal(1, mirrorCalls);
    }

    /// <summary>
    /// B3 -- the post-Clear case at the core. Honest scope: this is a NEAR-DUPLICATE of B2. The core
    /// takes a single nullable path, so a predicted-set ledger is not expressible against its
    /// signature and this test cannot catch one. The real anti-ledger enforcement is that SIGNATURE
    /// plus S6 (iii)'s single-writer count.
    /// </summary>
    [Fact]
    public void AUserOpenOfANotYetReplayedPathFoldsEvenAfterAClear()
    {
        var recentPaths = new List<string>();
        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { A, B },
            argvPath: null);
        ApplicateMainWindow.ClearRecentPathsAndCommit(recentPaths, _ => { }, () => { });
        Assert.Empty(recentPaths);

        // B is the in-flight path; A is a saved-open path the loop has NOT reached yet. A live user
        // open of A must fold, leaving exactly the file the user just asked for.
        var mirrorCalls = 0;
        ApplicateMainWindow.NoteRecentDocumentCore(
            recentPaths,
            replayFoldInFlight: B,
            path: A,
            _ => mirrorCalls++);

        Assert.Equal(new List<string> { A }, recentPaths);
        Assert.Equal(1, mirrorCalls);
    }

    /// <summary>B3b -- the exemption must not outlive the call that set it.</summary>
    [Fact]
    public void AReplaySetPathFoldsAgainOnceTheMarkerIsCleared()
    {
        var recentPaths = new List<string> { B, A };
        var mirrorCalls = 0;

        ApplicateMainWindow.NoteRecentDocumentCore(
            recentPaths,
            replayFoldInFlight: null,
            path: A,
            _ => mirrorCalls++);

        Assert.Equal(new List<string> { A, B }, recentPaths);
        Assert.Equal(1, mirrorCalls);
    }

    /// <summary>
    /// B3c -- records the accepted attribution-collision residual; it does NOT falsify it. The core
    /// is ORIGIN-BLIND by construction, and no cheap probe reaches the real lock-queue interleaving.
    /// <para>
    /// The path is seeded at a NON-HEAD position and the assertion is POSITIONAL EQUALITY, not mere
    /// absence. That is what makes this test discriminate: a decline REINVENTED from list state or a
    /// removal ledger -- "decline only if this path was removed/cleared in this window", or "decline
    /// only if it is absent from recentPaths" -- would FOLD Z to the head here, because Z was never
    /// removed and is not absent. B1 structurally cannot catch that class: there the path is removed
    /// BEFORE the marked add, so "declined" and "absent" coincide and the reinvented form stays green.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAddOfTheMarkedPathIsDeclinedRegardlessOfOrigin()
    {
        var recentPaths = new List<string> { A, B, Z, C };
        var mirrorCalls = 0;

        ApplicateMainWindow.NoteRecentDocumentCore(
            recentPaths,
            replayFoldInFlight: Z,
            path: Z,
            _ => mirrorCalls++);

        Assert.Equal(new List<string> { A, B, Z, C }, recentPaths);
        Assert.Equal(0, mirrorCalls);
    }

    /// <summary>B4 -- declared behaviour delta 1, pinned as a literal list.</summary>
    [Fact]
    public void SeedPutsArgvAtHeadWhenArgvIsAlsoASavedOpenPath()
    {
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { A, B, C },
            argvPath: A);

        // Today the argv open deduplicates WITHOUT an add, so argv folds nothing and stays buried at
        // [C,B,A]. After the hoist argv takes the head. Declared, not smuggled.
        Assert.Equal(new List<string> { A, C, B }, recentPaths);
    }

    /// <summary>B5 -- fold order: saved open paths in order, then argv.</summary>
    [Fact]
    public void SeedFoldsSavedOpenPathsThenArgv()
    {
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { A, B },
            argvPath: C);

        Assert.Equal(new List<string> { C, B, A }, recentPaths);
    }

    /// <summary>B6 -- the cap. The old inline seed did NOT cap.</summary>
    [Fact]
    public void SeedCapsAtMaxRecentPaths()
    {
        var savedRecent = Enumerable.Range(1, 20).Select(i => $@"C:\a\r{i}.md").ToList();
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            savedRecent,
            new List<string> { savedRecent[0] },
            argvPath: null);

        Assert.Equal(ApplicateSession.MaxRecentPaths, recentPaths.Count);
    }

    /// <summary>
    /// B7 -- a pre-D11 session file (populated OpenPaths, empty RecentPaths) must still back-fill.
    /// The probe that looked for this shape found n=1 and did not see it; one dev install is not a
    /// distribution, so this stays a unit-test case rather than an environment question.
    /// </summary>
    [Fact]
    public void SeedWithEmptySavedRecentPathsStillFoldsTheOpenSet()
    {
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { A, B },
            argvPath: null);

        Assert.Equal(new List<string> { B, A }, recentPaths);
    }

    /// <summary>B8 -- declared behaviour delta 2's mechanism: the seed is pure list arithmetic.</summary>
    [Fact]
    public void SeedFoldsAPathThatDoesNotExistOnDisk()
    {
        var missing = @"C:\mark-mello-no-such-directory-9f3c17\ghost.md";
        Assert.False(File.Exists(missing), "The delta-2 fixture must genuinely not exist on disk.");
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { missing },
            argvPath: null);

        // An existence filter here would silently re-hide delta 2 and defeat the whole hoist: the
        // fold must not depend on the open succeeding. Display-pruning is the VM's job (d11 clause 6).
        Assert.Equal(new List<string> { missing }, recentPaths);
    }

    /// <summary>B9 -- declared behaviour delta 3, pinned as a literal list.</summary>
    [Fact]
    public void SeedFoldsADuplicateSavedOpenPathMoveToFront()
    {
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string>(),
            new List<string> { A, B, A },
            argvPath: null);

        // A dedup-by-skip-later-occurrence variant yields [B,A]; move-to-front yields [A,B].
        Assert.Equal(new List<string> { A, B }, recentPaths);
    }

    /// <summary>
    /// B12 -- F1 of <c>work-items/bugs/2026-08-02-tab-prefetch-warms-the-wrong-three-documents.md</c>.
    /// The persisted MRU is genuine ACTIVATION recency: <c>ApplicateSession.RecentPaths</c> is folded on
    /// every <c>ActiveDocumentChanged</c> and persisted by the <c>SaveSession</c> in the same handler, and
    /// the store round-trips it. Folding the saved OPEN set in TAB order threw that away and left the
    /// seed at exactly REVERSE TAB ORDER -- so the first background-prefetch pass of every cold start
    /// warmed the last three tabs in the strip instead of the three documents last reached for.
    /// <para>
    /// The fixture separates all three candidate orders, so it discriminates the two live mutations as
    /// well as the defect: persisted recency is [B,A,C], reverse tab order is [C,B,A] (the defect), and
    /// plain tab order is [A,B,C].
    /// </para>
    /// </summary>
    [Fact]
    public void SeedKeepsThePersistedRecencyOrderOfTheSavedOpenPaths()
    {
        var recentPaths = new List<string>();

        // Persisted recency: B used last, then A, then C. Tab-strip order: A, B, C.
        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string> { B, A, C },
            new List<string> { A, B, C },
            argvPath: null);

        Assert.Equal(new List<string> { B, A, C }, recentPaths);
    }

    /// <summary>
    /// B13 -- the two properties the recency-ordered fold must NOT buy at the cost of. A saved open path
    /// the persisted MRU does not rank (it fell off <c>MaxRecentPaths</c>, or the file predates D11) is
    /// still folded -- d12 clause 1's presence guarantee -- and lands BEHIND the ranked ones, matching
    /// the tie-break <c>ApplicateBackgroundTabPrefetcher.OrderCandidates</c> already applies to the same
    /// two populations. A ranked path that is no longer open keeps its persisted place behind the block.
    /// <para>
    /// Four distinct mutations fail here, which is why the fixture carries all three populations at once:
    /// folding the unranked one LAST gives [Z,B,A,X]; dropping it gives [B,A,X]; leaving ranked open
    /// paths unfolded gives [Z,X,B,A]; the unfixed tab-order fold gives [Z,B,A,X].
    /// </para>
    /// </summary>
    [Fact]
    public void SeedFoldsAnUnrankedOpenPathBehindTheRankedOnes()
    {
        var recentPaths = new List<string>();

        // X: ranked, closed. B and A: ranked, open. Z: open, absent from the persisted MRU.
        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            new List<string> { X, B, A },
            new List<string> { A, B, Z },
            argvPath: null);

        Assert.Equal(new List<string> { B, A, Z, X }, recentPaths);
    }

    /// <summary>
    /// B14 -- <c>work-items/bugs/2026-08-03-the-restore-seed-loop-does-not-apply-the-recent-list-cap.md</c>.
    /// The cap is the seed's POSTCONDITION, not a side effect of a fold happening to run. B6 feeds an
    /// over-length saved list too, but it also folds one open path -- and it is that FOLD which caps the
    /// result there, so B6 stays green with the seed's own cap entirely absent. The shape that escapes is a
    /// restore with an EMPTY open set and no argv document: nothing folds, nothing caps, and the
    /// over-length list is what the convergence <c>SaveSession</c> then writes back. The store swallows
    /// save failures (d11's accepted limitation), so that growth would be silent.
    /// <para>
    /// Pinned as a literal list rather than a count: trimming the HEAD satisfies a count assertion just as
    /// well while discarding exactly the most-recent entries the list exists to hold.
    /// </para>
    /// </summary>
    [Fact]
    public void SeedCapsAnOverLengthSavedListWhenNothingIsFolded()
    {
        var savedRecent = Enumerable.Range(1, 20).Select(i => $@"C:\a\r{i}.md").ToList();
        var recentPaths = new List<string>();

        ApplicateMainWindow.SeedRecentPathsForRestore(
            recentPaths,
            savedRecent,
            new List<string>(),
            argvPath: null);

        Assert.Equal(savedRecent.Take(ApplicateSession.MaxRecentPaths).ToList(), recentPaths);
    }

    /// <summary>
    /// B10 -- pins an implementation STYLE, not the single-owner guarantee. recentPaths is a captured
    /// local, so even a returning form would reassign the one shared closure field and every reader
    /// would still see exactly one list; claim 8's real enforcement is the marker's shape plus
    /// S6 (iii). The style is kept for its own reason: in-place mutation avoids a transient window in
    /// which a reference already handed to the VM mirror diverges from the host's list.
    /// </summary>
    [Fact]
    public void SeedMutatesTheCallersListInPlace()
    {
        var callerOwnedInstance = new List<string> { "stale-entry" };

        ApplicateMainWindow.SeedRecentPathsForRestore(
            callerOwnedInstance,
            new List<string> { A },
            new List<string> { B },
            argvPath: null);

        Assert.Equal(new List<string> { B, A }, callerOwnedInstance);
    }

    /// <summary>
    /// B11 -- the predicate is subtype-INCLUSIVE for IOException while EXCLUDING the programming and
    /// platform faults that derive from the admitted argument types. At least one SUBTYPE negative is
    /// mandatory: a negative drawn from outside the four hierarchies (InvalidOperationException)
    /// passes for every implementation, correct or over-catching, and so enforces nothing on its own.
    /// </summary>
    [Fact]
    public void IsUnusablePersistedPathIsSubtypeInclusiveButExcludesProgrammingFaults()
    {
        // Positives -- the normal shapes of an unusable persisted path.
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new IOException()));
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new UnauthorizedAccessException()));
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new ArgumentException()));
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new NotSupportedException()));
        // Subtype positives: exact-type matching would drop these, and they are what the runtime
        // actually throws for a stale saved path.
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new PathTooLongException()));
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new FileNotFoundException()));
        Assert.True(ApplicateMainWindow.IsUnusablePersistedPath(new DirectoryNotFoundException()));

        // Subtype negatives -- each derives from an admitted type, so a naive four-way `is` test
        // swallows all three. The MRU handler runs INSIDE these catch blocks (CollectionChanged is
        // raised synchronously inside the add), so over-catching here would silently eat a
        // programming fault thrown from the very code this change introduces.
        Assert.False(ApplicateMainWindow.IsUnusablePersistedPath(new ArgumentNullException()));
        Assert.False(ApplicateMainWindow.IsUnusablePersistedPath(new ArgumentOutOfRangeException()));
        Assert.False(ApplicateMainWindow.IsUnusablePersistedPath(new PlatformNotSupportedException()));
        // Outside all four hierarchies: catches only a widening to Exception.
        Assert.False(ApplicateMainWindow.IsUnusablePersistedPath(new InvalidOperationException()));
    }

    // ---------------------------------------------------------------------------------------------
    // D12 source-text guards. Without these the whole behavioural block above stays green with the
    // bug fully alive: a tested core that production never calls, or a replay open placed outside
    // the marker bracket, is invisible to any test of the core itself.
    // ---------------------------------------------------------------------------------------------

    /// <summary>S1 -- production must actually delegate to the tested core, WITH the live marker.</summary>
    [Fact]
    public void ProductionNoteRecentDocumentDelegatesToTheTestedCore()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var noteRecent = ExtractMethodBody(bridge, "void NoteRecentDocument(string? path)");

        Assert.Contains("NoteRecentDocumentCore(", noteRecent, StringComparison.Ordinal);
        Assert.Contains("replayFoldInFlight", noteRecent, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildRecentPaths", noteRecent, StringComparison.Ordinal);
    }

    /// <summary>S2 -- the up-front fold must precede the first mirror push.</summary>
    [Fact]
    public void RestoreSeedFoldsBeforeTheFirstMirrorPush()
    {
        var restore = ExtractRestoreRegion(out _, out _);

        AssertOrdered(
            restore,
            "SeedRecentPathsForRestore(",
            "viewModel.SetRecentFiles(recentPaths)",
            "The whole restore set must be folded BEFORE the list is first shown, so the first list the user sees is the final one.");
    }

    /// <summary>S3 -- argv must be resolved before the seed, with no await in between.</summary>
    [Fact]
    public void ArgvIsResolvedBeforeTheSeed()
    {
        var restore = ExtractRestoreRegion(out _, out _);

        var argvIndex = restore.IndexOf("var argvPath =", StringComparison.Ordinal);
        var seedIndex = restore.IndexOf("SeedRecentPathsForRestore(", StringComparison.Ordinal);
        Assert.True(argvIndex >= 0, "The restore should resolve argvPath.");
        Assert.True(seedIndex >= 0, "The restore should call the up-front seed.");
        Assert.True(seedIndex > argvIndex, "argv must be resolved BEFORE the seed, or the seed folds a null argv.");

        // No await between them: the seed must land in the same uninterruptible dispatcher turn as
        // the session load, so the user cannot interleave a remove or clear before it.
        Assert.DoesNotContain("await", restore[argvIndex..seedIndex], StringComparison.Ordinal);
    }

    /// <summary>S4 -- the convergence save must live in a finally, so an activation fault cannot lose it.</summary>
    [Fact]
    public void ConvergenceSaveRunsInAFinally()
    {
        var restore = ExtractRestoreRegion(out _, out _);
        var (_, finallyOpener) = LocateConvergenceTryFinally(restore);

        // NOT a substring test for "finally" -- one already exists (the restore loop's own).
        var precedingToken = restore[..finallyOpener].TrimEnd();
        Assert.EndsWith("finally", precedingToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// S5 -- the convergence try must NOT wrap the restore loop. Widening it there would let
    /// SaveSession persist a PARTIAL OpenPaths and permanently drop the tabs that had not opened yet.
    /// </summary>
    [Fact]
    public void ConvergenceFinallyDoesNotWrapTheRestoreLoop()
    {
        var restore = ExtractRestoreRegion(out _, out _);
        var (tryKeyword, _) = LocateConvergenceTryFinally(restore);

        var restoreDoneIndex = restore.IndexOf("isRestoring = false", StringComparison.Ordinal);
        Assert.True(restoreDoneIndex >= 0, "The restore loop's own finally should clear isRestoring.");
        Assert.True(
            tryKeyword > restoreDoneIndex,
            "The convergence try must open AFTER the restore loop's own finally, or a mid-loop fault persists a partial OpenPaths.");
    }

    /// <summary>
    /// S6 -- every replay open goes through the one marker bracket. Four independent assertions;
    /// each pins a distinct total-defeat mutation.
    /// </summary>
    [Fact]
    public void EveryReplayOpenGoesThroughTheMarkerBracket()
    {
        var restore = ExtractRestoreRegion(out _, out var bridge);
        var replay = ExtractMethodBody(restore, "async Task ReplayOpenAsync(string path, bool stub)");

        // (i) Exactly one of each open call in the whole restore region, and both inside the bracket.
        // A fourth, un-bracketed open raises a count and turns this RED.
        Assert.Equal(1, CountOccurrences(restore, "openDocs.OpenStubAsync("));
        Assert.Equal(1, CountOccurrences(restore, "openDocs.OpenAsync("));
        Assert.Contains("openDocs.OpenStubAsync(", replay, StringComparison.Ordinal);
        Assert.Contains("openDocs.OpenAsync(", replay, StringComparison.Ordinal);

        // (ii) The bracket actually SETS the marker from the path it is opening, and nulls it in its
        // own finally. Deliberately NOT pinned to the literal `replayFoldInFlight = path` so the
        // strictly-better normalized form stays green.
        var setters = MarkerAssignments(replay).Where(rhs => rhs != "null").ToList();
        Assert.Single(setters);
        Assert.Contains("path", setters[0], StringComparison.Ordinal);
        var replayFinally = replay.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(replayFinally >= 0, "The bracket must clear the marker in its own finally.");
        Assert.Contains("replayFoldInFlight = null;", replay[replayFinally..], StringComparison.Ordinal);

        // (iii) Every non-null marker writer lives INSIDE the restore region. That is the actual
        // invariant: the marker suppresses an MRU fold, so a writer reachable from a NON-replay path
        // could silence a genuine user activation. The writers that matter are the ones the restore
        // anchor cannot see -- one placed in NoteRecentDocument or the CollectionChanged handler,
        // both of which precede it.
        //
        // Asserted by LOCATION, not by count. The replay legitimately owns more than one bracket
        // (the per-path open here, and the post-restore Activate guarded by S6b), so a bare count
        // would go red on a correct second bracket while staying green on a smuggled writer that
        // merely replaced the first -- wrong in both directions.
        var bridgeSetters = MarkerAssignments(bridge).Where(rhs => rhs != "null").ToList();
        var restoreSetters = MarkerAssignments(restore).Where(rhs => rhs != "null").ToList();
        Assert.NotEmpty(restoreSetters);
        Assert.Equal(restoreSetters, bridgeSetters);

        // (iii-b) Every bracket CLEARS what it sets: one null-clear per non-null writer. A bracket
        // that sets and forgets to clear leaks the decline past its own call, and every later
        // activation of that path is silently dropped from the MRU for the rest of the session.
        var restoreClears = MarkerAssignments(restore).Where(rhs => rhs == "null").ToList();
        Assert.Equal(restoreSetters.Count, restoreClears.Count);

        // (iv) Both replay opens are AWAITED. `_ = ReplayOpenAsync(...)` suppresses CS4014 and would
        // fire every replay open concurrently against one arbitrarily-overwritten marker.
        Assert.Equal(2, CountOccurrences(restore, "await ReplayOpenAsync("));
    }

    /// <summary>
    /// S6b -- the post-restore activation is bracketed too. Once the MRU gained its activation
    /// trigger (<c>ApplicateMainWindow.HandleActiveDocumentChangedForRecent</c>, filed defect
    /// <c>bugs/2026-08-02-mru-records-openings-not-activations.md</c>), the restore tail's own
    /// <c>Activate</c> became an MRU-writing event -- and it is the REPLAY's action, not a use:
    /// nobody clicked, the restore merely finished.
    /// <para>
    /// Unbracketed, it re-folds the activated path to the head of the recent list, which undoes a
    /// remove or clear the user made during the restore window -- exactly the d12 defect the marker
    /// exists to prevent -- and the convergence save then persists the resurrected entry.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePostRestoreActivationGoesThroughTheMarkerBracket()
    {
        var restore = ExtractRestoreRegion(out _, out _);

        var activateIndex = restore.IndexOf("openDocs.Activate(toActivate);", StringComparison.Ordinal);
        Assert.True(activateIndex >= 0, "The restore tail should activate the chosen document.");
        Assert.Equal(1, CountOccurrences(restore, "openDocs.Activate("));

        // The marker is set from the path being activated, BEFORE the activation.
        var setterIndex = restore.LastIndexOf(
            "replayFoldInFlight = toActivate.FilePath;",
            activateIndex,
            StringComparison.Ordinal);
        Assert.True(
            setterIndex >= 0,
            "The post-restore Activate must name its path in the marker before firing.");

        // Nothing but the bracket's own opening sits between the setter and the activation: an
        // awaited call in that gap would let a user activation land while the marker is held.
        var betweenSetterAndActivate = restore[setterIndex..activateIndex];
        Assert.DoesNotContain("await ", betweenSetterAndActivate, StringComparison.Ordinal);

        // ...and it is cleared in the bracket's own finally, not merely after the call.
        var finallyIndex = restore.IndexOf("finally", activateIndex, StringComparison.Ordinal);
        Assert.True(finallyIndex > activateIndex, "The activation bracket must have a finally.");
        var clearIndex = restore.IndexOf(
            "replayFoldInFlight = null;",
            finallyIndex,
            StringComparison.Ordinal);
        Assert.True(clearIndex > finallyIndex, "The activation bracket must clear the marker in its finally.");

        // The clear belongs to THIS finally, not a later one: no second finally intervenes.
        var nextFinally = restore.IndexOf("finally", finallyIndex + "finally".Length, StringComparison.Ordinal);
        Assert.True(
            nextFinally < 0 || clearIndex < nextFinally,
            "The clear must sit in the activation bracket's own finally.");
    }

    /// <summary>S7 -- both persisted-path catch sites share the ONE predicate.</summary>
    [Fact]
    public void BothPersistedPathCatchesUseTheOnePredicate()
    {
        var restore = ExtractRestoreRegion(out _, out _);

        Assert.Equal(2, CountOccurrences(restore, "IsUnusablePersistedPath"));

        // At least one occurrence must be past the argv guard: a substring test over the region
        // stays green when only the per-path catch is widened and the argv catch is left behind.
        var argvGuard = restore.IndexOf("if (!string.IsNullOrWhiteSpace(argvPath))", StringComparison.Ordinal);
        Assert.True(argvGuard >= 0, "The argv open should stay guarded.");
        var lastPredicate = restore.LastIndexOf("IsUnusablePersistedPath", StringComparison.Ordinal);
        Assert.True(lastPredicate > argvGuard, "The argv catch must use the same one predicate.");
    }

    /// <summary>
    /// S8 -- three-operand sandwich. A plain "before the await" order test is satisfied by putting
    /// the assignment in the `toActivate is null` fallback block, which leaves the common
    /// preferred-path case writing a null ActivePath over a correct on-disk value.
    /// </summary>
    [Fact]
    public void LastActivePathIsSeededInsideTheActivateGuardAndBeforeTheAwait()
    {
        var restore = ExtractRestoreRegion(out _, out _);

        var guardIndex = restore.IndexOf("if (toActivate is not null)", StringComparison.Ordinal);
        var seedIndex = restore.IndexOf("lastActivePath =", StringComparison.Ordinal);
        var awaitIndex = restore.IndexOf("EnsureLoadedAsync(", StringComparison.Ordinal);

        Assert.True(guardIndex >= 0, "The activation guard should exist.");
        Assert.True(seedIndex >= 0, "The restore should seed lastActivePath.");
        Assert.True(awaitIndex >= 0, "The activation should still ensure the document is loaded.");
        Assert.True(seedIndex > guardIndex, "The seed must be INSIDE the activate guard, not in the null fallback block.");
        Assert.True(awaitIndex > seedIndex, "The seed must land BEFORE the first throwing await.");
    }

    // ---------------------------------------------------------------------------------------------
    // D13 (decision 2026-07-26-d13-persisted-session-observation-contract): a persisted session this
    // process could not READ is never overwritten by this process. The composer is pure, so the
    // POLICY is proven behaviourally; the WIRING that calls it is source-text plus the compiler.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// C-R1 -- the filed defect's host half, made executable. Every input except the observation is
    /// deliberately POPULATED: a composer that ignores the observation returns a perfectly plausible
    /// non-null snapshot here, and that snapshot IS the data loss.
    /// </summary>
    [Fact]
    public void ComposeRefusesWhenThePersistedStateWasNotObserved()
    {
        var result = ApplicateMainWindow.ComposeSessionSnapshot(
            observedSession: null,
            new List<string> { A, B },
            activeDocumentPath: A,
            lastActivePath: B,
            new List<string> { X });

        Assert.Null(result);
    }

    /// <summary>
    /// C-R2 -- the snapshot carries the LIVE lists, by reference, not the observed baseline's. The
    /// baseline's lists are deliberately different, so a baseline-sourced composition (Phase 2's
    /// restore-window branch landing a phase early, on the path where it is wrong) fails on value AND
    /// on reference, and a defensive copy fails on reference. Assert.Same pins that the extraction is
    /// a pure MOVE: the production code aliases the caller's lists today.
    /// </summary>
    [Fact]
    public void ComposeCarriesTheLiveListsWhenTheStateWasObserved()
    {
        var observed = new ApplicateSession
        {
            OpenPaths = new List<string> { C },
            RecentPaths = new List<string> { C },
        };
        var openPaths = new List<string> { A, B };
        var recentPaths = new List<string> { X };

        var result = ApplicateMainWindow.ComposeSessionSnapshot(
            observed,
            openPaths,
            activeDocumentPath: A,
            lastActivePath: B,
            recentPaths);

        Assert.NotNull(result);
        Assert.Same(openPaths, result.OpenPaths);
        Assert.Same(recentPaths, result.RecentPaths);
        Assert.Equal(new List<string> { A, B }, result.OpenPaths);
        Assert.Equal(new List<string> { X }, result.RecentPaths);
    }

    /// <summary>
    /// C-R3 -- PathIfStillOpen's first direct coverage anywhere in the repo. Case (c) is the one the
    /// diff cannot show: the OrdinalIgnoreCase comparer is an ARGUMENT, so dropping it in the move
    /// still compiles and still passes (a) and (b), while silently rejecting a correct ActivePath
    /// that differs only in case -- the next startup then loses the active tab.
    /// </summary>
    [Fact]
    public void ComposeRejectsAnActivePathThatIsNotOpen()
    {
        var observed = ApplicateSession.Empty;

        var fallback = ApplicateMainWindow.ComposeSessionSnapshot(
            observed, new List<string> { A, B }, C, B, new List<string>());
        Assert.NotNull(fallback);
        Assert.Equal(B, fallback.ActivePath);

        var bothDangling = ApplicateMainWindow.ComposeSessionSnapshot(
            observed, new List<string> { A, B }, C, C, new List<string>());
        Assert.NotNull(bothDangling);
        Assert.Null(bothDangling.ActivePath);

        var caseDiffering = ApplicateMainWindow.ComposeSessionSnapshot(
            observed, new List<string> { @"C:\a\One.md" }, @"C:\a\one.md", null, new List<string>());
        Assert.NotNull(caseDiffering);
        Assert.Equal(@"C:\a\one.md", caseDiffering.ActivePath);
    }

    /// <summary>
    /// S1 -- the value handed to SaveAsync is the one the composer returned. This does NOT police how
    /// the refusal is SPELLED; that is the compiler's job (SaveAsync takes a non-nullable session, so
    /// every weakening that leaves the composer's value on the write path is CS8604). S1 asks only
    /// what the compiler cannot: is this the composer's value, or a construction, a coalesce, a
    /// null-forgive, or a second source?
    /// </summary>
    [Fact]
    public void SaveSessionWritesOnlyWhatTheComposerReturned()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var body = ExtractMethodBody(codeBehind, "void SaveSession()");

        Assert.Equal(1, CountOccurrences(body, "var snapshot = ComposeSessionSnapshot("));
        Assert.DoesNotContain("ApplicateSession", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new", body, StringComparison.Ordinal);
        Assert.DoesNotContain("??", body, StringComparison.Ordinal);
        Assert.Equal(CountOccurrences(body, "!="), CountOccurrences(body, "!"));

        var composeIndex = body.IndexOf("var snapshot = ComposeSessionSnapshot(", StringComparison.Ordinal);
        var writeIndex = body.IndexOf("sessionStore.SaveAsync(snapshot)", StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "SaveSession should compose through ComposeSessionSnapshot.");
        Assert.True(writeIndex >= 0, "SaveSession should still write the composed snapshot.");
        Assert.True(writeIndex > composeIndex, "The composition must precede the write.");

        Assert.Equal(1, CountOccurrences(body, "sessionStore.SaveAsync(snapshot)"));
    }

    /// <summary>
    /// S2 -- the observation is assigned exactly once, from the window-side observation owner, and
    /// never cleared. Both halves are load-bearing and neither subsumes the other: a coalesce on the
    /// RHS is caught only by the negative half, and an assignment from the already-coalesced local
    /// only by the positive half. The regex's optional <c>??</c> arm makes the compound <c>??=</c>
    /// spelling count as a third assignment, which is the form that slipped past an earlier version
    /// of this guard.
    ///
    /// <para>The RHS used to be the store call itself. It is now
    /// <c>ObserveStartupSessionOnce()</c> -- one indirection out, because the bridge and the
    /// constructor's reveal-hold decision must consume ONE observation rather than read the store
    /// twice (<c>bugs/2026-07-26-reveal-gate-held-for-a-document-that-never-arrives.md</c>). What
    /// that owner is allowed to return is guarded by
    /// <see cref="TheWindowHasExactlyOneSessionLoadCallSite"/>; this test still owns "assigned once,
    /// uncoalesced, never cleared".</para>
    /// </summary>
    [Fact]
    public void ObservedSessionIsAssignedOnceFromTheStoreAndNeverCleared()
    {
        var codeBehind = ReadMainWindowCodeBehind();
        var bridge = ExtractMethodBody(
            codeBehind,
            "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");

        var assignments = System.Text.RegularExpressions.Regex
            .Matches(bridge, @"observedSession\s*(?:\?\?)?=\s*(?<rhs>[^;=][^;]*);")
            .Select(match => match.Groups["rhs"].Value.Trim())
            .ToList();

        Assert.Equal(2, assignments.Count);
        var loadRhs = Assert.Single(assignments, rhs => rhs != "null");
        Assert.Contains("ObserveStartupSessionOnce(", loadRhs, StringComparison.Ordinal);
        Assert.DoesNotContain("??", loadRhs, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateSession.Empty", loadRhs, StringComparison.Ordinal);

        Assert.DoesNotMatch(@"\bout\s+observedSession\b", bridge);
        Assert.DoesNotMatch(@"\bref\s+observedSession\b", bridge);
    }

    /// <summary>
    /// S2b -- ONE physical window-side session read exists in the whole code-behind, it lives in the
    /// memoized owner, and that owner never forges <c>ApplicateSession.Empty</c> out of a failure to
    /// observe (d13).
    ///
    /// <para>The behavioural guard for this is
    /// <c>ApplicateMainWindowStartupSessionTests.WindowSideStartupObservationIsPerformedExactlyOnceAndRestoresItsDocument</c>,
    /// which counts real calls against a fake store. This source-text test is the cheap companion
    /// that fires on the SHAPE -- a third consumer added later gets caught at the call site rather
    /// than only when someone happens to run the headless window test.</para>
    ///
    /// <para><b>Limit.</b> The count keys on the <c>.LoadAsync(</c> spelling. A read routed through a
    /// delegate or an extension method would not be seen here; the behavioural count test is what
    /// covers that.</para>
    /// </summary>
    [Fact]
    public void TheWindowHasExactlyOneSessionLoadCallSite()
    {
        var codeBehind = ReadMainWindowCodeBehind();

        Assert.Equal(1, CountOccurrences(codeBehind, ".LoadAsync("));
        Assert.Equal(1, CountOccurrences(codeBehind, "_startupSessionObservation ??="));

        var owner = ExtractMethodBody(
            codeBehind,
            "private static async Task<ApplicateSession?> LoadStartupSessionOnceAsync()");

        Assert.Contains("sessionStore.LoadAsync()", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicateSession.Empty", owner, StringComparison.Ordinal);
    }

    /// <summary>
    /// S3 -- ExtractRestoreRegion anchors every restore guard on this exact literal. Rewriting it
    /// silently re-bases the slice they are all measured over, and they would most likely stay green
    /// while asserting about a different region.
    /// </summary>
    [Fact]
    public void RestoreRegionAnchorSurvives()
    {
        var codeBehind = ReadMainWindowCodeBehind();

        Assert.Equal(1, CountOccurrences(codeBehind, "ApplicateSession saved = ApplicateSession.Empty"));
    }

    /// <summary>
    /// The restore lambda, anchored on the unique <c>ApplicateSession saved</c> declaration rather
    /// than on a <c>Dispatcher.UIThread.Post</c> (there are three). Anchoring here also puts the
    /// earlier bridge open at <c>openDocs.OpenAsync(path)</c> out of scope, so the open counts in S6
    /// mean what they say.
    /// </summary>
    private static string ExtractRestoreRegion(out string codeBehind, out string bridge)
    {
        codeBehind = ReadMainWindowCodeBehind();
        bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var anchor = bridge.IndexOf("ApplicateSession saved = ApplicateSession.Empty", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "The restore lambda's session declaration should exist.");
        return bridge[anchor..];
    }

    /// <summary>Right-hand sides of every <c>replayFoldInFlight</c> ASSIGNMENT (never a comparison).</summary>
    private static List<string> MarkerAssignments(string region)
        => System.Text.RegularExpressions.Regex
            .Matches(region, @"replayFoldInFlight\s*=\s*(?<rhs>[^;=][^;]*);")
            .Select(match => match.Groups["rhs"].Value.Trim())
            .ToList();

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Locate the convergence <c>try</c>/<c>finally</c> STRUCTURALLY: from the last
    /// <c>SaveSession();</c>, walk back by brace depth to its enclosing block opener, then across to
    /// the try block it belongs to. Returns the index of the <c>try</c> keyword and of the
    /// <c>finally</c> block's opening brace.
    /// </summary>
    private static (int TryKeyword, int FinallyOpener) LocateConvergenceTryFinally(string region)
    {
        var lastSave = region.LastIndexOf("SaveSession();", StringComparison.Ordinal);
        Assert.True(lastSave >= 0, "The restore should end with a consolidated save.");

        var finallyOpener = EnclosingBlockOpener(region, lastSave);
        Assert.True(finallyOpener >= 0, "The consolidated save should sit inside a block.");

        var beforeFinallyKeyword = region[..finallyOpener].TrimEnd();
        Assert.EndsWith("finally", beforeFinallyKeyword, StringComparison.Ordinal);

        var beforeTryClose = beforeFinallyKeyword[..^"finally".Length].TrimEnd();
        Assert.EndsWith("}", beforeTryClose, StringComparison.Ordinal);

        var tryBlockOpener = MatchingOpenBrace(region, beforeTryClose.Length - 1);
        Assert.True(tryBlockOpener >= 0, "The finally should be paired with a try block.");

        var beforeTryKeyword = region[..tryBlockOpener].TrimEnd();
        Assert.EndsWith("try", beforeTryKeyword, StringComparison.Ordinal);

        return (beforeTryKeyword.Length - "try".Length, finallyOpener);
    }

    /// <summary>Walk BACK from an index to the <c>{</c> that opens its enclosing block.</summary>
    private static int EnclosingBlockOpener(string region, int index)
    {
        var depth = 0;
        for (var cursor = index; cursor >= 0; cursor--)
        {
            if (region[cursor] == '}')
            {
                depth++;
            }
            else if (region[cursor] == '{')
            {
                if (depth == 0)
                {
                    return cursor;
                }

                depth--;
            }
        }

        return -1;
    }

    /// <summary>Walk BACK from a <c>}</c> to its matching <c>{</c>.</summary>
    private static int MatchingOpenBrace(string region, int closeIndex)
    {
        var depth = 0;
        for (var cursor = closeIndex; cursor >= 0; cursor--)
        {
            if (region[cursor] == '}')
            {
                depth++;
            }
            else if (region[cursor] == '{')
            {
                depth--;
                if (depth == 0)
                {
                    return cursor;
                }
            }
        }

        return -1;
    }

    private static void AssertOrdered(string region, string earlier, string later, string because)
    {
        var earlierIndex = region.IndexOf(earlier, StringComparison.Ordinal);
        var laterIndex = region.IndexOf(later, StringComparison.Ordinal);
        Assert.True(earlierIndex >= 0, $"'{earlier}' should exist. {because}");
        Assert.True(laterIndex >= 0, $"'{later}' should exist. {because}");
        Assert.True(laterIndex > earlierIndex, because);
    }

    /// <summary>
    /// The two VM-event subscriptions, bounded from <c>RecentFileRemoveRequested</c> up to (not
    /// including) the pre-existing <c>CollectionChanged</c> subscription that follows them.
    /// </summary>
    private static string ExtractRecentFilesHandlersRegion(out string codeBehind, out string bridge)
    {
        codeBehind = ReadMainWindowCodeBehind();
        bridge = ExtractMethodBody(codeBehind, "private void InstallActiveDocumentBridge(MainWindowViewModel viewModel)");
        var removeIndex = bridge.IndexOf("viewModel.RecentFileRemoveRequested", StringComparison.Ordinal);
        var clearIndex = bridge.IndexOf("viewModel.RecentFilesClearRequested", StringComparison.Ordinal);
        var collectionChangedIndex = bridge.IndexOf(
            "((INotifyCollectionChanged)openDocs.OpenDocuments).CollectionChanged",
            StringComparison.Ordinal);

        Assert.True(removeIndex >= 0, "RecentFileRemoveRequested subscription should exist.");
        Assert.True(clearIndex > removeIndex, "RecentFilesClearRequested subscription should follow the remove subscription.");
        Assert.True(collectionChangedIndex > clearIndex, "Both subscriptions should precede the pre-existing CollectionChanged subscription.");

        return bridge[removeIndex..collectionChangedIndex];
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

    private static string ReadSessionStoreSource()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "MarkMello.Applicate.Desktop",
            "Editing",
            "JsonApplicateSessionStore.cs"));

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
