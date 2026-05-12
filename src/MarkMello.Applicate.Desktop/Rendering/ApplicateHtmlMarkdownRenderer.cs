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
        var baseDirectory = ResolveBaseDirectory(source.Path);
        var rendered = _markdownRenderer.Render(source.Content, baseDirectory);
        var context = new RenderContext(imageSourceResolver, baseDirectory, cancellationToken);
        var assets = _assetEmbedder is null
            ? ApplicateWebAssetBundle.Empty
            : await _assetEmbedder.LoadBundleAsync(cancellationToken).ConfigureAwait(false);

        foreach (var block in rendered.Blocks)
        {
            await RenderBlockAsync(context, block).ConfigureAwait(false);
        }

        var body = context.Html.ToString();
        return new ApplicateHtmlDocument(
            ApplicateHtmlDocumentTemplate.Build(source.FileName, body, preferences, assets),
            context.PlainText.ToString(),
            context.Headings,
            context.Blocks);
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
        context.Blocks.Add(new ApplicateHtmlBlockMarker(blockIndex, GetBlockKind(block), GetBlockPlainText(block)));

        switch (block)
        {
            case MarkdownHeadingBlock heading:
                await RenderHeadingAsync(context, heading, blockIndex).ConfigureAwait(false);
                break;
            case MarkdownParagraphBlock paragraph:
                context.Html.Append("<p>");
                await RenderInlinesAsync(context, paragraph.Inlines).ConfigureAwait(false);
                context.Html.AppendLine("</p>");
                break;
            case MarkdownQuoteBlock quote:
                context.Html.AppendLine("<blockquote>");
                foreach (var child in quote.Blocks)
                {
                    await RenderBlockAsync(context, child).ConfigureAwait(false);
                }

                context.Html.AppendLine("</blockquote>");
                break;
            case MarkdownListBlock list:
                await RenderListAsync(context, list).ConfigureAwait(false);
                break;
            case MarkdownHorizontalRuleBlock:
                context.Html.AppendLine("<hr>");
                break;
            case MarkdownCodeBlock code:
                RenderCodeBlock(context, code);
                break;
            case MarkdownTableBlock table:
                await RenderTableAsync(context, table).ConfigureAwait(false);
                break;
            case MarkdownImageBlock image:
                await RenderImageBlockAsync(context, image.Url, image.AltText, image.Title, image.Width, image.Height).ConfigureAwait(false);
                break;
            case ApplicateMathBlock math:
                RenderMathBlock(context, math);
                break;
        }
    }

    private static async Task RenderHeadingAsync(RenderContext context, MarkdownHeadingBlock heading, int blockIndex)
    {
        var level = System.Math.Clamp(heading.Level, 1, 6);
        var text = GetPlainText(heading.Inlines);
        var anchor = MarkdownHeadingAnchorSlugger.CreateAnchor(heading.Inlines);
        context.Headings.Add(new ApplicateHtmlHeading(level, text, anchor, blockIndex));
        context.PlainText.AppendLine(text);

        context.Html.Append("<h").Append(level).Append(" id=\"").Append(HtmlAttribute(anchor)).Append("\">");
        await RenderInlinesAsync(context, heading.Inlines).ConfigureAwait(false);
        context.Html.Append("</h").Append(level).AppendLine(">");
    }

    private static async Task RenderListAsync(RenderContext context, MarkdownListBlock list)
    {
        context.Html.AppendLine(list.IsOrdered ? "<ol>" : "<ul>");
        foreach (var item in list.Items)
        {
            context.Html.AppendLine("<li>");
            foreach (var child in item.Blocks)
            {
                await RenderBlockAsync(context, child).ConfigureAwait(false);
            }

            context.Html.AppendLine("</li>");
        }

        context.Html.AppendLine(list.IsOrdered ? "</ol>" : "</ul>");
    }

    private static void RenderCodeBlock(RenderContext context, MarkdownCodeBlock code)
    {
        var language = string.IsNullOrWhiteSpace(code.Info) ? string.Empty : $" class=\"language-{HtmlAttribute(code.Info)}\"";
        context.PlainText.AppendLine(code.Code);
        context.Html.Append("<pre><code").Append(language).Append('>')
            .Append(HtmlText(code.Code))
            .AppendLine("</code></pre>");
    }

    private static async Task RenderTableAsync(RenderContext context, MarkdownTableBlock table)
    {
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
    }

    private static async Task RenderImageBlockAsync(
        RenderContext context,
        string url,
        string? altText,
        string? title,
        double? width,
        double? height)
    {
        var src = await TryResolveImageDataUriAsync(context, url).ConfigureAwait(false);
        if (src is null)
        {
            RenderImagePlaceholder(context, altText);
            return;
        }

        context.Html.Append("<figure><img src=\"").Append(HtmlAttribute(src)).Append('"');
        AppendImageAttributes(context.Html, altText, title, width, height);
        context.Html.Append('>');
        if (!string.IsNullOrWhiteSpace(altText))
        {
            context.Html.Append("<figcaption>").Append(HtmlText(altText)).Append("</figcaption>");
        }

        context.Html.AppendLine("</figure>");
    }

    private static void RenderMathBlock(RenderContext context, ApplicateMathBlock math)
    {
        context.PlainText.AppendLine(math.Tex);
        context.Html.Append("<div class=\"math-display\" data-tex=\"")
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
        var safeHref = IsSafeLinkTarget(link.Url) ? link.Url : string.Empty;
        context.Html.Append("<a href=\"").Append(HtmlAttribute(safeHref)).Append('"');
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

    private static void RenderImagePlaceholder(RenderContext context, string? altText)
        => context.Html.Append("<figure class=\"image-placeholder\"><figcaption>")
            .Append(HtmlText(altText ?? "image"))
            .AppendLine("</figcaption></figure>");

    private static bool IsSafeLinkTarget(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith('#'))
        {
            return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto";
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
        CancellationToken cancellationToken)
    {
        public StringBuilder Html { get; } = new();

        public StringBuilder PlainText { get; } = new();

        public List<ApplicateHtmlHeading> Headings { get; } = [];

        public List<ApplicateHtmlBlockMarker> Blocks { get; } = [];

        public IImageSourceResolver? ImageSourceResolver { get; } = imageSourceResolver;

        public string? BaseDirectory { get; } = baseDirectory;

        public CancellationToken CancellationToken { get; } = cancellationToken;
    }
}
