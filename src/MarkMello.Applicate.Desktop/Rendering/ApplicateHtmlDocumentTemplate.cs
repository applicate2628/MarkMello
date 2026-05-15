using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

public static class ApplicateHtmlDocumentTemplate
{
    public static string Build(
        string title,
        string body,
        ReadingPreferences preferences,
        ApplicateWebBaseAssets baseAssets,
        ApplicateWebMermaidAssets? mermaidAssets,
        ApplicateWebHighlightAssets? hljsAssets)
    {
        var head = BuildHeadComponents(title, baseAssets, mermaidAssets, hljsAssets);
        var nonce = head.Nonce;
        var encodedTitle = head.EncodedTitle;
        var style = head.Style;
        var script = head.Script;

        // Style CSP relaxed (no nonce, 'unsafe-inline' only) — Mermaid SVG output
        // contains inline <style> tags and style= attributes без nonce; with CSP3
        // nonce-overrides-unsafe-inline rule those would be blocked. Script CSP
        // (nonce-bound) remains the real JS execution boundary. See ADR in design
        // doc 2026-05-13-mermaid-syntax-highlighting-design.md.
        // КОНТЕНТНЫЙ комментарий: backwards compatibility — старый Build с одним
        // ApplicateWebAssetBundle парам — removed; callers must use new triple-asset signature.
        return $$"""
            <!doctype html>
            <html data-mm-chrome="off">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; connect-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'; script-src 'nonce-{{nonce}}';">
              <title>{{encodedTitle}}</title>
              <style>{{style}}</style>
              <script nonce="{{nonce}}">{{script}}</script>
            </head>
            <body>
              <main class="mm-document" data-font-size="{{preferences.FontSize}}" data-line-height="{{preferences.LineHeight}}">
            {{body}}
              </main>
            </body>
            </html>
            """;
    }

    private static string CreateNonce()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    internal record HeadComponents(string EncodedTitle, string Style, string Script, string Nonce);

    internal static HeadComponents BuildHeadComponents(
        string title,
        ApplicateWebBaseAssets baseAssets,
        ApplicateWebMermaidAssets? mermaidAssets,
        ApplicateWebHighlightAssets? hljsAssets)
    {
        ArgumentNullException.ThrowIfNull(baseAssets);

        var nonce = CreateNonce();
        var encodedTitle = HtmlEncoder.Default.Encode(title);

        var style = new StringBuilder();
        style.Append(baseAssets.RendererCss).Append('\n').Append(baseAssets.KatexCss);
        if (hljsAssets is not null)
        {
            // CSS Nesting wraps full theme stylesheet under [data-theme="X"] parent
            // selector. WebView2 (Edge Chromium 120+) supports CSS Nesting natively.
            // Cleaner than per-selector regex prefixing and handles all hljs CSS shapes uniformly.
            style.Append("\n[data-theme=\"light\"] { ").Append(hljsAssets.LightCss).Append(" }");
            style.Append("\n[data-theme=\"dark\"] { ").Append(hljsAssets.DarkCss).Append(" }");
        }

        var script = new StringBuilder();
        script.Append(baseAssets.KatexScript);
        if (mermaidAssets is not null)
        {
            script.Append('\n').Append(mermaidAssets.Script);
        }
        if (hljsAssets is not null)
        {
            script.Append('\n').Append(hljsAssets.Script);
        }
        script.Append('\n').Append(baseAssets.RendererScript);

        return new HeadComponents(encodedTitle, style.ToString(), script.ToString(), nonce);
    }
}
