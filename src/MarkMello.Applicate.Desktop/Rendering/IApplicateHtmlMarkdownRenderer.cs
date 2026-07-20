using MarkMello.Application.Abstractions;
using MarkMello.Domain;

namespace MarkMello.Applicate.Desktop.Rendering;

public interface IApplicateHtmlMarkdownRenderer
{
    Task<ApplicateHtmlDocument> RenderAsync(
        MarkdownSource source,
        ReadingPreferences preferences,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken);

    Task<ApplicateRenderedBody> RenderBodyAsync(
        MarkdownSource source,
        ReadingPreferences preferences,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken);

    /// <summary>
    /// Renders one table cell's markdown to its rendered inner HTML, so a committed
    /// RAW cell edit can settle back to rendered content without a cold document
    /// re-render. The renderer process has no markdown parser of its own.
    /// </summary>
    Task<string> RenderTableCellHtmlAsync(
        string rawCellMarkdown,
        IImageSourceResolver? imageSourceResolver,
        CancellationToken cancellationToken);
}
