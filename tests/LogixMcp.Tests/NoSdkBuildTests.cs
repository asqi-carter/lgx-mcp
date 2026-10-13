using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Source-scan regression guards for the no-SDK Linux build (epic "Restore the no-SDK build and gate it in make test"). Compiled in both
/// lanes: it reads source text only and never touches the Logix SDK.
/// </summary>
public class NoSdkBuildTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LogixMcp.sln not found above test base directory.");
    }

    // Declarations logix_doctor needs in BOTH lanes. Studio*Kind constants are matched only
    // when initialised with a string literal (the canonical definitions in StudioKinds.cs);
    // forwarding aliases such as StudioLifecycleHandler.StudioOfflineKind = StudioKinds.X are
    // legitimately SDK-only.
    private static readonly (string Name, Regex Pattern)[] DoctorDeclarations =
    {
        ("IEnvironmentProbe",  new Regex(@"\binterface\s+IEnvironmentProbe\b")),
        ("EnvironmentProbe",   new Regex(@"\bclass\s+EnvironmentProbe\b")),
        ("Studio*Kind",        new Regex(@"\bconst\s+string\s+Studio\w*Kind\s*=\s*""")),
    };

    /// <summary>
    /// Returns the 1-based line numbers of lines matching <paramref name="pattern"/> that sit
    /// inside an <c>#if LOGIX_SDK</c> (or <c>#else</c> of <c>#if !LOGIX_SDK</c>) region.
    /// </summary>
    internal static List<int> LinesInsideSdkGuard(string source, Regex pattern)
    {
        var hits = new List<int>();
        // kind: +1 = #if mentions LOGIX_SDK, -1 = mentions !LOGIX_SDK, 0 = unrelated.
        var stack = new Stack<(int Kind, bool InElse)>();
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#if"))
            {
                int kind = !line.Contains("LOGIX_SDK") ? 0 : line.Contains("!LOGIX_SDK") ? -1 : 1;
                stack.Push((kind, false));
            }
            else if (line.StartsWith("#elif") && stack.Count > 0)
            {
                var (kind, _) = stack.Pop();
                // An #elif branch is never treated as the plain SDK branch; re-evaluate its own condition.
                int k = !line.Contains("LOGIX_SDK") ? 0 : line.Contains("!LOGIX_SDK") ? -1 : 1;
                stack.Push((k != 0 ? k : kind == 1 ? -1 : 0, false));
            }
            else if (line.StartsWith("#else") && stack.Count > 0)
            {
                var (kind, _) = stack.Pop();
                stack.Push((kind, true));
            }
            else if (line.StartsWith("#endif") && stack.Count > 0)
            {
                stack.Pop();
            }
            else if (pattern.IsMatch(lines[i]) && stack.Any(f => (f.Kind == 1 && !f.InElse) || (f.Kind == -1 && f.InElse)))
            {
                hits.Add(i + 1);
            }
        }
        return hits;
    }

    /// <summary>
    /// IEnvironmentProbe, EnvironmentProbe and the Studio*Kind constants are consumed by
    /// logix_doctor, which compiles in the non-SDK build, so none of their declarations may be
    /// inside an <c>#if LOGIX_SDK</c> block.
    /// </summary>
    [Fact]
    public void DoctorTypes_NotInsideLogixSdkGuard()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var violations = new List<string>();
        foreach (var (name, pattern) in DoctorDeclarations)
        {
            var declaring = files.Where(f => pattern.IsMatch(File.ReadAllText(f))).ToList();
            Assert.True(declaring.Count > 0, $"No source file declares {name}; the scan is stale.");
            foreach (var file in declaring)
                foreach (int line in LinesInsideSdkGuard(File.ReadAllText(file), pattern))
                    violations.Add($"{name} declared inside #if LOGIX_SDK at {Path.GetRelativePath(RepoRoot(), file)}:{line}");
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>The scanner itself must flag a declaration wrapped in <c>#if LOGIX_SDK</c>.</summary>
    [Fact]
    public void DoctorTypes_ScannerDetectsDeclarationInsideGuard()
    {
        var pattern = DoctorDeclarations[0].Pattern;
        const string guarded = "#if LOGIX_SDK\npublic interface IEnvironmentProbe\n{\n}\n#endif\n";
        const string unguarded = "#if LOGIX_SDK\n#endif\npublic interface IEnvironmentProbe\n{\n}\n";
        const string elseBranch = "#if LOGIX_SDK\n#else\npublic interface IEnvironmentProbe { }\n#endif\n";

        Assert.Equal(new[] { 2 }, LinesInsideSdkGuard(guarded, pattern));
        Assert.Empty(LinesInsideSdkGuard(unguarded, pattern));
        Assert.Empty(LinesInsideSdkGuard(elseBranch, pattern));
    }
}
