using System.Text.Json.Serialization;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Successful response from <see cref="ControllerModeHandler.ExecuteAsync"/>
/// for both read and write operations.
/// </summary>
/// <param name="Status">Always <c>"ok"</c> on success.</param>
/// <param name="ControllerMode">
/// The current controller mode as observed by this service.
/// Values: <c>"Program"</c>, <c>"Run"</c>, <c>"Test"</c>, <c>"Faulted"</c>,
/// or <c>"Unknown"</c> when the actual controller state has not yet been read.
/// </param>
/// <param name="RequestedControllerMode">
/// The mode that was last requested via a write call, or the same as
/// <see cref="ControllerMode"/> when no write has been made in this session.
/// </param>
/// <param name="Virtual">
/// <c>true</c> when the active session is backed by an in-memory
/// <see cref="VirtualTagService"/>; <c>false</c> for a live PLC.
/// </param>
/// <param name="AppliedToController">
/// Present (always <c>false</c>) on write results: the request was recorded in
/// memory only and no mode change was sent to any controller.  Omitted on reads.
/// </param>
/// <param name="Note">
/// Present only on reads where the live mode could not be determined: names the
/// SDK error (or unrecognised value) behind a <c>"Unknown"</c> mode.  Omitted otherwise.
/// </param>
public record ControllerModeResult(
    [property: JsonPropertyName("status")]                   string Status,
    [property: JsonPropertyName("controllerMode")]           string ControllerMode,
    [property: JsonPropertyName("requestedControllerMode")] string RequestedControllerMode,
    [property: JsonPropertyName("virtual")]                  bool   Virtual,
    [property: JsonPropertyName("appliedToController"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? AppliedToController = null,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null,
    [property: JsonPropertyName("keyswitch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Keyswitch = null);

/// <summary>
/// A controller-mode observation read from a live session.
/// </summary>
/// <param name="Mode">
/// <c>"Program"</c>, <c>"Run"</c>, <c>"Test"</c>, <c>"Faulted"</c>, or <c>"Unknown"</c>.
/// </param>
/// <param name="Note">
/// When <paramref name="Mode"/> is <c>"Unknown"</c>, the reason (naming the SDK
/// error); otherwise <c>null</c>.
/// </param>
public record ControllerModeReading(string Mode, string? Note = null);

/// <summary>
/// Implemented by live <see cref="ITagService"/> backends that can read the
/// controller's real mode (the SDK backend reads it from the same session and
/// call that <c>logix_pending_edits</c> uses).
/// </summary>
public interface IControllerModeSource
{
    /// <summary>
    /// Reads the live controller mode.  Never throws for SDK failures: an
    /// unreadable mode is reported as <c>"Unknown"</c> with a note.
    /// </summary>
    Task<ControllerModeReading> ReadControllerModeAsync(CancellationToken ct = default);
}

/// <summary>
/// Derives the controller mode of a CIP session from the ListIdentity status word - the same
/// source <c>logix_diagnose</c> decodes (<see cref="IdentityStatus.Decode"/>).
/// </summary>
public static class IdentityControllerMode
{
    /// <summary>
    /// Maps a decoded identity status word to a mode name.  <c>run</c> and <c>program</c> device states
    /// become <c>Run</c> / <c>Program</c>, prefixed with <c>Remote </c> when the keyswitch is in REM;
    /// <c>faultedModes</c> becomes <c>Faulted</c>; anything else is <c>Unknown</c>.
    /// </summary>
    /// <param name="status">The decoded identity status word.</param>
    /// <returns>The mode name.</returns>
    public static string FromStatus(StatusWord status)
    {
        var remote = status.Keyswitch == "remote" ? "Remote " : "";
        return status.DeviceStatus switch
        {
            "run"          => remote + "Run",
            "program"      => remote + "Program",
            "faultedModes" => "Faulted",
            _              => "Unknown",
        };
    }

    /// <summary>Builds the reading (mode, optional note, keyswitch) for an identity status word.</summary>
    /// <param name="identityStatus">The raw ListIdentity status word.</param>
    /// <returns>The mode and keyswitch, with a note when the mode is <c>Unknown</c>.</returns>
    public static (string Mode, string? Note, string Keyswitch) Read(ushort identityStatus)
    {
        var status = IdentityStatus.Decode(identityStatus);
        var mode   = FromStatus(status);
        var note   = mode == "Unknown"
            ? $"Identity status {IdentityStatus.Format(identityStatus)} reports device status " +
              $"'{status.DeviceStatus}' (bits {status.DeviceStatusBits}), which is not a run, program or faulted state."
            : null;
        return (mode, note, status.Keyswitch);
    }
}

/// <summary>
/// Error response from <see cref="ControllerModeHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
/// <param name="Kind">
/// Machine-readable error category (e.g. <c>"ModeChangeNotApplied"</c>), or
/// <c>null</c> for uncategorised errors.
/// </param>
public record ControllerModeError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null);

// ── ControllerModeHandler ─────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_controller_mode</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// <b>Read path</b> — when <paramref name="mode"/> is <c>null</c> or omitted,
/// the handler returns the current <see cref="ControllerModeService.ControllerMode"/>
/// and <see cref="ControllerModeService.RequestedMode"/> without modifying anything.
/// </para>
/// <para>
/// <b>Write path</b> — when <paramref name="mode"/> is supplied, the handler
/// enforces a safety gate before changing the mode:
/// <list type="bullet">
///   <item>If the active session is backed by an emulated controller
///   (<see cref="TagSessionService.IsVirtual"/> is <c>true</c>), the change
///   is always allowed — there is no real process that could be stopped.</item>
///   <item>If the session is connected to a physical controller and
///   <paramref name="confirm"/> is <c>false</c> (the default), the request is
///   refused with a descriptive error.  Changing to <c>Program</c> mode on a
///   live PLC halts program scan; the confirm flag makes the risk explicit.</item>
///   <item>If the session is connected to a physical controller and
///   <paramref name="confirm"/> is <c>true</c>, the safety gate passes but the
///   request is still NOT applied: an error of kind <c>ModeChangeNotApplied</c>
///   directs the engineer to Studio 5000 or the controller keyswitch.</item>
///   <item>Virtual sessions record the request in memory only; the result carries
///   <c>appliedToController: false</c>.</item>
///   <item>Every non-refused write attempt is audited as a <c>controller_mode</c> event.</item>
/// </list>
/// </para>
/// </summary>
public static class ControllerModeHandler
{
    /// <summary>
    /// Mode strings accepted by the write path.  Matching is case-insensitive.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidModes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Program", "Run", "Test" };

    /// <summary>
    /// Reads or sets the controller mode for the active session.
    /// </summary>
    /// <param name="mode">
    /// When <c>null</c>, the handler performs a read-only query and returns the
    /// current mode without making any change.  When non-null, the handler
    /// attempts to set the requested mode (subject to the safety gate).
    /// Accepted values: <c>"Program"</c>, <c>"Run"</c>, <c>"Test"</c>
    /// (case-insensitive).
    /// </param>
    /// <param name="confirm">
    /// Required to be <c>true</c> to allow a mode change on a physical (non-virtual)
    /// controller.  Ignored for virtual sessions.  Defaults to <c>false</c>.
    /// </param>
    /// <param name="sessionService">The active tag session singleton.</param>
    /// <param name="modeService">
    /// Singleton that tracks the current and requested controller mode.
    /// </param>
    /// <param name="ct">Cancellation token (reserved for future async reads).</param>
    /// <returns>
    /// A <see cref="ControllerModeResult"/> on success, or a
    /// <see cref="ControllerModeError"/> on failure.  Never throws.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string?               mode,
        bool                  confirm,
        TagSessionService     sessionService,
        ControllerModeService modeService,
        CancellationToken     ct           = default,
        Capabilities?         capabilities = null,
        DiagnoseHandler.IdentityReader? identityReader = null,
        ushort?               identityStatus = null)
    {
        // ── Pre-condition: active session required ────────────────────────────
        if (!sessionService.IsConnected)
        {
            // A read (mode null) changes nothing, so only a mode change request is audited.
            if (mode is not null)
                AuditLogger.LogToolRefused("logix_controller_mode", ErrorKinds.NoActiveSession);
            return new ControllerModeError(
                "No active session. Call logix_connect first.",
                Kind: ErrorKinds.NoActiveSession);
        }

        // ── Read path ─────────────────────────────────────────────────────────
        // Reads are always allowed regardless of the LGX_ENABLE_LIVE_WRITES gate.
        if (mode is null)
        {
            // Live SDK sessions: read the real mode from the same source
            // logix_pending_edits uses rather than the in-memory placeholder.
            var svc = TagRouteResolver.Backend(sessionService.Service);

            if (!sessionService.IsVirtual && svc is IControllerModeSource source)
            {
                var reading = await source.ReadControllerModeAsync(ct).ConfigureAwait(false);
                return new ControllerModeResult(
                    Status:                  "ok",
                    ControllerMode:          reading.Mode,
                    RequestedControllerMode: modeService.RequestedMode,
                    Virtual:                 false,
                    Note:                    reading.Note);
            }

            // CIP sessions: use the source logix_diagnose uses (the ListIdentity status word).
            if (!sessionService.IsVirtual && sessionService.IsLiveController
                && !string.IsNullOrWhiteSpace(sessionService.ControllerHost))
            {
                var host = sessionService.ControllerHost!;
                var word = identityStatus;
                if (word is null)
                {
                    try
                    {
                        var id = await (identityReader ?? DefaultIdentityReader)(host, ct).ConfigureAwait(false);
                        word = id.Status;
                    }
                    catch (EmulatorIdentityException ex)
                    {
                        return new ControllerModeResult(
                            Status:                  "ok",
                            ControllerMode:          "Unknown",
                            RequestedControllerMode: modeService.RequestedMode,
                            Virtual:                 false,
                            Note:                    $"Cannot read the controller's ListIdentity: {ex.Message}");
                    }
                }

                var (cipMode, cipNote, keyswitch) = IdentityControllerMode.Read(word.Value);
                return new ControllerModeResult(
                    Status:                  "ok",
                    ControllerMode:          cipMode,
                    RequestedControllerMode: modeService.RequestedMode,
                    Virtual:                 false,
                    Note:                    cipNote,
                    Keyswitch:               keyswitch);
            }

            return new ControllerModeResult(
                Status:                  "ok",
                ControllerMode:          modeService.ControllerMode,
                RequestedControllerMode: modeService.RequestedMode,
                Virtual:                 sessionService.IsVirtual);
        }

        // ── Write path ────────────────────────────────────────────────────────

        // ── Live-write capability gate ─────────────────────────────────────────
        // When LGX_ENABLE_LIVE_WRITES is not set, mode changes on live controllers
        // are refused before any network or SDK call.  Mode changes on virtual,
        // L5X, and SDK offline sessions are always allowed.
        if (capabilities is not { LiveWritesEnabled: true } && sessionService.IsLiveController)
        {
            AuditLogger.LogCapabilityRefusal("logix_controller_mode", Capabilities.EnvVar);
            return (
                new CapabilityDisabledError(
                    Error:
                        $"Controller mode change to '{mode}' refused: " +
                        "LGX_ENABLE_LIVE_WRITES is not set. " +
                        "Mode changes on live controllers require the opt-in capability flag.",
                    Hint:
                        $"Set the {Capabilities.EnvVar} environment variable to 1 to enable " +
                        $"live controller mutations. " +
                        $"Client command: {Capabilities.ClientCommandHint}"));
        }

        // Validate the requested mode string.
        if (!ValidModes.Contains(mode))
            return (
                new ControllerModeError(
                    $"Invalid mode '{mode}'. Valid modes are: Program, Run, Test."));

        // Safety gate: physical controllers require explicit confirmation.
        // Changing to Program mode halts program scan — the caller must
        // acknowledge the risk by passing confirm=true.
        if (!sessionService.IsVirtual && !confirm)
            return (
                new ControllerModeError(
                    $"Mode change to '{mode}' refused: the session is connected to a " +
                    "physical controller. Changing to Program mode stops the running process. " +
                    "Pass confirm=true to override this safety gate."));

        // Live (non-virtual) sessions: this release does not change a live
        // controller's mode.  Never report "ok" for a request that was not applied.
        if (!sessionService.IsVirtual)
        {
            AuditLogger.LogControllerMode(mode, isVirtual: false, outcome: "ModeChangeNotApplied");
            return (
                new ControllerModeError(
                    $"Mode change to '{mode}' was NOT applied: this tool does not change a " +
                    "live controller's mode in this release. Use Studio 5000 or the " +
                    "controller keyswitch to change the mode.",
                    Kind: "ModeChangeNotApplied"));
        }

        // Virtual session: track the request in memory only.
        modeService.SetRequestedMode(mode);
        AuditLogger.LogControllerMode(mode, isVirtual: true, outcome: "recorded_virtual");

        return (
            new ControllerModeResult(
                Status:                  "ok",
                ControllerMode:          modeService.ControllerMode,
                RequestedControllerMode: modeService.RequestedMode,
                Virtual:                 true,
                AppliedToController:     false));
    }

    private static Task<EmulatorIdentityInfo> DefaultIdentityReader(string host, CancellationToken ct) =>
        EmulatorIdentity.ReadAsync(host, ct: ct);
}
