using L5Sharp.Core;

/// <summary>
/// Flags a tag or member path driven by OTE in more than one rung.
/// Rationale: OTE is re-evaluated every scan, so with two OTEs the last one executed wins and silently defeats the other rung's interlock.
/// </summary>
public sealed class DuplicateDestructiveOutputRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "DuplicateDestructiveOutput";

    /// <inheritdoc />
    public string Rationale =>
        "OTE is re-evaluated every scan, so with two OTEs on one operand the last one wins and defeats the other rung's interlock.";

    /// <summary>
    /// Finds every OTE operand used in more than one distinct rung across all ladder routines.
    /// Each occurrence yields its own error naming all the other locations. OTL/OTU are
    /// deliberately ignored: a latch/unlatch pair on one operand is the intended idiom.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Raw findings (the handler wraps rung text).</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        // Operand key -> distinct rung occurrences, in project order.
        var sites = new Dictionary<string, List<Site>>(StringComparer.OrdinalIgnoreCase);

        foreach (var routine in context.LadderRoutines)
        {
            var localTags = ProgramLocalTags(context.Project, routine.Program);
            foreach (var rung in routine.Rungs)
            {
                foreach (var instruction in rung.Instructions)
                {
                    if (!string.Equals(instruction.Key, "OTE", StringComparison.OrdinalIgnoreCase)) continue;
                    var operand = instruction.Arguments.FirstOrDefault()?.ToString();
                    if (string.IsNullOrWhiteSpace(operand)) continue;

                    // Program-local tags are distinct per program even when names collide.
                    var baseName = WriteClassifier.ExtractBaseName(operand) ?? operand;
                    var key = localTags.Contains(baseName) ? $"{routine.Program}::{operand}" : operand;

                    if (!sites.TryGetValue(key, out var list))
                        sites[key] = list = new List<Site>();
                    if (!list.Any(s => s.Program == routine.Program && s.Routine == routine.Routine && s.Rung.Number == rung.Number))
                        list.Add(new Site(routine.Program, routine.Routine, rung, operand));
                }
            }
        }

        foreach (var list in sites.Values.Where(l => l.Count > 1))
        {
            foreach (var site in list)
            {
                var others = string.Join("; ", list.Where(o => !ReferenceEquals(o, site)).Select(Describe));
                yield return new LintFinding(
                    Id, LintHandler.SeverityName(LintSeverity.Error),
                    site.Program, site.Routine, site.Rung.Number, site.Rung.Text,
                    $"OTE({site.Operand}) is also driven by OTE in {others}. The last OTE executed wins each scan and defeats the other rung's conditions. See lgx-instruction-reference, section \"Bit instructions: XIC, XIO, OTE, OTL, OTU, ONS, OSR, OSF\".",
                    "Drive the operand from a single OTE rung, combining the conditions in parallel branches, or use OTL/OTU if latching is intended.");
            }
        }
    }

    private static string Describe(Site s) => $"{s.Program}/{s.Routine} rung {s.Rung.Number}";

    private static HashSet<string> ProgramLocalTags(L5X project, string program)
    {
        var p = project.Programs.FirstOrDefault(x => string.Equals(x.Name, program, StringComparison.OrdinalIgnoreCase));
        return p is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(p.Tags.Select(t => t.Name.ToString()), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Site(string Program, string Routine, LintRung Rung, string Operand);
}
