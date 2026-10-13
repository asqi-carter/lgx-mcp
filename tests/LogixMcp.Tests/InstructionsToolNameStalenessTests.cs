using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Staleness guard for <see cref="LogixTools.CanonicalInstructions"/>.
///
/// <para>
/// The MCP server's <c>instructions</c> field is always-resident text that
/// every session reads.  When a tool is renamed or removed, the instructions
/// string silently becomes wrong without this test.
/// </para>
///
/// <para>
/// The single test here extracts every <c>logix_*</c> token from
/// <see cref="LogixTools.CanonicalInstructions"/> and asserts that each one
/// corresponds to a method decorated with <c>[McpServerTool]</c> in the
/// tool assembly.  If a tool is renamed or removed, the test fails and the
/// author is prompted to update the instructions text before merging.
/// </para>
/// </summary>
public class InstructionsToolNameStalenessTests
{
    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The assembly that contains all MCP tool types (LogixTools, LogixTagTools,
    /// LogixProjectTools).  Matches the assembly used in ToolDescriptionTests.
    /// </summary>
    private static readonly Assembly ToolAssembly = typeof(LogixTagTools).Assembly;

    /// <summary>
    /// Discovers every public method decorated with <c>[McpServerTool]</c> and
    /// returns the canonical tool name (the value of the attribute's <c>Name</c>
    /// property, falling back to the method name when absent).
    /// </summary>
    private static HashSet<string> GetRegisteredToolNames()
    {
        return ToolAssembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => new
            {
                Method   = m,
                ToolAttr = m.GetCustomAttributes()
                             .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"),
            })
            .Where(x => x.ToolAttr != null)
            .Select(x =>
            {
                var nameVal = x.ToolAttr!.GetType()
                               .GetProperty("Name")
                               ?.GetValue(x.ToolAttr) as string;
                return nameVal ?? x.Method.Name;
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every tool name mentioned in <see cref="LogixTools.CanonicalInstructions"/>
    /// must exist in the registered tool set.
    ///
    /// <para>
    /// A tool name is any token matching <c>logix_[a-z_]+</c> in the instructions
    /// string.  This regex is intentionally conservative — it matches the naming
    /// convention used by every tool in the server and won't produce false
    /// positives from prose words.
    /// </para>
    /// </summary>
    [Fact]
    public void CanonicalInstructions_ToolNamesAllExistInRegisteredToolSet()
    {
        // Extract every logix_* token from the instructions string.
        var mentionedToolNames = Regex
            .Matches(LogixTools.CanonicalInstructions, @"logix_[a-z_]+")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        // There must be at least one tool name — a blank or prose-only
        // instructions string would silently pass this test otherwise.
        Assert.NotEmpty(mentionedToolNames);

        var registeredTools = GetRegisteredToolNames();

        var missing = mentionedToolNames
            .Where(n => !registeredTools.Contains(n))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"The following tool names appear in LogixTools.CanonicalInstructions " +
            $"but are NOT registered in the tool assembly. " +
            $"Update the instructions text or restore the missing tools before merging:\n" +
            string.Join("\n", missing.Select(n => $"  - {n}")));
    }

    /// <summary>
    /// The instructions string must contain at least the four orientation tool
    /// names that the MCP Development Foundations §3 requires:
    /// <c>logix_open_project</c>, <c>logix_connect</c>,
    /// <c>logix_project_summary</c>, and <c>logix_gate_report</c>.
    ///
    /// <para>
    /// This test is a companion to the staleness test above: it prevents the
    /// instructions string from being shortened to the point where orientation
    /// information disappears.
    /// </para>
    /// </summary>
    [Fact]
    public void CanonicalInstructions_ContainsRequiredOrientationToolNames()
    {
        var required = new[]
        {
            "logix_open_project",
            "logix_connect",
            "logix_project_summary",
            "logix_gate_report",
        };

        var missing = required
            .Where(n => !LogixTools.CanonicalInstructions.Contains(n, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"The following required orientation tool names are absent from " +
            $"LogixTools.CanonicalInstructions. The §3 instructions field must " +
            $"cover the canonical sequence and the two orientation tools:\n" +
            string.Join("\n", missing.Select(n => $"  - {n}")));
    }

    /// <summary>
    /// The instructions must name <c>logix_list_skills</c>
    /// so that an agent discovering available playbooks calls the correct tool.
    ///
    /// <para>
    /// The instructions direct agents to call <c>logix_list_skills</c> before
    /// proposing rung changes or when a write is denied, ensuring cold agents
    /// discover the relevant playbook from the always-resident orientation text
    /// rather than only from <c>logix_list_skills</c>'s own tool description.
    /// </para>
    /// </summary>
    [Fact]
    public void CanonicalInstructions_ContainsLogixListSkills()
    {
        Assert.True(
            LogixTools.CanonicalInstructions.Contains(
                "logix_list_skills", StringComparison.OrdinalIgnoreCase),
            "LogixTools.CanonicalInstructions must contain 'logix_list_skills'. " +
            "The skill-discovery contract requires the instructions to direct agents to call " +
            "logix_list_skills before proposing rung edits or when a write is denied.");
    }

    /// <summary>
    /// The instructions must name <c>logix_get_skill</c>
    /// so that an agent loading a playbook body calls the correct follow-up tool.
    ///
    /// <para>
    /// The instructions pair <c>logix_list_skills</c> (discovery) with
    /// <c>logix_get_skill</c> (retrieval), ensuring cold agents know the full
    /// two-step lookup sequence from the always-resident orientation text.
    /// </para>
    /// </summary>
    [Fact]
    public void CanonicalInstructions_ContainsLogixGetSkill()
    {
        Assert.True(
            LogixTools.CanonicalInstructions.Contains(
                "logix_get_skill", StringComparison.OrdinalIgnoreCase),
            "LogixTools.CanonicalInstructions must contain 'logix_get_skill'. " +
            "The skill-discovery contract requires the instructions to direct agents to call " +
            "logix_get_skill (after logix_list_skills) to load the matching playbook.");
    }
}
