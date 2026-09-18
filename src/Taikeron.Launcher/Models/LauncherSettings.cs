namespace Taikeron.Launcher.Models;

public sealed class LauncherSettings
{
    public string AppsRoot { get; set; } = string.Empty;

    // Canonical 0.4.4+ persistent roots.
    public string DataRoot { get; set; } = string.Empty;
    public string VaultRoot { get; set; } = string.Empty;

    // Backward-compatibility root used by TL <= 2.302.6.4. It is the common
    // parent of Data and Vault, never the canonical storage destination itself.
    public string DataVaultRoot { get; set; } = string.Empty;

    public string MapsRoot { get; set; } = string.Empty;
    public string DownloadsRoot { get; set; } = string.Empty;
    public string BackupRoot { get; set; } = string.Empty;

    public bool InitialSetupCompleted { get; set; }

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

        return new LauncherSettings
        {
            AppsRoot = Path.Combine(taikeron, "Apps"),
            DataRoot = Path.Combine(taikeron, "Data"),
            VaultRoot = Path.Combine(taikeron, "Vault"),
            DataVaultRoot = taikeron,
            MapsRoot = Path.Combine(taikeron, "Maps"),
            DownloadsRoot = Path.Combine(taikeron, "Launcher", "Downloads"),
            BackupRoot = string.Empty,
            InitialSetupCompleted = false,
            AutomaticBackupEnabled = false,
            BackupIntervalHours = 24,
            BackupRetentionCount = 7,
            VerifyBackupHashes = true
        };
    }
}
