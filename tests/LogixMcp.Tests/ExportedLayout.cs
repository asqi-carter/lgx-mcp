namespace LogixMcp.Tests;

/// <summary>
/// Where the documentation set lives, as the shared test helpers need to know it. The documentation sits at the
/// repo root (<c>README.md</c>, <c>CONTRIBUTING.md</c>, <c>docs/*.md</c>) and every file is read from its own place.
/// </summary>
public static class ExportedLayout
{
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj" };

    /// <summary>The directory the documentation set is read from: the repo root.</summary>
    public static string DocsRoot(string root) => root;

    /// <summary>The <c>docs/</c> directory of the documentation set.</summary>
    public static string DocsDirectory(string root) => Path.Combine(root, "docs");

    /// <summary>
    /// The path of the file or directory at <paramref name="rel"/> (forward slashes, no leading slash),
    /// or null when it does not exist.
    /// </summary>
    public static string? Resolve(string root, string rel)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) || Directory.Exists(full) ? full : null;
    }

    /// <summary>Every file under <paramref name="root"/> (relative, forward slashes), skipping <c>.git</c>, <c>bin</c> and <c>obj</c>.</summary>
    public static IEnumerable<string> GuardedFiles(string root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var f in dir.GetFiles())
                yield return Path.GetRelativePath(root, f.FullName).Replace('\\', '/');
            foreach (var sub in dir.GetDirectories())
                if (!SkippedDirectories.Contains(sub.Name))
                    stack.Push(sub);
        }
    }
}
