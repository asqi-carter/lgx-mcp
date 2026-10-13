using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>Why and when the active session lost its project.</summary>
/// <param name="Cause">One of <see cref="SessionLostCauses"/>, chosen only from observed evidence.</param>
/// <param name="At">When the loss was detected (the serialized <c>lostAt</c>). Always the detection time, never the restart time.</param>
/// <param name="Observed">The SDK's message that showed the project closed (for example <c>Project is not open.</c>).</param>
/// <param name="Evidence">The evidence the cause was chosen from; <c>null</c> for <see cref="SessionLostCauses.ProjectClosedUnknown"/>.</param>
/// <param name="SdkRestartedAt">When the SDK server restarted (serialized <c>sdkRestartedAt</c>): the start time of the new
/// <c>LdSdkServer</c> process. Earlier than <see cref="At"/>, which is when the restart was detected. <c>null</c> unless the
/// cause is <see cref="SessionLostCauses.SdkServiceRestarted"/> and a new process start time could be read.</param>
public sealed record SessionLostInfo(
    [property: JsonPropertyName("cause")]    string         Cause,
    [property: JsonPropertyName("at")]       DateTimeOffset At,
    [property: JsonPropertyName("observed")] string?        Observed = null,
    [property: JsonPropertyName("evidence")] string?        Evidence = null,
    [property: JsonPropertyName("sdkRestartedAt")] DateTimeOffset? SdkRestartedAt = null);

/// <summary>The three causes a <see cref="SessionLostInfo"/> can carry.</summary>
public static class SessionLostCauses
{
    /// <summary>The LdSdkServer process changed since connect, or the SDK service was stopped.</summary>
    public const string SdkServiceRestarted = "sdk-service-restarted";

    /// <summary>A lease record shows another server process holds the host's upload/reconnect lease.</summary>
    public const string OtherProcessReconnect = "other-process-reconnect";

    /// <summary>The project was found closed and nothing observed says why.</summary>
    public const string ProjectClosedUnknown = "project-closed-unknown";
}

/// <summary>
/// Observes the host for the evidence <see cref="SessionLostCauses"/> are chosen from. Every probe is injectable so
/// tests stand in for the SDK service and for a second server process.
/// </summary>
public sealed class SessionLostEvidence
{
    private readonly Func<IReadOnlyCollection<int>> _sdkServerPids;
    private readonly Func<bool> _sdkServiceStopped;
    private readonly Func<UploadLeaseOwner?> _leaseOwner;
    private readonly Func<int, DateTimeOffset?> _processStart;
    private readonly int _ownPid;

    /// <summary>Creates the evidence source.</summary>
    /// <param name="sdkServerPids">Pids of the running <c>LdSdkServer</c> processes; default a process lookup.</param>
    /// <param name="sdkServiceStopped">True when the SCM reports <c>LdSdkService</c> stopped or stopping; default an SCM query (Windows only).</param>
    /// <param name="leaseOwner">The live host upload-lease owner, or <c>null</c>; default <see cref="HostUploadLease.Default"/>.</param>
    /// <param name="ownPid">This process's pid; default the current process.</param>
    /// <param name="processStartTime">Start time of a process by pid, or <c>null</c> when unknown; default a process lookup.</param>
    public SessionLostEvidence(
        Func<IReadOnlyCollection<int>>? sdkServerPids = null, Func<bool>? sdkServiceStopped = null,
        Func<UploadLeaseOwner?>? leaseOwner = null, int? ownPid = null,
        Func<int, DateTimeOffset?>? processStartTime = null)
    {
        _processStart      = processStartTime ?? DefaultProcessStart;
        _sdkServerPids     = sdkServerPids ?? DefaultServerPids;
        _sdkServiceStopped = sdkServiceStopped ?? DefaultServiceStopped;
        _leaseOwner        = leaseOwner ?? (() => HostUploadLease.Default.CurrentOwner());
        _ownPid            = ownPid ?? Environment.ProcessId;
    }

    /// <summary>The <c>LdSdkServer</c> pids right now; recorded at connect to detect a later restart.</summary>
    public IReadOnlyCollection<int> SnapshotServerPids() => Safe(_sdkServerPids, []);

    /// <summary>
    /// Chooses the cause from the evidence, strongest first: an SDK restart (changed pid or SCM stop), then a lease
    /// held by another pid, else unknown.
    /// </summary>
    /// <param name="pidsAtConnect"><see cref="SnapshotServerPids"/> taken when the session was attached.</param>
    /// <returns>The cause and the evidence sentence (<c>null</c> evidence for unknown).</returns>
    public (string Cause, string? Evidence) Decide(IReadOnlyCollection<int> pidsAtConnect)
    {
        if (pidsAtConnect.Count > 0)
        {
            var now = SnapshotServerPids();
            if (!pidsAtConnect.Any(now.Contains))
                return (SessionLostCauses.SdkServiceRestarted,
                    "LdSdkServer pid " + string.Join("/", pidsAtConnect) + " at connect, " +
                    (now.Count == 0 ? "no LdSdkServer running now" : "pid " + string.Join("/", now) + " now"));
        }
        if (Safe(_sdkServiceStopped, false))
            return (SessionLostCauses.SdkServiceRestarted, "the service control manager reports LdSdkService as not running (Stopped or StopPending)");

        if (Safe(_leaseOwner, null) is { } owner && owner.Pid != _ownPid)
            return (SessionLostCauses.OtherProcessReconnect,
                "the host upload lease is held by server process " + owner.Pid + " since " + owner.AcquiredAt.ToString("O"));

        return (SessionLostCauses.ProjectClosedUnknown, null);
    }

    /// <summary>
    /// When the SDK server restarted: the earliest start time among the running <c>LdSdkServer</c> pids that were not
    /// present at connect. <c>null</c> when none changed or no start time can be read. The detection time is separate
    /// (<see cref="SessionLostInfo.At"/>).
    /// </summary>
    /// <param name="pidsAtConnect"><see cref="SnapshotServerPids"/> taken when the session was attached.</param>
    public DateTimeOffset? SdkRestartedAt(IReadOnlyCollection<int> pidsAtConnect)
    {
        if (pidsAtConnect.Count == 0) return null;
        var now = SnapshotServerPids();
        if (pidsAtConnect.Any(now.Contains)) return null;
        DateTimeOffset? earliest = null;
        foreach (var pid in now)
            if (Safe(() => _processStart(pid), null) is { } t && (earliest is null || t < earliest)) earliest = t;
        return earliest;
    }

    /// <summary>
    /// True when <paramref name="pidsAtConnect"/> was recorded (non-empty) and none of those <c>LdSdkServer</c> pids
    /// is running now: the SDK server restarted since connect. Cheap enough to poll every second.
    /// </summary>
    /// <param name="pidsAtConnect"><see cref="SnapshotServerPids"/> taken when the session was attached.</param>
    public bool ServerRestarted(IReadOnlyCollection<int> pidsAtConnect) =>
        pidsAtConnect.Count > 0 && !pidsAtConnect.Any(SnapshotServerPids().Contains);

    private static T Safe<T>(Func<T> probe, T fallback)
    {
        try { return probe(); } catch (Exception) { return fallback; }
    }

    private static IReadOnlyCollection<int> DefaultServerPids()
    {
        var pids = new List<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("LdSdkServer"))
            using (p) pids.Add(p.Id);
        return pids;
    }

    private static DateTimeOffset? DefaultProcessStart(int pid)
    {
        using var p = System.Diagnostics.Process.GetProcessById(pid);
        return new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    private static bool DefaultServiceStopped()
    {
#if LOGIX_SDK
        // The ServiceController package is referenced only in the SDK lane (see LogixMcp.csproj); the no-SDK build
        // has no SCM evidence and falls through to the pid and lease evidence.
        return OperatingSystem.IsWindows() && QueryServiceStopped();
#else
        return false;
#endif
    }

#if LOGIX_SDK
    // NoInlining: a missing ServiceController assembly fails when the enclosing method is JIT-compiled, so the
    // touch lives in its own method behind the OperatingSystem.IsWindows() guard at the call site.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site; the block is SDK-only (Windows).")]
    private static bool QueryServiceStopped()
    {
        using var sc = new System.ServiceProcess.ServiceController("LdSdkService");
        return sc.Status is System.ServiceProcess.ServiceControllerStatus.Stopped
            or System.ServiceProcess.ServiceControllerStatus.StopPending;
    }
#endif
}

/// <summary>
/// Turns "Project is not open." on a connected session into a typed <see cref="ErrorKinds.SessionLost"/> (G33).
/// Another server process's reconnect can close the project a session holds; the next call finds it closed. The
/// session is marked lost (<see cref="TagSessionService.SessionLost"/>, <c>connected:false</c> in status) and the
/// caller is told the cause instead of a bare SDK message.
/// </summary>
public static class SessionLostSupport
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Tools that establish, replace or report on the session; their errors are never session loss.</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        TasksSupport.ConnectToolName, ConnectState.PollTool, "logix_open_project", "logix_close_project",
    };

    /// <summary>Builds the SessionLost payload.</summary>
    /// <param name="info">The recorded loss.</param>
    /// <param name="toolName">The tool that found it, or <c>null</c>.</param>
    public static string BuildPayload(SessionLostInfo info, string? toolName) =>
        JsonSerializer.Serialize(new
        {
            error     = Message(info),
            kind      = ErrorKinds.SessionLost,
            cause     = info.Cause,
            evidence  = info.Evidence,
            observed  = info.Observed,
            lostAt    = info.At,
            sdkRestartedAt = info.SdkRestartedAt,
            connected = false,
            tool      = toolName,
            hint      = "Reconnect: call logix_connect to establish a new session. Edits staged in the lost session are gone; " +
                        "the controller was not changed by this failed call.",
        }, Json);

    /// <summary>The message text for a loss; it states only what <see cref="SessionLostInfo.Cause"/> supports.</summary>
    /// <param name="info">The recorded loss.</param>
    public static string Message(SessionLostInfo info)
    {
        var seen = string.IsNullOrEmpty(info.Observed) ? "" : " The SDK reported \"" + info.Observed + "\".";
        var why = info.Cause switch
        {
            SessionLostCauses.SdkServiceRestarted =>
                "The session lost its project because the Logix Designer SDK (LdSdkService) was restarted (" + info.Evidence + ").",
            SessionLostCauses.OtherProcessReconnect =>
                "The session lost its project and another server process is connecting or reconnecting (" + info.Evidence +
                "), which most likely closed it.",
            _ => "The session lost its project, but nothing observed shows why (no LdSdkServer pid change, no SCM stop and no other " +
                 "server process holding the upload lease).",
        };
        return why + seen + " This session is no longer connected.";
    }

    /// <summary>
    /// If <paramref name="ex"/> says the project is not open and <paramref name="session"/> has a live session,
    /// marks it lost and returns the SessionLost payload; otherwise <c>null</c>.
    /// </summary>
    /// <param name="session">The tag session.</param>
    /// <param name="toolName">The tool that failed.</param>
    /// <param name="ex">What the tool threw.</param>
    public static string? TryFromException(TagSessionService session, string? toolName, Exception ex)
    {
        if (IsExempt(toolName) || !RockwellErrorHints.IsProjectNotOpen(ex)) return null;
        return Lose(session, toolName, FirstLine(ex.Message));
    }

    /// <summary>
    /// Rewrites a tool result's JSON text: a top-level <c>error</c> naming "Project is not open." on a live
    /// session becomes SessionLost (and marks the session); <c>NoActiveSession</c> on an already-lost session
    /// becomes SessionLost. Returns <c>null</c> when the text should be left alone.
    /// </summary>
    /// <param name="session">The tag session.</param>
    /// <param name="toolName">The tool that produced the text.</param>
    /// <param name="text">The result's JSON text.</param>
    public static string? TryRewrite(TagSessionService session, string? toolName, string text)
    {
        if (IsExempt(toolName)) return null;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{') return null;
        JsonObject? o;
        try { o = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
        if (o?["error"] is not JsonValue ev || !ev.TryGetValue<string>(out var error) || string.IsNullOrEmpty(error))
            return null;

        var kind = o["kind"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;
        if (session.SessionLost is { } lost && kind == ErrorKinds.NoActiveSession)
            return BuildPayload(lost, toolName);
        if (kind != ErrorKinds.SessionLost && RockwellErrorHints.IsMatch(error, RockwellErrorHintIds.ProjectNotOpen))
            return Lose(session, toolName, FirstLine(error));
        return null;
    }

    /// <summary>The longest the watch lets a tool call wait between checks of the SDK server pid; detection-to-failure is within this.</summary>
    public static readonly TimeSpan DefaultRestartPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The bound (G37): a restart of the SDK service fails a call with SessionLost within this of being detected.</summary>
    public static readonly TimeSpan RestartFailureBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks the live session's SDK server against the pids recorded at connect. If the server restarted, marks the
    /// session lost with cause <see cref="SessionLostCauses.SdkServiceRestarted"/> and returns the SessionLost payload.
    /// </summary>
    /// <param name="session">The tag session.</param>
    /// <param name="toolName">The tool whose call is being watched.</param>
    /// <returns>The SessionLost payload, or <c>null</c> when there is nothing to watch or the pid is unchanged.</returns>
    public static string? CheckServiceRestart(TagSessionService session, string? toolName)
    {
        if (IsExempt(toolName) || !session.WatchesSdkService || session.SessionLost is not null) return null;
        if (!session.LostEvidence.ServerRestarted(session.ServerPidsAtConnect)) return null;
        var info = session.MarkSessionLost(null);
        return info is null ? null : BuildPayload(info, toolName);
    }

    private static string? Lose(TagSessionService session, string? toolName, string cause)
    {
        if (session.Service is null) return null;
        var info = session.MarkSessionLost(cause);
        return info is null ? null : BuildPayload(info, toolName);
    }

    /// <summary>True for tools that establish, replace or report on the session; they never see SessionLost or ConnectInProgress rewrites.</summary>
    /// <param name="toolName">The tool name, or <c>null</c>.</param>
    public static bool IsExempt(string? toolName) => toolName is not null && Exempt.Contains(toolName);

    private static string FirstLine(string message)
    {
        var nl = message.IndexOfAny(['\r', '\n']);
        return (nl < 0 ? message : message[..nl]).Trim();
    }

    /// <summary>
    /// Adds <c>connected</c> (and, for a lost session, <c>sessionLost</c>) to a <c>logix_connect_status</c> payload.
    /// A <c>connected</c> status over a lost session becomes <c>lost</c>.
    /// </summary>
    /// <param name="session">The tag session.</param>
    /// <param name="statusJson">The connect-status JSON.</param>
    public static string AnnotateStatus(TagSessionService session, string statusJson)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(statusJson) as JsonObject; } catch (JsonException) { return statusJson; }
        if (o is null) return statusJson;

        // Connected only once the connect has completed and no reconnect is running.
        var connecting = o["status"] is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs == "connecting";
        o["connected"] = session.IsConnected && !connecting;
        // Another instance's outcome says nothing about this session: leave its status as reported.
        var foreign = o["kind"] is JsonValue kv && kv.TryGetValue<string>(out var kk) && kk == ErrorKinds.ConnectOwnedByOtherInstance;
        if (session.SessionLost is { } lost)
        {
            o["sessionLost"] = JsonSerializer.SerializeToNode(lost, Json);
            if (!foreign && o["status"] is JsonValue sv && sv.TryGetValue<string>(out var st) && st == "connected")
                o["status"] = "lost";
        }
        return o.ToJsonString(Json);
    }
}
