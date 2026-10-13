using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>
/// MCP tool type exposing skill-playbook lookup operations.
/// These tools require no open project and no PLC connection — they read from
/// embedded assembly resources only and are therefore always available.
/// </summary>
[McpServerToolType]
public class LogixSkillsTools
{
    // ── MCP Tool endpoints ────────────────────────────────────────────────────

    /// <summary>
    /// Returns all bundled agent-playbook skills sorted alphabetically by name.
    /// Each entry contains the skill <c>name</c> and its one-sentence <c>description</c>.
    /// Reads from embedded assembly resources — no project or PLC connection required.
    /// </summary>
    [McpServerTool(Name = "logix_list_skills")]
    [Description(
        "Returns all bundled agent-playbook skills sorted alphabetically by name. " +
        "Each entry carries the skill name and a one-sentence description. " +
        "Use this when the user asks what playbooks or skills are available, " +
        "or to discover skill names before calling logix_get_skill for the full content. " +
        "Do NOT use this when you need the full playbook body — call logix_get_skill instead. " +
        "Requires no open project and no PLC connection.")]
    public string ListSkills()
    {
        var sw     = Stopwatch.StartNew();
        var result = JsonSerializer.Serialize(SkillsHandler.ListSkills());
        sw.Stop();
        TrafficLogger.Append(
            "logix_list_skills",
            requestBytes:  0,
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            true);
        return result;
    }

    /// <summary>
    /// Returns the full playbook body for the named skill, with YAML front matter stripped.
    /// Returns a <c>SkillNotFound</c> error object listing all valid names when the name is
    /// not recognised.
    /// Reads from embedded assembly resources — no project or PLC connection required.
    /// </summary>
    [McpServerTool(Name = "logix_get_skill")]
    [Description(
        "Returns the full playbook content for the named skill. " +
        "The 'content' field is the markdown body with YAML front matter stripped — " +
        "ready to inject into an agent prompt or display to the user. " +
        "Use this when you have a skill name (from logix_list_skills or prior context) " +
        "and need the full step-by-step instructions. " +
        "Do NOT use this when you only need a summary — call logix_list_skills instead. " +
        "Returns a SkillNotFound error object (with the full list of valid names) " +
        "when the requested name does not match any bundled skill. " +
        "Requires no open project and no PLC connection.")]
    public string GetSkill(
        [Description(
            "Skill identifier to retrieve, e.g. 'lgx-import-datatype'. " +
            "Must match a name from logix_list_skills exactly (case-sensitive, lower-kebab-case). " +
            "An unknown name returns a SkillNotFound error with the full list of valid names.")]
        string name)
    {
        var sw     = Stopwatch.StartNew();
        var result = JsonSerializer.Serialize(SkillsHandler.GetSkill(name));
        sw.Stop();
        TrafficLogger.Append(
            "logix_get_skill",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { name }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            true);
        return result;
    }
}
