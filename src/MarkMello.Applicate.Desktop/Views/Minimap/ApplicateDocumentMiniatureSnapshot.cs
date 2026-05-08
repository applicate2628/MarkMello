namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal sealed class ApplicateDocumentMiniatureSnapshot(double totalWidth, double totalHeight)
{
    public static ApplicateDocumentMiniatureSnapshot Empty { get; } = new(0, 0);

    public double TotalWidth { get; } = totalWidth;

    public double TotalHeight { get; } = totalHeight;

    public bool IsEmpty => TotalWidth <= 0 || TotalHeight <= 0;
}
