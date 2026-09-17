using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class TaikeronLabService
{
    public const string StableManifestUrl = "https://taikeron-cyclingos.github.io/releases/tl/stable.json";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public string CanonicalInstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taikeron",
        "Apps",
        "Lab");

    public string CanonicalExecutable => Path.Combine(CanonicalInstallDirectory, "Taikeron Lab.exe");

    public string? FindInstalledExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidates =
        [
            CanonicalExecutable,
            Path.Combine(localAppData, "Programs", "Taikeron Lab", "Taikeron Lab.exe"),
            Path.Combine(localAppData, "Taikeron Lab", "Taikeron Lab.exe"),
            Path.Combine(programFiles, "Taikeron Lab", "Taikeron Lab.exe"),
            Path.Combine(programFilesX86, "Taikeron Lab", "Taikeron Lab.exe")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public string? GetInstalledVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        var info = FileVersionInfo.GetVersionInfo(executablePath);
        return NormalizeVersion(info.ProductVersion) ?? NormalizeVersion(info.FileVersion);
    }

    public async Task<TlReleaseManifest?> GetStableReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(StableManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonSerializer.DeserializeAsync<TlReleaseManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken);
    }

    public bool IsUpdateAvailable(string? installedVersion, string? remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(remoteVersion))
            return false;

        if (string.IsNullOrWhiteSpace(installedVersion))
            return true;

        return Version.TryParse(installedVersion, out var local)
            && Version.TryParse(remoteVersion, out var remote)
            && remote > local;
    }

    public void Launch(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Taikeron Lab est introuvable.", executablePath);

        Process.Start(new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        });
    }

    public async Task<string> DownloadAndVerifyAsync(
        TlReleaseManifest release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.Url))
            throw new InvalidOperationException("Le manifeste TL ne contient pas d’URL de téléchargement.");

        var downloadsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Taikeron",
            "Launcher",
            "Downloads");

        Directory.CreateDirectory(downloadsDirectory);

        var fileName = string.IsNullOrWhiteSpace(release.File)
            ? $"Taikeron-Lab-{release.Version}.exe"
            : Path.GetFileName(release.File);

        var destination = Path.Combine(downloadsDirectory, fileName);
        var partial = destination + ".part";

        if (File.Exists(partial))
            File.Delete(partial);

        using var response = await _httpClient.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? release.Bytes;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        {
            var buffer = new byte[1024 * 128];
            long totalRead = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (contentLength > 0)
                    progress?.Report(Math.Clamp((double)totalRead / contentLength, 0, 1));
            }
        }

        var actualLength = new FileInfo(partial).Length;
        if (release.Bytes > 0 && actualLength != release.Bytes)
        {
            File.Delete(partial);
            throw new InvalidDataException($"Taille invalide : {actualLength} octets reçus, {release.Bytes} attendus.");
        }

        if (!string.IsNullOrWhiteSpace(release.Sha256))
        {
            var expected = NormalizeSha256(release.Sha256);
            var actual = await ComputeSha256Async(partial, cancellationToken);

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Échec de vérification SHA-256 du paquet Taikeron Lab.");
            }
        }

        File.Move(partial, destination, overwrite: true);
        progress?.Report(1);
        return destination;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf(':');
        if (separator >= 0)
            trimmed = trimmed[(separator + 1)..];
        return trimmed.Trim().ToLowerInvariant();
    }

    private static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var clean = version.Split('+')[0].Split('-')[0].Trim();
        return Version.TryParse(clean, out var parsed) ? parsed.ToString() : clean;
    }
}
