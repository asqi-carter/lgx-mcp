using System.Text.Json.Serialization;
using L5Sharp.Core;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Successful response from <see cref="ConnectHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Status">Always <c>"connected"</c> on success.</param>
/// <param name="Gateway">
/// Gateway address echoed from the caller's input, or <c>null</c> when the
/// backend does not open a network connection (i.e. when
/// <see cref="GatewayUsed"/> is <c>false</c>).  Only non-null for the
/// <c>"cip"</c> backend.
/// </param>
/// <param name="Path">
/// CIP backplane path echoed from the caller's input, or <c>null</c> when the
/// backend does not open a network connection (i.e. when
/// <see cref="GatewayUsed"/> is <c>false</c>).  Only non-null for the
/// <c>"cip"</c> backend.
/// </param>
/// <param name="GatewayUsed">
/// <c>true</c> when <see cref="Gateway"/> and <see cref="Path"/> refer to an
/// active EtherNet/IP connection to a physical controller (backend
/// <c>"cip"</c>).  <c>false</c> for every offline backend
/// (<c>"sdk"</c>, <c>"l5x"</c>, <c>"virtual"</c>), in which case
/// <see cref="Gateway"/> and <see cref="Path"/> are both <c>null</c>.
/// Callers MUST check this flag — do not infer a live network session from the
/// mere presence of an IP address in earlier versions of this response.
/// </param>
/// <param name="Virtual">
/// <c>true</c> when the session is backed by an in-memory
/// <see cref="VirtualTagService"/>; <c>false</c> for a live PLC or SDK backend.
/// </param>
/// <param name="Backend">
/// The tag-service backend in use: <c>"cip"</c> for a live EtherNet/IP connection
/// via <see cref="LibPlcTagService"/>, <c>"l5x"</c> for an offline
/// <see cref="L5XOfflineTagService"/> backed by the open L5X project, or
/// <c>"sdk"</c> for an SDK-backed service created by <see cref="ConnectHandler.DefaultSdkFactory"/>
/// against the loaded project file.
/// Virtual-mode sessions always use the <c>"virtual"</c> value.
/// </param>
/// <param name="TagCount">
/// Number of tags seeded into the store (virtual mode only; <c>null</c> for live).
/// </param>
/// <param name="Gated">
/// <c>true</c> when the session is wrapped in a <see cref="GatedTagService"/>
/// that enforces the project's write-gate policy on every write.
/// </param>
/// <param name="Target">
/// Unambiguous operating-mode identifier that tells callers whether writes will
/// reach a physical PLC:
/// <list type="bullet">
///   <item><c>"online-controller"</c> — session targets a live PLC via EtherNet/IP
///     (backend <c>"cip"</c>); writes reach the physical controller.</item>
///   <item><c>"offline-project"</c> — session operates against a project file or
///     in-memory store only (backend <c>"sdk"</c>, <c>"l5x"</c>, or
///     <c>"virtual"</c>); writes do <em>not</em> reach any physical PLC.</item>
/// </list>
/// This field is always present so callers and agents can determine the operating
/// mode without reading source code or inferring it from the <paramref name="Backend"/>
/// or <paramref name="Virtual"/> fields.
/// </param>
/// <param name="Online">
/// <c>true</c> when the session targets a live physical controller (i.e.
/// <see cref="Target"/> is <c>"online-controller"</c>); <c>false</c> for every
/// offline backend (<c>"sdk"</c> offline, <c>"l5x"</c>, <c>"virtual"</c>, or
/// <c>"cip"</c> CIP sessions also set this via the target field).
/// Callers SHOULD use <see cref="Target"/> for authoritative operating-mode
/// classification; this field is a convenience shorthand.
/// </param>
/// <param name="DownloadPerformed">
/// <c>true</c> when the PROGRAM→download→go_online→RUN sequence was executed
/// during session establishment (i.e. <c>backend="sdk"</c>,
/// <c>sdkMode="online"</c>, <c>allowControllerDownload=true</c> were all
/// specified <em>and</em> the initial go-online probe failed so a download was
/// required to bring the controller image in sync with the open project).
/// <c>false</c> in all other cases — including when
/// <c>allowControllerDownload=true</c> was specified but the probe succeeded
/// without a download.
/// <c>null</c> for non-SDK backends (CIP, L5X, virtual) where the concept of
/// a project download does not apply.
/// </param>
/// <param name="ModeTransitions">
/// Ordered list of controller mode names that were requested during session
/// establishment, in invocation order.  Only populated (non-empty) when
/// <see cref="DownloadPerformed"/> is <c>true</c>.  Typical value for a full
/// download sequence: <c>["Program", "Run"]</c>.  An empty array when no mode
/// transitions were performed; <c>null</c> for non-SDK backends.
/// </param>
/// <param name="Correlation">
/// Identifies the mechanism used to establish the online SDK session.  Present
/// only when <c>backend="sdk"</c> and <c>sdkMode="online"</c> (or the
/// no-project-file upload path); <c>null</c> for every other backend and for
/// SDK offline sessions.
/// <list type="bullet">
///   <item>
///     <c>"already-correlated"</c> — <c>GoOnlineAsync</c> succeeded on the
///     first attempt (rung 1).  The controller already held a matching project
///     image; no upload or download was required.
///   </item>
///   <item>
///     <c>"upload"</c> — <c>UploadAsync</c> was required to pull the
///     controller's current image into the working copy before
///     <c>GoOnlineAsync</c> could succeed (rung 2).  Non-destructive to the
///     controller.
///   </item>
///   <item>
///     <c>"download"</c> — the PROGRAM→download→go_online→RUN sequence was
///     executed (rung 3).  Only reached when rungs 1 and 2 both failed and
///     <c>allowControllerDownload=true</c> was specified.  Destructive —
///     overwrites the controller's stored program image.
///   </item>
///   <item>
///     <c>"upload-to-new-project"</c> — the session was established via
///     <c>LogixProject.UploadToNewProjectAsync</c> (no pre-existing project
///     file was required).  The controller's current program image was
///     uploaded to a new server-managed ACD, which was then used to open the
///     online session.  Non-destructive to the controller.  Only produced
///     when <c>backend="sdk"</c>, <c>sdkMode="online"</c>, and no project
///     was pre-loaded via <c>logix_open_project</c>.
///   </item>
/// </list>
/// Callers should check this field to confirm whether the controller was
/// disturbed during session establishment.
/// </param>
/// <param name="Message">Human-readable summary of the connection outcome, including
/// the operating mode in plain language.</param>
/// <param name="GateSource">
/// Identifies which project the write-gate policy for this session was derived
/// from, so that an open (file-loaded) project and an uploaded (controller-
/// sourced) project are never confused when both are in play.
/// <c>"uploaded-controller-project"</c> when the comm-path-wins upload-from-
/// controller branch was taken (<c>backend="sdk"</c>, <c>sdkMode="online"</c>,
/// and a comm path — <c>sdkCommPath</c>, <c>gateway</c>, or <c>gateway</c>+
/// <c>path</c> — was supplied); the gate is derived from the project uploaded
/// from the controller for THIS session, even when a project was already open
/// via <c>logix_open_project</c> (that project remains loaded and keeps
/// serving static tools, but is not the gate source for this online session).
/// <c>null</c> for every other branch (virtual, CIP, L5X, and file-based SDK
/// sessions), where the gate — if any — is derived from the single project the
/// session already operates against and no ambiguity is possible.
/// </param>
public record ConnectResult(
    [property: JsonPropertyName("status")]             string    Status,
    [property: JsonPropertyName("gateway")]            string?   Gateway,
    [property: JsonPropertyName("path")]               string?   Path,
    [property: JsonPropertyName("gateway_used")]       bool      GatewayUsed,
    [property: JsonPropertyName("virtual")]            bool      Virtual,
    [property: JsonPropertyName("backend")]            string    Backend,
    [property: JsonPropertyName("tagCount")]           int?      TagCount,
    [property: JsonPropertyName("gated")]              bool      Gated,
    [property: JsonPropertyName("target")]             string    Target,
    [property: JsonPropertyName("online")]             bool      Online,
    [property: JsonPropertyName("downloadPerformed")]  bool?     DownloadPerformed,
    [property: JsonPropertyName("modeTransitions")]    string[]? ModeTransitions,
    [property: JsonPropertyName("correlation")]        string?   Correlation,
    [property: JsonPropertyName("message")]            string    Message,
    [property: JsonPropertyName("gateSource")]         string?   GateSource = null,
    [property: JsonPropertyName("allowlistSha256")]    string?   AllowlistSha256 = null,
    [property: JsonPropertyName("allowlistEntries")]   int?      AllowlistEntries = null)
{
    /// <summary>
    /// The effective command prefixes (<c>LGX_GATE_COMMAND_PREFIXES</c>): the list, or the
    /// string <c>"*"</c> when every tag name is designated (naming rule only).
    /// </summary>
    [JsonPropertyName("commandPrefixes")]
    public object? CommandPrefixes { get; init; }

    /// <summary>
    /// Channel that carries tag reads and writes of an online SDK session: <c>cip</c> or <c>sdk</c>.
    /// Omitted when the route could not be chosen at connect (<c>LGX_TAG_ROUTE=cip</c> and CIP unavailable;
    /// see <see cref="TagRouteReason"/>) and for sessions that are not online SDK sessions.
    /// </summary>
    [JsonPropertyName("tagRoute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TagRoute { get; init; }

    /// <summary>
    /// Why <see cref="TagRoute"/> is what it is: the setting, the verified identity, or why CIP was not used
    /// (parse failure, identity mismatch, connect failure).
    /// </summary>
    [JsonPropertyName("tagRouteReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TagRouteReason { get; init; }

    /// <summary>
    /// How the CIP controller was verified as the SDK session's controller: <c>serial</c>, <c>name</c> or
    /// <c>serial+name</c>. Omitted unless <see cref="TagRoute"/> is <c>cip</c>.
    /// </summary>
    [JsonPropertyName("identityCheck")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdentityCheck { get; init; }

    /// <summary>
    /// Milliseconds each phase of the SDK upload-connect took (<c>upload</c>, <c>saveL5x</c>, <c>parse</c>,
    /// <c>gate</c>, <c>identity</c>, <c>total</c>). Omitted for connects that take no upload route.
    /// </summary>
    [JsonPropertyName("phaseTimings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectPhaseTimings? PhaseTimings { get; init; }

    /// <summary>
    /// The same object as <see cref="PhaseTimings"/>, under the name <c>logix_connect_status</c> uses for it, so a
    /// connected payload carries <c>phaseTimingsSummary</c> whether it came from a connect that finished within the
    /// sync budget, from a direct connect, or from the status poll. Omitted when there are no timings.
    /// </summary>
    [JsonPropertyName("phaseTimingsSummary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectPhaseTimings? PhaseTimingsSummary => PhaseTimings;

    /// <summary>
    /// How many stale offline build-check copies (older than 24 h, not locked) this connect removed from
    /// <c>%TEMP%\logix-mcp-work\build-verify\</c>. Omitted when the SDK backend is not loaded.
    /// </summary>
    [JsonPropertyName("staleOfflineCopiesRemoved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StaleOfflineCopiesRemoved { get; init; }

    /// <summary>
    /// How many <c>ctl-*</c> upload slot folders this connect removed because the process that owned them (the pid in
    /// the slot file) is no longer running, for example a server that was killed mid-session. Omitted when the SDK
    /// backend is not loaded.
    /// </summary>
    [JsonPropertyName("staleUploadSlotsRemoved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StaleUploadSlotsRemoved { get; init; }

    /// <summary>
    /// Present when this connect replaced a session that was live: the id and finish time of the connect that made
    /// it. The old session was closed and the controller was uploaded again. Omitted for a first connect.
    /// </summary>
    [JsonPropertyName("replacedSession")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveSessionStamp? ReplacedSession { get; init; }

    /// <summary>
    /// Logix Designer version as <c>major.minor</c> (SDK backend only); omitted when not known. At connect it comes
    /// from the project's software revision; see <see cref="DesignerVersionSource"/>.
    /// </summary>
    [JsonPropertyName("designerVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DesignerVersion { get; init; }

    /// <summary>
    /// Whether the SDK build check can run: <c>true</c> for Logix Designer 37 and later; omitted when not known
    /// (a version below 37 is only confirmed by the SDK's own error on the first publish).
    /// </summary>
    [JsonPropertyName("buildCheckAvailable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BuildCheckAvailable { get; init; }

    /// <summary>
    /// The SDK capability table (SDK backend only; omitted otherwise): what the SDK can do on this Logix Designer,
    /// each entry available / unavailable / unknown with its source. Same shape as <c>lgx://sdk/capabilities</c>.
    /// </summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SdkCapabilitiesReport? Capabilities { get; init; }

    /// <summary><c>"default"</c> or <c>"LGX_GATE_COMMAND_PREFIXES"</c>.</summary>
    [JsonPropertyName("commandPrefixesSource")]
    public string? CommandPrefixesSource { get; init; }

    /// <summary>
    /// Number of rungs or elements the write gate could not parse (they fail closed: tags depending on
    /// them are never granted). Present whenever the session enforces a gate derived from a project;
    /// omitted when no gate was derived.
    /// </summary>
    [JsonPropertyName("unparsedContentCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UnparsedContentCount { get; init; }

    /// <summary>The first 10 unparsed entries (location, first 200 characters, reason); omitted when none.</summary>
    [JsonPropertyName("unparsedContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedContent>? UnparsedContent { get; init; }

    /// <summary>The first 10 rungs the write gate could not parse (program, routine, rung, error); omitted when none.</summary>
    [JsonPropertyName("unparsedRungs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// Major revision of the target (for example <c>36</c>): <c>Controller/@MajorRev</c> of the open project or ACD,
    /// else the integer part of <c>@SoftwareRevision</c>; for an online target the uploaded project's value.
    /// <c>null</c> when unknown.
    /// </summary>
    [JsonPropertyName("targetMajorRevision")]
    public int? TargetMajorRevision { get; init; }

    /// <summary>
    /// Instruction mnemonic set the target uses: <c>iec</c> (revision 36 and above), <c>legacy</c> (35 and below)
    /// or <c>unknown</c> (no check applied; a warning is shown).
    /// </summary>
    [JsonPropertyName("mnemonicSet")]
    public string MnemonicSet { get; init; } = MnemonicSetNames.Unknown;

    /// <summary>Warnings about the configuration (for example <c>*</c> mixed with other entries); omitted when none.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }

    /// <summary>
    /// Retries the SDK upload connect made because <c>LdSdkService</c> was unreachable (one entry per retry:
    /// the error and the milliseconds waited). Omitted when the first attempt succeeded.
    /// </summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}

/// <summary>
/// Error response from <see cref="ConnectHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
public record ConnectError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Retries made before the connect gave up (error and ms waited each); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }

    /// <summary>Machine-readable error kind (for example <c>NoProjectLoaded</c>); omitted when the error is untyped.</summary>
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>Next steps for the caller; omitted when there is none.</summary>
    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

#if LOGIX_SDK
/// <summary>
/// Structured error returned when a connect gave up after its crash-retry limit because the Rockwell SDK service
/// (<c>LdSdkServer</c>) kept crashing (G48).
/// </summary>
/// <param name="Error">The last failure's message.</param>
/// <param name="Kind">Machine-readable kind: always <c>"SdkServiceUnstable"</c>.</param>
/// <param name="SdkRestarts">Every SDK restart seen in this connect, one record per crash, in order.</param>
/// <param name="Hint">States the restart count and duration, advises waiting a minute, and points to <c>logix_doctor</c>.</param>
public record ConnectSdkServiceUnstableError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("sdkRestarts")] IReadOnlyList<SdkRestartRecord> SdkRestarts,
    [property: JsonPropertyName("hint")]        string  Hint)
{
    /// <summary>Retries made before the connect gave up (error and ms waited each); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }

    /// <summary>The wait guidance shared by the error tail and the hint.</summary>
    public const string WaitGuidance = "wait a minute, then reconnect";

    private const string LegacyTail = "Recovery will restart it \u2014 wait a few seconds and retry.";

    // The comms-fault message's manual-restart advice contradicts a give-up's "recovery will restart it".
    private const string CommsFaultAdvice =
        "Check the Windows Application event log for LdSdkServer.exe errors and restart the LdSdkService service, then retry.";

    private const string EventLogFallback =
        " If it keeps failing, check the Windows Application event log for LdSdkServer.exe errors.";

    /// <summary>
    /// Builds the error text for a give-up: <paramref name="message"/> failure message with its tail saying
    /// to wait a minute, then reconnect (matching <see cref="BuildHint"/>).
    /// </summary>
    /// <param name="commPath">The controller comm path.</param>
    /// <param name="message">The underlying failure message.</param>
    public static string BuildError(string commPath, string message)
    {
        var commsFault = message.Contains(CommsFaultAdvice, StringComparison.Ordinal);
        var body = message.Replace(LegacyTail, "Recovery will restart it \u2014 " + WaitGuidance + ".")
                          .Replace(CommsFaultAdvice, "").TrimEnd();
        var text = $"Upload from controller at '{commPath}' failed: {body}";
        if (!text.Contains(WaitGuidance, StringComparison.Ordinal))
            text = $"{text} Recovery will restart it \u2014 {WaitGuidance}.";
        return commsFault ? text + EventLogFallback : text;
    }

    /// <summary>Builds the hint for <paramref name="restarts"/> seen over <paramref name="elapsed"/>.</summary>
    /// <param name="restarts">Number of SDK restarts in the connect.</param>
    /// <param name="elapsed">How long the connect ran before giving up.</param>
    public static string BuildHint(int restarts, TimeSpan elapsed) =>
        $"The Rockwell SDK service crashed and restarted {restarts} times in {(int)Math.Round(elapsed.TotalSeconds)} seconds " +
        "during this connect and the connect gave up. Wait a minute for the service to settle, then reconnect. " +
        "logix_doctor shows recent crash counts.";
}
#endif

/// <summary>
/// Structured error returned by <see cref="ConnectHandler.ExecuteAsync"/> whenever a
/// contextually required argument was omitted (or supplied as an empty/whitespace
/// string) for the combination of <c>backend</c>/<c>sdkMode</c> parameters the
/// caller selected.
///
/// <para>
/// This is the dedicated error surface for every "missing argument" case on
/// <c>logix_connect</c> — e.g. <c>backend="cip"</c> with no <c>gateway</c>, or
/// <c>backend="sdk"</c>/<c>sdkMode="online"</c> with neither <c>sdkCommPath</c>
/// nor <c>gateway</c> and no project loaded. Because <c>gateway</c>, <c>path</c>,
/// and <c>sdkCommPath</c> all default to empty/<c>null</c> at the MCP tool
/// boundary, the MCP argument marshaller never
/// rejects these calls itself — so the handler MUST be the one to detect the
/// missing value and explain it. The marshaller's generic "missing required
/// parameter" text must never be the error surface for this tool.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description naming the backend/mode combination and the
/// missing argument.
/// </param>
/// <param name="Kind">
/// Machine-readable error category, e.g. <c>"MissingGateway"</c> or
/// <c>"MissingSdkCommPath"</c>, so callers can branch without parsing
/// <see cref="Error"/>.
/// </param>
/// <param name="Parameter">
/// The exact parameter name the caller must supply (e.g. <c>"gateway"</c> or
/// <c>"sdkCommPath"</c>).
/// </param>
/// <param name="Hint">
/// Actionable, imperative instruction naming the specific argument to supply
/// (and any viable alternative, such as a different backend or pre-loading a
/// project) so the caller can self-correct without consulting documentation.
/// </param>
public record ConnectMissingArgumentError(
    [property: JsonPropertyName("error")]     string Error,
    [property: JsonPropertyName("kind")]      string Kind,
    [property: JsonPropertyName("parameter")] string Parameter,
    [property: JsonPropertyName("hint")]      string Hint);

/// <summary>
/// Structured error returned when the registered project's file is gone from disk
/// (<see cref="ErrorKinds.ProjectFileMissing"/>).
/// </summary>
/// <param name="Error">Human-readable description naming the missing file.</param>
/// <param name="Kind">Always <c>"ProjectFileMissing"</c>.</param>
/// <param name="Hint">Names the file, says the upload copy was removed, and gives the next step.</param>
public record ConnectProjectFileMissingError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind")]  string Kind,
    [property: JsonPropertyName("hint")]  string Hint);

/// <summary>
/// Structured error returned when the <c>backend</c> parameter supplied to
/// <see cref="ConnectHandler.ExecuteAsync"/> is not one of the recognised values.
/// Lists the exact set of valid options so callers can self-correct without
/// consulting documentation.
/// </summary>
/// <param name="Error">Human-readable description of the validation failure.</param>
/// <param name="Backend">The unrecognised value that was supplied.</param>
/// <param name="ValidValues">
/// The exhaustive list of values the <c>backend</c> parameter accepts:
/// <c>"cip"</c>, <c>"sdk"</c>, and <c>"l5x"</c>.
/// </param>
public record ConnectBadBackendError(
    [property: JsonPropertyName("error")]       string   Error,
    [property: JsonPropertyName("backend")]     string   Backend,
    [property: JsonPropertyName("validValues")] string[] ValidValues);

/// <summary>
/// Structured error returned by <see cref="ConnectHandler.ExecuteAsync"/> when the
/// post-connect CIP probe (an <c>@tags</c> listing) fails for <c>backend="cip"</c>.
/// <para>
/// <see cref="Kind"/> is <c>"CipPathInvalid"</c> when the caller supplied an empty
/// or omitted <c>path</c> (the path is never silently defaulted), and
/// <c>"ConnectionFailed"</c> when an explicit path was supplied. In both cases the
/// <see cref="Error"/> message names the gateway and path that were tried.
/// </para>
/// </summary>
/// <param name="Error">Human-readable description naming the gateway and path tried.</param>
/// <param name="Kind"><c>"CipPathInvalid"</c>, <c>"ConnectionFailed"</c> or <c>"CipRuntimeMissing"</c>.</param>
/// <param name="Gateway">The gateway that was probed.</param>
/// <param name="Path">The path that was probed (empty string when none was supplied).</param>
/// <param name="Hint">Actionable guidance for the caller.</param>
public record ConnectCipProbeError(
    [property: JsonPropertyName("error")]   string Error,
    [property: JsonPropertyName("kind")]    string Kind,
    [property: JsonPropertyName("gateway")] string Gateway,
    [property: JsonPropertyName("path")]    string Path,
    [property: JsonPropertyName("hint")]    string Hint);

/// <summary>
/// Describes the outcome of probing a single candidate SDK directory during the
/// runtime resolver's search for <c>RockwellAutomation*.dll</c> files.
/// Included in <see cref="ConnectSdkNotAvailableError.ProbedPaths"/> so the caller
/// can see exactly which directories were checked and why each one was rejected.
/// </summary>
/// <param name="Path">
/// Absolute path of the candidate directory that was probed.
/// </param>
/// <param name="Status">
/// Short machine-readable string indicating the probe outcome:
/// <list type="bullet">
///   <item><c>"not_found"</c> — the directory does not exist on the file system.</item>
///   <item><c>"no_rockwell_dlls"</c> — the directory exists but contains no
///     <c>RockwellAutomation*.dll</c> files at all.</item>
///   <item><c>"client_dll_missing"</c> — the directory exists and contains other
///     <c>RockwellAutomation*.dll</c> files (partial SDK install) but is missing the
///     required <c>RockwellAutomation.LogixDesigner.LogixProject.CSClient.dll</c> client
///     assembly, which ships only as a NuGet package.</item>
///   <item><c>"ok"</c> — the directory qualifies (the CSClient assembly is present).</item>
/// </list>
/// </param>
/// <param name="Message">
/// Optional human-readable elaboration on the probe outcome.  Set when additional
/// context helps the caller take action — for example, naming the installer target
/// when the app-local <c>sdk/vendor/</c> directory is present but incomplete.
/// <c>null</c> for entries where the <paramref name="Status"/> alone is sufficient.
/// </param>
public record SdkProbeEntry(
    [property: JsonPropertyName("path")]    string  Path,
    [property: JsonPropertyName("status")]  string  Status,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>
/// Error returned when <c>backend="sdk"</c> is requested but the runtime resolver
/// could not find or load the Logix Designer SDK adapter.  Provides actionable
/// instructions — including every probed path and its failure reason — so the
/// caller can self-diagnose without consulting external documentation.
/// </summary>
/// <param name="Error">
/// Human-readable description including:
/// <list type="bullet">
///   <item>Every path that was probed in probe order, with its failure reason.</item>
///   <item>Instructions for setting <c>LOGIX_SDK_PATH</c> to override the search.</item>
///   <item>Steps to enable SDK support: place the
///     <c>RockwellAutomation.LogixDesigner.CSClient</c> NuGet package in
///     <c>.localfeed/</c> and rebuild with <c>-p:EnableLogixSdk=true</c>.</item>
/// </list>
/// </param>
/// <param name="ProbedPaths">
/// Structured list of every directory probed by <see cref="SdkAdapterLoader"/>,
/// in probe order, with a <c>status</c> string for each entry
/// (<c>"not_found"</c>, <c>"no_rockwell_dlls"</c>, <c>"client_dll_missing"</c>,
/// or <c>"ok"</c>).
/// Exposed as a structured field so tooling can inspect individual entries without
/// parsing the human-readable <paramref name="Error"/> string.
/// </param>
/// <param name="LoadFailureReason">
/// When the adapter was located but failed to load (corrupt assembly, version
/// mismatch, missing transitive dependency, or a throwing module initializer),
/// contains the human-readable failure description recorded by
/// <see cref="SdkAdapterLoader.LoadFailureReason"/>.
/// <c>null</c> when no load was attempted — i.e. when the adapter DLL itself
/// was absent or no qualifying Rockwell SDK directory was found during the probe,
/// in which case the <paramref name="ProbedPaths"/> block alone explains the
/// unavailability.
/// Exposed as a dedicated structured field so tooling can detect and act on a
/// load failure without parsing the human-readable <paramref name="Error"/> string.
/// </param>
public record ConnectSdkNotAvailableError(
    [property: JsonPropertyName("error")]             string          Error,
    [property: JsonPropertyName("probedPaths")]       SdkProbeEntry[] ProbedPaths,
    [property: JsonPropertyName("loadFailureReason")] string?         LoadFailureReason,
    [property: JsonPropertyName("hint")]              string?         Hint = null);

/// <summary>
/// Error returned when the <c>logix_connect</c> MCP tool is called with an argument
/// key that is not present in the declared parameter list.
/// Names the unrecognised parameter(s) and lists all valid parameter names so the
/// caller can self-correct without consulting documentation.
/// </summary>
/// <param name="Error">Human-readable description of the validation failure.</param>
/// <param name="UnknownParameters">
/// The exact argument key(s) that were not recognised.
/// </param>
/// <param name="ValidParameters">
/// The exhaustive list of parameter names that <c>logix_connect</c> accepts.
/// </param>
public record ConnectUnknownParameterError(
    [property: JsonPropertyName("error")]             string   Error,
    [property: JsonPropertyName("unknownParameters")] string[] UnknownParameters,
    [property: JsonPropertyName("validParameters")]   string[] ValidParameters);

/// <summary>
/// Structured error returned when <c>backend="sdk"</c> fails because the supplied
/// comm path uses the wrong addressing scheme for the Logix Designer SDK.
///
/// <para>
/// The Logix Designer SDK requires a comm path in the Logix Designer format
/// (e.g. <c>"EmulateEthernet\192.0.2.10"</c>), not the CIP/libplctag format
/// (e.g. <c>"192.0.2.10,1,0"</c>).  When the SDK rejects the supplied path with
/// "No module was found using the supplied encoded route string.", this error is
/// returned with actionable instructions naming the expected form.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description including the path that was tried and the expected
/// Logix Designer comm path format.
/// </param>
/// <param name="CommPathUsed">
/// The comm path that was supplied to the SDK call and caused the route error.
/// </param>
/// <param name="ExpectedFormat">
/// Human-readable description of the expected Logix Designer comm path format,
/// including a concrete example.
/// </param>
/// <param name="Hint">
/// Actionable instruction: check the comm path (slot and port) and that the controller is reachable.
/// </param>
public record ConnectSdkRouteError(
    [property: JsonPropertyName("error")]          string  Error,
    [property: JsonPropertyName("commPathUsed")]   string  CommPathUsed,
    [property: JsonPropertyName("expectedFormat")] string  ExpectedFormat,
    [property: JsonPropertyName("hint")]           string? Hint = null)
{
    /// <summary>Machine-readable error kind: always <see cref="ErrorKinds.CommPathInvalid"/> (the status mirror carries it too).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = ErrorKinds.CommPathInvalid;

    /// <summary>Retries made before the connect gave up (each with the SDK restart it saw, if any); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}

/// <summary>
/// Structured error returned when the Logix Designer SDK's encoded-route error
/// (see <see cref="ConnectHandler.IsRouteError"/>) is probed and the
/// controller's address — derived from the comm path via
/// <see cref="ConnectHandler.TryParseControllerIp"/> — does not answer on TCP
/// port 44818 within the probe timeout.
///
/// <para>
/// This distinguishes a genuinely unreachable controller from a malformed comm
/// path: the SDK's "No module was found using the supplied encoded route
/// string." message is ambiguous between the two, and an engineer or model
/// reading only that text will rewrite a comm path that was correct all along.
/// When the TCP probe confirms the target does not answer, this record is
/// returned instead of <see cref="ConnectSdkRouteError"/> — it deliberately
/// omits any comm-path-format advice, because the format is not the problem.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description naming the controller address, port 44818, and
/// including the verbatim SDK error text so it can be searched.
/// </param>
/// <param name="Kind">Machine-readable error category: always <c>"ConnectionFailed"</c>.</param>
/// <param name="ControllerAddress">
/// The controller address (host:port form, e.g. <c>"192.0.2.20:44818"</c>)
/// that the TCP probe attempted and failed to reach.
/// </param>
/// <param name="Hint">
/// Actionable instruction: check that the controller or emulator is running
/// and reachable on the network. Does not mention the comm-path format —
/// the probe already confirmed the format parses to a valid address.
/// </param>
public record ConnectSdkConnectionFailedError(
    [property: JsonPropertyName("error")]              string Error,
    [property: JsonPropertyName("kind")]               string Kind,
    [property: JsonPropertyName("controller_address")] string ControllerAddress,
    [property: JsonPropertyName("hint")]               string Hint)
{
    /// <summary>Retries made before the connect gave up (each with the SDK restart it saw, if any); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}

/// <summary>
/// Structured error returned when the <c>logix_connect</c> upload-from-controller
/// path fails because the controller holds uncommitted (pending) edits that prevent
/// project extraction.
///
/// <para>
/// When a pending edit is left open in Logix Designer the SDK's
/// <c>SaveAsAsync</c> call raises <c>"Project is not open."</c> — a message
/// about the SDK handle state rather than the real cause.  This record surfaces
/// the actual cause and names both paths out without preferring either,
/// because accepting or cancelling someone else's pending edit is a decision
/// this tool must not make on the operator's behalf.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description naming the controller and the pending-edit
/// root cause.
/// </param>
/// <param name="Kind">
/// Machine-readable error category: always <c>"PendingEditsPresent"</c>.
/// </param>
/// <param name="Hint">
/// Neutral, imperative remediation listing both paths out:
/// accept/finalize the pending edits in Logix Designer,
/// or cancel the pending edits in Logix Designer.
/// Neither option is preferred.
/// </param>
/// <param name="DocsAnchor">
/// Stable slug pointing to the relevant section in <c>docs/SPEC.md</c>:
/// <c>"controller-has-pending-edits"</c>.
/// </param>
public record ConnectPendingEditsError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("hint")]        string  Hint,
    [property: JsonPropertyName("docs_anchor")] string  DocsAnchor)
{
    /// <summary>Retries made before the connect gave up (each with the SDK restart it saw, if any); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}

#if LOGIX_SDK
/// <summary>
/// Structured error returned when the Logix Designer SDK reports an
/// <c>RxE_COMM_*</c> comm-layer fault (e.g. <c>RxE_COMM_DTLERROR</c>) during
/// session establishment.
///
/// <para>
/// This error indicates that the <c>LdSdkService</c> process is running but its
/// internal communications layer has faulted.  It is distinct from a dead PLC:
/// if the PLC is genuinely unreachable a TCP probe on port 44818 also fails;
/// if port 44818 is reachable the PLC is alive and only the SDK comm layer
/// needs recovery (restart <c>LdSdkService</c>).
/// </para>
/// </summary>
/// <param name="Error">Human-readable description including the <c>RxE_COMM_*</c> token.</param>
/// <param name="Kind">Machine-readable kind: always <c>"SdkCommLayerFaulted"</c>.</param>
/// <param name="Hint">Actionable remediation steps.</param>
/// <param name="DocsAnchor">
/// Stable slug referencing the relevant docs section, or <c>null</c>.
/// </param>
public record ConnectSdkCommLayerFaultedError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("hint")]        string  Hint,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null)
{
    /// <summary>Retries made before the connect gave up (each with the SDK restart it saw, if any); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}

/// <summary>
/// Structured error returned when the Logix Designer SDK reports an
/// <c>RxE_COMM_*</c> comm-layer fault on the connect/upload path AND a TCP
/// probe on the controller's EtherNet/IP port 44818 confirms the controller
/// is reachable.
///
/// <para>
/// This indicates that the PLC is alive on the network but
/// <c>LdSdkService</c> is running with a faulted communications layer.  The
/// condition is distinct from <see cref="ConnectError"/>: the controller itself
/// is reachable, so the fault is isolated to the SDK service's comm layer.
/// An operator must restart <c>LdSdkService</c> to recover communications.
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description including the controller IP, port 44818 reachability
/// confirmation, and the raw <c>RxE_COMM_*</c> SDK code verbatim.
/// </param>
/// <param name="Kind">Machine-readable kind: always <c>"SdkServiceDegraded"</c>.</param>
/// <param name="SdkCode">
/// The verbatim SDK error code (e.g. <c>"RxE_COMM_DTLERROR"</c>) from the
/// underlying Rockwell SDK exception.  Callers can use this to search Rockwell
/// documentation or filter telemetry without parsing <see cref="Error"/>.
/// </param>
/// <param name="ControllerAddress">
/// The controller IP address that was being contacted when the comm-layer fault
/// was detected.  The TCP probe confirmed this address answers on port 44818,
/// so the PLC itself is reachable.
/// </param>
/// <param name="Hint">
/// Human-readable instruction to restart <c>LdSdkService</c>.  Addressed to an
/// operator because restarting the service requires elevated privileges and
/// human judgement about whether the process is safe to interrupt.
/// </param>
/// <param name="DocsAnchor">
/// Stable slug referencing the relevant docs section:
/// always <c>"sdk-service-degraded"</c>.
/// </param>
public record ConnectSdkServiceDegradedError(
    [property: JsonPropertyName("error")]              string  Error,
    [property: JsonPropertyName("kind")]               string  Kind,
    [property: JsonPropertyName("sdk_code")]           string  SdkCode,
    [property: JsonPropertyName("controller_address")] string  ControllerAddress,
    [property: JsonPropertyName("hint")]               string  Hint,
    [property: JsonPropertyName("docs_anchor")]        string  DocsAnchor)
{
    /// <summary>Retries made before the connect gave up (each with the SDK restart it saw, if any); omitted when none.</summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }
}
#endif

/// <summary>
/// Structured error returned when a file-opened project (L5X or uncorrelated ACD)
/// cannot be correlated with the controller via the file-based
/// <c>GoOnlineAsync</c> path because the Logix Designer SDK reported an
/// <c>RxE_COMM_*</c> error.
///
/// <para>
/// A project file opened via <c>logix_open_project</c> cannot prove it matches the
/// controller's running program image.  The correct path for an online session is
/// the comm-path/upload route: call <c>logix_connect</c> with
/// <c>sdkMode="online"</c> and a <c>gateway</c> or <c>sdkCommPath</c> without a
/// pre-opened project file.
/// </para>
/// </summary>
/// <param name="Error">Human-readable description of why the correlation failed.</param>
/// <param name="Kind">Machine-readable kind: always <c>"ProjectNotCorrelated"</c>.</param>
/// <param name="Hint">Actionable remediation steps naming the comm-path/upload route.</param>
/// <param name="DocsAnchor">Stable slug referencing the relevant docs section, or <c>null</c>.</param>
public record ConnectProjectNotCorrelatedError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("hint")]        string  Hint,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null);

/// <summary>
/// Structured error returned when <c>backend="sdk"</c>, <c>sdkMode="online"</c>,
/// no comm path is supplied, and a project is open, but
/// <c>allowControllerDownload</c> was absent or <c>false</c>.
///
/// <para>
/// The file-based online route (<c>SdkConnectFactory.CreateAsync</c> against
/// the open project's file) is explicit opt-in: it is taken
/// only when the caller has deliberately set <c>allowControllerDownload=true</c>,
/// because the PROGRAM→download→go_online→RUN sequence is the one thing that
/// route can do that the comm-path/upload route cannot. This holds even when
/// the controller's stored image already happens to match the open project and
/// no download would actually be performed — an online session correlated by a
/// file, rather than by the controller, must never be established silently.
/// </para>
/// <para>
/// The caller must choose one of two remediation paths:
/// <list type="bullet">
///   <item>
///     Non-destructive: call <c>logix_connect</c> again with a
///     <c>gateway</c> or <c>sdkCommPath</c> — supplying a comm path takes the
///     upload-from-controller route (whether or not a project is open) and
///     establishes a non-destructive online session correlated by the
///     controller itself.
///   </item>
///   <item>
///     Destructive: pass <c>allowControllerDownload=true</c> to authorise the
///     PROGRAM→download→go_online→RUN sequence.  See
///     <see cref="DownloadEffect"/> for a plain-language description of what
///     that sequence does.
///   </item>
/// </list>
/// </para>
/// </summary>
/// <param name="Error">
/// Human-readable description of why the online session could not be established.
/// Includes the underlying SDK incompatibility detail.
/// </param>
/// <param name="Kind">
/// Machine-readable kind: always <c>"DownloadRequired"</c>. Present so callers can
/// branch on the error category without parsing <see cref="Error"/>.
/// </param>
/// <param name="DownloadEffect">
/// Plain-language description of what the PROGRAM→download→go_online→RUN sequence
/// does: it stops the running controller and permanently overwrites the program
/// image stored in it with the open project.
/// </param>
/// <param name="HowToOptIn">
/// Actionable instruction for authorising the download: call
/// <c>logix_connect</c> again with <c>allowControllerDownload=true</c>.
/// </param>
public record ConnectDownloadRequiredError(
    [property: JsonPropertyName("error")]          string  Error,
    [property: JsonPropertyName("downloadEffect")] string  DownloadEffect,
    [property: JsonPropertyName("howToOptIn")]     string  HowToOptIn,
    [property: JsonPropertyName("hint")]           string? Hint = null,
    [property: JsonPropertyName("kind")]           string  Kind = "DownloadRequired");

// ── ConnectHandler ────────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_connect</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// When <c>virtual=true</c> the handler constructs a <see cref="VirtualTagService"/>
/// seeded from the open project's controller-tag defaults and wraps it in a
/// <see cref="GatedTagService"/> enforcing the project-derived write-gate policy.
/// </para>
/// <para>
/// When <c>virtual=false</c> and <c>backend="cip"</c> (the default), the handler
/// creates a live <see cref="LibPlcTagService"/> and calls
/// <see cref="ITagService.ConnectAsync"/>.  If a project is currently loaded, the
/// live service is additionally wrapped in a <see cref="GatedTagService"/> so that
/// writes are gate-checked before reaching the PLC.
/// </para>
/// <para>
/// When <c>virtual=false</c> and <c>backend="l5x"</c>, the handler creates a
/// <see cref="L5XOfflineTagService"/> backed by the open L5X project (no network
/// connection is made) and wraps it in a <see cref="GatedTagService"/>.  Requires
/// a project to be loaded.
/// </para>
/// <para>
/// When <c>virtual=false</c> and <c>backend="sdk"</c>, the handler delegates to
/// <see cref="ConnectHandler.DefaultSdkFactory"/> (or a per-call injected factory) to
/// create an SDK-backed <see cref="ITagService"/> and wraps it in a
/// <see cref="GatedTagService"/>.  Requires the <c>LogixMcp.SdkAdapter</c>
/// assembly to be present.
/// <b>Comm path wins:</b> when <c>sdkMode="online"</c> and a comm
/// path is supplied (<c>sdkCommPath</c>, <c>gateway</c>, or <c>gateway</c>+
/// <c>path</c>), the upload-from-controller branch is always taken —
/// regardless of whether a project is already loaded via
/// <c>logix_open_project</c>.  A pre-loaded project is never required for this
/// branch, and an already-open project is left untouched (it keeps serving
/// static tools); the response's <c>gateSource</c> field names the uploaded
/// project as the source of the write-gate policy for this session.  Only
/// when <c>sdkMode="online"</c> with no comm path at all — or
/// <c>sdkMode="offline"</c> — does the handler fall back to the file-backed
/// path against <c>projectService.FilePath</c>, which requires a project to
/// be loaded.  For <c>sdkMode="online"</c>, the file-backed path is
/// additionally explicit opt-in: it is only taken when
/// <c>allowControllerDownload=true</c> is also supplied — without it, the
/// handler returns <see cref="ConnectDownloadRequiredError"/> instead of
/// attempting the file-based connection, even if no download would actually
/// turn out to be necessary.
/// </para>
/// <para>
/// Any other <c>backend</c> value causes the handler to return a
/// <see cref="ConnectBadBackendError"/> that lists the valid options.  Silent
/// fallback is not permitted.
/// </para>
/// </summary>
public static class ConnectHandler
{
    /// <summary>
    /// The capability table to report for an SDK session: the session's own table, or one built from the
    /// version the factory reported when it supplied none.
    /// </summary>
    private static SdkCapabilitiesReport CapabilitiesOf(SdkSessionResult result) =>
        (result.Capabilities
            ?? SdkCapabilities.ForSession(result.DesignerVersion, DoctorEnvironment.SdkObservations?.AssemblyVersion))
        .Report();

    /// <summary>
    /// Records the target's revision on the session and returns <paramref name="result"/> carrying
    /// <c>targetMajorRevision</c> and <c>mnemonicSet</c>, with the unknown-revision warning appended to
    /// <c>warnings</c>. A target with no readable revision (for example a CIP connect with no project open)
    /// is reported as <c>unknown</c>, never silently as legacy or IEC.
    /// </summary>
    internal static ConnectResult WithTargetRevision(
        ConnectResult result, TargetRevisionInfo? info, TagSessionService sessionService)
    {
        if (info is null || ReferenceEquals(info, TargetRevision.NoTarget))
            info = TargetRevision.UnknownTarget();
        sessionService.SetTargetRevision(info);
        var warnings = info.Warning is null ? result.Warnings : [.. result.Warnings ?? [], info.Warning];
        return result with
        {
            TargetMajorRevision = info.MajorRevision,
            MnemonicSet         = info.MnemonicSet,
            Warnings            = warnings,
        };
    }

    /// <summary>The exhaustive set of recognised <c>backend</c> values advertised to callers.</summary>
    /// <remarks>
    /// <c>"virtual"</c> is an alias of <c>useVirtual=true</c>. <c>"sdk"</c> is listed only
    /// in SDK builds (<c>LOGIX_SDK</c>); a no-SDK build advertises just <c>cip</c>, <c>l5x</c>
    /// and <c>virtual</c> (an explicit <c>backend="sdk"</c> still yields the structured
    /// SDK-not-available error).
    /// </remarks>
    public static readonly string[] ValidBackends =
#if LOGIX_SDK
        ["cip", "sdk", "l5x", "virtual"];
#else
        ["cip", "l5x", "virtual"];
#endif

    /// <summary>
    /// The exhaustive set of parameter names that <c>logix_connect</c> accepts.
    /// Used by <see cref="CheckForUnknownParameters"/> to detect caller typos such as
    /// <c>mode</c> instead of <c>sdkMode</c> before any connection attempt is made.
    /// </summary>
    public static readonly string[] ValidParameters =
#if LOGIX_SDK
        ["gateway", "path", "useVirtual", "backend", "sdkMode", "allowControllerDownload",
         "sdkCommPath", "confirmedByUser"];
#else
        ["gateway", "path", "useVirtual", "backend", "confirmedByUser"];
#endif

    /// <summary>
    /// Returns a description of the first character in <paramref name="value"/> that could
    /// alter a libplctag attribute string ('&amp;', '=', control or whitespace characters),
    /// or any non-ASCII character (e.g. full-width '＆' / '＝' look-alikes), or null when the value is clean. A plain ASCII space is tolerated only when
    /// <paramref name="allowSpace"/> is true (the separator CIP path syntax allows).
    /// </summary>
    private static string? FindInjectionChar(string value, bool allowSpace)
    {
        foreach (var c in value)
        {
            if (c == ' ' && allowSpace) continue;
            if (c == '&' || c == '=' || c > 0x7F || char.IsControl(c) || char.IsWhiteSpace(c))
                return c == '&' || c == '=' ? $"'{c}'" : $"U+{(int)c:X4}";
        }
        return null;
    }

    /// <summary>Builds the InvalidArgument error for a gateway/path rejected by <see cref="FindInjectionChar"/>.</summary>
    private static ConnectMissingArgumentError BuildInvalidAddressArgument(string parameter, string offender) =>
        new(
            Error: $"Invalid {parameter}: contains disallowed character {offender}. " +
                   "'&', '=', control characters, whitespace and non-ASCII characters are not permitted.",
            Kind: "InvalidArgument",
            Parameter: parameter,
            Hint: parameter == "gateway"
                ? "Supply gateway as a plain IP address or hostname (e.g. '198.51.100.10') with no spaces, '&' or '='."
                : "Supply path as a CIP route such as '1,0' (only single spaces between segments are allowed; no '&' or '=').");

    /// <summary>Hint appended to the missing-gateway error: which backends need no gateway.</summary>
#if LOGIX_SDK
    private const string NoGatewayAlternatives =
        "To connect without a live network gateway, use backend='sdk', " +
        "backend='l5x', or useVirtual=true instead.";
#else
    private const string NoGatewayAlternatives =
        "To connect without a live network gateway, use backend='l5x', " +
        "backend='virtual', or useVirtual=true instead.";

    /// <summary>Hint returned with the no-SDK refusal: the backends this build offers.</summary>
    private const string NoSdkBackendHint =
        "Use backend='cip' with gateway and path for a live controller, or " +
        "backend='l5x' / 'virtual' with logix_open_project.";
#endif

    /// <summary>
    /// Checks <paramref name="argumentKeys"/> for any key that is not in
    /// <see cref="ValidParameters"/> (case-insensitive).
    /// </summary>
    /// <param name="argumentKeys">
    /// The raw argument keys supplied by the caller in the MCP tool invocation.
    /// Typically sourced from <c>CallToolRequestParams.Arguments.Keys</c>.
    /// </param>
    /// <returns>
    /// A <see cref="ConnectUnknownParameterError"/> describing every unrecognised key
    /// when at least one is found; <c>null</c> when all keys are valid.
    /// </returns>
    public static ConnectUnknownParameterError? CheckForUnknownParameters(
        IEnumerable<string> argumentKeys)
    {
        var validSet = new HashSet<string>(ValidParameters, StringComparer.OrdinalIgnoreCase);
        var unknown  = argumentKeys.Where(k => !validSet.Contains(k)).ToList();

        if (unknown.Count == 0)
            return null;

        return new ConnectUnknownParameterError(
            Error: $"Unknown parameter{(unknown.Count == 1 ? "" : "s")}: " +
                   $"{string.Join(", ", unknown.Select(p => $"'{p}'"))}. " +
                   $"Valid parameters are: {string.Join(", ", ValidParameters)}.",
            UnknownParameters: unknown.ToArray(),
            ValidParameters:   ValidParameters);
    }

    /// <summary>
    /// The default SDK-backend factory used when no per-call factory is supplied.
    ///
    /// <para>
    /// Set by an explicit call to <c>SdkAdapterInitializer.Initialize()</c> in
    /// <c>LogixMcp.SdkAdapter</c> after the adapter assembly is loaded.
    /// <c>SdkAdapterLoader.TryLoad</c> performs this call reflectively to ensure
    /// registration is reliable regardless of runtime module-initializer support.
    /// The <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> on
    /// <c>Initialize</c> provides a secondary guarantee for hosts that happen to execute
    /// adapter code directly, but is not the primary registration mechanism.
    /// When <c>null</c> (the adapter is absent or <c>Initialize</c> was never called),
    /// the <c>sdk</c> backend returns <see cref="ConnectSdkNotAvailableError"/>.
    /// </para>
    /// </summary>
    public static ISdkTagServiceFactory? DefaultSdkFactory { get; set; }

    /// <summary>
    /// Removes stale offline build-check copies and returns how many it removed. Registered by the SDK adapter;
    /// <c>null</c> when the adapter is not loaded. Run once per connect; a failure is ignored.
    /// </summary>
    public static Func<int>? StaleOfflineCopyCleaner { get; set; }

    /// <summary>
    /// Removes <c>ctl-*</c> upload slot folders whose owner process is gone and returns how many it removed.
    /// Registered by the SDK adapter; <c>null</c> when the adapter is not loaded. Run once per connect; a failure is ignored.
    /// </summary>
    public static Func<int>? StaleUploadSlotCleaner { get; set; }

    /// <summary>
    /// Builds the connect warning for staged rungs the outgoing session holds, or <c>null</c> when there is
    /// nothing to discard (no SDK session, a faulted one whose work is already lost, or an empty buffer).
    /// Never throws.
    /// </summary>
    /// <param name="inspector">The SDK session inspector seam, or <c>null</c> in a core-only build.</param>
    /// <param name="sessionService">The tag-session singleton about to be replaced.</param>
    /// <returns>The warning text, or <c>null</c>.</returns>
    internal static string? DiscardedStagedBufferWarning(
        Func<TagSessionService, SdkSessionCloseInfo?>? inspector, TagSessionService sessionService)
    {
        try
        {
            if (inspector?.Invoke(sessionService) is not { Faulted: false, StagedRungCount: > 0 } info)
                return null;
            var routines = info.StagedRoutines is { Count: > 0 }
                ? string.Join(", ", info.StagedRoutines)
                : "the staged routines";
            var one = info.StagedRungCount == 1;
            return $"{info.StagedRungCount} staged {(one ? "rung" : "rungs")} in {routines} " +
                   $"{(one ? "was" : "were")} discarded by the new session; re-stage them";
        }
        catch
        {
            return null;   // best effort: the connect must not fail on an inspection error
        }
    }

    /// <summary>
    /// Finds <c>rung_edit</c> <c>"sending"</c> intent lines the audit log never saw completed, and for each one whose
    /// routine now has a pending zone holding one of the rung texts the line recorded writes an <c>"orphaned"</c>
    /// line (same correlation id) and builds the connect warning. A line whose recorded rung texts are not in the
    /// routine's pending zone is stale (a later publish owns the zone): it gets an <c>"abandoned"</c> line and no
    /// warning. A line that recorded no texts matches on the routine alone. Lines whose routine has no pending zone
    /// are left alone (the zone read may have failed, or the import never landed). Without an injected reader it only runs for an online SDK connect. Never throws.
    /// </summary>
    /// <param name="connected">The successful connect result.</param>
    /// <param name="sessionService">The tag-session singleton holding the new session.</param>
    /// <param name="pendingZoneReader">Zone read seam; <c>null</c> uses the loaded SDK adapter's observations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The warning text, or <c>null</c> when nothing was orphaned.</returns>
    internal static async Task<string?> OrphanedRungEditWarningAsync(
        ConnectResult connected,
        TagSessionService sessionService,
        Func<TagSessionService, CancellationToken, Task<IReadOnlyList<DoctorZone>>>? pendingZoneReader,
        CancellationToken ct)
    {
        try
        {
            // The default zone read needs an online SDK session; an injected reader decides for itself.
            if (pendingZoneReader is null
                && (!connected.Online || !string.Equals(connected.Backend, "sdk", StringComparison.OrdinalIgnoreCase)))
                return null;
            var unfinished = AuditLogger.FindUnfinishedRungEdits();
            if (unfinished.Count == 0)
                return null;

            pendingZoneReader ??= DoctorEnvironment.SdkObservations is { } obs ? obs.ReadPendingZonesAsync : null;
            if (pendingZoneReader is null)
                return null;
            var zones = await pendingZoneReader(sessionService, ct).ConfigureAwait(false);
            var open = zones
                .Where(z => !string.Equals(z.ZoneState, "none", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var orphaned = new List<string>();
            foreach (var intent in unfinished)
            {
                if (!RoutineRungSelector.TryParseRoutineXPath(intent.Routine, out var program, out var routine))
                    continue;
                var zone = open.FirstOrDefault(z => string.Equals(z.Program, program, StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals(z.Routine, routine, StringComparison.OrdinalIgnoreCase));
                if (zone is null)
                    continue;
                // The zone must hold a rung this import sent; otherwise it belongs to a later edit.
                if (intent.RungTexts is { Count: > 0 } sent && zone.PendingTexts is { } zoneTexts
                    && !zoneTexts.Any(t => RungTextComparer.AnyExpectedMatches(sent, t)))
                {
                    AuditLogger.LogRungEditAbandoned(intent);
                    continue;
                }
                AuditLogger.LogRungEditOrphaned(intent);
                orphaned.Add($"{program}/{routine} at position {intent.InsertPosition}" +
                             (intent.Timestamp is null ? "" : $" ({intent.Timestamp})") +
                             $" [correlation {intent.Correlation}]");
            }
            return orphaned.Count == 0
                ? null
                : $"{orphaned.Count} rung edit(s) were sent to the controller but the server stopped before " +
                  $"recording their outcome (audit lines marked 'orphaned'): {string.Join("; ", orphaned)}. " +
                  "The routine has a pending edit zone; check logix_pending_edits before editing it again.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // best effort: the connect must not fail on an audit or zone-read error
        }
    }

#if LOGIX_SDK
    /// <summary>Hint on a <c>CommPathInvalid</c> error. Never tells the caller to use <c>sdkCommPath</c>: it was used.</summary>
    internal const string CommPathInvalidHint =
        "check the comm path (slot and port) and that the controller is reachable; the SDK could not route it";

    /// <summary>Warning added to a connect result when <c>path</c> is passed with <c>backend="sdk"</c>.</summary>
    internal const string PathUnusedBySdkWarning =
        "path is a CIP backplane path and is not used by the sdk backend; the SDK uses sdkCommPath";
#endif

    /// <summary>
    /// Disposes the active SDK session, faulted or healthy, through the same seam
    /// <c>logix_close_project</c> uses, so the connect that follows never leaves it open in
    /// LdSdkServer for the keep-alive reaper. An absent session is left alone. Never throws.
    /// </summary>
    /// <param name="sessionService">The tag-session singleton.</param>
    /// <returns><c>true</c> when a session was disposed.</returns>
    internal static bool DisposeOutgoingSdkSession(TagSessionService sessionService, ProjectService? projectService = null)
    {
        try
        {
            if (CloseProjectHandler.SdkSessionInspector?.Invoke(sessionService) is null)
                return false;
            CloseProjectHandler.SdkSessionDisposer?.Invoke(sessionService);
            projectService?.NoteSessionEnded();   // readers' NoProjectLoaded hint names the displaced session
            ClearUploadRegistration(projectService);
            return true;
        }
        catch
        {
            return false;   // best effort: the connect that follows must still run
        }
    }

    /// <summary>
    /// Drops the project an upload registered once its session is gone. Disposing an upload-backed session deletes its
    /// ACD, so a registration left behind would point at a file that no longer exists. A project opened with
    /// <c>logix_open_project</c> is never touched. Uses the same <see cref="ProjectService.Clear"/> primitive as
    /// <c>logix_close_project</c>; the ACD is not kept.
    /// </summary>
    /// <param name="projectService">The project singleton, or <c>null</c> to do nothing.</param>
    private static void ClearUploadRegistration(ProjectService? projectService)
    {
        if (projectService is { IsLoaded: true, OpenedExplicitly: false })
            projectService.Clear();
    }

    /// <summary>
    /// Establishes a tag-service session and attaches it to
    /// <paramref name="sessionService"/>.
    /// </summary>
    /// <param name="gateway">
    /// EtherNet/IP gateway address (e.g. <c>"198.51.100.10"</c>).  Used only for
    /// the <c>"cip"</c> backend; ignored for virtual, SDK, and L5X sessions.
    /// The <see cref="ConnectResult.GatewayUsed"/> field in the response
    /// indicates whether this value was actually used for a network connection.
    /// </param>
    /// <param name="path">
    /// CIP backplane path (e.g. <c>"1,0"</c>).  Used only for the <c>"cip"</c>
    /// backend; ignored for virtual, SDK, and L5X sessions.
    /// </param>
    /// <param name="sdkCommPath">
    /// Logix Designer SDK communications path to the controller
    /// (e.g. <c>"EmulateEthernet\192.0.2.10"</c> for an emulated controller,
    /// or <c>"192.0.2.10"</c> for a real controller).
    /// Used only when <paramref name="backend"/> is <c>"sdk"</c> and
    /// <paramref name="sdkMode"/> is <c>"online"</c>.
    /// <para>
    /// <b>Comm path wins:</b> supplying this (or <paramref name="gateway"/>)
    /// always routes to the upload-from-controller branch, regardless of
    /// whether a project is currently loaded via <c>logix_open_project</c> —
    /// see <see cref="ExecuteAsync"/> remarks. An already-open project is
    /// never required and is never disturbed by this parameter's presence.
    /// </para>
    /// This parameter uses the Logix Designer comm path format (backslash-delimited
    /// protocol prefix), which is different from the CIP <paramref name="gateway"/>
    /// and <paramref name="path"/> parameters used by libplctag.
    /// When <c>null</c> or empty, falls back to building the comm path from
    /// <paramref name="gateway"/> and <paramref name="path"/>.
    /// </param>
    /// <param name="useVirtual">
    /// When <c>true</c>, constructs an in-memory session seeded from the open
    /// project and wrapped in a write-gate policy.  Requires a project to be loaded.
    /// When <c>false</c>, the backend selection is governed by
    /// <paramref name="backend"/>.
    /// </param>
    /// <param name="backend">
    /// Tag-service backend to use when <paramref name="useVirtual"/> is
    /// <c>false</c>.  Must be one of <c>"cip"</c> (default), <c>"sdk"</c>, or
    /// <c>"l5x"</c> (case-insensitive).  Any other value causes
    /// <see cref="ExecuteAsync"/> to return a <see cref="ConnectBadBackendError"/>
    /// that lists the valid options.  Silent fallback to a default backend is not
    /// permitted.
    /// <list type="bullet">
    ///   <item><c>"cip"</c> — creates a live <see cref="LibPlcTagService"/>.</item>
    ///   <item><c>"sdk"</c> — creates a <see cref="TypedSdkTagService"/> backed by
    ///     a <see cref="LogixProjectSession"/> opened against the loaded project
    ///     file via <see cref="DefaultSdkFactory"/> or the per-call
    ///     <paramref name="sdkFactory"/> (requires a project to be loaded and the
    ///     adapter assembly to be present).</item>
    ///   <item><c>"l5x"</c> — creates an offline <see cref="L5XOfflineTagService"/>
    ///     backed by the open L5X project (requires a project to be loaded).</item>
    /// </list>
    /// </param>
    /// <param name="projectService">Provides the currently open L5X project.</param>
    /// <param name="sessionService">Receives the newly created session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="modeService">Optional controller-mode tracking service.</param>
    /// <param name="sdkFactory">
    /// Optional per-call <see cref="ISdkTagServiceFactory"/> for injection in unit
    /// tests.  When <c>null</c> (the default), <see cref="DefaultSdkFactory"/> is
    /// used.  Only relevant when <paramref name="backend"/> is <c>"sdk"</c>;
    /// ignored for all other backends.
    /// </param>
    /// <param name="sdkMode">
    /// The SDK operation mode when <paramref name="backend"/> is <c>"sdk"</c>.
    /// <c>"online"</c> targets a live controller via the SDK gRPC channel (reads and
    /// writes reach the physical PLC); <c>"offline"</c> (default) operates against the
    /// project file only (no physical PLC is targeted).  Case-insensitive.
    /// Ignored for all other backends.
    /// </param>
    /// <param name="allowControllerDownload">
    /// When <c>true</c>, and <paramref name="backend"/> is <c>"sdk"</c> and
    /// <paramref name="sdkMode"/> is <c>"online"</c>, executes the
    /// PROGRAM→download→go_online→RUN sequence before establishing the session:
    /// the controller is transitioned to PROGRAM mode (halting program scan), the
    /// open project is downloaded to the controller (overwriting the stored program
    /// image), <c>GoOnlineAsync</c> is called, and the controller is transitioned
    /// back to RUN mode.
    /// <para>
    /// <b>WARNING:</b> This sequence stops the running controller and overwrites
    /// the program stored in it.  Only pass <c>true</c> when you have confirmed
    /// it is safe to interrupt the running process.
    /// </para>
    /// Ignored for all backends other than <c>"sdk"</c> with <c>sdkMode="online"</c>.
    /// Returns a <see cref="ConnectError"/> when <c>true</c> is passed with an
    /// incompatible backend or mode combination.
    /// Defaults to <c>false</c>.
    /// </param>
    /// <param name="controllerReachableProbe">
    /// Optional injectable TCP reachability probe used by the
    /// route-error discriminator: <c>(ip, ct) -&gt; Task&lt;bool&gt;</c>, returning
    /// <c>true</c> when a TCP handshake to <c>&lt;ip&gt;:44818</c> succeeds.
    /// When <c>null</c> (the default), <see cref="DefaultControllerReachableProbe"/>
    /// is used, which performs a real TCP connect with a 2-second timeout.
    /// Injectable so unit tests never touch the network.
    /// </param>
    /// <param name="cipConnectProbe">
    /// Optional injectable post-connect probe for <c>backend="cip"</c>:
    /// <c>(service, ct) -&gt; Task</c>, which must throw when the controller cannot be
    /// reached with the supplied gateway/path. When <c>null</c> (the default) the
    /// probe lists the controller's <c>@tags</c> via
    /// <see cref="ITagService.ListTagsAsync"/>. A probe failure yields a
    /// <see cref="ConnectCipProbeError"/> (<c>CipPathInvalid</c> when
    /// <c>path</c> was empty, otherwise <c>ConnectionFailed</c>). Injectable so unit
    /// tests never touch the network.
    /// </param>
    /// <param name="sdkConnectRetry">
    /// Optional retry policy for the SDK upload connect (service-state query and delay are injectable).
    /// When <c>null</c> (the default), <see cref="SdkConnectRetryPolicy.Default"/> is used: up to two retries
    /// for the three dead-service messages, after waiting for <c>LdSdkService</c> to be Running (30 s at most)
    /// and backing off 10 s then 20 s, except that a retry starts at once when the SDK server restarted and is back.
    /// Retries made are reported as <c>retries</c>; a restart seen is recorded as <c>sdkRestart</c>.
    /// </param>
    /// <param name="progress">
    /// Optional receiver for <see cref="ConnectProgress"/> phase reports (upload, save L5X,
    /// parse, gate derivation) on the SDK upload-from-controller route; <c>null</c> (the
    /// default) reports nothing and changes no behaviour or result.
    /// </param>
    /// <param name="confirmedByUser">
    /// Must be <c>true</c> (in addition to <c>LGX_ENABLE_LIVE_WRITES=1</c> and
    /// <c>LGX_ENABLE_CONTROLLER_DOWNLOAD=1</c>) for <paramref name="allowControllerDownload"/>
    /// to take effect; set it only after the user has explicitly approved overwriting the
    /// controller's program in this conversation.  Ignored when
    /// <paramref name="allowControllerDownload"/> is <c>false</c>.  Defaults to <c>false</c>.
    /// </param>
    /// <returns>
    /// A <see cref="ConnectResult"/> on success, a <see cref="ConnectError"/> on a
    /// recoverable failure, or a <see cref="ConnectBadBackendError"/> when
    /// <paramref name="backend"/> is not a recognised value.  Never throws.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string                 gateway,
        string                 path,
        bool                   useVirtual,
        ProjectService         projectService,
        TagSessionService      sessionService,
        string?                backend                = "cip",
        CancellationToken      ct                     = default,
        ControllerModeService? modeService            = null,
        ISdkTagServiceFactory? sdkFactory             = null,
        string                 sdkMode                = "offline",
        bool                   allowControllerDownload = false,
        string?                sdkCommPath            = null,
        Func<string, CancellationToken, Task<bool>>? controllerReachableProbe = null,
        Capabilities?          capabilities           = null,
        Func<ITagService, CancellationToken, System.Threading.Tasks.Task>? cipConnectProbe = null,
        IProgress<ConnectProgress>? progress = null,
        TimeSpan?              cipProbeDeadline       = null,
        bool                   confirmedByUser        = false,
        Func<string, string, CancellationToken, Task<ITagService>>? cipRouteConnector = null,
        Func<string, CancellationToken, Task<ControllerIdentity>>? cipIdentityReader = null,
        SdkConnectRetryPolicy? sdkConnectRetry = null,
        Func<TagSessionService, CancellationToken, Task<IReadOnlyList<DoctorZone>>>? pendingZoneReader = null,
        ConnectState? connectState = null)
    {
        // ── Own connect visible at once ──────────────────────
        // Mark this instance's connect as started before any pre-work, so a status poll answers
        // connecting/starting and never falls through to the registry's history of other instances.
        using var ownConnect = connectState?.BeginOwnConnect(string.IsNullOrWhiteSpace(sdkCommPath) ? path : sdkCommPath);

        // ── Command prefixes (LGX_GATE_COMMAND_PREFIXES) ──────────────────────
        // Resolved once, before any connection work. An invalid entry fails the connect with
        // kind 'GateConfigInvalid'; entries are never silently dropped.
        if (GateConfigResolver.FromEnvironment() is GateConfigInvalidError badConfig) return badConfig;
        var gateConfig = GateConfigResolver.Current;

        // ── Optional write allowlist (LGX_WRITE_ALLOWLIST) ────────────────────
        // Validated before any connection work. Fail closed: an unreadable file or
        // invalid entry fails the connect with kind 'AllowlistInvalid'. When unset,
        // behaviour is unchanged.
        WriteAllowlist? allowlist = null;
        if (capabilities?.WriteAllowlistPath is { } allowlistPath)
        {
            var loaded = WriteAllowlist.Load(allowlistPath);
            if (loaded is AllowlistInvalidError invalid) return invalid;
            allowlist = (WriteAllowlist)loaded;
        }

        // ── Staged work the new session would discard ──
        // A connect replaces the active session, and a session's staged-rung buffer lives and dies with it.
        // Read what the outgoing session holds now (a faulted session's work is already lost), so a successful
        // connect can say so instead of dropping the rungs silently.
        var discardedWarning = DiscardedStagedBufferWarning(
            CloseProjectHandler.SdkSessionInspector, sessionService);

        // ── Outgoing-session cleanup ──────────────────────────
        // A connect always opens a fresh SDK session and never reuses the outgoing one, so close the
        // outgoing one (faulted or healthy) before connecting. An abandoned project stays open in
        // LdSdkServer until its keep-alive reaper disposes it, and that reaper crashes the whole service
        // (IOException deleting the project's temp .acd in ReceivedFile.Dispose) — the in-connect SDK
        // crashes.
        // A live session is replaced by this connect (a new upload, ~19 s): name the connect that made it.
        var stateForSession = connectState ?? sessionService.ConnectState;
        var replacedSession = sessionService.IsConnected ? stateForSession?.LiveSession : null;

        DisposeOutgoingSdkSession(sessionService, projectService);

        // ── Zone ledger: a connect starts from an empty record of the zones this server published. ──
        ZoneLedger.ClearAll();

        var result = await ExecuteCoreAsync(
            gateway, path, useVirtual, projectService, sessionService, backend, ct,
            modeService, sdkFactory, sdkMode, allowControllerDownload, sdkCommPath,
            controllerReachableProbe, capabilities, cipConnectProbe, allowlist?.Sha256, progress,
            cipProbeDeadline, confirmedByUser, cipRouteConnector, cipIdentityReader, sdkConnectRetry);

        // Activate the allowlist for the session just attached (Attach cleared any previous one).
        if (result is ConnectResult) sessionService.SetWriteAllowlist(allowlist);

        if (result is not ConnectResult connected) return result;

        var configWarnings = gateConfig.Warnings.Count > 0 ? gateConfig.Warnings.ToArray() : null;
        // Keep the warnings the core connect already raised (an unknown target revision) beside the config's.
        var warnings = configWarnings is null ? connected.Warnings
            : [.. connected.Warnings ?? [], .. configWarnings];
        if (discardedWarning is not null)
            warnings = [.. warnings ?? [], discardedWarning];
        // `path` is the CIP backplane path; only the CIP probe reads it. Under the sdk backend the SDK uses
        // commPath, so say the argument had no effect rather than silently ignoring it. No refusal.
#if LOGIX_SDK
        if (string.Equals(backend?.Trim(), "sdk", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(path))
            warnings = [.. warnings ?? [], PathUnusedBySdkWarning];
#endif

        // ── Rung edits the previous server never finished ──
        // A "sending" rung_edit intent line with no outcome line means the server stopped mid-import. When the
        // routine now has a pending zone, the import reached the controller: record an "orphaned" line and warn.
        var orphanedWarning = await OrphanedRungEditWarningAsync(
            connected, sessionService, pendingZoneReader, ct).ConfigureAwait(false);
        if (orphanedWarning is not null)
            warnings = [.. warnings ?? [], orphanedWarning];
        int? staleCopiesRemoved = null;
        try { staleCopiesRemoved = StaleOfflineCopyCleaner?.Invoke(); }
        catch { /* best effort: a cleanup failure never fails a connect */ }
        int? staleSlotsRemoved = null;
        try { staleSlotsRemoved = StaleUploadSlotCleaner?.Invoke(); }
        catch { /* best effort: a cleanup failure never fails a connect */ }
        var unparsed = (sessionService.Service as GatedTagService)?.Policy.Unparsed;
        var replacedNote = replacedSession is null ? null
            : $"This connect replaced the live session from connect {replacedSession.ConnectId} " +
              $"(connected {replacedSession.ConnectedAt.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}); that session was closed.";
        connected = connected with
        {
            StaleOfflineCopiesRemoved = staleCopiesRemoved,
            StaleUploadSlotsRemoved   = staleSlotsRemoved,
            ReplacedSession       = replacedSession,
            CommandPrefixes       = gateConfig.CommandPrefixesWire,
            CommandPrefixesSource = gateConfig.CommandPrefixesSource,
            UnparsedContentCount  = unparsed?.Count,
            UnparsedContent       = unparsed is { Count: > 0 }
                ? unparsed.Take(UnparsedContent.MaxReported).ToList()
                : null,
            UnparsedRungs         = unparsed is { Count: > 0 } ? UnparsedContent.RungsOf(unparsed) : null,
            Warnings              = warnings,
            Message               = (replacedNote is null ? connected.Message : connected.Message + " " + replacedNote)
                + (configWarnings is null && discardedWarning is null && orphanedWarning is null
                ? ""
                : " Warning: " +
                  string.Join(" ", (configWarnings ?? [])
                      .Concat(discardedWarning is null ? [] : [discardedWarning])
                      .Concat(orphanedWarning is null ? [] : [orphanedWarning]))),
        };
        stateForSession?.NoteSessionLive(
            stateForSession.Current?.ConnectId ?? Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        return allowlist is not null
            ? connected with { AllowlistSha256 = allowlist.Sha256, AllowlistEntries = allowlist.EntryCount }
            : connected;
    }

    private static async Task<object> ExecuteCoreAsync(
        string                 gateway,
        string                 path,
        bool                   useVirtual,
        ProjectService         projectService,
        TagSessionService      sessionService,
        string?                backend                = "cip",
        CancellationToken      ct                     = default,
        ControllerModeService? modeService            = null,
        ISdkTagServiceFactory? sdkFactory             = null,
        string                 sdkMode                = "offline",
        bool                   allowControllerDownload = false,
        string?                sdkCommPath            = null,
        Func<string, CancellationToken, Task<bool>>? controllerReachableProbe = null,
        Capabilities?          capabilities           = null,
        Func<ITagService, CancellationToken, System.Threading.Tasks.Task>? cipConnectProbe = null,
        string?                allowlistSha256        = null,
        IProgress<ConnectProgress>? progress          = null,
        TimeSpan?              cipProbeDeadline       = null,
        bool                   confirmedByUser        = false,
        Func<string, string, CancellationToken, Task<ITagService>>? cipRouteConnector = null,
        Func<string, CancellationToken, Task<ControllerIdentity>>? cipIdentityReader = null,
        SdkConnectRetryPolicy? sdkConnectRetry = null)
    {
        // backend="virtual" is an alias of useVirtual=true.
        if (!useVirtual && string.Equals(backend?.Trim(), "virtual", StringComparison.OrdinalIgnoreCase))
            useVirtual = true;

        if (useVirtual)
        {
            // ── Virtual mode ──────────────────────────────────────────────────
            // Requires an open project to seed the in-memory tag store.
            if (!projectService.IsLoaded)
                return new ConnectError(
                    "No project loaded. Call logix_open_project first to use virtual mode.")
                {
                    Kind = ErrorKinds.NoProjectLoaded,
                    Hint =
                        "Call logix_open_project with a path to a project file, then call logix_connect with " +
                        "useVirtual=true again.",
                };

            var project = projectService.Project!;

            // Build seed dictionary from the project's controller-scoped tags.
            var seed = BuildSeedFromProject(project);

            var virtualSvc = new VirtualTagService(seed);
            await virtualSvc.ConnectAsync(gateway, path, ct);   // no-op

            // Derive write-gate policy from the same project; a partial export derives none.
            var vGate = BuildFileGate(virtualSvc, projectService, allowlistSha256);

            sessionService.Attach(vGate.Service, isVirtual: true, isLiveController: false);
            ApplyNoGate(sessionService, vGate);

            // Virtual sessions start in Program mode (emulated, safe to operate).
            modeService?.Reset("Program");

            var virtualResult = new ConnectResult(
                Status:             "connected",
                Gateway:            null,
                Path:               null,
                GatewayUsed:        false,
                Virtual:            true,
                Backend:            "virtual",
                TagCount:           seed.Count,
                Gated:              vGate.Gated,
                Target:             "offline-project",
                Online:             false,
                DownloadPerformed:  null,
                ModeTransitions:    null,
                Correlation:        null,
                Message:            $"Connected in virtual (offline) mode. Writes reach the in-memory " +
                                    $"store only — no physical PLC is targeted. " +
                                    $"Seeded {seed.Count} controller tag(s) from project." +
                                    (vGate.Note ?? " Writes are gate-checked against the derived policy."),
                GateSource:         vGate.GateSourceOverride);
            virtualResult = WithTargetRevision(virtualResult, projectService.TargetRevision, sessionService);
            AuditLogger.LogConnect(
                virtualResult.Backend, virtualResult.Target,
                virtualResult.Correlation, virtualResult.DownloadPerformed,
                gated: vGate.Gated,
                allowlistSha256: allowlistSha256,
                gateSource: FileGateAuditSource(vGate, projectService, allowlistSha256, out var virtualGateSha),
                gateSourceSha256: virtualGateSha);
            return virtualResult;
        }

        // ── Backend normalization ─────────────────────────────────────────────
        // A null, empty or whitespace backend means "not specified" and takes the
        // default (cip). Accepted values are case-insensitive; "virtual" was handled above.
        var normalizedBackend = string.IsNullOrWhiteSpace(backend)
            ? "cip"
            : backend.Trim().ToLowerInvariant();

#if !LOGIX_SDK
        // ── No-SDK build: the sdk backend is refused at dispatch ──────────────
        // Nothing below this point in a no-SDK build names or implements it.
        if (normalizedBackend == "sdk")
        {
            return new CapabilityDisabledError(
                Error: "The SDK backend is not part of this build. Use cip, l5x or virtual.",
                Hint:  NoSdkBackendHint);
        }

        // The tool schema of this build does not carry the SDK-only options. A direct
        // caller that still supplies one fails closed instead of being silently ignored.
        if (allowControllerDownload ||
            string.Equals(sdkMode?.Trim(), "online", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(sdkCommPath))
        {
            return new CapabilityDisabledError(
                Error: "That connect option is not part of this build. Use cip, l5x or virtual.",
                Hint:  NoSdkBackendHint);
        }
#endif

#if LOGIX_SDK
        // ── Live-write capability gate ────────────────────────────────────────
        // When LGX_ENABLE_LIVE_WRITES is not set, allowControllerDownload=true is
        // refused before any network or SDK call.  This is the most destructive
        // operation the connect handler can perform — it stops the running
        // controller, overwrites its stored program, and disconnects all online
        // sessions — so it requires an explicit opt-in beyond the existing flag.
        if (capabilities is not { LiveWritesEnabled: true } && allowControllerDownload)
        {
            AuditLogger.LogCapabilityRefusal("logix_connect", Capabilities.EnvVar);
            return new CapabilityDisabledError(
                Error:
                    "allowControllerDownload=true requires LGX_ENABLE_LIVE_WRITES=1. " +
                    "The PROGRAM→download→go_online→RUN sequence is a live controller " +
                    "mutation that stops the running process and overwrites the stored " +
                    "program image. Set LGX_ENABLE_LIVE_WRITES=1 to enable this operation.",
                Hint:
                    $"Set the {Capabilities.EnvVar} environment variable to 1 to enable " +
                    $"live controller mutations. " +
                    $"Client command: {Capabilities.ClientCommandHint}");
        }

        // ── Controller-download capability gate ───────────────────────────────
        // allowControllerDownload=true additionally requires its own opt-in flag
        // (LGX_ENABLE_CONTROLLER_DOWNLOAD=1) — separate from live writes because the
        // download overwrites the controller's stored program — and an explicit
        // per-call confirmedByUser=true. Both are refused before any network or SDK call.
        if (allowControllerDownload)
        {
            if (capabilities is not { ControllerDownloadEnabled: true })
            {
                AuditLogger.LogCapabilityRefusal("logix_connect", Capabilities.EnvVarControllerDownload);
                return new CapabilityDisabledError(
                    Error:
                        "allowControllerDownload=true requires LGX_ENABLE_CONTROLLER_DOWNLOAD=1 " +
                        "(in addition to LGX_ENABLE_LIVE_WRITES=1). The PROGRAM→download→" +
                        "go_online→RUN sequence stops the running controller and overwrites " +
                        "its stored program image, so it has its own opt-in flag.",
                    Hint:
                        $"Set the {Capabilities.EnvVarControllerDownload} environment variable to 1 " +
                        $"to enable controller downloads. " +
                        $"Client command: {Capabilities.ClientCommandHintControllerDownload}");
            }

            if (!confirmedByUser)
            {
                return new ConfirmationRequiredError(
                    Error:
                        "allowControllerDownload=true refused: a controller download stops the " +
                        "running controller and permanently overwrites its stored program, so it " +
                        "requires the user's explicit approval. Tell the user exactly what will " +
                        "be overwritten, wait for their explicit approval in the conversation, " +
                        "and only then call logix_connect again with confirmedByUser=true.");
            }
        }

#endif

        // ── Non-virtual: validate backend before dispatching ──────────────────
        // Any value other than cip, l5x (and sdk in an SDK build) is an error;
        // silent fallback is not permitted.

        if (normalizedBackend != "cip" && normalizedBackend != "sdk" && normalizedBackend != "l5x")
        {
            return new ConnectBadBackendError(
                Error:       $"Unrecognized backend '{backend}'. " +
                             $"Valid values are: {string.Join(", ", ValidBackends)}.",
                Backend:     backend ?? string.Empty,
                ValidValues: ValidBackends);
        }

#if LOGIX_SDK
        // allowControllerDownload is only valid for backend='sdk' with sdkMode='online'.
        // Reject explicitly to prevent silent misuse on other paths.
        if (allowControllerDownload)
        {
            var sdkIsOnlineCheck = sdkMode.Equals("online", StringComparison.OrdinalIgnoreCase);
            if (normalizedBackend != "sdk" || !sdkIsOnlineCheck)
                return new ConnectError(
                    "allowControllerDownload=true is only valid when backend='sdk' and " +
                    "sdkMode='online'. The PROGRAM→download→go_online→RUN sequence " +
                    "requires a live SDK session targeting a physical controller.");
        }
#endif

#if LOGIX_SDK
        if (normalizedBackend == "sdk")
        {
            // ── Logix Designer SDK mode ───────────────────────────────────────
            // Delegated to ISdkTagServiceFactory so the core assembly carries no
            // compile-time dependency on RockwellAutomation.*.  The factory is
            // provided by LogixMcp.SdkAdapter via its ModuleInitializer.  When
            // neither a per-call factory nor the default is registered, the SDK
            // adapter is unavailable and a structured error is returned.

            var factory = sdkFactory ?? DefaultSdkFactory;

            if (factory is null)
            {
                var probeResults = SdkAdapterLoader.CollectProbeResults();

                var pathLines = string.Join(
                    Environment.NewLine,
                    SdkAdapterLoader.DescribeProbeResults(probeResults)
                        .Select(line => $"  {line}"));

                // When the adapter load sequence threw (corrupt assembly, version
                // mismatch, etc.), include the recorded reason so the caller can
                // self-diagnose without needing server logs.
                var loadFailure = SdkAdapterLoader.LoadFailureReason;
                var loadFailureNote = loadFailure is not null
                    ? Environment.NewLine + Environment.NewLine +
                      $"Adapter load error: {loadFailure}"
                    : string.Empty;

                return new ConnectSdkNotAvailableError(
                    Error: "backend='sdk' requires the Logix Designer SDK adapter, which could " +
                           "not be loaded. The following paths were probed (first hit wins):" +
                           Environment.NewLine + pathLines + loadFailureNote +
                           Environment.NewLine +
                           Environment.NewLine +
                           "To use a custom SDK installation, set the LOGIX_SDK_PATH environment " +
                           "variable to the directory containing RockwellAutomation*.dll files." +
                           Environment.NewLine +
                           "To enable SDK support, place the RockwellAutomation.LogixDesigner.CSClient " +
                           "NuGet package in .localfeed/ and rebuild with -p:EnableLogixSdk=true.",
                    ProbedPaths:      probeResults,
                    LoadFailureReason: loadFailure,
                    Hint: "Run bootstrap/provision-sdk.ps1 to install the Logix Designer SDK. " +
                          "Check the probedPaths field in this response to see which directories " +
                          "were searched and why each was rejected.");
            }

            // ── Comm-path-wins: upload-from-controller branch ──────
            // When sdkMode="online" and a comm path is supplied (sdkCommPath,
            // gateway, or gateway+path), this branch is ALWAYS taken —
            // regardless of whether projectService.IsLoaded is true.  An open
            // project is input for analysis and gate derivation; an online
            // session is correlated by the controller, not by a file, so the
            // two must not be tied together by the accident of which tool was
            // called first.  A project opened via logix_open_project stays
            // loaded and keeps serving static tools (RegisterFromUpload below
            // is a no-op only for an explicitly opened project; an earlier upload is replaced); the write-gate for
            // THIS online session is derived from the uploaded project, and
            // the response names that source via gateSource so the two
            // projects are never confused.  The file-backed path below
            // (factory.CreateAsync against projectService.FilePath) is never
            // reached when a comm path is present.
            var sdkIsOnlineForRouting = sdkMode.Equals(
                "online", StringComparison.OrdinalIgnoreCase);
            var hasCommPathForRouting =
                !string.IsNullOrWhiteSpace(sdkCommPath) || !string.IsNullOrWhiteSpace(gateway);

            if (sdkIsOnlineForRouting && hasCommPathForRouting)
            {
                // Captured before RegisterFromUpload runs so the outcome message
                // can describe the prior state accurately — RegisterFromUpload
                // never overwrites an already-loaded project.
                var projectAlreadyLoaded = projectService.IsLoaded;
                // A project registered by an earlier connect's upload (UploadedFrom set) was not opened with
                // logix_open_project; the message must not claim it was.
                var loadedProjectSource = projectService.UploadedFrom is { } priorUpload
                    ? $"uploaded by an earlier connect from '{priorUpload}'"
                    : "opened via logix_open_project";

                // Build the Rockwell SDK communications path.
                // sdkCommPath (Logix Designer format, e.g. "EmulateEthernet\192.0.2.10")
                // takes precedence over the gateway/path pair (CIP/libplctag format).
                // The two formats are NOT interchangeable: the SDK rejects the comma-separated
                // CIP form with "No module was found using the supplied encoded route string."
                var commPath = !string.IsNullOrWhiteSpace(sdkCommPath)
                    ? sdkCommPath
                    : string.IsNullOrWhiteSpace(path)
                        ? gateway
                        : $"{gateway},{path}";

                // Times each phase from its progress report; forwards every report to the caller's sink.
                var phaseRecorder = new ConnectPhaseRecorder(progress);

                // LdSdkService can be unreachable straight after an earlier upload (it restarts after a
                // crash). Retry the upload up to twice for the three dead-service messages only; each
                // retry waits for the service to be Running, then backs off 10 s / 20 s.
                var uploadRetries = new List<ConnectRetryEntry>();
                var uploadGiveUp = new SdkConnectGiveUp();
                var uploadClock = System.Diagnostics.Stopwatch.StartNew();
                SdkSessionResult uploadResult;
                var uploadSucceeded = false;
                try
                {
                    uploadResult = await (sdkConnectRetry ?? SdkConnectRetryPolicy.Default).RunAsync(
                        sink => factory.CreateFromControllerAsync(commPath, sink, ct),
                        uploadRetries, ct, phaseRecorder, () => phaseRecorder.CurrentPhase, uploadGiveUp).ConfigureAwait(false);
                    uploadSucceeded = true;
                }
                catch (TagServiceException ex) when (IsRouteError(ex))
                {
                    // The SDK's "No module was found using the supplied
                    // encoded route string." fires identically whether the comm path
                    // format is wrong OR the target simply does not answer on the
                    // network. Probe before blaming the path — reusing the
                    // IP-derivation + TCP-probe discriminator.
                    var probedIp = TryParseControllerIp(commPath);

                    if (probedIp is not null)
                    {
                        var reachable = await (controllerReachableProbe ?? DefaultControllerReachableProbe)(
                            probedIp, ct).ConfigureAwait(false);

                        if (!reachable)
                        {
                            AuditLogger.LogSdkServiceDiagnosis(
                                ex.Error.Message, "unreachable", "ConnectionFailed");

                            return new ConnectSdkConnectionFailedError(
                                Error:
                                    $"The controller at '{probedIp}' does not answer on TCP port " +
                                    $"44818 (EtherNet/IP). The comm path format is valid — this is " +
                                    $"not a comm-path error. Check that the controller or emulator " +
                                    $"is running and reachable on the network. " +
                                    $"SDK detail: {ex.Error.Message}",
                                Kind:              "ConnectionFailed",
                                ControllerAddress: $"{probedIp}:44818",
                                Hint:
                                    "Check that the controller or emulator is running and reachable " +
                                    "on the network, then retry. Do not change the comm path — its " +
                                    "format was already confirmed valid by the probe.")
                            {
                                Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                            };
                        }

                        // Controller answers — the comm path really is wrong (or the
                        // module route is). Fall through to the unchanged route-error
                        // message below.
                        AuditLogger.LogSdkServiceDiagnosis(ex.Error.Message, "reachable", "RouteError");
                    }
                    else
                    {
                        AuditLogger.LogSdkServiceDiagnosis(ex.Error.Message, "not-probed", "RouteError");
                    }

                    var targetMayBeDownNote = probedIp is null
                        ? " Additionally, the controller's address could not be derived from " +
                          "this comm path, so reachability could not be checked — the target " +
                          "may also be down."
                        : string.Empty;

                    return new ConnectSdkRouteError(
                        Error:
                            $"SDK upload failed: the comm path '{commPath}' was rejected by the " +
                            $"Logix Designer SDK with a route error. " +
                            $"The SDK requires a Logix comm path (e.g. \"EmulateEthernet\\192.0.2.10\"), " +
                            $"not a CIP/libplctag backplane path (e.g. \"192.0.2.10,1,0\"). " +
                            $"SDK detail: {ex.Error.Message}" + targetMayBeDownNote,
                        CommPathUsed:   commPath,
                        ExpectedFormat:
                            "Logix Designer comm path — examples: " +
                            "\"EmulateEthernet\\192.0.2.10\" (emulated controller), " +
                            "\"192.0.2.10\" (real controller, direct IP).",
                        Hint:
                            CommPathInvalidHint)
                    {
                        Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                    };
                }
                catch (TagServiceException ex) when (
                    ex.Error.Kind == TagServiceErrorKind.ConnectBusy)
                {
                    // Another upload holds the host-wide lease past the bounded wait (G33): nothing was
                    // uploaded, so no other session was disturbed. Say when to retry. A crash this connect survived
                    // before it queued for the lease is still part of the story (G50): it is in retries[]/sdkRestarts
                    // and named in the message.
                    var busyCrashes = AllUploadRestarts(uploadRetries, uploadGiveUp);
                    return ConnectBusyError.From(ex, busyCrashes) with
                    {
                        Retries     = uploadRetries.Count > 0 ? uploadRetries : null,
                        SdkRestarts = busyCrashes.Count > 0 ? busyCrashes : null,
                    };
                }
                catch (TagServiceException ex) when (
                    ex.Error.Kind == TagServiceErrorKind.PendingEditsPresent)
                {
                    // The controller holds uncommitted (pending) edits that prevented
                    // the SDK from extracting the project image after upload.
                    // Surface a structured error with a neutral hint listing both paths
                    // out — do not prefer accept over cancel or vice versa; that is the
                    // operator's decision.
                    return new ConnectPendingEditsError(
                        Error: ex.Error.Message,
                        Kind:  "PendingEditsPresent",
                        Hint:
                            "First check Logix Designer. If it shows edits present, either " +
                            "(a) accept or finalize them, or (b) cancel them, then call " +
                            "logix_connect again — this server will not clear them for you, " +
                            "they may be an operator's work in progress. " +
                            "If Logix Designer shows NO edits, the Logix SDK service has " +
                            "most likely faulted: check the Windows Application event log " +
                            "for LdSdkServer.exe errors and restart the LdSdkService " +
                            "service, then retry.",
                        DocsAnchor: "controller-has-pending-edits")
                    {
                        Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                    };
                }
                catch (TagServiceException ex) when (
                    ex.Error.Kind == TagServiceErrorKind.SdkServiceDegraded)
                {
                    // The TCP probe confirmed the controller answers on port 44818, so
                    // the PLC is alive.  LdSdkService's communications layer has faulted.
                    // Surface a structured record so callers can react programmatically
                    // without parsing the human-readable Error string.
                    return new ConnectSdkServiceDegradedError(
                        Error:             ex.Error.Message,
                        Kind:              "SdkServiceDegraded",
                        SdkCode:           ex.Error.SdkCode           ?? ex.InnerException?.Message ?? "unknown",
                        ControllerAddress: ex.Error.ControllerAddress ?? gateway,
                        Hint:
                            "The controller is reachable (port 44818 answered) but " +
                            "LdSdkService's communications layer has faulted. " +
                            "An operator must restart the LdSdkService process to " +
                            "restore the SDK communications channel.",
                        DocsAnchor: "sdk-service-degraded")
                    {
                        Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                    };
                }
                catch (TagServiceException ex) when (
                    ex.Error.Kind == TagServiceErrorKind.SdkCommLayerFaulted)
                {
                    // The Logix Designer SDK comm layer has faulted (RxE_COMM_*).
                    // LdSdkService is running but its internal communications layer
                    // is degraded — distinct from a dead PLC.
                    return new ConnectSdkCommLayerFaultedError(
                        Error: ex.Error.Message,
                        Kind:  "SdkCommLayerFaulted",
                        Hint:
                            "The Logix SDK comm layer is faulted (RxE_COMM_* error). " +
                            "Distinguish degraded LdSdkService from an unreachable PLC: " +
                            "if a TCP probe on the controller's port 44818 succeeds, the " +
                            "PLC is reachable but LdSdkService is degraded — restart " +
                            "LdSdkService. " +
                            "If port 44818 is unreachable, the controller itself is down.")
                    {
                        Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                    };
                }
                catch (TagServiceException ex)
                {
                    // G48: gave up after the crash-retry limit with at least one SDK restart seen: typed give-up.
                    var crashes = AllUploadRestarts(uploadRetries, uploadGiveUp);
                    if (uploadGiveUp.GaveUp && crashes.Count > 0)
                    {
                        return new ConnectSdkServiceUnstableError(
                            Error: ConnectSdkServiceUnstableError.BuildError(commPath, ex.Error.Message),
                            Kind:  "SdkServiceUnstable",
                            SdkRestarts: crashes,
                            Hint:  ConnectSdkServiceUnstableError.BuildHint(crashes.Count, uploadClock.Elapsed))
                        {
                            Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                        };
                    }
                    return new ConnectError(
                        $"Upload from controller at '{commPath}' failed: {ex.Error.Message}")
                    {
                        Retries = uploadRetries.Count > 0 ? uploadRetries : null,
                    };
                }
                finally
                {
                    // G46: the restarts the retries saw go into the connect's phaseTimings and into the audit log
                    // (success or failure), which logix_doctor reads back. G48: including the final crash.
                    var restarts = AllUploadRestarts(uploadRetries, uploadGiveUp);
                    phaseRecorder.NoteSdkRestart(restarts.Count > 0 ? restarts[^1] : null);
                    phaseRecorder.NoteCommsRetry(uploadRetries.LastOrDefault(r => r.CommsRetry is not null)?.CommsRetry);
                    foreach (var restart in restarts) AuditLogger.LogConnectSdkRestart(restart, commPath);

                    // G50: this connect failed. A session from an earlier connect whose LdSdkServer has since been
                    // replaced is dead; report it (connected:false) instead of leaving it looking live.
                    if (!uploadSucceeded) sessionService.MarkLostIfSdkRestarted();
                }

                // Derive write-gate policy from the uploaded project's L5X.
                // TypeProject is always non-null for CreateFromControllerAsync results.
                var uploadTypeProject = uploadResult.TypeProject;
                phaseRecorder.Report(ConnectPhases.For(ConnectPhases.GateDerivation));
                ITagService uploadSvc;
                bool        uploadGated;

                // Tag route (LGX_TAG_ROUTE): the SDK session stays the source of truth for
                // types and the write gate; reads and writes go over the route the setting gives.
                TagRouteMode tagRouteMode;
                try { tagRouteMode = TagRouteResolver.Current; }
                catch (TagRouteSettingInvalidException ex)
                {
                    try { await uploadResult.Service.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best-effort cleanup */ }
                    return new ConnectError(ex.Message);
                }
                var routedSdk = BuildRoutedService(
                    uploadResult.Service, commPath, uploadResult.TypeProject, tagRouteMode, cipRouteConnector,
                    uploadResult.ControllerIdentity ?? ControllerIdentity.FromProject(uploadResult.TypeProject),
                    cipIdentityReader);

                // Choose the route now (CIP connect + same-controller check, ~1 s) so connect can report it.
                // In 'cip' mode a failure leaves the route unset; the first tag operation retries and
                // raises the typed TagRouteUnavailable.
                string? tagRoute;
                string? tagRouteReason;
                string? identityCheck = null;
                var identityClock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    tagRoute       = await routedSdk.ResolveRouteAsync(ct).ConfigureAwait(false);
                    tagRouteReason = routedSdk.RouteReason;
                    identityCheck  = routedSdk.IdentityCheck;
                }
                catch (TagServiceException ex)
                {
                    tagRoute       = null;
                    tagRouteReason = ex.Error.Message;
                }
                catch (OperationCanceledException)
                {
                    try { await uploadResult.Service.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best-effort cleanup */ }
                    throw;
                }
                var identityMs = identityClock.ElapsedMilliseconds;

                if (uploadTypeProject is not null)
                {
                    var uploadPolicy = GateEngine.DerivePolicy(uploadTypeProject, GateConfigResolver.Current)
                        with { Origin = new GatePolicyOrigin(
                            GatePolicyOrigin.ControllerUpload, uploadResult.AcdPath, EnforcedBySession: true) };
                    uploadSvc   = new GatedTagService(routedSdk, uploadPolicy);
                    uploadGated = true;
                }
                else
                {
                    uploadSvc   = routedSdk;
                    uploadGated = false;
                }

                sessionService.Attach(uploadSvc, isVirtual: false, isLiveController: true,
                    controllerHost: TryParseControllerIp(commPath));
                sessionService.SetAuditController(commPath);

                // Keep the detailed L5X image of the controller for logix_diff_against_controller.
                if (uploadTypeProject is not null)
                    sessionService.SetControllerImage(uploadTypeProject, DateTimeOffset.UtcNow);

                // Populate the project service from the uploaded ACD so that
                // subsequent tool calls that guard on projectService.IsLoaded or
                // projectService.FilePath see a valid project without requiring a
                // separate logix_open_project invocation.
                // RegisterFromUpload is a no-op if a logix_open_project project is loaded —
                // an already-open logix_open_project project is never overridden
                // or unloaded by this branch; it keeps serving static tools while
                // this online session's gate is derived from the uploaded project.
                if (uploadResult.TypeProject is not null && uploadResult.AcdPath is not null)
                    projectService.RegisterFromUpload(uploadResult.TypeProject, uploadResult.AcdPath, commPath);

                modeService?.Reset("Unknown");

                var uploadConnectResult = new ConnectResult(
                    Status:            "connected",
                    Gateway:           null,
                    Path:              null,
                    GatewayUsed:       false,
                    Virtual:           false,
                    Backend:           "sdk",
                    TagCount:          null,
                    Gated:             uploadGated,
                    Target:            "online-controller",
                    Online:            true,
                    DownloadPerformed: false,
                    ModeTransitions:   [],
                    Correlation:       uploadResult.CorrelationPath,
                    GateSource:        "uploaded-controller-project",
                    Message:           projectAlreadyLoaded
                        ? "Connected via Logix Designer SDK backend (online-controller mode) " +
                          $"using controller at '{commPath}'. " +
                          "The comm path wins: the controller's current project was uploaded to a new " +
                          "server-managed ACD to correlate this online session. The project already " +
                          $"{loadedProjectSource} remains loaded and continues to serve static tools " +
                          "(logix_gate_report, logix_cross_reference, logix_get_routine, ...); it is not " +
                          "the source of this session's write-gate policy — see gateSource. " +
                          "Reads and writes target the live controller via the SDK gRPC channel. " +
                          $"Write-gate policy derived from the uploaded controller project " +
                          $"(gated: {uploadGated.ToString().ToLowerInvariant()})."
                        : "Connected via Logix Designer SDK backend (online-controller mode) " +
                          $"using controller at '{commPath}'. " +
                          "The controller's current project was uploaded to a new server-managed ACD " +
                          "to establish correlation — no pre-existing project file was required. " +
                          "Reads and writes target the live controller via the SDK gRPC channel. " +
                          $"Write-gate policy derived from uploaded project " +
                          $"(gated: {uploadGated.ToString().ToLowerInvariant()}).");
                // Online target: the uploaded project's revision, not any file open beside it.
                uploadConnectResult = WithTargetRevision(
                    uploadConnectResult,
                    uploadTypeProject is null ? null : TargetRevision.Detect(uploadTypeProject, uploaded: true),
                    sessionService);
                uploadConnectResult = uploadConnectResult with
                {
                    DesignerVersion     = uploadResult.DesignerVersion?.ToString(),
                    BuildCheckAvailable = uploadResult.DesignerVersion?.BuildCheckAvailable,
                    Capabilities        = CapabilitiesOf(uploadResult),
                    TagRoute            = tagRoute,
                    TagRouteReason      = tagRouteReason,
                    IdentityCheck       = identityCheck,
                    PhaseTimings        = phaseRecorder.Snapshot(identityMs),
                    Retries             = uploadRetries.Count > 0 ? uploadRetries : null,
                };
                AuditLogger.LogConnect(
                    uploadConnectResult.Backend, uploadConnectResult.Target,
                    uploadConnectResult.Correlation, uploadConnectResult.DownloadPerformed,
                    uploadConnectResult.GateSource,
                    allowlistSha256,
                    gateway: commPath,
                    gated: uploadGated,
                    phaseTimings: uploadConnectResult.PhaseTimings);
                return uploadConnectResult;
            }

            // Builds the structured "download required" error shared by the two
            // paths that guard the file-based online route:
            // the up-front explicit-opt-in refusal below, and the defensive
            // ImagesIncompatible catch (kept in case allowControllerDownload=true
            // was supplied but the download itself still could not correlate).
            // The hint explicitly names the comm-path/upload route as the
            // non-destructive alternative — it works whether or not a project is
            // open — before mentioning the destructive
            // allowControllerDownload=true fallback.
            static ConnectDownloadRequiredError BuildDownloadRequiredError(string errorMessage) =>
                new ConnectDownloadRequiredError(
                    Error: errorMessage,
                    Kind:  "DownloadRequired",
                    DownloadEffect:
                        "A download stops the running controller (transitions it to PROGRAM mode) " +
                        "and permanently overwrites the program image stored in it with the open project. " +
                        "The controller is then transitioned back to RUN mode.",
                    HowToOptIn:
                        "To authorise the download, call logix_connect again with " +
                        "allowControllerDownload=true and confirmedByUser=true (set " +
                        "confirmedByUser only after the user has explicitly approved; the server " +
                        "must also be started with LGX_ENABLE_CONTROLLER_DOWNLOAD=1). " +
                        "WARNING: this will halt the running controller and irreversibly overwrite " +
                        "the program stored in it. Only proceed when you have confirmed it is safe " +
                        "to interrupt the running process.",
                    Hint:
                        "Non-destructive alternative: call logix_connect with sdkMode='online' and a " +
                        "gateway or sdkCommPath — supplying a comm path takes the upload-from-controller " +
                        "route (whether or not a project is open via logix_open_project) and gives a " +
                        "non-destructive online session, with no mode change or program loss. " +
                        "Destructive fallback: call logix_connect again with " +
                        "allowControllerDownload=true to overwrite the controller with the open " +
                        "project (stops the controller and permanently reloads it).");

            // ── No comm path available for this call ───────────────────────────
            // Reached when sdkMode="offline", or sdkMode="online" with neither
            // sdkCommPath nor gateway supplied.  The upload-from-controller
            // branch above cannot run without a comm path, so a project must
            // already be open to proceed via the file-backed path below.
            if (!projectService.IsLoaded)
            {
                if (sdkIsOnlineForRouting)
                {
                    // sdkMode="online" but neither sdkCommPath nor gateway was
                    // supplied, and no project is loaded to fall back on. This is
                    // the missing-argument case: without a comm path there is no
                    // way to reach a live controller, and without a loaded project
                    // there is no file-based fallback either. Name the specific
                    // arguments the caller may supply rather than letting this
                    // fall through as a bare ConnectError.
                    return new ConnectMissingArgumentError(
                        Error:
                            "backend='sdk' with sdkMode='online' requires a comm path " +
                            "(sdkCommPath or gateway) when no project is loaded; " +
                            "neither was supplied.",
                        Kind:      "MissingSdkCommPath",
                        Parameter: "sdkCommPath",
                        Hint:
                            "Supply sdkCommPath (Logix Designer comm path, e.g. " +
                            "'EmulateEthernet\\192.0.2.10') or gateway (IP address) " +
                            "to upload the controller's current project and establish an " +
                            "online session — this works whether or not a project is open. " +
                            "Alternatively, call logix_open_project first to load a project " +
                            "file for the file-based route.");
                }

                return new ConnectError(
                    "No project loaded. " +
                    "To use backend='sdk' without a project file, call logix_connect " +
                    "with sdkMode='online' and a gateway or sdkCommPath — the controller's " +
                    "project will be uploaded automatically. " +
                    "Alternatively, call logix_open_project first to load a project file.")
                {
                    Kind = ErrorKinds.NoProjectLoaded,
                    Hint =
                        "Call logix_connect with backend='sdk', sdkMode='online' and sdkCommPath or gateway to " +
                        "upload the controller's project, or call logix_open_project with a path to a project file.",
                };
            }

            if (projectService.FilePath is null)
                return new ConnectError(
                    "Project file path is unavailable. " +
                    "Re-open the project via logix_open_project to use backend='sdk'.");

            // A registration left by an upload whose session closed names an ACD that was deleted with it.
            if (!File.Exists(projectService.FilePath))
                return new ConnectProjectFileMissingError(
                    Error:
                        $"The registered project file '{projectService.FilePath}' no longer exists on disk, " +
                        "so there is nothing to open an SDK session against.",
                    Kind: ErrorKinds.ProjectFileMissing,
                    Hint:
                        $"The project file '{projectService.FilePath}' is missing: the upload copy was removed " +
                        "when its session closed. Call logix_connect with sdkMode='online' and a commPath " +
                        "(sdkCommPath or gateway) to upload it again, or call logix_open_project with a path " +
                        "to a project file.");

            // A partial export derives no write gate, so it cannot back a live SDK session.
            if (sdkIsOnlineForRouting && projectService.PartialExport is not null)
                return new ConnectError(
                    "Cannot open an online SDK session from the open project: " +
                    PartialExportInfo.Reason + ".");

            // ── File-based online stays explicit opt-in ──────
            // The file-based branch below (factory.CreateAsync against
            // projectService.FilePath) is the only branch capable of running the
            // PROGRAM→download→go_online→RUN sequence. That capability must be a
            // deliberate, explicit choice — even in the case where the
            // controller's stored image already happens to match the open
            // project and no download would actually be performed. Without
            // allowControllerDownload=true, refuse up front instead of silently
            // attempting a live file-based connection.
            if (sdkIsOnlineForRouting && !allowControllerDownload)
            {
                return BuildDownloadRequiredError(
                    "Cannot establish an online SDK session via the file-based route: " +
                    "allowControllerDownload was not set to true. The file-based online " +
                    "route is explicit opt-in and is only taken when sdkMode='online', no " +
                    "comm path is supplied, a project is open, and allowControllerDownload=true.");
            }

            var project = projectService.Project!;

            // SHA-256 of the project file about to be downloaded, captured BEFORE the
            // download so the audit record names exactly the bytes that were sent.
            string? downloadFileSha = allowControllerDownload
                ? TryHashFile(projectService.FilePath)
                : null;

            // Real phase for logix_connect_status; without it a slow file-based connect reads "starting".
            progress?.Report(ConnectPhases.For(ConnectPhases.OpenProject));

            SdkSessionResult sdkResult;
            try
            {
                sdkResult = await factory.CreateAsync(
                    projectService.FilePath, project, sdkMode,
                    allowControllerDownload, ct).ConfigureAwait(false);
            }
            catch (TagServiceException ex) when (
                ex.Error.Kind == TagServiceErrorKind.ImagesIncompatible)
            {
                // The controller's stored image is incompatible with the open project
                // even though allowControllerDownload=true (checked above). Return a
                // structured error that explains exactly what is required so the
                // caller can make an informed decision — do NOT proceed to download
                // silently.
                return BuildDownloadRequiredError(
                    $"Cannot establish an online SDK session: {ex.Error.Message}");
            }
            catch (TagServiceException ex) when (
                ex.Error.Kind == TagServiceErrorKind.ProjectNotCorrelated)
            {
                // The SDK threw an RxE_COMM_* error during GoOnlineAsync on the
                // file-based path.  A file-opened project cannot prove it matches
                // the controller's running image — this is the "uncorrelated file"
                // trap. The TCP probe was NOT invoked; guide the
                // caller to the comm-path/upload route instead.
                return new ConnectProjectNotCorrelatedError(
                    Error: ex.Error.Message,
                    Kind:  "ProjectNotCorrelated",
                    Hint:
                        "A file-opened project cannot be correlated with the controller " +
                        "via the file-based GoOnline path. " +
                        "Use the comm-path/upload route: call logix_connect with " +
                        "sdkMode='online' and a gateway or sdkCommPath without a " +
                        "pre-opened project — the controller's current project will be " +
                        "uploaded automatically and used to establish the correlated " +
                        "online session.");
            }
            catch (TagServiceException ex) when (
                ex.Error.Kind == TagServiceErrorKind.SdkCommLayerFaulted)
            {
                // The Logix Designer SDK comm layer has faulted (RxE_COMM_*).
                // LdSdkService is running but its internal communications layer
                // is degraded — distinct from a dead PLC.
                return new ConnectSdkCommLayerFaultedError(
                    Error: ex.Error.Message,
                    Kind:  "SdkCommLayerFaulted",
                    Hint:
                        "The Logix SDK comm layer is faulted (RxE_COMM_* error). " +
                        "Distinguish degraded LdSdkService from an unreachable PLC: " +
                        "if a TCP probe on the controller's port 44818 succeeds, the " +
                        "PLC is reachable but LdSdkService is degraded — restart " +
                        "LdSdkService. " +
                        "If port 44818 is unreachable, the controller itself is down.");
            }
            catch (TagServiceException ex)
            {
                return new ConnectError(
                    $"SDK session open failed: {ex.Error.Message}");
            }

            var sdkPolicy   = GateEngine.DerivePolicy(project, GateConfigResolver.Current)
                with { Origin = GatePolicyOrigin.ForSessionFromFile(projectService.FilePath) };
            var gatedSdkSvc = new GatedTagService(sdkResult.Service, sdkPolicy);

            var sdkIsOnline = sdkMode.Equals("online", StringComparison.OrdinalIgnoreCase);
            sessionService.Attach(gatedSdkSvc, isVirtual: false, isLiveController: sdkIsOnline);

            // SDK online sessions target a live controller; mode unknown until queried.
            // SDK offline sessions are backed by the project file; mode is unknown.
            modeService?.Reset("Unknown");

            // Build a download note for the message when a download was performed.
            var downloadNote = sdkResult.DownloadPerformed
                ? " A project download was performed during session establishment " +
                  $"(mode transitions: {string.Join(" → ", sdkResult.ModeTransitions)})."
                : string.Empty;

            var sdkConnectResult = new ConnectResult(
                Status:            "connected",
                Gateway:           null,
                Path:              null,
                GatewayUsed:       false,
                Virtual:           false,
                Backend:           "sdk",
                TagCount:          null,
                Gated:             true,
                Target:            sdkIsOnline ? "online-controller" : "offline-project",
                Online:            sdkIsOnline,
                DownloadPerformed: sdkResult.DownloadPerformed,
                ModeTransitions:   sdkResult.ModeTransitions,
                Correlation:       sdkResult.CorrelationPath,
                Message:           sdkIsOnline
                    ? "Connected via Logix Designer SDK backend (online-controller mode). " +
                      "Reads and writes target the live controller via the SDK gRPC channel. " +
                      $"Write-gate policy derived from open project.{downloadNote}"
                    : "Connected via Logix Designer SDK backend (offline-project mode). " +
                      "Writes reach the project file only — no physical PLC is targeted. " +
                      "Write-gate policy derived from open project.");
            sdkConnectResult = WithTargetRevision(
                sdkConnectResult,
                sessionService.ControllerImage is { } sdkImage ? TargetRevision.Detect(sdkImage, uploaded: true)
                                                               : projectService.TargetRevision,
                sessionService);
            sdkConnectResult = sdkConnectResult with
            {
                DesignerVersion     = sdkResult.DesignerVersion?.ToString(),
                BuildCheckAvailable = sdkResult.DesignerVersion?.BuildCheckAvailable,
                Capabilities        = CapabilitiesOf(sdkResult),
            };
            AuditLogger.LogConnect(
                sdkConnectResult.Backend, sdkConnectResult.Target,
                sdkConnectResult.Correlation, sdkConnectResult.DownloadPerformed,
                gated: true,
                allowlistSha256: allowlistSha256,
                gateSource: ProjectGateSource(projectService, out var sdkGateSha),
                gateSourceSha256: sdkGateSha,
                downloadedFileSha256: sdkResult.DownloadPerformed ? downloadFileSha : null);
            return sdkConnectResult;
        }
#endif

        if (normalizedBackend == "l5x")
        {
            // ── L5X offline mode ──────────────────────────────────────────────
            // Backed by L5XOfflineTagService against the open L5X project.
            if (!projectService.IsLoaded)
                return new ConnectError(
                    "No project loaded. Call logix_open_project first to use backend='l5x'.")
                {
                    Kind = ErrorKinds.NoProjectLoaded,
                    Hint =
                        "Call logix_open_project with a path to an L5X project file, then call logix_connect with " +
                        "backend='l5x' again.",
                };

            var project = projectService.Project!;
            var l5xSvc  = new L5XOfflineTagService(project);
            await l5xSvc.ConnectAsync(gateway, path, ct);   // no-op

            // Wrap in GatedTagService; a partial export derives no gate (see BuildFileGate).
            var l5xGate = BuildFileGate(l5xSvc, projectService, allowlistSha256);

            sessionService.Attach(l5xGate.Service, isVirtual: false, isLiveController: false);
            ApplyNoGate(sessionService, l5xGate);

            // L5X offline sessions have no live controller; mode is unknown.
            modeService?.Reset("Unknown");

            var l5xConnectResult = new ConnectResult(
                Status:            "connected",
                Gateway:           null,
                Path:              null,
                GatewayUsed:       false,
                Virtual:           false,
                Backend:           "l5x",
                TagCount:          null,
                Gated:             l5xGate.Gated,
                Target:            "offline-project",
                Online:            false,
                DownloadPerformed: null,
                ModeTransitions:   null,
                Correlation:       null,
                Message:           "Connected via L5X offline backend (offline-project mode). " +
                                   "Writes reach the in-memory L5X store only — no physical PLC is targeted." +
                                   (l5xGate.Note ?? " Write-gate policy derived from open project."),
                GateSource:        l5xGate.GateSourceOverride);
            l5xConnectResult = WithTargetRevision(l5xConnectResult, projectService.TargetRevision, sessionService);
            AuditLogger.LogConnect(
                l5xConnectResult.Backend, l5xConnectResult.Target,
                l5xConnectResult.Correlation, l5xConnectResult.DownloadPerformed,
                gated: l5xGate.Gated,
                allowlistSha256: allowlistSha256,
                gateSource: FileGateAuditSource(l5xGate, projectService, allowlistSha256, out var l5xGateSha),
                gateSourceSha256: l5xGateSha);
            return l5xConnectResult;
        }

        // ── Live CIP mode ─────────────────────────────────────────────────────
        // normalizedBackend == "cip" (the only remaining case after validation above).
        // gateway defaults to "" at the MCP tool boundary, so a caller
        // who omits it entirely reaches this branch rather than being rejected by
        // the MCP marshaller. Check explicitly up front and return a structured
        // missing-argument error naming "gateway" — do not rely solely on
        // LibPlcTagService.ConnectAsync's internal validation, whose
        // TagServiceException is caught below only as a defensive fallback.
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return new ConnectMissingArgumentError(
                Error: "backend='cip' requires a gateway address; none was supplied.",
                Kind:  "MissingGateway",
                Parameter: "gateway",
                Hint:
                    "Supply the gateway parameter (EtherNet/IP IP address or hostname, " +
                    "e.g. '198.51.100.10') to connect with backend='cip'. " +
                    NoGatewayAlternatives);
        }

        // Refuse libplctag attribute-string injection (e.g. "10.0.0.1&elem_count=9")
        // before any native call is made.
        if (FindInjectionChar(gateway, allowSpace: false) is { } badGw)
            return BuildInvalidAddressArgument("gateway", badGw);
        if (path is not null && FindInjectionChar(path, allowSpace: true) is { } badPath)
            return BuildInvalidAddressArgument("path", badPath);

        var liveSvc = new LibPlcTagService();

        try
        {
            await liveSvc.ConnectAsync(gateway, path, ct);
        }
        catch (TagServiceException ex) when (
            ex.Message.Contains("Gateway address", StringComparison.OrdinalIgnoreCase))
        {
            // Defensive fallback: should be unreachable given the explicit check
            // above, but kept in case LibPlcTagService's validation is ever the
            // first line of defense again.
            return new ConnectMissingArgumentError(
                Error: $"backend='cip' requires a gateway address: {ex.Message}",
                Kind:  "MissingGateway",
                Parameter: "gateway",
                Hint:
                    "Supply the gateway parameter (EtherNet/IP IP address or hostname, " +
                    "e.g. '198.51.100.10') to connect with backend='cip'. " +
                    NoGatewayAlternatives);
        }
        catch (TagServiceException ex)
        {
            return new ConnectError($"Connection failed: {ex.Message}");
        }

        // Probe the controller: ConnectAsync only stores gateway/path, so a wrong
        // path would otherwise only surface on the first tag read. Fail fast here.
        try
        {
            await RunCipProbeWithDeadlineAsync(
                cipConnectProbe ?? DefaultCipConnectProbe, liveSvc,
                cipProbeDeadline ?? DefaultCipProbeDeadline, ct).ConfigureAwait(false);
        }
        catch (CipProbeDeadlineException ex)
        {
            // A timeout is never evidence of a bad path, so this is always
            // ConnectionFailed (not CipPathInvalid), even when path is empty.
            try { await liveSvc.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }

            return BuildCipDeadlineError(gateway, path, ex.Elapsed, ex.Deadline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { await liveSvc.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }

            var runtimeMissing = ex switch
            {
                TagServiceException { Error.Kind: TagServiceErrorKind.CipRuntimeMissing } tse => tse,
                DllNotFoundException dnf => LibPlcTagService.MapNativeLoadFailure(dnf),
                _ => null,
            };
            if (runtimeMissing is not null)
            {
                return new ConnectCipProbeError(
                    Error: runtimeMissing.Error.Message,
                    Kind: "CipRuntimeMissing",
                    Gateway: gateway,
                    Path: path ?? "",
                    Hint: runtimeMissing.Error.Hint ?? LibPlcTagService.CipRuntimeMissingHint);
            }

            return BuildCipProbeError(gateway, path, ex.Message);
        }

        // One type resolver per CIP session: project types first, then the
        // controller tag list (read once, cached), otherwise unresolved.
        liveSvc.TypeResolver = new CipTypeResolver(
            projectService.IsLoaded ? projectService.Project : null,
            liveSvc.ListTagsAsync);
        // Aliases follow the same project the gate is derived from (the shared resolver).
        if (projectService.IsLoaded) liveSvc.Aliases = new AliasResolver(projectService.Project!);

        // Optionally wrap in GatedTagService if a project is loaded.
        ITagService effectiveSvc;
        bool        cipGated;
        string      gateNote;
        FileGate?   noGate = null;

        string? cipGateSourceOverride = null;

        if (projectService.IsLoaded)
        {
            var cipGate = BuildFileGate(liveSvc, projectService, allowlistSha256);
            effectiveSvc          = cipGate.Service;
            cipGated              = cipGate.Gated;
            cipGateSourceOverride = cipGate.GateSourceOverride;
            gateNote              = cipGate.Note ?? " Write-gate policy derived from open project.";
            noGate                = cipGate;
        }
        else
        {
            effectiveSvc = liveSvc;
            cipGated     = false;
            gateNote     = allowlistSha256 is null
                ? " No project loaded: reads only. Writes are refused until a project is open or LGX_WRITE_ALLOWLIST is set."
                : " No project loaded: writes are limited to the site allowlist.";
        }

        sessionService.Attach(effectiveSvc, isVirtual: false, isLiveController: true,
            controllerHost: TryParseControllerIp(string.IsNullOrWhiteSpace(path) ? gateway : $"{gateway},{path}"));
        sessionService.SetAuditController(ControllerLabel(gateway, path));
        if (noGate is { } cipNoGate) ApplyNoGate(sessionService, cipNoGate);

        // Live PLC: actual mode unknown until queried via CIP.
        modeService?.Reset("Unknown");

        string? cipGateSha = null;
        var cipConnectResult = new ConnectResult(
            Status:            "connected",
            Gateway:           gateway,
            Path:              path,
            GatewayUsed:       true,
            Virtual:           false,
            Backend:           "cip",
            TagCount:          null,
            Gated:             cipGated,
            Target:            "online-controller",
            Online:            true,
            DownloadPerformed: null,
            ModeTransitions:   null,
            Correlation:       null,
            Message:           $"Connected to live PLC at {gateway} (path: {path}) " +
                               $"in online-controller mode — writes reach the physical PLC.{gateNote}",
            GateSource:        cipGateSourceOverride);
        cipConnectResult = WithTargetRevision(cipConnectResult, projectService.TargetRevision, sessionService);
        AuditLogger.LogConnect(
            cipConnectResult.Backend, cipConnectResult.Target,
            cipConnectResult.Correlation, cipConnectResult.DownloadPerformed,
            gateway: gateway,
            path: path,
            gated: cipGated,
            allowlistSha256: allowlistSha256,
            gateSource: cipGateSourceOverride is not null ? cipGateSourceOverride
                      : cipGated ? ProjectGateSource(projectService, out cipGateSha)
                                 : (allowlistSha256 is null ? "none" : "allowlist"),
            gateSourceSha256: cipGateSha);
        return cipConnectResult;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Outcome of <see cref="BuildFileGate"/>: the service a session should use, and how it is gated.
    /// </summary>
    /// <param name="Service">The service to attach (a <see cref="GatedTagService"/> unless there is no gate).</param>
    /// <param name="Gated">Whether <paramref name="Service"/> enforces a write gate.</param>
    /// <param name="Partial">Whether the open file is a partial export (no gate derived from it).</param>
    /// <param name="GateSourceOverride">Value for <c>gateSource</c> when it is not the project file.</param>
    /// <param name="Note">Message fragment describing the gate; <c>null</c> for the ordinary full-export gate.</param>
    internal sealed record FileGate(
        ITagService Service, bool Gated, bool Partial, string? GateSourceOverride, string? Note);

    /// <summary>
    /// The one rule for gating a session on the open file, shared by the virtual, l5x and cip
    /// backends. A full controller export derives the write gate. A partial (routine/program)
    /// export never does: with <c>LGX_WRITE_ALLOWLIST</c> set the grants come only from the
    /// allowlist (every deny and range the partial file proves still applies); without it the
    /// session has no gate and refuses writes with <c>WriteGateRequired</c>.
    /// </summary>
    /// <param name="inner">The backend service to wrap.</param>
    /// <param name="projectService">The loaded project (must be loaded).</param>
    /// <param name="allowlistSha256">SHA-256 of the active allowlist, or <c>null</c> when none.</param>
    /// <returns>The gating outcome.</returns>
    internal static FileGate BuildFileGate(
        ITagService inner, ProjectService projectService, string? allowlistSha256)
    {
        var origin = GatePolicyOrigin.ForSessionFromFile(projectService.FilePath);

        // The gate follows aliases in the same file it was derived from, whichever backend sits beneath it
        // (CIP, l5x, virtual), so a write to Alias_ToCmd is judged and written as its base.
        ISessionTypeSource? aliasSource = (inner as IAliasAware)?.Aliases is null
            ? new StaticTypeSource(projectService.Project!)
            : null;

        if (projectService.PartialExport is null)
        {
            var policy = GateEngine.DerivePolicy(projectService.Project!, GateConfigResolver.Current) with { Origin = origin };
            return new FileGate(new GatedTagService(inner, policy, aliasSource), true, false, null, null);
        }

        if (allowlistSha256 is not null)
        {
            // Every tag is treated as a command candidate so the name-independent denies (firmware
            // types, unknown write context, External Access, program-written, safety) and ranges
            // are all reported; GatedTagService then grants only what the allowlist lists.
            var partialConfig = GateConfigResolver.PartialExport;
            var policy = GateEngine.DerivePolicy(projectService.Project!, partialConfig) with { Origin = origin };
            var gated = new GatedTagService(inner, policy, aliasSource);
            gated.GrantFromAllowlist();
            return new FileGate(gated, true, true, PartialAllowlistGateSource,
                " Partial export plus write allowlist: grants come only from the allowlist; " +
                $"every deny and range the partial file proves still applies ({PartialExportInfo.Reason}).");
        }

        return new FileGate(inner, false, true, null,
            $" No write gate: {PartialExportInfo.Reason}." +
            " Writes are refused until a full controller export is open or LGX_WRITE_ALLOWLIST is set.");
    }

    /// <summary>Records on the session that it has no write gate because the file is partial.</summary>
    private static void ApplyNoGate(TagSessionService sessionService, FileGate gate)
    {
        if (gate.Partial && !gate.Gated) sessionService.MarkNoWriteGate(PartialExportInfo.Reason);
    }

    /// <summary><c>gateSource</c> for the audit record of a file-gated virtual or l5x session.</summary>
    private static string FileGateAuditSource(
        FileGate gate, ProjectService projectService, string? allowlistSha256, out string? sha256)
    {
        sha256 = null;
        if (gate.GateSourceOverride is not null) return gate.GateSourceOverride;
        if (gate.Gated) return ProjectGateSource(projectService, out sha256);
        return allowlistSha256 is null ? "none" : "allowlist";
    }

    /// <summary>
    /// <c>gateSource</c> for a CIP session on a partial export with <c>LGX_WRITE_ALLOWLIST</c> set:
    /// grants come from the allowlist, denies and ranges from the partial file.
    /// </summary>
    internal const string PartialAllowlistGateSource = "allowlist+partial-export-denies";

    /// <summary><c>gateway/path</c> label for audit records, or just the gateway when no path.</summary>
    internal static string? ControllerLabel(string? gateway, string? path) =>
        string.IsNullOrWhiteSpace(gateway) ? null
        : string.IsNullOrWhiteSpace(path) ? gateway : $"{gateway}/{path}";

    /// <summary>
    /// Lower-case hex SHA-256 of <paramref name="file"/>, or <c>null</c> when the path is
    /// empty or the file cannot be read.
    /// </summary>
    internal static string? TryHashFile(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
            using var fs = File.OpenRead(file);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Audit <c>gate_source</c> for a session gated by the open project:
    /// <c>project:&lt;file path&gt;</c>, with the file's SHA-256 in <paramref name="sha256"/>
    /// (<c>null</c> when the file cannot be read).
    /// </summary>
    internal static string ProjectGateSource(ProjectService projectService, out string? sha256)
    {
        var file = projectService.FilePath;
        sha256 = null;
        if (string.IsNullOrEmpty(file)) return "project";
        try
        {
            using var fs = File.OpenRead(file);
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { /* unreadable project file: record the path without a hash */ }
        return "project:" + file;
    }

    /// <summary>Hard deadline for the whole post-connect CIP probe.</summary>
    internal static readonly TimeSpan DefaultCipProbeDeadline = TimeSpan.FromSeconds(15);

    /// <summary>Raised when the CIP connect probe exceeds its hard deadline.</summary>
    private sealed class CipProbeDeadlineException(TimeSpan elapsed, TimeSpan deadline)
        : Exception($"connect probe did not complete within {deadline.TotalSeconds:0.#} s")
    {
        /// <summary>Wall-clock time spent before giving up.</summary>
        public TimeSpan Elapsed { get; } = elapsed;
        /// <summary>The deadline that was exceeded.</summary>
        public TimeSpan Deadline { get; } = deadline;
    }

    /// <summary>
    /// Runs <paramref name="probe"/> under a hard <paramref name="deadline"/>.
    /// The probe gets a linked, deadline-cancelled token, but because a native
    /// call may ignore cancellation (or block synchronously) it is run on the
    /// thread pool and raced against a delay with <see cref="Task.WhenAny(Task[])"/>;
    /// if the deadline wins the probe is abandoned and its eventual outcome
    /// observed so nothing is left unobserved. The linked token source is
    /// disposed only once the abandoned probe finishes, so the probe's tag
    /// cleanup never races a disposed token.
    /// </summary>
    /// <param name="probe">The probe delegate.</param>
    /// <param name="svc">The freshly connected live service.</param>
    /// <param name="deadline">Maximum time to wait.</param>
    /// <param name="ct">Caller cancellation (propagates as <see cref="OperationCanceledException"/>).</param>
    /// <exception cref="CipProbeDeadlineException">The deadline elapsed first.</exception>
    private static async System.Threading.Tasks.Task RunCipProbeWithDeadlineAsync(
        Func<ITagService, CancellationToken, System.Threading.Tasks.Task> probe,
        ITagService svc, TimeSpan deadline, CancellationToken ct)
    {
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        var probeTask = System.Threading.Tasks.Task.Run(() => probe(svc, cts.Token));
        _ = probeTask.ContinueWith(
            t => { _ = t.Exception; cts.Dispose(); },
            System.Threading.Tasks.TaskScheduler.Default);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = System.Threading.Tasks.Task.Delay(deadline, delayCts.Token);

        var winner = await System.Threading.Tasks.Task.WhenAny(probeTask, delayTask).ConfigureAwait(false);
        if (winner == probeTask)
        {
            delayCts.Cancel();
            await probeTask.ConfigureAwait(false);   // propagate probe result / failure
            return;
        }

        ct.ThrowIfCancellationRequested();
        throw new CipProbeDeadlineException(sw.Elapsed, deadline);
    }

    /// <summary>
    /// Builds the <c>ConnectionFailed</c> error for a probe that exceeded its
    /// hard deadline, naming the gateway, path and elapsed time.
    /// </summary>
    /// <param name="gateway">Gateway that was tried.</param>
    /// <param name="path">Path that was tried (may be empty).</param>
    /// <param name="elapsed">Time spent before giving up.</param>
    /// <param name="deadline">The deadline that was exceeded.</param>
    private static ConnectCipProbeError BuildCipDeadlineError(
        string gateway, string? path, TimeSpan elapsed, TimeSpan deadline)
    {
        var shownPath = path ?? "";
        return new ConnectCipProbeError(
            Error: $"Connection failed: timed out connecting to gateway '{gateway}' " +
                   $"using path '{shownPath}' after {elapsed.TotalSeconds:0.0} s " +
                   $"(hard deadline {deadline.TotalSeconds:0.#} s); the controller did not respond.",
            Kind: "ConnectionFailed",
            Gateway: gateway,
            Path: shownPath,
            Hint: $"Check that the controller is powered and reachable at '{gateway}' " +
                  "(cabling, IP address, firewall) and that the path routes to the CPU slot. " +
                  "The server is still responsive; retry logix_connect once the target is reachable.");
    }

    /// <summary>
    /// Default post-connect CIP probe: lists the controller's <c>@tags</c>, which
    /// exists on every controller and exercises the full gateway + path route.
    /// </summary>
    /// <param name="svc">The freshly connected live service.</param>
    /// <param name="ct">Upstream cancellation token.</param>
    private static async System.Threading.Tasks.Task DefaultCipConnectProbe(ITagService svc, CancellationToken ct)
        => _ = await svc.ListTagsAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Builds the structured error for a failed CIP connect probe.
    /// <c>CipPathInvalid</c> when <paramref name="path"/> is empty/whitespace
    /// (with the <c>1,&lt;slot&gt;</c> hint), <c>ConnectionFailed</c> otherwise.
    /// </summary>
    /// <param name="gateway">Gateway that was tried.</param>
    /// <param name="path">Path that was tried (may be empty).</param>
    /// <param name="reason">Underlying failure message.</param>
    private static ConnectCipProbeError BuildCipProbeError(string gateway, string? path, string reason)
    {
        var shownPath = path ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ConnectCipProbeError(
                Error: $"Could not reach a controller at gateway '{gateway}' with an empty path " +
                       $"(path tried: ''): {reason}",
                Kind: "CipPathInvalid",
                Gateway: gateway,
                Path: shownPath,
                Hint: "No path was supplied and none is assumed. " +
                      "ControlLogix/CompactLogix 5x80 controllers usually need path '1,<slot>', " +
                      "for example '1,0'. Retry logix_connect with an explicit path.");
        }

        return new ConnectCipProbeError(
            Error: $"Connection failed: could not reach a controller at gateway '{gateway}' " +
                   $"using path '{path}': {reason}",
            Kind: "ConnectionFailed",
            Gateway: gateway,
            Path: shownPath,
            Hint: $"Check that the controller is powered and reachable at '{gateway}' and that " +
                  $"path '{path}' routes to the controller's CPU slot (e.g. '1,0' for slot 0).");
    }

    /// <summary>
    /// Builds a <see cref="VirtualTagService"/> seed dictionary from the
    /// controller-scoped tags in <paramref name="project"/>.
    /// Each tag is given a type-appropriate zero/default value:
    /// <list type="bullet">
    ///   <item><c>BOOL</c> → <c>false</c></item>
    ///   <item><c>SINT / INT / DINT / LINT</c> → <c>0</c> (<see cref="int"/>)</item>
    ///   <item><c>REAL / LREAL</c> → <c>0.0</c> (<see cref="double"/>)</item>
    ///   <item>Any other type (UDT, array, etc.) → <c>0</c> (<see cref="int"/>)</item>
    /// </list>
    /// </summary>
    /// <param name="project">Parsed L5X project providing the controller tags.</param>
    /// <returns>
    /// A case-insensitive dictionary keyed by tag name, ready to pass to
    /// <see cref="VirtualTagService(IReadOnlyDictionary{string, ValueTuple{object, string?}})"/>.
    /// </returns>
    public static IReadOnlyDictionary<string, (object Value, string? DataType)>
        BuildSeedFromProject(L5X project)
    {
        var seed = new Dictionary<string, (object Value, string? DataType)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var tag in project.Tags)
            seed[tag.Name] = (DefaultValueForType(tag.DataType), tag.DataType);

        return seed;
    }

    /// <summary>
    /// Returns a zero/default .NET value appropriate for <paramref name="dataType"/>.
    /// </summary>
    /// <param name="dataType">Logix data-type string (e.g. <c>"BOOL"</c>, <c>"REAL"</c>).</param>
    /// <returns>
    /// <c>false</c> for BOOL; <c>0.0</c> (<see cref="double"/>) for REAL/LREAL;
    /// <c>0</c> (<see cref="int"/>) for all other types.
    /// </returns>
    public static object DefaultValueForType(string dataType) =>
        dataType.ToUpperInvariant() switch
        {
            "BOOL"  => (object) false,
            "REAL"  => 0.0,
            "LREAL" => 0.0,
            _       => 0,
        };

    /// <summary>Every SDK restart a connect's upload saw: one per retry that saw one, plus the give-up crash.</summary>
    /// <param name="retries">The retries made.</param>
    /// <param name="giveUp">What the retry policy saw when it gave up.</param>
    /// <returns>The restart records in order.</returns>
    internal static List<SdkRestartRecord> AllUploadRestarts(
        IReadOnlyList<ConnectRetryEntry> retries, SdkConnectGiveUp giveUp)
    {
        var all = retries.Where(r => r.SdkRestart is not null).Select(r => r.SdkRestart!).ToList();
        if (giveUp.FinalRestart is not null) all.Add(giveUp.FinalRestart);
        return all;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> represents a Logix Designer SDK
    /// route error — specifically "No module was found using the supplied encoded route
    /// string." — which occurs when the comm path uses the wrong addressing scheme.
    ///
    /// <para>
    /// The SDK requires a Logix Designer comm path (e.g.
    /// <c>"EmulateEthernet\192.0.2.10"</c>) and rejects the CIP/libplctag
    /// backplane path form (e.g. <c>"192.0.2.10,1,0"</c>) with this error.
    /// </para>
    /// </summary>
    /// <param name="ex">The <see cref="TagServiceException"/> to inspect.</param>
    /// <returns>
    /// <c>true</c> when the error message contains route-error tokens;
    /// <c>false</c> otherwise.
    /// </returns>
    internal static bool IsRouteError(TagServiceException ex)
        => ex.Error.Message.Contains("No module was found", StringComparison.OrdinalIgnoreCase)
        || ex.Error.Message.Contains("encoded route", StringComparison.OrdinalIgnoreCase)
        || ex.Error.Message.Contains("encoded route string", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to extract a controller IP address from a Logix Designer SDK
    /// comm path or a bare CIP gateway string (the route-error
    /// discriminator; mirrors the <c>SdkConnectFactory.TryParseControllerIp</c>
    /// so the same parsing rules apply on the connect path even when the
    /// SDK adapter assembly — which is <c>#if LOGIX_SDK</c>-gated — is not loaded).
    ///
    /// <para>
    /// Recognised forms (case-insensitive prefix matching):
    /// <list type="bullet">
    ///   <item><c>"EmulateEthernet\&lt;ip&gt;"</c> — emulated controller.</item>
    ///   <item><c>"&lt;ip&gt;"</c> — bare IP address (real controller, direct).</item>
    ///   <item><c>"AB_ETHIP-1\&lt;ip&gt;"</c> — real controller via AB EtherNet/IP driver.</item>
    ///   <item>Any of the above with a trailing backplane route
    ///     (<c>"\Backplane\0"</c> etc.) or CIP comma route (<c>",1,0"</c> etc.).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> for any path that does not match a recognised form,
    /// including paths with an unrecognised protocol prefix.
    /// </para>
    /// </summary>
    /// <param name="commPath">
    /// The comm path string to parse (e.g. <c>"EmulateEthernet\192.0.2.10"</c>,
    /// <c>"192.0.2.10,1,0"</c>).
    /// </param>
    /// <returns>
    /// The extracted IP address string when a recognised form is matched;
    /// <c>null</c> otherwise.
    /// </returns>
    internal static string? TryParseControllerIp(string commPath)
    {
        if (string.IsNullOrWhiteSpace(commPath))
            return null;

        var path = commPath.Trim();

        // Strip trailing CIP comma route: ,<anything>
        // e.g. "198.51.100.1,1,0" → "198.51.100.1"
        var commaIdx = path.IndexOf(',');
        if (commaIdx >= 0)
            path = path[..commaIdx];

        // Strip trailing backplane route: \Backplane\<n>
        // e.g. "EmulateEthernet\198.51.100.1\Backplane\0" → "EmulateEthernet\198.51.100.1"
        var backplaneIdx = path.IndexOf(
            "\\Backplane\\", StringComparison.OrdinalIgnoreCase);
        if (backplaneIdx >= 0)
            path = path[..backplaneIdx];

        // Backslash-prefixed forms: "EmulateEthernet\<ip>" or "AB_ETHIP-1\<ip>"
        var backslashIdx = path.IndexOf('\\');
        if (backslashIdx >= 0)
        {
            var prefix = path[..backslashIdx];
            if (prefix.Equals("EmulateEthernet", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("AB_ETHIP-1", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(backslashIdx + 1)..];
            }
            else
            {
                // Unrecognised protocol prefix — cannot extract IP.
                return null;
            }
        }

        // Validate the remaining segment as an IP address.
        return System.Net.IPAddress.TryParse(path, out _) ? path : null;
    }

    /// <summary>
    /// Wraps the SDK service of an online session in a <see cref="RoutedTagService"/> that applies
    /// <paramref name="mode"/> (<c>LGX_TAG_ROUTE</c>). The CIP target is derived from the SDK comm path;
    /// when none can be derived, <c>auto</c> uses the SDK and <c>cip</c> fails with
    /// <see cref="TagServiceErrorKind.TagRouteUnavailable"/> on the first tag operation.
    /// </summary>
    /// <param name="sdk">The SDK-backed service.</param>
    /// <param name="commPath">The SDK comm path of the session.</param>
    /// <param name="typeProject">The uploaded project, used for CIP type resolution; may be <c>null</c>.</param>
    /// <param name="mode">The route setting.</param>
    /// <param name="connector">Test seam replacing the real CIP connect: (gateway, path, ct).</param>
    internal static RoutedTagService BuildRoutedService(
        ITagService sdk, string commPath, L5X? typeProject, TagRouteMode mode,
        Func<string, string, CancellationToken, Task<ITagService>>? connector = null,
        ControllerIdentity? sdkIdentity = null,
        Func<string, CancellationToken, Task<ControllerIdentity>>? identityReader = null)
    {
        if (!CipCommPath.TryParse(commPath, out var target, out var parseFailure))
            return new RoutedTagService(sdk, mode, null, parseFailure, aliasTypes: AliasTypesOf(sdk, typeProject));

        // Types for the CIP route come from the SDK session's own lookup (shared state, shared refresh);
        // a session whose service has none falls back to a fixed view of the uploaded project.
        var types = TagRouteResolver.Backend(sdk) as ISessionTypeSource
                    ?? (typeProject is null ? null : new StaticTypeSource(typeProject));
        connector      ??= (gw, p, token) => OpenCipRouteAsync(gw, p, types, token);
        identityReader ??= (gw, token) => DefaultCipIdentityReader(gw, target!.Path, token);
        string? note = null;
        string? identityCheck = null;

        // Same-controller check first: never open (or route to) a controller that is not verified
        // to be the one the SDK session is attached to.
        async Task<ITagService> VerifyThenConnect(CancellationToken token)
        {
            ControllerIdentity cipIdentity;
            try { cipIdentity = await identityReader(target!.Gateway, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"the controller identity could not be read over CIP, so the controller is unverified: {ex.Message}", ex);
            }

            if (!CipRouteVerifier.TryVerify(cipIdentity, sdkIdentity, out var verdict, out var method))
                throw new InvalidOperationException($"not the verified controller: {verdict}");

            var cip = await connector(target!.Gateway, target.Path, token).ConfigureAwait(false);
            note = $"{target.Gateway} path {target.Path}; {verdict}";
            identityCheck = method;
            return cip;
        }

        return new RoutedTagService(sdk, mode, VerifyThenConnect, connectedNote: () => note, identityCheck: () => identityCheck, aliasTypes: AliasTypesOf(sdk, typeProject));
    }

    private static ISessionTypeSource? AliasTypesOf(ITagService sdk, L5X? typeProject) =>
        TagRouteResolver.Backend(sdk) as ISessionTypeSource
        ?? (typeProject is null ? null : new StaticTypeSource(typeProject));

    /// <summary>CIP identity of <paramref name="gateway"/> from ListIdentity (serial) plus the controller name from a CIP Get_Attribute_List on class 0x64.</summary>
    private static async Task<ControllerIdentity> DefaultCipIdentityReader(string gateway, string path, CancellationToken ct)
    {
        var info = await EmulatorIdentity.ReadAsync(gateway, ct: ct).ConfigureAwait(false);
        // Name over the derived path (Get_Attribute_List class 0x64); null when that read fails.
        var name = await ControllerNameReader.ReadAsync(gateway, path, ct: ct).ConfigureAwait(false);
        return new ControllerIdentity(Name: name, Serial: info.SerialNumber);
    }

    /// <summary>
    /// CIP path for an SDK comm path: <c>1,&lt;slot&gt;</c> from <c>\Backplane\&lt;slot&gt;</c> or a
    /// trailing comma route; <c>1,0</c> otherwise.
    /// </summary>
    internal static string DeriveCipPath(string commPath) =>
        CipCommPath.TryParse(commPath, out var target, out _) ? target!.Path : "1,0";

    /// <summary>Opens and probes a CIP session for the tag route; types come from the SDK session's lookup, <paramref name="types"/>.</summary>
    private static async Task<ITagService> OpenCipRouteAsync(
        string gateway, string path, ISessionTypeSource? types, CancellationToken ct)
    {
        var cip = new LibPlcTagService();
        try
        {
            await cip.ConnectAsync(gateway, path, ct).ConfigureAwait(false);
            await RunCipProbeWithDeadlineAsync(DefaultCipConnectProbe, cip, DefaultCipProbeDeadline, ct)
                .ConfigureAwait(false);
            cip.TypeResolver = types is null
                ? new CipTypeResolver(null, cip.ListTagsAsync)
                : CipTypeResolver.ForSession(types, cip.ListTagsAsync);
            return cip;
        }
        catch
        {
            try { await cip.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Production implementation of the route-error TCP probe: opens a
    /// TCP connection to <paramref name="ip"/>:44818 (the standard EtherNet/IP
    /// port) with a 2-second timeout to determine whether the controller is
    /// reachable on the network.
    ///
    /// <para>
    /// Returns <c>true</c> when the three-way TCP handshake completes within
    /// 2 seconds; <c>false</c> on any failure (host unreachable, connection
    /// refused, timeout, cancellation, etc.).  The connection is closed
    /// immediately after a successful handshake — no EtherNet/IP traffic is
    /// sent.  Used only when the caller does not supply its own
    /// <c>controllerReachableProbe</c> delegate to <see cref="ExecuteAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="ip">IP address of the controller to probe.</param>
    /// <param name="ct">Upstream cancellation token.</param>
    private static async Task<bool> DefaultControllerReachableProbe(string ip, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(ip, 44818, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
