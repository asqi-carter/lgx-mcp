using L5Sharp.Core;

/// <summary>
/// Flags Add-On Instruction definitions that no ladder rung and no other AOI's logic calls.
/// Rationale: dead AOIs add download size and maintenance burden; advisory only.
/// </summary>
public sealed class AoiNeverUsedRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "AoiNeverUsed";

    /// <inheritdoc />
    public string Rationale =>
        "An Add-On Instruction that nothing calls is dead code that adds download size and maintenance burden (advisory only).";

    /// <summary>
    /// Emits one info finding per AOI that is not called by any ladder rung in any program, nor by the
    /// ladder logic of any other AOI. An AOI calling itself does not count as use. Findings cite no program,
    /// an empty routine and rung -1. Unused tags are deliberately not reported.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Info findings, one per unused AOI.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var aois = context.Project.AddOnInstructions.ToList();
        if (aois.Count == 0) yield break;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var routine in context.LadderRoutines)
            foreach (var rung in routine.Rungs)
                foreach (var instruction in rung.Instructions)
                    used.Add(instruction.Key);

        foreach (var aoi in aois)
            foreach (var routine in aoi.Routines)
            {
                if (!WriteClassifier.IsLadder(routine)) continue;
                foreach (var rung in routine.Rungs)
                    CollectKeys(rung, aoi.Name, routine.Name, context, used);
            }

        foreach (var aoi in aois)
        {
            if (used.Contains(aoi.Name)) continue;
            yield return new LintFinding(
                Id, LintHandler.SeverityName(LintSeverity.Info),
                string.Empty, string.Empty, -1, string.Empty,
                $"AOI {aoi.Name} is defined but not used",
                "Remove the unused AOI definition, or call it from a ladder rung.");
        }
    }

    /// <summary>
    /// Records the instruction keys of an AOI routine rung. A rung that cannot be parsed is reported
    /// through <see cref="LintContext.AoiUnparsedRungs"/> (program empty, routine <c>AOI/routine</c>) and skipped.
    /// </summary>
    private static void CollectKeys(
        Rung rung, string excludeSelf, string routineName, LintContext context, HashSet<string> used)
    {
        foreach (var instruction in RungParser.ParseContained(
                     rung, string.Empty, $"{excludeSelf}/{routineName}", context.AoiUnparsedRungs))
        {
            if (excludeSelf is not null &&
                string.Equals(instruction.Key, excludeSelf, StringComparison.OrdinalIgnoreCase))
                continue;
            used.Add(instruction.Key);
        }
    }
}
