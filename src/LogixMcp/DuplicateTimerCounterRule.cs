using L5Sharp.Core;

/// <summary>
/// Flags a TIMER or COUNTER tag used as the operand of more than one TON/TOF/RTO/CTU/CTD
/// anywhere in the controller. Same defect class as <see cref="DuplicateDestructiveOutputRule"/>:
/// each instruction rewrites the shared ACC/status bits every scan, so the last one executed wins.
/// </summary>
public sealed class DuplicateTimerCounterRule : ILintRule
{
    private static readonly HashSet<string> Keys =
        new(StringComparer.OrdinalIgnoreCase) { "TON", "TOF", "RTO", "CTU", "CTD" };

    /// <inheritdoc />
    public string Id => "DuplicateTimerCounter";

    /// <inheritdoc />
    public string Rationale =>
        "A timer/counter tag driven by two timing instructions has its ACC and status bits rewritten twice per scan, so the last one executed wins and the other's timing or count is lost.";

    /// <summary>
    /// Finds every TIMER/COUNTER operand used by more than one TON/TOF/RTO/CTU/CTD across all
    /// ladder routines of the controller. Each occurrence yields its own error naming all the others.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Raw findings (the handler wraps rung text).</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var sites = new Dictionary<string, List<Site>>(StringComparer.OrdinalIgnoreCase);

        foreach (var routine in context.LadderRoutines)
        {
            var localTags = ProgramLocalTags(context.Project, routine.Program);
            foreach (var rung in routine.Rungs)
            {
                foreach (var instruction in rung.Instructions)
                {
                    if (!Keys.Contains(instruction.Key)) continue;
                    var operand = instruction.Arguments.FirstOrDefault()?.ToString();
                    if (string.IsNullOrWhiteSpace(operand)) continue;

                    // Program-local tags are distinct per program even when names collide.
                    var baseName = WriteClassifier.ExtractBaseName(operand) ?? operand;
                    var key = localTags.Contains(baseName) ? $"{routine.Program}::{operand}" : operand;

                    if (!sites.TryGetValue(key, out var list))
                        sites[key] = list = new List<Site>();
                    list.Add(new Site(routine.Program, routine.Routine, rung, operand, instruction.Key.ToUpperInvariant()));
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
                    $"{site.Instruction}({site.Operand}) shares its timer/counter tag with {others}. Each instruction rewrites the same ACC and status bits every scan, so the last one executed wins.",
                    "Give each timing instruction its own TIMER/COUNTER tag, or merge the conditions into a single instruction.");
            }
        }
    }

    private static string Describe(Site s) => $"{s.Instruction} in {s.Program}/{s.Routine} rung {s.Rung.Number}";

    private static HashSet<string> ProgramLocalTags(L5X project, string program)
    {
        var p = project.Programs.FirstOrDefault(x => string.Equals(x.Name, program, StringComparison.OrdinalIgnoreCase));
        return p is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(p.Tags.Select(t => t.Name.ToString()), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record Site(string Program, string Routine, LintRung Rung, string Operand, string Instruction);
}
