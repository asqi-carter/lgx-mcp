using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guard against phantom tool names: every <c>logix_*</c> tool name that appears
/// in agent-facing text must be registered via <c>[McpServerTool(Name = "...")]</c>.
/// Registered names are collected from source (not reflection) so the SDK-lane tools are
/// covered even in a non-SDK build.
/// </summary>
public class ToolNameReferenceTests
{
    private static readonly Regex ToolNameRegex = new(@"\blogix_[a-z_]+\b", RegexOptions.Compiled);
    private static readonly Regex RegistrationRegex =
        new(@"McpServerTool\(\s*Name\s*=\s*""(logix_[a-z_]+)""", RegexOptions.Compiled);

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
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return null; // code and /// doc comments
        var idx = line.IndexOf(" // ", StringComparison.Ordinal);
        if (idx >= 0 && line.IndexOf('"', idx) < 0) return line[..idx];
        return line;
    }

    /// <summary>
    /// Every <c>logix_*</c> name in string literals / [Description] text of src/**/*.cs,
    /// skills/**/SKILL.md, and README.md must be a registered tool name. XML doc comments
    /// and code comments are excluded (they may name history).
    /// </summary>
    [Fact]
    public void EveryLogixToolNameInAgentFacingText_IsRegistered()
    {
        var root = RepoRoot();

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(root))
            foreach (Match m in RegistrationRegex.Matches(File.ReadAllText(file)))
                registered.Add(m.Groups[1].Value);
        // The SDK-lane tools are registered in sources that only the SDK lane compiles; SdkToolNames
        // is their non-conditional name registry, so a name listed there counts as registered.
        registered.UnionWith(SdkToolNames.All);
        Assert.NotEmpty(registered);

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
                foreach (Match m in ToolNameRegex.Matches(line))
                    if (!registered.Contains(m.Value))
                        offenders.Add($"{rel}:{n}: {m.Value}");
            }
        }

        foreach (var file in SourceFiles(root))
            Scan(file, File.ReadAllLines(file));

        var skillsDir = Path.Combine(root, "skills");
        if (Directory.Exists(skillsDir))
            foreach (var file in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories))
                Scan(file, File.ReadAllLines(file));

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
            Scan(readme, File.ReadAllLines(readme));

        Assert.True(offenders.Count == 0,
            "Unregistered logix_* tool names in agent-facing text:\n" + string.Join("\n", offenders));
    }

    private static readonly Regex StudioOnlyAcceptRegex = new(
        @"accepting\s+is\s+studio[-\s]only"
        + @"|studio[-\s]only\b.{0,60}\b(accept|finali[sz])"
        + @"|\b(accept|finali[sz])\w*\b.{0,60}studio[-\s]only",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Accepting pending edits is available through <c>logix_accept_pending_edits</c> (behind the
    /// Studio-automation flag), so no agent-facing text may claim that accepting/finalizing is
    /// "Studio-only". Same scan scope as <see cref="EveryLogixToolNameInAgentFacingText_IsRegistered"/>:
    /// src/**/*.cs (comments excluded), skills/**/SKILL.md, README.md. This is a line-based text
    /// guard: a claim split across lines or built by concatenation will not be caught.
    /// </summary>
    [Fact]
    public void AgentFacingText_HasNoStudioOnlyAcceptClaim()
    {
        var root = RepoRoot();
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
                if (StudioOnlyAcceptRegex.IsMatch(line))
                    offenders.Add($"{rel}:{n}: {line.Trim()}");
            }
        }

        foreach (var file in SourceFiles(root))
            Scan(file, File.ReadAllLines(file));

        var skillsDir = Path.Combine(root, "skills");
        if (Directory.Exists(skillsDir))
            foreach (var file in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories))
                Scan(file, File.ReadAllLines(file));

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
            Scan(readme, File.ReadAllLines(readme));

        Assert.True(offenders.Count == 0,
            "Agent-facing text claims accepting is Studio-only:\n" + string.Join("\n", offenders));
    }

    /// <summary>The guard regex flags the known bad phrasings and not benign text.</summary>
    [Theory]
    [InlineData("Note: accepting is Studio-only.", true)]
    [InlineData("Finalize is Studio only", true)]
    [InlineData("Studio-only: you must accept the edits", true)]
    [InlineData("Use logix_accept_pending_edits to accept pending edits.", false)]
    [InlineData("Opening a project is Studio-only.", false)]
    public void StudioOnlyAcceptRegex_MatchesExpectedPhrases(string text, bool expected) =>
        Assert.Equal(expected, StudioOnlyAcceptRegex.IsMatch(text));

    private static readonly Regex EnvelopeOpenTagRegex =
        new(@"<RSLogix5000Content\b[^>]*>?", RegexOptions.Compiled);

    /// <summary>
    /// No agent-facing string may show an <c>&lt;RSLogix5000Content</c> opening tag without a
    /// <c>SchemaRevision</c> attribute: an envelope-less example teaches agents to hand-write an
    /// envelope the SDK importer rejects. Same scan scope as
    /// <see cref="EveryLogixToolNameInAgentFacingText_IsRegistered"/>. Line-based guard.
    /// </summary>
    [Fact]
    public void AgentFacingText_HasNoEnvelopeWithoutSchemaRevision()
    {
        var root = RepoRoot();
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
                foreach (Match m in EnvelopeOpenTagRegex.Matches(line))
                    if (!m.Value.Contains("SchemaRevision", StringComparison.Ordinal)
                        && !line.AsSpan(m.Index).StartsWith("<RSLogix5000Content SchemaRevision", StringComparison.Ordinal))
                        offenders.Add($"{rel}:{n}: {line.Trim()}");
            }
        }

        foreach (var file in SourceFiles(root))
            Scan(file, File.ReadAllLines(file));

        var skillsDir = Path.Combine(root, "skills");
        if (Directory.Exists(skillsDir))
            foreach (var file in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories))
                Scan(file, File.ReadAllLines(file));

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
            Scan(readme, File.ReadAllLines(readme));

        Assert.True(offenders.Count == 0,
            "Agent-facing text shows an RSLogix5000Content envelope without SchemaRevision:\n"
            + string.Join("\n", offenders));
    }

    private static readonly Regex TestedUntestedIndistinguishableRegex = new(
        @"tested\s+and\s+untested\s+(the\s+same|alike|look\s+the\s+same)"
        + @"|(cannot|can't|can\s+not|unable\s+to)\s+(tell|distinguish)\s+tested"
        + @"|(does\s+not|doesn't|do\s+not|don't)\s+distinguish\s+tested",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The program export's <c>TestEdits</c> attribute (surfaced as <c>zoneState</c>) does tell tested
    /// from untested, so no agent-facing text may claim they are indistinguishable. Same scan
    /// scope as <see cref="EveryLogixToolNameInAgentFacingText_IsRegistered"/>: src/**/*.cs (comments
    /// excluded), skills/**/SKILL.md, README.md. Line-based guard.
    /// </summary>
    [Fact]
    public void AgentFacingText_HasNoTestedUntestedIndistinguishableClaim()
    {
        var root = RepoRoot();
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
                if (TestedUntestedIndistinguishableRegex.IsMatch(line))
                    offenders.Add($"{rel}:{n}: {line.Trim()}");
            }
        }

        foreach (var file in SourceFiles(root))
            Scan(file, File.ReadAllLines(file));

        var skillsDir = Path.Combine(root, "skills");
        if (Directory.Exists(skillsDir))
            foreach (var file in Directory.EnumerateFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories))
                Scan(file, File.ReadAllLines(file));

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
            Scan(readme, File.ReadAllLines(readme));

        Assert.True(offenders.Count == 0,
            "Agent-facing text claims tested and untested are indistinguishable:\n" + string.Join("\n", offenders));
    }

    /// <summary>The guard regex flags the known bad phrasings and not benign text.</summary>
    [Theory]
    [InlineData("The export shows tested and untested the same (both I).", true)]
    [InlineData("the export cannot tell tested from untested", true)]
    [InlineData("The SDK export does not distinguish tested from untested", true)]
    [InlineData("Read zoneState to tell tested from untested.", false)]
    [InlineData("Assemble on an untested zone returns NotTested.", false)]
    public void TestedUntestedIndistinguishableRegex_MatchesExpectedPhrases(string text, bool expected) =>
        Assert.Equal(expected, TestedUntestedIndistinguishableRegex.IsMatch(text));

    /// <summary>
    /// <c>logix_doctor</c> is a registered tool, registered from the core assembly
    /// and therefore not listed in <see cref="SdkToolNames.All"/>.
    /// </summary>
    [Fact]
    public void Doctor_IsARegisteredTool_FromCore_AndNotInSdkToolNames()
    {
        var root = RepoRoot();
        var found = SourceFiles(root).Any(f =>
            RegistrationRegex.Matches(File.ReadAllText(f)).Any(m => m.Groups[1].Value == "logix_doctor"));
        Assert.True(found);
        Assert.DoesNotContain("logix_doctor", SdkToolNames.All);
    }
}
