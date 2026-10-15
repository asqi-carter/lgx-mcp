using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>A single rung entry returned by <see cref="GetRoutineHandler.Execute"/>.</summary>
/// <param name="Number">Zero-based rung number (position in the routine).</param>
/// <param name="Text">Neutral-text content of the rung (e.g. <c>XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run);</c>).</param>
/// <param name="Comment">Rung comment, or empty string if none.</param>
public record RungEntry(
    [property: JsonPropertyName("number")]  int    Number,
    [property: JsonPropertyName("text")]    string Text,
    [property: JsonPropertyName("comment")] string Comment);

/// <summary>
/// Strongly-typed success response returned by <see cref="GetRoutineHandler.Execute"/>.
/// </summary>
/// <param name="Program">Name of the program containing the routine.</param>
/// <param name="Routine">Name of the routine.</param>
/// <param name="Rungs">Ordered list of rung entries.</param>
/// <param name="Source">
/// <c>"project-file"</c>: the rungs above were read from the
/// opened or uploaded project file on disk, not from the live controller and not
/// from any pending (unsaved) edit. <c>"session-image"</c>: this session applied an accepted or
/// assembled edit to the routine, so the rungs come from the session image, which follows that edit; a routine created this session (the image holds it, the project file does not) is also read from the image and carries <c>createdThisSession: true</c>. For the live routine content including
/// pending edits, call <c>logix_export_rungs</c> instead.
/// </param>
public record GetRoutineResult(
    [property: JsonPropertyName("program")] string                   Program,
    [property: JsonPropertyName("routine")] string                   Routine,
    [property: JsonPropertyName("rungs")]   IReadOnlyList<RungEntry> Rungs,
    [property: JsonPropertyName("source")]  string                   Source = "project-file")
{
    /// <summary><c>true</c> when the session image is marked stale and this response may not show the controller's current logic; omitted otherwise.</summary>
    [JsonPropertyName("imageStale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ImageStale { get; init; }

    /// <summary><c>true</c> when the routine was created this session and only the session image holds it (the project file lacks it); omitted otherwise.</summary>
    [JsonPropertyName("createdThisSession"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CreatedThisSession { get; init; }

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
/// Strongly-typed error response returned when the project is not loaded or
/// the requested program or routine cannot be found.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record GetRoutineError(
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
/// Pure handler for the <c>logix_get_routine</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class GetRoutineHandler
{
    /// <summary>
    /// Returns an ordered list of rungs (number, neutral-text content, and comment) for the
    /// specified program and routine in the currently open project.
    /// Never throws; returns <see cref="GetRoutineError"/> on any error condition.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="program">
    /// Name of the program that contains the routine (case-insensitive).
    /// </param>
    /// <param name="routine">
    /// Name of the routine whose rungs should be returned (case-insensitive).
    /// </param>
    /// <param name="session">
    /// Optional tag session. A routine whose rungs it applied to its session image (accept or assemble) is served from
    /// that image, and a stale image is flagged on the response.
    /// </param>
    /// <returns>
    /// A <see cref="GetRoutineResult"/> with ordered rung entries on success,
    /// or a <see cref="GetRoutineError"/> when no project is open or the
    /// specified program or routine is not found.
    /// </returns>
    public static object Execute(ProjectService service, string program, string routine, TagSessionService? session = null)
    {
        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_get_routine");
            return new GetRoutineError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        // A routine this session changed through an accepted or assembled edit is read from the session image,
        // which follows the edit; the project file still holds the old rungs. The tool wrapper
        // (LogixProjectTools.GetRoutine) refreshed a stale image first (AnalysisImage.PrepareAsync).
        var image   = ProjectFileStaleness.ImageFor(session, service, program, routine);
        var project = image ?? service.Project!;
        var source  = image is null ? ProjectSourceLabel.ProjectFile : ProjectSourceLabel.SessionImage;

        // Locate the program (case-insensitive)
        var prog = project.Programs
            .FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase));

        if (prog is null)
        {
            var available = string.Join(", ", project.Programs.Select(p => p.Name));
            return new GetRoutineError(
                $"Program '{program}' not found in the open project. " +
                $"Available programs: {(available.Length > 0 ? available : "(none)")}.")
            { Kind = ErrorKinds.ProgramNotFound };
        }

        // Locate the routine within the program (case-insensitive)
        var rtn = prog.Routines
            .FirstOrDefault(r => string.Equals(r.Name, routine, StringComparison.OrdinalIgnoreCase));

        if (rtn is null)
        {
            var available = string.Join(", ", prog.Routines.Select(r => r.Name));
            return new GetRoutineError(
                $"Routine '{routine}' not found in program '{prog.Name}'. " +
                $"Available routines: {(available.Length > 0 ? available : "(none)")}.")
            { Kind = ErrorKinds.RoutineNotFound };
        }

        // Only ladder routines have rungs. Enumerating Rungs on an FBD/SFC/ST
        // routine throws rather than yielding nothing — see WriteClassifier.IsLadder.
        if (!WriteClassifier.IsLadder(rtn))
        {
            return new GetRoutineError(
                $"Routine '{rtn.Name}' in program '{prog.Name}' is a {rtn.Type.Name} routine, " +
                $"not ladder (RLL). logix_get_routine returns rung neutral text, which only " +
                $"ladder routines have.");
        }

        // Collect rungs in order.
        // Comment is customer-authored text from the project file; wrap it so the
        // model can distinguish project content from server-authored text.
        var rungs = rtn.Rungs
            .Select(rung => new RungEntry(
                Number:  rung.Number,
                Text:    rung.Text?.ToString() ?? string.Empty,
                Comment: string.IsNullOrWhiteSpace(rung.Comment)
                    ? string.Empty   // no comment: nothing to mark as untrusted, so no empty wrapper
                    : UntrustedWrapper.WrapRungComment(rung.Comment)))
            .ToList();

        var created = image is not null && ProjectFileStaleness.IsCreatedThisSession(session, service, prog.Name, rtn.Name);
        var stale   = ProjectFileStaleness.For(session, source, service);
        return new GetRoutineResult(
            Program: prog.Name,
            Routine: rtn.Name,
            Rungs:   rungs,
            Source:  source)
        {
            CreatedThisSession = created ? true : null,
            ImageStale  = stale is null ? null : true,
            StaleSince  = stale?.StaleSince,
            StaleReason = stale?.StaleReason,
            Hint        = stale?.Hint,
        };
    }
}
