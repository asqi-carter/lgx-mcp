using System.Text.RegularExpressions;

/// <summary>
/// Flags a program-scoped tag whose name matches a controller-scoped tag (case-insensitive).
/// Rationale: inside that program the program tag silently hides the controller tag, so logic that looks like it
/// touches the shared controller tag actually reads and writes a different, program-local one.
/// </summary>
public sealed class ShadowedControllerTagRule : ILintRule
{
    private static readonly Regex Identifier = new(@"(?<![\w.:])[A-Za-z_]\w*", RegexOptions.Compiled);

    /// <inheritdoc />
    public string Id => "ShadowedControllerTag";

    /// <inheritdoc />
    public string Rationale =>
        "A program tag with the same name as a controller tag hides it inside that program, so rungs there silently use the local tag instead of the shared one.";

    /// <summary>
    /// For every program tag that shares a name (ignoring case) with a controller tag, emits one warning citing the
    /// program, the tag name, and the first rung in that program (project order) whose instruction operands
    /// reference the name. When no rung references it, the finding cites rung -1 with an empty routine and text.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Warning findings, one per shadowing program tag.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var controllerTags = new HashSet<string>(
            context.Project.Tags.Select(t => t.Name.ToString()), StringComparer.OrdinalIgnoreCase);
        if (controllerTags.Count == 0) yield break;

        foreach (var program in context.Project.Programs)
        {
            foreach (var tag in program.Tags)
            {
                var name = tag.Name.ToString();
                if (!controllerTags.Contains(name)) continue;

                var routine = string.Empty;
                var rungNumber = -1;
                var text = string.Empty;
                foreach (var candidate in context.LadderRoutines.Where(r =>
                             string.Equals(r.Program, program.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var hit = candidate.Rungs.FirstOrDefault(r => References(r, name));
                    if (hit is null) continue;
                    routine = candidate.Routine;
                    rungNumber = hit.Number;
                    text = hit.Text;
                    break;
                }

                var where = rungNumber >= 0
                    ? $"first referenced in {program.Name}/{routine} rung {rungNumber}"
                    : "no rung in the program references it";
                yield return new LintFinding(
                    Id, LintHandler.SeverityName(LintSeverity.Warning),
                    program.Name, routine, rungNumber, text,
                    $"Program tag '{name}' in program scope '{program.Name}' shadows the controller-scoped tag of the same name (compared case-insensitively); {where}. " +
                    "Within this program the program-scoped tag hides the controller-scoped one.",
                    "Rename the program tag (or the controller tag) so the two scopes do not share a name.");
            }
        }
    }

    private static bool References(LintRung rung, string name)
    {
        foreach (var instruction in rung.Instructions)
            foreach (var argument in instruction.Arguments)
            {
                var s = argument.ToString();
                if (string.IsNullOrEmpty(s)) continue;
                foreach (Match m in Identifier.Matches(s))
                    if (string.Equals(m.Value, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        return false;
    }
}
