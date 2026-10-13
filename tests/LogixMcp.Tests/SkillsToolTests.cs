using System.Reflection;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Regression gate: asserts that every <c>skills/*/SKILL.md</c> playbook is
/// embedded as a manifest resource in the <c>LogixMcp</c> assembly.
///
/// <para>
/// Skill content must be embedded so that the single-file publish artifact
/// carries all playbooks without any runtime file-path lookup that can fail.
/// A file lookup would break when the binary is run from
/// a directory that does not contain a <c>skills/</c> tree — exactly the
/// expected deployment topology.
/// </para>
///
/// <para>
/// Resource naming: MSBuild embeds each <c>skills/&lt;name&gt;/SKILL.md</c>
/// under the virtual <c>Link</c> path <c>skills\&lt;name&gt;\SKILL.md</c>
/// (set in <c>LogixMcp.csproj</c>), which the SDK translates to the manifest
/// resource name <c>LogixMcp.skills.&lt;name&gt;.SKILL.md</c>.
/// </para>
/// </summary>
public class SkillsToolTests
{
    /// <summary>
    /// The assembly under test — resolved via a type that lives in it so no
    /// hard-coded path is required.
    /// </summary>
    private static readonly Assembly CoreAssembly = typeof(ConnectHandler).Assembly;

    /// <summary>
    /// All manifest resource names in the core assembly that end with
    /// <c>SKILL.md</c>, representing the embedded skill playbooks.
    /// </summary>
    private static readonly IReadOnlyList<string> EmbeddedSkillResources =
        CoreAssembly
            .GetManifestResourceNames()
            .Where(n => n.EndsWith("SKILL.md", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The skill directories that must be present in the assembly
    /// manifest.  These names are derived from the <c>skills/*/</c>
    /// subdirectory names checked into the repository.
    /// </summary>
    private static readonly IReadOnlyList<string> ExpectedSkillNames =
    [
        "lgx-explain-logic",
        "lgx-import-datatype",
        "lgx-instruction-reference",
#if LOGIX_SDK
        // SDK-lane skills: embedded only when EnableLogixSdk=true (see LogixMcp.csproj).
        "lgx-propose-rung-change",
        "lgx-read-pending-state",
        "lgx-test-logic",
#endif
        "lgx-troubleshoot-connection",
        "lgx-write-gate",
    ];

    /// <summary>
    /// Skills that drive SDK-adapter tools. They are not part of the no-SDK build and must not be embedded
    /// (or listed by <c>logix_list_skills</c>) when <c>EnableLogixSdk=false</c>.
    /// </summary>
    internal static readonly IReadOnlyList<string> SdkOnlySkillNames =
    [
        "lgx-propose-rung-change",
        "lgx-read-pending-state",
        "lgx-test-logic",
    ];

    /// <summary>
    /// Asserts that every <c>skills/*/SKILL.md</c> file is present as an
    /// embedded resource in the compiled assembly and that no runtime file
    /// lookup is needed to access skill content.
    ///
    /// <para>
    /// This test fails if the <c>EmbeddedResource</c> glob in
    /// <c>LogixMcp.csproj</c> is removed or narrowed so that a playbook is
    /// missing from the manifest, guarding against the class of
    /// defect (green tests, dead artifact).
    /// </para>
    ///
    /// <para>
    /// Note: MSBuild normalises hyphens in directory names to underscores
    /// when computing manifest resource names, so
    /// <c>lgx-import-datatype</c> becomes <c>lgx_import_datatype</c> in the
    /// resource name.  The lookup below normalises both sides to underscores
    /// before comparing.
    /// </para>
    /// </summary>
    [Fact]
    public void Skills_AreEmbeddedResources_NotFileLookups()
    {
        // MSBuild converts hyphens to underscores in manifest resource names;
        // normalise both the expected names and the actual resource names so
        // the comparison is hyphen/underscore-agnostic.
        var normalised = EmbeddedSkillResources
            .Select(r => r.Replace('-', '_'))
            .ToList();

        // Verify that each expected skill name appears in the manifest.
        var missing = ExpectedSkillNames
            .Where(name => !normalised.Any(r =>
                r.Contains(name.Replace('-', '_'), StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"The following skill playbooks were NOT found as embedded resources " +
            $"in assembly '{CoreAssembly.GetName().Name}'. " +
            $"Add or restore the EmbeddedResource glob in LogixMcp.csproj " +
            $"(Include=\"..\\..\\skills\\**\\SKILL.md\") so the single-file " +
            $"publish carries all skill content without runtime path lookups. " +
            $"Missing: {string.Join(", ", missing)}. " +
            $"Present resources ending in SKILL.md: " +
            $"{string.Join(", ", EmbeddedSkillResources)}.");

        // Verify the total count matches so new skill directories are noticed.
        Assert.True(
            EmbeddedSkillResources.Count >= ExpectedSkillNames.Count,
            $"Expected at least {ExpectedSkillNames.Count} embedded SKILL.md " +
            $"resources but found {EmbeddedSkillResources.Count}. " +
            $"All resources ending in SKILL.md: " +
            $"{string.Join(", ", EmbeddedSkillResources)}.");
    }

    /// <summary>
    /// Asserts that each embedded skill resource stream can be opened and
    /// yields non-empty content, confirming that the embed is valid and the
    /// playbook text is actually present in the assembly.
    /// </summary>
    [Fact]
    public void Skills_EmbeddedContent_IsReadable()
    {
        Assert.True(
            EmbeddedSkillResources.Count > 0,
            $"No embedded SKILL.md resources found in assembly " +
            $"'{CoreAssembly.GetName().Name}'. " +
            $"Check the EmbeddedResource glob in LogixMcp.csproj.");

        foreach (var resourceName in EmbeddedSkillResources)
        {
            using var stream = CoreAssembly.GetManifestResourceStream(resourceName);
            Assert.True(
                stream is not null,
                $"GetManifestResourceStream returned null for '{resourceName}'. " +
                $"The resource is listed in GetManifestResourceNames() but cannot " +
                $"be opened — this should not happen for a valid EmbeddedResource.");

            Assert.True(
                stream.Length > 0,
                $"Embedded resource '{resourceName}' is empty. " +
                $"The corresponding SKILL.md file may be empty or was not " +
                $"written to the assembly correctly.");
        }
    }

    // ── SkillsHandler integration tests ───────────────────────────────────────

    /// <summary>
    /// Asserts that <see cref="SkillsHandler.ListSkills"/> returns exactly the five
    /// bundled skill entries, that they are sorted alphabetically by name, and that
    /// each entry carries a non-empty description.
    /// </summary>
    [Fact]
    public void ListSkills_ReturnsAllFiveBundledSkills_SortedByName()
    {
        var response = SkillsHandler.ListSkills();
        var skills   = response.Skills;

        // Correct count.
        Assert.Equal(ExpectedSkillNames.Count, skills.Count);

        // Sorted by name.
        var names       = skills.Select(s => s.Name).ToList();
        var sortedNames = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(sortedNames, names);

        // All expected skill names present.
        foreach (var expected in ExpectedSkillNames)
            Assert.Contains(expected, names);

        // Each entry carries a non-empty description.
        foreach (var skill in skills)
            Assert.False(
                string.IsNullOrWhiteSpace(skill.Description),
                $"Skill '{skill.Name}' has an empty description.");
    }

    /// <summary>
    /// Asserts that <see cref="SkillsHandler.GetSkill"/> for a known name returns a
    /// <see cref="SkillDetail"/> whose <c>content</c> contains the playbook body and
    /// does NOT contain any YAML front-matter delimiters (<c>---</c>) or front-matter
    /// field lines (<c>name:</c>, <c>description:</c>).
    /// </summary>
    [Fact]
    public void GetSkill_ReturnsBodyWithoutFrontMatter()
    {
        // Use the first known skill as a representative sample.
        var skillName = "lgx-import-datatype";
        var raw = SkillsHandler.GetSkill(skillName);

        var detail = Assert.IsType<SkillDetail>(raw);

        Assert.Equal(skillName, detail.Name);
        Assert.False(string.IsNullOrWhiteSpace(detail.Description),
            "Description should be non-empty.");
        Assert.False(string.IsNullOrWhiteSpace(detail.Content),
            "Content should be non-empty.");

        // Front-matter delimiters and field lines must be stripped.
        var lines = detail.Content.Split('\n');

        Assert.False(
            lines.Any(l => l.Trim() == "---"),
            "Content must not contain YAML front-matter delimiter '---'.");

        // The first line of front matter (name: ...) and second (description: ...)
        // must not appear verbatim in the body.
        Assert.False(
            lines.Any(l => l.TrimStart().StartsWith("name:", StringComparison.Ordinal)),
            "Content must not contain a 'name:' front-matter field.");

        Assert.False(
            lines.Any(l => l.TrimStart().StartsWith("description:", StringComparison.Ordinal)),
            "Content must not contain a 'description:' front-matter field.");
    }

    /// <summary>
    /// Asserts that <see cref="SkillsHandler.GetSkill"/> for an unrecognised name
    /// returns a <see cref="SkillNotFoundResponse"/> with <c>kind == "SkillNotFound"</c>
    /// and a <c>validNames</c> list that includes all five bundled skill names.
    /// </summary>
    [Fact]
    public void GetSkill_UnknownName_ReturnsSkillNotFound_WithValidNames()
    {
        const string bogus = "lgx-does-not-exist";
        var raw = SkillsHandler.GetSkill(bogus);

        var notFound = Assert.IsType<SkillNotFoundResponse>(raw);

        Assert.Equal("SkillNotFound", notFound.Kind);
        Assert.Equal(bogus, notFound.RequestedName);

        // The valid-names list must include all five bundled skills.
        foreach (var expected in ExpectedSkillNames)
            Assert.Contains(expected, notFound.ValidNames);
    }

#if !LOGIX_SDK
    /// <summary>
    /// The no-SDK binary offers no SDK skill: <c>logix_list_skills</c> omits them, <c>logix_get_skill</c>
    /// answers <c>SkillNotFound</c>, and no SDK-skill resource is embedded.
    /// </summary>
    [Fact]
    public void NoSdkBuild_EmbedsAndListsNoSdkSkills()
    {
        var listed = SkillsHandler.ListSkills().Skills.Select(x => x.Name).ToList();
        Assert.Equal(ExpectedSkillNames.OrderBy(n => n, StringComparer.Ordinal), listed.OrderBy(n => n, StringComparer.Ordinal));

        foreach (var sdkSkill in SdkOnlySkillNames)
        {
            Assert.DoesNotContain(sdkSkill, listed);
            Assert.DoesNotContain(EmbeddedSkillResources,
                r => r.Contains(sdkSkill.Replace('-', '_'), StringComparison.Ordinal));
            var raw = SkillsHandler.GetSkill(sdkSkill);
            var notFound = Assert.IsType<SkillNotFoundResponse>(raw);
            Assert.DoesNotContain(sdkSkill, notFound.ValidNames);
        }
    }
#endif
}
