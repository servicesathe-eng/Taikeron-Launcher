using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Taikeron.Launcher.Models;
using Taikeron.Launcher.Services;

namespace Taikeron.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherSettingsService _settingsService = new();
    private readonly VaultBackupService _backupService = new();
    private readonly TaikeronLabService _labService;
    private readonly DispatcherTimer _backupTimer;

    private TlReleaseManifest? _stableRelease;
    private string? _installedExecutable;
    private string? _installedVersion;
    private bool _backupInProgress;

    public MainWindow()
    {
        _labService = new TaikeronLabService(_settingsService);
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _backupTimer.Tick += async (_, _) => await CheckAutomaticBackupAsync();

        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _settingsService.EnsureConfiguredDirectories();
            await RefreshAsync();
            await CheckAutomaticBackupAsync();
            _backupTimer.Start();
        };
        Closed += (_, _) => _backupTimer.Stop();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true, "Vérification de Taikeron Lab…");

        try
        {
            _installedExecutable = _labService.FindInstalledExecutable();
            _installedVersion = _labService.GetInstalledVersion(_installedExecutable);

            InstallPathText.Text = _installedExecutable is null
                ? $"TL non détecté. Emplacement canonique prévu : {_labService.CanonicalInstallDirectory}"
                : _installedExecutable;

            InstalledVersionText.Text = _installedVersion ?? "Non installé";
            LaunchButton.IsEnabled = _installedExecutable is not null;

            _stableRelease = await _labService.GetStableReleaseAsync();
            LatestVersionText.Text = string.IsNullOrWhiteSpace(_stableRelease?.Version) ? "—" : _stableRelease.Version;
            VersionCardValue.Text = LatestVersionText.Text;
            DownloadSizeText.Text = _stableRelease is null ? "Taille : —" : $"Taille : {FormatBytes(_stableRelease.Bytes)}";
            VersionDateText.Text = _stableRelease?.PublishedAt is null
                ? "Publication : —"
                : $"Publication : {_stableRelease.PublishedAt.Value.LocalDateTime:dd/MM/yyyy HH:mm}";

            ShaStatusText.Text = string.IsNullOrWhiteSpace(_stableRelease?.Sha256)
                ? "SHA-256 absent"
                : "SHA-256 disponible";

            var updateAvailable = _labService.IsUpdateAvailable(_installedVersion, _stableRelease?.Version);
            UpdateBadge.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
            UpdateButton.IsEnabled = _stableRelease is not null;
            UpdateButton.Content = _installedExecutable is null ? "↓  Télécharger TL" : "↓  Mettre à jour";

            if (_installedExecutable is null)
            {
                StatusDot.Fill = Brushes.DarkOrange;
                StatusText.Foreground = Brushes.DarkOrange;
                StatusText.Text = "Non installé";
                ActivityText.Text = $"TL pourra être installé dans {_labService.CanonicalInstallDirectory}.";
            }
            else if (updateAvailable)
            {
                StatusDot.Fill = (Brush)FindResource("Green");
                StatusText.Foreground = (Brush)FindResource("Green");
                StatusText.Text = "Installé";
                UpdateBadgeText.Text = "Mise à jour disponible";
                ActivityText.Text = "Une nouvelle version stable de TL est disponible.";
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("Green");
                StatusText.Foreground = (Brush)FindResource("Green");
                StatusText.Text = "Installé · à jour";
                ActivityText.Text = "Taikeron Lab est à jour.";
            }
        }
        catch (Exception ex)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Vérification incomplète";
            LatestVersionText.Text = "Indisponible";
            UpdateBadge.Visibility = Visibility.Collapsed;
            ActivityText.Text = $"Impossible de lire le manifeste distant : {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_installedExecutable is null)
            {
                MessageBox.Show("Taikeron Lab n’est pas installé ou n’a pas été détecté.", "Taikeron Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _labService.Launch(_installedExecutable);
            ActivityText.Text = "Taikeron Lab lancé.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Impossible de lancer TL", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stableRelease is null)
        {
            MessageBox.Show("Aucune release stable n’est chargée.", "Taikeron Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        SetBusy(true, $"Téléchargement de TL {_stableRelease.Version}…");

        try
        {
            var progress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100;
                ActivityText.Text = $"Téléchargement et vérification… {value:P0}";
            });

            var packagePath = await _labService.DownloadAndVerifyAsync(_stableRelease, progress);
            DownloadProgress.Value = 100;
            ActivityText.Text = $"Paquet vérifié : {packagePath}";

            MessageBox.Show(
                "Le paquet TL a été téléchargé et vérifié (taille + SHA-256).\n\nLa prochaine étape ajoutera le worker externe qui fermera TL, supprimera réellement l’ancien code puis installera ce paquet de façon atomique.",
                "Paquet TL prêt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DownloadProgress.Value = 0;
            ActivityText.Text = $"Échec : {ex.Message}";
            MessageBox.Show(ex.Message, "Échec de la mise à jour", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();

        var settings = _settingsService.Current;
        var message = _installedExecutable is null
            ? $"TL n’est pas détecté.\n\nDossier code prévu :\n{_labService.CanonicalInstallDirectory}\n\nData Vault :\n{settings.DataVaultRoot}\n\nLa réparation complète sera activée avec le worker d’installation atomique."
            : $"Installation détectée :\n{_installedExecutable}\n\nVersion : {_installedVersion ?? "inconnue"}\n\nData Vault :\n{settings.DataVaultRoot}\n\nAucun fichier utilisateur n’a été modifié.";

        MessageBox.Show(message, "Diagnostic TL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new StorageSettingsWindow(_settingsService, _backupService)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _settingsService.EnsureConfiguredDirectories();
            await RefreshAsync();
            await CheckAutomaticBackupAsync();
        }
    }

    private async Task CheckAutomaticBackupAsync()
    {
        if (_backupInProgress)
            return;

        var settings = _settingsService.Current;
        if (!_backupService.IsBackupDue(settings))
            return;

        var state = _backupService.GetTargetState(settings);
        if (!state.Available)
        {
            settings.LastBackupAttemptUtc = DateTimeOffset.UtcNow;
            settings.LastBackupStatus = $"Sauvegarde en attente — {state.Message}";
            _settingsService.Save(settings);
            ActivityText.Text = settings.LastBackupStatus;
            return;
        }

        _backupInProgress = true;
        ActivityText.Text = "Sauvegarde automatique du Data Vault en cours…";

        try
        {
            var result = await Task.Run(() => _backupService.BackupAsync(settings)).Unwrap();
            settings.LastBackupStatus = result.Message;
            _settingsService.Save(settings);
            ActivityText.Text = result.Success
                ? $"Data Vault sauvegardé : {result.SnapshotPath}"
                : result.Message;
        }
        catch (Exception ex)
        {
            settings.LastBackupStatus = $"Sauvegarde automatique échouée — {ex.Message}";
            _settingsService.Save(settings);
            ActivityText.Text = settings.LastBackupStatus;
        }
        finally
        {
            _backupInProgress = false;
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        UpdateButton.IsEnabled = !busy && _stableRelease is not null;
        LaunchButton.IsEnabled = !busy && _installedExecutable is not null;

        if (!string.IsNullOrWhiteSpace(text))
            ActivityText.Text = text;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "—";

        string[] units = ["o", "Ko", "Mo", "Go"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}
