using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Source-scan guards for the no-SDK publish/release Makefile recipes. It reads the
/// Makefile text only.
/// </summary>
public class ReleaseTargetsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LogixMcp.sln not found above test base directory.");
    }

    /// <summary>Returns (target, logical recipe line) pairs with backslash continuations joined.</summary>
    internal static List<(string Target, string Line)> RecipeLines(string makefile)
    {
        var result = new List<(string, string)>();
        var raw = makefile.Replace("\r\n", "\n").Split('\n');
        string target = "";
        for (int i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            var rule = Regex.Match(line, @"^([A-Za-z0-9_.-]+)\s*:(?!=)");
            if (rule.Success && !line.StartsWith(".PHONY")) { target = rule.Groups[1].Value; continue; }
            if (!line.StartsWith('\t')) { if (line.Length > 0 && !line.StartsWith('#')) target = ""; continue; }
            var joined = line.Substring(1);
            while (joined.EndsWith('\\') && i + 1 < raw.Length)
                joined = joined.Substring(0, joined.Length - 1) + " " + raw[++i].Trim();
            result.Add((target, joined));
        }
        return result;
    }

    /// <summary>Splits a recipe line into shell command segments with leading keywords removed.</summary>
    internal static IEnumerable<string> Segments(string recipeLine)
    {
        foreach (var seg in Regex.Split(recipeLine, @"&&|\|\||;|\|"))
        {
            var s = Regex.Replace(seg.Trim(), @"^[@\-+]+\s*", "");
            s = Regex.Replace(s, @"^(?:(?:do|then|else)\s+)+", "");
            if (s.Length > 0) yield return s;
        }
    }

    /// <summary>True when a shell segment is <c>cp</c> (optionally with flags) and nothing else.</summary>
    internal static bool IsBareCp(string segment) =>
        Regex.IsMatch(segment, @"^cp(\s+-\S+)*\s*$");

    private static bool SourceExists(string root, string path)
    {
        var dir = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var name = Path.GetFileName(path);
        var full = Path.Combine(root, dir);
        if (name.Contains('*'))
            return Directory.Exists(full) && Directory.GetFileSystemEntries(full, name).Length > 0;
        return File.Exists(Path.Combine(root, path)) || Directory.Exists(Path.Combine(root, path));
    }

    [Fact]
    public void NoSdkRecipesAreWellFormed()
    {
        var root = RepoRoot();
        var recipes = RecipeLines(File.ReadAllText(Path.Combine(root, "Makefile")));

        // 1. No recipe line anywhere may be a cp with no arguments.
        var bare = recipes
            .Where(r => Segments(r.Line).Any(IsBareCp))
            .Select(r => $"{r.Target}: {r.Line.Trim()}")
            .ToList();
        Assert.True(bare.Count == 0, "Argument-less cp in Makefile:" + Environment.NewLine + string.Join(Environment.NewLine, bare));

        // 2. Every literal path a no-SDK recipe copies, or runs as a script, must exist in the tree.
        var noSdk = new[] { "publish-win-nosdk", "publish-linux-nosdk", "release-nosdk" };
        var scanned = 0;
        var missing = new List<string>();
        foreach (var (target, line) in recipes.Where(r => noSdk.Contains(r.Target)))
        {
            foreach (var seg in Segments(line))
            {
                var cp = Regex.Match(seg, @"^cp((?:\s+-\S+)*)\s+(\S+)\s+(\S+)");
                if (cp.Success)
                {
                    var src = cp.Groups[2].Value.Trim('"');
                    if (src.Contains('$')) continue;
                    scanned++;
                    if (!SourceExists(root, src)) missing.Add($"{target}: cp source '{src}' does not exist");
                }
                var script = Regex.Match(seg, @"^(?:bash|python3)\s+(scripts/\S+)");
                if (script.Success)
                {
                    scanned++;
                    if (!SourceExists(root, script.Groups[1].Value))
                        missing.Add($"{target}: script '{script.Groups[1].Value}' does not exist");
                }
            }
        }
        Assert.True(scanned > 0, "No-SDK recipes copied/ran nothing checkable; the scan is stale.");
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// <summary>The scanner must flag the exact breakage seen in an export (cp left with no source).</summary>
    [Fact]
    public void BareCpScanner_DetectsArgumentlessCp()
    {
        var mk = "publish-x:\n\t@cp\n\t@cp $(OUT)/bench/\n\t@for f in a; do \\\n\t\tcp\\\n\tdone\n";
        var lines = RecipeLines(mk);
        Assert.Contains(lines, r => Segments(r.Line).Any(IsBareCp));
        Assert.False(IsBareCp("cp a b"));
        Assert.True(IsBareCp("cp -r"));
    }
}
