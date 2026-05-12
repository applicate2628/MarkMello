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
        ApplicateWebAssetBundle assets)
    {
        var nonce = CreateNonce();
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var style = assets.RendererCss + Environment.NewLine + assets.KatexCss;
        var script = assets.KatexScript + Environment.NewLine + assets.RendererScript;

        // KaTeX may inject style attributes while laying out math; CSP keeps all
        // scripts nonce-bound and permits inline styles only for rendered markup.
        return $$"""
            <!doctype html>
            <html data-mm-chrome="off">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; connect-src 'none'; img-src data:; font-src data:; style-src 'nonce-{{nonce}}' 'unsafe-inline'; script-src 'nonce-{{nonce}}';">
              <title>{{encodedTitle}}</title>
              <style nonce="{{nonce}}">{{style}}</style>
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
}
