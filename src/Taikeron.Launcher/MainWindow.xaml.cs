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
    private readonly TlIntegrityService _integrityService = new();
    private readonly LauncherSelfUpdateService _launcherSelfUpdateService = new();
    private readonly TaikeronLabService _labService;
    private readonly TlUpdateWorkerService _updateWorkerService;
    private readonly DispatcherTimer _backupTimer;

    private TlReleaseManifest? _stableRelease;
    private LauncherReleaseInfo? _launcherRelease;
    private TlIntegrityCheckResult? _integrityResult;
    private string? _installedExecutable;
    private string? _installedVersion;
    private bool _backupInProgress;

    public MainWindow()
    {
        _labService = new TaikeronLabService(_settingsService);
        _updateWorkerService = new TlUpdateWorkerService(_settingsService);
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _backupTimer.Tick += async (_, _) => await CheckAutomaticBackupAsync();

        InitializeComponent();
        Loaded += async (_, _) =>
        {
            LauncherVersionText.Text = $"v{_launcherSelfUpdateService.CurrentVersion}";
            await CheckLauncherSelfUpdateAsync();
            _settingsService.EnsureConfiguredDirectories();
            await RefreshAsync();
            await CheckAutomaticBackupAsync();
            _backupTimer.Start();
        };
        Closed += (_, _) => _backupTimer.Stop();
    }


    private async Task CheckLauncherSelfUpdateAsync()
    {
        LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;

        try
        {
            _launcherRelease = await _launcherSelfUpdateService.GetLatestReleaseAsync();
            if (_launcherSelfUpdateService.IsUpdateAvailable(_launcherRelease))
            {
                LauncherSelfUpdateButton.Content = $"↑  Launcher {_launcherRelease!.Version}";
                LauncherSelfUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // Une panne réseau du canal Launcher ne doit pas empêcher TL de fonctionner.
            _launcherRelease = null;
            LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void LauncherSelfUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherRelease is null || !_launcherSelfUpdateService.IsUpdateAvailable(_launcherRelease))
            return;

        var answer = MessageBox.Show(
            $"Taikeron Launcher {_launcherRelease.Version} est disponible.\n\n" +
            "Le Launcher va télécharger la release officielle, vérifier son SHA-256, se fermer, remplacer son ancien code puis redémarrer.",
            "Mettre à jour Taikeron Launcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            SetBusy(true, $"Préparation du Launcher {_launcherRelease.Version}…");
            LauncherSelfUpdateButton.IsEnabled = false;

            var progress = new Progress<string>(message => ActivityText.Text = message);
            await _launcherSelfUpdateService.PrepareAndLaunchUpdateAsync(_launcherRelease, progress);

            ActivityText.Text = "Mise à jour Launcher préparée. Fermeture pour remplacement…";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            LauncherSelfUpdateButton.IsEnabled = true;
            SetBusy(false);
            ActivityText.Text = $"Échec de mise à jour du Launcher : {ex.Message}";
            MessageBox.Show(
                ex.Message,
                "Échec de mise à jour du Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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
            LaunchButton.IsEnabled = false;

            _stableRelease = await _labService.GetStableReleaseAsync();
            LatestVersionText.Text = string.IsNullOrWhiteSpace(_stableRelease?.Version) ? "—" : _stableRelease.Version;
            VersionCardValue.Text = LatestVersionText.Text;
            DownloadSizeText.Text = _stableRelease is null ? "Taille : —" : $"Taille : {FormatBytes(_stableRelease.Bytes)}";
            VersionDateText.Text = _stableRelease?.PublishedAt is null
                ? "Publication : —"
                : $"Publication : {_stableRelease.PublishedAt.Value.LocalDateTime:dd/MM/yyyy HH:mm}";

            _integrityResult = await CheckIntegrityAsync();
            ApplyTruthStatus();
        }
        catch (Exception ex)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Vérification incomplète";
            LatestVersionText.Text = "Indisponible";
            UpdateBadge.Visibility = Visibility.Collapsed;
            LaunchButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            ShaStatusText.Text = "Référence officielle indisponible";
            ActivityText.Text = $"Impossible d’établir l’état officiel de TL : {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<TlIntegrityCheckResult> CheckIntegrityAsync()
    {
        if (_installedExecutable is null)
        {
            return new TlIntegrityCheckResult(
                TlIntegrityState.NotInstalled,
                "Taikeron Lab n’est pas installé.",
                0,
                0,
                0,
                []);
        }

        try
        {
            ActivityText.Text = "Lecture de la référence d’intégrité officielle…";
            var manifest = await _integrityService.GetManifestForInstalledVersionAsync(_installedVersion, _stableRelease);
            var progress = new Progress<double>(value =>
            {
                ActivityText.Text = $"Vérification locale du code TL… {value:P0}";
            });

            return await _integrityService.VerifyAsync(
                _installedExecutable,
                _installedVersion,
                _stableRelease,
                manifest,
                progress);
        }
        catch (Exception ex)
        {
            return new TlIntegrityCheckResult(
                TlIntegrityState.ReferenceUnavailable,
                $"Référence d’intégrité indisponible : {ex.Message}",
                0,
                0,
                0,
                []);
        }
    }

    private void ApplyTruthStatus()
    {
        var updateAvailable = _labService.IsUpdateAvailable(_installedVersion, _stableRelease?.Version);
        UpdateButton.IsEnabled = _stableRelease is not null;
        UpdateBadge.Visibility = updateAvailable ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadgeText.Text = updateAvailable ? "Mise à jour disponible" : "";

        if (_installedExecutable is null || _integrityResult?.State == TlIntegrityState.NotInstalled)
        {
            StatusDot.Fill = Brushes.DarkOrange;
            StatusText.Foreground = Brushes.DarkOrange;
            StatusText.Text = "Non installé";
            LaunchButton.IsEnabled = false;
            UpdateButton.Content = "↓  Installer TL";
            ShaStatusText.Text = "Intégrité : non applicable";
            ActivityText.Text = $"TL pourra être installé dans {_labService.CanonicalInstallDirectory}.";
            return;
        }

        if (_integrityResult?.State == TlIntegrityState.Corrupted)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Installation altérée";
            UpdateBadge.Visibility = Visibility.Visible;
            UpdateBadgeText.Text = "Réparation requise";
            LaunchButton.IsEnabled = false;
            UpdateButton.Content = "↻  Supprimer + réinstaller TL";
            ShaStatusText.Text = "Intégrité : ÉCHEC";
            var detail = _integrityResult.Problems.FirstOrDefault();
            ActivityText.Text = string.IsNullOrWhiteSpace(detail)
                ? _integrityResult.Message
                : $"{_integrityResult.Message} {detail}";
            return;
        }

        if (_integrityResult?.State == TlIntegrityState.Healthy)
        {
            StatusDot.Fill = (Brush)FindResource("Green");
            StatusText.Foreground = (Brush)FindResource("Green");
            StatusText.Text = updateAvailable ? "Installé · intact" : "Installé · intact · à jour";
            LaunchButton.IsEnabled = true;
            UpdateButton.Content = updateAvailable ? "↻  Réinstaller la dernière version" : "↻  Réinstaller proprement";
            ShaStatusText.Text = $"Intégrité vérifiée · {_integrityResult.CheckedFiles} SHA-256";
            ActivityText.Text = updateAvailable
                ? $"{_integrityResult.Message} Une version plus récente est disponible."
                : _integrityResult.Message;
            return;
        }

        StatusDot.Fill = Brushes.DarkOrange;
        StatusText.Foreground = Brushes.DarkOrange;
        StatusText.Text = updateAvailable ? "Installé · non certifié" : "Installé · référence incomplète";
        LaunchButton.IsEnabled = true;
        UpdateButton.Content = updateAvailable ? "↻  Réinstaller la dernière version" : "↻  Réinstaller proprement";
        ShaStatusText.Text = "Intégrité : référence indisponible";
        ActivityText.Text = _integrityResult?.Message ?? "Impossible de certifier les fichiers locaux.";
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_settingsService.Current.InitialSetupCompleted)
            {
                var setup = new FirstInstallWindow(_settingsService)
                {
                    Owner = this
                };

                if (setup.ShowDialog() != true)
                {
                    ActivityText.Text = "Configuration du stockage annulée avant lancement.";
                    return;
                }

                _settingsService.EnsureConfiguredDirectories();
            }

            if (_installedExecutable is null)
            {
                MessageBox.Show("Taikeron Lab n’est pas installé ou n’a pas été détecté.", "Taikeron Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_integrityResult?.BlocksLaunch == true)
            {
                MessageBox.Show(
                    "Le Launcher a détecté que les fichiers de Taikeron Lab ne correspondent pas à la référence officielle. Répare TL avant de le lancer.",
                    "Intégrité TL invalide",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
        if (_installedExecutable is null && !_settingsService.Current.InitialSetupCompleted)
        {
            var setup = new FirstInstallWindow(_settingsService)
            {
                Owner = this
            };

            if (setup.ShowDialog() != true)
            {
                ActivityText.Text = "Installation TL annulée avant choix des emplacements.";
                return;
            }

            _settingsService.EnsureConfiguredDirectories();
            InstallPathText.Text = $"TL sera installé dans {_labService.CanonicalInstallDirectory}.";
        }

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
            var downloadProgress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100;
                ActivityText.Text = $"Téléchargement et vérification du paquet officiel… {value:P0}";
            });

            var packagePath = await _labService.DownloadAndVerifyAsync(_stableRelease, downloadProgress);
            DownloadProgress.Value = 100;
            ActivityText.Text = "Paquet vérifié. Suppression complète de l’ancienne installation avant réinstallation…";

            var workerProgress = new Progress<TlWorkerStatus>(status =>
            {
                ActivityText.Text = status.Message;
            });

            var result = await _updateWorkerService.ReplaceAsync(
                packagePath,
                _stableRelease,
                _installedExecutable,
                _installedVersion,
                workerProgress);

            if (!result.Ok)
            {
                var recovery = string.IsNullOrWhiteSpace(result.PreservedMapsPath)
                    ? string.Empty
                    : $"\n\nCartes protégées dans :\n{result.PreservedMapsPath}";
                throw new InvalidOperationException((result.Error ?? "Le remplacement TL a échoué.") + recovery);
            }

            if (!result.OldCodeRemoved)
                throw new InvalidOperationException("Le worker n’a pas confirmé la suppression de l’ancien code TL.");

            _installedExecutable = result.ExecutablePath;
            ActivityText.Text = "Ancien runtime supprimé. Nouveau runtime installé. Contrôle d’intégrité final…";
            await RefreshAsync();

            if (_integrityResult?.BlocksLaunch == true)
                throw new InvalidOperationException("La réinstallation est terminée, mais le contrôle d’intégrité final a échoué. TL ne sera pas lancé.");

            if (File.Exists(result.ExecutablePath))
                _labService.Launch(result.ExecutablePath);

            var verification = _integrityResult?.State == TlIntegrityState.Healthy
                ? "Le nouveau runtime correspond au manifeste d’intégrité officiel."
                : "Le paquet officiel a été vérifié et installé proprement ; cette version ne dispose pas encore d’un manifeste d’intégrité fichier par fichier.";

            MessageBox.Show(
                $"Taikeron Lab {_stableRelease.Version} a été installé proprement.\n\n" +
                "L’ancien dossier code a été supprimé avant l’installation du nouveau runtime.\n" +
                verification,
                "Mise à jour TL terminée",
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
        var truth = _integrityResult?.Message ?? "État d’intégrité inconnu.";
        var message = _installedExecutable is null
            ? $"TL n’est pas détecté.\n\nDossier code prévu :\n{_labService.CanonicalInstallDirectory}\n\nData Vault :\n{settings.DataVaultRoot}\n\n{truth}"
            : $"Installation détectée :\n{_installedExecutable}\n\nVersion : {_installedVersion ?? "inconnue"}\n\nData Vault :\n{settings.DataVaultRoot}\n\nVérité Launcher :\n{truth}\n\nLe Data Vault n’est jamais inclus dans le contrôle d’intégrité du code.";

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
            var result = await Task.Run(async () => await _backupService.BackupAsync(settings));
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
        if (busy)
        {
            UpdateButton.IsEnabled = false;
            LaunchButton.IsEnabled = false;
        }
        else
        {
            UpdateButton.IsEnabled = _stableRelease is not null;
            LaunchButton.IsEnabled = _installedExecutable is not null && _integrityResult?.BlocksLaunch != true;
        }

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
