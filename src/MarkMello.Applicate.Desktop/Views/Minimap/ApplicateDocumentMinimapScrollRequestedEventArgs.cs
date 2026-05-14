namespace MarkMello.Applicate.Desktop.Views.Minimap;

internal sealed class ApplicateDocumentMinimapScrollRequestedEventArgs(double offsetY) : EventArgs
{
    public double OffsetY { get; } = offsetY;
}
