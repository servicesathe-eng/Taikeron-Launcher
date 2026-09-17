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
        VaultRootBox.Text = settings.DataVaultRoot;
        MapsRootBox.Text = settings.MapsRoot;
    }

    private void BrowseApps_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(AppsRootBox, "Dossier des applications Taikeron");

    private void BrowseVault_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(VaultRootBox, "Dossier des données personnelles Taikeron");

    private void BrowseMaps_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(MapsRootBox, "Dossier des cartes Taikeron");

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
            var vault = LauncherSettingsService.NormalizePath(VaultRootBox.Text);
            var maps = LauncherSettingsService.NormalizePath(MapsRootBox.Text);

            if (string.IsNullOrWhiteSpace(apps)
                || string.IsNullOrWhiteSpace(vault)
                || string.IsNullOrWhiteSpace(maps))
            {
                throw new InvalidOperationException(
                    "Les emplacements Applications, Données personnelles et Cartes sont obligatoires.");
            }

            var tlCodeDirectory = Path.Combine(apps, "Lab");
            if (PathsOverlap(vault, tlCodeDirectory))
            {
                throw new InvalidOperationException(
                    "Le dossier des données personnelles doit être séparé du dossier code de Taikeron Lab.");
            }

            if (PathsOverlap(maps, tlCodeDirectory))
            {
                throw new InvalidOperationException(
                    "Le dossier des cartes doit être séparé du dossier code de Taikeron Lab.");
            }

            var current = _settingsService.Current;
            var settings = new LauncherSettings
            {
                AppsRoot = apps,
                DataVaultRoot = vault,
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
