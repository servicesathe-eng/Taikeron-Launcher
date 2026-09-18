using System.Diagnostics;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class TlUpdateWorkerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly LauncherSettingsService _settingsService;

    public TlUpdateWorkerService(LauncherSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string WorkerExecutable => Path.Combine(AppContext.BaseDirectory, "TaikeronUpdateWorker.exe");

    public async Task<TlReplaceResult> ReplaceAsync(
        string installerPath,
        TlReleaseManifest release,
        string? installedExecutable,
        string? currentVersion,
        IProgress<TlWorkerStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(WorkerExecutable))
            throw new FileNotFoundException("TaikeronUpdateWorker.exe est absent du launcher. Réinstalle le launcher avant de modifier TL.", WorkerExecutable);

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Le paquet TL vérifié est introuvable.", installerPath);

        if (string.IsNullOrWhiteSpace(release.Sha256))
            throw new InvalidOperationException("Le manifeste TL ne contient pas de SHA-256. Le remplacement destructif est refusé.");

        var installDirectory = !string.IsNullOrWhiteSpace(installedExecutable) && File.Exists(installedExecutable)
            ? Path.GetDirectoryName(Path.GetFullPath(installedExecutable))!
            : Path.Combine(_settingsService.Current.AppsRoot, "Lab");

        var executablePath = Path.Combine(installDirectory, "Taikeron Lab.exe");
        var jobsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Taikeron",
            "Launcher",
            "Jobs");

        Directory.CreateDirectory(jobsRoot);
        var jobDirectory = Path.Combine(jobsRoot, $"tlreplace-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);

        var requestFile = Path.Combine(jobDirectory, "request.json");
        var statusFile = Path.Combine(jobDirectory, "status.json");
        var resultFile = Path.Combine(jobDirectory, "result.json");

        var request = new
        {
            format = "taikeron_tl_replace_request",
            installerPath = Path.GetFullPath(installerPath),
            installerSha256 = release.Sha256,
            installerBytes = release.Bytes,
            installDirectory,
            executablePath,
            currentVersion = currentVersion ?? string.Empty,
            targetVersion = release.Version,
            dataRoot = Path.GetFullPath(_settingsService.Current.DataRoot),
            vaultRoot = Path.GetFullPath(_settingsService.Current.VaultRoot),
            dataVaultRoot = Path.GetFullPath(_settingsService.Current.DataVaultRoot),
            mapsRoot = Path.GetFullPath(_settingsService.Current.MapsRoot),
            backupRoot = string.IsNullOrWhiteSpace(_settingsService.Current.BackupRoot) ? string.Empty : Path.GetFullPath(_settingsService.Current.BackupRoot),
            jobDirectory,
            statusFile,
            resultFile
        };

        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(requestFile);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de démarrer le worker de remplacement TL.");

        string lastPhase = string.Empty;
        string lastMessage = string.Empty;

        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = ReadStatus(statusFile);
            if (status is not null && (status.Phase != lastPhase || status.Message != lastMessage))
            {
                lastPhase = status.Phase;
                lastMessage = status.Message;
                progress?.Report(status);
            }

            await Task.Delay(300, cancellationToken);
            process.Refresh();
        }

        await process.WaitForExitAsync(cancellationToken);

        var finalStatus = ReadStatus(statusFile);
        if (finalStatus is not null)
            progress?.Report(finalStatus);

        TlReplaceResult? result = null;
        try
        {
            if (File.Exists(resultFile))
            {
                result = JsonSerializer.Deserialize<TlReplaceResult>(
                    await File.ReadAllTextAsync(resultFile, cancellationToken),
                    JsonOptions);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Le worker TL a terminé mais son résultat est illisible.", ex);
        }

        if (result is null)
            throw new InvalidOperationException($"Le worker TL a terminé sans résultat exploitable (code {process.ExitCode}).");

        if (result.Ok)
        {
            TryDeleteFile(installerPath);
            TryDeleteFile(installerPath + ".part");
            TryDeleteDirectory(jobDirectory);
            CleanupOldJobs(jobsRoot);
        }

        return result;
    }

    private static void CleanupOldJobs(string jobsRoot)
    {
        if (!Directory.Exists(jobsRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(jobsRoot, "tlreplace-*", SearchOption.TopDirectoryOnly))
            TryDeleteDirectory(directory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    private static TlWorkerStatus? ReadStatus(string statusFile)
    {
        try
        {
            if (!File.Exists(statusFile))
                return null;

            return JsonSerializer.Deserialize<TlWorkerStatus>(File.ReadAllText(statusFile), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class TlWorkerStatus
{
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class TlReplaceResult
{
    public bool Ok { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public string InstalledFileVersion { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public bool OldCodeRemoved { get; set; }
    public string? OldAsarSha256 { get; set; }
    public string? NewAsarSha256 { get; set; }
    public string? InstallerSha256 { get; set; }
    public string? PreservedMapsPath { get; set; }
    public string? MigratedDataRoot { get; set; }
    public string? MigratedVaultRoot { get; set; }
    public string? MigratedMapsRoot { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
