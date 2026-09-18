using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class VaultBackupService
{
    public bool IsBackupDue(LauncherSettings settings)
    {
        if (!settings.AutomaticBackupEnabled || string.IsNullOrWhiteSpace(settings.BackupRoot))
            return false;

        if (settings.LastBackupUtc is null)
            return true;

        return DateTimeOffset.UtcNow - settings.LastBackupUtc.Value >= TimeSpan.FromHours(settings.BackupIntervalHours);
    }

    public BackupTargetState GetTargetState(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BackupRoot))
            return new BackupTargetState(false, "Aucun disque de sauvegarde configuré");

        try
        {
            var root = Path.GetPathRoot(settings.BackupRoot);
            if (string.IsNullOrWhiteSpace(root))
                return new BackupTargetState(false, "Chemin de sauvegarde invalide");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new BackupTargetState(false, $"Disque absent : {root}");

            return new BackupTargetState(true, $"Disponible : {root}");
        }
        catch (Exception ex)
        {
            return new BackupTargetState(false, ex.Message);
        }
    }

    public async Task<BackupResult> BackupAsync(
        LauncherSettings settings,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        settings.LastBackupAttemptUtc = DateTimeOffset.UtcNow;

        if (!Directory.Exists(settings.DataRoot) && !Directory.Exists(settings.VaultRoot))
            return BackupResult.Fail("Data et Vault sont introuvables.");

        var targetState = GetTargetState(settings);
        if (!targetState.Available)
            return BackupResult.Fail($"Sauvegarde en attente — {targetState.Message}");

        var dataRoot = Path.GetFullPath(settings.DataRoot).TrimEnd(Path.DirectorySeparatorChar);
        var vaultRoot = Path.GetFullPath(settings.VaultRoot).TrimEnd(Path.DirectorySeparatorChar);
        var backupRoot = Path.GetFullPath(settings.BackupRoot).TrimEnd(Path.DirectorySeparatorChar);

        if (Overlaps(backupRoot, dataRoot) || Overlaps(backupRoot, vaultRoot))
            return BackupResult.Fail("Le dossier de sauvegarde doit être distinct de Data et Vault.");

        var snapshotsRoot = Path.Combine(backupRoot, "TaikeronPersistent");
        Directory.CreateDirectory(snapshotsRoot);

        var snapshotName = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var snapshotPath = Path.Combine(snapshotsRoot, snapshotName);
        Directory.CreateDirectory(snapshotPath);

        try
        {
            var sources = new List<(string Label, string Root)>();
            if (Directory.Exists(dataRoot))
                sources.Add(("Data", dataRoot));
            if (Directory.Exists(vaultRoot))
                sources.Add(("Vault", vaultRoot));

            var files = sources
                .SelectMany(source => Directory.EnumerateFiles(source.Root, "*", SearchOption.AllDirectories)
                    .Select(path => (source.Label, source.Root, Path: path)))
                .ToList();

            var totalBytes = files.Sum(item => new FileInfo(item.Path).Length);
            long copiedBytes = 0;
            var manifestEntries = new List<BackupManifestEntry>(files.Count);

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = files[index];
                var relativeWithinRoot = Path.GetRelativePath(item.Root, item.Path);
                var relative = Path.Combine(item.Label, relativeWithinRoot);
                var destination = Path.Combine(snapshotPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                File.Copy(item.Path, destination, overwrite: true);

                var sourceInfo = new FileInfo(item.Path);
                var destinationInfo = new FileInfo(destination);
                if (sourceInfo.Length != destinationInfo.Length)
                    throw new IOException($"Taille différente après copie : {relative}");

                string? sha256 = null;
                if (settings.VerifyBackupHashes)
                {
                    var sourceHash = await ComputeSha256Async(item.Path, cancellationToken);
                    var destinationHash = await ComputeSha256Async(destination, cancellationToken);
                    if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Vérification SHA-256 échouée : {relative}");
                    sha256 = sourceHash;
                }

                copiedBytes += sourceInfo.Length;
                manifestEntries.Add(new BackupManifestEntry(
                    relative.Replace('\\', '/'),
                    sourceInfo.Length,
                    sha256));
                progress?.Report(new BackupProgress(
                    index + 1,
                    files.Count,
                    copiedBytes,
                    totalBytes,
                    relative));
            }

            var manifest = new BackupManifest(
                DateTimeOffset.UtcNow,
                dataRoot,
                vaultRoot,
                files.Count,
                totalBytes,
                settings.VerifyBackupHashes,
                manifestEntries);

            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(snapshotPath, "_backup-manifest.json"), manifestJson, cancellationToken);

            ApplyRetention(snapshotsRoot, settings.BackupRetentionCount, snapshotPath);

            settings.LastBackupUtc = DateTimeOffset.UtcNow;
            settings.LastBackupStatus = $"Sauvegarde Data + Vault OK — {snapshotName}";
            return BackupResult.Ok(snapshotPath, files.Count, totalBytes);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(snapshotPath);
            return BackupResult.Fail($"Sauvegarde échouée — {ex.Message}");
        }
    }

    private static void ApplyRetention(string snapshotsRoot, int keepCount, string currentSnapshot)
    {
        var directories = new DirectoryInfo(snapshotsRoot)
            .EnumerateDirectories()
            .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var directory in directories.Skip(Math.Max(1, keepCount)))
        {
            if (string.Equals(directory.FullName, currentSnapshot, StringComparison.OrdinalIgnoreCase))
                continue;
            TryDeleteDirectory(directory.FullName);
        }
    }

    private static bool Overlaps(string first, string second) =>
        IsSameOrNested(first, second) || IsSameOrNested(second, first);

    private static bool IsSameOrNested(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed cleanup should not hide the original backup result.
        }
    }

    private sealed record BackupManifest(
        DateTimeOffset CreatedAtUtc,
        string SourceData,
        string SourceVault,
        int FileCount,
        long TotalBytes,
        bool Sha256Verified,
        IReadOnlyList<BackupManifestEntry> Files);

    private sealed record BackupManifestEntry(string RelativePath, long Bytes, string? Sha256);
}

public sealed record BackupTargetState(bool Available, string Message);

public sealed record BackupProgress(
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string CurrentFile)
{
    public double Fraction => BytesTotal <= 0 ? 1 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

public sealed record BackupResult(bool Success, string Message, string? SnapshotPath, int FileCount, long TotalBytes)
{
    public static BackupResult Ok(string snapshotPath, int fileCount, long totalBytes) =>
        new(true, "Sauvegarde Data + Vault terminée et vérifiée.", snapshotPath, fileCount, totalBytes);

    public static BackupResult Fail(string message) =>
        new(false, message, null, 0, 0);
}
