using System.Windows;
using Microsoft.Win32;
using Taikeron.Launcher.Models;
using Taikeron.Launcher.Services;

namespace Taikeron.Launcher;

public partial class StorageSettingsWindow : Window
{
    private readonly LauncherSettingsService _settingsService;
    private readonly VaultBackupService _backupService;

    public StorageSettingsWindow(LauncherSettingsService settingsService, VaultBackupService backupService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _backupService = backupService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current;
        AppsRootBox.Text = settings.AppsRoot;
        DataRootBox.Text = settings.DataRoot;
        VaultRootBox.Text = settings.VaultRoot;
        MapsRootBox.Text = settings.MapsRoot;
        DownloadsRootBox.Text = settings.DownloadsRoot;
        BackupRootBox.Text = settings.BackupRoot;
        AutomaticBackupCheck.IsChecked = settings.AutomaticBackupEnabled;
        BackupIntervalBox.Text = settings.BackupIntervalHours.ToString();
        RetentionBox.Text = settings.BackupRetentionCount.ToString();
        VerifyHashesCheck.IsChecked = settings.VerifyBackupHashes;
        RefreshBackupStatus();
    }

    private void RefreshBackupStatus()
    {
        var settings = BuildSettings(showErrors: false) ?? _settingsService.Current;
        var state = _backupService.GetTargetState(settings);
        BackupTargetStateText.Text = state.Available ? $"✓ {state.Message}" : state.Message;
        LastBackupText.Text = settings.LastBackupUtc is null
            ? $"Dernière sauvegarde : aucune — {settings.LastBackupStatus}"
            : $"Dernière sauvegarde : {settings.LastBackupUtc.Value.LocalDateTime:dd/MM/yyyy HH:mm} — {settings.LastBackupStatus}";
    }

    private void BrowseApps_Click(object sender, RoutedEventArgs e) => BrowseInto(AppsRootBox, "Dossier des applications Taikeron");
    private void BrowseData_Click(object sender, RoutedEventArgs e) => BrowseInto(DataRootBox, "Dossier Data Taikeron");
    private void BrowseVault_Click(object sender, RoutedEventArgs e) => BrowseInto(VaultRootBox, "Dossier Vault Taikeron");
    private void BrowseMaps_Click(object sender, RoutedEventArgs e) => BrowseInto(MapsRootBox, "Dossier des cartes Taikeron");
    private void BrowseDownloads_Click(object sender, RoutedEventArgs e) => BrowseInto(DownloadsRootBox, "Dossier des téléchargements temporaires");
    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        BrowseInto(BackupRootBox, "Disque ou dossier de sauvegarde du Data Vault");
        RefreshBackupStatus();
    }

    private static void BrowseInto(System.Windows.Controls.TextBox textBox, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(textBox.Text) ? textBox.Text : null
        };

        if (dialog.ShowDialog() == true)
            textBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings(showErrors: true);
        if (settings is null)
            return;

        _settingsService.Save(settings);
        _settingsService.EnsureConfiguredDirectories();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildSettings(showErrors: true);
        if (settings is null)
            return;

        _settingsService.Save(settings);
        _settingsService.EnsureConfiguredDirectories();

        BackupNowButton.IsEnabled = false;
        BackupProgressBar.Visibility = Visibility.Visible;
        BackupProgressBar.Value = 0;
        BackupProgressText.Text = "Préparation de la sauvegarde…";

        try
        {
            var progress = new Progress<BackupProgress>(value =>
            {
                BackupProgressBar.Value = value.Fraction * 100;
                BackupProgressText.Text = $"{value.FilesDone}/{value.FilesTotal} — {value.CurrentFile}";
            });

            var result = await _backupService.BackupAsync(settings, progress);
            settings.LastBackupStatus = result.Message;
            _settingsService.Save(settings);
            RefreshBackupStatus();

            if (result.Success)
            {
                BackupProgressBar.Value = 100;
                BackupProgressText.Text = result.SnapshotPath ?? result.Message;
                MessageBox.Show(
                    $"Sauvegarde terminée et vérifiée.\n\n{result.FileCount} fichiers\n{FormatBytes(result.TotalBytes)}\n\n{result.SnapshotPath}",
                    "Data + Vault sauvegardés",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                BackupProgressBar.Value = 0;
                BackupProgressText.Text = result.Message;
                MessageBox.Show(result.Message, "Sauvegarde en attente", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            settings.LastBackupStatus = $"Sauvegarde échouée — {ex.Message}";
            _settingsService.Save(settings);
            BackupProgressText.Text = settings.LastBackupStatus;
            MessageBox.Show(ex.Message, "Échec de la sauvegarde", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
            RefreshBackupStatus();
        }
    }

    private LauncherSettings? BuildSettings(bool showErrors)
    {
        try
        {
            if (!int.TryParse(BackupIntervalBox.Text, out var interval) || interval < 1)
                throw new InvalidOperationException("La fréquence de sauvegarde doit être un nombre d’heures supérieur ou égal à 1.");

            if (!int.TryParse(RetentionBox.Text, out var retention) || retention < 1)
                throw new InvalidOperationException("Le nombre de sauvegardes à conserver doit être supérieur ou égal à 1.");

            var apps = LauncherSettingsService.NormalizePath(AppsRootBox.Text);
            var data = LauncherSettingsService.NormalizePath(DataRootBox.Text);
            var vault = LauncherSettingsService.NormalizePath(VaultRootBox.Text);
            var maps = LauncherSettingsService.NormalizePath(MapsRootBox.Text);
            var downloads = LauncherSettingsService.NormalizePath(DownloadsRootBox.Text);
            var backup = string.IsNullOrWhiteSpace(BackupRootBox.Text)
                ? string.Empty
                : LauncherSettingsService.NormalizePath(BackupRootBox.Text);

            if (string.IsNullOrWhiteSpace(apps) || string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(vault) || string.IsNullOrWhiteSpace(maps) || string.IsNullOrWhiteSpace(downloads))
                throw new InvalidOperationException("Les emplacements Applications, Data, Vault, Cartes et Téléchargements sont obligatoires.");

            if (string.Equals(data, vault, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Data et Vault doivent être deux dossiers distincts.");

            var storageRoot = LauncherSettingsService.GetCommonStorageRoot(data, vault);

            if (AutomaticBackupCheck.IsChecked == true && string.IsNullOrWhiteSpace(backup))
                throw new InvalidOperationException("Choisis une destination avant d’activer la sauvegarde automatique.");

            var current = _settingsService.Current;
            return new LauncherSettings
            {
                AppsRoot = apps,
                DataRoot = data,
                VaultRoot = vault,
                DataVaultRoot = storageRoot,
                MapsRoot = maps,
                DownloadsRoot = downloads,
                BackupRoot = backup,
                InitialSetupCompleted = current.InitialSetupCompleted,
                AutomaticBackupEnabled = AutomaticBackupCheck.IsChecked == true,
                BackupIntervalHours = interval,
                BackupRetentionCount = retention,
                VerifyBackupHashes = VerifyHashesCheck.IsChecked == true,
                LastBackupUtc = current.LastBackupUtc,
                LastBackupAttemptUtc = current.LastBackupAttemptUtc,
                LastBackupStatus = current.LastBackupStatus
            };
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show(ex.Message, "Paramètres invalides", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        var size = (double)Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }
}
