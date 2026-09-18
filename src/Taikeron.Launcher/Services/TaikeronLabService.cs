using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class TaikeronLabService
{
    public const string StableManifestUrl = "https://taikeron-cyclingos.github.io/releases/tl/stable.json";

    private readonly LauncherSettingsService _settingsService;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public TaikeronLabService(LauncherSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string CanonicalInstallDirectory => Path.Combine(_settingsService.Current.AppsRoot, "Lab");
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

        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            var proofPath = Path.Combine(installDirectory, ".taikeron-installation.json");
            var proofVersion = TryReadLauncherInstallationVersion(proofPath);
            if (!string.IsNullOrWhiteSpace(proofVersion))
                return proofVersion;
        }

        var info = FileVersionInfo.GetVersionInfo(executablePath);
        return NormalizeVersion(info.FileVersion) ?? NormalizeVersion(info.ProductVersion);
    }

    private static string? TryReadLauncherInstallationVersion(string proofPath)
    {
        try
        {
            if (!File.Exists(proofPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(proofPath));
            var root = document.RootElement;

            var format = root.TryGetProperty("format", out var formatNode) ? formatNode.GetString() : null;
            var product = root.TryGetProperty("product", out var productNode) ? productNode.GetString() : null;
            var version = root.TryGetProperty("version", out var versionNode) ? versionNode.GetString() : null;

            if (!string.Equals(format, "taikeron_launcher_installation", StringComparison.Ordinal) ||
                !string.Equals(product, "TL", StringComparison.OrdinalIgnoreCase))
                return null;

            var normalized = NormalizeVersion(version);
            return Version.TryParse(normalized, out _) ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<TlReleaseManifest?> GetStableReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(StableManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var manifest = await JsonSerializer.DeserializeAsync<TlReleaseManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken);

        if (manifest is null || !manifest.Available || manifest.WindowsX64 is null)
            return manifest;

        if (string.IsNullOrWhiteSpace(manifest.Url) || string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new InvalidDataException("Le manifeste stable TL ne contient pas de paquet windows-x64 vérifiable.");

        return manifest;
    }

    public bool IsUpdateAvailable(string? installedVersion, string? remoteVersion)
    {
        var localNormalized = NormalizeVersion(installedVersion);
        var remoteNormalized = NormalizeVersion(remoteVersion);

        if (string.IsNullOrWhiteSpace(remoteNormalized))
            return false;

        if (string.IsNullOrWhiteSpace(localNormalized))
            return true;

        return Version.TryParse(localNormalized, out var local)
            && Version.TryParse(remoteNormalized, out var remote)
            && remote > local;
    }

    public void Launch(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Taikeron Lab est introuvable.", executablePath);

        if (!_settingsService.Current.InitialSetupCompleted)
            throw new InvalidOperationException("Configure d’abord l’emplacement des applications et des données dans le Launcher.");

        EnsureConfiguredDataAvailable(executablePath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.Environment["TAIKERON_DATA_ROOT"] = _settingsService.Current.DataVaultRoot;
        startInfo.Environment["TAIKERON_MAPS_ROOT"] = _settingsService.Current.MapsRoot;

        Process.Start(startInfo);
    }

    private void EnsureConfiguredDataAvailable(string executablePath)
    {
        var dataRoot = Path.GetFullPath(_settingsService.Current.DataVaultRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new InvalidOperationException("Le Data Vault n’est pas configuré.");

        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (string.Equals(dataRoot, installDirectory, StringComparison.OrdinalIgnoreCase)
            || IsPathInside(dataRoot, installDirectory))
        {
            throw new InvalidOperationException("Le Data Vault doit être séparé du dossier d’installation de Taikeron Lab.");
        }

        Directory.CreateDirectory(dataRoot);

        foreach (var name in new[] { "data", "vault" })
        {
            var destination = Path.Combine(dataRoot, name);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                continue;

            var source = Path.Combine(installDirectory, name);
            if (!Directory.Exists(source))
                continue;

            if (Directory.Exists(destination))
                Directory.Delete(destination, true);

            CopyDirectory(source, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsPathInside(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> DownloadAndVerifyAsync(
        TlReleaseManifest release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.Url))
            throw new InvalidOperationException("Le manifeste TL ne contient pas d’URL de téléchargement.");
        if (string.IsNullOrWhiteSpace(release.Sha256))
            throw new InvalidOperationException("Le manifeste TL ne contient pas de SHA-256.");

        var downloadsDirectory = _settingsService.Current.DownloadsRoot;
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

        var expected = NormalizeSha256(release.Sha256);
        var actual = await ComputeSha256Async(partial, cancellationToken);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("Échec de vérification SHA-256 du paquet Taikeron Lab.");
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

        var clean = version.Split('+')[0].Trim().Replace('-', '.');
        return Version.TryParse(clean, out var parsed) ? parsed.ToString() : clean;
    }
}
