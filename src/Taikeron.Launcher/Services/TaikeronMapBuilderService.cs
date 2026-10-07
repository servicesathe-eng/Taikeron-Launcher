using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class TaikeronMapBuilderService
{
    public const string StableManifestUrl = "https://taikeron-cyclingos.github.io/releases/tmb/stable.json";

    private readonly LauncherSettingsService _settingsService;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public TaikeronMapBuilderService(LauncherSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string CanonicalInstallDirectory =>
        Path.Combine(_settingsService.Current.AppsRoot, "MapBuilder");

    public string CanonicalExecutable =>
        Path.Combine(CanonicalInstallDirectory, "Taikeron Map Builder.exe");

    private string InstallationProofPath =>
        Path.Combine(CanonicalInstallDirectory, ".taikeron-installation.json");

    public string? FindInstalledExecutable()
    {
        if (File.Exists(CanonicalExecutable))
            return CanonicalExecutable;

        if (Directory.Exists(CanonicalInstallDirectory))
        {
            var managedPortable = Directory
                .EnumerateFiles(CanonicalInstallDirectory, "Taikeron_Map_Builder_*_Portable.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(managedPortable))
                return managedPortable;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] legacyCandidates =
        [
            Path.Combine(localAppData, "Programs", "Taikeron Map Builder", "Taikeron Map Builder.exe"),
            Path.Combine(localAppData, "Taikeron Map Builder", "Taikeron Map Builder.exe"),
            Path.Combine(programFiles, "Taikeron Map Builder", "Taikeron Map Builder.exe"),
            Path.Combine(programFilesX86, "Taikeron Map Builder", "Taikeron Map Builder.exe")
        ];

        return legacyCandidates.FirstOrDefault(File.Exists);
    }

    public string? GetInstalledVersion(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        if (IsLauncherManaged(executablePath))
        {
            var proofVersion = TryReadInstallationVersion();
            if (!string.IsNullOrWhiteSpace(proofVersion))
                return proofVersion;
        }

        var info = FileVersionInfo.GetVersionInfo(executablePath);
        return NormalizeVersion(info.FileVersion) ?? NormalizeVersion(info.ProductVersion);
    }

    public bool IsLauncherManaged(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        var executable = Path.GetFullPath(executablePath);
        var canonical = Path.GetFullPath(CanonicalExecutable);
        return string.Equals(executable, canonical, StringComparison.OrdinalIgnoreCase)
            && TryReadInstallationVersion() is not null;
    }

    public async Task<TmbReleaseManifest?> GetStableReleaseAsync(
        bool forceRefresh = false,
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
        var manifest = await JsonSerializer.DeserializeAsync<TmbReleaseManifest>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        if (manifest is null || !manifest.Available)
            return manifest;

        if (!string.Equals(manifest.Product, "TMB", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le manifeste stable reçu ne concerne pas TMB.");

        if (manifest.WindowsX64Portable is null
            || string.IsNullOrWhiteSpace(manifest.Url)
            || string.IsNullOrWhiteSpace(manifest.Sha256)
            || manifest.Bytes <= 0)
        {
            throw new InvalidDataException(
                "Le manifeste stable TMB ne contient pas de portable windows-x64 vérifiable.");
        }

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

    public async Task<TmbIntegrityCheckResult> VerifyAsync(
        string? executablePath,
        string? installedVersion,
        TmbReleaseManifest? stableRelease,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.NotInstalled,
                "Taikeron Map Builder n’est pas installé.",
                0,
                null,
                null);
        }

        if (!IsLauncherManaged(executablePath))
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.LegacyUnmanaged,
                "Installation TMB détectée hors du runtime géré par le Launcher.",
                new FileInfo(executablePath).Length,
                null,
                null);
        }

        if (stableRelease is null || stableRelease.WindowsX64Portable is null)
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.ReferenceUnavailable,
                "Référence stable TMB indisponible.",
                new FileInfo(executablePath).Length,
                null,
                null);
        }

        if (!VersionsEqual(installedVersion, stableRelease.Version))
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.ReferenceUnavailable,
                "Version TMB installée différente de la dernière release ; le paquet local n’est pas comparé au SHA de la dernière version.",
                new FileInfo(executablePath).Length,
                stableRelease.Sha256,
                null);
        }

        var actualLength = new FileInfo(executablePath).Length;
        if (actualLength != stableRelease.Bytes)
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.Corrupted,
                $"Taille TMB invalide : {actualLength} octets locaux, {stableRelease.Bytes} attendus.",
                actualLength,
                stableRelease.Sha256,
                null);
        }

        var actualSha = await ComputeSha256Async(executablePath, cancellationToken);
        if (!string.Equals(
                NormalizeSha256(stableRelease.Sha256),
                actualSha,
                StringComparison.OrdinalIgnoreCase))
        {
            return new TmbIntegrityCheckResult(
                TmbIntegrityState.Corrupted,
                "Le SHA-256 du runtime TMB installé ne correspond pas au paquet public officiel.",
                actualLength,
                stableRelease.Sha256,
                actualSha);
        }

        return new TmbIntegrityCheckResult(
            TmbIntegrityState.Healthy,
            "Runtime TMB vérifié contre le portable public officiel.",
            actualLength,
            stableRelease.Sha256,
            actualSha);
    }

    public async Task<string> DownloadAndVerifyAsync(
        TmbReleaseManifest release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.WindowsX64Portable is null)
            throw new InvalidOperationException("Portable TMB windows-x64 absent du manifeste.");

        if (string.IsNullOrWhiteSpace(release.Url))
            throw new InvalidOperationException("Le manifeste TMB ne contient pas d’URL de téléchargement.");
        if (string.IsNullOrWhiteSpace(release.Sha256))
            throw new InvalidOperationException("Le manifeste TMB ne contient pas de SHA-256.");

        var downloadsDirectory = _settingsService.Current.DownloadsRoot;
        Directory.CreateDirectory(downloadsDirectory);

        var fileName = string.IsNullOrWhiteSpace(release.File)
            ? $"Taikeron_Map_Builder_{release.Version}_Portable.exe"
            : Path.GetFileName(release.File);
        var destination = Path.Combine(downloadsDirectory, fileName);
        var partial = destination + ".part";

        TryDeleteFile(partial);

        using var response = await _httpClient.GetAsync(
            release.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? release.Bytes;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(
                         partial,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 128,
                         useAsync: true))
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
        if (actualLength != release.Bytes)
        {
            TryDeleteFile(partial);
            throw new InvalidDataException(
                $"Taille TMB invalide : {actualLength} octets reçus, {release.Bytes} attendus.");
        }

        var actualSha = await ComputeSha256Async(partial, cancellationToken);
        if (!string.Equals(
                NormalizeSha256(release.Sha256),
                actualSha,
                StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partial);
            throw new InvalidDataException("Échec de vérification SHA-256 du paquet Taikeron Map Builder.");
        }

        File.Move(partial, destination, overwrite: true);
        progress?.Report(1);
        return destination;
    }

    public async Task<string> InstallOrReplaceAsync(
        string verifiedPortablePath,
        TmbReleaseManifest release,
        string? currentExecutable,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_settingsService.Current.InitialSetupCompleted)
            throw new InvalidOperationException(
                "Configure d’abord l’emplacement des applications dans le Launcher.");

        if (!File.Exists(verifiedPortablePath))
            throw new FileNotFoundException("Le portable TMB vérifié est introuvable.", verifiedPortablePath);

        progress?.Report("Fermeture de Taikeron Map Builder…");
        await StopManagedOrSelectedTmbAsync(currentExecutable, cancellationToken);

        Directory.CreateDirectory(CanonicalInstallDirectory);

        var staging = CanonicalExecutable + ".new";
        var previous = CanonicalExecutable + ".previous";
        TryDeleteFile(staging);
        TryDeleteFile(previous);

        progress?.Report("Copie du runtime TMB vérifié…");
        File.Copy(verifiedPortablePath, staging, overwrite: true);

        var stagedLength = new FileInfo(staging).Length;
        if (stagedLength != release.Bytes)
        {
            TryDeleteFile(staging);
            throw new InvalidDataException("La copie locale du portable TMB a une taille inattendue.");
        }

        var stagedSha = await ComputeSha256Async(staging, cancellationToken);
        if (!string.Equals(
                NormalizeSha256(release.Sha256),
                stagedSha,
                StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(staging);
            throw new InvalidDataException("Le SHA-256 du runtime TMB copié a changé.");
        }

        try
        {
            if (File.Exists(CanonicalExecutable))
                File.Move(CanonicalExecutable, previous, overwrite: true);

            File.Move(staging, CanonicalExecutable, overwrite: true);
            await WriteInstallationProofAsync(release, cancellationToken);
            TryDeleteFile(previous);
            CleanupManagedPortableResidues();
            TryDeleteFile(verifiedPortablePath);
            TryDeleteFile(verifiedPortablePath + ".part");
            return CanonicalExecutable;
        }
        catch
        {
            TryDeleteFile(staging);
            if (!File.Exists(CanonicalExecutable) && File.Exists(previous))
                File.Move(previous, CanonicalExecutable, overwrite: true);
            throw;
        }
    }

    public void Launch(string executablePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Taikeron Map Builder est introuvable.", executablePath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.Environment["TAIKERON_MAPS_ROOT"] = _settingsService.Current.MapsRoot;
        startInfo.Environment["TAIKERON_LAUNCHER_MANAGED"] = "1";
        Process.Start(startInfo);
    }

    public async Task<string> UninstallAsync(
        string? executablePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Fermeture de Taikeron Map Builder…");
        await StopManagedOrSelectedTmbAsync(executablePath, cancellationToken);

        var canonical = Path.GetFullPath(CanonicalInstallDirectory);
        if (Directory.Exists(canonical))
        {
            progress?.Report("Suppression du runtime TMB géré par le Launcher…");
            Directory.Delete(canonical, recursive: true);
        }

        CleanupTmbDownloads(_settingsService.Current.DownloadsRoot);
        return canonical;
    }

    private async Task StopManagedOrSelectedTmbAsync(
        string? selectedExecutable,
        CancellationToken cancellationToken)
    {
        var installRoot = Path.GetFullPath(CanonicalInstallDirectory);
        var selected = string.IsNullOrWhiteSpace(selectedExecutable)
            ? null
            : Path.GetFullPath(selectedExecutable);

        var processes = Process.GetProcesses()
            .Where(process =>
            {
                try
                {
                    var fileName = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(fileName))
                        return false;
                    var full = Path.GetFullPath(fileName);
                    return IsPathInside(full, installRoot)
                        || (selected is not null
                            && string.Equals(full, selected, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            })
            .ToList();

        foreach (var process in processes)
        {
            try { process.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && processes.Any(p => !p.HasExited))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
        }

        foreach (var process in processes.Where(p => !p.HasExited))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch { }
        }

        if (processes.Any(p => !p.HasExited))
            throw new InvalidOperationException("Taikeron Map Builder est encore actif.");
    }

    private async Task WriteInstallationProofAsync(
        TmbReleaseManifest release,
        CancellationToken cancellationToken)
    {
        var proof = new
        {
            format = "taikeron_launcher_installation",
            product = "TMB",
            version = NormalizeVersion(release.Version) ?? release.Version,
            sourceFile = release.File,
            sha256 = NormalizeSha256(release.Sha256),
            bytes = release.Bytes,
            installedAtUtc = DateTimeOffset.UtcNow
        };

        await File.WriteAllTextAsync(
            InstallationProofPath,
            JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private string? TryReadInstallationVersion()
    {
        try
        {
            if (!File.Exists(InstallationProofPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(InstallationProofPath));
            var root = document.RootElement;
            var format = root.TryGetProperty("format", out var formatNode) ? formatNode.GetString() : null;
            var product = root.TryGetProperty("product", out var productNode) ? productNode.GetString() : null;
            var version = root.TryGetProperty("version", out var versionNode) ? versionNode.GetString() : null;

            if (!string.Equals(format, "taikeron_launcher_installation", StringComparison.Ordinal)
                || !string.Equals(product, "TMB", StringComparison.OrdinalIgnoreCase))
                return null;

            return NormalizeVersion(version);
        }
        catch
        {
            return null;
        }
    }

    private void CleanupManagedPortableResidues()
    {
        if (!Directory.Exists(CanonicalInstallDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(
                     CanonicalInstallDirectory,
                     "Taikeron_Map_Builder_*_Portable.exe",
                     SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(
                    Path.GetFullPath(file),
                    Path.GetFullPath(CanonicalExecutable),
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFile(file);
            }
        }
    }

    private static void CleanupTmbDownloads(string downloadsRoot)
    {
        try
        {
            if (!Directory.Exists(downloadsRoot))
                return;

            foreach (var file in Directory.EnumerateFiles(
                         downloadsRoot,
                         "Taikeron_Map_Builder_*",
                         SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(file);
            }
        }
        catch { }
    }

    private static bool IsPathInside(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool VersionsEqual(string? first, string? second)
    {
        var a = NormalizeVersion(first);
        var b = NormalizeVersion(second);
        return !string.IsNullOrWhiteSpace(a)
            && !string.IsNullOrWhiteSpace(b)
            && Version.TryParse(a, out var av)
            && Version.TryParse(b, out var bv)
            && av == bv;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { }
    }
}
