using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using L5Sharp.Core;
using L5Program = L5Sharp.Core.Program;
using SysTask = System.Threading.Tasks.Task;

// ── Suspect types ────────────────────────────────────

/// <summary>A rung that cites a timer or array operand. Rung text is wrapped as untrusted.</summary>
/// <param name="Program">Program that contains the routine.</param>
/// <param name="Routine">Routine that contains the rung.</param>
/// <param name="RungNumber">Zero-based rung number.</param>
/// <param name="Instruction">Instruction key at the operand (e.g. ADD, TON).</param>
/// <param name="Rung">Rung neutral text wrapped with <see cref="UntrustedWrapper.WrapRungText"/>.</param>
public sealed record RungCitation(
    [property: JsonPropertyName("program")]     string Program,
    [property: JsonPropertyName("routine")]     string Routine,
    [property: JsonPropertyName("rungNumber")]  int    RungNumber,
    [property: JsonPropertyName("instruction")] string Instruction,
    [property: JsonPropertyName("rung")]        string Rung);

/// <summary>
/// Advisory suspect for a 4/20 fault: a ladder rung with an array operand whose subscript is a tag.
/// </summary>
/// <param name="Citation">Where the operand is.</param>
/// <param name="Array">Array operand as written, without the subscript (e.g. <c>Probe_Arr</c>).</param>
/// <param name="Index">Subscript expression as written (e.g. <c>Probe_Idx</c>).</param>
/// <param name="IndexValue">Subscript's current value when it could be read; omitted otherwise.</param>
/// <param name="Dimension">Declared size of the matching array dimension; omitted when unknown.</param>
/// <param name="OutOfRange">True when the current index is negative or &gt;= the dimension (ranked first).</param>
/// <param name="Summary">Server-authored one-line description, e.g. "index currently 5, array size 2".</param>
public sealed record ArrayIndexSuspect(
    [property: JsonPropertyName("citation")] RungCitation Citation,
    [property: JsonPropertyName("array")]    string Array,
    [property: JsonPropertyName("index")]    string Index,
    [property: JsonPropertyName("indexValue"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? IndexValue,
    [property: JsonPropertyName("dimension"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Dimension,
    [property: JsonPropertyName("outOfRange")] bool OutOfRange,
    [property: JsonPropertyName("summary")]    string Summary)
{
    /// <summary>Always <c>arrayIndex</c>.</summary>
    [JsonPropertyName("kind")] public string Kind => "arrayIndex";
}

/// <summary>Advisory suspect for a 4/34 fault: a timer whose .PRE reads negative.</summary>
/// <param name="Timer">Timer tag (or array element) name as read.</param>
/// <param name="Pre">The negative .PRE value read.</param>
/// <param name="References">Rungs that reference the timer (from <see cref="CrossReferenceHandler"/>).</param>
/// <param name="Summary">Server-authored one-line description.</param>
public sealed record NegativePreSuspect(
    [property: JsonPropertyName("timer")]      string Timer,
    [property: JsonPropertyName("pre")]        long Pre,
    [property: JsonPropertyName("references")] IReadOnlyList<RungCitation> References,
    [property: JsonPropertyName("summary")]    string Summary)
{
    /// <summary>Always <c>negativeTimerPre</c>.</summary>
    [JsonPropertyName("kind")] public string Kind => "negativeTimerPre";
}

/// <summary>Suspects plus an optional note explaining an empty or partial list.</summary>
/// <param name="Suspects">Ranked suspects (most likely first).</param>
/// <param name="Note">Why the list is empty or partial; null when there is nothing to explain.</param>
/// <param name="UnparsedRungs">Rungs that could not be parsed and so were not scanned; null when every rung parsed.</param>
public sealed record SuspectSet(
    IReadOnlyList<object> Suspects, string? Note, IReadOnlyList<UnparsedRungEntry>? UnparsedRungs = null);

/// <summary>
/// Static, advisory rung-citation suspects for <c>logix_diagnose</c>. Reads only: project file
/// rungs and live tag reads; never writes and never changes mode.
/// </summary>
public static class DiagnoseSuspects
{
    private const int MaxTimerElements = 256;
    private static readonly Regex SimpleTagPath =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*|\[\d+\])*$", RegexOptions.Compiled);

    /// <summary>
    /// Builds suspects for the decoded fault. 4/20 lists array subscripts, 4/34 lists negative-.PRE timers,
    /// every other type returns an empty list with a note.
    /// </summary>
    /// <param name="fault">The decoded major fault.</param>
    /// <param name="projectService">Loaded project (rung text, tags, dimensions); may be null or unloaded.</param>
    /// <param name="tags">Tag service for live reads of subscripts and .PRE values; may be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SuspectSet"/>. Never throws for read failures.</returns>
    public static async Task<SuspectSet> BuildAsync(
        MajorFault fault, ProjectService? projectService, ITagService? tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fault);

        var is420 = fault.Type == 4 && fault.Code == 20;
        var is434 = fault.Type == 4 && fault.Code == 34;
        if (!is420 && !is434)
            return new SuspectSet([],
                $"No rung suspects are generated for major fault type {fault.Type} code {fault.Code}; " +
                "only 4/20 (array subscript) and 4/34 (negative timer .PRE) have static rung suspects.");

        if (projectService is not { IsLoaded: true })
            return new SuspectSet([],
                "No project is loaded, so rung suspects cannot be listed. Call logix_open_project " +
                "(or logix_connect to an online controller) and run logix_diagnose again.");

        return is420
            ? await ArrayIndexSuspectsAsync(projectService.Project!, tags, ct).ConfigureAwait(false)
            : await NegativeTimerSuspectsAsync(projectService, tags, ct).ConfigureAwait(false);
    }

    // ── 4/20 ──────────────────────────────────────────────────────────────────

    private static async Task<SuspectSet> ArrayIndexSuspectsAsync(L5X project, ITagService? tags, CancellationToken ct)
    {
        var found = new List<ArrayIndexSuspect>();
        var unparsed = new List<UnparsedRungEntry>();
        var values = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        var anyLiveValue = false;

        foreach (var program in project.Programs)
        {
            foreach (var routine in program.Routines)
            {
                if (!WriteClassifier.IsLadder(routine)) continue;

                foreach (var rung in routine.Rungs)
                {
                    var rungText = rung.Text?.ToString() ?? string.Empty;
                    foreach (var instruction in RungParser.ParseContained(rung, program.Name, routine.Name, unparsed))
                    {
                        var args = instruction.Arguments.Select(a => a.ToString()).ToList();
                        foreach (var (array, index, position) in ArrayIndexUncheckedRule.SubscriptsWithPosition(args))
                        {
                            ct.ThrowIfCancellationRequested();
                            var dimension = DeclaredDimension(project, program, array, position);

                            var readName = OnlineName(program, index);
                            if (!values.TryGetValue(readName, out var value))
                            {
                                value = await TryReadIntegerAsync(tags, readName, ct).ConfigureAwait(false);
                                values[readName] = value;
                            }
                            if (value.HasValue) anyLiveValue = true;

                            var outOfRange = value.HasValue && dimension.HasValue
                                && (value.Value >= dimension.Value || value.Value < 0);
                            found.Add(new ArrayIndexSuspect(
                                new RungCitation(program.Name, routine.Name, rung.Number, instruction.Key,
                                    UntrustedWrapper.WrapRungText(rungText)),
                                array, index, value, dimension, outOfRange,
                                Summarize(array, index, value, dimension)));
                        }
                    }
                }
            }
        }

        // Rank: out-of-range first, then unknown, then in range. OrderBy is stable, so rung order is kept.
        var ranked = found
            .OrderBy(s => s.OutOfRange ? 0 : s.IndexValue.HasValue && s.Dimension.HasValue ? 2 : 1)
            .Cast<object>()
            .ToList();

        string? note = null;
        if (ranked.Count == 0)
            note = "No ladder rung has an array operand with a tag subscript. Suspects are static and advisory " +
                   "(structured text, FBD and SFC routines are not scanned).";
        else if (!anyLiveValue)
            note = "Current subscript values could not be read (no active tag session, or the reads failed), " +
                   "so suspects are not ranked by index. Suspects are static and advisory.";
        return new SuspectSet(ranked, note, unparsed.Count == 0 ? null : unparsed);
    }

    private static string Summarize(string array, string index, long? value, int? dimension)
    {
        var current = value.HasValue ? $"index currently {value.Value}" : "index current value unknown";
        var size = dimension.HasValue ? $"array size {dimension.Value}" : "array size unknown";
        return $"{array}[{index}]: {current}, {size}";
    }

    /// <summary>Declared size of dimension <paramref name="position"/> of a plain (member-free) array tag.</summary>
    private static int? DeclaredDimension(L5X project, L5Program program, string array, int position)
    {
        if (!SimpleName(array)) return null;
        var tag = FindTag(project, program, array);
        if (tag is null) return null;
        var dims = tag.Dimensions;
        if (dims.IsEmpty) return null;
        int? d = position switch { 0 => (int)dims.X, 1 => (int)dims.Y, 2 => (int)dims.Z, _ => null };
        return d is > 0 ? d : null;
    }

    private static bool SimpleName(string s) => s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static Tag? FindTag(L5X project, L5Program program, string name) =>
        program.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), name, StringComparison.OrdinalIgnoreCase))
        ?? project.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Prefixes a program-scoped tag path with <c>Program:Name.</c> for an online read.</summary>
    private static string OnlineName(L5Program program, string expression)
    {
        var expr = expression.Trim();
        var cut = expr.IndexOfAny(['.', '[']);
        var baseName = cut > 0 ? expr[..cut] : expr;
        return program.Tags.Any(t => string.Equals(t.Name.ToString(), baseName, StringComparison.OrdinalIgnoreCase))
            ? $"Program:{program.Name}.{expr}"
            : expr;
    }

    // ── 4/34 ──────────────────────────────────────────────────────────────────

    private static async Task<SuspectSet> NegativeTimerSuspectsAsync(
        ProjectService projectService, ITagService? tags, CancellationToken ct)
    {
        var project = projectService.Project!;
        if (tags is null)
            return new SuspectSet([],
                "A timer's .PRE can only be read from an active tag session, and there is none. Call logix_connect " +
                "and run logix_diagnose again. Suspects are static and advisory.");

        var found = new List<object>();
        var unparsed = new List<UnparsedRungEntry>();
        var routineText = new Dictionary<(string Program, string Routine), IReadOnlyList<RungEntry>?>();

        async SysTask ScanAsync(IEnumerable<Tag> scope, L5Program? owner)
        {
            foreach (var tag in scope)
            {
                if (!string.Equals(tag.DataType, "TIMER", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var element in ElementNames(tag))
                {
                    ct.ThrowIfCancellationRequested();
                    var online = owner is null ? element : $"Program:{owner.Name}.{element}";
                    var pre = await TryReadIntegerAsync(tags, online + ".PRE", ct).ConfigureAwait(false);
                    if (pre is not long preValue || !IsNegativePre(pre)) continue;

                    var baseName = tag.Name.ToString();
                    var refs = CitationsFor(projectService, baseName, owner?.Name, routineText, unparsed);
                    found.Add(new NegativePreSuspect(
                        online, preValue, refs, $"{online}.PRE currently {preValue} (negative)"));
                }
            }
        }

        await ScanAsync(project.Tags, null).ConfigureAwait(false);
        foreach (var program in project.Programs)
            await ScanAsync(program.Tags, program).ConfigureAwait(false);

        return new SuspectSet(found,
            found.Count == 0
                ? "No TIMER tag in the project reads a negative .PRE (timers inside structures or Add-On " +
                  "Instructions are not scanned). Suspects are static and advisory."
                : null,
            unparsed.Count == 0 ? null : unparsed);
    }

    private static IEnumerable<string> ElementNames(Tag tag)
    {
        var name = tag.Name.ToString();
        var d = tag.Dimensions;
        if (d.IsEmpty) { yield return name; yield break; }
        if (d.Length > MaxTimerElements) yield break;

        for (var x = 0; x < Math.Max(d.X, (ushort)1); x++)
        {
            if (d.Rank == 1) { yield return $"{name}[{x}]"; continue; }
            for (var y = 0; y < Math.Max(d.Y, (ushort)1); y++)
            {
                if (d.Rank == 2) { yield return $"{name}[{x},{y}]"; continue; }
                for (var z = 0; z < d.Z; z++) yield return $"{name}[{x},{y},{z}]";
            }
        }
    }

    /// <summary>References to a timer via <see cref="CrossReferenceHandler"/>, with rung text from <see cref="GetRoutineHandler"/>.</summary>
    private static IReadOnlyList<RungCitation> CitationsFor(
        ProjectService projectService, string tagName, string? ownerProgram,
        Dictionary<(string Program, string Routine), IReadOnlyList<RungEntry>?> routineText,
        List<UnparsedRungEntry> unparsed)
    {
        if (CrossReferenceHandler.Execute(projectService, tagName) is not CrossRefResult xref) return [];
        // The same bad rung shows up in every cross-reference; report it once.
        foreach (var u in xref.UnparsedRungs ?? [])
            if (!unparsed.Contains(u)) unparsed.Add(u);

        var citations = new List<RungCitation>();
        foreach (var r in xref.References)
        {
            // A program-scoped timer is only visible inside its own program.
            if (ownerProgram is not null && !string.Equals(r.Program, ownerProgram, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = (r.Program, r.Routine);
            if (!routineText.TryGetValue(key, out var rungs))
            {
                rungs = GetRoutineHandler.Execute(projectService, r.Program, r.Routine) is GetRoutineResult g ? g.Rungs : null;
                routineText[key] = rungs;
            }
            var text = rungs?.FirstOrDefault(x => x.Number == r.RungNumber)?.Text ?? string.Empty;
            citations.Add(new RungCitation(
                r.Program, r.Routine, r.RungNumber, r.Instruction, UntrustedWrapper.WrapRungText(text)));
        }
        return citations;
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>Reads an integer tag value; null when unreadable, not a simple tag path, or not integral.</summary>
    private static async Task<long?> TryReadIntegerAsync(ITagService? tags, string expression, CancellationToken ct)
    {
        if (tags is null) return null;
        var path = expression.Trim();
        if (!SimpleTagPath.IsMatch(path.Replace("Program:", string.Empty, StringComparison.Ordinal))) return null;
        try
        {
            var tv = await tags.ReadAsync(path, ct).ConfigureAwait(false);
            return ToInteger(tv.Value);
        }
        catch (OperationCanceledException) { throw; }
        // LGX_TAG_ROUTE=cip with no CIP route: not an unreadable tag, the whole route is unavailable.
        catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.TagRouteUnavailable) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The negative-.PRE test behind the <c>negativeTimerPre</c> suspect: true only for a known, negative value.
    /// Shared with the <c>NegativeTimerPreset</c> lint rule so both report the same condition.
    /// </summary>
    /// <param name="pre">The .PRE value, or null when it could not be read.</param>
    /// <returns>True when <paramref name="pre"/> has a value below zero.</returns>
    internal static bool IsNegativePre(long? pre) => pre is < 0;

    /// <summary>Converts a tag value to <see cref="long"/>; null for booleans, non-integral or out-of-range values.</summary>
    /// <param name="value">Value as read from a tag service or the project data.</param>
    /// <returns>The integer value, or null.</returns>
    internal static long? ToInteger(object? value) => value switch
    {
        bool => null,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value),
        ulong u when u <= long.MaxValue => (long)u,
        _ => null,
    };
}
