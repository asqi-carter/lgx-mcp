// ── DoctorHandler — logix_doctor ──────────────────────────────────
//
// Lives in the core assembly (logix_doctor is registered from there in every install
// state) and compiles in SDK and non-SDK builds: every fact about the machine comes through
// IDoctorEnvironment, so the ordered checks, statuses and fixes are testable on Linux.
// The handler is strictly read-only. IDoctorEnvironment has no member that connects a
// session, changes a mode, launches a process or writes a file; the only I/O it may
// perform is opening and closing a TCP connection to <ip>:44818.

using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>Check status values used by <c>logix_doctor</c>.</summary>
public static class DoctorStatus
{
    /// <summary>The check passed.</summary>
    public const string Ok = "ok";

    /// <summary>Something is degraded or suspicious but work may continue.</summary>
    public const string Warn = "warn";

    /// <summary>Something is broken and the named fix is needed.</summary>
    public const string Fail = "fail";

    /// <summary>The check does not apply here; <c>observed</c> says why.</summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// Informational only: a component this build does not use is absent (for example Studio or the
    /// SDK in the no-SDK build). Never raises the overall status and needs no fix.
    /// </summary>
    public const string Info = "info";

    /// <summary>Severity rank for the overall roll-up (skipped and info never raise it).</summary>
    internal static int Rank(string status) => status switch
    {
        Fail => 2,
        Warn => 1,
        _    => 0,
    };
}

/// <summary>Windows service states reported by <see cref="IDoctorEnvironment.GetServiceState"/>.</summary>
public static class DoctorServiceState
{
    /// <summary>The service is running.</summary>
    public const string Running = "Running";

    /// <summary>The service is not installed on this machine.</summary>
    public const string NotInstalled = "NotInstalled";

    /// <summary>The service control manager cannot be queried on this OS or build.</summary>
    public const string Unsupported = "Unsupported";
}

/// <summary>One entry of the <c>logix_doctor</c> result.</summary>
/// <param name="Id">Check id, e.g. <c>sdkService</c>.</param>
/// <param name="Status"><c>ok</c>, <c>warn</c>, <c>fail</c>, <c>skipped</c> or <c>info</c>.</param>
/// <param name="Observed">What the check saw (or, for <c>skipped</c>, why it does not apply).</param>
/// <param name="Fix">One sentence naming the exact next step; present on every warn and fail.</param>
/// <param name="Kind">Typed error kind for the Studio failures; omitted otherwise.</param>
public record DoctorCheck(
    [property: JsonPropertyName("id")]       string  Id,
    [property: JsonPropertyName("status")]   string  Status,
    [property: JsonPropertyName("observed")] string  Observed,
    [property: JsonPropertyName("fix"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Fix = null,
    [property: JsonPropertyName("kind"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Kind = null);

/// <summary>The <c>logix_doctor</c> result.</summary>
/// <param name="Overall">The worst status among the checks: <c>ok</c>, <c>warn</c> or <c>fail</c>.</param>
/// <param name="Checks">The checks, in the fixed order.</param>
/// <param name="Capabilities">The SDK capability table (<c>lgx://sdk/capabilities</c>); omitted when not reported.</param>
/// <param name="DesignerVersion">The Logix Designer version the active SDK session reports, or <c>unknown until connect</c>. Never the SDK package version.</param>
/// <param name="TargetMajorRevision">Major revision of the current target (session, else open project); <c>null</c> when unknown.</param>
/// <param name="MnemonicSet"><c>iec</c> (36 and above), <c>legacy</c> (35 and below) or <c>unknown</c> (no check applied).</param>
public record DoctorResult(
    [property: JsonPropertyName("overall")] string                   Overall,
    [property: JsonPropertyName("checks")]  IReadOnlyList<DoctorCheck> Checks,
    [property: JsonPropertyName("capabilities"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               SdkCapabilitiesReport? Capabilities = null,
    [property: JsonPropertyName("designerVersion")]
               string DesignerVersion = DoctorHandler.DesignerVersionUnknown,
    [property: JsonPropertyName("targetMajorRevision")] int? TargetMajorRevision = null,
    [property: JsonPropertyName("mnemonicSet")] string MnemonicSet = MnemonicSetNames.Unknown)
{
    /// <summary>Warnings about the target (an unknown revision applies no mnemonic check); omitted when none.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }

    /// <summary>
    /// Phase timings (ms) of the last timed connect this process made (<c>upload</c>, <c>saveL5x</c>,
    /// <c>parse</c>, <c>gate</c>, <c>identity</c>, <c>total</c>); omitted until one has run.
    /// </summary>
    [JsonPropertyName("lastConnect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectTimingsSnapshot? LastConnect { get; init; }
}

/// <summary>Snapshot of the active tag session, as the doctor reports it.</summary>
/// <param name="Backend"><c>sdk</c>, <c>cip</c>, <c>l5x</c> or <c>virtual</c>.</param>
/// <param name="Target"><c>online-controller</c> or <c>offline-project</c> (as <c>logix_connect</c> reports).</param>
/// <param name="Online">Whether the session is online.</param>
/// <param name="Correlation"><c>correlated</c> / <c>uncorrelated</c> for SDK sessions; null otherwise.</param>
/// <param name="TypeSnapshotAge">Age of the type snapshot; null when the backend keeps none.</param>
/// <param name="DesignerVersion">Logix Designer version (<c>major.minor</c>) with its source in parentheses; null when unknown or not an SDK session.</param>
/// <param name="TagRoute"><c>cip</c> or <c>sdk</c>: the route the session's tag reads and writes use (LGX_TAG_ROUTE); null when the session is not routed or no route has been chosen yet.</param>
/// <param name="TagRouteReason">Why <paramref name="TagRoute"/> is what it is (the setting, or why CIP was not used); null when not routed or not yet chosen.</param>
/// <param name="TagRouteSetting">The <c>LGX_TAG_ROUTE</c> setting the session applies (<c>auto</c>, <c>cip</c> or <c>sdk</c>); null when the session is not routed.</param>
/// <param name="IdentityCheck">How the CIP controller was verified (<c>serial</c>, <c>name</c> or <c>serial+name</c>); null unless the CIP route is active.</param>
/// <param name="Faulted">True when the SDK server faulted (<c>LgxSrv_E_FATAL_ERROR</c> / <c>LgxSrv_E_SERVER_FAULTED</c>) and the session is dead; the doctor reports <c>session: faulted</c>.</param>
public sealed record DoctorSessionInfo(
    string    Backend,
    string    Target,
    bool      Online,
    string?   Correlation,
    TimeSpan? TypeSnapshotAge,
    string?   DesignerVersion = null,
    string?   TagRoute = null,
    string?   TagRouteReason = null,
    string?   TagRouteSetting = null,
    string?   IdentityCheck = null,
    bool      Faulted = false);

/// <summary>Edit-zone state of one routine, from <c>logix_pending_edits</c>.</summary>
/// <param name="Program">Program name.</param>
/// <param name="Routine">Routine name.</param>
/// <param name="ZoneState"><c>none</c>, <c>untested</c>, <c>tested</c> or <c>unknown</c>.</param>
/// <param name="PendingTexts">
/// Texts of the rungs in the pending zone; <c>null</c> when the reader did not supply them.
/// </param>
public sealed record DoctorZone(
    string Program, string Routine, string ZoneState, IReadOnlyList<string>? PendingTexts = null);

/// <summary>
/// <c>LdSdkServer</c> restarts that fell inside a <c>logix_connect</c>, from the server's own connect log.
/// </summary>
/// <param name="Total">Number of restarts.</param>
/// <param name="ByPhase">Restarts per connect phase.</param>
public sealed record SdkConnectCrashes(int Total, IReadOnlyDictionary<string, int> ByPhase)
{
    /// <summary>Builds the counts from one phase name per crash.</summary>
    /// <param name="phases">The phase each crash hit.</param>
    public static SdkConnectCrashes From(IEnumerable<string> phases)
    {
        var byPhase = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var p in phases)
        {
            byPhase[p] = byPhase.GetValueOrDefault(p) + 1;
            total++;
        }
        return new SdkConnectCrashes(total, byPhase);
    }

    /// <summary>
    /// "LdSdkServer crashes inside a connect (last 24 h): 3 (upload 2, save-l5x 1)", phases by count then name;
    /// "... : 0" when none.
    /// </summary>
    public string Format() =>
        "LdSdkServer crashes inside a connect (last 24 h): " + Total +
        (Total == 0 ? "" : " (" + string.Join(", ",
            ByPhase.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                   .Select(kv => $"{kv.Key} {kv.Value}")) + ")");
}

/// <summary>
/// Read-only view of the machine and the server for <see cref="DoctorHandler"/>.
/// Deliberately has no member that connects, mutates, launches or writes.
/// </summary>
public interface IDoctorEnvironment
{
    /// <summary>Windows session id of this server process.</summary>
    int CurrentSessionId { get; }

    /// <summary>
    /// Raw value of <c>LGX_GATE_COMMAND_PREFIXES</c>, or null when unset. Read-only; the server check
    /// resolves it here rather than from the process environment so tests control it.
    /// </summary>
    string? GateConfigRaw();

    /// <summary>
    /// Raw value of <c>LGX_WRITE_ALLOWLIST</c> (the allowlist file path), or null when unset or blank. Read-only.
    /// </summary>
    string? WriteAllowlistRaw();

    /// <summary>Whether this server process is elevated.</summary>
    bool IsElevated { get; }

    /// <summary>Version of the loaded Rockwell SDK assembly, or null when it is not loaded.</summary>
    string? SdkAssemblyVersion { get; }

    /// <summary>
    /// Whether this build can use the Rockwell SDK and Studio at all. False in the no-SDK build, where
    /// a missing SDK or Studio is expected and is reported as <c>info</c>, not <c>warn</c> or <c>fail</c>.
    /// </summary>
    bool SdkSupportedInBuild => true;

    /// <summary>
    /// Why the SDK adapter is unavailable, when known: the directories the loader probed and why each
    /// was rejected, plus the recorded adapter load error.
    /// </summary>
    string? SdkLoadFailureReason { get; }

    /// <summary>
    /// Whether the CIP backend's native library (libplctag) loads on this machine. False when its
    /// native component cannot load, typically because the Visual C++ runtime is missing. Read-only.
    /// </summary>
    bool CipRuntimeLoads() => true;

    /// <summary>
    /// State of the named Windows service: the <c>ServiceControllerStatus</c> name, or
    /// <see cref="DoctorServiceState.NotInstalled"/> / <see cref="DoctorServiceState.Unsupported"/>.
    /// </summary>
    string GetServiceState(string serviceName);

    /// <summary>The last SDK service diagnosis made by this process, if any.</summary>
    SdkServiceDiagnosisSnapshot? LastSdkServiceDiagnosis { get; }

    /// <summary>
    /// Whether <paramref name="address"/> is assigned to one of this host's own network interfaces.
    /// Read-only. The default reports false.
    /// </summary>
    bool IsOwnInterfaceAddress(IPAddress address) => false;

    /// <summary>
    /// Number of <c>LdSdkServer.exe</c> crashes in the Windows Application event log (source <c>.NET Runtime</c> or
    /// <c>Application Error</c>) in the last 24 hours, or null when the log cannot be read (not Windows, no SDK
    /// build, access denied). The default reports null.
    /// </summary>
    int? SdkServerCrashesLast24h() => null;

    /// <summary>
    /// When the newest <c>LdSdkServer.exe</c> crash of the last 24 hours happened (UTC), or null when there was none or
    /// the log cannot be read. The default reports null.
    /// </summary>
    DateTimeOffset? NewestSdkServerCrashLast24h() => null;

    /// <summary>
    /// Number of <c>LdSdkService</c>/<c>LdSdkServer</c> crashes in the last 7 days: System log event 7031 for the
    /// service, plus the Application-log <c>LdSdkServer.exe</c> crash events. Null when the logs cannot be read.
    /// The default reports null.
    /// </summary>
    int? SdkServerCrashesLast7d() => null;

    /// <summary>Phase timings of the last timed <c>logix_connect</c> made by this process, or null when none.</summary>
    ConnectTimingsSnapshot? LastConnect => null;

    /// <summary>
    /// The <c>LdSdkServer</c> restarts that fell inside a <c>logix_connect</c> in the last 24 hours, by phase, read
    /// from this server's own audit (connect) log; null when the log cannot be read. The default reports null.
    /// </summary>
    SdkConnectCrashes? SdkCrashesDuringConnect24h => null;

    /// <summary>Directory holding <c>audit.jsonl</c>.</summary>
    string AuditDirectory { get; }

    /// <summary>Whether the audit directory accepts writes (probed without writing).</summary>
    bool IsAuditDirectoryWritable();

    /// <summary>Current size of <c>audit.jsonl</c> in bytes; null when it does not exist.</summary>
    long? AuditLogBytes();

    /// <summary>Opens and closes a TCP connection; true when the handshake finished within <paramref name="timeout"/>.</summary>
    Task<bool> TcpConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Reads the raw controller fault record (CIP class 0x73 instance 1, Get_Attributes_All) from
    /// <paramref name="host"/> — the same reader <c>logix_diagnose</c> uses. Read-only. Throws
    /// <see cref="FaultRecordException"/> when the record cannot be read.
    /// </summary>
    Task<byte[]> ReadFaultRecordAsync(string host, CancellationToken ct);

    /// <summary>The active session, or null. Never creates one.</summary>
    DoctorSessionInfo? GetActiveSession();

    /// <summary>
    /// Major revision and mnemonic set of the current target (the session's, else the open project's).
    /// The default reports no target.
    /// </summary>
    TargetRevisionInfo GetTargetRevision() => TargetRevision.NoTarget;

    /// <summary>Reads zone state per routine from the active online session (read-only). Throws on failure.</summary>
    Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(CancellationToken ct);

    /// <summary>
    /// The SDK capability table: the active SDK session's, or what the install alone shows when there is none.
    /// The default knows only whether the SDK is loaded and its package version.
    /// </summary>
    SdkCapabilitiesReport GetSdkCapabilities() =>
        SdkCapabilities.NoSession(SdkSupportedInBuild && SdkAssemblyVersion is not null, SdkAssemblyVersion);

    /// <summary>Session ids of all running <c>LogixDesigner</c> processes.</summary>
    IReadOnlyList<int> StudioSessionIds { get; }

    /// <summary>Whether a Studio process in this session outranks this server's integrity level.</summary>
    bool StudioIntegrityHigherThanCurrent { get; }

    /// <summary>Main-window titles of the <c>LogixDesigner</c> processes in this server's session.</summary>
    IReadOnlyList<string> StudioWindowTitlesInCurrentSession { get; }
}

/// <summary>
/// Pure handler for the <c>logix_doctor</c> MCP tool. Runs the eleven checks in
/// order and returns <see cref="DoctorResult"/>. Never throws (except cancellation) and never
/// mutates anything: it advises, it does not repair.
/// </summary>
public static class DoctorHandler
{
    /// <summary>The Logix SDK Windows service.</summary>
    public const string SdkServiceName = "LdSdkService";

    /// <summary>The Echo emulator Windows service.</summary>
    public const string EchoServiceName = "FactoryTalk Logix Echo Service";

    /// <summary>The Echo message broker Windows service.</summary>
    public const string EchoBrokerName = "FactoryTalk Logix Echo Message Broker";

    /// <summary>EtherNet/IP explicit-messaging TCP port.</summary>
    public const int EtherNetIpPort = 44818;

    /// <summary>TCP connect timeout for <c>controllerReachable</c>.</summary>
    public static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The <c>designerVersion</c> value when no session has reported a Designer version.</summary>
    public const string DesignerVersionUnknown = "unknown until connect";

    /// <summary>The check ids, in execution order.</summary>
    public static readonly IReadOnlyList<string> CheckIds =
    [
        "server", "audit", "sdkInstalled", "sdkService", "cipRuntime", "echoService",
        "controllerReachable", "controllerFault", "session", "pendingZone", "studio",
    ];

    private static readonly Regex Ipv4Regex =
        new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

    /// <summary>
    /// Runs every check, in order.
    /// </summary>
    /// <param name="commPath">Optional CIP/SDK comm path the caller intends to use.</param>
    /// <param name="controllerIp">Optional controller IP; overrides the IP parsed from <paramref name="commPath"/>.</param>
    /// <param name="env">Read-only view of the machine.</param>
    /// <param name="capabilities">The server's capability flags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The overall status and one <see cref="DoctorCheck"/> per check.</returns>
    public static async Task<DoctorResult> ExecuteAsync(
        string? commPath,
        string? controllerIp,
        IDoctorEnvironment env,
        Capabilities capabilities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(capabilities);

        commPath     = string.IsNullOrWhiteSpace(commPath) ? null : commPath.Trim();
        controllerIp = string.IsNullOrWhiteSpace(controllerIp) ? null : controllerIp.Trim();

        var reachable = await GuardAsync("controllerReachable", () => CheckControllerReachableAsync(env, commPath, controllerIp, ct));
        var checks = new List<DoctorCheck>
        {
            Guard("server",       () => CheckServer(env, capabilities)),
            Guard("audit",        () => CheckAudit(env)),
            Guard("sdkInstalled", () => CheckSdkInstalled(env)),
            Guard("sdkService",   () => CheckSdkService(env)),
            Guard("cipRuntime",   () => CheckCipRuntime(env)),
            Guard("echoService",  () => CheckEchoService(env, commPath, controllerIp)),
            reachable,
            await GuardAsync("controllerFault", () => CheckControllerFaultAsync(env, commPath, controllerIp, reachable, ct)),
            Guard("session",      () => CheckSession(env, commPath)),
            await GuardAsync("pendingZone", () => CheckPendingZoneAsync(env, ct)),
            Guard("studio",       () => CheckStudio(env, capabilities)),
        };

        var worst = checks.Max(c => DoctorStatus.Rank(c.Status));
        var overall = worst switch { 2 => DoctorStatus.Fail, 1 => DoctorStatus.Warn, _ => DoctorStatus.Ok };
        SdkCapabilitiesReport? table = null;
        try { table = env.GetSdkCapabilities(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the table is advisory; never fail the doctor over it */ }
        var target = ResolveTargetRevision(env);
        return new DoctorResult(overall, checks, table, ResolveDesignerVersion(env),
            target.MajorRevision, target.MnemonicSet)
        {
            Warnings = target.Warning is { } warning ? [warning] : null,
            LastConnect = ResolveLastConnect(env),
        };
    }

    /// <summary>The last connect's phase timings; a failing read reports none rather than failing the doctor.</summary>
    private static ConnectTimingsSnapshot? ResolveLastConnect(IDoctorEnvironment env)
    {
        try { return env.LastConnect; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>The current target's revision; a failing read reports no target rather than failing the doctor.</summary>
    private static TargetRevisionInfo ResolveTargetRevision(IDoctorEnvironment env)
    {
        try { return env.GetTargetRevision(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return TargetRevision.NoTarget; }
    }

    /// <summary>
    /// The Designer version the active session reports, else <see cref="DesignerVersionUnknown"/>.
    /// Reads only the session's own value; <see cref="IDoctorEnvironment.SdkAssemblyVersion"/> is the SDK
    /// package version and is never used here.
    /// </summary>
    private static string ResolveDesignerVersion(IDoctorEnvironment env)
    {
        try
        {
            var v = env.GetActiveSession()?.DesignerVersion;
            return string.IsNullOrWhiteSpace(v) ? DesignerVersionUnknown : v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DesignerVersionUnknown;
        }
    }

    // ── Guards: a probe that throws becomes a warn, never an escaped exception ─

    private static DoctorCheck Guard(string id, Func<DoctorCheck> check)
    {
        try { return check(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Crashed(id, ex); }
    }

    private static async Task<DoctorCheck> GuardAsync(string id, Func<Task<DoctorCheck>> check)
    {
        try { return await check().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Crashed(id, ex); }
    }

    private static DoctorCheck Crashed(string id, Exception ex) =>
        new(id, DoctorStatus.Warn,
            $"the check could not complete: {ex.GetType().Name}: {ex.Message}",
            "Run the doctor again, and if the same exception repeats report its text to the maintainers.");

    // ── 1. server ─────────────────────────────────────────────────────────────

    private static DoctorCheck CheckServer(IDoctorEnvironment env, Capabilities caps)
    {
        // Process-state reads go through env, so the check is deterministic under a fake environment.
        var ping = PingHandler.Execute(caps with { WriteAllowlistPath = env.WriteAllowlistRaw() });
        var c = ping.Capabilities!;
        string Flag(bool on) => on ? "on" : "off";
        // The allowlist file, read by the same code connect uses (WriteAllowlist.Load). Connect fails closed
        // on an unreadable or invalid file, so the doctor reports fail rather than 'active'.
        var badAllowlist = c.WriteAllowlistActive
            ? WriteAllowlist.Load(c.WriteAllowlistPath!) as AllowlistInvalidError
            : null;
        var allowlistText = badAllowlist is not null
            ? $"fail ({badAllowlist.Path}): {badAllowlist.Error}"
            : c.WriteAllowlistActive ? $"active ({c.WriteAllowlistPath})" : "off";
        var observed =
            $"{ping.Server} {ping.Version}; OS session {env.CurrentSessionId}; " +
            $"elevated {(env.IsElevated ? "yes" : "no")}; " +
            $"{Capabilities.EnvVar}={Flag(c.LiveWrites)}; " +
            $"{Capabilities.EnvVarStudio}={Flag(c.StudioAutomation)}; " +
            $"{Capabilities.EnvVarLiveFinalize}={Flag(c.LiveFinalize)}; " +
            $"{Capabilities.EnvVarPublishMode}={c.PublishMode}; " +
            $"{WriteAllowlist.EnvVar}={allowlistText}";

        if (badAllowlist is not null)
            return new DoctorCheck("server", DoctorStatus.Fail, observed, badAllowlist.Hint);

        // Effective command prefixes (LGX_GATE_COMMAND_PREFIXES): the list, or '*'.
        var resolved = GateConfigResolver.Resolve(env.GateConfigRaw());
        if (resolved is GateConfigInvalidError badConfig)
            return new DoctorCheck("server", DoctorStatus.Fail,
                observed + $"; {GateConfigResolver.EnvVar} invalid: '{badConfig.Entry}'",
                badConfig.Hint);

        var gate = (GateConfig)resolved;
        observed += $"; {GateConfigResolver.EnvVar}={gate.CommandPrefixesText} ({gate.CommandPrefixesSource})";
        return new DoctorCheck("server", DoctorStatus.Ok, observed);
    }

    // ── 2. audit ──────────────────────────────────────────────────────────────

    private static DoctorCheck CheckAudit(IDoctorEnvironment env)
    {
        var dir      = env.AuditDirectory;
        var writable = env.IsAuditDirectoryWritable();
        var bytes    = env.AuditLogBytes();
        var maxMb    = AuditLogger.MaxMb;

        var sizeText = bytes is null ? "audit.jsonl not created yet" : $"audit.jsonl {FormatMb(bytes.Value)}";
        var limit    = maxMb <= 0 ? "rotation off (LGX_AUDIT_MAX_MB=0)" : $"limit {maxMb:0.##} MB (LGX_AUDIT_MAX_MB)";
        var failures = AuditLogger.WriteFailures;
        var observed = $"{dir} {(writable ? "writable" : "NOT writable")}; {sizeText}; {limit}; " +
                       $"auditWriteFailures={failures}" +
                       (AuditLogger.FailClosed ? $"; {AuditLogger.FailClosedEnvVar}=1" : "");

        if (!writable)
            return new DoctorCheck("audit", DoctorStatus.Warn, observed,
                $"Make '{dir}' writable by the server's account so writes and publishes are audited.");

        if (bytes is not null && maxMb > 0 && bytes.Value > (long)(maxMb * 1024 * 1024))
            return new DoctorCheck("audit", DoctorStatus.Warn, observed,
                "Check that nothing else holds audit.jsonl open, because rotation at LGX_AUDIT_MAX_MB is not happening.");

        if (failures > 0)
            return new DoctorCheck("audit", DoctorStatus.Warn, observed,
                $"{failures} audit record(s) could not be written (last: {AuditLogger.LastWriteFailure}); " +
                "fix the audit directory, because those events are missing from audit.jsonl.");

        return new DoctorCheck("audit", DoctorStatus.Ok, observed);
    }

    private static string FormatMb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.##} MB";

    // ── 3. sdkInstalled ───────────────────────────────────────────────────────

    private const string NotUsedByThisBuild = "not used by this build";

    private static DoctorCheck CheckSdkInstalled(IDoctorEnvironment env)
    {
        var version = env.SdkAssemblyVersion;
        if (version is not null)
            return new DoctorCheck("sdkInstalled", DoctorStatus.Ok, $"Logix Designer SDK assembly {version} is loaded");

        if (!env.SdkSupportedInBuild)
            return new DoctorCheck("sdkInstalled", DoctorStatus.Info, NotUsedByThisBuild);

        var why = string.IsNullOrWhiteSpace(env.SdkLoadFailureReason) ? "" : $" ({env.SdkLoadFailureReason})";
        return new DoctorCheck("sdkInstalled", DoctorStatus.Warn,
            $"the Logix Designer SDK assembly is not loaded{why}; backend=\"sdk\" is unavailable",
            "Install Studio 5000 Logix Designer or set LOGIX_SDK_PATH to its SDK directory, then restart the MCP server.");
    }

    /// <summary>
    /// Skip text when the Windows service control manager cannot be queried: names the build
    /// (no SDK adapter loaded) before the OS (not Windows), so the two causes are not conflated.
    /// </summary>
    private static string ServiceSkipText(IDoctorEnvironment env)
    {
        if (env.SdkAssemblyVersion is null)
            return "not checked: this build has no Logix Designer SDK support";
        if (!OperatingSystem.IsWindows())
            return "not checked: not Windows";
        return "the Windows service control manager is not available";
    }

    // ── 4. sdkService ─────────────────────────────────────────────────────────

    private const string SdkServiceFix =
        "Restart the LdSdkService Windows service, then logix_connect again.";

    /// <summary>Hint added to the sdkService check when LdSdkServer.exe crashed in the last 24 h.</summary>
    internal const string SdkServerCrashHint =
        "LdSdkServer.exe crashes are a known issue: logix_connect retries up to twice while the service " +
        "restarts; if connects keep failing, check the Application event log for the LdSdkServer.exe entries.";

    /// <summary>
    /// Added after <see cref="SdkServerCrashHint"/> only when this server is not elevated: the known cause that
    /// elevation avoids.
    /// </summary>
    internal const string SdkServerCrashElevationHint =
        "A non-elevated session-1 connect crashes LdSdkServer with an IOException on its temp .acd at " +
        "ReceivedFile.Dispose(); run the MCP client elevated to avoid it.";

    /// <summary>
    /// The sdkService check plus the 24 h LdSdkServer crash count: a count above 0 is appended to the observation,
    /// raises an ok check to warn, and adds <see cref="SdkServerCrashHint"/> to the fix. A count that cannot be read
    /// changes nothing.
    /// </summary>
    private static DoctorCheck CheckSdkService(IDoctorEnvironment env)
    {
        var check = CheckSdkServiceState(env);
        if (check.Status is DoctorStatus.Info or DoctorStatus.Skipped) return check;

        int? crashes;
        try { crashes = env.SdkServerCrashesLast24h(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { crashes = null; }
        if (crashes is null)
        {
            // The event log cannot be read (not Windows, access denied); the connect log still can.
            var inConnectOnly = FormatInConnectCrashes(env);
            return inConnectOnly.Length == 0 ? check : check with { Observed = check.Observed + inConnectOnly };
        }

        var observed = $"{check.Observed}; LdSdkServer.exe crashes in the last 24 h: {crashes}";
        DateTimeOffset? newest = null;
        if (crashes > 0)
        {
            try { newest = env.NewestSdkServerCrashLast24h(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { newest = null; }
        }
        if (newest is { } at)
            observed += $" (newest {at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)})";
        observed += FormatSevenDayCount(env);
        observed += FormatInConnectCrashes(env);
        if (crashes > SdkCrashNoteThreshold24h) observed += $". {SdkServerCrashFrequentNote}";

        if (crashes <= 0) return check with { Observed = observed };

        var hint = env.IsElevated ? SdkServerCrashHint : $"{SdkServerCrashHint} {SdkServerCrashElevationHint}";
        var fix = check.Fix is null ? hint : $"{check.Fix} {hint}";
        var status = check.Status == DoctorStatus.Ok ? DoctorStatus.Warn : check.Status;
        return check with { Status = status, Observed = observed, Fix = fix };
    }

    /// <summary>Crashes in 24 h above which the sdkService check adds <see cref="SdkServerCrashFrequentNote"/>.</summary>
    internal const int SdkCrashNoteThreshold24h = 10;

    /// <summary>Note added to the sdkService fix when more than <see cref="SdkCrashNoteThreshold24h"/> crashes happened in 24 h.</summary>
    internal const string SdkServerCrashFrequentNote =
        "Note: more than 10 LdSdkService/LdSdkServer crashes in 24 h; live sessions are repeatedly lost when the SDK server restarts " +
        "(SessionLost, cause sdk-service-restarted), so reconnect after each one and look for the trigger.";

    /// <summary>
    /// "; in a connect: 3 (upload 2, save-l5x 1)" from the server's connect log over the last 24 h; empty when the
    /// log cannot be read. Shown even at zero so a clean log reads as clean.
    /// </summary>
    internal static string FormatInConnectCrashes(IDoctorEnvironment env)
    {
        SdkConnectCrashes? inConnect;
        try { inConnect = env.SdkCrashesDuringConnect24h; }
        catch (Exception ex) when (ex is not OperationCanceledException) { inConnect = null; }
        return inConnect is null ? "" : $"; {inConnect.Format()}";
    }

    private static string FormatSevenDayCount(IDoctorEnvironment env)
    {
        int? week;
        try { week = env.SdkServerCrashesLast7d(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { week = null; }
        return week is null ? "" : $"; last 7 d: {week}";
    }

    private static DoctorCheck CheckSdkServiceState(IDoctorEnvironment env)
    {
        if (!env.SdkSupportedInBuild)
            return new DoctorCheck("sdkService", DoctorStatus.Info, NotUsedByThisBuild);

        var state = env.GetServiceState(SdkServiceName);
        if (state == DoctorServiceState.Unsupported)
            return new DoctorCheck("sdkService", DoctorStatus.Skipped,
                ServiceSkipText(env));

        if (state == DoctorServiceState.NotInstalled)
            return new DoctorCheck("sdkService", DoctorStatus.Fail,
                $"{SdkServiceName} is not installed",
                "Install Studio 5000 Logix Designer, which installs LdSdkService, then run the doctor again.");

        if (state != DoctorServiceState.Running)
            return new DoctorCheck("sdkService", DoctorStatus.Fail,
                $"{SdkServiceName} is {state}", SdkServiceFix);

        var diag = env.LastSdkServiceDiagnosis;
        if (diag is { Kind: "SdkServiceDegraded" })
            return new DoctorCheck("sdkService", DoctorStatus.Warn,
                $"{SdkServiceName} is Running, but this process diagnosed SdkServiceDegraded at " +
                $"{diag.At:u} (controller probe {diag.Probe}): {Truncate(diag.SdkCode, 160)}",
                SdkServiceFix);

        var last = diag is null ? "no SDK diagnosis recorded in this process" : $"last diagnosis {diag.Kind} at {diag.At:u}";
        return new DoctorCheck("sdkService", DoctorStatus.Ok, $"{SdkServiceName} is Running; {last}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // ── 4b. cipRuntime ─────────────────────────────────────────────

    private static DoctorCheck CheckCipRuntime(IDoctorEnvironment env)
    {
        if (env.CipRuntimeLoads())
            return new DoctorCheck("cipRuntime", DoctorStatus.Ok, "the CIP library's native component (libplctag) loads");

        return new DoctorCheck("cipRuntime", DoctorStatus.Fail,
            LibPlcTagService.CipRuntimeMissingMessage,
            LibPlcTagService.CipRuntimeMissingHint,
            "CipRuntimeMissing");
    }

    // ── 5. echoService ────────────────────────────────────────────────────────

    /// <summary>
    /// True when the target is a FactoryTalk Logix Echo emulator. A comm path starting with
    /// <c>EmulateEthernet</c> is always an Echo target (so a missing install is still reported). Otherwise
    /// the controller IP (explicit, else parsed from the comm path) must be on this host
    /// (<c>localhost</c>, any IPv4 address in 127.0.0.0/8, <c>::1</c>, or one of the host's own interface
    /// addresses) AND the Echo service must be installed.
    /// </summary>
    /// <param name="env">The environment used to query the Echo service and the host's own addresses.</param>
    /// <param name="commPath">The comm path, or null.</param>
    /// <param name="controllerIp">The explicit controller IP or host name, or null.</param>
    /// <returns>True when the echoService check applies.</returns>
    public static bool IsEchoTarget(IDoctorEnvironment env, string? commPath, string? controllerIp)
    {
        if (commPath?.TrimStart().StartsWith("EmulateEthernet", StringComparison.OrdinalIgnoreCase) ?? false)
            return true;

        var text = controllerIp?.Trim();
        var isLocalhost = string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase);
        IPAddress? addr = null;
        if (!isLocalhost)
        {
            var ip = controllerIp is not null ? NormaliseIp(controllerIp) : ParseIpFromCommPath(commPath);
            if (ip is null || !IPAddress.TryParse(ip, out addr))
                return false;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        }

        var onThisHost = isLocalhost || IPAddress.IsLoopback(addr!) || env.IsOwnInterfaceAddress(addr!);
        return onThisHost && env.GetServiceState(EchoServiceName) != DoctorServiceState.NotInstalled;
    }

    private static DoctorCheck CheckEchoService(IDoctorEnvironment env, string? commPath, string? controllerIp)
    {
        var ip = controllerIp ?? ParseIpFromCommPath(commPath);
        if (!IsEchoTarget(env, commPath, controllerIp))
            return new DoctorCheck("echoService", DoctorStatus.Skipped,
                "the target is not an Echo emulator (commPath does not start with EmulateEthernet, or the controller is not this host - loopback, localhost or one of its own addresses - with the Echo service installed)");

        var echo   = env.GetServiceState(EchoServiceName);
        var broker = env.GetServiceState(EchoBrokerName);
        if (echo == DoctorServiceState.Unsupported && broker == DoctorServiceState.Unsupported)
            return new DoctorCheck("echoService", DoctorStatus.Skipped,
                ServiceSkipText(env));

        var observed = $"{EchoServiceName} is {echo}; {EchoBrokerName} is {broker}";
        if (echo == DoctorServiceState.NotInstalled)
            return new DoctorCheck("echoService", DoctorStatus.Fail, observed,
                "Install FactoryTalk Logix Echo, then run the doctor again.");
        if (echo != DoctorServiceState.Running)
            return new DoctorCheck("echoService", DoctorStatus.Fail, observed,
                $"Start-Service '{EchoServiceName}'");
        if (broker != DoctorServiceState.Running)
            return new DoctorCheck("echoService", DoctorStatus.Warn, observed,
                $"Start-Service '{EchoBrokerName}'");
        return new DoctorCheck("echoService", DoctorStatus.Ok, observed);
    }

    // ── 6. controllerReachable ────────────────────────────────────────────────

    private static async Task<DoctorCheck> CheckControllerReachableAsync(
        IDoctorEnvironment env, string? commPath, string? controllerIp, CancellationToken ct)
    {
        string? ip;
        if (controllerIp is not null)
        {
            ip = NormaliseIp(controllerIp);
            if (ip is null)
                return new DoctorCheck("controllerReachable", DoctorStatus.Fail,
                    $"controllerIp '{controllerIp}' is not an IP address, so port {EtherNetIpPort} was not probed",
                    "Pass controllerIp as a dotted IPv4 address such as 10.0.0.10 and run the doctor again.");
        }
        else
        {
            ip = ParseIpFromCommPath(commPath);
        }

        if (ip is null)
            return new DoctorCheck("controllerReachable", DoctorStatus.Skipped,
                "no controllerIp was given and none could be parsed from commPath");

        var reached = await env.TcpConnectAsync(ip, EtherNetIpPort, ReachabilityTimeout, ct).ConfigureAwait(false);
        if (reached)
            return new DoctorCheck("controllerReachable", DoctorStatus.Ok,
                $"TCP connect to {ip}:{EtherNetIpPort} succeeded");

        return new DoctorCheck("controllerReachable", DoctorStatus.Fail,
            $"no TCP connection to {ip}:{EtherNetIpPort} within {ReachabilityTimeout.TotalSeconds:0} s",
            $"Check that the controller at {ip} is powered and that port {EtherNetIpPort} is reachable from this machine " +
            "(IP address, route, firewall), then run the doctor again.");
    }

    /// <summary>
    /// First dotted-IPv4 segment of a comm path such as <c>EmulateEthernet\127.0.0.1</c> or
    /// <c>AB_ETHIP-1\10.0.0.10\Backplane\0</c>; null when there is none.
    /// </summary>
    /// <param name="commPath">The comm path, or null.</param>
    /// <returns>The IP address text, or null.</returns>
    public static string? ParseIpFromCommPath(string? commPath)
    {
        if (string.IsNullOrWhiteSpace(commPath)) return null;
        foreach (var seg in commPath.Split('\\', '/', ',', ' '))
            if (NormaliseIp(seg) is { } ip && ip.Contains('.'))
                return ip;
        return null;
    }

    private static string? NormaliseIp(string text)
    {
        text = text.Trim();
        // "localhost" is the IPv4 loopback for the probes (the reachability TCP connect and the
        // controller fault read); the host name itself is not resolved through DNS.
        if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase)) return "127.0.0.1";
        if (Ipv4Regex.IsMatch(text) && IPAddress.TryParse(text, out var v4)) return v4.ToString();
        if (text.Contains(':') && IPAddress.TryParse(text, out var v6)) return v6.ToString();
        return null;
    }

    // ── 7. controllerFault ─────────────────────────────────────────

    private static async Task<DoctorCheck> CheckControllerFaultAsync(
        IDoctorEnvironment env, string? commPath, string? controllerIp, DoctorCheck reachable, CancellationToken ct)
    {
        var ip = controllerIp is not null ? NormaliseIp(controllerIp) : ParseIpFromCommPath(commPath);
        if (ip is null)
            return new DoctorCheck("controllerFault", DoctorStatus.Skipped,
                "no controllerIp was given and none could be parsed from commPath");
        if (reachable.Status != DoctorStatus.Ok)
            return new DoctorCheck("controllerFault", DoctorStatus.Skipped,
                $"the controller at {ip} is not reachable on port {EtherNetIpPort}, so the fault record was not read");

        byte[] record;
        MajorFault? fault;
        try
        {
            record = await env.ReadFaultRecordAsync(ip, ct).ConfigureAwait(false);
            fault  = FaultRecordDecoder.Decode(record);
        }
        catch (FaultRecordException ex)
        {
            return new DoctorCheck("controllerFault", DoctorStatus.Warn,
                $"the controller fault record at {ip} could not be read: {ex.Message}",
                "Run logix_diagnose to see the exact error, then run the doctor again.");
        }

        if (fault is null)
            return new DoctorCheck("controllerFault", DoctorStatus.Ok, $"no major fault recorded on the controller at {ip}");

        if (fault.IsUnparsed)
            return new DoctorCheck("controllerFault", DoctorStatus.Warn,
                $"the controller fault record at {ip} was too short to parse ({fault.Meaning}) raw: {fault.Raw}",
                "Run logix_diagnose to see the raw record, then run the doctor again.");

        return new DoctorCheck("controllerFault", DoctorStatus.Fail,
            $"controller FAULTED: major fault type {fault.Type} code {fault.Code} ({fault.Name}): {fault.Meaning}",
            "Call logix_diagnose for the suspect rungs. " + DiagnoseHandler.FixText);
    }

    // ── 8. session ────────────────────────────────────────────────────────────

    private static DoctorCheck CheckSession(IDoctorEnvironment env, string? commPath)
    {
        var s = env.GetActiveSession();
        if (s is null)
            return new DoctorCheck("session", DoctorStatus.Skipped,
                "no active session; logix_connect creates one and the doctor never does");

        if (s.Faulted)
            return new DoctorCheck("session", DoctorStatus.Fail,
                $"session: {ErrorKinds.SessionFaulted}; the SDK server (Logix Services) hit a fatal error " +
                $"(LgxSrv_E_FATAL_ERROR / LgxSrv_E_SERVER_FAULTED), so every SDK call on this session returns {ErrorKinds.SdkServerFaulted} " +
                $"without reaching the SDK; backend {s.Backend}; target {s.Target}",
                RockwellErrorHints.ServerFaultedHint,
                ErrorKinds.SdkServerFaulted);

        var age = s.TypeSnapshotAge is { } a
            ? $"type snapshot {Math.Max(0, (long)a.TotalSeconds)} s old"
            : "no type snapshot for this backend";
        var observed =
            $"backend {s.Backend}; target {s.Target}; online {(s.Online ? "yes" : "no")}; " +
            $"correlation {s.Correlation ?? "n/a"}; {age}" +
            (s.DesignerVersion is null ? string.Empty : $"; Logix Designer {s.DesignerVersion}") +
            DescribeTagRoute(s);

        if (s.Target == "offline-project" && commPath is not null)
            return new DoctorCheck("session", DoctorStatus.Warn, observed,
#if LOGIX_SDK
                "Use sdkMode=\"online\" with sdkCommPath on logix_connect, because this session is the offline project and a commPath was given.");
#else
                "Call logix_connect with backend=\"cip\" and the controller's gateway and path, because this session is the offline project and a commPath was given.");
#endif

#if LOGIX_SDK
        if (s.Backend == "sdk" && s.Online && s.Correlation == "uncorrelated")
            return new DoctorCheck("session", DoctorStatus.Warn, observed,
                "Call logix_connect again with sdkMode=\"online\" and sdkCommPath so the project is uploaded and correlated.");
#endif

        return new DoctorCheck("session", DoctorStatus.Ok, observed);
    }

    /// <summary>
    /// The "; tag route ..." clause of the session check: the active route and why. Empty when the session
    /// is not routed. Read-only: when no route has been chosen yet it says so and never chooses one.
    /// </summary>
    private static string DescribeTagRoute(DoctorSessionInfo s)
    {
        if (s.TagRouteSetting is null && s.TagRoute is null) return string.Empty;
        var setting = s.TagRouteSetting is null ? string.Empty : $" ({TagRouteResolver.EnvVar}={s.TagRouteSetting})";
        if (s.TagRoute is null)
            return $"; tag route not chosen yet{setting}";
        return $"; tag route {s.TagRoute}{setting}" +
               (string.IsNullOrWhiteSpace(s.TagRouteReason) ? string.Empty : $", because {s.TagRouteReason}") +
               (string.IsNullOrWhiteSpace(s.IdentityCheck) ? string.Empty : $"; identityCheck {s.IdentityCheck}");
    }

    // ── 9. pendingZone ────────────────────────────────────────────────────────

    private static async Task<DoctorCheck> CheckPendingZoneAsync(IDoctorEnvironment env, CancellationToken ct)
    {
        var s = env.GetActiveSession();
        if (s is null)
            return new DoctorCheck("pendingZone", DoctorStatus.Skipped, "no active session");
        if (s.Faulted)
            return new DoctorCheck("pendingZone", DoctorStatus.Skipped,
                $"session: {ErrorKinds.SessionFaulted}; the SDK was not called");
        if (!s.Online || s.Backend != "sdk")
            return new DoctorCheck("pendingZone", DoctorStatus.Skipped,
                $"the session is not an online SDK session (backend {s.Backend}, target {s.Target})");

        IReadOnlyList<DoctorZone> zones;
        try
        {
            zones = await env.ReadPendingZonesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DoctorCheck("pendingZone", DoctorStatus.Warn,
                $"zone state could not be read: {ex.Message}",
                "Call logix_pending_edits to see the exact error, then run the doctor again.");
        }

        var open = zones.Where(z => !string.Equals(z.ZoneState, "none", StringComparison.OrdinalIgnoreCase)).ToList();
        if (open.Count == 0)
        {
            var programs = zones.Select(z => z.Program).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new DoctorCheck("pendingZone", DoctorStatus.Ok,
                $"no pending edit zones in {programs} program(s)");
        }

        var names = open.Select(z => z.Program).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var observed = "pending edit zone in " + string.Join("; ", open.Select(z =>
            $"program '{z.Program}' routine '{z.Routine}' (zoneState {z.ZoneState})"));
        var fix = names.Count == 1
            ? $"The SDK refuses imports into '{names[0]}' until it is accepted or cancelled in Studio."
            : $"The SDK refuses imports into {string.Join(", ", names.Select(n => $"'{n}'"))} until each is accepted or cancelled in Studio.";
        return new DoctorCheck("pendingZone", DoctorStatus.Warn, observed, fix);
    }

    // ── 10. studio ─────────────────────────────────────────────────────────────

    private static DoctorCheck CheckStudio(IDoctorEnvironment env, Capabilities caps)
    {
        if (!caps.StudioAutomationEnabled)
            return new DoctorCheck("studio", DoctorStatus.Skipped,
                $"{Capabilities.EnvVarStudio} is not 1, so Studio automation is off");

        if (!env.SdkSupportedInBuild)
            return new DoctorCheck("studio", DoctorStatus.Info, NotUsedByThisBuild);

        var sessionId = env.CurrentSessionId;
        var ids       = env.StudioSessionIds;

        var preLaunch = StudioPreLaunch.CheckStudioInThisSession(sessionId, ids);
        switch (StudioPreLaunch.PreLaunchKindOf(preLaunch))
        {
            case StudioKinds.StudioNotRunningKind:
                return new DoctorCheck("studio", DoctorStatus.Fail,
                    "no LogixDesigner process is running in any session",
                    "Ask the engineer to open the project in Studio and go online by Upload; lgx-mcp never launches Studio or goes online through it.",
                    StudioKinds.StudioNotRunningKind);
            case StudioKinds.StudioInOtherSessionKind:
                var there = string.Join(", ", ids.Distinct());
                return new DoctorCheck("studio", DoctorStatus.Fail,
                    $"this server is in Windows session {sessionId}, but LogixDesigner runs in session {there}",
                    $"Run the MCP client in Windows session {there}, or ask the engineer to open Studio in session {sessionId}.",
                    StudioKinds.StudioInOtherSessionKind);
        }

        if (env.StudioIntegrityHigherThanCurrent)
            return new DoctorCheck("studio", DoctorStatus.Fail,
                "Studio is running elevated and this server is not, so Windows blocks it from driving Studio",
                "Start Studio normally (not as administrator), or run the MCP client elevated.",
                StudioKinds.StudioElevatedKind);

        var titles = env.StudioWindowTitlesInCurrentSession;
        var online = titles.Select(ParseStudioOnlineState).FirstOrDefault(t => t.Online);
        if (online is null || !online.Online)
        {
            var shown = titles.Count == 0 ? "no main-window title" : $"title '{titles[0]}'";
            return new DoctorCheck("studio", DoctorStatus.Fail,
                $"LogixDesigner is running in session {sessionId} but is offline ({shown})",
                "Ask the engineer to go online in Studio by Upload; lgx-mcp never takes Studio online.",
                StudioKinds.StudioOfflineKind);
        }

        return new DoctorCheck("studio", DoctorStatus.Ok,
            $"LogixDesigner is running in session {sessionId} and online [{online.Mode}]");
    }

    /// <summary>Online state and mode token parsed from a Logix Designer window title.</summary>
    /// <param name="Online">True when the title carries the bracketed mode prefix.</param>
    /// <param name="Mode">The token inside the brackets; null when offline.</param>
    public sealed record StudioTitleState(bool Online, string? Mode);

    /// <summary>
    /// C# equivalent of <c>Get-StudioOnlineStateFromTitle</c> in <c>bench/studio-edits.ps1</c>:
    /// an online title starts with a bracketed prefix (<c>[Rem Run] - Logix Designer - ...</c>);
    /// its absence means offline. Never a substring match on the word "online".
    /// </summary>
    /// <param name="title">The window title; null or empty means offline.</param>
    /// <returns>The parsed state.</returns>
    public static StudioTitleState ParseStudioOnlineState(string? title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            var m = Regex.Match(title, @"^\[([^\]]+)\] - ");
            if (m.Success) return new StudioTitleState(true, m.Groups[1].Value);
        }
        return new StudioTitleState(false, null);
    }
}
