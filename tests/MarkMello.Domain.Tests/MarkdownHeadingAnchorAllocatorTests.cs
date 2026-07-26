using MarkMello.Domain;

namespace MarkMello.Domain.Tests;

public sealed class MarkdownHeadingAnchorAllocatorTests
{
    private static List<string> AllocateAll(params string[] headingTexts)
    {
        var allocator = new MarkdownHeadingAnchorAllocator();
        var anchors = new List<string>(headingTexts.Length);
        foreach (var text in headingTexts)
        {
            anchors.Add(allocator.Allocate(text));
        }

        return anchors;
    }

    private static void AssertNoCollisions(IReadOnlyList<string> anchors)
    {
        var colliding = anchors
            .Where(anchor => anchor.Length > 0)
            .GroupBy(anchor => anchor, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToList();

        Assert.True(
            colliding.Count == 0,
            $"Headings share an anchor, so the shadowed ones are unreachable: {string.Join(", ", colliding)}. "
                + $"Allocated: [{string.Join(", ", anchors)}]");
    }

    [Fact]
    public void IdenticallyTitledHeadingsNeverShareAnAnchor()
    {
        var anchors = AllocateAll("Repeated Title", "Repeated Title", "Repeated Title", "Repeated Title", "Repeated Title");

        AssertNoCollisions(anchors);
        Assert.Equal(
            ["repeated-title", "repeated-title-1", "repeated-title-2", "repeated-title-3", "repeated-title-4"],
            anchors);
    }

    [Fact]
    public void FirstHeadingKeepsTheBareSlugSoExistingLinksStillResolve()
    {
        Assert.Equal(["intro", "details"], AllocateAll("Intro", "Details"));
    }

    [Fact]
    public void GeneratedSuffixDoesNotStealAnAnchorAnotherHeadingAlreadyOwns()
    {
        // "Title 1" slugs to "title-1", which the second "Title" already took. A bare per-base
        // counter would hand out "title-1" twice.
        var anchors = AllocateAll("Title", "Title", "Title 1");

        AssertNoCollisions(anchors);
        Assert.Equal(["title", "title-1", "title-1-1"], anchors);
    }

    [Fact]
    public void EarlierOwnerOfAContestedAnchorKeepsIt()
    {
        var anchors = AllocateAll("Title 1", "Title", "Title");

        AssertNoCollisions(anchors);

        // The link "#title-1" pointed at the "Title 1" heading before this fix and still does.
        Assert.Equal(["title-1", "title", "title-2"], anchors);
    }

    [Fact]
    public void UnsluggableHeadingYieldsNoAnchorAndDoesNotShiftLaterNumbering()
    {
        var anchors = AllocateAll("...", "Notes", "Notes");

        Assert.Equal([string.Empty, "notes", "notes-1"], anchors);
    }

    [Fact]
    public void SeparateDocumentsDoNotShareNumbering()
    {
        Assert.Equal(AllocateAll("Notes", "Notes"), AllocateAll("Notes", "Notes"));
    }

    [Fact]
    public void DuplicateDetectionIsCaseAndPunctuationInsensitiveTheSameWayTheSluggerIs()
    {
        // Both slug to "notes", so they are duplicates even though the raw texts differ.
        var anchors = AllocateAll("Notes", "notes!");

        AssertNoCollisions(anchors);
        Assert.Equal(["notes", "notes-1"], anchors);
    }
}
