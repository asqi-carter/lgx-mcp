using L5Sharp.Core;

/// <summary>
/// Flags a JSR whose target routine does not exist in the calling program.
/// Rationale: a JSR to a missing routine fails verification/download or faults the task, so the call never does what the rung author intended.
/// </summary>
public sealed class JsrTargetMissingRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "JsrTargetMissing";

    /// <inheritdoc />
    public string Rationale =>
        "A JSR naming a routine that does not exist in its program cannot run; it fails verification or faults the task.";

    /// <summary>
    /// Checks every JSR in every ladder routine against the routine names of the
    /// calling program (all routine types count as existing targets; JSR cannot cross programs).
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One error per JSR whose target is absent.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var routine in context.LadderRoutines)
        {
            var existing = LintJsr.RoutineNames(context.Project, routine.Program);
            foreach (var rung in routine.Rungs)
            {
                foreach (var target in LintJsr.Targets(rung))
                {
                    if (existing.Contains(target)) continue;
                    yield return new LintFinding(
                        Id, LintHandler.SeverityName(LintSeverity.Error),
                        routine.Program, routine.Routine, rung.Number, rung.Text,
                        $"JSR({target}) calls a routine that does not exist in program '{routine.Program}'. See lgx-instruction-reference, section \"Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP\".",
                        "Correct the routine name in the JSR, or create the missing routine in this program.");
                }
            }
        }
    }
}

/// <summary>
/// Flags a non-main, non-fault routine that no JSR calls, in a program whose main routine is set.
/// Rationale: a routine nothing calls never executes, so it is usually dead logic or a call that was deleted by mistake.
/// </summary>
public sealed class RoutineNeverCalledRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "RoutineNeverCalled";

    /// <inheritdoc />
    public string Rationale =>
        "A routine that no JSR calls (and that is not the main or fault routine) never executes, so it is dead logic or a lost call.";

    /// <summary>
    /// Reports uncalled routines as info findings citing rung 0 with empty text (the finding is
    /// about the routine, not a rung). Exempt: the main and fault routines. Programs with no main
    /// routine set are skipped. Programs containing non-ladder routines are also skipped, because
    /// JSR calls inside structured text / FBD / SFC are not visible to the linter and reporting
    /// would risk false positives. A routine's call to itself does not count as being called.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Info findings.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var program in context.Project.Programs)
        {
            var main = program.MainRoutineName;
            if (string.IsNullOrWhiteSpace(main)) continue;
            if (program.Routines.Any(r => !WriteClassifier.IsLadder(r))) continue;

            var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var caller in context.LadderRoutines.Where(r =>
                         string.Equals(r.Program, program.Name, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var rung in caller.Rungs)
                    foreach (var target in LintJsr.Targets(rung))
                        if (!string.Equals(target, caller.Routine, StringComparison.OrdinalIgnoreCase))
                            called.Add(target);
            }

            foreach (var routine in program.Routines)
            {
                var name = routine.Name.ToString();
                if (string.Equals(name, main, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(program.FaultRoutineName) &&
                    string.Equals(name, program.FaultRoutineName, StringComparison.OrdinalIgnoreCase)) continue;
                if (called.Contains(name)) continue;

                yield return new LintFinding(
                    Id, LintHandler.SeverityName(LintSeverity.Info),
                    program.Name, name, 0, string.Empty,
                    $"Routine '{name}' is never called by a JSR in program '{program.Name}'. See lgx-instruction-reference, section \"Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP\".",
                    "Call it with a JSR from the main routine (or another routine), or delete it if it is dead logic.");
            }
        }
    }
}

/// <summary>Shared JSR helpers for the JSR-related lint rules.</summary>
internal static class LintJsr
{
    /// <summary>Names of all routines (any type) in <paramref name="program"/>.</summary>
    public static HashSet<string> RoutineNames(L5X project, string program)
    {
        var p = project.Programs.FirstOrDefault(x => string.Equals(x.Name, program, StringComparison.OrdinalIgnoreCase));
        return p is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(p.Routines.Select(r => r.Name.ToString()), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Target routine names of every JSR in <paramref name="rung"/>.</summary>
    public static IEnumerable<string> Targets(LintRung rung)
    {
        foreach (var instruction in rung.Instructions)
        {
            if (!string.Equals(instruction.Key, "JSR", StringComparison.OrdinalIgnoreCase)) continue;
            var target = instruction.Arguments.FirstOrDefault()?.ToString();
            if (!string.IsNullOrWhiteSpace(target)) yield return target;
        }
    }
}
