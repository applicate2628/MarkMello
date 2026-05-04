namespace MarkMello.Presentation.Views.Markdown.Minimap;

internal sealed class DocumentMinimapScrollRequestedEventArgs(double offsetY) : EventArgs
{
    public double OffsetY { get; } = offsetY;
}
