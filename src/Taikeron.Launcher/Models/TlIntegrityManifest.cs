using System.Text.Json.Serialization;

namespace Taikeron.Launcher.Models;

public sealed class TlIntegrityManifest
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("product")]
    public string Product { get; set; } = "";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "windows-x64";

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("packageSha256")]
    public string PackageSha256 { get; set; } = "";

    [JsonPropertyName("files")]
    public List<TlIntegrityFile> Files { get; set; } = [];
}

public sealed class TlIntegrityFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

public enum TlIntegrityState
{
    NotInstalled,
    ReferenceUnavailable,
    VersionMismatch,
    Healthy,
    Corrupted
}

public sealed record TlIntegrityCheckResult(
    TlIntegrityState State,
    string Message,
    int CheckedFiles,
    int MissingFiles,
    int MismatchedFiles,
    IReadOnlyList<string> Problems)
{
    public bool IsHealthy => State == TlIntegrityState.Healthy;
    public bool BlocksLaunch => State == TlIntegrityState.Corrupted;
}
