using System.Diagnostics;
using System.Text.Json;

namespace Taikeron.LauncherUpdater;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        LauncherUpdateRequest? request = null;
        string? backupDirectory = null;

        try
        {
            if (args.Length != 1)
                throw new ArgumentException("Usage: TaikeronLauncherUpdater <request.json>");

            var requestFile = Path.GetFullPath(args[0]);
            request = JsonSerializer.Deserialize<LauncherUpdateRequest>(
                await File.ReadAllTextAsync(requestFile),
                JsonOptions) ?? throw new InvalidDataException("Requête d’auto-mise à jour Launcher invalide.");

            ValidateRequest(request);

            var installDirectory = NormalizeDirectory(request.InstallDirectory);
            var stagingDirectory = NormalizeDirectory(request.StagingDirectory);
            var launcherExe = Path.Combine(installDirectory, request.LauncherExecutableName);
            var marker = Path.Combine(installDirectory, request.MarkerFileName);
            var stagedLauncher = Path.Combine(stagingDirectory, request.LauncherExecutableName);
            var stagedMarker = Path.Combine(stagingDirectory, request.MarkerFileName);

            ValidateManagedRoot(installDirectory, launcherExe, marker);
            ValidateStaging(stagingDirectory, stagedLauncher, stagedMarker);

            if (IsPathInside(stagingDirectory, installDirectory) || IsPathInside(installDirectory, stagingDirectory))
                throw new InvalidOperationException("Le staging du Launcher doit être extérieur au dossier à remplacer.");

            await WaitForParentExitAsync(request.ParentProcessId);

            backupDirectory = installDirectory + $".old-{Guid.NewGuid():N}";
            if (Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, true);

            Directory.Move(installDirectory, backupDirectory);

            try
            {
                Directory.CreateDirectory(installDirectory);
                CopyDirectory(stagingDirectory, installDirectory);

                var newLauncher = Path.Combine(installDirectory, request.LauncherExecutableName);
                var newMarker = Path.Combine(installDirectory, request.MarkerFileName);

                if (!File.Exists(newLauncher) || !File.Exists(newMarker))
                    throw new InvalidDataException("La nouvelle installation Launcher est incomplète.");

                var installedVersion = NormalizeVersion(
                    FileVersionInfo.GetVersionInfo(newLauncher).ProductVersion
                    ?? FileVersionInfo.GetVersionInfo(newLauncher).FileVersion
                    ?? string.Empty);

                var targetVersion = NormalizeVersion(request.TargetVersion);
                if (!string.IsNullOrWhiteSpace(installedVersion)
                    && !string.Equals(installedVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Version Launcher installée {installedVersion} différente de la cible {targetVersion}.");
                }

                using var launched = Process.Start(new ProcessStartInfo
                {
                    FileName = newLauncher,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException("Impossible de relancer le nouveau Launcher.");

                await Task.Delay(TimeSpan.FromSeconds(5));
                if (launched.HasExited)
                    throw new InvalidOperationException("Le nouveau Launcher s’est arrêté immédiatement après la mise à jour.");

                DeleteDirectoryStrict(backupDirectory);
                backupDirectory = null;

                await WriteResultAsync(request.ResultFile, new
                {
                    ok = true,
                    targetVersion = request.TargetVersion,
                    installedVersion,
                    installDirectory,
                    completedAtUtc = DateTimeOffset.UtcNow
                });

                return 0;
            }
            catch
            {
                try
                {
                    if (Directory.Exists(installDirectory))
                        DeleteDirectoryStrict(installDirectory);

                    if (!string.IsNullOrWhiteSpace(backupDirectory) && Directory.Exists(backupDirectory))
                    {
                        Directory.Move(backupDirectory, installDirectory);
                        backupDirectory = null;
                    }
                }
                catch
                {
                    // Preserve the original update error.
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (request is not null && !string.IsNullOrWhiteSpace(request.ResultFile))
                {
                    await WriteResultAsync(request.ResultFile, new
                    {
                        ok = false,
                        targetVersion = request.TargetVersion,
                        error = ex.Message,
                        backupDirectory,
                        completedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }
            catch { }

            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ValidateRequest(LauncherUpdateRequest request)
    {
        if (!string.Equals(request.Format, "taikeron_launcher_replace_request", StringComparison.Ordinal))
            throw new InvalidDataException("Format de requête Launcher inconnu.");

        if (string.IsNullOrWhiteSpace(request.InstallDirectory)
            || string.IsNullOrWhiteSpace(request.StagingDirectory)
            || string.IsNullOrWhiteSpace(request.LauncherExecutableName)
            || string.IsNullOrWhiteSpace(request.MarkerFileName)
            || string.IsNullOrWhiteSpace(request.TargetVersion)
            || request.ParentProcessId <= 0)
        {
            throw new InvalidDataException("Requête d’auto-mise à jour Launcher incomplète.");
        }

        if (!string.Equals(request.LauncherExecutableName, "TaikeronLauncher.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nom d’exécutable Launcher inattendu.");

        if (!string.Equals(request.MarkerFileName, ".taikeron-launcher-root", StringComparison.Ordinal))
            throw new InvalidDataException("Marqueur de dossier Launcher inattendu.");
    }

    private static void ValidateManagedRoot(string installDirectory, string launcherExe, string marker)
    {
        var root = Path.GetPathRoot(installDirectory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, installDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refus de remplacer une racine de disque.");

        string[] forbiddenRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        ];

        if (forbiddenRoots.Any(path =>
            !string.IsNullOrWhiteSpace(path)
            && string.Equals(NormalizeDirectory(path), installDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Le dossier Launcher cible est trop large pour être remplacé.");
        }

        if (!File.Exists(marker))
            throw new InvalidOperationException("Marqueur de sécurité Launcher absent. Remplacement refusé.");

        if (!File.Exists(launcherExe))
            throw new InvalidOperationException("TaikeronLauncher.exe absent de l’installation actuelle.");
    }

    private static void ValidateStaging(string stagingDirectory, string stagedLauncher, string stagedMarker)
    {
        if (!Directory.Exists(stagingDirectory))
            throw new DirectoryNotFoundException("Dossier de staging Launcher absent.");

        if (!File.Exists(stagedMarker))
            throw new InvalidDataException("Marqueur de sécurité absent du nouveau paquet Launcher.");

        if (!File.Exists(stagedLauncher))
            throw new InvalidDataException("TaikeronLauncher.exe absent du nouveau paquet Launcher.");
    }

    private static async Task WaitForParentExitAsync(int processId)
    {
        Process? parent = null;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }

        using (parent)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!parent.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            if (!parent.HasExited)
                throw new TimeoutException("Le Launcher actuel ne s’est pas fermé dans le délai autorisé.");
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
            File.Copy(file, target, true);
        }
    }

    private static void DeleteDirectoryStrict(string directory)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 20; attempt++)
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

            Thread.Sleep(300);
        }

        throw new IOException($"Impossible de supprimer {directory}.", lastError);
    }

    private static bool IsPathInside(string candidate, string parent)
    {
        var fullCandidate = NormalizeDirectory(candidate) + Path.DirectorySeparatorChar;
        var fullParent = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeVersion(string? value)
    {
        var clean = (value ?? string.Empty).Trim();
        var plus = clean.IndexOf('+');
        if (plus >= 0)
            clean = clean[..plus];
        clean = clean.TrimStart('v', 'V').Replace('-', '.');
        return Version.TryParse(clean, out var version) ? version.ToString() : clean;
    }

    private static async Task WriteResultAsync(string? path, object value)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}

internal sealed class LauncherUpdateRequest
{
    public string Format { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public string StagingDirectory { get; set; } = string.Empty;
    public string LauncherExecutableName { get; set; } = string.Empty;
    public string MarkerFileName { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public int ParentProcessId { get; set; }
    public string ResultFile { get; set; } = string.Empty;
}
