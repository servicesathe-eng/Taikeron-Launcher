using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class LauncherSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public LauncherSettings Current { get; private set; }

    public string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taikeron",
        "Launcher");

    public string SettingsPath => Path.Combine(SettingsDirectory, "launcher-settings.json");

    public LauncherSettingsService()
    {
        Current = LoadOrCreate();
    }

    public LauncherSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    var legacyVaultRoot = string.IsNullOrWhiteSpace(loaded.DataRoot)
                        && string.IsNullOrWhiteSpace(loaded.VaultRoot)
                        ? loaded.DataVaultRoot
                        : string.Empty;

                    ApplyMissingDefaults(loaded);
                    MigrateLegacyLayout(legacyVaultRoot, loaded);
                    Current = loaded;
                    PersistWithoutReapplying(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // A malformed settings file must never prevent the launcher from starting.
        }

        var settings = LauncherSettings.CreateDefault();
        Current = settings;
        Save(settings);
        return settings;
    }

    public void Save(LauncherSettings settings)
    {
        ApplyMissingDefaults(settings);
        Directory.CreateDirectory(SettingsDirectory);
        PersistWithoutReapplying(settings);
        Current = settings;
    }

    public void EnsureConfiguredDirectories()
    {
        CreateIfDriveReady(Current.AppsRoot);
        CreateIfDriveReady(Current.DataRoot);
        CreateIfDriveReady(Current.VaultRoot);
        CreateIfDriveReady(Current.MapsRoot);
        CreateIfDriveReady(Current.DownloadsRoot);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    public static string GetCommonStorageRoot(string dataRoot, string vaultRoot)
    {
        var data = NormalizePath(dataRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var vault = NormalizePath(vaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dataParent = Directory.GetParent(data)?.FullName;
        var vaultParent = Directory.GetParent(vault)?.FullName;

        if (string.IsNullOrWhiteSpace(dataParent) ||
            string.IsNullOrWhiteSpace(vaultParent) ||
            !string.Equals(dataParent, vaultParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Data et Vault doivent actuellement être deux dossiers séparés placés dans le même dossier parent.");
        }

        return dataParent;
    }

    private static void ApplyMissingDefaults(LauncherSettings settings)
    {
        var defaults = LauncherSettings.CreateDefault();

        settings.AppsRoot = string.IsNullOrWhiteSpace(settings.AppsRoot)
            ? defaults.AppsRoot
            : NormalizePath(settings.AppsRoot);

        if (string.IsNullOrWhiteSpace(settings.DataRoot) || string.IsNullOrWhiteSpace(settings.VaultRoot))
        {
            var legacy = string.IsNullOrWhiteSpace(settings.DataVaultRoot)
                ? string.Empty
                : NormalizePath(settings.DataVaultRoot);

            var parent = LegacyStorageParent(legacy) ?? Path.GetDirectoryName(defaults.DataRoot)!;
            settings.DataRoot = string.IsNullOrWhiteSpace(settings.DataRoot)
                ? Path.Combine(parent, "Data")
                : NormalizePath(settings.DataRoot);
            settings.VaultRoot = string.IsNullOrWhiteSpace(settings.VaultRoot)
                ? Path.Combine(parent, "Vault")
                : NormalizePath(settings.VaultRoot);
        }
        else
        {
            settings.DataRoot = NormalizePath(settings.DataRoot);
            settings.VaultRoot = NormalizePath(settings.VaultRoot);
        }

        settings.DataVaultRoot = GetCommonStorageRoot(settings.DataRoot, settings.VaultRoot);
        settings.MapsRoot = string.IsNullOrWhiteSpace(settings.MapsRoot)
            ? Path.Combine(settings.DataVaultRoot, "Maps")
            : NormalizePath(settings.MapsRoot);
        settings.DownloadsRoot = string.IsNullOrWhiteSpace(settings.DownloadsRoot)
            ? defaults.DownloadsRoot
            : NormalizePath(settings.DownloadsRoot);
        settings.BackupRoot = string.IsNullOrWhiteSpace(settings.BackupRoot)
            ? string.Empty
            : NormalizePath(settings.BackupRoot);
        settings.BackupIntervalHours = Math.Clamp(settings.BackupIntervalHours, 1, 24 * 30);
        settings.BackupRetentionCount = Math.Clamp(settings.BackupRetentionCount, 1, 365);
        settings.LastBackupStatus = string.IsNullOrWhiteSpace(settings.LastBackupStatus)
            ? "Aucune sauvegarde effectuée"
            : settings.LastBackupStatus;
    }

    private static string? LegacyStorageParent(string legacyRoot)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot))
            return null;

        var full = NormalizePath(legacyRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(Path.GetFileName(full), "DataVault", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(full)?.FullName;

        return full;
    }

    private static void MigrateLegacyLayout(string legacyRoot, LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot))
            return;

        var legacy = NormalizePath(legacyRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(Path.GetFileName(legacy), "DataVault", StringComparison.OrdinalIgnoreCase))
            return;

        MoveLegacyDirectory(Path.Combine(legacy, "data"), settings.DataRoot);
        MoveLegacyDirectory(Path.Combine(legacy, "vault"), settings.VaultRoot);

        try
        {
            if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy);
        }
        catch
        {
            // Migration is best effort; old data is never destroyed on failure.
        }
    }

    private static void MoveLegacyDirectory(string source, string destination)
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
                File.Move(file, target);
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }

            if (!Directory.EnumerateFileSystemEntries(source).Any())
                Directory.Delete(source);
        }
        catch
        {
            // Any collision keeps the legacy copy in place rather than overwriting data.
        }
    }

    private void PersistWithoutReapplying(LauncherSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static void CreateIfDriveReady(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return;
            }

            Directory.CreateDirectory(path);
        }
        catch
        {
            // A removable/offline drive is allowed; its absence is surfaced in the UI.
        }
    }
}
