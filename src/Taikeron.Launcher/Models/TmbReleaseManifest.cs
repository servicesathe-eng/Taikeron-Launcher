using System.Text.Json.Serialization;

namespace Taikeron.Launcher.Models;

public sealed class TmbReleaseManifest
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
    public Dictionary<string, TmbReleasePlatform> Platforms { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public TmbReleasePlatform? WindowsX64Portable =>
        Platforms.TryGetValue("windows-x64-portable", out var platform) ? platform : null;

    [JsonIgnore]
    public TmbReleasePlatform? WindowsX64Nsis =>
        Platforms.TryGetValue("windows-x64-nsis", out var platform) ? platform : null;

    [JsonIgnore]
    public string File => WindowsX64Portable?.FileName ?? "";

    [JsonIgnore]
    public string Url => WindowsX64Portable?.Url ?? "";

    [JsonIgnore]
    public string Sha256 => WindowsX64Portable?.Sha256 ?? "";

    [JsonIgnore]
    public long Bytes => WindowsX64Portable?.Bytes ?? 0;
}

public sealed class TmbReleasePlatform
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}

public enum TmbIntegrityState
{
    NotInstalled,
    Healthy,
    ReferenceUnavailable,
    LegacyUnmanaged,
    Corrupted
}

public sealed record TmbIntegrityCheckResult(
    TmbIntegrityState State,
    string Message,
    long CheckedBytes,
    string? ExpectedSha256,
    string? ActualSha256)
{
    public bool BlocksLaunch => State == TmbIntegrityState.Corrupted;
}
