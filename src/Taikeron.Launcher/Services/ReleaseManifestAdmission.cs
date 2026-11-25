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

    internal static void Package(string file, string url, string sha, long bytes)
    {
        if (string.IsNullOrWhiteSpace(file) || !Regex.IsMatch(file, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,220}\.exe$", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Uri.UnescapeDataString(uri.Segments.Last()), file, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sha) || !Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$") || bytes <= 0)
            throw new InvalidDataException("Paquet de distribution non vérifiable.");
    }
}
