using System.Text.Json.Serialization;

/// <summary>A routine's name and language type.</summary>
/// <param name="Name">Routine name.</param>
/// <param name="Type">Routine type: RLL, ST, FBD or SFC.</param>
public record RoutineSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type)
{
    /// <summary><c>true</c> when the routine was created this session and only the session image holds it (the project file lacks it); omitted otherwise.</summary>
    [JsonPropertyName("createdThisSession"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CreatedThisSession { get; init; }
}

/// <summary>Per-program summary of tag and routine counts.</summary>
/// <param name="Name">Program name.</param>
/// <param name="Tags">Number of program-scoped tags.</param>
/// <param name="Routines">Number of routines in the program.</param>
/// <param name="RoutineList">Name and type of each routine in the program.</param>
public record ProgramSummary(
    [property: JsonPropertyName("name")]     string Name,
    [property: JsonPropertyName("tags")]     int    Tags,
    [property: JsonPropertyName("routines")] int    Routines,
    [property: JsonPropertyName("routineList")] IReadOnlyList<RoutineSummary> RoutineList);

/// <summary>
/// Strongly-typed success response returned by <see cref="ProjectSummaryHandler.Execute"/>.
/// </summary>
/// <param name="Controller">Name of the controller.</param>
/// <param name="SchemaRevision">Schema revision from the L5X file.</param>
/// <param name="ControllerTags">Number of controller-scoped tags.</param>
/// <param name="Programs">Per-program routine and tag counts.</param>
/// <param name="Source">
/// <c>"project-file"</c>: this summary was computed from the
/// opened or uploaded project file on disk, not from the live controller and not
/// from any pending (unsaved) edit. <c>"session-image"</c>: the session's accepted image holds a routine the project
/// file lacks (created this session), so the counts and lists come from that image and the new routine is marked
/// <c>createdThisSession</c>. For the live routine content including
/// pending edits, call <c>logix_export_rungs</c> instead.
/// </param>
public record ProjectSummaryResult(
    [property: JsonPropertyName("controller")]     string                  Controller,
    [property: JsonPropertyName("schemaRevision")] string                  SchemaRevision,
    [property: JsonPropertyName("controllerTags")] int                     ControllerTags,
    [property: JsonPropertyName("programs")]       IReadOnlyList<ProgramSummary> Programs,
    [property: JsonPropertyName("source")]         string                  Source = "project-file")
{
    /// <summary>
    /// Routines (<c>program/routine</c>) whose rungs this session changed since connect through an accepted or assembled
    /// edit; <c>logix_get_routine</c> serves them from the session image. <c>null</c> (omitted) when there are none.
    /// </summary>
    [JsonPropertyName("appliedEdits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? AppliedEdits { get; init; }

    /// <summary>
    /// <c>"session-image"</c> when <see cref="AppliedEdits"/> is non-empty, labelling where those routines' current content
    /// comes from (<c>source</c> still describes the summary's counts: <c>"project-file"</c>); <c>null</c> (omitted) otherwise.
    /// </summary>
    [JsonPropertyName("appliedEditsSource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedEditsSource { get; init; }

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
/// Strongly-typed error response returned when no project is loaded.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record ProjectSummaryError(
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
/// Pure handler for the <c>logix_project_summary</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class ProjectSummaryHandler
{
    /// <summary>
    /// Returns controller info plus per-program routine and tag counts.
    /// Never throws; returns <see cref="ProjectSummaryError"/> if no project is loaded.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="session">
    /// Optional tag session: its applied edits are listed in <c>appliedEdits</c> and a stale session image is flagged.
    /// </param>
    /// <returns>
    /// A <see cref="ProjectSummaryResult"/> on success,
    /// or a <see cref="ProjectSummaryError"/> when no project is open.
    /// </returns>
    public static object Execute(ProjectService service, TagSessionService? session = null)
    {
        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_project_summary");
            return new ProjectSummaryError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        // A routine created this session is only in the session image, so a summary that lists one is built from the
        // accepted image (which holds everything the file does); otherwise the project file is read as before.
        var file  = service.Project!;
        var image = session?.AcceptedControllerImage;
        var created = image is null
            ? []
            : image.Programs.SelectMany(p => p.Routines.Select(r => (Program: p.Name, Routine: r.Name)))
                .Where(x => ProjectFileStaleness.IsCreatedThisSession(session, service, x.Program, x.Routine))
                .ToHashSet();
        var fromImage = image is not null && created.Count > 0;
        var project   = fromImage ? image! : file;
        var source    = fromImage ? ProjectSourceLabel.SessionImage : ProjectSourceLabel.ProjectFile;

        var programs = project.Programs
            .Select(p => new ProgramSummary(
                Name:     p.Name,
                Tags:     p.Tags.Count,
                Routines: p.Routines.Count,
                RoutineList: p.Routines
                    .Select(r => new RoutineSummary(r.Name, r.Type.Name)
                    {
                        CreatedThisSession = fromImage && created.Contains((p.Name, r.Name)) ? true : null,
                    })
                    .ToList()))
            .ToList();

        var stale   = ProjectFileStaleness.For(session, source, service);
        var applied = ProjectFileStaleness.AppliedEdits(session, service);
        return new ProjectSummaryResult(
            Controller:     project.Controller.Name,
            SchemaRevision: project.Content.SchemaRevision.ToString(),
            ControllerTags: project.Tags.Count,
            Programs:       programs,
            Source:         source)
        {
            AppliedEdits = applied.Count == 0 ? null : applied,
            AppliedEditsSource = applied.Count == 0 ? null : "session-image",
            ImageStale  = stale is null ? null : true,
            StaleSince  = stale?.StaleSince,
            StaleReason = stale?.StaleReason,
            Hint        = stale?.Hint,
        };
    }
}
