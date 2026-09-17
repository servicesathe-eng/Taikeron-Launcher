using System.Text.Json.Serialization;

namespace Taikeron.Launcher.Models;

public sealed class TlReleaseManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }
}
