using L5Sharp.Core;

/// <summary>
/// Flags a JSR whose input or return parameter count differs from the target routine's SBR / RET.
/// Rationale: Rockwell documents major fault type 4 code 31 (JSR parameter mismatch) when the counts disagree.
/// </summary>
public sealed class JsrParameterMismatchRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "JsrParameterMismatch";

    /// <inheritdoc />
    public string Rationale =>
        "A JSR whose input or return parameter count differs from the target's SBR or RET raises major fault type 4 code 31.";

    /// <summary>
    /// Compares each JSR's input count with the target's SBR input count (only when the target
    /// has an SBR) and its return count with the target's RET (only when the target has a RET).
    /// JSRs whose target routine is missing are skipped (<c>JsrTargetMissing</c> reports them), as are
    /// targets that are not ladder routines (their SBR/RET are not visible to the linter).
    /// A JSR is <c>JSR(routine, inputCount, in1..inN, ret1..retM)</c>; a routine may hold several RETs,
    /// and any RET whose count differs is a mismatch.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One error per mismatching JSR.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var caller in context.LadderRoutines)
        {
            foreach (var rung in caller.Rungs)
            {
                foreach (var instruction in rung.Instructions)
                {
                    if (!string.Equals(instruction.Key, "JSR", StringComparison.OrdinalIgnoreCase)) continue;
                    var args = instruction.Arguments.Select(a => a.ToString()).ToList();
                    if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0])) continue;
                    var target = args[0];

                    var targetRoutine = context.LadderRoutines.FirstOrDefault(r =>
                        string.Equals(r.Program, caller.Program, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Routine, target, StringComparison.OrdinalIgnoreCase));
                    if (targetRoutine is null) continue; // missing (JsrTargetMissing) or not ladder

                    // Input count is a literal; if it is unreadable or inconsistent, do not guess.
                    if (args.Count < 2 || !int.TryParse(args[1], out var inputs) || inputs < 0 || inputs > args.Count - 2)
                        continue;
                    var returns = args.Count - 2 - inputs;

                    var problems = new List<string>();

                    var sbrs = Callee(targetRoutine, "SBR").ToList();
                    if (sbrs.Count > 0 && sbrs.Any(s => s.Count != inputs))
                        problems.Add($"passes {inputs} input parameter(s) but the SBR in '{target}' declares {sbrs[0].Count}");

                    var rets = Callee(targetRoutine, "RET").ToList();
                    var badRet = rets.FirstOrDefault(r => r.Count != returns);
                    if (badRet is not null)
                        problems.Add($"expects {returns} return parameter(s) but a RET in '{target}' returns {badRet.Count}");

                    if (problems.Count == 0) continue;
                    yield return new LintFinding(
                        Id, LintHandler.SeverityName(LintSeverity.Error),
                        caller.Program, caller.Routine, rung.Number, rung.Text,
                        $"JSR({target}) {string.Join(" and ", problems)}. A parameter count mismatch raises major fault type 4 code 31. See lgx-instruction-reference, section \"Program control: JSR, RET, SBR, JMP, LBL, AFI, NOP\".",
                        "Make the JSR input/return parameter counts match the target routine's SBR and RET parameter lists.");
                }
            }
        }
    }

    /// <summary>Operand counts of every instruction named <paramref name="key"/> in <paramref name="routine"/>.</summary>
    private static IEnumerable<IReadOnlyList<string>> Callee(LintRoutine routine, string key)
    {
        foreach (var rung in routine.Rungs)
            foreach (var instruction in rung.Instructions)
                if (string.Equals(instruction.Key, key, StringComparison.OrdinalIgnoreCase))
                    yield return instruction.Arguments.Select(a => a.ToString()).ToList();
    }
}
