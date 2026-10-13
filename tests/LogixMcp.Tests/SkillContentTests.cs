using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Content guards for the <c>lgx-instruction-reference</c> skill: one heading per instruction
/// group, and every fault mention carries both a type and a code (<c>type/code</c>, e.g. <c>4/20</c>).
/// </summary>
public class SkillContentTests
{
    private static string SkillText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("LogixMcp.sln not found above test base directory.");
        return File.ReadAllText(Path.Combine(root, "skills", "lgx-instruction-reference", "SKILL.md"));
    }

    /// <summary>A heading per group, each naming every instruction of the group.</summary>
    [Fact]
    public void InstructionReference_HasEverySectionListed()
    {
        var groups = new[]
        {
            new[] { "XIC", "XIO", "OTE", "OTL", "OTU", "ONS", "OSR", "OSF" },
            new[] { "TON", "TOF", "RTO", "RES" },
            new[] { "CTU", "CTD", "RES" },
            new[] { "EQ", "NE", "LT", "LE", "GT", "GE", "LIMIT" },
            new[] { "ADD", "SUB", "MUL", "DIV", "MOD", "CPT", "MOVE", "COP", "CPS", "CLR" },
            new[] { "JSR", "RET", "SBR", "JMP", "LBL", "AFI", "NOP" },
            new[] { "Arrays" },
            new[] { "Versions" },
        };

        var headings = SkillText().Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .ToList();

        foreach (var group in groups)
        {
            var match = headings.FirstOrDefault(h =>
                group.All(name => Regex.IsMatch(h, $@"\b{name}\b", RegexOptions.IgnoreCase)));
            Assert.True(match is not null, $"No '## ' heading names all of: {string.Join(", ", group)}");
        }

        // Each section also cites the Rockwell publication by title.
        var sections = Regex.Split(SkillText(), @"(?m)^## ").Skip(1).ToList();
        Assert.Equal(groups.Length, sections.Count);
        foreach (var section in sections)
            Assert.Contains("1756-RM003", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every line that mentions a fault must contain a <c>type/code</c> pair (digits/digits).
    /// The publication title "Major, Minor and I/O Faults" (1756-PM014) is not a fault mention.
    /// </summary>
    [Fact]
    public void InstructionReference_EveryFaultMentionHasTypeAndCode()
    {
        var offenders = new List<string>();
        var n = 0;
        foreach (var raw in SkillText().Split('\n'))
        {
            n++;
            var line = raw.Replace("Major, Minor and I/O Faults", "", StringComparison.Ordinal);
            if (!Regex.IsMatch(line, @"\bfault", RegexOptions.IgnoreCase)) continue;
            if (!Regex.IsMatch(line, @"\b\d+/\d+\b"))
                offenders.Add($"line {n}: {raw.Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "Fault mentions without a type/code:\n" + string.Join("\n", offenders));
    }

    // ── No-SDK skills cite only what this build registers ──────────────────────

    private static readonly Assembly CoreAssembly = typeof(ConnectHandler).Assembly;

    /// <summary>Embedded skills a no-SDK binary offers: every embedded <c>SKILL.md</c> except the SDK-lane ones.</summary>
    private static IReadOnlyDictionary<string, string> NoSdkSkills()
    {
        var sdkOnly = SkillsToolTests.SdkOnlySkillNames.Select(n => n.Replace('-', '_')).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in CoreAssembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".SKILL.md", StringComparison.Ordinal)))
        {
            if (sdkOnly.Any(n => resource.Contains(n, StringComparison.Ordinal))) continue;
            using var reader = new StreamReader(CoreAssembly.GetManifestResourceStream(resource)!);
            result[resource] = reader.ReadToEnd();
        }
        return result;
    }

    /// <summary>
    /// Tool names registered by this build: reflection over <c>[McpServerTool]</c> in the core assembly and the
    /// SDK adapter assembly. <c>SdkToolNames.All</c> is deliberately NOT used: it lists SDK tools even in a
    /// build that does not register them, which is exactly the drift this test exists to catch.
    /// </summary>
    private static HashSet<string> RegisteredToolNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in new[] { CoreAssembly, typeof(SdkToolNames).Assembly })
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
            foreach (var method in types.SelectMany(t => t.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)))
            {
                var attr = method.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr is null) continue;
                names.Add((attr.GetType().GetProperty("Name")?.GetValue(attr) as string) ?? method.Name);
            }
        }
        return names;
    }

    /// <summary>Every <c>kind</c> value the server can return, plus the gate's deny reasons the skills also name.</summary>
    private static HashSet<string> KnownKindsAndReasons()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in new[] { typeof(ErrorKinds), typeof(MnemonicCheckKinds) }
                     .SelectMany(k => k.GetFields(BindingFlags.Public | BindingFlags.Static))
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            known.Add((string)f.GetRawConstantValue()!);
        foreach (var n in Enum.GetNames<TagServiceErrorKind>()) known.Add(n);
        foreach (var n in Enum.GetNames<DenyReason>()) known.Add(n);
        return known;
    }

    /// <summary>
    /// Every embedded skill of the no-SDK build cites only tools this build registers, only error kinds (or
    /// gate deny reasons) that exist in <see cref="ErrorKinds"/> / <see cref="TagServiceErrorKind"/> /
    /// <see cref="DenyReason"/>, and only skills that are embedded too.
    /// </summary>
    [Fact]
    public void NoSdkSkills_CiteOnlyRegisteredToolsAndKinds()
    {
        var skills = NoSdkSkills();
        Assert.NotEmpty(skills);

        var tools = RegisteredToolNames();
        var known = KnownKindsAndReasons();
        var embeddedSkillNames = skills.Keys
            .Select(k => Regex.Match(k, @"^LogixMcp\.skills\.(.+)\.SKILL\.md$").Groups[1].Value.Replace('_', '-'))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        var kindCitations = 0;
        foreach (var (resource, text) in skills)
        {
            foreach (Match m in Regex.Matches(text, @"\blogix_[a-z][a-z_]*[a-z]\b"))
                if (!tools.Contains(m.Value))
                    offenders.Add($"{resource}: tool '{m.Value}' is not registered in this build");

            foreach (Match m in Regex.Matches(text, @"`([A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+)`"))
            {
                kindCitations++;
                if (!known.Contains(m.Groups[1].Value))
                    offenders.Add($"{resource}: kind '{m.Groups[1].Value}' does not exist in ErrorKinds / TagServiceErrorKind / DenyReason");
            }

            foreach (Match m in Regex.Matches(text, @"\blgx-[a-z]+(?:-[a-z]+)*\b"))
                if (m.Value != "lgx-mcp" && !embeddedSkillNames.Contains(m.Value))
                    offenders.Add($"{resource}: skill '{m.Value}' is not embedded in this build");
        }

        Assert.True(kindCitations > 0, "No error kinds were found in any skill: the kind pattern has gone stale.");
        Assert.True(offenders.Count == 0, "No-SDK skills cite things this build does not have:\n" + string.Join("\n", offenders));
    }

    /// <summary>The three skills that must reference only CIP / L5X name no SDK vocabulary at all.</summary>
    [Theory]
    [InlineData("lgx_troubleshoot_connection")]
    [InlineData("lgx_write_gate")]
    [InlineData("lgx_import_datatype")]
    public void CipL5xSkills_DoNotMentionTheSdkLane(string dir)
    {
        var (resource, text) = NoSdkSkills().Single(kv => kv.Key.Contains(dir, StringComparison.Ordinal));
        foreach (var banned in new[] { "backend=\"sdk\"", "sdkMode", "sdkCommPath", "LdSdkService", "SdkServiceDegraded",
                                       "logix_propose_rung_edit", "logix_run_test", "logix_pending_edits", "PartialImport" })
            Assert.False(text.Contains(banned, StringComparison.Ordinal), $"{resource} mentions '{banned}'.");
    }
}
