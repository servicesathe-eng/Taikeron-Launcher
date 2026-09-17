using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Taikeron.Launcher.Models;

namespace Taikeron.Launcher.Services;

public sealed class TlIntegrityService
{
    public const string StableIntegrityManifestUrl =
        "https://taikeron-cyclingos.github.io/releases/tl/integrity-stable.json";

    private const string VersionedIntegrityBaseUrl =
        "https://taikeron-cyclingos.github.io/releases/tl/integrity";

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<TlIntegrityManifest?> GetManifestForInstalledVersionAsync(
        string? installedVersion,
        TlReleaseManifest? officialRelease,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeVersion(installedVersion);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var versionedUrl = $"{VersionedIntegrityBaseUrl}/{Uri.EscapeDataString(normalized)}/windows-x64.json";
        var versioned = await GetManifestAsync(versionedUrl, cancellationToken);
        if (versioned is not null)
            return versioned;

        if (string.Equals(normalized, NormalizeVersion(officialRelease?.Version), StringComparison.OrdinalIgnoreCase))
            return await GetManifestAsync(StableIntegrityManifestUrl, cancellationToken);

        return null;
    }

    public async Task<TlIntegrityCheckResult> VerifyAsync(
        string? executablePath,
        string? installedVersion,
        TlReleaseManifest? officialRelease,
        TlIntegrityManifest? integrityManifest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return Result(TlIntegrityState.NotInstalled, "Taikeron Lab n’est pas installé.");

        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new InvalidOperationException("Dossier d’installation TL introuvable.");

        if (integrityManifest is null)
            return Result(
                TlIntegrityState.ReferenceUnavailable,
                "Cette version ne possède pas encore de manifeste d’intégrité public. La version est connue, mais les fichiers ne peuvent pas encore être certifiés.");

        ValidateManifest(integrityManifest);

        var officialVersion = NormalizeVersion(officialRelease?.Version);
        var referenceVersion = NormalizeVersion(integrityManifest.Version);
        var localVersion = NormalizeVersion(installedVersion);

        if (!string.Equals(localVersion, referenceVersion, StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                TlIntegrityState.VersionMismatch,
                $"Le manifeste récupéré concerne {integrityManifest.Version}, mais TL local déclare {installedVersion ?? "une version inconnue"}.");
        }

        if (officialRelease is not null &&
            string.Equals(officialVersion, referenceVersion, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(integrityManifest.PackageSha256) &&
            !string.Equals(NormalizeSha(integrityManifest.PackageSha256), NormalizeSha(officialRelease.Sha256), StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                TlIntegrityState.ReferenceUnavailable,
                "Le manifeste d’intégrité ne correspond pas au paquet officiel publié. Vérification refusée.");
        }

        var problems = new List<string>();
        var missing = 0;
        var mismatched = 0;
        var checkedFiles = 0;
        var files = integrityManifest.Files;

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = files[index];
            var localPath = ResolveSafePath(installDirectory, expected.Path);

            if (!File.Exists(localPath))
            {
                missing++;
                AddProblem(problems, $"Absent : {expected.Path}");
                progress?.Report((double)(index + 1) / Math.Max(1, files.Count));
                continue;
            }

            var info = new FileInfo(localPath);
            if (expected.Bytes >= 0 && info.Length != expected.Bytes)
            {
                mismatched++;
                AddProblem(problems, $"Taille différente : {expected.Path}");
                progress?.Report((double)(index + 1) / Math.Max(1, files.Count));
                continue;
            }

            var actualSha = await ComputeSha256Async(localPath, cancellationToken);
            checkedFiles++;
            if (!string.Equals(actualSha, NormalizeSha(expected.Sha256), StringComparison.OrdinalIgnoreCase))
            {
                mismatched++;
                AddProblem(problems, $"SHA-256 différent : {expected.Path}");
            }

            progress?.Report((double)(index + 1) / Math.Max(1, files.Count));
        }

        foreach (var residue in FindKnownUpdaterResidues(installDirectory))
        {
            mismatched++;
            AddProblem(problems, $"Résidu d’ancienne activation : {Path.GetRelativePath(installDirectory, residue)}");
        }

        if (missing > 0 || mismatched > 0)
        {
            return new TlIntegrityCheckResult(
                TlIntegrityState.Corrupted,
                $"Installation non conforme : {missing} fichier(s) absent(s), {mismatched} différence(s).",
                checkedFiles,
                missing,
                mismatched,
                problems);
        }

        progress?.Report(1);
        return new TlIntegrityCheckResult(
            TlIntegrityState.Healthy,
            $"Installation vérifiée : {files.Count} fichier(s) conformes à Taikeron {integrityManifest.Version}.",
            checkedFiles,
            0,
            0,
            []);
    }

    private async Task<TlIntegrityManifest?> GetManifestAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<TlIntegrityManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, cancellationToken);
    }

    private static void ValidateManifest(TlIntegrityManifest manifest)
    {
        if (!string.Equals(manifest.Format, "taikeron_integrity_manifest", StringComparison.Ordinal))
            throw new InvalidDataException("Format du manifeste d’intégrité TL inconnu.");
        if (!string.Equals(manifest.Product, "TL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le manifeste d’intégrité ne concerne pas Taikeron Lab.");
        if (!string.Equals(manifest.Platform, "windows-x64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plateforme d’intégrité TL non prise en charge.");
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files.Count == 0)
            throw new InvalidDataException("Manifeste d’intégrité TL incomplet.");

        var duplicate = manifest.Files
            .GroupBy(item => item.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Chemin dupliqué dans le manifeste d’intégrité : {duplicate.Key}");
    }

    private static string ResolveSafePath(string installDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Chemin de fichier d’intégrité invalide.");

        var root = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, normalizedRelative));

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le manifeste d’intégrité tente de sortir du dossier TL.");

        return candidate;
    }

    private static IEnumerable<string> FindKnownUpdaterResidues(string installDirectory)
    {
        var resources = Path.Combine(installDirectory, "resources");
        if (!Directory.Exists(resources))
            yield break;

        string[] names =
        [
            "app.asar.wdst-new",
            "app.asar.wdst-backup",
            "app.asar.wdst-failed"
        ];

        foreach (var name in names)
        {
            var candidate = Path.Combine(resources, name);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeSha(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        var separator = normalized.IndexOf(':');
        if (separator >= 0)
            normalized = normalized[(separator + 1)..];
        return normalized.Trim().ToLowerInvariant();
    }

    private static string NormalizeVersion(string? value)
    {
        var clean = (value ?? string.Empty).Trim().Replace('-', '.');
        return Version.TryParse(clean, out var parsed) ? parsed.ToString() : clean;
    }

    private static TlIntegrityCheckResult Result(TlIntegrityState state, string message) =>
        new(state, message, 0, 0, 0, []);

    private static void AddProblem(List<string> problems, string problem)
    {
        if (problems.Count < 12)
            problems.Add(problem);
    }
}
