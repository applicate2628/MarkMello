using MarkMello.Domain;

namespace MarkMello.Presentation.ViewModels;

internal static class ReadingLayoutMetrics
{
    public const double DocumentHorizontalPadding = 144;

    public static double GetDocumentColumnMaxWidth(ReadingPreferences preferences)
        => ReadingPreferences.Normalize(preferences).ContentWidth + DocumentHorizontalPadding;
}
