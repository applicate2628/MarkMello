using System.Text.Json.Serialization;

namespace MarkMello.Infrastructure.Updates;

internal sealed class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public GitHubReleaseAssetResponse[] Assets { get; init; } = [];
}
