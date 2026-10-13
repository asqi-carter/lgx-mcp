using System.Globalization;
using System.Text.RegularExpressions;
using L5Sharp.Core;

/// <summary>
/// Flags DIV, MOD or CPT '/' whose divisor is a literal 0, or a plain tag that no rung writes and whose L5X value is 0.
/// Rationale: dividing by zero raises a minor fault (type 4, code 4) unless the logic handles it, and a divisor nothing ever writes stays 0.
/// </summary>
public sealed class DivideByPossibleZeroRule : ILintRule
{
    // Operand directly after a '/' in a CPT expression: a tag path or a numeric literal.
    private static readonly Regex DivisorInExpression = new(
        @"/\s*(?<d>[A-Za-z_][\w.\[\]]*|[-+]?\d[\w#.]*)", RegexOptions.Compiled);

    /// <inheritdoc />
    public string Id => "DivideByPossibleZero";

    /// <inheritdoc />
    public string Rationale =>
        "Dividing by zero raises a minor fault (4/4) unless handled, and a divisor tag that no rung writes keeps its initial 0.";

    /// <summary>
    /// Checks every DIV/MOD (divisor = second argument) and CPT (each operand following '/')
    /// in every ladder routine. Only whole-tag divisors are judged by value: member paths and
    /// array elements are not (their L5X value is not resolved), and a tag written anywhere by
    /// a rung (per <see cref="WriteClassifier.CollectProgramWrittenTags"/>) is assumed handled.
    /// Tags written outside the logic (HMI, MSG) can still be non-zero at run time.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One warning per offending divisor.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var written = WriteClassifier.CollectProgramWrittenTags(context.Project);

        foreach (var routine in context.LadderRoutines)
        {
            foreach (var rung in routine.Rungs)
            {
                foreach (var instruction in rung.Instructions)
                {
                    var key = instruction.Key;
                    foreach (var divisor in Divisors(key, instruction.Arguments.Select(a => a.ToString()).ToList()))
                    {
                        var why = Reason(context.Project, routine.Program, divisor, written);
                        if (why is null) continue;
                        yield return new LintFinding(
                            Id, LintHandler.SeverityName(LintSeverity.Warning),
                            routine.Program, routine.Routine, rung.Number, rung.Text,
                            $"{key} divides by {divisor}, {why}. A zero divisor causes a minor fault (4/4) unless handled. See lgx-instruction-reference, section \"Math and move: ADD, SUB, MUL, DIV, MOD, CPT, MOVE, COP, CPS, CLR\".",
                            $"Guard the instruction with a rung condition that the divisor is not zero (e.g. {InstructionMnemonics.Describe("NE")} divisor 0), or make sure logic writes a non-zero value first.");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> Divisors(string key, IReadOnlyList<string> args)
    {
        if (key.Equals("DIV", StringComparison.OrdinalIgnoreCase) || key.Equals("MOD", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count > 1 && !string.IsNullOrWhiteSpace(args[1])) yield return args[1].Trim();
        }
        else if (key.Equals("CPT", StringComparison.OrdinalIgnoreCase) && args.Count > 1)
        {
            foreach (Match m in DivisorInExpression.Matches(string.Join(",", args.Skip(1))))
                yield return m.Groups["d"].Value;
        }
    }

    private static string? Reason(L5X project, string program, string divisor, Dictionary<string, string> written)
    {
        if (IsLiteralZero(divisor)) return "a literal 0";
        if (divisor.IndexOfAny(['.', '[']) >= 0 || !char.IsLetter(divisor[0]) && divisor[0] != '_') return null;
        if (written.ContainsKey(divisor)) return null;

        var local = project.Programs
            .FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase))
            ?.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), divisor, StringComparison.OrdinalIgnoreCase));
        var tag = local ?? project.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), divisor, StringComparison.OrdinalIgnoreCase));
        if (tag is null || tag.Value is not AtomicData) return null;

        return IsZero(tag.Value.ToString()) ? $"a tag that no rung writes and whose value in the project file is 0" : null;
    }

    private static bool IsLiteralZero(string s)
    {
        var i = s.IndexOf('#');
        if (i >= 0) s = s[(i + 1)..];
        return s.Length > 0 && (char.IsDigit(s[0]) || s[0] is '-' or '+') && IsZero(s.Replace("_", ""));
    }

    private static bool IsZero(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0;
}
