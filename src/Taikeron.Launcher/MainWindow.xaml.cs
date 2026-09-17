using System.Windows;
using System.Windows.Media;
using Taikeron.Launcher.Models;
using Taikeron.Launcher.Services;

namespace Taikeron.Launcher;

public partial class MainWindow : Window
{
    private readonly TaikeronLabService _labService = new();
    private TlReleaseManifest? _stableRelease;
    private string? _installedExecutable;
    private string? _installedVersion;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
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
                ActivityText.Text = "TL pourra être installé dans le dossier canonique géré par le launcher.";
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

        var message = _installedExecutable is null
            ? "TL n’est pas détecté. La réparation complète sera activée avec le worker d’installation atomique."
            : $"Installation détectée :\n{_installedExecutable}\n\nVersion : {_installedVersion ?? "inconnue"}\n\nAucun fichier utilisateur n’a été modifié.";

        MessageBox.Show(message, "Diagnostic TL", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"Canal : stable\n\nDossier code canonique TL :\n{_labService.CanonicalInstallDirectory}\n\nLes données utilisateur resteront séparées du dossier code.",
            "Paramètres du launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
