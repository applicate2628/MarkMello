using System.Net;
using System.Text.RegularExpressions;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Rendering;
using MarkMello.Domain;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// The WebView renderer is the PRIMARY render path, and the Table of Contents tells its rows apart by
/// the heading's HTML <c>id</c>. Two headings sharing an id make every duplicate row highlight and
/// scroll to the LAST of them, leaving the shadowed rows permanently unreachable — so id uniqueness
/// is the property under test here, not the suffix spelling.
/// </summary>
public sealed class ApplicateHeadingAnchorUniquenessTests
{
    private static readonly Regex HeadingIdPattern = new(
        "<h[1-6][^>]*\\sid=\"([^\"]*)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static async Task<string> RenderAsync(string markdown)
    {
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource("headings.md", "headings.md", markdown);
        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        return document.Html;
    }

    /// <summary>
    /// The renderer writes the id through <c>HtmlEncoder</c>, so a non-ASCII anchor appears in the
    /// HTML source as numeric character references (<c>&amp;#x437;</c>...). The browser decodes those
    /// back, and it is the DECODED value that becomes the DOM id the TOC matches on — so decode here
    /// too, or this would compare against the wire spelling rather than the effective id.
    /// </summary>
    private static List<string> HeadingIds(string html)
        => HeadingIdPattern.Matches(html)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToList();

    private static void AssertNoCollidingIds(IReadOnlyList<string> ids)
    {
        var colliding = ids
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToList();

        Assert.True(
            colliding.Count == 0,
            $"Headings share an HTML id, so their TOC rows cannot be told apart: {string.Join(", ", colliding)}. "
                + $"Emitted: [{string.Join(", ", ids)}]");
    }

    [Fact]
    public async Task IdenticallyTitledHeadingsGetDistinctHtmlIds()
    {
        // The shape from the runtime sighting: five verbatim-identical headings, which previously all
        // rendered as id="repeated-title".
        var html = await RenderAsync(string.Concat(Enumerable.Repeat("## Repeated Title\n\nbody\n\n", 5)));

        var ids = HeadingIds(html);

        AssertNoCollidingIds(ids);
        Assert.Equal(
            ["repeated-title", "repeated-title-1", "repeated-title-2", "repeated-title-3", "repeated-title-4"],
            ids);
    }

    [Fact]
    public async Task RealisticRepeatedSectionTitlesGetDistinctHtmlIds()
    {
        var html = await RenderAsync(
            "# Guide\n\n## Alpha\n\n### Notes\n\n## Beta\n\n### Notes\n\n## Gamma\n\n### Notes\n");

        var ids = HeadingIds(html);

        AssertNoCollidingIds(ids);
        Assert.Equal(["guide", "alpha", "notes", "beta", "notes-1", "gamma", "notes-2"], ids);
    }

    [Fact]
    public async Task TocFacingHeadingListCarriesTheSameDistinctAnchors()
    {
        // The renderer also publishes the anchor on ApplicateHtmlHeading; if that copy kept the
        // colliding value the TOC would still be unable to separate the rows.
        var renderer = new ApplicateHtmlMarkdownRenderer();
        var source = new MarkdownSource(
            "headings.md",
            "headings.md",
            "## Repeated Title\n\n## Repeated Title\n\n## Repeated Title\n");

        var document = await renderer.RenderAsync(
            source,
            ReadingPreferences.Default,
            imageSourceResolver: null,
            CancellationToken.None);

        var anchors = document.Headings.Select(heading => heading.Anchor).ToList();

        AssertNoCollidingIds(anchors);
        Assert.Equal(["repeated-title", "repeated-title-1", "repeated-title-2"], anchors);
        Assert.Equal(HeadingIds(document.Html), anchors);
    }

    [Fact]
    public async Task UniquelyTitledHeadingsKeepTheirBareSlugs()
    {
        // Only-forward guard: de-duplication must not rename anchors in the ordinary case, or every
        // existing in-document link would break.
        var html = await RenderAsync("# Intro\n\n## Details\n\n## Зачем нужна эта документация\n");

        Assert.Equal(["intro", "details", "зачем-нужна-эта-документация"], HeadingIds(html));
    }
}
