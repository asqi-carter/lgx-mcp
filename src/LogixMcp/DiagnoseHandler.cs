using System.Text.Json.Serialization;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>Successful <c>logix_diagnose</c> response.</summary>
/// <param name="Faulted">True when the identity status word or the fault record shows a major fault.</param>
/// <param name="IdentityStatus">ListIdentity status word as <c>0xNNNN</c> (0x3060 healthy, 0x3450 faulted).</param>
/// <param name="Status">The identity status word decoded with Rockwell's published bit table.</param>
/// <param name="Forces">Always <c>reported: false</c>: force status is not readable over CIP.</param>
/// <param name="Mode">Controller mode from the active session (read only); omitted when unknown.</param>
/// <param name="MajorFault">The decoded major fault; omitted when not faulted.</param>
/// <param name="Suspects">Static, advisory rung suspects; empty when none are found or not faulted.</param>
/// <param name="Fix">Fixed fix text when faulted; omitted otherwise.</param>
/// <param name="SuspectsNote">Why <c>suspects</c> is empty or partial (e.g. a fault type with no static suspects); omitted otherwise.</param>
public sealed record DiagnoseResult(
    [property: JsonPropertyName("faulted")]        bool Faulted,
    [property: JsonPropertyName("identityStatus")] string IdentityStatus,
    [property: JsonPropertyName("status")]         StatusWord Status,
    [property: JsonPropertyName("forces")]         ForcesInfo Forces,
    [property: JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Mode,
    [property: JsonPropertyName("majorFault"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    MajorFault? MajorFault,
    [property: JsonPropertyName("suspects")]       IReadOnlyList<object> Suspects,
    [property: JsonPropertyName("fix"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Fix,
    [property: JsonPropertyName("suspectsNote"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SuspectsNote = null)
{
    /// <summary>
    /// Rungs the suspect scan could not parse (program, routine, rung, error); omitted when every rung parsed.
    /// Suspects on all other rungs are unaffected.
    /// </summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }
}

/// <summary>
/// The <c>forces</c> field of <c>logix_diagnose</c>. Force status is not readable over CIP, so
/// <see cref="Reported"/> is always false; this never claims forces are absent.
/// </summary>
/// <param name="Reported">Always false.</param>
/// <param name="Reason">Why force status is not reported.</param>
/// <param name="HowToCheck">How to check for forces outside this tool.</param>
/// <param name="Note">Present only when the status word reports debug mode.</param>
public sealed record ForcesInfo(
    [property: JsonPropertyName("reported")]   bool Reported,
    [property: JsonPropertyName("reason")]     string Reason,
    [property: JsonPropertyName("howToCheck")] string HowToCheck,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null);

/// <summary>Error response from <see cref="DiagnoseHandler"/>.</summary>
/// <param name="Error">Human-readable description.</param>
/// <param name="Kind">Machine-readable error kind.</param>
/// <param name="Hint">Optional next step.</param>
public sealed record DiagnoseError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint = null);

// ── DiagnoseHandler ───────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the read-only <c>logix_diagnose</c> tool. It reads the ListIdentity
/// status word and the controller fault record (CIP class 0x73), and reads the mode through
/// <see cref="ControllerModeHandler"/>'s read path. It never writes a tag and never requests a mode change.
/// </summary>
public static class DiagnoseHandler
{
    /// <summary>Fixed fix text: lgx-mcp cannot clear faults.</summary>
    public const string FixText =
        "Clear the fault in Logix Designer (Controller Properties → Major Faults → Clear Majors) after correcting " +
        "the cause; lgx-mcp cannot clear faults.";

    /// <summary>Why force status is not reported.</summary>
    public const string ForcesReason =
        "Logix controllers do not report force status over CIP to an outside client. Rockwell documents it " +
        "only as the Module object's ForceStatus attribute, readable with a GSV instruction in logic.";

    /// <summary>How to check for forces outside this tool.</summary>
    public const string ForcesHowToCheck =
        "Look at the Forces indicator in Logix Designer (online), or add logic: GSV Module ForceStatus into a " +
        "DINT tag (bit 0 = forces installed, bit 1 = forces enabled), then read that tag with logix_read.";

    /// <summary>Note added when the status word reports debug mode.</summary>
    public const string DebugModeNote =
        "The controller status word reports debug mode (bits 15..14 = 10). Rockwell's help does not say " +
        "whether this means forces are on; check in Logix Designer.";

    /// <summary>Builds the <c>forces</c> field: never reported; adds a note only for debug mode.</summary>
    /// <param name="status">The decoded status word.</param>
    /// <returns>The <see cref="ForcesInfo"/>.</returns>
    public static ForcesInfo BuildForces(StatusWord status) =>
        new(false, ForcesReason, ForcesHowToCheck, status.ModeFlag == "debugMode" ? DebugModeNote : null);

    /// <summary>Reads ListIdentity from <c>host</c>; overridable so tests need no network.</summary>
    public delegate Task<EmulatorIdentityInfo> IdentityReader(string host, CancellationToken ct);

    /// <summary>Reads the raw fault record (class 0x73 instance 1) from <c>host</c>; overridable for tests.</summary>
    public delegate Task<byte[]> FaultRecordReader(string host, CancellationToken ct);

    /// <summary>
    /// Diagnoses the controller. Never throws for I/O or parse failures.
    /// </summary>
    /// <param name="controllerIp">Controller address; the active session's address when null or empty.</param>
    /// <param name="session">Active tag session (supplies the default address and the mode).</param>
    /// <param name="modeService">Controller mode tracker (read only).</param>
    /// <param name="identityReader">ListIdentity reader; defaults to <see cref="EmulatorIdentity.ReadAsync"/>.</param>
    /// <param name="faultReader">Fault record reader; defaults to <see cref="ControllerFaultReader.ReadAsync"/>.</param>
    /// <param name="project">Loaded project used for rung-citation suspects (4/20, 4/34); none when null or unloaded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DiagnoseResult"/> or a <see cref="DiagnoseError"/>.</returns>
    public static async Task<object> ExecuteAsync(
        string?                controllerIp,
        TagSessionService      session,
        ControllerModeService  modeService,
        IdentityReader?        identityReader = null,
        FaultRecordReader?     faultReader    = null,
        ProjectService?        project        = null,
        CancellationToken      ct             = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(modeService);

        var explicitIp = !string.IsNullOrWhiteSpace(controllerIp);
        var host = explicitIp ? controllerIp!.Trim() : session.ControllerHost;

        if (string.IsNullOrWhiteSpace(host))
        {
            return session.IsConnected
                ? new DiagnoseError(
                    "The active session has no controller address (offline, virtual and file-backed sessions " +
                    "have none), and no controllerIp was given.",
                    ErrorKinds.IdentityUnavailable,
                    "Pass controllerIp, or connect online with logix_connect.")
                : new DiagnoseError(
                    "No active session and no controllerIp given. Call logix_connect first, or pass controllerIp.",
                    ErrorKinds.NoActiveSession);
        }

        EmulatorIdentityInfo identity;
        try
        {
            identity = await (identityReader ?? DefaultIdentityReader)(host, ct).ConfigureAwait(false);
        }
        catch (EmulatorIdentityException ex)
        {
            return new DiagnoseError(
                $"Cannot read the controller's ListIdentity: {ex.Message}",
                ErrorKinds.IdentityUnavailable,
                $"Check that {host}:44818 is reachable (logix_doctor), then retry.");
        }

        byte[] record;
        try
        {
            record = await (faultReader ?? DefaultFaultReader)(host, ct).ConfigureAwait(false);
        }
        catch (FaultRecordException ex)
        {
            return new DiagnoseError(
                $"Cannot read the controller fault record: {ex.Message}",
                ErrorKinds.FaultRecordUnavailable,
                $"Identity status was {Format(identity.Status)}. Check that {host}:44818 is reachable (logix_doctor), then retry.");
        }

        MajorFault? fault;
        try
        {
            fault = FaultRecordDecoder.Decode(record);
        }
        catch (FaultRecordException ex)
        {
            return new DiagnoseError(
                $"Cannot parse the controller fault record: {ex.Message}", ErrorKinds.FaultRecordUnavailable);
        }

        var faulted = fault is { IsUnparsed: false } || IdentityStatus.IsFaulted(identity.Status);
        var mode = await ReadModeAsync(host, explicitIp, session, modeService, identity.Status, ct).ConfigureAwait(false);

        // Suspects are static and advisory; reads only (project rungs, live tag reads), never writes.
        SuspectSet suspects;
        try
        {
            suspects = fault is null or { IsUnparsed: true }
                ? new SuspectSet([], null)
                : await DiagnoseSuspects.BuildAsync(
                    fault, project, SessionTagsFor(host, explicitIp, session), ct).ConfigureAwait(false);
        }
        catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.TagRouteUnavailable)
        {
            return new DiagnoseError(ex.Error.Message, ErrorKinds.TagRouteUnavailable, ex.Error.Hint);
        }

        var status = IdentityStatus.Decode(identity.Status);

        return new DiagnoseResult(
            Faulted:        faulted,
            IdentityStatus: Format(identity.Status),
            Status:         status,
            Forces:         BuildForces(status),
            Mode:           mode,
            MajorFault:     fault,
            Suspects:       suspects.Suspects,
            Fix:            faulted ? FixText : null,
            SuspectsNote:   suspects.Note)
        {
            UnparsedRungs = suspects.UnparsedRungs,
        };
    }

    /// <summary>The session's tag service for live subscript/.PRE reads; null when it is not the diagnosed controller.</summary>
    private static ITagService? SessionTagsFor(string host, bool explicitIp, TagSessionService session)
    {
        if (!session.IsConnected) return null;
        if (explicitIp && !string.Equals(host, session.ControllerHost, StringComparison.OrdinalIgnoreCase))
            return null;
        return session.Service;
    }

    private static string Format(ushort status) => IdentityStatus.Format(status);

    /// <summary>
    /// Read-only mode lookup through <see cref="ControllerModeHandler"/> (mode argument always null).
    /// Omitted when no session is active or when an explicit address differs from the session's.
    /// </summary>
    private static async Task<string?> ReadModeAsync(
        string host, bool explicitIp, TagSessionService session, ControllerModeService modeService,
        ushort identityStatus, CancellationToken ct)
    {
        if (!session.IsConnected) return null;
        if (explicitIp && !string.Equals(host, session.ControllerHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var result = await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modeService, ct, identityStatus: identityStatus).ConfigureAwait(false);
        return result is ControllerModeResult r && r.ControllerMode != "Unknown" ? r.ControllerMode : null;
    }

    private static Task<EmulatorIdentityInfo> DefaultIdentityReader(string host, CancellationToken ct) =>
        EmulatorIdentity.ReadAsync(host, ct: ct);

    private static Task<byte[]> DefaultFaultReader(string host, CancellationToken ct) =>
        ControllerFaultReader.ReadAsync(host, ct: ct);
}
