namespace MarkMello.Application.Updates;

/// <summary>
/// Progress of an in-flight update download. <see cref="TotalBytes"/> is null
/// when the server did not send a Content-Length header, in which case the UI
/// keeps an indeterminate bar instead of showing a percentage.
/// </summary>
public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>
    /// Completion as 0-100, or null when the total size is unknown.
    /// </summary>
    public double? Percent =>
        TotalBytes is > 0
            ? System.Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0, 100)
            : null;
}
