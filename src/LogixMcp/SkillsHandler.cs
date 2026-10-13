using System.Reflection;
using System.Text.Json.Serialization;

/// <summary>
/// Summary entry for a skill playbook returned by <c>logix_list_skills</c>.
/// </summary>
/// <param name="Name">Skill identifier, e.g. <c>lgx-import-datatype</c>.</param>
/// <param name="Description">One-sentence description extracted from YAML front matter.</param>
public record SkillSummary(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description);

/// <summary>
/// Full skill detail returned by <c>logix_get_skill</c> on a successful lookup.
/// </summary>
/// <param name="Name">Skill identifier.</param>
/// <param name="Description">One-sentence description extracted from YAML front matter.</param>
/// <param name="Content">
/// Markdown body of the playbook with YAML front matter delimiters and fields stripped.
/// Leading whitespace / blank lines before the first heading are trimmed.
/// </param>
public record SkillDetail(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("content")]     string Content);

/// <summary>
/// Response envelope returned by <see cref="SkillsHandler.ListSkills"/>.
/// </summary>
/// <param name="Skills">All bundled skills sorted alphabetically by name.</param>
public record ListSkillsResponse(
    [property: JsonPropertyName("skills")] IReadOnlyList<SkillSummary> Skills);

/// <summary>
/// Error response returned by <see cref="SkillsHandler.GetSkill"/> when the
/// requested skill name is not found.
/// </summary>
/// <param name="Kind">Always <c>"SkillNotFound"</c>.</param>
/// <param name="RequestedName">The name that was supplied by the caller.</param>
/// <param name="ValidNames">Full list of skill names that are available.</param>
public record SkillNotFoundResponse(
    [property: JsonPropertyName("kind")]          string                  Kind,
    [property: JsonPropertyName("requestedName")] string                  RequestedName,
    [property: JsonPropertyName("validNames")]    IReadOnlyList<string>   ValidNames);

/// <summary>
/// Pure business-logic handler for the skills tools.
/// Reads skill content from manifest resources embedded in the executing assembly
/// at build time (via the <c>EmbeddedResource</c> glob in <c>LogixMcp.csproj</c>).
/// No filesystem access, no project context, no PLC connection.
/// </summary>
public static class SkillsHandler
{
    /// <summary>
    /// Assembly from which embedded <c>SKILL.md</c> resources are read.
    /// Resolved via a type that lives in it so no hard-coded path is required.
    /// </summary>
    private static readonly Assembly _assembly = typeof(ConnectHandler).Assembly;

    // ── Resource discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the manifest resource names of all embedded <c>SKILL.md</c> files,
    /// ordered alphabetically.
    /// </summary>
    private static IEnumerable<string> SkillResourceNames() =>
        _assembly
            .GetManifestResourceNames()
            .Where(n => n.EndsWith("SKILL.md", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

    // ── Parsing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the named resource stream and parses it into a <see cref="SkillDetail"/>.
    /// </summary>
    /// <param name="resourceName">
    /// Manifest resource name, e.g. <c>LogixMcp.skills.lgx_import_datatype.SKILL.md</c>.
    /// </param>
    /// <returns>
    /// A fully populated <see cref="SkillDetail"/> with front matter stripped from
    /// <see cref="SkillDetail.Content"/>.
    /// </returns>
    private static SkillDetail ParseResource(string resourceName)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Manifest resource '{resourceName}' could not be opened.");

        using var reader = new System.IO.StreamReader(stream);
        var raw = reader.ReadToEnd();

        return ParseSkillMarkdown(raw);
    }

    /// <summary>
    /// Parses a SKILL.md file string, extracting <c>name</c> and <c>description</c>
    /// from the YAML front matter block and returning the body with front matter stripped.
    /// </summary>
    /// <param name="markdown">Raw file content including YAML front matter.</param>
    internal static SkillDetail ParseSkillMarkdown(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');

        string name        = string.Empty;
        string description = string.Empty;
        int    bodyStart   = 0;

        // Walk the front-matter block bounded by "---" delimiters.
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            int closeIdx = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    closeIdx = i;
                    break;
                }

                // Simple "key: value" extraction — sufficient for the two fields we need.
                var colonPos = lines[i].IndexOf(':', StringComparison.Ordinal);
                if (colonPos > 0)
                {
                    var key   = lines[i][..colonPos].Trim();
                    var value = lines[i][(colonPos + 1)..].Trim();
                    if (key == "name")        name        = value;
                    if (key == "description") description = value;
                }
            }

            bodyStart = closeIdx >= 0 ? closeIdx + 1 : 1;
        }

        // Collect body lines, trimming leading blank lines.
        var bodyLines = lines[bodyStart..];
        int firstNonBlank = 0;
        while (firstNonBlank < bodyLines.Length
               && string.IsNullOrWhiteSpace(bodyLines[firstNonBlank]))
        {
            firstNonBlank++;
        }

        var content = string.Join("\n", bodyLines[firstNonBlank..]).TrimEnd();

        return new SkillDetail(name, description, content);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all bundled skills as a list of summary objects sorted by name.
    /// </summary>
    public static ListSkillsResponse ListSkills()
    {
        var skills = SkillResourceNames()
            .Select(ParseResource)
            .Select(d => new SkillSummary(d.Name, d.Description))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new ListSkillsResponse(skills);
    }

    /// <summary>
    /// Returns the full detail for the skill identified by <paramref name="name"/>,
    /// or a <see cref="SkillNotFoundResponse"/> when the name is not recognised.
    /// </summary>
    /// <param name="name">
    /// Skill name as it appears in front matter, e.g. <c>lgx-import-datatype</c>.
    /// Lookup is case-sensitive (skill names are lower-kebab-case by convention).
    /// </param>
    /// <returns>
    /// <see cref="SkillDetail"/> on success; <see cref="SkillNotFoundResponse"/> on failure.
    /// Both are serialisable to JSON by the caller.
    /// </returns>
    public static object GetSkill(string name)
    {
        var allSkills = SkillResourceNames()
            .Select(ParseResource)
            .ToList();

        var match = allSkills.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.Ordinal));

        if (match is not null)
            return match;

        var validNames = allSkills
            .Select(d => d.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new SkillNotFoundResponse(
            Kind:          "SkillNotFound",
            RequestedName: name,
            ValidNames:    validNames);
    }
}
