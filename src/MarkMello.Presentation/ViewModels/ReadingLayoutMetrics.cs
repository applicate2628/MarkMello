using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

/// <summary>
/// Single source of truth for the document column layout metrics shared by
/// both the viewer (read-only) and the edit-preview surfaces. Promoted to
/// public so Applicate's view layer reads the same canonical horizontal
/// padding instead of re-stating the literal 144 in consumer constructors.
/// </summary>
public static class ReadingLayoutMetrics
{
    /// <summary>
    /// Total horizontal padding (left + right combined) reserved by the
    /// document column around the normalized reading content. The viewer and
    /// edit-preview surfaces both consume this value so a future tuning
    /// lands in one place.
    /// </summary>
    public const double DocumentHorizontalPadding = 144;

    /// <summary>
    /// Returns the column width the rendered document column must reach to
    /// fit the normalized content width plus
    /// <see cref="DocumentHorizontalPadding"/> on both sides.
    /// </summary>
    public static double GetDocumentColumnMaxWidth(ReadingPreferences preferences)
        => ReadingPreferences.Normalize(preferences).ContentWidth + DocumentHorizontalPadding;

    /// <summary>
    /// Canonical horizontal padding the rendered document column reserves
    /// around the active <see cref="ReadingPreferences.ContentWidth"/>.
    /// Equivalent to
    /// <c>GetDocumentColumnMaxWidth(preferences) - preferences.ContentWidth</c>:
    /// when ContentWidth is one of the named presets (Narrow/Medium/Wide)
    /// this collapses to <see cref="DocumentHorizontalPadding"/>; when an
    /// unnormalized custom width is in flight (e.g. width-resizer mid-drag)
    /// the difference compensates so the column extent stays consistent
    /// with the normalized layout target.
    /// </summary>
    public static double GetDocumentHorizontalPadding(ReadingPreferences preferences)
        => System.Math.Max(0, GetDocumentColumnMaxWidth(preferences) - preferences.ContentWidth);
}
