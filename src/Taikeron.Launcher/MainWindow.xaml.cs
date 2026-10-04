using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly TaikeronMapBuilderService _tmbService;
    private readonly TlUpdateWorkerService _updateWorkerService;
    private readonly DispatcherTimer _backupTimer;

    private TlReleaseManifest? _stableRelease;
    private TmbReleaseManifest? _tmbStableRelease;
    private LauncherReleaseInfo? _launcherRelease;
    private TlIntegrityCheckResult? _integrityResult;
    private TmbIntegrityCheckResult? _tmbIntegrityResult;
    private string? _installedExecutable;
    private string? _installedVersion;
    private string? _tmbInstalledExecutable;
    private string? _tmbInstalledVersion;
    private bool _backupInProgress;
    private string _selectedProduct = "TL";

    public MainWindow()
    {
        _labService = new TaikeronLabService(_settingsService);
        _tmbService = new TaikeronMapBuilderService(_settingsService);
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
            ApplyTmbIdentity();
            await RefreshTmbAsync();
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
        ProductHeroImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/TaikeronLabMark.png", UriKind.Absolute));
        ProductHeroTitleText.Text = "Taikeron Lab (TL)";
        ProductHeroSubtitleText.Text = "Création, édition et analyse de parcours cyclistes.";
        ProductHeroDescriptionText.Text = "Le Launcher installe, vérifie, répare et met à jour TL.";
        ProductBullet1Text.Text = "• Distribution centralisée par le Launcher";
        ProductBullet2Text.Text = "• Vérification taille + SHA-256";
        ProductBullet3Text.Text = "• Remplacement intégral runtime + caches, Data/Vault/Maps protégés";
        RepairActionButton.IsEnabled = true;
        UninstallButton.IsEnabled = true;
        UninstallButton.Content = "✕  Désinstaller TL";
    }

    private void ApplyTmbIdentity()
    {
        ProductHeroImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/TaikeronMapBuilderMark.png", UriKind.Absolute));
        ProductHeroTitleText.Text = "Taikeron Map Builder (TMB)";
        ProductHeroSubtitleText.Text = "Création et préparation de cartes Taikeron.";
        ProductHeroDescriptionText.Text = "Le Launcher installe, vérifie, répare et met à jour TMB.";
        ProductBullet1Text.Text = "• Runtime Portable officiel géré dans Apps/MapBuilder";
        ProductBullet2Text.Text = "• Vérification taille + SHA-256 du binaire public";
        ProductBullet3Text.Text = "• Workspace et cartes restent séparés du code";
        UninstallButton.Content = "✕  Désinstaller TMB";
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
            await RefreshTmbAsync(forceRemoteRefresh);
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

    private async Task RefreshTmbAsync(bool forceRemoteRefresh = false)
    {
        SetBusy(true, "Vérification de Taikeron Map Builder…");

        try
        {
            _tmbInstalledExecutable = _tmbService.FindInstalledExecutable();
            _tmbInstalledVersion = _tmbService.GetInstalledVersion(_tmbInstalledExecutable);

            InstallPathText.Text = _tmbInstalledExecutable is null
                ? $"TMB non détecté. Emplacement canonique prévu : {_tmbService.CanonicalInstallDirectory}"
                : _tmbInstalledExecutable;
            InstalledVersionText.Text = _tmbInstalledVersion ?? "Non installé";

            _tmbStableRelease = await _tmbService.GetStableReleaseAsync(forceRemoteRefresh);
            LatestVersionText.Text = string.IsNullOrWhiteSpace(_tmbStableRelease?.Version)
                ? "—"
                : _tmbStableRelease.Version;
            VersionCardValue.Text = LatestVersionText.Text;
            DownloadSizeText.Text = _tmbStableRelease is null
                ? "Taille : —"
                : $"Taille : {FormatBytes(_tmbStableRelease.Bytes)}";
            VersionDateText.Text = _tmbStableRelease?.PublishedAt is null
                ? "Publication : —"
                : $"Publication : {_tmbStableRelease.PublishedAt.Value.LocalDateTime:dd/MM/yyyy HH:mm}";

            _tmbIntegrityResult = await _tmbService.VerifyAsync(
                _tmbInstalledExecutable,
                _tmbInstalledVersion,
                _tmbStableRelease);
            ApplyTmbTruthStatus();
        }
        catch (Exception ex)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Vérification TMB incomplète";
            LatestVersionText.Text = "Indisponible";
            UpdateBadge.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = false;
            ShaStatusText.Text = "Référence officielle TMB indisponible";
            FooterInstallStateText.Text = "⚠  État TMB non vérifié";
            ActivityText.Text = $"Impossible d’établir l’état officiel de TMB : {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyTmbTruthStatus()
    {
        var updateAvailable = _tmbService.IsUpdateAvailable(
            _tmbInstalledVersion,
            _tmbStableRelease?.Version);
        var installed = _tmbInstalledExecutable is not null;
        var blocked = _tmbIntegrityResult?.BlocksLaunch == true;
        var managed = _tmbService.IsLauncherManaged(_tmbInstalledExecutable);

        UpdateBadge.Visibility = updateAvailable || blocked || (installed && !managed)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateBadgeText.Text = blocked
            ? "Réparation requise"
            : installed && !managed
                ? "Migration Launcher"
                : updateAvailable
                    ? "Mise à jour disponible"
                    : "";

        RepairActionButton.IsEnabled = true;
        UninstallButton.IsEnabled = managed;

        if (!installed || _tmbIntegrityResult?.State == TmbIntegrityState.NotInstalled)
        {
            StatusDot.Fill = Brushes.DarkOrange;
            StatusText.Foreground = Brushes.DarkOrange;
            StatusText.Text = "Non installé";
            ShaStatusText.Text = "Intégrité : non applicable";
            FooterInstallStateText.Text = "◉  TMB non installé";
            ActivityText.Text = $"TMB pourra être installé dans {_tmbService.CanonicalInstallDirectory}.";
            ApplyTmbLaunchButtonState(updateAvailable);
            return;
        }

        if (_tmbIntegrityResult?.State == TmbIntegrityState.Corrupted)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Installation TMB altérée";
            ShaStatusText.Text = "SHA-256 TMB : ÉCHEC";
            FooterInstallStateText.Text = "⚠  Installation TMB à réparer";
            ActivityText.Text = _tmbIntegrityResult.Message;
            ApplyTmbLaunchButtonState(updateAvailable);
            return;
        }

        if (_tmbIntegrityResult?.State == TmbIntegrityState.Healthy)
        {
            StatusDot.Fill = (Brush)FindResource("Green");
            StatusText.Foreground = (Brush)FindResource("Green");
            StatusText.Text = updateAvailable ? "Installé · intact" : "Installé · intact · à jour";
            ShaStatusText.Text = "Portable TMB vérifié · SHA-256 conforme";
            FooterInstallStateText.Text = "✓  Installation TMB vérifiée";
            ActivityText.Text = updateAvailable
                ? "Runtime TMB vérifié. Une version plus récente est disponible."
                : _tmbIntegrityResult.Message;
            ApplyTmbLaunchButtonState(updateAvailable);
            return;
        }

        if (_tmbIntegrityResult?.State == TmbIntegrityState.LegacyUnmanaged)
        {
            StatusDot.Fill = Brushes.DarkOrange;
            StatusText.Foreground = Brushes.DarkOrange;
            StatusText.Text = "Installé · hors gestion Launcher";
            ShaStatusText.Text = "Intégrité : installation historique";
            FooterInstallStateText.Text = "◉  TMB détecté hors Apps/MapBuilder";
            ActivityText.Text = "TMB peut être lancé tel quel ou migré vers le runtime Portable géré par le Launcher.";
            ApplyTmbLaunchButtonState(updateAvailable);
            return;
        }

        StatusDot.Fill = Brushes.DarkOrange;
        StatusText.Foreground = Brushes.DarkOrange;
        StatusText.Text = updateAvailable ? "Installé · mise à jour disponible" : "Installé · référence incomplète";
        ShaStatusText.Text = "Intégrité : référence locale non comparable";
        FooterInstallStateText.Text = "◉  Installation TMB non certifiée";
        ActivityText.Text = _tmbIntegrityResult?.Message ?? "Impossible de certifier le runtime TMB.";
        ApplyTmbLaunchButtonState(updateAvailable);
    }

    private void ApplyTmbLaunchButtonState(bool updateAvailable)
    {
        var installed = _tmbInstalledExecutable is not null;
        var blocked = _tmbIntegrityResult?.BlocksLaunch == true;
        var managed = _tmbService.IsLauncherManaged(_tmbInstalledExecutable);
        var ready = installed && !blocked && (!managed || !updateAvailable);

        LaunchButton.Style = (Style)FindResource(ready ? "GoldButton" : "ActionButton");
        LaunchButton.IsEnabled = ready || _tmbStableRelease is not null;
        UpdateButton.IsEnabled = _tmbStableRelease is not null;

        if (!installed)
        {
            LaunchButton.Content = "↓  Installer TMB";
            UpdateButton.Content = "↓  Installer TMB";
        }
        else if (blocked)
        {
            LaunchButton.Content = "↻  Réparer TMB";
            UpdateButton.Content = "↻  Réinstaller TMB";
        }
        else if (!managed)
        {
            LaunchButton.Content = "▶  Lancer TMB";
            UpdateButton.Content = "↓  Migrer TMB vers le Launcher";
        }
        else if (updateAvailable)
        {
            LaunchButton.Content = "↓  Mettre à jour TMB";
            UpdateButton.Content = "↓  Mettre à jour TMB";
        }
        else
        {
            LaunchButton.Content = "▶  Lancer TMB";
            UpdateButton.Content = "↻  Réinstaller proprement";
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
        UninstallButton.IsEnabled = true;

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
        if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            var tmbUpdateAvailable = _tmbService.IsUpdateAvailable(
                _tmbInstalledVersion,
                _tmbStableRelease?.Version);
            var managed = _tmbService.IsLauncherManaged(_tmbInstalledExecutable);
            var ready = _tmbInstalledExecutable is not null
                && _tmbIntegrityResult?.BlocksLaunch != true
                && (!managed || !tmbUpdateAvailable);

            if (!ready)
            {
                UpdateButton_Click(sender, e);
                return;
            }

            try
            {
                _tmbService.Launch(_tmbInstalledExecutable!);
                ActivityText.Text = "Taikeron Map Builder lancé.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Impossible de lancer TMB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

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
        if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            await InstallOrUpdateTmbAsync();
            return;
        }

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

    private async Task InstallOrUpdateTmbAsync()
    {
        if (!_settingsService.Current.InitialSetupCompleted)
        {
            var setup = new FirstInstallWindow(_settingsService)
            {
                Owner = this
            };

            if (setup.ShowDialog() != true)
            {
                ActivityText.Text = "Installation TMB annulée avant choix des emplacements.";
                return;
            }

            _settingsService.EnsureConfiguredDirectories();
        }

        if (_tmbStableRelease is null)
        {
            MessageBox.Show(
                "Aucune release stable TMB n’est chargée.",
                "Taikeron Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        SetBusy(true, $"Téléchargement de TMB {_tmbStableRelease.Version}…");

        try
        {
            var downloadProgress = new Progress<double>(value =>
            {
                DownloadProgress.Value = value * 100;
                ActivityText.Text = $"Téléchargement et vérification du Portable TMB… {value:P0}";
            });

            var packagePath = await _tmbService.DownloadAndVerifyAsync(
                _tmbStableRelease,
                downloadProgress);

            DownloadProgress.Value = 100;
            var installProgress = new Progress<string>(message => ActivityText.Text = message);
            var executable = await _tmbService.InstallOrReplaceAsync(
                packagePath,
                _tmbStableRelease,
                _tmbInstalledExecutable,
                installProgress);

            _tmbInstalledExecutable = executable;
            ActivityText.Text = "Runtime TMB installé. Contrôle SHA-256 final…";
            await RefreshTmbAsync(forceRemoteRefresh: true);

            if (_tmbIntegrityResult?.BlocksLaunch == true)
                throw new InvalidOperationException(
                    "TMB a été installé, mais le contrôle SHA-256 final a échoué.");

            _tmbService.Launch(executable);
            MessageBox.Show(
                $"Taikeron Map Builder {_tmbStableRelease.Version} a été installé et vérifié.\n\n" +
                $"Runtime : {_tmbService.CanonicalInstallDirectory}\n" +
                "Le workspace, les cartes et la configuration TMB restent séparés du code.",
                "Installation TMB terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            DownloadProgress.Value = 0;
            ActivityText.Text = $"Échec TMB : {ex.Message}";
            MessageBox.Show(
                ex.Message,
                "Échec de l’installation TMB",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            if (!_tmbService.IsLauncherManaged(_tmbInstalledExecutable))
            {
                MessageBox.Show(
                    "Cette installation TMB n’est pas gérée par le Launcher. Elle ne sera pas supprimée automatiquement.",
                    "TMB hors gestion Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var tmbConfirm = MessageBox.Show(
                "Supprimer le runtime Taikeron Map Builder géré par le Launcher ?\n\n" +
                "Le workspace TMB, les cartes produites et la configuration utilisateur sont conservés.",
                "Désinstaller Taikeron Map Builder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (tmbConfirm != MessageBoxResult.Yes)
                return;

            try
            {
                SetBusy(true, "Désinstallation du runtime TMB…");
                var progress = new Progress<string>(message => ActivityText.Text = message);
                var removed = await _tmbService.UninstallAsync(_tmbInstalledExecutable, progress);
                _tmbInstalledExecutable = null;
                _tmbInstalledVersion = null;
                _tmbIntegrityResult = null;
                await RefreshTmbAsync(forceRemoteRefresh: true);
                MessageBox.Show(
                    $"Runtime TMB supprimé :\n{removed}\n\nWorkspace et cartes conservés.",
                    "Désinstallation TMB terminée",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Échec de désinstallation TMB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
            return;
        }

        if (!string.Equals(_selectedProduct, "TL", StringComparison.OrdinalIgnoreCase))
            return;

        var installedLabel = _installedExecutable is null
            ? "Aucune installation TL active n’est détectée. Le Launcher peut néanmoins nettoyer les restes runtime."
            : $"Installation détectée :\n{_installedExecutable}";

        var confirm = MessageBox.Show(
            $"{installedLabel}\n\n" +
            "Cette opération supprime complètement le code Taikeron Lab, les profils Electron/Chromium, caches GPU/Code Cache, IndexedDB/localStorage/sessionStorage, crash dumps et paquets TL temporaires.\n\n" +
            "Data, Vault, Maps et les sauvegardes configurées sont conservés.\n\n" +
            "Continuer ?",
            "Désinstaller complètement Taikeron Lab",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            SetBusy(true, "Désinstallation complète du runtime Taikeron Lab…");
            var progress = new Progress<string>(message => ActivityText.Text = message);
            var result = await _labService.UninstallAsync(_installedExecutable, progress);

            _installedExecutable = null;
            _installedVersion = null;
            _integrityResult = null;

            await RefreshAsync(forceRemoteRefresh: true);

            MessageBox.Show(
                "Taikeron Lab a été désinstallé complètement.\n\n" +
                $"Profils/caches runtime supprimés : {result.RemovedRuntimeDirectories}\n" +
                $"Fichiers runtime supprimés : {result.RemovedRuntimeFiles}\n\n" +
                $"Data conservées : {result.DataRoot}\n" +
                $"Vault conservé : {result.VaultRoot}\n" +
                $"Maps conservées : {result.MapsRoot}",
                "Désinstallation TL terminée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ActivityText.Text = $"Échec de désinstallation : {ex.Message}";
            MessageBox.Show(
                ex.Message,
                "Échec de désinstallation TL",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            RefreshButton.IsEnabled = false;
            var tmbPreviousContent = RefreshButton.Content;
            RefreshButton.Content = "↻  Actualisation…";
            try
            {
                await RefreshTmbAsync(forceRemoteRefresh: true);
            }
            finally
            {
                RefreshButton.Content = tmbPreviousContent;
                RefreshButton.IsEnabled = true;
            }
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
        if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshTmbAsync(forceRemoteRefresh: true);
            var integrity = _tmbIntegrityResult?.Message ?? "État d’intégrité inconnu.";
            var location = _tmbInstalledExecutable ?? _tmbService.CanonicalInstallDirectory;
            MessageBox.Show(
                $"Installation :\n{location}\n\n" +
                $"Version : {_tmbInstalledVersion ?? "non installée"}\n\n" +
                $"État Launcher :\n{integrity}\n\n" +
                "Réinstaller TMB remplace uniquement le runtime géré ; workspace et cartes sont conservés.",
                "Diagnostic TMB",
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
        if (busy)
        {
            LaunchButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            RepairActionButton.IsEnabled = false;
            UninstallButton.IsEnabled = false;
        }
        else if (string.Equals(_selectedProduct, "TMB", StringComparison.OrdinalIgnoreCase))
        {
            var updateAvailable = _tmbService.IsUpdateAvailable(
                _tmbInstalledVersion,
                _tmbStableRelease?.Version);
            ApplyTmbLaunchButtonState(updateAvailable);
            RepairActionButton.IsEnabled = true;
            UninstallButton.IsEnabled = _tmbService.IsLauncherManaged(_tmbInstalledExecutable);
        }
        else
        {
            UpdateButton.IsEnabled = _stableRelease is not null;
            RepairActionButton.IsEnabled = true;
            UninstallButton.IsEnabled = true;
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
