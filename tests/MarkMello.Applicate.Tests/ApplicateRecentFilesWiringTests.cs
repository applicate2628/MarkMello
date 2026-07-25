using MarkMello.Applicate.Desktop;
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

        foreach (var region in new[] { handlers, removeHelper, clearHelper })
        {
            Assert.DoesNotContain("Timer", region, StringComparison.Ordinal);
            Assert.DoesNotContain("DispatcherTimer", region, StringComparison.Ordinal);
            Assert.DoesNotContain("Task.Delay", region, StringComparison.Ordinal);
            Assert.DoesNotContain("Delay(", region, StringComparison.Ordinal);
        }
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
