namespace MarkMello.Applicate.Desktop.Views;

/// <summary>
/// Table-cell edit received from the WebView renderer.
/// </summary>
public sealed class ApplicateWebTableCellEditEventArgs(
    int line,
    int cellIndex,
    string text,
    string? key,
    bool raw = false) : EventArgs
{
    public int Line { get; } = line;

    public int CellIndex { get; } = cellIndex;

    public string Text { get; } = text;

    public string? Key { get; } = key;

    /// <summary>
    /// The renderer edited the cell's RAW markdown (a rich cell whose rendered DOM
    /// is not its source), so <see cref="Text"/> is markdown, not literal text.
    /// Absent or <c>false</c> keeps the literal plain-text contract unchanged.
    /// </summary>
    public bool Raw { get; } = raw;
}
