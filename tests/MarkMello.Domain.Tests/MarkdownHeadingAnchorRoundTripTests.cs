using MarkMello.Domain;

namespace MarkMello.Domain.Tests;

/// <summary>
/// Link resolution goes `#fragment` -> <see cref="MarkdownHeadingAnchorSlugger.TryNormalizeFragment"/>
/// -> <see cref="MarkdownHeadingAnchorSlugger.CreateAnchor"/>, and the result is matched against the
/// anchor a heading was ALLOCATED. So an in-document link can only reach a de-duplicated heading if
/// the suffixed form survives `CreateAnchor` unchanged.
///
/// <para>These guards pin that round-trip for the `-1` / `-2` suffix format specifically: digits are
/// letter-or-digit so they are kept, and a single interior hyphen is preserved (only RUNS collapse and
/// only a TRAILING hyphen is trimmed). If the suffix format ever changes, these fail first.</para>
/// </summary>
public sealed class MarkdownHeadingAnchorRoundTripTests
{
    [Theory]
    [InlineData("repeated-title")]
    [InlineData("repeated-title-1")]
    [InlineData("repeated-title-2")]
    [InlineData("repeated-title-12")]
    [InlineData("repeated-title-1-1")]
    [InlineData("раздел-1")]
    public void SuffixedAnchorSurvivesCreateAnchorUnchanged(string allocatedAnchor)
    {
        Assert.Equal(allocatedAnchor, MarkdownHeadingAnchorSlugger.CreateAnchor(allocatedAnchor));
    }

    [Theory]
    [InlineData("#repeated-title-1", "repeated-title-1")]
    [InlineData("#repeated-title-2", "repeated-title-2")]
    [InlineData("#%D1%80%D0%B0%D0%B7%D0%B4%D0%B5%D0%BB-1", "раздел-1")]
    public void FragmentNormalizationReachesTheSuffixedAnchor(string href, string expected)
    {
        Assert.True(MarkdownHeadingAnchorSlugger.TryNormalizeFragment(href, out var anchor));
        Assert.Equal(expected, anchor);
    }
}
