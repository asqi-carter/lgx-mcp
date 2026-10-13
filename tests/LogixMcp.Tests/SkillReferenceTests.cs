using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guard against phantom skill names and missing skill pointers: every
/// <c>lgx-*</c> skill name in agent-facing text must be a <c>skills/&lt;name&gt;/SKILL.md</c>
/// directory, and the lifecycle tool descriptions must point at their playbook.
/// Scan scope matches <see cref="ToolNameReferenceTests"/>: string literals / [Description] text
/// of src/**/*.cs (comments excluded), skills/**/SKILL.md, and README.md.
/// Source is scanned (not reflection) so SDK-lane tools are covered in a non-SDK build.
/// </summary>
public class SkillReferenceTests
{
    private static readonly Regex SkillNameRegex = new(@"\blgx-[a-z-]+\b", RegexOptions.Compiled);
    private static readonly Regex RegistrationRegex =
        new(@"McpServerTool\(\s*Name\s*=\s*""(logix_[a-z_]+)""", RegexOptions.Compiled);
    private static readonly Regex StringLiteralRegex =
        new(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    /// <summary>
    /// <c>lgx-*</c> tokens that are not skill names: <c>lgx-mcp</c> is the MCP server's own
    /// registration name (PingHandler.Name, <c>claude mcp add lgx-mcp</c> hints); <c>lgx-acd</c> is the
    /// temp-file prefix of the derived L5X that <c>AcdProjectOpener</c> writes (<c>lgx-acd-{slug}.L5X</c>).
    /// </summary>
    private static readonly HashSet<string> NonSkillTokens = new(StringComparer.Ordinal) { "lgx-mcp", "lgx-acd" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LogixMcp.sln not found above test base directory.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(f));

    /// <summary>Returns the line without comment-only lines and trailing // comments; null when skipped.</summary>
    private static string? StripComments(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return null;
        var idx = line.IndexOf(" // ", StringComparison.Ordinal);
        if (idx >= 0 && line.IndexOf('"', idx) < 0) return line[..idx];
        return line;
    }

    /// <summary>
    /// Every <c>lgx-[a-z-]+</c> token in agent-facing text must name an existing
    /// <c>skills/&lt;name&gt;/SKILL.md</c>.
    /// </summary>
    [Fact]
    public void EverySkillNameInAgentFacingText_Exists()
    {
        var root = RepoRoot();
        var skillsDir = Path.Combine(root, "skills");
        var existing = Directory.Exists(skillsDir)
            ? Directory.EnumerateDirectories(skillsDir)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(d => Path.GetFileName(d))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();
        Assert.NotEmpty(existing);

        var offenders = new List<string>();

        void Scan(string file, IEnumerable<string> lines)
        {
            var rel = Path.GetRelativePath(root, file);
            var n = 0;
            foreach (var raw in lines)
            {
                n++;
                var line = file.EndsWith(".cs", StringComparison.Ordinal) ? StripComments(raw) : raw;
                if (line is null) continue;
                foreach (Match m in SkillNameRegex.Matches(line))
                {
                    var name = m.Value.TrimEnd('-');
                    if (!existing.Contains(name) && !NonSkillTokens.Contains(name))
                        offenders.Add($"{rel}:{n}: {m.Value}");
                }
            }
        }

        foreach (var file in SourceFiles(root))
            Scan(file, File.ReadAllLines(file));

        if (Directory.Exists(skillsDir))
            foreach (var file in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories))
                Scan(file, File.ReadAllLines(file));

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
            Scan(readme, File.ReadAllLines(readme));

        Assert.True(offenders.Count == 0,
            "lgx-* names in agent-facing text with no skills/<name>/SKILL.md:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Concatenated string-literal text of the <c>[Description(...)]</c> attribute that follows the
    /// <c>[McpServerTool(Name = "toolName")]</c> registration, or null when not found.
    /// </summary>
    private static string? ToolDescription(string root, string toolName)
    {
        foreach (var file in SourceFiles(root))
        {
            var lines = File.ReadAllLines(file);
            var start = Array.FindIndex(lines, l =>
                RegistrationRegex.Match(l) is { Success: true } m && m.Groups[1].Value == toolName);
            if (start < 0) continue;

            var sb = new System.Text.StringBuilder();
            for (var i = start + 1; i < lines.Length; i++)
            {
                foreach (Match m in StringLiteralRegex.Matches(lines[i]))
                    sb.Append(m.Groups[1].Value.Replace("\\\"", "\""));
                if (Regex.IsMatch(lines[i], @"\)\]\s*$")) break;
            }
            return sb.ToString();
        }
        return null;
    }

    /// <summary>The core tools in the skill-hint set also point at their playbook.</summary>
    [Theory]
    [InlineData("logix_write", "lgx-write-gate")]
    [InlineData("logix_import_datatype", "lgx-import-datatype")]
    [InlineData("logix_connect", "lgx-troubleshoot-connection")]
    public void OtherToolDescriptions_NameTheirSkill(string tool, string skill) =>
        AssertDescriptionNamesSkill(tool, skill);

    private static void AssertDescriptionNamesSkill(string tool, string skill)
    {
        var description = ToolDescription(RepoRoot(), tool);
        Assert.True(description is not null, $"No [McpServerTool(Name = \"{tool}\")] registration found in src.");
        Assert.Contains($"logix_get_skill(\"{skill}\")", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The instruction-reference skill is a registered skill directory with valid frontmatter
    /// (name matches the directory, non-empty description), and the playbooks that should point at it do.
    /// </summary>
    [Fact]
    public void InstructionReferenceSkill_IsRegistered_WithValidFrontmatter_AndIsLinked()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "skills", "lgx-instruction-reference", "SKILL.md");
        Assert.True(File.Exists(path), "skills/lgx-instruction-reference/SKILL.md is missing.");

        var text = File.ReadAllText(path);
        var fm = Regex.Match(text, @"\A---\r?\n(.*?)\r?\n---\r?\n", RegexOptions.Singleline);
        Assert.True(fm.Success, "SKILL.md has no YAML frontmatter block.");
        Assert.Matches(@"(?m)^name:\s*lgx-instruction-reference\s*$", fm.Groups[1].Value);
        Assert.Matches(@"(?m)^description:\s*\S.{20,}$", fm.Groups[1].Value);
    }

    /// <summary>Lint messages that concern an instruction name the reference section, and the section exists.</summary>
    [Fact]
    public void LintRuleMessages_NameExistingInstructionReferenceSections()
    {
        var root = RepoRoot();
        var skill = File.ReadAllText(Path.Combine(root, "skills", "lgx-instruction-reference", "SKILL.md"));
        var headings = Regex.Matches(skill, @"(?m)^##\s+(.+?)\s*$").Select(m => m.Groups[1].Value).ToHashSet();
        var cited = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*Rule.cs", SearchOption.AllDirectories)
                     .Where(f => !IsBuildOutput(f)))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"lgx-instruction-reference, section \\""(.+?)\\"""))
                cited.Add(m.Groups[1].Value);
        Assert.True(cited.Count >= 5, "Expected lint rule messages to cite instruction-reference sections.");
        Assert.All(cited, c => Assert.Contains(c, headings));
    }
}
