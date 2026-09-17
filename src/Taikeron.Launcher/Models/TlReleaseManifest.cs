using System.Text.Json.Serialization;

namespace Taikeron.Launcher.Models;

public sealed class TlReleaseManifest
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("product")]
    public string Product { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("platforms")]
    public Dictionary<string, TlReleasePlatform> Platforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public TlReleasePlatform? WindowsX64 =>
        Platforms.TryGetValue("windows-x64", out var platform) ? platform : null;

    // Compatibility helpers used by the launcher UI/services.
    [JsonIgnore]
    public string File => WindowsX64?.FileName ?? "";

    [JsonIgnore]
    public string Url => WindowsX64?.Url ?? "";

    [JsonIgnore]
    public string Sha256 => WindowsX64?.Sha256 ?? "";

    [JsonIgnore]
    public long Bytes => WindowsX64?.Bytes ?? 0;
}

public sealed class TlReleasePlatform
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "nsis-installer";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}
