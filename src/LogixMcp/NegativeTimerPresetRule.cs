using L5Sharp.Core;

/// <summary>
/// Flags a TON/TOF/RTO or CTU/CTD whose tag's stored <c>.PRE</c> in the project data is negative.
/// Rationale: a negative timer PRE raises major fault type 4 code 34 when the instruction executes. For a counter a
/// negative PRE is legal but almost always a bug.
/// </summary>
/// <remarks>
/// The negative test is <see cref="DiagnoseSuspects.IsNegativePre"/> and the value conversion is
/// <see cref="DiagnoseSuspects.ToInteger"/>, both shared with the <c>negativeTimerPre</c> diagnose suspect. Lint has no
/// controller connection, so the value is the one stored in the project data instead of a live read.
/// </remarks>
public sealed class NegativeTimerPresetRule : ILintRule
{
    private static readonly HashSet<string> TimerKeys = new(StringComparer.OrdinalIgnoreCase) { "TON", "TOF", "RTO" };
    private static readonly HashSet<string> CounterKeys = new(StringComparer.OrdinalIgnoreCase) { "CTU", "CTD" };

    /// <inheritdoc />
    public string Id => "NegativeTimerPreset";

    /// <inheritdoc />
    public string Rationale =>
        "A negative timer .PRE raises major fault 4/34 when the timer executes; a negative counter .PRE is legal but almost always a bug.";

    /// <summary>
    /// Checks the first operand of every TON/TOF/RTO/CTU/CTD in every ladder routine. Only a whole tag (optionally
    /// with array subscripts) whose stored <c>.PRE</c> reads below zero is reported; tags whose value cannot be
    /// resolved from the project data are skipped.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>A warning per offending timer and an info finding per offending counter.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var routine in context.LadderRoutines)
        {
            foreach (var rung in routine.Rungs)
            {
                foreach (var instruction in rung.Instructions)
                {
                    var key = instruction.Key;
                    var isTimer = TimerKeys.Contains(key);
                    if (!isTimer && !CounterKeys.Contains(key)) continue;

                    var operand = instruction.Arguments.FirstOrDefault()?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(operand)) continue;

                    var pre = StoredPre(context.Project, routine.Program, operand);
                    if (!DiagnoseSuspects.IsNegativePre(pre)) continue;

                    yield return isTimer
                        ? new LintFinding(
                            Id, LintHandler.SeverityName(LintSeverity.Warning),
                            routine.Program, routine.Routine, rung.Number, rung.Text,
                            $"{key} timer {operand} has a stored .PRE of {pre} in the project data. A negative timer PRE causes major fault type 4 code 34 when the instruction executes.",
                            "Set the timer's .PRE to zero or a positive value, and check which logic writes it.")
                        : new LintFinding(
                            Id, LintHandler.SeverityName(LintSeverity.Info),
                            routine.Program, routine.Routine, rung.Number, rung.Text,
                            $"{key} counter {operand} has a stored .PRE of {pre} in the project data. A negative counter PRE is legal but almost always a bug.",
                            "Confirm the negative preset is intended; otherwise set the counter's .PRE to a positive value.");
                }
            }
        }
    }

    /// <summary>Stored .PRE of the timer/counter named by <paramref name="operand"/>, or null when it cannot be resolved.</summary>
    private static long? StoredPre(L5X project, string program, string operand)
    {
        var cut = operand.IndexOfAny(['.', '[']);
        var baseName = cut > 0 ? operand[..cut] : operand;
        var rest = cut > 0 ? operand[cut..] : string.Empty;

        var tag = project.Programs
                      .FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase))
                      ?.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), baseName, StringComparison.OrdinalIgnoreCase))
                  ?? project.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), baseName, StringComparison.OrdinalIgnoreCase));
        if (tag is null) return null;

        try
        {
            var path = rest.StartsWith('.') ? rest[1..] + ".PRE" : rest + ".PRE";
            var value = rest.Length == 0 ? tag.Member("PRE")?.Value : tag.Member(path)?.Value;
            return value is AtomicData atomic
                && long.TryParse(atomic.ToString(Radix.Decimal), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var n)
                ? DiagnoseSuspects.ToInteger(n)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
