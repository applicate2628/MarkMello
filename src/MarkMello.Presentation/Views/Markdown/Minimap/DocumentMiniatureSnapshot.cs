namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal sealed class DocumentMiniatureSnapshot
{
    public static DocumentMiniatureSnapshot Empty { get; } = new(0, 0);

    public DocumentMiniatureSnapshot(double totalWidth, double totalHeight)
    {
        TotalWidth = Math.Max(0, totalWidth);
        TotalHeight = Math.Max(0, totalHeight);
    }

    public double TotalWidth { get; }

    public double TotalHeight { get; }

    public bool IsEmpty => TotalWidth <= 0 || TotalHeight <= 0;
}
