using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taikeron.Launcher.Services;

public sealed class LauncherSelfUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/servicesathe-eng/Taikeron-Launcher/releases/latest";

    private const string PackageName = "Taikeron-Launcher-win-x64.zip";
    private const string ShaName = "Taikeron-Launcher-win-x64.sha256.txt";
    private const string RootMarker = ".taikeron-launcher-root";
    private const string LauncherExe = "TaikeronLauncher.exe";
    private const string UpdaterExe = "TaikeronLauncherUpdater.exe";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public LauncherSelfUpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TaikeronLauncher/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task<LauncherReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApi, cancellationToken);
        if ((int)response.StatusCode == 404)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? string.Empty : string.Empty;
        var version = NormalizeVersion(tag.Replace("launcher-v", string.Empty, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(version) || !root.TryGetProperty("assets", out var assets))
            return null;

        LauncherReleaseAsset? package = null;
        LauncherReleaseAsset? sha = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty;
            var url = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
            var size = asset.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;

            if (string.Equals(name, PackageName, StringComparison.OrdinalIgnoreCase))
                package = new LauncherReleaseAsset(name, url, size);
            else if (string.Equals(name, ShaName, StringComparison.OrdinalIgnoreCase))
                sha = new LauncherReleaseAsset(name, url, size);
        }

        if (package is null || sha is null)
            return null;

        return new LauncherReleaseInfo(version, tag, package, sha);
    }

    public bool IsUpdateAvailable(LauncherReleaseInfo? release)
    {
        if (release is null)
            return false;

        return Version.TryParse(NormalizeVersion(CurrentVersion), out var current)
            && Version.TryParse(NormalizeVersion(release.Version), out var remote)
            && remote > current;
    }

    public async Task PrepareAndLaunchUpdateAsync(
        LauncherReleaseInfo release,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<LauncherDownloadProgress>? downloadProgress = null)
    {
        if (!IsUpdateAvailable(release))
            throw new InvalidOperationException("Aucune mise à jour du Launcher n’est nécessaire.");

        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var markerPath = Path.Combine(installDirectory, RootMarker);

        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                "Cette copie du Launcher n’est pas une installation gérée. Réinstalle d’abord la release publique pour activer l’auto-mise à jour.");
        }

        var jobDirectory = Path.Combine(
            Path.GetTempPath(),
            "Taikeron",
            "LauncherSelfUpdate",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);

        var packagePath = Path.Combine(jobDirectory, PackageName);
        var shaPath = Path.Combine(jobDirectory, ShaName);
        var stagingDirectory = Path.Combine(jobDirectory, "staging");

        progress?.Report($"Téléchargement du Launcher {release.Version}…");
        await LauncherUpdateDownload.DownloadAsync(_httpClient, release.Package.Url, packagePath,
            release.Package.Bytes, "Paquet Launcher", downloadProgress, cancellationToken);
        await LauncherUpdateDownload.DownloadAsync(_httpClient, release.Sha256File.Url, shaPath,
            release.Sha256File.Bytes, "Empreinte SHA-256", downloadProgress, cancellationToken);

        progress?.Report("Vérification SHA-256 du nouveau Launcher…");
        var shaText = await File.ReadAllTextAsync(shaPath, cancellationToken);
        var expectedMatch = Regex.Match(shaText, @"\b[0-9a-fA-F]{64}\b");
        if (!expectedMatch.Success)
            throw new InvalidDataException("Le fichier SHA-256 de la release Launcher est invalide.");

        var expectedSha = expectedMatch.Value.ToLowerInvariant();
        var actualSha = await ComputeSha256Async(packagePath, cancellationToken);
        if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le paquet Launcher téléchargé ne correspond pas à son SHA-256 officiel.");

        progress?.Report("Préparation de la nouvelle version…");
        Directory.CreateDirectory(stagingDirectory);
        await Task.Run(() => ZipFile.ExtractToDirectory(packagePath, stagingDirectory, overwriteFiles: true), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var stagedMarker = Path.Combine(stagingDirectory, RootMarker);
        var stagedLauncher = Path.Combine(stagingDirectory, LauncherExe);
        var stagedUpdater = Path.Combine(stagingDirectory, UpdaterExe);

        if (!File.Exists(stagedMarker))
            throw new InvalidDataException("Le paquet Launcher ne contient pas son marqueur de sécurité.");
        if (!File.Exists(stagedLauncher))
            throw new InvalidDataException("TaikeronLauncher.exe est absent du paquet de mise à jour.");
        if (!File.Exists(stagedUpdater))
            throw new InvalidDataException("TaikeronLauncherUpdater.exe est absent du paquet de mise à jour.");

        var targetFileVersion = NormalizeVersion(
            FileVersionInfo.GetVersionInfo(stagedLauncher).ProductVersion
            ?? FileVersionInfo.GetVersionInfo(stagedLauncher).FileVersion
            ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(targetFileVersion)
            && !string.Equals(targetFileVersion, NormalizeVersion(release.Version), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Le binaire du Launcher annonce {targetFileVersion} alors que la release annonce {release.Version}.");
        }

        var externalUpdater = Path.Combine(jobDirectory, UpdaterExe);
        File.Copy(stagedUpdater, externalUpdater, overwrite: true);

        var requestFile = Path.Combine(jobDirectory, "launcher-update-request.json");
        var resultFile = Path.Combine(jobDirectory, "launcher-update-result.json");

        var request = new
        {
            format = "taikeron_launcher_replace_request",
            installDirectory,
            stagingDirectory,
            launcherExecutableName = LauncherExe,
            markerFileName = RootMarker,
            targetVersion = release.Version,
            parentProcessId = Environment.ProcessId,
            resultFile
        };

        await File.WriteAllTextAsync(
            requestFile,
            JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report("Le Launcher va se fermer et se remplacer par la nouvelle version.");

        var startInfo = new ProcessStartInfo
        {
            FileName = externalUpdater,
            WorkingDirectory = jobDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(requestFile);

        cancellationToken.ThrowIfCancellationRequested();
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de démarrer le worker d’auto-mise à jour du Launcher.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string? value)
    {
        var clean = (value ?? string.Empty).Trim();
        var plus = clean.IndexOf('+');
        if (plus >= 0)
            clean = clean[..plus];
        clean = clean.TrimStart('v', 'V').Replace('-', '.');
        return Version.TryParse(clean, out var version) ? version.ToString() : clean;
    }
}

public sealed record LauncherReleaseAsset(string Name, string Url, long Bytes);

public sealed record LauncherReleaseInfo(
    string Version,
    string Tag,
    LauncherReleaseAsset Package,
    LauncherReleaseAsset Sha256File);
