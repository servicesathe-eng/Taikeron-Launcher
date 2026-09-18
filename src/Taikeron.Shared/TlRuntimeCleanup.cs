namespace Taikeron.Shared;

public sealed record TlRuntimeCleanupReport(
    IReadOnlyList<string> RemovedDirectories,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> SkippedProtectedPaths);

public static class TlRuntimeCleanup
{
    public static TlRuntimeCleanupReport PurgeVolatileState(
        IEnumerable<string?> preservedRoots,
        Action<string>? progress = null)
    {
        var protectedRoots = preservedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Normalize(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var removedDirectories = new List<string>();
        var removedFiles = new List<string>();
        var skipped = new List<string>();

        foreach (var candidate in CandidateDirectories())
        {
            if (protectedRoots.Any(root => PathsIntersect(candidate, root)))
            {
                skipped.Add(candidate);
                continue;
            }

            if (!Directory.Exists(candidate))
                continue;

            progress?.Invoke($"Suppression runtime Electron/Chromium : {candidate}");
            DeleteDirectoryStrict(candidate);
            removedDirectories.Add(candidate);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var crashRoot = Path.Combine(local, "CrashDumps");
        if (Directory.Exists(crashRoot))
        {
            foreach (var pattern in new[]
            {
                "Taikeron Lab*.dmp",
                "TaikeronLab*.dmp",
                "taikeron-lab-cycling-os*.dmp"
            })
            {
                foreach (var file in Directory.EnumerateFiles(crashRoot, pattern, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removedFiles.Add(file);
                    }
                    catch
                    {
                        // Crash dumps are non-critical cleanup targets.
                    }
                }
            }
        }

        return new TlRuntimeCleanupReport(removedDirectories, removedFiles, skipped);
    }

    public static IReadOnlyList<string> CandidateDirectories()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var temp = Path.GetTempPath();

        return new[]
        {
            Path.Combine(local, "Taikeron", "Runtime", "Lab"),
            Path.Combine(local, "Taikeron", "Lab"),
            Path.Combine(roaming, "Taikeron", "Lab"),
            Path.Combine(local, "Taikeron Lab"),
            Path.Combine(roaming, "Taikeron Lab"),
            Path.Combine(local, "taikeron-lab-cycling-os"),
            Path.Combine(roaming, "taikeron-lab-cycling-os"),
            Path.Combine(local, "com.taikeronlab.cyclingos"),
            Path.Combine(roaming, "com.taikeronlab.cyclingos"),
            Path.Combine(temp, "Taikeron", "Lab"),
            Path.Combine(temp, "Taikeron Lab")
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(Normalize)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static bool PathsIntersect(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || IsInside(left, right)
            || IsInside(right, left);
    }

    private static bool IsInside(string candidate, string parent)
    {
        var fullCandidate = Normalize(candidate);
        var fullParent = Normalize(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void DeleteDirectoryStrict(string directory)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                ClearReadOnly(directory);
                Directory.Delete(directory, recursive: true);

                if (!Directory.Exists(directory))
                    return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }

            Thread.Sleep(250);
        }

        throw new IOException($"Impossible de supprimer complètement le runtime volatile : {directory}", lastError);
    }

    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        catch { }
    }
}
