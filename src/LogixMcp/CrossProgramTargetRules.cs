using L5Sharp.Core;

/// <summary>
/// Flags a <c>\Program.Tag</c> operand whose program does not exist or does not declare the tag.
/// Rationale: the reference can never resolve, so the project fails verification or the rung never does what was intended.
/// </summary>
public sealed class CrossProgramTargetMissingRule : ILintRule
{
    /// <inheritdoc />
    public string Id => "CrossProgramTargetMissing";

    /// <inheritdoc />
    public string Rationale =>
        "A \\Program.Tag operand naming a program or tag that does not exist cannot resolve; the project fails verification.";

    /// <summary>Reports one error per missing program-qualified target per rung.</summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Error findings naming the program and tag.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var scopes = new TagScopes(context.Project);
        foreach (var (routine, rung, operand) in CrossProgramTargets.QualifiedOperands(context))
        {
            if (!CrossReferenceHandler.IsTargetMissing(scopes, operand)) continue;
            var exists = context.Project.Programs.Any(p =>
                string.Equals(p.Name, operand.Program, StringComparison.OrdinalIgnoreCase));
            var why = exists ? $"program '{operand.Program}' has no tag '{operand.Tag}'" : $"program '{operand.Program}' does not exist";
            yield return new LintFinding(
                Id, LintHandler.SeverityName(LintSeverity.Error),
                routine.Program, routine.Routine, rung.Number, rung.Text,
                $"{operand.Qualified} references a missing target: {why} (program: {operand.Program}, tag: {operand.Tag}).",
                "Correct the program or tag name, or create the missing tag in that program and make it Public.");
        }
    }
}

/// <summary>
/// Flags a <c>\Program.Tag</c> operand that names a tag which exists but is not reachable from a different program.
/// A program tag is reachable only when its Usage is Public, Input or Output; a missing Usage (what a real Logix
/// export has for an ordinary local tag), Local or InOut is not. Rationale: only Public tags (and the Input and
/// Output parameters) can be referenced from another program.
/// </summary>
public sealed class CrossProgramTargetNotPublicRule : ILintRule
{
    /// <summary>True when a tag with this Usage can be referenced from another program (Public, Input or Output).</summary>
    /// <param name="usage">The tag's Usage; a missing attribute is not reachable.</param>
    /// <returns>Whether another program may reference the tag.</returns>
    internal static bool IsReachableFromOtherPrograms(TagUsage? usage) =>
        usage is not null && (Equals(usage, TagUsage.Public) || Equals(usage, TagUsage.Input) || Equals(usage, TagUsage.Output));

    /// <inheritdoc />
    public string Id => "CrossProgramTargetNotPublic";

    /// <inheritdoc />
    public string Rationale =>
        "A tag that is not Public in its program cannot be referenced from another program with \\Program.Tag.";

    /// <summary>Reports one error per reference from another program to an existing non-Public tag.</summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Error findings naming the program and tag.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var scopes = new TagScopes(context.Project);
        foreach (var (routine, rung, operand) in CrossProgramTargets.QualifiedOperands(context))
        {
            if (string.Equals(operand.Program, routine.Program, StringComparison.OrdinalIgnoreCase)) continue;
            if (CrossReferenceHandler.IsTargetMissing(scopes, operand)) continue;
            var name = WriteClassifier.ExtractBaseName(operand.Tag) ?? operand.Tag;
            var program = context.Project.Programs.First(p =>
                string.Equals(p.Name, operand.Program, StringComparison.OrdinalIgnoreCase));
            var tag = program.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tag is null || IsReachableFromOtherPrograms(tag.Usage)) continue;
            yield return new LintFinding(
                Id, LintHandler.SeverityName(LintSeverity.Error),
                routine.Program, routine.Routine, rung.Number, rung.Text,
                $"{operand.Qualified} references tag '{name}' of program '{program.Name}', which is not Public (program: {program.Name}, tag: {name}).",
                "Make the tag Public in its program (or use a Public tag or a program parameter), or reference a controller-scope tag.");
        }
    }
}

/// <summary>
/// Flags an <c>OTE</c>, <c>OTL</c> or <c>OTU</c> whose destination operand is another program's Output parameter
/// (<c>OTE(\Line_A.P6_Out)</c>). Logix rejects the import with <c>RxCMP_E_DIRECT_WRITE_OUTPUT</c>. Reads of the
/// same Output (XIC, XIO, input operands) are allowed, and the owning program may write its own Output.
/// </summary>
public sealed class CrossProgramOutputNotWritableRule : ILintRule
{
    private static readonly HashSet<string> CoilWrites = new(StringComparer.OrdinalIgnoreCase) { "OTE", "OTL", "OTU" };

    /// <inheritdoc />
    public string Id => "CrossProgramOutputNotWritable";

    /// <inheritdoc />
    public string Rationale =>
        "Only the program that owns an Output parameter can write it; a coil in another program aimed at it is rejected by Logix (RxCMP_E_DIRECT_WRITE_OUTPUT).";

    /// <summary>Reports one error per OTE/OTL/OTU in a rung that writes another program's Output parameter.</summary>
    /// <param name="context">Controller-wide lint context.</param>
    /// <returns>Error findings naming the rung and the operand.</returns>
    public IEnumerable<LintFinding> Check(LintContext context)
    {
        var scopes = new TagScopes(context.Project);
        foreach (var routine in context.LadderRoutines)
            foreach (var rung in routine.Rungs)
                foreach (var instruction in rung.Instructions)
                {
                    var mnemonic = instruction.Key.ToUpperInvariant();
                    if (!CoilWrites.Contains(mnemonic)) continue;
                    var operand = RungParser.Operands(instruction).FirstOrDefault();
                    if (operand is null || !operand.IsProgramQualified) continue;
                    if (string.Equals(operand.Program, routine.Program, StringComparison.OrdinalIgnoreCase)) continue;
                    if (CrossReferenceHandler.IsTargetMissing(scopes, operand)) continue;
                    var name = WriteClassifier.ExtractBaseName(operand.Tag) ?? operand.Tag;
                    var program = context.Project.Programs.FirstOrDefault(p =>
                        string.Equals(p.Name, operand.Program, StringComparison.OrdinalIgnoreCase));
                    var tag = program?.Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (tag is null || !Equals(tag.Usage, TagUsage.Output)) continue;
                    yield return new LintFinding(
                        Id, LintHandler.SeverityName(LintSeverity.Error),
                        routine.Program, routine.Routine, rung.Number, rung.Text,
                        $"Rung {rung.Number}: {mnemonic}({operand.Qualified}) writes the Output parameter '{name}' of program '{program!.Name}' " +
                        $"(operand: {operand.Qualified}); only that program can write it ({ReferenceCheckKinds.DirectWriteOutputMnemonic}).",
                        "Write the value inside the owning program, use a Public or Input parameter as the destination, or write a tag of this program; reads of the Output are allowed.");
                }
    }
}

/// <summary>Shared walk over the program-qualified operands of the ladder rungs.</summary>
internal static class CrossProgramTargets
{
    /// <summary>Every distinct program-qualified operand per rung, with its routine and rung.</summary>
    public static IEnumerable<(LintRoutine Routine, LintRung Rung, RungOperand Operand)> QualifiedOperands(LintContext context)
    {
        foreach (var routine in context.LadderRoutines)
            foreach (var rung in routine.Rungs)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var instruction in rung.Instructions)
                    foreach (var operand in RungParser.Operands(instruction))
                        if (operand.IsProgramQualified && seen.Add(operand.Qualified))
                            yield return (routine, rung, operand);
            }
    }
}
