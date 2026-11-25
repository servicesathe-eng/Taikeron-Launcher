using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Taikeron.Launcher.Services;

// Qualification boundary: only the published v1 dialects are supported.
// Hash equality still has to be checked on the downloaded bytes.
internal static class ReleaseManifestAdmission
{
    internal static void Header(string format, string schema, string product, string channel, string version, string expected)
    {
        var supported = schema == "1.0.0" || (expected == "TMC" && schema == "1");
        if (format != "taikeron_release_manifest" || !supported ||
            !string.Equals(product, expected, StringComparison.OrdinalIgnoreCase) ||
            channel != "stable" || !Version.TryParse(version, out _))
            throw new InvalidDataException("Manifeste de distribution incompatible.");
    }

    internal static void Package(string file, string url, string sha, long bytes, string product, string version)
    {
        if (string.IsNullOrWhiteSpace(file) || !Regex.IsMatch(file, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,220}\.exe$", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !uri.IsDefaultPort ||
            !TrustedReleasePath(uri, file, product, version) ||
            string.IsNullOrWhiteSpace(sha) || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$") || bytes <= 0)
            throw new InvalidDataException("Paquet de distribution non vérifiable.");
    }
    private static bool TrustedReleasePath(Uri uri, string file, string product, string version)
    {
        if (!Version.TryParse(version, out _)) return false;
        var repository = product switch
        {
            "TL" or "TMB" => "/Taikeron-CyclingOS/Taikeron-CyclingOS.github.io",
            "TMC" => "/servicesathe-eng/Taikeron-TMC-Releases",
            _ => null
        };
        if (repository is null) return false;
        var tag = product switch
        {
            "TL" => "tl-" + version,
            "TMB" => "tmb-" + version,
            _ => "tmc-v" + version
        };
        var expected = repository + "/releases/download/" + tag + "/" + file;
        return string.Equals(uri.AbsolutePath, expected, StringComparison.Ordinal);
    }
}
