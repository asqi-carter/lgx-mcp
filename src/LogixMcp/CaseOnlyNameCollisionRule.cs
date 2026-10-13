/// <summary>
/// Flags tags in the same scope whose names differ only by case.
/// Rationale: Logix treats tag names case-insensitively, so such tags are one name; a download fails or one tag is lost.
/// </summary>
public sealed class CaseOnlyNameCollisionRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "CaseOnlyNameCollision";

    /// <inheritdoc />
    public string Rationale =>
        "Logix tag names are case-insensitive, so two tags in one scope that differ only by case are the same name; a download fails or one tag is lost.";

    /// <summary>
    /// Compares tag names within each scope separately (controller scope, then each program scope) and emits
    /// one info finding per group of two or more names that are equal ignoring case. Tags in different scopes
    /// are never compared (that is the job of <c>ShadowedControllerTag</c>). Controller-scope findings cite an
    /// empty program; all findings cite an empty routine and rung -1.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Info findings, one per colliding name group.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var f in CheckScope(string.Empty, "controller scope", context.Project.Tags.Select(t => t.Name.ToString())))
            yield return f;

        foreach (var program in context.Project.Programs)
            foreach (var f in CheckScope(program.Name, $"program scope '{program.Name}'", program.Tags.Select(t => t.Name.ToString())))
                yield return f;
    }

    private IEnumerable<LintFinding> CheckScope(string program, string label, IEnumerable<string> names)
    {
        var groups = names
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var list = string.Join(", ", group.Select(n => $"'{n}'"));
            yield return new LintFinding(
                Id, LintHandler.SeverityName(LintSeverity.Info),
                program, string.Empty, -1, string.Empty,
                $"Tags {list} in {label} differ only by case. Logix treats tag names case-insensitively, so they are the same name and a download fails or one of the tags is lost.",
                "Rename one of the tags so the names differ by more than case.");
        }
    }
}
