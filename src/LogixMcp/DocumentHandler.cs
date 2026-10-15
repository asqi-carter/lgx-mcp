using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using L5Sharp.Core;
using L5Program = L5Sharp.Core.Program;

/// <summary>One heading of the generated document: the outline entry for a section.</summary>
/// <param name="Title">Heading text (server-authored: names come from the project, no free text).</param>
/// <param name="Level">Markdown heading level (1 = document title).</param>
/// <param name="Kind">
/// Section kind: <c>overview</c>, <c>datatypes</c>, <c>aois</c>, <c>modules</c>, <c>controller-tags</c>, <c>program</c>,
/// <c>program-tags</c>, <c>routine</c>, <c>rungs</c> or <c>cross-reference</c>.
/// </param>
/// <param name="Program">Owning program for program / routine sections, else <c>null</c>.</param>
/// <param name="Routine">Owning routine for routine sections, else <c>null</c>.</param>
/// <param name="Bytes">UTF-8 size of this section's own markdown (heading plus body).</param>
public record DocumentSection(
    [property: JsonPropertyName("title")]   string  Title,
    [property: JsonPropertyName("level")]   int     Level,
    [property: JsonPropertyName("kind")]    string  Kind,
    [property: JsonPropertyName("program")] string? Program,
    [property: JsonPropertyName("routine")] string? Routine,
    [property: JsonPropertyName("bytes")]   int     Bytes);

/// <summary>Success response of <see cref="DocumentHandler.Execute"/>.</summary>
/// <param name="Markdown">The document, or (when <paramref name="Truncated"/>) just its outline.</param>
/// <param name="Sections">Outline of every section in document order, present even when truncated.</param>
/// <param name="Source"><c>"project-file"</c> for a project opened via <c>logix_open_project</c>; <c>"session-image"</c> when an active session supplies the controller image. Never pending edits.</param>
/// <param name="GeneratedAt">UTC timestamp, ISO 8601.</param>
/// <param name="UntrustedContent">Names of fields that carry wrapped customer-authored text.</param>
/// <param name="Truncated"><c>true</c> when the full document exceeded the size cap (omitted otherwise).</param>
/// <param name="Note">When truncated, names a narrower scope to request instead (omitted otherwise).</param>
public record DocumentResult(
    [property: JsonPropertyName("markdown")]         string                    Markdown,
    [property: JsonPropertyName("sections")]         IReadOnlyList<DocumentSection> Sections,
    [property: JsonPropertyName("source")]           string                    Source,
    [property: JsonPropertyName("generatedAt")]      string                    GeneratedAt,
    [property: JsonPropertyName("untrustedContent")] IReadOnlyList<string>     UntrustedContent,
    [property: JsonPropertyName("truncated")]        bool?                     Truncated = null,
    [property: JsonPropertyName("note")]             string?                   Note = null)
{
    /// <summary>
    /// Rungs in scope that could not be parsed (program, routine, rung, error); omitted when every rung parsed.
    /// They are still listed in the rung sections; only the tag cross-reference and "written by" / "called by"
    /// columns lack their contribution.
    /// </summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary><c>true</c> when the session image could not be kept current after accept, assemble or cancel; omitted otherwise.</summary>
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

    /// <summary>Returns a copy carrying the stale fields of <paramref name="stale"/> (unchanged when <c>null</c>).</summary>
    internal DocumentResult WithStaleness(SessionImageStaleness? stale) =>
        stale is null ? this : this with
        {
            ImageStale  = true,
            StaleSince  = stale.SinceText,
            StaleReason = stale.Reason,
            Hint        = ProjectSourceLabel.StaleHint,
        };
}

/// <summary>Error response of <see cref="DocumentHandler.Execute"/>.</summary>
public record DocumentError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Machine-readable error kind (for example <c>NoProjectLoaded</c>); omitted for untyped errors.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>Next step (for example how to recover from <c>NoProjectLoaded</c>); omitted when none.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

/// <summary>
/// Pure handler for the read-only <c>logix_document</c> MCP tool: deterministic Markdown
/// documentation of the open project. Kept free of MCP-specific attributes so it can be
/// unit-tested directly.
/// </summary>
/// <remarks>
/// Reuses the existing walks: <see cref="ProjectSummaryHandler"/> (overview),
/// <see cref="ListDatatypesHandler"/> (user data types), <see cref="FindTagsHandler"/> (tags),
/// <see cref="GetRoutineHandler"/> (numbered rungs) and the <see cref="CrossReferenceHandler"/>
/// classification primitives (<see cref="WriteClassifier"/>), applied in one pass instead of
/// one scan per tag. Output is deterministic apart from <c>generatedAt</c>.
/// </remarks>
public static class DocumentHandler
{
    /// <summary>Default cap on the markdown size, in UTF-8 bytes (200 KB).</summary>
    public const int DefaultMaxBytes = 200 * 1024;

    private static readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    /// <summary>
    /// Documents the loaded project. Never throws for bad input; returns a
    /// <see cref="DocumentError"/> instead.
    /// </summary>
    /// <param name="service">
    /// Project holder: the open project, or the project image a connect/upload session
    /// registered — the same source <c>logix_gate_report</c> and <c>logix_lint</c> use.
    /// </param>
    /// <param name="scope">
    /// <c>controller</c> (default), <c>program</c> or <c>routine</c> (case-insensitive). When
    /// omitted it is inferred: <paramref name="routine"/> given means routine, else
    /// <paramref name="program"/> given means program.
    /// </param>
    /// <param name="program">Program name (case-insensitive); required for program and routine scope.</param>
    /// <param name="routine">Routine name (case-insensitive); required for routine scope.</param>
    /// <param name="includeRungText">When <c>false</c>, rung neutral text is omitted (numbers and comments stay).</param>
    /// <param name="maxBytes">Markdown size cap in UTF-8 bytes; defaults to <see cref="DefaultMaxBytes"/>.</param>
    /// <param name="session">Optional tag session; a live controller makes <c>source</c> <c>"session-image"</c>.</param>
    /// <returns>A <see cref="DocumentResult"/> or a <see cref="DocumentError"/>.</returns>
    public static object Execute(
        ProjectService service,
        string? scope           = null,
        string? program         = null,
        string? routine         = null,
        bool    includeRungText = true,
        int     maxBytes        = DefaultMaxBytes,
        TagSessionService? session = null)
    {
        // An SDK-online session documents its controller image (the result is labelled 'session-image'), as lint
        // does, so a change applied to the image after accept or assemble shows in the document.
        if (session is { IsLiveController: true, ControllerImage: { } sessionImage } &&
            !ReferenceEquals(service.Project, sessionImage))
        {
            var imageService = new ProjectService();
            imageService.RegisterFromUpload(sessionImage, service.FilePath ?? string.Empty, service.UploadedFrom);
            service = imageService;
        }

        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_document");
            return new DocumentError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        bool hasProgram = !string.IsNullOrWhiteSpace(program);
        bool hasRoutine = !string.IsNullOrWhiteSpace(routine);

        string effScope = string.IsNullOrWhiteSpace(scope)
            ? (hasRoutine ? "routine" : hasProgram ? "program" : "controller")
            : scope.Trim().ToLowerInvariant();

        if (effScope is not ("controller" or "program" or "routine"))
            return new DocumentError(
                $"Unknown scope '{scope}'. Valid scopes: controller, program, routine.")
            { Kind = ErrorKinds.InvalidArguments };
        if (effScope != "controller" && !hasProgram)
            return new DocumentError($"scope '{effScope}' requires the 'program' parameter.")
            { Kind = ErrorKinds.InvalidArguments };
        if (effScope == "routine" && !hasRoutine)
            return new DocumentError("scope 'routine' requires the 'routine' parameter.")
            { Kind = ErrorKinds.InvalidArguments };

        var project = service.Project!;

        L5Program? prog = null;
        if (effScope != "controller")
        {
            prog = project.Programs.FirstOrDefault(p => Same(p.Name, program!));
            if (prog is null)
            {
                var available = string.Join(", ", project.Programs.Select(p => p.Name));
                return new DocumentError(
                    $"Program '{program}' not found in the open project. " +
                    $"Available programs: {(available.Length > 0 ? available : "(none)")}.")
                { Kind = ErrorKinds.ProgramNotFound };
            }
        }

        Routine? rout = null;
        if (effScope == "routine")
        {
            rout = prog!.Routines.FirstOrDefault(r => Same(r.Name, routine!));
            if (rout is null)
            {
                var available = string.Join(", ", prog.Routines.Select(r => r.Name));
                return new DocumentError(
                    $"Routine '{routine}' not found in program '{prog.Name}'. " +
                    $"Available routines: {(available.Length > 0 ? available : "(none)")}.")
                { Kind = ErrorKinds.RoutineNotFound };
            }
        }

        var doc = new DocBuilder();
        var unparsed = new DedupSink();
        var summary = (ProjectSummaryResult)ProjectSummaryHandler.Execute(service);

        switch (effScope)
        {
            case "controller":
                var writtenAll = BuildWriteIndex(project, project.Programs, unparsed);
                BuildOverview(doc, service, summary);
                BuildDataTypes(doc, project);
                BuildAddOnInstructions(doc, project);
                BuildModules(doc, project);
                BuildControllerTags(doc, project, writtenAll);
                foreach (var p in project.Programs)
                    BuildProgram(doc, service, summary, p, 2, includeRungText, writtenAll, unparsed);
                BuildCrossReference(doc, project, project.Programs, null, unparsed);
                break;

            case "program":
                doc.Begin(1, $"Program {prog!.Name}", "program", prog.Name, null);
                doc.Line($"Controller: {Code(summary.Controller)}. Source: {SourceDescription(service)}.");
                RoutineTable(doc, project, prog, unparsed);
                doc.End();
                BuildProgramBody(doc, service, summary, prog, 2, includeRungText,
                    BuildWriteIndex(project, new[] { prog }, unparsed));
                BuildCrossReference(doc, project, new[] { prog }, null, unparsed);
                break;

            default:
                doc.Begin(1, $"Routine {prog!.Name}/{rout!.Name}", "routine", prog.Name, rout.Name);
                doc.Line($"Controller: {Code(summary.Controller)}. Program: {Code(prog.Name)}. " +
                         $"Type: {rout.Type.Name}.");
                doc.End();
                BuildRoutineBody(doc, service, prog, rout, 2, includeRungText);
                BuildCrossReference(doc, project, new[] { prog }, rout, unparsed);
                break;
        }

        var sections    = doc.Sections;
        var markdown    = doc.ToString();
        var generatedAt = DateTimeOffset.UtcNow.ToString("o");
        var source      = ProjectSourceLabel.Resolve(session);
        var staleness   = ProjectSourceLabel.Staleness(session, source);
        var untrusted   = new[] { "markdown" };
        var unparsedRungs = unparsed.Count == 0 ? null : unparsed.ToList();

        if (Encoding.UTF8.GetByteCount(markdown) <= maxBytes)
            return new DocumentResult(markdown, sections, source, generatedAt, untrusted)
            {
                UnparsedRungs = unparsedRungs,
            }.WithStaleness(staleness);

        // Over the cap: return the outline only, and name a narrower scope to ask for.
        var note = NarrowerScopeNote(effScope, sections, prog?.Name, maxBytes);
        var outline = new StringBuilder();
        outline.AppendLine($"# Outline ({effScope} scope; full document truncated)");
        outline.AppendLine();
        foreach (var s in sections)
            outline.AppendLine($"{new string(' ', Math.Max(0, s.Level - 1) * 2)}- {s.Title} ({s.Kind}, {s.Bytes} bytes)");
        outline.AppendLine();
        outline.AppendLine($"> {note}");

        return new DocumentResult(
            outline.ToString(), sections, source, generatedAt, untrusted,
            Truncated: true, Note: note)
        {
            UnparsedRungs = unparsedRungs,
        }.WithStaleness(staleness);
    }

    /// <summary>Serializes the result of <see cref="Execute"/> to compact JSON.</summary>
    public static string Serialize(object result) => JsonSerializer.Serialize(result, _options);

    // ------------------------------------------------------------------ //
    //  Section builders                                                   //
    // ------------------------------------------------------------------ //

    private static void BuildOverview(
        DocBuilder doc, ProjectService service, ProjectSummaryResult summary)
    {
        var ctrl = service.Project!.Controller;
        doc.Begin(1, $"Controller {summary.Controller}", "overview", null, null);
        doc.Line($"- Name: {Code(summary.Controller)}");
        doc.Line($"- Type: {Code(ctrl.ProcessorType ?? string.Empty)}");
        doc.Line($"- Revision: {Code(ctrl.Revision.ToString())}");
        doc.Line($"- Source: {SourceDescription(service)}");
        doc.Line($"- Schema revision: {Code(summary.SchemaRevision)}");
        doc.Line($"- Controller tags: {summary.ControllerTags}");
        doc.Line($"- Programs: {summary.Programs.Count}");
        doc.Line();
        if (summary.Programs.Count > 0)
        {
            doc.Line("| Program | Tags | Routines |");
            doc.Line("|---|---|---|");
            foreach (var p in summary.Programs)
                doc.Line($"| {Cell(p.Name)} | {p.Tags} | {p.Routines} |");
        }

        doc.Line();
        doc.Line("Tasks:");
        doc.Line();
        BuildTaskTable(doc, service.Project!);
        doc.End();
    }

    /// <summary>Where the documented project came from: its file path, or the controller it was uploaded from.</summary>
    private static string SourceDescription(ProjectService service) =>
        !string.IsNullOrWhiteSpace(service.UploadedFrom)
            ? $"uploaded from {Code(service.UploadedFrom)}"
            : service.FilePath is { Length: > 0 } path ? Code(path) : "(unknown)";

    private static void BuildTaskTable(DocBuilder doc, L5X project)
    {
        if (project.Tasks.Count == 0) { doc.Line("No tasks."); return; }
        doc.Line("| Task | Type | Rate | Programs |");
        doc.Line("|---|---|---|---|");
        foreach (var t in project.Tasks)
        {
            var rate = t.Type == TaskType.Periodic && t.Rate is { } hz ? $"{(float)hz} ms" : "n/a";
            var progs = string.Join(", ", t.ScheduledPrograms.Select(sp => sp.Name.ToString()));
            doc.Line($"| {Cell(t.Name)} | {Cell(t.Type.Name)} | {rate} | " +
                     $"{(progs.Length > 0 ? Cell(progs) : "(none)")} |");
        }
    }

    private static void BuildDataTypes(DocBuilder doc, L5X project)
    {
        doc.Begin(2, "Data types", "datatypes", null, null);
        var types = project.DataTypes.Where(t => t.Class == DataTypeClass.User).ToList();
        if (types.Count == 0)
            doc.Line("No user-defined data types.");
        foreach (var t in types)
        {
            var members = t.Members.Where(m => !m.Hidden).ToList();
            doc.Line();
            doc.Line($"**{Cell(t.Name)}** ({t.Members.Count()} members)");
            doc.Line();
            doc.Line(DescriptionCell(t.Description));
            doc.Line();
            doc.Line("| Member | Type | Dimension | Description |");
            doc.Line("|---|---|---|---|");
            foreach (var m in members)
                doc.Line($"| {Cell(m.Name)} | {Cell(m.DataType)} | {Cell(m.Dimension.ToString())} | " +
                         $"{DescriptionCell(m.Description)} |");
        }
        doc.End();
    }

    /// <summary>
    /// Add-On Instruction section: one block per AOI with its parameter table. Omitted entirely
    /// when the project defines no AOIs.
    /// </summary>
    private static void BuildAddOnInstructions(DocBuilder doc, L5X project)
    {
        var aois = project.AddOnInstructions.ToList();
        if (aois.Count == 0) return;

        doc.Begin(2, "Add-On instructions", "aois", null, null);
        foreach (var a in aois)
        {
            doc.Line();
            doc.Line($"**{Cell(a.Name)}** (revision {Code(a.Revision?.ToString() ?? string.Empty)})");
            doc.Line();
            doc.Line(DescriptionCell(a.Description));
            doc.Line();
            doc.Line("| Parameter | Type | Usage | Required | Visible | Description |");
            doc.Line("|---|---|---|---|---|---|");
            foreach (var p in a.Parameters)
                doc.Line($"| {Cell(p.Name)} | {Cell(p.DataType?.ToString() ?? string.Empty)} | " +
                         $"{Cell(p.Usage?.Name ?? string.Empty)} | {YesNo(p.Required)} | {YesNo(p.Visible)} | " +
                         $"{DescriptionCell(p.Description)} |");
        }
        doc.End();
    }

    /// <summary>
    /// I/O module section, rendered from <see cref="ModuleTreeBuilder.Build"/> — the same shared
    /// builder behind <c>logix_list_modules</c> — so both tools agree on order (depth-first from
    /// <c>Local</c>), parent, slot/address and tag prefix. Omitted entirely when the project has none.
    /// </summary>
    private static void BuildModules(DocBuilder doc, L5X project)
    {
        var modules = ModuleTreeBuilder.Build(project);
        if (modules.Count == 0) return;

        doc.Begin(2, "I/O modules", "modules", null, null);
        doc.Line("| Module | Catalog number | Parent | Slot | Tag prefix |");
        doc.Line("|---|---|---|---|---|");
        foreach (var m in modules)
        {
            // The root (Local) has no parent; slot is the slot number or, for Ethernet modules, the address.
            doc.Line($"| {Dash(Cell(m.Name))} | {Dash(Cell(m.CatalogNumber))} | " +
                     $"{Dash(Cell(m.Parent ?? string.Empty))} | {Dash(Cell(m.SlotOrAddress ?? string.Empty))} | " +
                     $"{Dash(Cell(m.TagPrefix ?? string.Empty))} |");
        }
        doc.End();
    }

    private static void BuildControllerTags(
        DocBuilder doc, L5X project, IReadOnlyDictionary<string, SortedSet<string>> written)
    {
        doc.Begin(2, "Controller tags", "controller-tags", null, null);
        TagTable(doc, project.Tags, string.Empty, written);
        doc.End();
    }

    /// <summary>
    /// Tag table: name, type, description, External Access and "written by" (the
    /// <c>Program/Routine</c> locations of ladder instructions that write the tag, or a dash).
    /// </summary>
    private static void TagTable(
        DocBuilder doc, IEnumerable<Tag> tags, string scope,
        IReadOnlyDictionary<string, SortedSet<string>> written)
    {
        var list = tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var descriptions = new SafeTagDescriptions();
        if (list.Count == 0) { doc.Line("No tags."); return; }
        doc.Line("| Tag | Type | Description | External Access | Written by |");
        doc.Line("|---|---|---|---|---|");
        foreach (var t in list)
        {
            var by = written.TryGetValue(WriteKey(scope, t.Name), out var set)
                ? Cell(string.Join(", ", set)) : "—";
            doc.Line($"| {Cell(t.Name)} | {Cell(t.DataType)} | " +
                     $"{DescriptionCell(descriptions.Read(t))} | {AccessCell(t.ExternalAccess?.Name)} | {by} |");
        }
    }

    private static string WriteKey(string scope, string tag) => $"{scope}\u0000{tag}";

    /// <summary>
    /// One pass over the ladder routines of <paramref name="programs"/> collecting, per tag, the
    /// <c>Program/Routine</c> locations that write it (same classification as the cross-reference).
    /// A name that matches a program tag resolves to that program's tag; otherwise to the
    /// controller tag. Keyed by <see cref="WriteKey"/> (empty scope = controller).
    /// </summary>
    private static Dictionary<string, SortedSet<string>> BuildWriteIndex(
        L5X project, IEnumerable<L5Program> programs, ICollection<UnparsedRungEntry> unparsed)
    {
        var index = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var aoiWrites = WriteClassifier.BuildAoiWriteMap(project);

        foreach (var p in programs)
        {
            var local = new HashSet<string>(p.Tags.Select(t => t.Name.ToString()), StringComparer.OrdinalIgnoreCase);
            foreach (var r in p.Routines.Where(WriteClassifier.IsLadder))
            foreach (var rung in r.Rungs)
            foreach (var ins in RungParser.ParseContained(rung, p.Name, r.Name, unparsed))
            {
                var args = ins.Arguments.ToList();
                for (int i = 0; i < args.Count; i++)
                {
                    var name = WriteClassifier.ExtractBaseName(args[i].ToString());
                    if (name is null || !(char.IsLetter(name[0]) || name[0] == '_')) continue;
                    if (WriteClassifier.ClassifyKind(ins, i, aoiWrites) != "write") continue;

                    var key = WriteKey(local.Contains(name) ? p.Name.ToString() : string.Empty, name);
                    if (!index.TryGetValue(key, out var set))
                        index[key] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add($"{p.Name}/{r.Name}");
                }
            }
        }
        return index;
    }

    private static void BuildProgram(
        DocBuilder doc, ProjectService service, ProjectSummaryResult summary,
        L5Program prog, int level, bool includeRungText,
        IReadOnlyDictionary<string, SortedSet<string>> written, ICollection<UnparsedRungEntry> unparsed)
    {
        doc.Begin(level, $"Program {prog.Name}", "program", prog.Name, null);
        var main = string.IsNullOrEmpty(prog.MainRoutineName) ? "(none)" : Code(prog.MainRoutineName);
        doc.Line($"Main routine: {main}.");
        RoutineTable(doc, service.Project!, prog, unparsed);
        doc.End();
        BuildProgramBody(doc, service, summary, prog, level + 1, includeRungText, written);
    }

    /// <summary>
    /// Routine table of a program: name, type, rung count and "called by" (JSR cross-reference
    /// within the program; JSR cannot cross programs). Non-ladder routines show no rung count and
    /// their JSR calls are not visible, as with the linter.
    /// </summary>
    private static void RoutineTable(
        DocBuilder doc, L5X project, L5Program prog, ICollection<UnparsedRungEntry> unparsed)
    {
        doc.Line();
        if (prog.Routines.Count == 0) { doc.Line("No routines."); return; }

        var calledBy = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var caller in prog.Routines.Where(WriteClassifier.IsLadder))
        foreach (var rung in caller.Rungs)
        foreach (var ins in RungParser.ParseContained(rung, prog.Name, caller.Name, unparsed))
        {
            if (!string.Equals(ins.Key, "JSR", StringComparison.OrdinalIgnoreCase)) continue;
            var target = ins.Arguments.FirstOrDefault()?.ToString();
            if (string.IsNullOrWhiteSpace(target)) continue;
            if (!calledBy.TryGetValue(target, out var set))
                calledBy[target] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(caller.Name.ToString());
        }

        doc.Line("| Routine | Type | Rungs | Called by |");
        doc.Line("|---|---|---|---|");
        foreach (var r in prog.Routines)
        {
            var rungs  = WriteClassifier.IsLadder(r) ? r.Rungs.Count.ToString() : "n/a";
            var callers = calledBy.TryGetValue(r.Name.ToString(), out var set)
                ? Cell(string.Join(", ", set)) : string.Empty;
            doc.Line($"| {Cell(r.Name)} | {r.Type.Name} | {rungs} | {callers} |");
        }
    }

    private static void BuildProgramBody(
        DocBuilder doc, ProjectService service, ProjectSummaryResult summary,
        L5Program prog, int level, bool includeRungText,
        IReadOnlyDictionary<string, SortedSet<string>> written)
    {
        doc.Begin(level, $"Tags of {prog.Name}", "program-tags", prog.Name, null);
        TagTable(doc, prog.Tags, prog.Name, written);
        doc.End();

        foreach (var r in prog.Routines)
            BuildRoutineBody(doc, service, prog, r, level, includeRungText);
    }

    private static void BuildRoutineBody(
        DocBuilder doc, ProjectService service, L5Program prog, Routine rout, int level, bool includeRungText)
    {
        doc.Begin(level, $"Routine {prog.Name}/{rout.Name}", "routine", prog.Name, rout.Name);
        doc.Line($"Type: {rout.Type.Name}.");
        if (!WriteClassifier.IsLadder(rout))
        {
            doc.Line();
            doc.Line($"Not a ladder (RLL) routine: its {rout.Type.Name} content is not rendered.");
            doc.End();
            return;
        }

        var result = GetRoutineHandler.Execute(service, prog.Name, rout.Name);
        if (result is not GetRoutineResult r)
        {
            doc.Line();
            doc.Line("Rungs unavailable.");
            doc.End();
            return;
        }
        doc.End();

        doc.Begin(level + 1, $"Rungs of {prog.Name}/{rout.Name}", "rungs", prog.Name, rout.Name);
        if (r.Rungs.Count == 0) doc.Line("No rungs.");
        foreach (var rung in r.Rungs)
        {
            doc.Line();
            doc.Line($"**Rung {rung.Number}**");
            // Comment arrives wrapped untrusted from GetRoutineHandler; an empty comment is
            // still a (wrapped) empty string, so test the unwrapped length.
            // A missing comment is shown as a dash, never invented.
            doc.Line();
            doc.Line(IsEmptyWrapped(rung.Comment) ? "—" : rung.Comment.Replace("\r", "").Replace("\n", " "));
            if (includeRungText)
            {
                doc.Line();
                doc.Line(CodeSpan(UntrustedWrapper.WrapRungText(rung.Text)));
            }
        }
        doc.End();
    }

    private static bool IsEmptyWrapped(string wrapped) =>
        string.IsNullOrEmpty(wrapped) || wrapped == UntrustedWrapper.WrapRungComment(string.Empty);

    /// <summary>
    /// One-pass tag cross-reference over <paramref name="programs"/> (restricted to
    /// <paramref name="onlyRoutine"/> when given), using the same read/write classification as
    /// <see cref="CrossReferenceHandler"/>.
    /// </summary>
    private static void BuildCrossReference(
        DocBuilder doc, L5X project, IEnumerable<L5Program> programs, Routine? onlyRoutine,
        ICollection<UnparsedRungEntry> unparsed)
    {
        doc.Begin(2, "Tag cross-reference", "cross-reference", null, null);

        var aoiWrites = WriteClassifier.BuildAoiWriteMap(project);
        var reads   = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var writes  = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in programs)
        {
            foreach (var r in p.Routines)
            {
                if (onlyRoutine is not null && r != onlyRoutine) continue;
                if (!WriteClassifier.IsLadder(r)) continue;
                var where = $"{p.Name}/{r.Name}";

                foreach (var rung in r.Rungs)
                foreach (var ins in RungParser.ParseContained(rung, p.Name, r.Name, unparsed))
                {
                    var args = ins.Arguments.ToList();
                    for (int i = 0; i < args.Count; i++)
                    {
                        var name = WriteClassifier.ExtractBaseName(args[i].ToString());
                        if (name is null || !(char.IsLetter(name[0]) || name[0] == '_')) continue;

                        var target = WriteClassifier.ClassifyKind(ins, i, aoiWrites) switch
                        {
                            "write" => writes,
                            "read"  => reads,
                            _       => unknown,
                        };
                        if (!target.TryGetValue(name, out var set))
                            target[name] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(where);
                    }
                }
            }
        }

        var all = reads.Keys.Concat(writes.Keys).Concat(unknown.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (all.Count == 0)
        {
            doc.Line("No tag references in ladder routines.");
        }
        else
        {
            doc.Line("| Tag | Read by | Written by | Unclassified |");
            doc.Line("|---|---|---|---|");
            foreach (var n in all)
                doc.Line($"| {Cell(n)} | {Join(reads, n)} | {Join(writes, n)} | {Join(unknown, n)} |");
        }
        doc.End();
    }

    private static string Join(SortedDictionary<string, SortedSet<string>> d, string key) =>
        d.TryGetValue(key, out var set) ? Cell(string.Join(", ", set)) : string.Empty;

    // ------------------------------------------------------------------ //
    //  Truncation                                                         //
    // ------------------------------------------------------------------ //

    private static string NarrowerScopeNote(
        string scope, IReadOnlyList<DocumentSection> sections, string? programName, int maxBytes)
    {
        var head = $"The full document exceeds the {maxBytes / 1024} KB cap, so only the outline is returned.";
        switch (scope)
        {
            case "controller":
            {
                var big = sections.Where(s => s.Kind is "program" or "program-tags" or "routine" or "rungs")
                    .GroupBy(s => s.Program!)
                    .OrderByDescending(g => g.Sum(s => s.Bytes)).FirstOrDefault();
                var example = big?.Key ?? "<program>";
                return $"{head} Ask for a narrower scope: logix_document(scope: \"program\", program: \"{example}\") " +
                       $"for one program, or scope: \"routine\" with program and routine for one routine.";
            }
            case "program":
            {
                var big = sections.Where(s => s.Kind is "routine" or "rungs" && s.Routine is not null)
                    .GroupBy(s => s.Routine!)
                    .OrderByDescending(g => g.Sum(s => s.Bytes)).FirstOrDefault();
                var example = big?.Key ?? "<routine>";
                return $"{head} Ask for a narrower scope: logix_document(scope: \"routine\", " +
                       $"program: \"{programName}\", routine: \"{example}\") for one routine.";
            }
            default:
                return $"{head} This is already the narrowest scope; set includeRungText: false to drop rung text.";
        }
    }

    // ------------------------------------------------------------------ //
    //  Markdown helpers                                                   //
    // ------------------------------------------------------------------ //

    private static string Code(string s) => $"`{s.Replace("`", "'")}`";

    /// <summary>Single-line Markdown code span; the delimiter outgrows any backtick run inside.</summary>
    private static string CodeSpan(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " ");
        int run = 0, longest = 0;
        foreach (char c in s) { run = c == '`' ? run + 1 : 0; longest = Math.Max(longest, run); }
        var fence = new string('`', longest + 1);
        var pad = s.StartsWith('`') || s.EndsWith('`') ? " " : string.Empty;
        return $"{fence}{pad}{s}{pad}{fence}";
    }

    /// <summary>Makes a value safe for a single Markdown table cell.</summary>
    private static string Cell(string s) =>
        s.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");

    /// <summary>Wrapped (untrusted) description for a table cell; a dash when there is none.</summary>
    private static string DescriptionCell(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "—" : Cell(UntrustedWrapper.WrapTagDescription(text));

    /// <summary>External Access in Logix Designer's display form (L5Sharp names are <c>ReadWrite</c> / <c>ReadOnly</c>).</summary>
    private static string AccessCell(string? name) => name switch
    {
        null or "" => "—",
        "ReadWrite" => "Read/Write",
        "ReadOnly"  => "Read Only",
        _           => Cell(name),
    };

    private static string Dash(string s) => s.Length == 0 ? "—" : s;

    private static string YesNo(bool? b) => b is null ? "—" : b.Value ? "yes" : "no";

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Collects unparsed rungs, ignoring a rung already recorded by an earlier pass over the same routine.</summary>
    private sealed class DedupSink : System.Collections.ObjectModel.Collection<UnparsedRungEntry>
    {
        protected override void InsertItem(int index, UnparsedRungEntry item)
        {
            if (!Contains(item)) base.InsertItem(index, item);
        }
    }

    /// <summary>Accumulates markdown and the per-section outline with byte sizes.</summary>
    private sealed class DocBuilder
    {
        private readonly StringBuilder _all = new();
        private readonly List<DocumentSection> _sections = new();
        private StringBuilder? _cur;
        private (int Level, string Title, string Kind, string? Program, string? Routine)? _head;

        public IReadOnlyList<DocumentSection> Sections => _sections;

        public void Begin(int level, string title, string kind, string? program, string? routine)
        {
            _head = (level, title, kind, program, routine);
            _cur  = new StringBuilder();
            _cur.Append(new string('#', level)).Append(' ').AppendLine(title).AppendLine();
        }

        public void Line(string text = "") => _cur!.AppendLine(text);

        public void End()
        {
            var h = _head!.Value;
            _cur!.AppendLine();
            var text = _cur.ToString();
            _all.Append(text);
            _sections.Add(new DocumentSection(
                h.Title, h.Level, h.Kind, h.Program, h.Routine, Encoding.UTF8.GetByteCount(text)));
            _cur = null;
        }

        public override string ToString() => _all.ToString();
    }
}
