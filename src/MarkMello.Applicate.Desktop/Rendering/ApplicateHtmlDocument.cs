namespace MarkMello.Applicate.Desktop.Rendering;

public sealed record ApplicateHtmlDocument(
    string Html,
    string PlainText,
    IReadOnlyList<ApplicateHtmlHeading> Headings,
    IReadOnlyList<ApplicateHtmlBlockMarker> Blocks);

public sealed record ApplicateHtmlHeading(
    int Level,
    string Text,
    string Anchor,
    int BlockIndex,
    IReadOnlyList<ApplicateHtmlHeadingInline> Inlines)
{
    public ApplicateHtmlHeading(int Level, string Text, string Anchor, int BlockIndex)
        : this(Level, Text, Anchor, BlockIndex, CreatePlainFallbackInlines(Text))
    {
    }

    private static IReadOnlyList<ApplicateHtmlHeadingInline> CreatePlainFallbackInlines(string text)
        => string.IsNullOrEmpty(text)
            ? []
            : [new ApplicateHtmlHeadingInline(ApplicateHtmlHeadingInlineKind.Text, text)];
}

public enum ApplicateHtmlHeadingInlineKind
{
    Text,
    Math,
}

public sealed record ApplicateHtmlHeadingInline(ApplicateHtmlHeadingInlineKind Kind, string Text);

public sealed record ApplicateHtmlBlockMarker(
    int BlockIndex,
    string Kind,
    string PlainText);

public sealed record ApplicateRenderedBody(
    string BodyHtml,
    string PlainText,
    IReadOnlyList<ApplicateHtmlHeading> Headings,
    IReadOnlyList<ApplicateHtmlBlockMarker> Blocks,
    IReadOnlyList<int> TopLevelBlockEndOffsets,
    bool HasMermaidBlock,
    bool HasCodeBlockWithSyntax,
    string? RendererCacheKeySuffix = null);
