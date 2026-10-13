using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>One occurrence of a tag found inside a program's rung logic.</summary>
/// <param name="Program">Name of the program that contains the routine.</param>
/// <param name="Routine">Name of the routine that contains the rung.</param>
/// <param name="RungNumber">Zero-based rung index within the routine.</param>
/// <param name="Instruction">The instruction key where the tag was found (e.g. XIC, OTE).</param>
/// <param name="Kind">
/// Reference classification: <c>"read"</c>, <c>"write"</c>, or <c>"unknown"</c>.
/// </param>
/// <param name="CrossProgram">
/// <c>true</c> when the rung is in a different program from the tag it references and names the
/// tag with a program qualifier (<c>\P2.Src</c>). Omitted from the JSON when <c>false</c>.
/// </param>
/// <param name="Target">
/// For a cross-program reference, the referenced tag as <c>Program.Tag</c> (e.g. <c>P2.Src</c>);
/// omitted otherwise. Also set when a program-qualified reference names a missing target.
/// </param>
/// <param name="TargetMissing">
/// <c>true</c> when the reference is program-qualified (<c>\Prog.Tag</c>) but the program does not exist or
/// does not declare the tag, so the reference cannot resolve. Omitted from the JSON when <c>false</c>.
/// </param>
public record CrossRefEntry(
    [property: JsonPropertyName("program")]     string Program,
    [property: JsonPropertyName("routine")]     string Routine,
    [property: JsonPropertyName("rungNumber")]  int    RungNumber,
    [property: JsonPropertyName("instruction")] string Instruction,
    [property: JsonPropertyName("kind")]        string Kind,
    [property: JsonPropertyName("crossProgram"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool CrossProgram = false,
    [property: JsonPropertyName("target"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Target = null,
    [property: JsonPropertyName("targetMissing"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool TargetMissing = false);

/// <summary>
/// Strongly-typed success response returned by <see cref="CrossReferenceHandler.Execute"/>.
/// </summary>
/// <param name="References">All occurrences of the tag across all programs and routines.</param>
/// <param name="Source">
/// Always <c>"project-file"</c>. States that the references above were found by
/// scanning the opened or uploaded project file on disk, not the live controller
/// and not any pending (unsaved) edit. For the live routine content including
/// pending edits, call <c>logix_export_rungs</c> instead.
/// </param>
/// <param name="UnparsedRungs">
/// Rungs that could not be parsed and so were not searched (program, routine, rung, error);
/// omitted when every rung parsed. References on all other rungs are unaffected.
/// </param>
public record CrossRefResult(
    [property: JsonPropertyName("references")] IReadOnlyList<CrossRefEntry> References,
    [property: JsonPropertyName("source")]      string                      Source = "project-file")
{
    /// <summary>Rungs the search skipped because they could not be parsed; <c>null</c> when none.</summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// Set when the tag has no parsed reference but appears in rungs that could not be parsed, so
    /// "no references" must not be read as "unused"; <c>null</c> otherwise.
    /// </summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>
    /// Pending rungs that write the analysed tag but were left out because <c>includePending</c> is false
    /// (program, routine, rungIndex, tag); <c>null</c> when none were left out.
    /// </summary>
    [JsonPropertyName("pendingWritersExcluded"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PendingWriter>? PendingWritersExcluded { get; init; }

    /// <summary><c>true</c> when the session image is marked stale and this response may not show the controller's current logic; omitted otherwise.</summary>
    [JsonPropertyName("imageStale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ImageStale { get; init; }

    /// <summary>UTC time (ISO 8601) the image was marked stale; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleSince { get; init; }

    /// <summary>What the image could not follow; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleReason { get; init; }

    /// <summary>The reconnect hint; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

/// <summary>
/// Strongly-typed error response returned when no project is loaded or input is invalid.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record CrossRefError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Machine-readable error kind (e.g. <c>AmbiguousTagName</c>); <c>null</c> for plain errors.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>The name that was requested, for <c>AmbiguousTagName</c>; <c>null</c> otherwise.</summary>
    [JsonPropertyName("tag"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; init; }

    /// <summary>Qualified names (<c>\Program.Tag</c>) the bare name matches, for <c>AmbiguousTagName</c>; <c>null</c> otherwise.</summary>
    [JsonPropertyName("matches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Matches { get; init; }

    /// <summary>Remediation text; <c>null</c> when none.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

/// <summary>
/// Pure handler for the <c>logix_cross_reference</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// Write-classification logic is delegated to the shared <see cref="WriteClassifier"/>.
/// </summary>
public static class CrossReferenceHandler
{
    /// <summary>
    /// Searches every RLL rung in every program and routine of the loaded project for
    /// instructions that reference <paramref name="tagName"/> (exact or member-path prefix).
    /// Each matching instruction produces one <see cref="CrossRefEntry"/> whose
    /// <c>Kind</c> is classified using <see cref="WriteClassifier"/>.
    /// A rung that cannot be parsed is skipped and listed in
    /// <see cref="CrossRefResult.UnparsedRungs"/>; it never fails the search.
    /// Never throws; returns <see cref="CrossRefError"/> when no project is open or input
    /// is invalid.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="tagName">
    /// Exact tag name to search for (case-insensitive). Member paths such as
    /// <c>Cmd_Motor.Enable</c> are also matched when the base name equals this value.
    /// </param>
    /// <param name="session">Optional tag session whose controller image <paramref name="includePending"/> may read.</param>
    /// <param name="includePending">
    /// Read the session's controller image instead of the project file. Defaults to <c>true</c> when the session is
    /// SDK-online and holds an image, otherwise <c>false</c>.
    /// </param>
    /// <returns>
    /// A <see cref="CrossRefResult"/> with every occurrence on success,
    /// or a <see cref="CrossRefError"/> on invalid input or missing project.
    /// </returns>
    public static object Execute(
        ProjectService service, string tagName, TagSessionService? session = null, bool? includePending = null)
    {
        var selection = AnalysisImage.Resolve(service, session, includePending);
        if (selection.Project is null)
            return new CrossRefError("No project loaded. Call logix_open_project first.");

        if (string.IsNullOrWhiteSpace(tagName))
            return new CrossRefError("tagName must not be empty.");

        var project   = selection.Project;
        var ambiguous = AmbiguousTagNames.Find(project, tagName);
        if (ambiguous is not null)
            return new CrossRefError(AmbiguousTagNames.Message(tagName, ambiguous))
            {
                Kind = ErrorKinds.AmbiguousTagName, Tag = tagName.Trim(), Matches = ambiguous, Hint = AmbiguousTagNames.Hint,
            };

        var aoiWrites = WriteClassifier.BuildAoiWriteMap(project);
        var unparsed  = new List<UnparsedRungEntry>();
        var results   = Find(project, tagName, aoiWrites, unparsed);

        // includePending: false leaves pending rungs out; a pending writer must not make the tag look unwritten.
        var pendingWriters = AnalysisImage.PendingWriters(selection, pending =>
            Find(pending, tagName, WriteClassifier.BuildAoiWriteMap(pending), new List<UnparsedRungEntry>())
                .Where(r => r.Kind == "write")
                .Select(r => new PendingWriter(r.Program, r.Routine, r.RungNumber, tagName.Trim())));

        var note      = RungParser.OnlyInUnparsedNote(tagName.Trim(), results.Count > 0, unparsed);
        var staleNote = AnalysisImage.StaleNote(selection, tagName);
        if (staleNote is not null) note = note is null ? staleNote : $"{note} {staleNote}";
        if (pendingWriters.Count > 0)
        {
            var pendingNote = results.Any(r => r.Kind == "write")
                ? AnalysisImage.AlsoWrittenByPending(tagName.Trim(), pendingWriters)
                : AnalysisImage.NotWrittenByAccepted($"'{tagName.Trim()}'", pendingWriters);
            note = note is null ? pendingNote : $"{note} {pendingNote}";
        }

        var stale = ProjectFileStaleness.For(session, selection.Source, service);
        return new CrossRefResult(results, selection.Source)
        {
            UnparsedRungs         = unparsed.Count == 0 ? null : unparsed,
            Note                  = note,
            PendingWritersExcluded = pendingWriters.Count == 0 ? null : pendingWriters,
            ImageStale             = stale is null ? null : true,
            StaleSince             = stale?.StaleSince,
            StaleReason            = stale?.StaleReason,
            Hint                   = stale?.Hint,
        };
    }

    /// <summary>Finds every reference to <paramref name="tagName"/> in <paramref name="project"/>.</summary>
    /// <param name="project">The model to scan.</param>
    /// <param name="tagName">The requested tag.</param>
    /// <param name="aoiWrites">AOI parameter write map of <paramref name="project"/>.</param>
    /// <param name="unparsed">Receives rungs that could not be parsed.</param>
    private static List<CrossRefEntry> Find(
        L5X project, string tagName, IReadOnlyDictionary<string, IReadOnlySet<int>> aoiWrites, List<UnparsedRungEntry> unparsed)
    {
        var (targetProgram, targetTag) = ResolveTarget(project, tagName);

        // An unqualified name also matches \Prog.Tag operands, but only where Prog really owns that tag.
        // When the controller has a tag of that name the request means the controller tag: a program's own
        // same-named tag (its unqualified operands, or \Prog.Tag) is a different tag and is left out.
        var scopes     = new TagScopes(project);
        var targetBase = WriteClassifier.ExtractBaseName(targetTag) ?? targetTag;
        var controllerOnly = targetProgram is null && scopes.ControllerHas(targetBase);

        return Scan(project, aoiWrites, unparsed, (refProgram, operand) =>
        {
            if (operand.IsProgramQualified)
            {
                var owner = operand.Program!;
                if (!WriteClassifier.ArgumentMatchesTag(operand.Tag, targetTag)) return null;
                if (targetProgram is not null
                    ? !string.Equals(owner, targetProgram, StringComparison.OrdinalIgnoreCase)
                    : controllerOnly || !scopes.ProgramOwns(owner, targetBase))
                    return null;
                var cross = !string.Equals(owner, refProgram, StringComparison.OrdinalIgnoreCase);
                return (cross, cross ? $"{owner}.{operand.Tag}" : null);
            }

            // Unqualified operand: inside a qualified target's own program it is that tag.
            if (targetProgram is not null
                && !string.Equals(refProgram, targetProgram, StringComparison.OrdinalIgnoreCase))
                return null;
            if (controllerOnly && scopes.Resolve(refProgram, operand) != TagScopes.Controller) return null;
            return WriteClassifier.ArgumentMatchesTag(operand.Tag, targetTag) ? (false, null) : null;
        });
    }

    /// <summary>
    /// Splits a requested tag name into the owning program (or <c>null</c>) and the tag expression.
    /// Accepts <c>\P2.Src</c>, <c>Program:P2.Src</c> and plain <c>P2.Src</c> where <c>P2</c> is a
    /// program of the project and no controller tag is called <c>P2</c>.
    /// </summary>
    /// <param name="project">The loaded project.</param>
    /// <param name="tagName">The name the caller asked for.</param>
    /// <returns>The program (null for an unqualified tag) and the tag expression.</returns>
    internal static (string? Program, string Tag) ResolveTarget(L5X project, string tagName)
    {
        var operand = RungOperand.Parse(tagName);
        if (operand.IsProgramQualified) return (operand.Program, operand.Tag);

        var text = tagName.Trim();
        var dot  = text.IndexOf('.');
        if (dot > 0 && dot < text.Length - 1)
        {
            var first = text[..dot];
            var program = project.Programs.FirstOrDefault(
                p => string.Equals(p.Name, first, StringComparison.OrdinalIgnoreCase));
            if (program is not null
                && !project.Tags.Any(t => string.Equals(t.Name, first, StringComparison.OrdinalIgnoreCase)))
                return (program.Name, text[(dot + 1)..]);
        }
        return (null, text);
    }

    /// <summary>
    /// Walks every ladder rung of every program and reports each instruction that has an operand the
    /// <paramref name="match"/> callback accepts. A rung that cannot be parsed is added to
    /// <paramref name="unparsed"/> and skipped.
    /// </summary>
    /// <param name="project">The loaded project.</param>
    /// <param name="aoiWrites">AOI parameter write map for classification.</param>
    /// <param name="unparsed">Receives rungs that could not be parsed.</param>
    /// <param name="match">
    /// Given the referencing program and an operand, returns <c>null</c> for no match, or whether the
    /// match is cross-program and its <c>Program.Tag</c> target text.
    /// </param>
    /// <param name="onlyQualified">
    /// When <c>true</c>, rungs whose text has no program qualifier are skipped without parsing.
    /// </param>
    /// <returns>One entry per matching instruction; <c>write</c> wins over other kinds.</returns>
    internal static List<CrossRefEntry> Scan(
        L5X project,
        IReadOnlyDictionary<string, IReadOnlySet<int>> aoiWrites,
        List<UnparsedRungEntry> unparsed,
        Func<string, RungOperand, (bool CrossProgram, string? Target)?> match,
        bool onlyQualified = false)
    {
        var results = new List<CrossRefEntry>();
        var scopes  = new TagScopes(project);
        foreach (var program in project.Programs)
        {
            foreach (var routine in program.Routines)
            {
                // Enumerating Rungs on a non-RLL routine throws — see WriteClassifier.IsLadder.
                if (!WriteClassifier.IsLadder(routine)) continue;

                foreach (var rung in routine.Rungs)
                {
                    if (onlyQualified && !MayHaveProgramQualifier(rung)) continue;

                    foreach (var instruction in RungParser.ParseContained(rung, program.Name, routine.Name, unparsed))
                    {
                        var operands = RungParser.Operands(instruction);
                        string? matchedKind = null;
                        (bool CrossProgram, string? Target) matchedInfo = default;
                        RungOperand? matchedOperand = null;

                        for (int i = 0; i < operands.Count; i++)
                        {
                            var hit = match(program.Name, operands[i]);
                            if (hit is null) continue;

                            string candidateKind = WriteClassifier.ClassifyKind(instruction, i, aoiWrites);

                            // 'write' takes priority — once we find a write reference we stop
                            // searching further argument positions for this instruction.
                            if (candidateKind == "write")
                            {
                                matchedKind = "write";
                                matchedInfo = hit.Value;
                                matchedOperand = operands[i];
                                break;
                            }

                            // First match wins for 'read' / 'unknown'; keep scanning in case
                            // a later position yields 'write'.
                            if (matchedKind is null)
                            {
                                matchedKind = candidateKind;
                                matchedInfo = hit.Value;
                                matchedOperand = operands[i];
                            }
                        }

                        if (matchedKind is not null)
                        {
                            var missing = matchedOperand is { IsProgramQualified: true } q && IsTargetMissing(scopes, q);
                            if (missing && matchedInfo.Target is null)
                                matchedInfo = (matchedInfo.CrossProgram, $"{matchedOperand!.Program}.{matchedOperand.Tag}");
                            results.Add(new CrossRefEntry(
                                Program:      program.Name,
                                Routine:      routine.Name,
                                RungNumber:   rung.Number,
                                Instruction:  instruction.Key,
                                Kind:         matchedKind,
                                CrossProgram: matchedInfo.CrossProgram,
                                Target:       matchedInfo.Target,
                                TargetMissing: missing));
                        }
                    }
                }
            }
        }
        return results;
    }

    /// <summary>
    /// True when a program-qualified operand names a program that does not exist, or a tag that program
    /// does not declare (members and array indexes are ignored; only the base tag is checked).
    /// </summary>
    /// <param name="scopes">Tag index of the project.</param>
    /// <param name="operand">A program-qualified operand.</param>
    internal static bool IsTargetMissing(TagScopes scopes, RungOperand operand)
    {
        var name = WriteClassifier.ExtractBaseName(operand.Tag) ?? operand.Tag;
        return !scopes.ProgramOwns(operand.Program!, name);
    }

    /// <summary>Cheap pre-check: could this rung's text hold a <c>\Prog.Tag</c> operand? True when unreadable.</summary>
    private static bool MayHaveProgramQualifier(Rung rung)
    {
        try
        {
            var text = rung.Text.ToString();
            return text.Contains('\\') || text.Contains(RungParser.ProgramScopePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return true;
        }
    }
}

/// <summary>
/// Detects a bare tag name that names program-scoped tags in more than one program with no controller tag to
/// settle it, so <c>logix_verify_interlock</c> and <c>logix_cross_reference</c> can refuse instead of merging.
/// </summary>
internal static class AmbiguousTagNames
{
    /// <summary>Remediation text shared by the <c>AmbiguousTagName</c> errors.</summary>
    public const string Hint = "Re-run with one of the qualified names in 'matches' (\\Program.Tag).";

    /// <summary>
    /// The qualified names a bare <paramref name="tagName"/> matches, or <c>null</c> when it is not ambiguous.
    /// A name is ambiguous when it is unqualified, no controller tag has that name, and two or more programs
    /// declare a tag of that name.
    /// </summary>
    /// <param name="project">The loaded project.</param>
    /// <param name="tagName">The name as the caller gave it (members and indexes allowed).</param>
    /// <returns>Qualified names in project order, or <c>null</c>.</returns>
    public static IReadOnlyList<string>? Find(L5X project, string tagName)
    {
        var (program, tag) = CrossReferenceHandler.ResolveTarget(project, tagName);
        if (program is not null) return null;
        var baseName = WriteClassifier.ExtractBaseName(tag) ?? tag;
        var scopes   = new TagScopes(project);
        if (scopes.ControllerHas(baseName)) return null;
        var owners = scopes.ProgramsOwning(baseName);
        return owners.Count < 2 ? null : owners.Select(o => $"\\{o}.{tag}").ToList();
    }

    /// <summary>Human-readable message listing the qualified names.</summary>
    /// <param name="tagName">The requested name.</param>
    /// <param name="matches">The qualified names from <see cref="Find"/>.</param>
    public static string Message(string tagName, IReadOnlyList<string> matches) =>
        $"AmbiguousTagName: '{tagName.Trim()}' matches program tags in {matches.Count} programs and no controller tag: " +
        $"{string.Join(", ", matches)}. Use one of the qualified names.";
}
