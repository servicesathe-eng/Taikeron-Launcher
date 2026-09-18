using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
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
    private string _selectedProduct = "TL";

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
            if (await CheckLauncherSelfUpdateAsync())
                return;

            _settingsService.EnsureConfiguredDirectories();
            await RefreshAsync();
            await CheckAutomaticBackupAsync();
            _backupTimer.Start();
        };
        Closed += (_, _) => _backupTimer.Stop();
    }


    private async void ProductCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string product)
            return;

        await SelectProductAsync(product);
    }

    private async Task SelectProductAsync(string product)
    {
        _selectedProduct = product;
        ApplySidebarSelection();

        if (string.Equals(product, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTmbPanel();
            return;
        }

        ApplyTlIdentity();
        await RefreshAsync();
    }

    private void ApplySidebarSelection()
    {
        var gold = (Brush)FindResource("Gold");
        var inactiveBorder = new SolidColorBrush(Color.FromRgb(0x25, 0x3B, 0x49));
        var activeBackground = new SolidColorBrush(Color.FromRgb(0x16, 0x27, 0x1E));
        var inactiveBackground = new SolidColorBrush(Color.FromRgb(0x09, 0x19, 0x23));

        var tlSelected = string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase);
        TlProductCard.BorderBrush = tlSelected ? gold : inactiveBorder;
        TlProductCard.Background = tlSelected ? activeBackground : inactiveBackground;
        TmbProductCard.BorderBrush = tlSelected ? inactiveBorder : gold;
        TmbProductCard.Background = tlSelected ? inactiveBackground : activeBackground;
    }

    private void ApplyTlIdentity()
    {
        ProductHeroCodeText.Text = "TL";
        ProductHeroTitleText.Text = "Taikeron Lab (TL)";
        ProductHeroSubtitleText.Text = "Création, édition et analyse de parcours cyclistes.";
        ProductHeroDescriptionText.Text = "Le Launcher installe, vérifie, répare et met à jour TL.";
        ProductBullet1Text.Text = "• Distribution centralisée par le Launcher";
        ProductBullet2Text.Text = "• Vérification taille + SHA-256";
        ProductBullet3Text.Text = "• Remplacement complet du code, Data/Vault protégés";
        RepairActionButton.IsEnabled = true;
    }

    private void ApplyTmbPanel()
    {
        ProductHeroCodeText.Text = "TMB";
        ProductHeroTitleText.Text = "Taikeron Map Builder (TMB)";
        ProductHeroSubtitleText.Text = "Création et préparation de cartes Taikeron.";
        ProductHeroDescriptionText.Text = "TMB est maintenant sélectionnable depuis la colonne de gauche.";
        ProductBullet1Text.Text = "• Outil Windows de génération cartographique";
        ProductBullet2Text.Text = "• Gestion séparée des cartes Taikeron";
        ProductBullet3Text.Text = "• Intégration Launcher prévue dans une étape dédiée";

        StatusDot.Fill = (Brush)FindResource("Gold");
        StatusText.Foreground = (Brush)FindResource("GoldBright");
        StatusText.Text = "TMB sélectionné";
        UpdateBadge.Visibility = Visibility.Collapsed;
        InstalledVersionText.Text = "—";
        LatestVersionText.Text = "—";
        InstallPathText.Text = "La sélection TMB est active. Installation et mise à jour TMB restent séparées pour le moment.";
        VersionCardValue.Text = "TMB";
        DownloadSizeText.Text = "Taille : —";
        VersionDateText.Text = "Publication : —";
        LaunchButton.Content = "▶  Lancer TMB";
        LaunchButton.Style = (Style)FindResource("ActionButton");
        LaunchButton.IsEnabled = false;
        UpdateButton.Content = "↓  Mettre à jour TMB";
        UpdateButton.IsEnabled = false;
        RepairActionButton.IsEnabled = false;
        ShaStatusText.Text = "Gestion TMB non activée";
        FooterInstallStateText.Text = "◉  TMB sélectionné";
        ActivityText.Text = "TMB est sélectionné dans le Launcher.";
        DownloadProgress.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> CheckLauncherSelfUpdateAsync()
    {
        LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;

        try
        {
            _launcherRelease = await _launcherSelfUpdateService.GetLatestReleaseAsync();
            if (!_launcherSelfUpdateService.IsUpdateAvailable(_launcherRelease))
                return false;

            LauncherSelfUpdateButton.Content = $"↑  Réessayer Launcher {_launcherRelease!.Version}";
            LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;
            LauncherSelfUpdateButton.IsEnabled = false;

            try
            {
                SetBusy(true, $"Mise à jour automatique du Launcher {_launcherRelease.Version}…");
                var progress = new Progress<string>(message => ActivityText.Text = message);
                await _launcherSelfUpdateService.PrepareAndLaunchUpdateAsync(_launcherRelease, progress);

                ActivityText.Text = $"Launcher {_launcherRelease.Version} vérifié. Redémarrage automatique…";
                Application.Current.Shutdown();
                return true;
            }
            catch (Exception ex)
            {
                // Le Launcher reste utilisable si l'auto-update échoue. Le bouton
                // devient alors un secours manuel pour réessayer sans bloquer TL.
                LauncherSelfUpdateButton.Content = $"↑  Réessayer Launcher {_launcherRelease.Version}";
                LauncherSelfUpdateButton.Visibility = Visibility.Visible;
                LauncherSelfUpdateButton.IsEnabled = true;
                SetBusy(false);
                ActivityText.Text = $"Auto-update Launcher impossible : {ex.Message}";
                return false;
            }
        }
        catch
        {
            // Une panne réseau du canal Launcher ne doit pas empêcher TL de fonctionner.
            _launcherRelease = null;
            LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private async void LauncherSelfUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launcherRelease is null || !_launcherSelfUpdateService.IsUpdateAvailable(_launcherRelease))
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

    private async Task RefreshAsync(bool forceRemoteRefresh = false)
    {
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTmbPanel();
            return;
        }

        SetBusy(true, "Vérification de Taikeron Lab…");

        try
        {
            _installedExecutable = _labService.FindInstalledExecutable();
            _installedVersion = _labService.GetInstalledVersion(_installedExecutable);

            InstallPathText.Text = _installedExecutable is null
                ? $"TL non détecté. Emplacement canonique prévu : {_labService.CanonicalInstallDirectory}"
                : _installedExecutable;

            InstalledVersionText.Text = _installedVersion ?? "Non installé";

            _stableRelease = await _labService.GetStableReleaseAsync(forceRemoteRefresh);
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
            UpdateButton.IsEnabled = false;
            ShaStatusText.Text = "Référence officielle indisponible";
            FooterInstallStateText.Text = "⚠  État TL non vérifié";
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
        ApplyLaunchButtonState(updateAvailable);

        if (_installedExecutable is null || _integrityResult?.State == TlIntegrityState.NotInstalled)
        {
            StatusDot.Fill = Brushes.DarkOrange;
            StatusText.Foreground = Brushes.DarkOrange;
            StatusText.Text = "Non installé";
            UpdateButton.Content = "↓  Installer TL";
            ShaStatusText.Text = "Intégrité : non applicable";
            FooterInstallStateText.Text = "◉  TL non installé";
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
            UpdateButton.Content = "↻  Supprimer + réinstaller TL";
            ShaStatusText.Text = "Intégrité : ÉCHEC";
            FooterInstallStateText.Text = "⚠  Installation TL à réparer";
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
            UpdateButton.Content = updateAvailable ? "↻  Réinstaller la dernière version" : "↻  Réinstaller proprement";
            ShaStatusText.Text = $"Intégrité vérifiée · {_integrityResult.CheckedFiles} SHA-256";
            FooterInstallStateText.Text = "✓  Installation TL vérifiée";
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
        FooterInstallStateText.Text = "◉  Installation TL non certifiée";
        ActivityText.Text = _integrityResult?.Message ?? "Impossible de certifier les fichiers locaux.";
    }

    private void ApplyLaunchButtonState(bool updateAvailable)
    {
        var installed = _installedExecutable is not null;
        var blocked = _integrityResult?.BlocksLaunch == true;
        var readyForLatest = installed && !blocked && !updateAvailable;

        LaunchButton.Style = (Style)FindResource(readyForLatest ? "GoldButton" : "ActionButton");
        LaunchButton.IsEnabled = readyForLatest || _stableRelease is not null;

        if (readyForLatest)
        {
            LaunchButton.Content = "▶  Lancer";
        }
        else if (!installed)
        {
            LaunchButton.Content = "↓  Installer TL";
        }
        else if (blocked)
        {
            LaunchButton.Content = "↻  Réparer TL";
        }
        else
        {
            LaunchButton.Content = "↓  Mettre à jour TL";
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
            return;

        var updateAvailable = _labService.IsUpdateAvailable(_installedVersion, _stableRelease?.Version);
        var readyForLatest = _installedExecutable is not null
            && _integrityResult?.BlocksLaunch != true
            && !updateAvailable;

        if (!readyForLatest)
        {
            UpdateButton_Click(sender, e);
            return;
        }

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
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
            return;

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
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTmbPanel();
            return;
        }

        RefreshButton.IsEnabled = false;
        var previousContent = RefreshButton.Content;
        RefreshButton.Content = "↻  Actualisation…";

        try
        {
            await RefreshAsync(forceRemoteRefresh: true);
        }
        finally
        {
            RefreshButton.Content = previousContent;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "TMB est bien sélectionné. Sa gestion installation/réparation sera branchée séparément.",
                "Taikeron Map Builder",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RefreshAsync();

        var settings = _settingsService.Current;
        var truth = _integrityResult?.Message ?? "État d’intégrité inconnu.";
        var message = _installedExecutable is null
            ? $"TL n’est pas détecté.\n\nDossier code prévu :\n{_labService.CanonicalInstallDirectory}\n\nData :\n{settings.DataRoot}\n\nVault :\n{settings.VaultRoot}\n\n{truth}"
            : $"Installation détectée :\n{_installedExecutable}\n\nVersion : {_installedVersion ?? "inconnue"}\n\nData :\n{settings.DataRoot}\n\nVault :\n{settings.VaultRoot}\n\nVérité Launcher :\n{truth}\n\nData et Vault ne sont jamais inclus dans le contrôle d’intégrité du code.";

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
        ActivityText.Text = "Sauvegarde automatique Data + Vault en cours…";

        try
        {
            var result = await Task.Run(async () => await _backupService.BackupAsync(settings));
            settings.LastBackupStatus = result.Message;
            _settingsService.Save(settings);
            ActivityText.Text = result.Success
                ? $"Data + Vault sauvegardés : {result.SnapshotPath}"
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
        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
        {
            LaunchButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            RepairActionButton.IsEnabled = false;
        }
        else if (busy)
        {
            UpdateButton.IsEnabled = false;
        }
        else
        {
            UpdateButton.IsEnabled = _stableRelease is not null;
            RepairActionButton.IsEnabled = true;
            ApplyLaunchButtonState(_labService.IsUpdateAvailable(_installedVersion, _stableRelease?.Version));
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
