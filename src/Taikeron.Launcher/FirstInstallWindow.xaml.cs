using System.Windows;
using Microsoft.Win32;
using Taikeron.Launcher.Models;
using Taikeron.Launcher.Services;

namespace Taikeron.Launcher;

public partial class FirstInstallWindow : Window
{
    private readonly LauncherSettingsService _settingsService;

    public FirstInstallWindow(LauncherSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        var settings = settingsService.Current;
        AppsRootBox.Text = settings.AppsRoot;
        DataRootBox.Text = settings.DataRoot;
        VaultRootBox.Text = settings.VaultRoot;
    }

    private void BrowseApps_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(AppsRootBox, "Dossier des applications Taikeron");

    private void BrowseData_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(DataRootBox, "Dossier Data Taikeron");

    private void BrowseVault_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(VaultRootBox, "Dossier Vault Taikeron");

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

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apps = LauncherSettingsService.NormalizePath(AppsRootBox.Text);
            var data = LauncherSettingsService.NormalizePath(DataRootBox.Text);
            var vault = LauncherSettingsService.NormalizePath(VaultRootBox.Text);
            if (string.IsNullOrWhiteSpace(apps)
                || string.IsNullOrWhiteSpace(data)
                || string.IsNullOrWhiteSpace(vault))
            {
                throw new InvalidOperationException(
                    "Les emplacements Applications, Data et Vault sont obligatoires.");
            }

            var storageRoot = LauncherSettingsService.GetCommonStorageRoot(data, vault);
            var maps = Path.Combine(storageRoot, "Maps");

            var tlCodeDirectory = Path.Combine(apps, "Lab");
            if (PathsOverlap(data, tlCodeDirectory) || PathsOverlap(vault, tlCodeDirectory))
            {
                throw new InvalidOperationException(
                    "Data et Vault doivent être séparés du dossier code de Taikeron Lab.");
            }

            if (PathsOverlap(data, vault))
                throw new InvalidOperationException("Data et Vault doivent être deux dossiers distincts.");

            var current = _settingsService.Current;
            var settings = new LauncherSettings
            {
                AppsRoot = apps,
                DataRoot = data,
                VaultRoot = vault,
                DataVaultRoot = storageRoot,
                MapsRoot = maps,
                DownloadsRoot = current.DownloadsRoot,
                BackupRoot = current.BackupRoot,
                InitialSetupCompleted = true,
                AutomaticBackupEnabled = current.AutomaticBackupEnabled,
                BackupIntervalHours = current.BackupIntervalHours,
                BackupRetentionCount = current.BackupRetentionCount,
                VerifyBackupHashes = current.VerifyBackupHashes,
                LastBackupUtc = current.LastBackupUtc,
                LastBackupAttemptUtc = current.LastBackupAttemptUtc,
                LastBackupStatus = current.LastBackupStatus
            };

            _settingsService.Save(settings);
            _settingsService.EnsureConfiguredDirectories();

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Emplacements invalides",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool PathsOverlap(string first, string second)
    {
        var a = NormalizeDirectory(first);
        var b = NormalizeDirectory(second);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || IsInside(a, b)
            || IsInside(b, a);
    }

    private static bool IsInside(string candidate, string parent)
    {
        var candidateRoot = NormalizeDirectory(candidate) + Path.DirectorySeparatorChar;
        var parentRoot = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        return candidateRoot.StartsWith(parentRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
