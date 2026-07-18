namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Literal plain-text table-cell edit received from the WebView renderer.
/// </summary>
public sealed class ApplicateWebTableCellEditEventArgs(
    int line,
    int cellIndex,
    string text,
    string? key) : EventArgs
{
    public int Line { get; } = line;

    public int CellIndex { get; } = cellIndex;

    public string Text { get; } = text;

    public string? Key { get; } = key;
}
