using System.Globalization;

namespace MarkMello.Domain;

/// <summary>
/// Allocates a DOCUMENT-UNIQUE anchor for each heading, in render order.
///
/// <para><see cref="MarkdownHeadingAnchorSlugger.CreateAnchor(string)"/> is a pure function of the
/// heading text, so two identically-titled headings necessarily slug to the same value. That is
/// correct for a text-to-slug primitive but wrong for an HTML <c>id</c>, which must be unique within
/// the document: colliding ids make every duplicate row in the Table of Contents resolve to the LAST
/// heading with that title, and the shadowed rows become permanently unreachable.</para>
///
/// <para>This type is the single owner of that de-duplication for BOTH render paths — the WebView
/// (primary) HTML renderer and the native fallback view. Neither path may keep a private counter:
/// the suffix format is part of the link contract (see the round-trip note below), so a second
/// implementation is a drift hazard, not a convenience.</para>
///
/// <para><b>Suffix format</b> — the first heading with a given base keeps the bare slug, and each
/// later one takes <c>base-1</c>, <c>base-2</c>, ... This is the format the native path has emitted
/// all along, and it matches GitHub's convention.</para>
///
/// <para><b>Round-trip contract</b> — link resolution runs <c>#fragment</c> through
/// <see cref="MarkdownHeadingAnchorSlugger.TryNormalizeFragment"/>, which slugs the fragment again
/// before matching it against an allocated anchor. The suffixed form therefore has to survive
/// <see cref="MarkdownHeadingAnchorSlugger.CreateAnchor(string)"/> unchanged, or in-document links to
/// de-duplicated headings would not resolve. Digits are kept and a single interior hyphen is
/// preserved, so it does. That property is format-dependent and is pinned by
/// <c>MarkdownHeadingAnchorRoundTripTests</c>.</para>
///
/// <para><b>Lifetime</b> — one instance per document render, owned by the caller, discarded with the
/// render. It carries mutable per-document state and is NOT thread-safe; both current callers
/// allocate strictly sequentially in document order.</para>
/// </summary>
public sealed class MarkdownHeadingAnchorAllocator
{
    private readonly Dictionary<string, int> _nextSuffixByBase = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allocated = new(StringComparer.Ordinal);

    /// <summary>
    /// Allocates the anchor for the next heading, given its inline content.
    /// </summary>
    public string Allocate(IReadOnlyList<MarkdownInline> inlines)
        => AllocateForBase(MarkdownHeadingAnchorSlugger.CreateAnchor(inlines));

    /// <summary>
    /// Allocates the anchor for the next heading, given its raw text.
    /// </summary>
    public string Allocate(string headingText)
        => AllocateForBase(MarkdownHeadingAnchorSlugger.CreateAnchor(headingText));

    private string AllocateForBase(string baseAnchor)
    {
        // A heading that slugs to nothing (empty, or punctuation only) has no anchor to
        // de-duplicate. Return empty WITHOUT consuming a suffix, so a document that opens with an
        // unsluggable heading does not shift the numbering of everything after it.
        if (baseAnchor.Length == 0)
        {
            return string.Empty;
        }

        var suffix = _nextSuffixByBase.TryGetValue(baseAnchor, out var next) ? next : 0;

        // The counter alone is not enough for uniqueness: "Title", "Title", "Title 1" would hand the
        // third heading the "title-1" the second one already took. Skip forward until the candidate
        // is genuinely free, which keeps the invariant this type exists to hold — no two headings in
        // one document share an anchor — without changing the suffix format.
        string anchor;
        while (true)
        {
            anchor = suffix == 0
                ? baseAnchor
                : string.Create(CultureInfo.InvariantCulture, $"{baseAnchor}-{suffix}");
            suffix++;

            if (_allocated.Add(anchor))
            {
                break;
            }
        }

        _nextSuffixByBase[baseAnchor] = suffix;
        return anchor;
    }
}
