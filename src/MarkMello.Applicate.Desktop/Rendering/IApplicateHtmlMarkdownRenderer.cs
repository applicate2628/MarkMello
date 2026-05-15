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
}
