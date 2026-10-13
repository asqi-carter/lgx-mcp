using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guard tests for the bundled skill playbooks embedded in the
/// <c>LogixMcp</c> assembly.
///
/// <list type="bullet">
///   <item>
///     <see cref="EverySkill_NameMatchesDirectory_AndHasDescription"/>:
///     asserts that each <c>SKILL.md</c> resource carries a non-empty YAML
///     <c>name</c> field that matches its parent directory name, and a
///     non-empty <c>description</c> field.
///   </item>
///   <item>
///     <see cref="EveryToolNameInSkills_IsARegisteredTool"/>:
///     asserts that every <c>logix_*</c> tool token referenced in any skill
///     body corresponds to a registered <c>[McpServerTool]</c> on the server,
///     catching playbooks that drift from the live tool surface.
///   </item>
/// </list>
///
/// <para>
/// Both tests read skill content exclusively from embedded assembly resources
/// (never from file-system paths) so they are safe to run in CI and from
/// single-file publish artifacts.
/// </para>
/// </summary>
public class SkillsGuardTests
{
    // ── Assembly handles ──────────────────────────────────────────────────────

    /// <summary>
    /// The core assembly that hosts the embedded <c>SKILL.md</c> resources
    /// and the majority of <c>[McpServerTool]</c>-decorated methods.
    /// </summary>
    private static readonly Assembly CoreAssembly = typeof(ConnectHandler).Assembly;

    /// <summary>
    /// The SDK adapter assembly — always present as a project reference; in
    /// non-SDK builds it compiles to a stub that only includes the non-conditional
    /// <see cref="SdkToolNames"/> registry.
    /// </summary>
    private static readonly Assembly SdkAdapterAssembly = typeof(SdkToolNames).Assembly;

    // ── Skill resource helpers ────────────────────────────────────────────────

    /// <summary>
    /// All manifest resource names in <see cref="CoreAssembly"/> that end with
    /// <c>SKILL.md</c>, sorted alphabetically.
    /// </summary>
    private static IReadOnlyList<string> EmbeddedSkillResources =>
        CoreAssembly
            .GetManifestResourceNames()
            .Where(n => n.EndsWith("SKILL.md", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Extracts the directory segment (the skill name as MSBuild normalised it)
    /// from a manifest resource name of the form
    /// <c>LogixMcp.skills.&lt;dir&gt;.SKILL.md</c>.
    ///
    /// <para>
    /// MSBuild converts hyphens to underscores when computing manifest resource
    /// names, so <c>lgx-import-datatype</c> becomes <c>lgx_import_datatype</c>
    /// in the resource name.
    /// </para>
    /// </summary>
    /// <param name="resourceName">
    /// Full manifest resource name, e.g.
    /// <c>LogixMcp.skills.lgx_import_datatype.SKILL.md</c>.
    /// </param>
    /// <returns>
    /// The directory segment with underscores, e.g. <c>lgx_import_datatype</c>.
    /// Returns an empty string when the resource name does not match the
    /// expected pattern.
    /// </returns>
    private static string DirectorySegmentFromResourceName(string resourceName)
    {
        // Expected pattern: LogixMcp.skills.<dir>.SKILL.md
        // Split on dots: ["LogixMcp", "skills", "<dir>", "SKILL", "md"]
        var parts = resourceName.Split('.');
        if (parts.Length >= 5
            && parts[0] == "LogixMcp"
            && parts[1] == "skills"
            && parts[^2] == "SKILL"
            && parts[^1] == "md")
        {
            // Directory may itself contain dots (unlikely but safe to handle):
            // all parts between index 2 and the final "SKILL.md" are the dir.
            return string.Join(".", parts[2..^2]);
        }

        return string.Empty;
    }

    // ── Registered-tool helpers ───────────────────────────────────────────────

    /// <summary>
    /// Returns all <c>[McpServerTool]</c>-registered tool names found in an
    /// assembly, by reflecting over every public method and reading the
    /// <c>Name</c> property of any <c>McpServerToolAttribute</c> present.
    /// </summary>
    /// <param name="assembly">Assembly to scan.</param>
    private static IEnumerable<string> ToolNamesFromAssembly(Assembly assembly)
    {
        IEnumerable<Type> types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Gracefully handle assemblies that can't resolve all type
            // dependencies (e.g. SdkAdapter without Rockwell DLLs present).
            types = ex.Types.OfType<Type>();
        }

        return types
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => new
            {
                Method   = m,
                ToolAttr = m.GetCustomAttributes()
                             .FirstOrDefault(a =>
                                 a.GetType().Name == "McpServerToolAttribute"),
            })
            .Where(x => x.ToolAttr != null)
            .Select(x =>
            {
                var nameVal = x.ToolAttr!.GetType()
                               .GetProperty("Name")
                               ?.GetValue(x.ToolAttr) as string;
                return nameVal ?? x.Method.Name;
            });
    }

    /// <summary>
    /// Collects the full set of valid tool names from all sources:
    /// <list type="bullet">
    ///   <item>Reflection on <see cref="CoreAssembly"/> (core tools).</item>
    ///   <item>Reflection on <see cref="SdkAdapterAssembly"/> (SDK tools when
    ///     compiled with <c>LOGIX_SDK</c>).</item>
    ///   <item><see cref="SdkToolNames.All"/> static registry (SDK tool names
    ///     always available, including in non-SDK builds where reflection on
    ///     the stub SdkAdapter assembly would find nothing).</item>
    /// </list>
    /// </summary>
    private static HashSet<string> GetAllRegisteredToolNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in ToolNamesFromAssembly(CoreAssembly))
            names.Add(n);

        foreach (var n in ToolNamesFromAssembly(SdkAdapterAssembly))
            names.Add(n);

        // Always include the static SdkToolNames registry so the guard passes
        // in non-SDK builds where LogixSdkTools is conditionally excluded.
        foreach (var n in SdkToolNames.All)
            names.Add(n);

        return names;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every embedded <c>SKILL.md</c> resource must satisfy two front-matter
    /// invariants:
    /// <list type="number">
    ///   <item>
    ///     The YAML <c>name</c> field must be non-empty and must equal the
    ///     skill's parent directory name (normalising hyphens and underscores
    ///     so that <c>lgx-import-datatype</c> and <c>lgx_import_datatype</c>
    ///     compare as equal).
    ///   </item>
    ///   <item>
    ///     The YAML <c>description</c> field must be non-empty.
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// Content is read exclusively from embedded assembly resources, not from
    /// file-system paths, so the test passes in CI and single-file publish
    /// environments where no <c>skills/</c> directory exists beside the binary.
    /// </para>
    /// </summary>
    [Fact]
    public void EverySkill_NameMatchesDirectory_AndHasDescription()
    {
        var resources = EmbeddedSkillResources;

        Assert.True(
            resources.Count > 0,
            $"No embedded SKILL.md resources found in assembly " +
            $"'{CoreAssembly.GetName().Name}'. " +
            $"Check the EmbeddedResource glob in LogixMcp.csproj.");

        var violations = new List<string>();

        foreach (var resourceName in resources)
        {
            using var stream = CoreAssembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                violations.Add(
                    $"{resourceName}: GetManifestResourceStream returned null.");
                continue;
            }

            using var reader = new System.IO.StreamReader(stream);
            var raw = reader.ReadToEnd();

            var detail = SkillsHandler.ParseSkillMarkdown(raw);

            var dirSegment = DirectorySegmentFromResourceName(resourceName);

            // Both sides normalised to underscores for a hyphen-agnostic comparison.
            var normDir  = dirSegment.Replace('-', '_');
            var normName = detail.Name.Replace('-', '_');

            if (string.IsNullOrWhiteSpace(detail.Name))
            {
                violations.Add(
                    $"{resourceName}: 'name' field is missing or empty in YAML front matter.");
            }
            else if (!string.Equals(normName, normDir, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{resourceName}: 'name' field '{detail.Name}' does not match " +
                    $"directory segment '{dirSegment}' " +
                    $"(normalised: '{normName}' vs '{normDir}').");
            }

            if (string.IsNullOrWhiteSpace(detail.Description))
            {
                violations.Add(
                    $"{resourceName}: 'description' field is missing or empty in " +
                    $"YAML front matter.");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"The following embedded skill resources have front-matter violations. " +
            $"Each SKILL.md must have a non-empty 'name' equal to its directory name " +
            $"and a non-empty 'description':\n" +
            string.Join("\n", violations.Select(v => $"  - {v}")));
    }

    /// <summary>
    /// Every <c>logix_*</c> tool token referenced in any embedded skill body
    /// must correspond to a registered <c>[McpServerTool]</c> on the server.
    ///
    /// <para>
    /// The guard catches playbooks that drift from the live tool surface
    /// (e.g. a skill still references <c>logix_old_tool</c> after it was
    /// renamed to <c>logix_new_tool</c>).  It does NOT catch the inverse —
    /// registered tools that have no skill coverage — which is intentional
    /// (many tools are lower-level utilities not covered by a playbook).
    /// </para>
    ///
    /// <para>
    /// Tool tokens are matched by the pattern <c>logix_[a-z_]+</c> in the
    /// playbook body (front matter already stripped by
    /// <see cref="SkillsHandler.ParseSkillMarkdown"/>).
    /// </para>
    ///
    /// <para>
    /// The registered-tool set is the union of:
    /// <list type="bullet">
    ///   <item>Reflection on the core <c>LogixMcp</c> assembly.</item>
    ///   <item>Reflection on <c>LogixMcp.SdkAdapter</c> (populated in SDK
    ///     builds; empty stub in non-SDK CI builds).</item>
    ///   <item><see cref="SdkToolNames.All"/> — a non-conditional static
    ///     registry of SDK-only tool names that ensures the guard passes in
    ///     non-SDK CI builds where <c>LogixSdkTools</c> is not compiled.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public void EveryToolNameInSkills_IsARegisteredTool()
    {
        var resources = EmbeddedSkillResources;

        Assert.True(
            resources.Count > 0,
            $"No embedded SKILL.md resources found in assembly " +
            $"'{CoreAssembly.GetName().Name}'. " +
            $"Check the EmbeddedResource glob in LogixMcp.csproj.");

        var registeredTools = GetAllRegisteredToolNames();

        var violations = new List<string>();

        foreach (var resourceName in resources)
        {
            using var stream = CoreAssembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var reader = new System.IO.StreamReader(stream);
            var raw = reader.ReadToEnd();

            // Parse to strip front matter; we check the body only.
            var detail = SkillsHandler.ParseSkillMarkdown(raw);

            var referencedToolNames = Regex
                .Matches(detail.Content, @"logix_[a-z_]+")
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            foreach (var toolName in referencedToolNames)
            {
                if (!registeredTools.Contains(toolName))
                {
                    violations.Add(
                        $"{resourceName}: '{toolName}' is referenced in the skill " +
                        $"body but is not a registered [McpServerTool].");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"The following skill playbooks reference tool names that do not exist " +
            $"as registered [McpServerTool] methods. Update the skill or restore the " +
            $"missing tool before merging:\n" +
            string.Join("\n", violations.Select(v => $"  - {v}")));
    }
}
