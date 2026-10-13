using System.Text;

/// <summary>
/// Flags an array operand with a tag subscript (<c>Arr[idx]</c>) when no earlier instruction in the same routine
/// compares <c>idx</c> with LES, LIM, GEQ or GRT.
/// Rationale: an out-of-range subscript raises a major fault (type 4, code 20) and stops the controller, so the index should be range-checked first.
/// </summary>
public sealed class ArrayIndexUncheckedRule : ILintRule
{
    /// <summary>Canonical keys of the range-guard comparisons; a lookup canonicalizes first, so both spellings match.</summary>
    private static readonly HashSet<string> Comparisons =
        new(["LT", "LIMIT", "GE", "GT"], StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string Id => "ArrayIndexUnchecked";

    /// <inheritdoc />
    public string Rationale =>
        "An out-of-range array subscript raises a major fault (4/20) and stops the controller, so a tag index should be range-checked first.";

    /// <summary>
    /// Walks each ladder routine in rung/instruction order, remembering every index expression that appeared as
    /// an operand of LT, LIMIT, GE or GT (or LES, LIM, GEQ or GRT). An array operand whose subscript is not a literal and was not yet
    /// compared is reported once per rung. Advisory only: the compared bound is not verified (a literal,
    /// <c>.Size</c> or a dimension constant are all accepted), and checks done outside the routine are not seen.
    /// </summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>One warning per unchecked subscript per rung.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        foreach (var routine in context.LadderRoutines)
        {
            var checkedIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rung in routine.Rungs)
            {
                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var instruction in rung.Instructions)
                {
                    var args = instruction.Arguments.Select(a => a.ToString()).ToList();

                    foreach (var (array, index) in Subscripts(args))
                    {
                        if (checkedIndexes.Contains(Normalize(index)) || !reported.Add(array + "[" + Normalize(index) + "]"))
                            continue;
                        yield return new LintFinding(
                            Id, LintHandler.SeverityName(LintSeverity.Warning),
                            routine.Program, routine.Routine, rung.Number, rung.Text,
                            $"{instruction.Key} uses {array}[{index}] and no earlier rung in this routine compares {index} with the array bound. " +
                            "An out-of-range index causes a major fault (4/20). See lgx-instruction-reference, section \"Arrays: subscripts and bounds\".",
                            $"Add a LT/LIMIT/GE/GT check (LES/LIM/GEQ/GRT before v36) of {index} against the array size on an earlier rung (or in the same rung) so the access only runs when it is in range.");
                    }

                    if (Comparisons.Contains(InstructionMnemonics.Canonical(instruction.Key)))
                        foreach (var a in args) checkedIndexes.Add(Normalize(a));
                }
            }
        }
    }

    private static string Normalize(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Yields (array name, index expression) for every non-literal subscript in <paramref name="args"/>.</summary>
    private static IEnumerable<(string Array, string Index)> Subscripts(IEnumerable<string> args) =>
        SubscriptsWithPosition(args).Select(s => (s.Array, s.Index));

    /// <summary>
    /// Yields (array name, index expression, zero-based dimension position) for every non-literal subscript in
    /// <paramref name="args"/>. The position is the subscript's place in a multi-dimensional access such as
    /// <c>Arr[0,Idx]</c> (position 1), so a caller can compare it with the matching declared dimension.
    /// Shared with <c>logix_diagnose</c> so both use one array-operand scan.
    /// </summary>
    /// <param name="args">Instruction operand strings.</param>
    /// <returns>One entry per tag-driven (non-literal) subscript.</returns>
    public static IEnumerable<(string Array, string Index, int Position)> SubscriptsWithPosition(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            for (var i = 0; i < arg.Length; i++)
            {
                if (arg[i] != '[') continue;
                var start = i;
                while (start > 0 && (char.IsLetterOrDigit(arg[start - 1]) || arg[start - 1] is '_' or '.')) start--;
                var depth = 0;
                var sb = new StringBuilder();
                var parts = new List<string>();
                var j = i + 1;
                for (; j < arg.Length; j++)
                {
                    var c = arg[j];
                    if (c == '[') depth++;
                    else if (c == ']' && depth == 0) break;
                    else if (c == ']') depth--;
                    if (c == ',' && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
                parts.Add(sb.ToString());
                var array = arg[start..i];
                for (var p = 0; p < parts.Count; p++)
                {
                    var idx = parts[p].Trim();
                    // Literal subscripts are range-checked by the compiler; only tag-driven indexes can fault at run time.
                    if (idx.Length > 0 && (char.IsLetter(idx[0]) || idx[0] == '_'))
                        yield return (array, idx, p);
                }
            }
        }
    }
}
