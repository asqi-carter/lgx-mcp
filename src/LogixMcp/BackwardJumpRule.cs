/// <summary>
/// Flags a JMP whose LBL sits on the same rung or an earlier rung of the same routine.
/// Rationale: a backward jump loops within one scan; if its exit condition never goes false the scan
/// never ends and trips the watchdog (major fault type 6).
/// </summary>
public sealed class BackwardJumpRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "BackwardJump";

    /// <inheritdoc />
    public string Rationale =>
        "A JMP to a LBL on the same or an earlier rung loops within the scan; a never-true exit condition makes the scan endless and trips the watchdog (major fault type 6).";

    /// <summary>
    /// Collects the LBL rung numbers of each ladder routine and reports every JMP whose label is on
    /// a rung at or before the JMP's own rung. JMPs whose label does not exist are ignored.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One warning per backward JMP.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var routine in context.LadderRoutines)
        {
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var rung in routine.Rungs)
                foreach (var name in Targets(rung, "LBL"))
                    labels.TryAdd(name, rung.Number);

            foreach (var rung in routine.Rungs)
            {
                foreach (var label in Targets(rung, "JMP"))
                {
                    if (!labels.TryGetValue(label, out var target) || target > rung.Number) continue;
                    yield return new LintFinding(
                        Id, LintHandler.SeverityName(LintSeverity.Warning),
                        routine.Program, routine.Routine, rung.Number, rung.Text,
                        $"JMP({label}) jumps backward to LBL({label}) on rung {target} (this JMP is on rung {rung.Number}); if the exit condition never becomes false the scan never ends and trips the watchdog (major fault type 6). See lgx-instruction-reference, section \"Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP\".",
                        "Jump forward instead, or make sure the loop is bounded by a counter or condition that is guaranteed to end within the watchdog time.");
                }
            }
        }
    }

    private static IEnumerable<string> Targets(LintRung rung, string key)
    {
        foreach (var instruction in rung.Instructions)
        {
            if (!string.Equals(instruction.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            var name = instruction.Arguments.FirstOrDefault()?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }
}
