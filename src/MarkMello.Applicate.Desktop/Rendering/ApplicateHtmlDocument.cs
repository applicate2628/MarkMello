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
    int BlockIndex);

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
