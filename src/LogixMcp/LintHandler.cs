using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>Severity of a <see cref="LintFinding"/>. Declaration order is the sort order.</summary>
public enum LintSeverity
{
    /// <summary>A defect that changes how the logic runs (e.g. a defeated interlock).</summary>
    Error = 0,

    /// <summary>A fault risk that is only a problem under some runtime conditions.</summary>
    Warning = 1,

    /// <summary>Advisory information; never a defect on its own.</summary>
    Info = 2,
}

/// <summary>
/// One lint finding, citing the program / routine / rung it was found on.
/// </summary>
/// <param name="Rule">Rule identifier, e.g. <c>DuplicateDestructiveOutput</c>.</param>
/// <param name="Severity"><c>"error"</c>, <c>"warning"</c> or <c>"info"</c>.</param>
/// <param name="Program">Program containing the cited rung.</param>
/// <param name="Routine">Routine containing the cited rung.</param>
/// <param name="Rung">Zero-based rung number within the routine.</param>
/// <param name="Text">
/// Rung neutral text, wrapped with <see cref="UntrustedWrapper.WrapRungText"/> by
/// <see cref="LintHandler"/> (rules supply the raw text).
/// </param>
/// <param name="Message">Server-authored explanation of the problem.</param>
/// <param name="Fix">Server-authored suggestion for resolving it.</param>
public record LintFinding(
    [property: JsonPropertyName("rule")]     string Rule,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("program")]  string Program,
    [property: JsonPropertyName("routine")]  string Routine,
    [property: JsonPropertyName("rung")]     int    Rung,
    [property: JsonPropertyName("text")]     string Text,
    [property: JsonPropertyName("message")]  string Message,
    [property: JsonPropertyName("fix")]      string Fix);

/// <summary>Per-severity tallies over the findings in a <see cref="LintResult"/>.</summary>
public record LintCounts(
    [property: JsonPropertyName("error")]   int Error,
    [property: JsonPropertyName("warning")] int Warning,
    [property: JsonPropertyName("info")]    int Info,
    [property: JsonPropertyName("total")]   int Total);

/// <summary>A routine the linter did not analyse because it is not ladder (RLL).</summary>
public record LintSkippedRoutine(
    [property: JsonPropertyName("program")] string Program,
    [property: JsonPropertyName("routine")] string Routine,
    [property: JsonPropertyName("type")]    string Type,
    [property: JsonPropertyName("reason")]  string Reason = "skipped: only ladder (RLL) routines are linted");

/// <summary>Success response of <see cref="LintHandler.Execute"/>.</summary>
/// <param name="Source"><c>"project-file"</c> for a project opened via <c>logix_open_project</c>; <c>"session-image"</c> when an active session supplies the controller image (see <see cref="ProjectSourceLabel"/>).</param>
/// <param name="Findings">Findings sorted error → warning → info, then program, routine, rung.</param>
/// <param name="Counts">Per-severity tallies.</param>
/// <param name="Skipped">Non-ladder routines in scope that were not analysed.</param>
/// <param name="UntrustedContent">Names of fields that carry wrapped customer-authored text.</param>
public record LintResult(
    [property: JsonPropertyName("source")]           string                            Source,
    [property: JsonPropertyName("findings")]         IReadOnlyList<LintFinding>        Findings,
    [property: JsonPropertyName("counts")]           LintCounts                        Counts,
    [property: JsonPropertyName("skipped")]          IReadOnlyList<LintSkippedRoutine> Skipped,
    [property: JsonPropertyName("untrustedContent")] IReadOnlyList<string>             UntrustedContent)
{
    /// <summary>
    /// Rungs in scope that could not be parsed (program, routine, rung, error); omitted when every
    /// in-scope rung parsed. Their rules were skipped for those rungs only; every other finding is unchanged.
    /// </summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// Set when the session's controller image is stale and could not be refreshed, so the findings may miss
    /// changes made in this session; omitted otherwise.
    /// </summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

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
}

/// <summary>Error response of <see cref="LintHandler.Execute"/>.</summary>
public record LintError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>
    /// Machine-readable error kind (<see cref="ErrorKinds.ProgramNotFound"/> when the <c>program</c> filter names
    /// a program the project does not have); <c>null</c> for other errors.
    /// </summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }
}

/// <summary>One rung of a ladder routine, as handed to lint rules.</summary>
/// <param name="Number">Zero-based rung number.</param>
/// <param name="Text">Raw rung neutral text (unwrapped; the handler wraps findings' text).</param>
/// <param name="Comment">Raw rung comment, or empty. Wrap with <see cref="UntrustedWrapper.WrapRungComment"/> if quoted.</param>
/// <param name="Source">The underlying L5Sharp rung, for instruction-level walks.</param>
public record LintRung(int Number, string Text, string Comment, Rung Source)
{
    private readonly Lazy<(IReadOnlyList<Instruction> Instructions, string? Error)> _parsed = new(() =>
    {
        var parsed = RungParser.TryParse(Source, out var error);
        return (parsed ?? Array.Empty<Instruction>(), parsed is null ? error ?? "rung text could not be parsed" : null);
    });

    /// <summary>
    /// The rung's instructions, parsed once through <see cref="RungParser"/>; empty when the rung
    /// cannot be parsed (see <see cref="ParseError"/>). Rules use this instead of parsing the rung themselves,
    /// so one unparseable rung never aborts a rule.
    /// </summary>
    public IReadOnlyList<Instruction> Instructions => _parsed.Value.Instructions;

    /// <summary>Why the rung could not be parsed, or <c>null</c> when it parsed.</summary>
    public string? ParseError => _parsed.Value.Error;
}

/// <summary>A ladder routine, as handed to lint rules.</summary>
public record LintRoutine(
    string Program,
    string Routine,
    string? MainRoutineName,
    string? FaultRoutineName,
    IReadOnlyList<LintRung> Rungs);

/// <summary>
/// Everything a rule needs. Always spans the whole controller — duplicate-output style
/// rules need cross-routine visibility — and <see cref="LintHandler"/> applies the
/// caller's program/routine scope to the findings afterwards.
/// </summary>
public sealed class LintContext
{
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<int>>> _aoiWrites;

    /// <summary>Creates a context over <paramref name="project"/> and its ladder routines.</summary>
    /// <param name="project">The parsed project.</param>
    /// <param name="ladderRoutines">Every ladder routine in the controller.</param>
    /// <param name="routineScoped">True when the caller restricted the lint to a routine.</param>
    public LintContext(L5X project, IReadOnlyList<LintRoutine> ladderRoutines, bool routineScoped = false)
    {
        AoiUnparsedRungs = new List<UnparsedRungEntry>();
        RoutineScoped  = routineScoped;
        Project        = project;
        LadderRoutines = ladderRoutines;
        _aoiWrites     = new(() => WriteClassifier.BuildAoiWriteMap(project));
    }

    /// <summary>The parsed project.</summary>
    public L5X Project { get; }

    /// <summary>
    /// Add-On Instruction rungs a rule could not parse (program empty, routine <c>AOI/routine</c>).
    /// Filled by rules while they run; the handler reports them when no program or routine filter is given.
    /// </summary>
    public ICollection<UnparsedRungEntry> AoiUnparsedRungs { get; }

    /// <summary>True when the caller gave the <c>routine</c> parameter; noisy rules then emit per-rung detail.</summary>
    public bool RoutineScoped { get; }

    /// <summary>Every ladder (RLL) routine in the controller, in project order.</summary>
    public IReadOnlyList<LintRoutine> LadderRoutines { get; }

    /// <summary>AOI parameter write map, for <c>WriteClassifier.ClassifyKind</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<int>> AoiWrites => _aoiWrites.Value;
}

/// <summary>
/// A single lint rule. Implement this and add the instance to <see cref="LintRules.Default"/>
/// to register a new rule; the handler takes care of scoping, wrapping and sorting.
/// </summary>
public interface ILintRule
{
    /// <summary>Stable rule identifier reported in <see cref="LintFinding.Rule"/>.</summary>
    string Id { get; }

    /// <summary>One-line rationale: why this pattern is worth reporting.</summary>
    string Rationale { get; }

    /// <summary>
    /// Produces findings for the whole controller. <see cref="LintFinding.Text"/> must be
    /// the RAW rung text; the handler wraps it as untrusted.
    /// </summary>
    IEnumerable<LintFinding> Check(LintContext context);
}

/// <summary>The registered rule set.</summary>
public static class LintRules
{
    /// <summary>
    /// Rules run by <c>logix_lint</c>. Rules are added here by their own tasks.
    /// Deliberately contains no unused-tag rule: unused tags are normal PLC practice.
    /// </summary>
    public static readonly IReadOnlyList<ILintRule> Default = new List<ILintRule>
    {
        new DuplicateDestructiveOutputRule(),
        new DuplicateTimerCounterRule(),
        new JsrTargetMissingRule(),
        new JsrParameterMismatchRule(),
        new RoutineNeverCalledRule(),
        new RecursiveJsrRule(),
        new BackwardJumpRule(),
        new DivideByPossibleZeroRule(),
        new ArrayIndexUncheckedRule(),
        new MissingRungCommentRule(),
        new ShadowedControllerTagRule(),
        new NegativeTimerPresetRule(),
        new CaseOnlyNameCollisionRule(),
        new AoiNeverUsedRule(),
        new CrossProgramTargetMissingRule(),
        new CrossProgramTargetNotPublicRule(),
        new CrossProgramOutputNotWritableRule(),
    };
}

/// <summary>
/// Pure handler for the read-only <c>logix_lint</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class LintHandler
{
    private static readonly JsonSerializerOptions _options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    /// <summary>
    /// Lints the ladder routines of the loaded project. Never throws for bad input;
    /// returns a <see cref="LintError"/> instead.
    /// </summary>
    /// <param name="service">
    /// Project holder. Holds the open project, or the project image a connect/upload
    /// session registered — the same source <c>logix_gate_report</c> uses.
    /// </param>
    /// <param name="program">Optional program name (case-insensitive) restricting reported findings.</param>
    /// <param name="routine">Optional routine name (case-insensitive) restricting reported findings.</param>
    /// <param name="rules">Rule set to run; defaults to <see cref="LintRules.Default"/>.</param>
    /// <param name="session">
    /// Optional tag session. When it targets a live controller the result is labelled
    /// <c>source: "session-image"</c> (see <see cref="ProjectSourceLabel"/>).
    /// </param>
    /// <param name="namingPattern">
    /// Optional .NET regex every controller- and program-scoped tag name must match. Non-matching
    /// tags produce <c>NamingConvention</c> info findings. Omit for no naming findings. An invalid
    /// regex returns a <see cref="LintError"/>.
    /// </param>
    /// <returns>A <see cref="LintResult"/> or a <see cref="LintError"/>.</returns>
    public static object Execute(
        ProjectService service,
        string? program = null,
        string? routine = null,
        IReadOnlyList<ILintRule>? rules = null,
        TagSessionService? session = null,
        string? namingPattern = null)
    {
        // An SDK-online session lints its controller image (the result is labelled 'session-image'), so a change
        // made in this session shows once the image is refreshed; otherwise the opened project file is linted.
        var sessionImage = session is { IsLiveController: true } ? session.ControllerImage : null;
        if (!service.IsLoaded && sessionImage is null)
            return new LintError("No project loaded. Call logix_open_project first.");

        var project = sessionImage ?? service.Project!;
        bool hasProgram = !string.IsNullOrWhiteSpace(program);
        bool hasRoutine = !string.IsNullOrWhiteSpace(routine);

        if (hasProgram && !project.Programs.Any(p => Same(p.Name, program!)))
        {
            var available = string.Join(", ", project.Programs.Select(p => p.Name));
            return new LintError(
                $"Program '{program}' not found in the open project. " +
                $"Available programs: {(available.Length > 0 ? available : "(none)")}.")
            { Kind = ErrorKinds.ProgramNotFound };
        }

        Regex? naming = null;
        if (!string.IsNullOrEmpty(namingPattern))
        {
            try { naming = new Regex(namingPattern, RegexOptions.None, NamingRegexTimeout); }
            catch (ArgumentException ex)
            {
                return new LintError($"Invalid namingPattern regex: {ex.Message}");
            }
        }

        bool InScope(string prog, string rout) =>
            (!hasProgram || Same(prog, program!)) && (!hasRoutine || Same(rout, routine!));

        // Collect every ladder routine (rules need controller-wide visibility) and
        // record in-scope non-ladder routines as skipped. Enumerating Rungs on a
        // non-RLL routine throws, so IsLadder must gate it.
        var ladder  = new List<LintRoutine>();
        var skipped = new List<LintSkippedRoutine>();
        bool routineSeen = false;

        foreach (var prog in project.Programs)
        {
            foreach (var rout in prog.Routines)
            {
                if (InScope(prog.Name, rout.Name)) routineSeen = true;

                if (!WriteClassifier.IsLadder(rout))
                {
                    if (InScope(prog.Name, rout.Name))
                        skipped.Add(new LintSkippedRoutine(prog.Name, rout.Name, rout.Type.ToString()));
                    continue;
                }

                var rungs = rout.Rungs
                    .Select(r => new LintRung(r.Number, r.Text?.ToString() ?? string.Empty, r.Comment ?? string.Empty, r))
                    .ToList();
                ladder.Add(new LintRoutine(
                    prog.Name, rout.Name, prog.MainRoutineName, prog.FaultRoutineName, rungs));
            }
        }

        if (hasRoutine && !routineSeen)
            return new LintError(
                $"Routine '{routine}' not found" +
                (hasProgram ? $" in program '{program}'." : " in the open project."));

        var context  = new LintContext(project, ladder, hasRoutine);
        var findings = new List<LintFinding>();
        foreach (var rule in rules ?? LintRules.Default)
        {
            foreach (var f in rule.Check(context))
            {
                if (!InScope(f.Program, f.Routine)) continue;
                // Rung text is customer-authored: wrap at the single choke point.
                findings.Add(f with { Text = UntrustedWrapper.WrapRungText(f.Text) });
            }
        }

        if (naming is not null)
        {
            try
            {
                foreach (var f in NamingFindings(project, naming, program, routine))
                    findings.Add(f with { Text = UntrustedWrapper.WrapRungText(f.Text) });
            }
            catch (RegexMatchTimeoutException)
            {
                return new LintError("namingPattern regex timed out (1 second limit); simplify the pattern.");
            }
        }

        var sorted = Sort(findings);
        var counts = new LintCounts(
            sorted.Count(f => f.Severity == SeverityName(LintSeverity.Error)),
            sorted.Count(f => f.Severity == SeverityName(LintSeverity.Warning)),
            sorted.Count(f => f.Severity == SeverityName(LintSeverity.Info)),
            sorted.Count);

        // Rungs outside the program / routine filter are never reported: a filter must not fail, or
        // grow a warning, because of a rung it does not cover.
        var unparsed = ladder
            .Where(r => InScope(r.Program, r.Routine))
            .SelectMany(r => r.Rungs
                .Where(x => x.ParseError is not null)
                .Select(x => new UnparsedRungEntry(r.Program, r.Routine, x.Number, x.ParseError!)))
            .ToList();
        if (!hasProgram && !hasRoutine)
            unparsed.AddRange(context.AoiUnparsedRungs);

        var source = ProjectSourceLabel.Resolve(session);
        var stale  = sessionImage is null ? null : ProjectSourceLabel.Staleness(session, source);
        return new LintResult(source, sorted, counts, skipped, new[] { "text" })
        {
            UnparsedRungs = unparsed.Count == 0 ? null : unparsed,
            Note          = sessionImage is null ? null : session!.ImageStaleNote,
            ImageStale    = stale is null ? null : true,
            StaleSince    = stale?.SinceText,
            StaleReason   = stale?.Reason,
            Hint          = stale is null ? null : ProjectSourceLabel.StaleHint,
        };
    }

    private static readonly TimeSpan NamingRegexTimeout = TimeSpan.FromSeconds(1);

    private const int NamingNameLimit = 20;

    /// <summary>
    /// Builds <c>NamingConvention</c> info findings for tags whose name does not match
    /// <paramref name="pattern"/>. Controller scope is included unless a program is given;
    /// program scopes are limited to the given program / the programs containing the given routine.
    /// Findings are summarised per scope (first 20 names), or listed per tag when a routine is given.
    /// All cite rung -1.
    /// </summary>
    private static IEnumerable<LintFinding> NamingFindings(L5X project, Regex pattern, string? program, string? routine)
    {
        bool hasProgram = !string.IsNullOrWhiteSpace(program);
        bool hasRoutine = !string.IsNullOrWhiteSpace(routine);
        var info = SeverityName(LintSeverity.Info);
        const string fix = "Rename the tag to match the naming pattern.";

        var scopes = new List<(string Program, string Label, List<string> Bad)>();
        if (!hasProgram)
            scopes.Add((string.Empty, "controller scope",
                project.Tags.Select(t => t.Name.ToString()).Where(n => !pattern.IsMatch(n)).ToList()));

        foreach (var prog in project.Programs)
        {
            if (hasProgram && !Same(prog.Name, program!)) continue;
            if (hasRoutine && !prog.Routines.Any(r => Same(r.Name, routine!))) continue;
            scopes.Add((prog.Name, $"program '{prog.Name}'",
                prog.Tags.Select(t => t.Name.ToString()).Where(n => !pattern.IsMatch(n)).ToList()));
        }

        foreach (var (prog, label, bad) in scopes)
        {
            if (bad.Count == 0) continue;
            var routineName = hasRoutine ? routine! : string.Empty;
            if (hasRoutine)
            {
                foreach (var name in bad)
                    yield return new LintFinding("NamingConvention", info, prog, routineName, -1, string.Empty,
                        $"Tag '{name}' in {label} does not match the naming pattern '{pattern}'.", fix);
                continue;
            }

            var shown = string.Join(", ", bad.Take(NamingNameLimit));
            var more  = bad.Count > NamingNameLimit ? $", ... (+{bad.Count - NamingNameLimit} more)" : string.Empty;
            yield return new LintFinding("NamingConvention", info, prog, routineName, -1, string.Empty,
                $"{bad.Count} tags do not match the naming pattern '{pattern}' in {label}: {shown}{more}.", fix);
        }
    }

    /// <summary>Serializes the result of <see cref="Execute"/> to compact JSON.</summary>
    public static string Serialize(object result) => JsonSerializer.Serialize(result, _options);

    /// <summary>Lower-case wire name of a severity (<c>error</c>, <c>warning</c>, <c>info</c>).</summary>
    public static string SeverityName(LintSeverity severity) => severity switch
    {
        LintSeverity.Error   => "error",
        LintSeverity.Warning => "warning",
        _                    => "info",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "error"   => (int)LintSeverity.Error,
        "warning" => (int)LintSeverity.Warning,
        _         => (int)LintSeverity.Info,
    };

    /// <summary>Orders findings error → warning → info, then program, routine, rung (rule/message break ties).</summary>
    private static List<LintFinding> Sort(IEnumerable<LintFinding> findings) =>
        findings
            .OrderBy(f => SeverityRank(f.Severity))
            .ThenBy(f => f.Program, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Routine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Rung)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.Message, StringComparer.Ordinal)
            .ToList();

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
