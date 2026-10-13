/// <summary>
/// Flags a rung that has an output instruction (any instruction that writes an operand) and no rung comment.
/// Rationale: an uncommented output rung hides the intent of what it drives, which makes later review and troubleshooting harder; this is advisory only.
/// </summary>
public sealed class MissingRungCommentRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "MissingRungComment";

    /// <inheritdoc />
    public string Rationale =>
        "A rung that drives an output without a comment hides its intent from reviewers and troubleshooters.";

    /// <summary>
    /// For each ladder routine, finds rungs with an output instruction (one whose operand is classified
    /// <c>write</c> by <see cref="WriteClassifier"/>) and a blank comment. Without a routine scope the result is one
    /// info finding per routine carrying the count (cited at the first such rung) to avoid noise; when
    /// <see cref="LintContext.RoutineScoped"/> is set, one info finding is emitted per offending rung.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Info findings, per routine (summary) or per rung (routine-scoped).</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var severity = LintHandler.SeverityName(LintSeverity.Info);
        foreach (var routine in context.LadderRoutines)
        {
            var missing = routine.Rungs
                .Where(r => string.IsNullOrWhiteSpace(r.Comment) && HasOutput(r, context))
                .ToList();
            if (missing.Count == 0) continue;

            const string fix = "Add a rung comment describing what the output does and why.";
            if (context.RoutineScoped)
            {
                foreach (var rung in missing)
                    yield return new LintFinding(
                        Id, severity, routine.Program, routine.Routine, rung.Number, rung.Text,
                        $"Rung has an output instruction and no comment ({missing.Count} such rung(s) in this routine).",
                        fix);
            }
            else
            {
                var first = missing[0];
                yield return new LintFinding(
                    Id, severity, routine.Program, routine.Routine, first.Number, first.Text,
                    $"{missing.Count} rung(s) in this routine have an output instruction and no comment (first shown). " +
                    "Pass the routine parameter to list each rung.",
                    fix);
            }
        }
    }

    private static bool HasOutput(LintRung rung, LintContext context)
    {
        foreach (var instruction in rung.Instructions)
        {
            var count = instruction.Arguments.Count();
            for (var i = 0; i < count; i++)
                if (WriteClassifier.ClassifyKind(instruction, i, context.AoiWrites) == "write")
                    return true;
        }
        return false;
    }
}
