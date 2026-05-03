using System.Text.Json.Serialization;

namespace MarkMello.Infrastructure.Updates;

internal sealed class GitHubReleaseAssetResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }
}
