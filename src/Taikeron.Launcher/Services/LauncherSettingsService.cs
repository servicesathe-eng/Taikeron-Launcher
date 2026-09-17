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
                    ApplyMissingDefaults(loaded);
                    Current = loaded;
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
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        Current = settings;
    }

    public void EnsureConfiguredDirectories()
    {
        CreateIfDriveReady(Current.AppsRoot);
        CreateIfDriveReady(Current.DataVaultRoot);
        CreateIfDriveReady(Current.MapsRoot);
        CreateIfDriveReady(Current.DownloadsRoot);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    private static void ApplyMissingDefaults(LauncherSettings settings)
    {
        var defaults = LauncherSettings.CreateDefault();
        settings.AppsRoot = string.IsNullOrWhiteSpace(settings.AppsRoot) ? defaults.AppsRoot : NormalizePath(settings.AppsRoot);
        settings.DataVaultRoot = string.IsNullOrWhiteSpace(settings.DataVaultRoot) ? defaults.DataVaultRoot : NormalizePath(settings.DataVaultRoot);
        settings.MapsRoot = string.IsNullOrWhiteSpace(settings.MapsRoot) ? DefaultMapsRoot(settings.DataVaultRoot) : NormalizePath(settings.MapsRoot);
        settings.DownloadsRoot = string.IsNullOrWhiteSpace(settings.DownloadsRoot) ? defaults.DownloadsRoot : NormalizePath(settings.DownloadsRoot);
        settings.BackupRoot = string.IsNullOrWhiteSpace(settings.BackupRoot) ? string.Empty : NormalizePath(settings.BackupRoot);
        settings.BackupIntervalHours = Math.Clamp(settings.BackupIntervalHours, 1, 24 * 30);
        settings.BackupRetentionCount = Math.Clamp(settings.BackupRetentionCount, 1, 365);
        settings.LastBackupStatus = string.IsNullOrWhiteSpace(settings.LastBackupStatus)
            ? "Aucune sauvegarde effectuée"
            : settings.LastBackupStatus;
    }

    private static string DefaultMapsRoot(string dataVaultRoot)
    {
        var vault = NormalizePath(dataVaultRoot);
        var parent = Directory.GetParent(vault)?.FullName;
        return Path.Combine(string.IsNullOrWhiteSpace(parent) ? vault : parent, "Maps");
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
