using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Taikeron.UpdateWorker;

internal static class ProgramV2
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: TaikeronUpdateWorker <request.json>");
            return 2;
        }

        ReplaceRequest? request = null;
        string? resultFile = null;
        string? preservedMapsPath = null;
        var oldCodeRemoved = false;

        try
        {
            var requestFile = Path.GetFullPath(args[0]);
            request = JsonSerializer.Deserialize<ReplaceRequest>(await File.ReadAllTextAsync(requestFile), JsonOptions)
                ?? throw new InvalidDataException("Requête de remplacement TL invalide.");

            ValidateRequest(request);
            resultFile = Path.GetFullPath(request.ResultFile);
            var statusFile = Path.GetFullPath(request.StatusFile);
            var jobDirectory = Path.GetFullPath(request.JobDirectory);
            Directory.CreateDirectory(jobDirectory);

            WriteStatus(statusFile, "staging", "Préparation du paquet TL vérifié.");

            var installer = Path.GetFullPath(request.InstallerPath);
            if (!File.Exists(installer))
                throw new FileNotFoundException("Installateur TL introuvable.", installer);

            if (request.InstallerBytes > 0 && new FileInfo(installer).Length != request.InstallerBytes)
                throw new InvalidDataException("La taille de l’installateur ne correspond plus au manifeste.");

            var installerSha = await ComputeSha256Async(installer);
            if (!string.Equals(NormalizeSha(request.InstallerSha256), installerSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Le SHA-256 de l’installateur ne correspond plus au manifeste.");

            var stagingDirectory = Path.Combine(jobDirectory, "staging");
            Directory.CreateDirectory(stagingDirectory);
            var stagedInstaller = Path.Combine(stagingDirectory, Path.GetFileName(installer));
            File.Copy(installer, stagedInstaller, true);

            if (!string.Equals(await ComputeSha256Async(stagedInstaller), installerSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Le paquet TL a été altéré pendant le staging.");

            var installDirectory = Path.GetFullPath(request.InstallDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var executablePath = Path.GetFullPath(request.ExecutablePath);
            ValidateInstallTarget(installDirectory, executablePath);

            var oldAsar = Path.Combine(installDirectory, "resources", "app.asar");
            var oldAsarSha = File.Exists(oldAsar) ? await ComputeSha256Async(oldAsar) : null;
            var oldExeSha = File.Exists(executablePath) ? await ComputeSha256Async(executablePath) : null;

            WriteStatus(statusFile, "closing", "Fermeture complète de Taikeron Lab.");
            await StopTaikeronLabAsync(installDirectory);

            var mapsSource = Path.Combine(installDirectory, "maps");
            if (Directory.Exists(mapsSource))
            {
                preservedMapsPath = Path.Combine(jobDirectory, "preserved-maps");
                if (Directory.Exists(preservedMapsPath))
                    Directory.Delete(preservedMapsPath, true);

                WriteStatus(statusFile, "preserving-data", "Protection des cartes locales avant nettoyage.");
                MoveOrCopyDirectory(mapsSource, preservedMapsPath);
            }

            if (Directory.Exists(installDirectory))
            {
                WriteStatus(statusFile, "cleaning", "Suppression réelle de l’ancien code Taikeron Lab.");
                DeleteDirectoryStrict(installDirectory);
            }
            oldCodeRemoved = !Directory.Exists(installDirectory);

            if (!oldCodeRemoved)
                throw new IOException("L’ancien dossier TL existe encore après le nettoyage. Installation refusée.");

            WriteStatus(statusFile, "installing", $"Installation propre de Taikeron Lab {request.TargetVersion}.");
            Directory.CreateDirectory(Path.GetDirectoryName(installDirectory)!);

            var startInfo = new ProcessStartInfo
            {
                FileName = stagedInstaller,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = stagingDirectory
            };
            startInfo.ArgumentList.Add("/S");
            startInfo.ArgumentList.Add("--updated");
            startInfo.ArgumentList.Add("/D=" + installDirectory);

            using (var installerProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Impossible de démarrer l’installateur TL."))
            {
                await installerProcess.WaitForExitAsync();
                if (installerProcess.ExitCode != 0 && installerProcess.ExitCode != 3010)
                    throw new InvalidOperationException($"L’installateur TL a quitté avec le code {installerProcess.ExitCode}.");
            }

            var installedExecutable = Path.Combine(installDirectory, "Taikeron Lab.exe");
            var installedAsar = Path.Combine(installDirectory, "resources", "app.asar");

            if (!File.Exists(installedExecutable))
                throw new FileNotFoundException("Taikeron Lab.exe est absent après installation.", installedExecutable);
            if (!File.Exists(installedAsar))
                throw new FileNotFoundException("resources\\app.asar est absent après installation.", installedAsar);

            WriteStatus(statusFile, "verifying", "Vérification du nouveau runtime actif.");
            var newExeSha = await ComputeSha256Async(installedExecutable);
            var newAsarSha = await ComputeSha256Async(installedAsar);
            var versionInfo = FileVersionInfo.GetVersionInfo(installedExecutable);
            var installedFileVersion = NormalizeVersion(versionInfo.ProductVersion ?? versionInfo.FileVersion ?? string.Empty);
            var targetVersion = NormalizeVersion(request.TargetVersion);

            if (!string.IsNullOrWhiteSpace(targetVersion) &&
                !string.IsNullOrWhiteSpace(installedFileVersion) &&
                !string.Equals(installedFileVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Version installée {installedFileVersion} différente de la cible {targetVersion}.");
            }

            if (!SameVersion(request.CurrentVersion, request.TargetVersion))
            {
                if (!string.IsNullOrWhiteSpace(oldAsarSha) && string.Equals(oldAsarSha, newAsarSha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Le nouvel app.asar est identique à l’ancien malgré un changement de version.");

                if (string.IsNullOrWhiteSpace(oldAsarSha) &&
                    !string.IsNullOrWhiteSpace(oldExeSha) &&
                    string.Equals(oldExeSha, newExeSha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Le nouvel exécutable est identique à l’ancien malgré un changement de version.");
                }
            }

            if (!string.IsNullOrWhiteSpace(preservedMapsPath) && Directory.Exists(preservedMapsPath))
            {
                WriteStatus(statusFile, "restoring-data", "Restauration des cartes locales.");
                var mapsDestination = Path.Combine(installDirectory, "maps");
                if (Directory.Exists(mapsDestination))
                    Directory.Delete(mapsDestination, true);
                MoveOrCopyDirectory(preservedMapsPath, mapsDestination);
                preservedMapsPath = null;
            }

            await WriteJsonAsync(resultFile, new ReplaceResult
            {
                Ok = true,
                TargetVersion = request.TargetVersion,
                InstalledFileVersion = installedFileVersion,
                InstallDirectory = installDirectory,
                ExecutablePath = installedExecutable,
                OldCodeRemoved = true,
                OldAsarSha256 = oldAsarSha,
                NewAsarSha256 = newAsarSha,
                InstallerSha256 = installerSha,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });

            WriteStatus(statusFile, "completed", "Ancien runtime supprimé et nouveau runtime TL vérifié.");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(resultFile))
                {
                    await WriteJsonAsync(resultFile, new ReplaceResult
                    {
                        Ok = false,
                        TargetVersion = request?.TargetVersion ?? string.Empty,
                        InstallDirectory = request?.InstallDirectory ?? string.Empty,
                        ExecutablePath = request?.ExecutablePath ?? string.Empty,
                        OldCodeRemoved = oldCodeRemoved,
                        PreservedMapsPath = preservedMapsPath,
                        Error = ex.Message,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
                }

                if (request is not null && !string.IsNullOrWhiteSpace(request.StatusFile))
                    WriteStatus(Path.GetFullPath(request.StatusFile), "failed", ex.Message);
            }
            catch { }

            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ValidateRequest(ReplaceRequest request)
    {
        if (!string.Equals(request.Format, "taikeron_tl_replace_request", StringComparison.Ordinal))
            throw new InvalidDataException("Format de requête worker inconnu.");
        if (string.IsNullOrWhiteSpace(request.InstallerPath) || string.IsNullOrWhiteSpace(request.InstallerSha256))
            throw new InvalidDataException("Paquet TL ou SHA-256 absent de la requête.");
        if (string.IsNullOrWhiteSpace(request.InstallDirectory) || string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new InvalidDataException("Cible d’installation TL absente de la requête.");
        if (string.IsNullOrWhiteSpace(request.ResultFile) || string.IsNullOrWhiteSpace(request.StatusFile) || string.IsNullOrWhiteSpace(request.JobDirectory))
            throw new InvalidDataException("Fichiers de suivi worker absents de la requête.");
    }

    private static void ValidateInstallTarget(string installDirectory, string executablePath)
    {
        var root = Path.GetPathRoot(installDirectory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, installDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refus de nettoyer une racine de disque.");

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
            !string.IsNullOrWhiteSpace(path) &&
            string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), installDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Le dossier cible est trop large pour être nettoyé en sécurité.");
        }

        if (!IsPathInside(executablePath, installDirectory) ||
            !string.Equals(Path.GetFileName(executablePath), "Taikeron Lab.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("L’exécutable TL attendu n’appartient pas au dossier d’installation demandé.");
        }

        if (File.Exists(executablePath))
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var productName = info.ProductName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(productName) && !productName.Contains("Taikeron", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le dossier cible ne contient pas une installation Taikeron Lab reconnue.");
        }
        else if (Directory.Exists(installDirectory) && Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            throw new InvalidOperationException("Le dossier cible existe mais ne contient pas Taikeron Lab.exe. Nettoyage refusé.");
        }
    }

    private static async Task StopTaikeronLabAsync(string installDirectory)
    {
        foreach (var process in FindTaikeronProcesses(installDirectory).ToList())
        {
            try { process.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && FindTaikeronProcesses(installDirectory).Any())
            await Task.Delay(300);

        foreach (var process in FindTaikeronProcesses(installDirectory).ToList())
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch { }
        }

        if (FindTaikeronProcesses(installDirectory).Any())
            throw new InvalidOperationException("Taikeron Lab utilise encore son dossier d’installation. Nettoyage refusé.");
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

            var isTaikeronLab = false;
            try
            {
                isTaikeronLab = string.Equals(
                    process.ProcessName,
                    "Taikeron Lab",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            if (isTaikeronLab)
                yield return process;
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
                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, true);
                if (!Directory.Exists(directory))
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }
            Thread.Sleep(350);
        }
        throw new IOException("Impossible de supprimer complètement l’ancien dossier TL.", lastError);
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        catch { }
    }

    private static void MoveOrCopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source));
        var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destination));

        if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, destination);
            return;
        }

        CopyDirectory(source, destination);
        Directory.Delete(source, true);
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

    private static bool IsPathInside(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameVersion(string? left, string? right) =>
        string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string? value)
    {
        var clean = (value ?? string.Empty).Trim().Replace('-', '.');
        return Version.TryParse(clean, out var version) ? version.ToString() : clean;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream)).ToLowerInvariant();
    }

    private static string NormalizeSha(string value)
    {
        var normalized = value.Trim();
        var colon = normalized.IndexOf(':');
        if (colon >= 0)
            normalized = normalized[(colon + 1)..];
        return normalized.Trim().ToLowerInvariant();
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temp, path, true);
    }

    private static void WriteStatus(string path, string phase, string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            phase,
            message,
            updatedAtUtc = DateTimeOffset.UtcNow
        }, JsonOptions));
    }
}

internal sealed class ReplaceRequest
{
    public string Format { get; set; } = string.Empty;
    public string InstallerPath { get; set; } = string.Empty;
    public string InstallerSha256 { get; set; } = string.Empty;
    public long InstallerBytes { get; set; }
    public string InstallDirectory { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string JobDirectory { get; set; } = string.Empty;
    public string StatusFile { get; set; } = string.Empty;
    public string ResultFile { get; set; } = string.Empty;
}

internal sealed class ReplaceResult
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
    public string? Error { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
