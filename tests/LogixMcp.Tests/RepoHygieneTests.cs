using System.Diagnostics;

namespace LogixMcp.Tests;

/// <summary>
/// Repo hygiene guard: asserts that no vendor binary or proprietary
/// controller-project file is committed to source control.
///
/// <para>
/// Tracked files are enumerated via <c>git ls-files</c> (the authoritative
/// source — it reflects exactly what is committed or staged, not just what
/// happens to be on disk).  When <c>git</c> is unavailable in the test
/// environment the test falls back to a recursive filesystem walk from the
/// project root, excluding <c>bin/</c>, <c>obj/</c>, <c>.localfeed/</c>,
/// and <c>.git/</c> directories so build artefacts and the git object store
/// are not falsely reported.
/// </para>
///
/// <para>
/// The following file patterns are forbidden in the tracked tree:
/// <list type="bullet">
///   <item><description>Extensions: <c>.nupkg</c>, <c>.dll</c>, <c>.exe</c>, <c>.acd</c>, <c>.acd.bak</c></description></item>
///   <item><description>Names containing the string <c>RockwellAutomation</c> (case-insensitive)</description></item>
/// </list>
/// </para>
/// </summary>
public class RepoHygieneTests
{
    // ── Repo-root locator ────────────────────────────────────────────────────

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the project root: no *.sln file found while " +
            $"walking up from '{AppContext.BaseDirectory}'.");
    }

    private static readonly string ProjectRoot = FindProjectRoot();

    // ── Forbidden-file criteria ──────────────────────────────────────────────

    private static readonly HashSet<string> ForbiddenExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".nupkg",
            ".dll",
            ".exe",
            ".acd",
            ".acd.bak",
        };

    private static bool IsForbidden(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);

        // Files whose name contains "RockwellAutomation" (any case).
        if (fileName.Contains("RockwellAutomation", StringComparison.OrdinalIgnoreCase))
            return true;

        // Files with a forbidden extension.
        // .acd.bak has two dots, so we need to check the full suffix, not just
        // the last extension component returned by Path.GetExtension().
        string ext = Path.GetExtension(fileName);
        if (ForbiddenExtensions.Contains(ext))
            return true;

        // Special case: ".acd.bak" — GetExtension returns ".bak" for these,
        // so check both the last extension and whether the name ends with
        // ".acd.bak" explicitly.
        if (fileName.EndsWith(".acd.bak", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // ── File enumeration (git ls-files with filesystem fallback) ─────────────

    private static IReadOnlyList<string> GetTrackedFiles()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "ls-files" },
                WorkingDirectory = ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process.");

            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
            }
        }
        catch (Exception)
        {
            // git not available — fall through to filesystem walk.
        }

        // Filesystem fallback: walk the project root, excluding build-output
        // and git-internal directories that are never part of the source tree.
        static IEnumerable<string> WalkExcludingBuildDirs(string root)
        {
            var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bin", "obj", ".localfeed", ".git",
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (Directory.Exists(entry))
                {
                    string dirName = Path.GetFileName(entry);
                    if (excludedDirs.Contains(dirName))
                        continue;

                    foreach (var nested in WalkExcludingBuildDirs(entry))
                        yield return nested;
                }
                else
                {
                    // Return a path relative to root (using forward slashes to
                    // match the git ls-files output format).
                    yield return Path.GetRelativePath(root, entry)
                        .Replace(Path.DirectorySeparatorChar, '/');
                }
            }
        }

        return WalkExcludingBuildDirs(ProjectRoot).ToList();
    }

    // ── Test ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that no file tracked by git (or found via the filesystem
    /// fallback walk) is a vendor binary or proprietary controller-project
    /// file.
    ///
    /// <para>
    /// Files are forbidden when their name contains <c>RockwellAutomation</c>
    /// or their extension is one of: <c>.nupkg</c>, <c>.dll</c>, <c>.exe</c>,
    /// <c>.acd</c>, <c>.acd.bak</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void NoVendorBinariesTracked()
    {
        var trackedFiles = GetTrackedFiles();

        var violations = trackedFiles
            .Where(IsForbidden)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"The following {violations.Count} tracked file(s) violate the " +
            "no-vendor-binaries policy (.nupkg / .dll / .exe / .acd / .acd.bak " +
            "extensions, or names containing 'RockwellAutomation' are forbidden " +
            "in source control):\n" +
            string.Join("\n", violations.Select(v => $"  {v}")));
    }
}
