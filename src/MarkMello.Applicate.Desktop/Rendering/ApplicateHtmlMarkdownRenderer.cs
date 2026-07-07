using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using MarkMello.Application.Abstractions;
using MarkMello.Applicate.Desktop.Math;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

public sealed class ApplicateHtmlMarkdownRenderer : IApplicateHtmlMarkdownRenderer
{
    private const int MaxInlineImageBytes = 5 * 1024 * 1024;

    private readonly ApplicateMarkdownDocumentRenderer _markdownRenderer;
    private readonly ApplicateWebAssetEmbedder? _assetEmbedder;

    public ApplicateHtmlMarkdownRenderer()
        : this(new ApplicateMarkdownDocumentRenderer(PreserveTexForKatex), assetEmbedder: null)
    {
    }

    public ApplicateHtmlMarkdownRenderer(ApplicateWebAssetEmbedder assetEmbedder)
        : this(new ApplicateMarkdownDocumentRenderer(PreserveTexForKatex), assetEmbedder)
    {
    }

    internal ApplicateHtmlMarkdownRenderer(
        ApplicateMarkdownDocumentRenderer markdownRenderer,
        ApplicateWebAssetEmbedder? assetEmbedder = null)
    {
        _markdownRenderer = markdownRenderer;
        _assetEmbedder = assetEmbedder;
    }

    private static string PreserveTexForKatex(string tex)
    {
        ArgumentNullException.ThrowIfNull(tex);
        return tex.Trim();
    }

    public async Task<ApplicateHtmlDocument> RenderAsync(
        MarkdownSource source,
        ReadingPreferences preferences,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken)
    {
        var context = await RenderMarkdownToContextAsync(source, imageSourceResolver, cancellationToken).ConfigureAwait(false);
        var body = context.Html.ToString();

        var baseAssets = _assetEmbedder is null
            ? ApplicateWebBaseAssets.Empty
            : await _assetEmbedder.LoadBaseBundleAsync(cancellationToken).ConfigureAwait(false);

        ApplicateWebMermaidAssets? mermaidAssets = null;
        if (context.HasMermaidBlock && _assetEmbedder is not null)
        {
            mermaidAssets = await _assetEmbedder.LoadMermaidAsync(cancellationToken).ConfigureAwait(false);
        }

        // hljs included для mermaid fallback (when render fails, source remains
        // as code block that may be highlighted) as well as regular code blocks.
        ApplicateWebHighlightAssets? hljsAssets = null;
        if ((context.HasCodeBlockWithSyntax || context.HasMermaidBlock) && _assetEmbedder is not null)
        {
            hljsAssets = await _assetEmbedder.LoadHighlightAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ApplicateHtmlDocument(
            ApplicateHtmlDocumentTemplate.Build(source.FileName, body, preferences, baseAssets, mermaidAssets, hljsAssets),
            context.PlainText.ToString(),
            context.Headings,
            context.Blocks);
    }

    public async Task<ApplicateRenderedBody> RenderBodyAsync(
        MarkdownSource source,
        ReadingPreferences preferences,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken)
    {
        var context = await RenderMarkdownToContextAsync(source, imageSourceResolver, cancellationToken).ConfigureAwait(false);
        var bodyHtml = context.Html.ToString();
        return new ApplicateRenderedBody(
            BodyHtml: bodyHtml,
            PlainText: context.PlainText.ToString(),
            Headings: context.Headings,
            Blocks: context.Blocks,
            TopLevelBlockEndOffsets: context.TopLevelBlockEndOffsets,
            HasMermaidBlock: context.HasMermaidBlock,
            HasCodeBlockWithSyntax: context.HasCodeBlockWithSyntax,
            RendererCacheKeySuffix: ApplicateRendererDocumentCacheKeys.CreateSuffix(bodyHtml));
    }

    private async Task<RenderContext> RenderMarkdownToContextAsync(
        MarkdownSource source,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken)
    {
        var baseDirectory = ResolveBaseDirectory(source.Path);
        var rendered = _markdownRenderer.Render(source.Content, baseDirectory);
        var context = new RenderContext(
            imageSourceResolver,
            baseDirectory,
            cancellationToken,
            source.Content.Split('\n'));

        foreach (var block in rendered.Blocks)
        {
            await RenderBlockAsync(context, block).ConfigureAwait(false);
            context.TopLevelBlockEndOffsets.Add(context.Html.Length);
        }

        return context;
    }

    private static string? ResolveBaseDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetDirectoryName(path);
    }

    private static async Task RenderBlockAsync(RenderContext context, MarkdownBlock block)
    {
        var blockIndex = context.Blocks.Count;
        var kind = GetBlockKind(block);
        context.Blocks.Add(new ApplicateHtmlBlockMarker(blockIndex, kind, GetBlockPlainText(block)));

        switch (block)
        {
            case MarkdownHeadingBlock heading:
                await RenderHeadingAsync(context, heading, blockIndex).ConfigureAwait(false);
                break;
            case MarkdownParagraphBlock paragraph:
                context.Html.Append("<p").Append(BlockDataAttributes(blockIndex, kind, paragraph.SourceSpan)).Append('>');
                await RenderInlinesAsync(context, paragraph.Inlines).ConfigureAwait(false);
                context.Html.AppendLine("</p>");
                break;
            case MarkdownQuoteBlock quote:
                context.Html.Append("<blockquote").Append(BlockDataAttributes(blockIndex, kind, quote.SourceSpan)).AppendLine(">");
                foreach (var child in quote.Blocks)
                {
                    await RenderBlockAsync(context, child).ConfigureAwait(false);
                }

                context.Html.AppendLine("</blockquote>");
                break;
            case MarkdownListBlock list:
                await RenderListAsync(context, list, blockIndex, kind).ConfigureAwait(false);
                break;
            case MarkdownHorizontalRuleBlock:
                context.Html.Append("<hr").Append(BlockDataAttributes(blockIndex, kind, block.SourceSpan)).AppendLine(">");
                break;
            case MarkdownCodeBlock code:
                RenderCodeBlock(context, code, blockIndex, kind);
                break;
            case MarkdownTableBlock table:
                await RenderTableAsync(context, table, blockIndex, kind).ConfigureAwait(false);
                break;
            case MarkdownImageBlock image:
                await RenderImageBlockAsync(context, image.Url, image.AltText, image.Title, image.Width, image.Height, blockIndex, kind, image.SourceSpan).ConfigureAwait(false);
                break;
            case ApplicateMathBlock math:
                RenderMathBlock(context, math, blockIndex, kind);
                break;
        }
    }

    private static string BlockDataAttributes(int blockIndex, string kind, MarkdownSourceSpan? sourceSpan)
    {
        var attributes = new StringBuilder()
            .Append(" data-mm-block-index=\"")
            .Append(blockIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append("\" data-mm-block-kind=\"")
            .Append(kind)
            .Append('"');
        if (sourceSpan is { } span)
        {
            attributes
                .Append(" data-mm-source-line=\"")
                .Append(span.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("\" data-mm-source-end-line=\"")
                .Append(span.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('"');
        }

        return attributes.ToString();
    }

    private static async Task RenderHeadingAsync(RenderContext context, MarkdownHeadingBlock heading, int blockIndex)
    {
        var level = System.Math.Clamp(heading.Level, 1, 6);
        var text = GetPlainText(heading.Inlines);
        var inlines = GetHeadingInlines(heading.Inlines);
        var anchor = MarkdownHeadingAnchorSlugger.CreateAnchor(heading.Inlines);
        context.Headings.Add(new ApplicateHtmlHeading(level, text, anchor, blockIndex, inlines));
        context.PlainText.AppendLine(text);

        context.Html.Append("<h").Append(level)
            .Append(BlockDataAttributes(blockIndex, "heading", heading.SourceSpan))
            .Append(" id=\"").Append(HtmlAttribute(anchor)).Append("\">");
        await RenderInlinesAsync(context, heading.Inlines).ConfigureAwait(false);
        context.Html.Append("</h").Append(level).AppendLine(">");
    }

    private static async Task RenderListAsync(RenderContext context, MarkdownListBlock list, int blockIndex, string kind)
    {
        var tag = list.IsOrdered ? "ol" : "ul";

        var hasTasks = false;
        foreach (var i in list.Items)
        {
            if (i.TaskChecked is not null)
            {
                hasTasks = true;
                break;
            }
        }

        context.Html.Append('<').Append(tag);
        if (hasTasks)
        {
            // Strips the bullet and reserves the checkbox gutter (see renderer.css).
            context.Html.Append(" class=\"mm-task-list\"");
        }
        context.Html.Append(BlockDataAttributes(blockIndex, kind, list.SourceSpan)).AppendLine(">");
        foreach (var item in list.Items)
        {
            if (item.TaskChecked is bool isChecked && item.TaskSourceLine is int taskLine)
            {
                // GFM task item: a real checkbox carrying its DOCUMENT-absolute
                // source line plus an identity key of that raw line, so a click
                // can toggle [ ]/[x] in the file (renderer.ts -> host IPC) and the
                // write-back can refuse when the view went stale (external edit
                // shifted lines). Key MUST come from the raw source line via the
                // shared TaskListIdentity routine — never from the rendered label.
                var taskKey = taskLine >= 0 && taskLine < context.SourceLines.Length
                    ? TaskListIdentity.ComputeKey(context.SourceLines[taskLine])
                    : null;
                context.Html.Append("<li class=\"mm-task-item\"><input type=\"checkbox\" class=\"mm-task-checkbox\" data-task-line=\"")
                    .Append(taskLine.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('"');
                if (taskKey is not null)
                {
                    // Omitted when the slice is out of range or the line is not a
                    // marker; the write-back then refuses (fail-closed).
                    context.Html.Append(" data-task-key=\"").Append(taskKey).Append('"');
                }
                if (isChecked)
                {
                    context.Html.Append(" checked");
                }
                context.Html.AppendLine(">");
            }
            else
            {
                context.Html.AppendLine("<li>");
            }

            foreach (var child in item.Blocks)
            {
                await RenderBlockAsync(context, child).ConfigureAwait(false);
            }

            context.Html.AppendLine("</li>");
        }

        context.Html.Append("</").Append(tag).AppendLine(">");
    }

    private static void RenderCodeBlock(RenderContext context, MarkdownCodeBlock code, int blockIndex, string kind)
    {
        context.PlainText.AppendLine(code.Code);

        var infoHead = (code.Info ?? string.Empty).Trim().Split(' ')[0].ToLowerInvariant();
        var blockAttrs = BlockDataAttributes(blockIndex, kind, code.SourceSpan);

        if (string.Equals(infoHead, "mermaid", StringComparison.Ordinal))
        {
            context.HasMermaidBlock = true;
            context.Html.Append("<pre class=\"mm-mermaid\"").Append(blockAttrs).Append("><code class=\"language-mermaid\" data-mm-mermaid>")
                        .Append(HtmlText(code.Code))
                        .AppendLine("</code></pre>");
            return;
        }

        context.HasCodeBlockWithSyntax = true;
        var langClass = string.IsNullOrEmpty(infoHead)
            ? "language-plaintext"
            : $"language-{HtmlAttribute(infoHead)}";
        context.Html.Append("<pre").Append(blockAttrs).Append("><code data-mm-code class=\"").Append(langClass).Append("\">")
                    .Append(HtmlText(code.Code))
                    .AppendLine("</code></pre>");
    }

    private static async Task RenderTableAsync(RenderContext context, MarkdownTableBlock table, int blockIndex, string kind)
    {
        context.Html.Append("<div class=\"mm-table-scroll\"").Append(BlockDataAttributes(blockIndex, kind, table.SourceSpan)).AppendLine(">");
        context.Html.AppendLine("<table>");
        if (table.Header.Count > 0)
        {
            context.Html.AppendLine("<thead><tr>");
            foreach (var cell in table.Header)
            {
                context.Html.Append("<th>");
                await RenderInlinesAsync(context, cell.Inlines).ConfigureAwait(false);
                context.Html.AppendLine("</th>");
            }

            context.Html.AppendLine("</tr></thead>");
        }

        context.Html.AppendLine("<tbody>");
        foreach (var row in table.Rows)
        {
            context.Html.AppendLine("<tr>");
            foreach (var cell in row)
            {
                context.Html.Append("<td>");
                await RenderInlinesAsync(context, cell.Inlines).ConfigureAwait(false);
                context.Html.AppendLine("</td>");
            }

            context.Html.AppendLine("</tr>");
        }

        context.Html.AppendLine("</tbody></table>");
        context.Html.AppendLine("</div>");
    }

    private static async Task RenderImageBlockAsync(
        RenderContext context,
        string url,
        string? altText,
        string? title,
        double? width,
        double? height,
        int blockIndex,
        string kind,
        MarkdownSourceSpan? sourceSpan)
    {
        var src = await TryResolveImageDataUriAsync(context, url).ConfigureAwait(false);
        if (src is null)
        {
            RenderImagePlaceholder(context, altText, blockIndex, kind, sourceSpan);
            return;
        }

        context.Html.Append("<figure").Append(BlockDataAttributes(blockIndex, kind, sourceSpan)).Append("><img src=\"").Append(HtmlAttribute(src)).Append('"');
        AppendImageAttributes(context.Html, altText, title, width, height);
        context.Html.Append('>');
        if (!string.IsNullOrWhiteSpace(altText))
        {
            context.Html.Append("<figcaption>").Append(HtmlText(altText)).Append("</figcaption>");
        }

        context.Html.AppendLine("</figure>");
    }

    private static void RenderMathBlock(RenderContext context, ApplicateMathBlock math, int blockIndex, string kind)
    {
        context.PlainText.AppendLine(math.Tex);
        context.Html.Append("<div class=\"math-display\"")
            .Append(BlockDataAttributes(blockIndex, kind, math.SourceSpan))
            .Append(" data-tex=\"")
            .Append(HtmlAttribute(math.Tex))
            .AppendLine("\"></div>");
    }

    private static async Task RenderInlinesAsync(RenderContext context, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            await RenderInlineAsync(context, inline).ConfigureAwait(false);
        }
    }

    private static async Task RenderInlineAsync(RenderContext context, MarkdownInline inline)
    {
        switch (inline)
        {
            case MarkdownTextInline text:
                context.PlainText.Append(text.Text);
                context.Html.Append(HtmlText(text.Text));
                break;
            case MarkdownStrongInline strong:
                context.Html.Append("<strong>");
                await RenderInlinesAsync(context, strong.Inlines).ConfigureAwait(false);
                context.Html.Append("</strong>");
                break;
            case MarkdownEmphasisInline emphasis:
                context.Html.Append("<em>");
                await RenderInlinesAsync(context, emphasis.Inlines).ConfigureAwait(false);
                context.Html.Append("</em>");
                break;
            case MarkdownCodeInline code:
                context.PlainText.Append(code.Code);
                context.Html.Append("<code>").Append(HtmlText(code.Code)).Append("</code>");
                break;
            case MarkdownImageInline image:
                await RenderImageInlineAsync(context, image).ConfigureAwait(false);
                break;
            case MarkdownLinkInline link:
                await RenderLinkAsync(context, link).ConfigureAwait(false);
                break;
            case MarkdownLineBreakInline:
                context.PlainText.AppendLine();
                context.Html.Append("<br>");
                break;
            case ApplicateMathInline math:
                context.PlainText.Append(math.Tex);
                context.Html.Append("<span class=\"math-inline\" data-tex=\"")
                    .Append(HtmlAttribute(math.Tex))
                    .Append("\"></span>");
                break;
        }
    }

    private static async Task RenderImageInlineAsync(RenderContext context, MarkdownImageInline image)
    {
        var src = await TryResolveImageDataUriAsync(context, image.Url).ConfigureAwait(false);
        if (src is null)
        {
            context.Html.Append("<span class=\"image-placeholder\">")
                .Append(HtmlText(image.AltText ?? "image"))
                .Append("</span>");
            return;
        }

        context.Html.Append("<img src=\"").Append(HtmlAttribute(src)).Append('"');
        AppendImageAttributes(context.Html, image.AltText, image.Title, width: null, height: null);
        context.Html.Append('>');
    }

    private static async Task RenderLinkAsync(RenderContext context, MarkdownLinkInline link)
    {
        var safeHref = CreateDisplayHref(context, link.Url);
        context.Html.Append("<a href=\"").Append(HtmlAttribute(safeHref)).Append('"');
        if (!string.Equals(safeHref, link.Url, StringComparison.Ordinal))
        {
            context.Html.Append(" data-mm-href=\"").Append(HtmlAttribute(link.Url)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(link.Title))
        {
            context.Html.Append(" title=\"").Append(HtmlAttribute(link.Title)).Append('"');
        }

        context.Html.Append('>');
        await RenderInlinesAsync(context, link.Inlines).ConfigureAwait(false);
        context.Html.Append("</a>");
    }

    private static async Task<string?> TryResolveImageDataUriAsync(RenderContext context, string url)
    {
        if (context.ImageSourceResolver is null)
        {
            return null;
        }

        await using var stream = await context.ImageSourceResolver
            .TryOpenAsync(url, context.BaseDirectory, context.CancellationToken)
            .ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, context.CancellationToken).ConfigureAwait(false);
        if (memory.Length == 0 || memory.Length > MaxInlineImageBytes)
        {
            return null;
        }

        var mime = GetImageMimeType(url);
        return $"data:{mime};base64,{Convert.ToBase64String(memory.ToArray())}";
    }

    private static void AppendImageAttributes(
        StringBuilder html,
        string? altText,
        string? title,
        double? width,
        double? height)
    {
        html.Append(" alt=\"").Append(HtmlAttribute(altText ?? string.Empty)).Append('"');
        if (!string.IsNullOrWhiteSpace(title))
        {
            html.Append(" title=\"").Append(HtmlAttribute(title)).Append('"');
        }

        if (width is > 0)
        {
            html.Append(" width=\"").Append(width.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
        }

        if (height is > 0)
        {
            html.Append(" height=\"").Append(height.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
        }
    }

    private static void RenderImagePlaceholder(RenderContext context, string? altText, int blockIndex, string kind, MarkdownSourceSpan? sourceSpan)
        => context.Html.Append("<figure class=\"image-placeholder\"")
            .Append(BlockDataAttributes(blockIndex, kind, sourceSpan))
            .Append("><figcaption>")
            .Append(HtmlText(altText ?? "image"))
            .AppendLine("</figcaption></figure>");

    private static string CreateDisplayHref(RenderContext context, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith('#'))
        {
            return trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            // Standard remote schemes go to the browser launcher.
            // `file:` is allowed so context-menu opens of local markdown
            // links route through the same host resolver as ordinary clicks.
            return absolute.Scheme is "http" or "https" or "mailto" or "file"
                ? trimmed
                : string.Empty;
        }

        var decoration = SplitLocalLinkDecoration(trimmed, out var pathPart);
        if (string.IsNullOrWhiteSpace(pathPart) || string.IsNullOrWhiteSpace(context.BaseDirectory))
        {
            return trimmed;
        }

        try
        {
            var resolved = Path.GetFullPath(Path.Combine(context.BaseDirectory, pathPart));
            return new Uri(resolved).AbsoluteUri + decoration;
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (UriFormatException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }
    }

    private static string SplitLocalLinkDecoration(string target, out string pathPart)
    {
        var fragmentIndex = target.IndexOf('#', StringComparison.Ordinal);
        var queryIndex = target.IndexOf('?', StringComparison.Ordinal);
        var cutIndex = fragmentIndex >= 0 && queryIndex >= 0
            ? System.Math.Min(fragmentIndex, queryIndex)
            : System.Math.Max(fragmentIndex, queryIndex);

        if (cutIndex < 0)
        {
            pathPart = target;
            return string.Empty;
        }

        pathPart = target[..cutIndex];
        return target[cutIndex..];
    }

    private static string GetImageMimeType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png"
        };
    }

    private static string GetBlockKind(MarkdownBlock block)
        => block switch
        {
            MarkdownHeadingBlock => "heading",
            MarkdownParagraphBlock => "paragraph",
            MarkdownQuoteBlock => "quote",
            MarkdownListBlock => "list",
            MarkdownHorizontalRuleBlock => "rule",
            MarkdownCodeBlock => "code",
            MarkdownTableBlock => "table",
            MarkdownImageBlock => "image",
            ApplicateMathBlock => "math",
            _ => "unknown"
        };

    private static string GetBlockPlainText(MarkdownBlock block)
        => block switch
        {
            MarkdownHeadingBlock heading => GetPlainText(heading.Inlines),
            MarkdownParagraphBlock paragraph => GetPlainText(paragraph.Inlines),
            MarkdownQuoteBlock quote => string.Join(Environment.NewLine, quote.Blocks.Select(GetBlockPlainText)),
            MarkdownListBlock list => string.Join(Environment.NewLine, list.Items.SelectMany(item => item.Blocks).Select(GetBlockPlainText)),
            MarkdownCodeBlock code => code.Code,
            MarkdownTableBlock table => string.Join(" ", table.Header.Select(cell => GetPlainText(cell.Inlines))),
            MarkdownImageBlock image => image.AltText ?? image.Title ?? "image",
            ApplicateMathBlock math => math.Tex,
            _ => string.Empty
        };

    private static string GetPlainText(IReadOnlyList<MarkdownInline> inlines)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines)
        {
            AppendPlainText(builder, inline);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ApplicateHtmlHeadingInline> GetHeadingInlines(IReadOnlyList<MarkdownInline> inlines)
    {
        var segments = new List<ApplicateHtmlHeadingInline>();
        AppendHeadingInlines(segments, inlines);
        return segments;
    }

    private static void AppendHeadingInlines(List<ApplicateHtmlHeadingInline> segments, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            AppendHeadingInline(segments, inline);
        }
    }

    private static void AppendHeadingInline(List<ApplicateHtmlHeadingInline> segments, MarkdownInline inline)
    {
        switch (inline)
        {
            case MarkdownTextInline text:
                AddHeadingSegment(segments, ApplicateHtmlHeadingInlineKind.Text, text.Text);
                break;
            case MarkdownStrongInline strong:
                AppendHeadingInlines(segments, strong.Inlines);
                break;
            case MarkdownEmphasisInline emphasis:
                AppendHeadingInlines(segments, emphasis.Inlines);
                break;
            case MarkdownCodeInline code:
                AddHeadingSegment(segments, ApplicateHtmlHeadingInlineKind.Text, code.Code);
                break;
            case MarkdownImageInline image:
                AddHeadingSegment(segments, ApplicateHtmlHeadingInlineKind.Text, image.AltText ?? image.Title ?? "image");
                break;
            case MarkdownLinkInline link:
                AppendHeadingInlines(segments, link.Inlines);
                break;
            case MarkdownLineBreakInline:
                AddHeadingSegment(segments, ApplicateHtmlHeadingInlineKind.Text, " ");
                break;
            case ApplicateMathInline math:
                AddHeadingSegment(segments, ApplicateHtmlHeadingInlineKind.Math, math.Tex);
                break;
        }
    }

    private static void AddHeadingSegment(
        List<ApplicateHtmlHeadingInline> segments,
        ApplicateHtmlHeadingInlineKind kind,
        string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            return;
        }

        segments.Add(new ApplicateHtmlHeadingInline(kind, text));
    }

    private static void AppendPlainText(StringBuilder builder, MarkdownInline inline)
    {
        switch (inline)
        {
            case MarkdownTextInline text:
                builder.Append(text.Text);
                break;
            case MarkdownStrongInline strong:
                AppendPlainText(builder, strong.Inlines);
                break;
            case MarkdownEmphasisInline emphasis:
                AppendPlainText(builder, emphasis.Inlines);
                break;
            case MarkdownCodeInline code:
                builder.Append(code.Code);
                break;
            case MarkdownImageInline image:
                builder.Append(image.AltText ?? image.Title ?? "image");
                break;
            case MarkdownLinkInline link:
                AppendPlainText(builder, link.Inlines);
                break;
            case MarkdownLineBreakInline:
                builder.Append(' ');
                break;
            case ApplicateMathInline math:
                builder.Append(math.Tex);
                break;
        }
    }

    private static void AppendPlainText(StringBuilder builder, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines)
        {
            AppendPlainText(builder, inline);
        }
    }

    private static string HtmlText(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string HtmlAttribute(string? value)
        => HtmlEncoder.Default.Encode(value ?? string.Empty);

    private sealed class RenderContext(
        IImageSourceResolver? imageSourceResolver,
        string? baseDirectory,
        CancellationToken cancellationToken,
        string[]? sourceLines = null)
    {
        public StringBuilder Html { get; } = new();

        /// <summary>
        /// RAW document source split on '\n' (lines keep any trailing '\r').
        /// Used to compute each task item's identity key from the ORIGINAL
        /// source line via <see cref="TaskListIdentity.ComputeKey"/> — the same
        /// routine the write-back verify side uses, so the two cannot diverge.
        /// </summary>
        public string[] SourceLines { get; } = sourceLines ?? [];

        public StringBuilder PlainText { get; } = new();

        public List<ApplicateHtmlHeading> Headings { get; } = [];

        public List<ApplicateHtmlBlockMarker> Blocks { get; } = [];

        public List<int> TopLevelBlockEndOffsets { get; } = [];

        public IImageSourceResolver? ImageSourceResolver { get; } = imageSourceResolver;

        public string? BaseDirectory { get; } = baseDirectory;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public bool HasMermaidBlock { get; set; }

        public bool HasCodeBlockWithSyntax { get; set; }
    }
}
