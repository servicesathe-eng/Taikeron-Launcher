using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;
using Taikeron.Shared;

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

    public Task<TlReleaseManifest?> GetStableReleaseAsync(CancellationToken cancellationToken = default) =>
        GetStableReleaseAsync(forceRefresh: false, cancellationToken);

    public async Task<TlReleaseManifest?> GetStableReleaseAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var requestUrl = forceRefresh
            ? $"{StableManifestUrl}?refresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : StableManifestUrl;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (forceRefresh)
        {
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            request.Headers.Pragma.ParseAdd("no-cache");
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
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
        // TL <= 2.302.6.4 interprets TAIKERON_DATA_ROOT as a common storage
        // parent and appends data/vault itself. Keep that compatibility contract
        // while also exposing the canonical split roots for newer TL builds.
        startInfo.Environment["TAIKERON_DATA_ROOT"] = _settingsService.Current.DataVaultRoot;
        startInfo.Environment["TAIKERON_DATA_DIR"] = _settingsService.Current.DataRoot;
        startInfo.Environment["TAIKERON_VAULT_ROOT"] = _settingsService.Current.VaultRoot;
        startInfo.Environment["TAIKERON_MAPS_ROOT"] = _settingsService.Current.MapsRoot;

        Process.Start(startInfo);
    }

    private void EnsureConfiguredDataAvailable(string executablePath)
    {
        var dataRoot = Path.GetFullPath(_settingsService.Current.DataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var vaultRoot = Path.GetFullPath(_settingsService.Current.VaultRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(vaultRoot))
            throw new InvalidOperationException("Data ou Vault n’est pas configuré.");

        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        foreach (var persistent in new[] { dataRoot, vaultRoot })
        {
            if (string.Equals(persistent, installDirectory, StringComparison.OrdinalIgnoreCase)
                || IsPathInside(persistent, installDirectory))
            {
                throw new InvalidOperationException("Data et Vault doivent être séparés du dossier d’installation de Taikeron Lab.");
            }
        }

        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(vaultRoot);

        CopyLegacyDirectoryIfDestinationEmpty(
            Path.Combine(installDirectory, "data"),
            dataRoot);
        CopyLegacyDirectoryIfDestinationEmpty(
            Path.Combine(installDirectory, "vault"),
            vaultRoot);
    }

    private static void CopyLegacyDirectoryIfDestinationEmpty(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            return;

        if (Directory.Exists(destination))
            Directory.Delete(destination, true);

        CopyDirectory(source, destination);
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

    public async Task<TlUninstallResult> UninstallAsync(
        string? executablePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installDirectory = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? Path.GetDirectoryName(Path.GetFullPath(executablePath))!
            : CanonicalInstallDirectory;

        var dataRoot = Path.GetFullPath(_settingsService.Current.DataRoot);
        var vaultRoot = Path.GetFullPath(_settingsService.Current.VaultRoot);
        var mapsRoot = Path.GetFullPath(_settingsService.Current.MapsRoot);
        var backupRoot = string.IsNullOrWhiteSpace(_settingsService.Current.BackupRoot)
            ? string.Empty
            : Path.GetFullPath(_settingsService.Current.BackupRoot);

        foreach (var persistent in new[] { dataRoot, vaultRoot, mapsRoot, backupRoot }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (string.Equals(Path.GetFullPath(persistent), Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase)
                || IsPathInside(persistent, installDirectory))
            {
                throw new InvalidOperationException("Data / Vault / Maps / sauvegardes doivent être extérieurs au dossier d’installation avant désinstallation.");
            }
        }

        progress?.Report("Fermeture complète de Taikeron Lab…");
        await StopTaikeronLabAsync(installDirectory, cancellationToken);

        progress?.Report("Protection des données persistantes…");
        MergeLegacyDirectoryNoOverwrite(Path.Combine(installDirectory, "data"), dataRoot);
        MergeLegacyDirectoryNoOverwrite(Path.Combine(installDirectory, "vault"), vaultRoot);
        MergeLegacyDirectoryNoOverwrite(Path.Combine(installDirectory, "maps"), mapsRoot);

        progress?.Report("Suppression des profils et caches Electron/Chromium…");
        var cleanup = TlRuntimeCleanup.PurgeVolatileState(
            new[] { dataRoot, vaultRoot, mapsRoot, backupRoot },
            message => progress?.Report(message));

        progress?.Report("Suppression du dossier application Taikeron Lab…");
        if (Directory.Exists(installDirectory))
            DeleteDirectoryStrict(installDirectory);

        if (Directory.Exists(installDirectory))
            throw new IOException("Le dossier Taikeron Lab existe encore après la désinstallation.");

        progress?.Report("Suppression des paquets TL téléchargés et temporaires…");
        CleanupTlDownloads(_settingsService.Current.DownloadsRoot);

        return new TlUninstallResult(
            true,
            installDirectory,
            cleanup.RemovedDirectories.Count,
            cleanup.RemovedFiles.Count,
            dataRoot,
            vaultRoot,
            mapsRoot);
    }

    private static async Task StopTaikeronLabAsync(string installDirectory, CancellationToken cancellationToken)
    {
        foreach (var process in FindTaikeronProcesses(installDirectory).ToList())
        {
            try { process.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && FindTaikeronProcesses(installDirectory).Any())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
        }

        foreach (var process in FindTaikeronProcesses(installDirectory).ToList())
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch { }
        }

        if (FindTaikeronProcesses(installDirectory).Any())
            throw new InvalidOperationException("Taikeron Lab est encore actif. Désinstallation refusée.");
    }

    private static IEnumerable<Process> FindTaikeronProcesses(string installDirectory)
    {
        foreach (var process in Process.GetProcesses())
        {
            string? executable = null;
            try { executable = process.MainModule?.FileName; } catch { }

            if (!string.IsNullOrWhiteSpace(executable) && IsPathInside(executable, installDirectory))
            {
                yield return process;
                continue;
            }

            try
            {
                if (string.Equals(process.ProcessName, "Taikeron Lab", StringComparison.OrdinalIgnoreCase))
                    yield return process;
            }
            catch { }
        }
    }

    private static void MergeLegacyDirectoryNoOverwrite(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;

        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
                File.Copy(file, target);
        }
    }

    private static void CleanupTlDownloads(string downloadsRoot)
    {
        try
        {
            if (!Directory.Exists(downloadsRoot))
                return;

            foreach (var file in Directory.EnumerateFiles(downloadsRoot, "Taikeron-Lab-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void DeleteDirectoryStrict(string directory)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(directory, true);
                if (!Directory.Exists(directory))
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }

            Thread.Sleep(250);
        }

        throw new IOException("Impossible de supprimer complètement le dossier Taikeron Lab.", lastError);
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


public sealed record TlUninstallResult(
    bool Ok,
    string InstallDirectory,
    int RemovedRuntimeDirectories,
    int RemovedRuntimeFiles,
    string DataRoot,
    string VaultRoot,
    string MapsRoot);
