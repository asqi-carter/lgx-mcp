using L5Sharp.Core;

/// <summary>
/// Resolves which scope a rung operand names, so a program-scoped tag is never confused with a controller
/// tag or with a same-named tag in another program. An unqualified operand in program <c>P</c> is
/// <c>P</c>'s tag when <c>P</c> declares one of that name, otherwise the controller tag; a
/// <c>\Q.Tag</c> operand is always <c>Q</c>'s tag.
/// </summary>
internal sealed class TagScopes
{
    /// <summary>Scope name used for controller-scoped tags.</summary>
    internal const string Controller = "";

    private readonly HashSet<string> _controllerTags;
    private readonly Dictionary<string, HashSet<string>> _programTags;

    /// <summary>Indexes the tags declared by <paramref name="project"/>.</summary>
    /// <param name="project">The loaded project.</param>
    public TagScopes(L5X project)
    {
        _controllerTags = project.Tags.Select(t => t.Name.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _programTags = project.Programs.ToDictionary(
            p => p.Name.ToString(),
            p => p.Tags.Select(t => t.Name.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the controller scope declares a tag called <paramref name="name"/>.</summary>
    /// <param name="name">Bare tag name.</param>
    public bool ControllerHas(string name) => _controllerTags.Contains(name);

    /// <summary>True when the controller scope or any program declares a tag called <paramref name="name"/>.</summary>
    /// <param name="name">Bare tag name.</param>
    public bool Declares(string name) =>
        _controllerTags.Contains(name) || _programTags.Values.Any(t => t.Contains(name));

    /// <summary>True when <paramref name="program"/> declares a tag called <paramref name="name"/>.</summary>
    /// <param name="program">Program name.</param>
    /// <param name="name">Bare tag name.</param>
    public bool ProgramOwns(string program, string name) =>
        _programTags.TryGetValue(program, out var tags) && tags.Contains(name);

    /// <summary>
    /// Programs that declare a tag called <paramref name="name"/>, in project order.
    /// </summary>
    /// <param name="name">Bare tag name.</param>
    /// <returns>The owning program names; empty when no program declares the tag.</returns>
    public IReadOnlyList<string> ProgramsOwning(string name) =>
        _programTags.Where(kv => kv.Value.Contains(name)).Select(kv => kv.Key).ToList();

    /// <summary>
    /// The scope an operand written in <paramref name="refProgram"/> designates.
    /// </summary>
    /// <param name="refProgram">Program that contains the rung.</param>
    /// <param name="operand">The operand.</param>
    /// <returns>The owning program's name, or <see cref="Controller"/> (empty) for a controller tag.</returns>
    public string Resolve(string refProgram, RungOperand operand)
    {
        if (operand.IsProgramQualified) return operand.Program!;
        var name = WriteClassifier.ExtractBaseName(operand.Tag) ?? operand.Tag;
        return ProgramOwns(refProgram, name) ? refProgram : Controller;
    }

    /// <summary>Same as <see cref="Resolve(string, RungOperand)"/> for operand text.</summary>
    /// <param name="refProgram">Program that contains the rung.</param>
    /// <param name="operandText">Operand text such as <c>Bit</c> or <c>\P2.Bit</c>.</param>
    public string Resolve(string refProgram, string operandText) => Resolve(refProgram, RungOperand.Parse(operandText));
}
