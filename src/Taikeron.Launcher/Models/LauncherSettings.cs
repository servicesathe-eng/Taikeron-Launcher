namespace Taikeron.Launcher.Models;

public sealed class LauncherSettings
{
    public string AppsRoot { get; set; } = string.Empty;
    public string DataVaultRoot { get; set; } = string.Empty;
    public string MapsRoot { get; set; } = string.Empty;
    public string DownloadsRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;

    public bool AutomaticBackupEnabled { get; set; }
    public int BackupIntervalHours { get; set; } = 24;
    public int BackupRetentionCount { get; set; } = 7;
    public bool VerifyBackupHashes { get; set; } = true;

    public DateTimeOffset? LastBackupUtc { get; set; }
    public DateTimeOffset? LastBackupAttemptUtc { get; set; }
    public string LastBackupStatus { get; set; } = "Aucune sauvegarde effectuée";

    public static LauncherSettings CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var taikeron = Path.Combine(local, "Taikeron");
        var vault = Path.Combine(taikeron, "DataVault");

        return new LauncherSettings
        {
            AppsRoot = Path.Combine(taikeron, "Apps"),
            DataVaultRoot = vault,
            MapsRoot = Path.Combine(vault, "Maps"),
            DownloadsRoot = Path.Combine(taikeron, "Launcher", "Downloads"),
            BackupRoot = string.Empty,
            AutomaticBackupEnabled = false,
            BackupIntervalHours = 24,
            BackupRetentionCount = 7,
            VerifyBackupHashes = true
        };
    }
}
