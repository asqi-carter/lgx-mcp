using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>
/// The final result of a background connect, kept as <see cref="ConnectState.LastOutcome"/> so a
/// later status poll can still read it after the connect has left the "in progress" state.
/// </summary>
/// <param name="ConnectId">Id of the connect that produced this outcome.</param>
/// <param name="ResponseJson">The exact tool payload the connect produced; <c>null</c> when it faulted.</param>
/// <param name="Error">The exception message when the connect threw; <c>null</c> on a normal completion.</param>
/// <param name="ElapsedMs">Wall-clock duration of the connect.</param>
/// <param name="CompletedAt">When the connect finished.</param>
/// <param name="Exception">The exception the connect threw, kept so its typed error can be rebuilt; <c>null</c> otherwise.</param>
/// <param name="Phase">Last phase the connect reached.</param>
/// <param name="PhaseTimings">Per-phase timings of the connect.</param>
public sealed record ConnectOutcome(
    string ConnectId, string? ResponseJson, string? Error, long ElapsedMs, DateTimeOffset CompletedAt,
    Exception? Exception = null, string Phase = ConnectState.StartingPhase,
    IReadOnlyList<PhaseTiming>? PhaseTimings = null);

/// <summary>
/// How long one connect phase started after the connect began, and how long it has run or ran.
/// </summary>
/// <param name="Phase">Phase name (a <see cref="ConnectPhases"/> value).</param>
/// <param name="StartedMs">Milliseconds from the start of the connect to the start of this phase.</param>
/// <param name="DurationMs">Milliseconds the phase ran; for the phase still running, the time so far.</param>
/// <param name="OwnerPid">For the <c>lease-wait</c> phase, the pid of the process holding the lease; omitted otherwise.</param>
/// <param name="Outcome">For the <c>lease-wait</c> phase, how the wait ended (<c>owner-finished</c> or <c>timeout</c>); omitted while it runs and for other phases.</param>
/// <param name="Error">For the <c>comms-retry</c> phase, the error that caused the retry; omitted for other phases.</param>
public sealed record PhaseTiming(
    [property: JsonPropertyName("phase")]      string Phase,
    [property: JsonPropertyName("startedMs")]  long   StartedMs,
    [property: JsonPropertyName("durationMs")] long   DurationMs,
    [property: JsonPropertyName("ownerPid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OwnerPid = null,
    [property: JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>
/// A point-in-time view of the connect that is currently running.
/// </summary>
/// <param name="ConnectId">Id handed to the caller in the <c>connecting</c> payload.</param>
/// <param name="Phase">Last <see cref="ConnectPhases"/> phase reached, or <see cref="ConnectState.StartingPhase"/>.</param>
/// <param name="ElapsedMs">Milliseconds since the connect started.</param>
/// <param name="PhaseTimings">Timings of the phases reached so far.</param>
/// <param name="Message">
/// Progress text for a phase that is a note rather than a step (<see cref="ConnectPhases.SdkRestartText"/> while the
/// retry waits for a restarted SDK service); <c>null</c> otherwise.
/// </param>
public sealed record ConnectSnapshot(
    string ConnectId, string Phase, long ElapsedMs, IReadOnlyList<PhaseTiming>? PhaseTimings = null,
    string? Message = null);

/// <summary>
/// The payload returned when a connect outlives its synchronous budget.
/// </summary>
/// <param name="ConnectId">Id to poll with.</param>
/// <param name="Phase">Phase the connect had reached when the budget ran out.</param>
/// <param name="ElapsedMs">Milliseconds the connect had been running.</param>
/// <param name="PollWith">Name of the tool to poll.</param>
/// <param name="Status">Always <c>"connecting"</c>.</param>
public sealed record ConnectingResult(
    [property: JsonPropertyName("connectId")] string ConnectId,
    [property: JsonPropertyName("phase")]     string Phase,
    [property: JsonPropertyName("elapsedMs")] long   ElapsedMs,
    [property: JsonPropertyName("pollWith")]  string PollWith = ConnectState.PollTool,
    [property: JsonPropertyName("status")]    string Status   = "connecting");

/// <summary>
/// Holds the state of the one SDK connect that may run in the background (connectId, phase, start
/// time, outcome, last outcome) and runs a connect against a synchronous budget: a connect that
/// finishes inside the budget returns its normal payload; one that does not returns a
/// <c>connecting</c> payload while the work keeps running in the server.
/// </summary>
public sealed class ConnectState
{
    /// <summary>Environment variable that overrides <see cref="DefaultSyncBudget"/> (seconds).</summary>
    public const string BudgetEnvVar = "LGX_CONNECT_SYNC_BUDGET_S";

    /// <summary>Tool the caller is told to poll.</summary>
    public const string PollTool = TasksSupport.ConnectToolName + "_status";

    /// <summary>Phase reported before the first <see cref="ConnectPhases"/> report arrives.</summary>
    public const string StartingPhase = "starting";

    /// <summary>Default synchronous budget: 20 seconds.</summary>
    public static readonly TimeSpan DefaultSyncBudget = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private string?   _connectId;
    private string?   _argsKey;
    private string    _phase = StartingPhase;
    private string?   _message;
    private readonly List<(string Phase, long StartedMs, int? OwnerPid, string? Outcome, long? FixedDurationMs, string? Error)> _phases = [];
    private Stopwatch? _clock;
    private Task?     _running;
    private ConnectOutcome? _last;
    private string?   _commPath;
    private readonly ConnectRegistry? _registry;

    /// <summary>
    /// Creates the connect state.
    /// </summary>
    /// <param name="registry">
    /// Cross-instance registry the connect progress is mirrored to, and consulted when this instance has no
    /// connect of its own; <c>null</c> (the default) keeps everything inside this process.
    /// </param>
    public ConnectState(ConnectRegistry? registry = null) => _registry = registry;

    /// <summary>Who this server instance is: reported as <c>serverInstance</c> in connect and status responses.</summary>
    public ServerInstance Instance => _registry?.Instance ?? ConnectRegistry.CurrentProcess();

    /// <summary>The connect currently running in the background, or <c>null</c> when none is.</summary>
    public ConnectSnapshot? Current
    {
        get
        {
            lock (_gate)
                return _connectId is null || _clock is null
                    ? null
                    : new ConnectSnapshot(_connectId, _phase, _clock.ElapsedMilliseconds,
                        Timings(_clock.ElapsedMilliseconds), _message);
        }
    }

    /// <summary>The outcome of the most recent connect that ran past its budget and then finished.</summary>
    public ConnectOutcome? LastOutcome
    {
        get { lock (_gate) return _last; }
    }

    /// <summary>Completes when the running connect (if any) has finished; used by tests and shutdown.</summary>
    public Task Completion
    {
        get { lock (_gate) return _running ?? Task.CompletedTask; }
    }

    /// <summary>Longest synchronous budget honoured: 600 seconds. Larger values are clamped to it.</summary>
    public static readonly TimeSpan MaxSyncBudget = TimeSpan.FromSeconds(600);

    /// <summary>
    /// Resolves the synchronous budget from the raw value of <see cref="BudgetEnvVar"/>.
    /// </summary>
    /// <param name="raw">
    /// Raw environment value; <c>null</c>, blank, non-numeric or negative gives the default. A value above
    /// <see cref="MaxSyncBudget"/> (600 s) is clamped to it and the clamp is logged to stderr.
    /// </param>
    /// <returns>The budget, within [0, 600] s; zero means "always return <c>connecting</c> at once".</returns>
    public static TimeSpan ResolveSyncBudget(string? raw)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s)
            || s < 0 || double.IsNaN(s))
            return DefaultSyncBudget;

        if (double.IsInfinity(s) || s > MaxSyncBudget.TotalSeconds)
        {
            try
            {
                Console.Error.WriteLine(
                    $"lgx-mcp: {BudgetEnvVar}={raw} exceeds the maximum; clamped to {MaxSyncBudget.TotalSeconds:0} s.");
            }
            catch (Exception) { /* logging must never break connect */ }
            return MaxSyncBudget;
        }
        return TimeSpan.FromSeconds(s);
    }

    /// <summary>Reads the budget from the process environment.</summary>
    /// <returns>The effective synchronous budget.</returns>
    public static TimeSpan SyncBudgetFromEnvironment() =>
        ResolveSyncBudget(Environment.GetEnvironmentVariable(BudgetEnvVar));

    /// <summary>
    /// Runs <paramref name="work"/> as a server-side job and waits up to <paramref name="budget"/> for it.
    /// </summary>
    /// <param name="work">
    /// The connect. Receives a progress sink that records the current phase (and forwards to
    /// <paramref name="forward"/> while the caller is still waiting) and returns the tool payload.
    /// </param>
    /// <param name="budget">How long the caller waits before getting a <c>connecting</c> payload.</param>
    /// <param name="forward">Optional downstream progress sink (MCP progress notifications).</param>
    /// <param name="argsKey">
    /// Canonical key of the connect arguments (see <see cref="ArgsKeyFor"/>). While a connect is running, a call
    /// with the same key gets the in-flight <c>connecting</c> payload, and a call with a different key is refused
    /// with <see cref="ErrorKinds.ConnectInProgress"/>. <c>null</c> is treated as "same arguments".
    /// </param>
    /// <param name="commPath">Communication path of the connect, recorded in the cross-instance registry; may be <c>null</c>.</param>
    /// <returns>
    /// The payload of <paramref name="work"/> when it finishes within the budget (an exception it throws
    /// is rethrown unchanged); otherwise the serialized <see cref="ConnectingResult"/>. If a background
    /// connect is already running, its <c>connecting</c> payload (same arguments) or a
    /// <see cref="ErrorKinds.ConnectInProgress"/> refusal (different arguments) is returned and no second
    /// one starts.
    /// </returns>
    public async Task<string> RunAsync(
        Func<IProgress<ConnectProgress>, Task<string>> work,
        TimeSpan budget,
        IProgress<ConnectProgress>? forward = null,
        string? argsKey = null,
        string? commPath = null)
    {
        string id;
        Stopwatch clock;
        Task<string> job;
        var detached = false;

        lock (_gate)
        {
            var guarded = GuardLocked(argsKey);
            if (guarded is not null) return guarded;

            id     = Guid.NewGuid().ToString("N");
            clock  = Stopwatch.StartNew();
            _connectId = id;
            _argsKey   = argsKey;
            _clock     = clock;
            _phase     = StartingPhase;
            _message   = null;
            _commPath  = commPath;
            _phases.Clear();
        }
        RegistryWrite(id, StartingPhase, 0, ConnectRegistry.StatusConnecting, null, commPath);

        var sink = new PhaseSink(this, id, forward, () => Volatile.Read(ref detached));
        try
        {
            job = Task.Run(() => work(sink));
        }
        catch
        {
            Clear(id);
            throw;
        }

        var tracked = Track(id, clock, job);
        lock (_gate) _running = tracked;

        using var delayCts = new CancellationTokenSource();
        Task finished;
        try
        {
            var delay = Task.Delay(budget, delayCts.Token);
            finished = await Task.WhenAny(job, delay).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The job already started and keeps running; a failing wait (e.g. an out-of-range budget) must
            // not turn into an error. Treat it as "budget elapsed" and answer 'connecting'.
            try { Console.Error.WriteLine($"lgx-mcp: connect wait failed ({ex.GetType().Name}: {ex.Message}); returning 'connecting'."); }
            catch (Exception) { /* ignore */ }
            finished = Task.CompletedTask;
        }
        if (finished == job)
        {
            delayCts.Cancel();
            await tracked.ConfigureAwait(false);   // state cleared, background bookkeeping done
            return await job.ConfigureAwait(false); // rethrows the connect's own exception
        }

        Volatile.Write(ref detached, true);
        lock (_gate)
        {
            // The job may have finished in the instant after the budget elapsed; prefer the real result.
            if (_connectId == id)
                return Serialize(id, _phase, clock.ElapsedMilliseconds);
        }
        return await job.ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the canonical key of a <c>logix_connect</c> argument set, so two calls can be compared.
    /// </summary>
    /// <param name="parts">The argument values in a fixed order; <c>null</c> and blank compare equal.</param>
    /// <returns>The key.</returns>
    public static string ArgsKeyFor(params object?[] parts) =>
        string.Join("\u001f", parts.Select(p => p switch
        {
            null      => "",
            bool b    => b ? "true" : "false",
            string s  => s.Trim().ToLowerInvariant(),
            _         => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        }));

    /// <summary>
    /// Checks a new <c>logix_connect</c> call against the connect in flight, without starting anything.
    /// </summary>
    /// <param name="argsKey">Key of the new call's arguments (see <see cref="ArgsKeyFor"/>).</param>
    /// <returns>
    /// <c>null</c> when no connect is running; the in-flight <c>connecting</c> payload when the arguments
    /// match; a <see cref="ErrorKinds.ConnectInProgress"/> error payload when they differ.
    /// </returns>
    public string? Guard(string? argsKey)
    {
        lock (_gate) return GuardLocked(argsKey);
    }

    /// <summary>Guard body. Caller holds <c>_gate</c>.</summary>
    private string? GuardLocked(string? argsKey)
    {
        if (_connectId is null || _clock is null) return null;
        var elapsed = _clock.ElapsedMilliseconds;
        return argsKey is null || _argsKey is null || string.Equals(argsKey, _argsKey, StringComparison.Ordinal)
            ? Serialize(_connectId, _phase, elapsed)
            : InProgressError(_connectId, _phase, elapsed,
                "A logix_connect with different arguments is already in progress; this call was refused and nothing was started.");
    }

    /// <summary>
    /// If this session's own connect or reconnect is in flight, a session call must answer
    /// <see cref="ErrorKinds.ConnectInProgress"/>, never <see cref="ErrorKinds.SessionLost"/>: while it runs the old
    /// project is closed on purpose. This turns a <see cref="ErrorKinds.NoActiveSession"/> or
    /// <see cref="ErrorKinds.SessionLost"/> error, or one saying "Project is not open.", into that error.
    /// </summary>
    /// <param name="text">A tool result's JSON text.</param>
    /// <param name="toolName">The tool that produced it; the connect, status, open and close tools are left alone.</param>
    /// <returns>The replacement payload, or <c>null</c> when <paramref name="text"/> should be left alone.</returns>
    public string? RewriteNoActiveSession(string text, string? toolName = null)
    {
        if (SessionLostSupport.IsExempt(toolName)) return null;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] != '{') return null;
        ConnectSnapshot? current;
        lock (_gate) current = Current;
        if (current is null) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o) return null;
            var kind  = o["kind"] is JsonValue k && k.TryGetValue<string>(out var kk) ? kk : null;
            var error = o["error"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : null;
            var hit = kind is ErrorKinds.NoActiveSession or ErrorKinds.SessionLost
                || (!string.IsNullOrEmpty(error) && RockwellErrorHints.IsMatch(error, RockwellErrorHintIds.ProjectNotOpen));
            if (!hit) return null;
        }
        catch (JsonException) { return null; }
        return InProgressPayload(current);
    }

    /// <summary>
    /// If a connect is in flight and <paramref name="ex"/> says the project is not open, returns the
    /// <see cref="ErrorKinds.ConnectInProgress"/> payload to send instead of marking the session lost.
    /// </summary>
    /// <param name="toolName">The tool that threw.</param>
    /// <param name="ex">What it threw.</param>
    /// <returns>The payload, or <c>null</c> when no connect is running or the failure is something else.</returns>
    public string? RewriteProjectNotOpen(string? toolName, Exception ex)
    {
        if (SessionLostSupport.IsExempt(toolName) || !RockwellErrorHints.IsProjectNotOpen(ex)) return null;
        var current = Current;
        return current is null ? null : InProgressPayload(current);
    }

    private static string InProgressPayload(ConnectSnapshot c) =>
        InProgressError(c.ConnectId, c.Phase, c.ElapsedMs,
            "A logix_connect is still in progress, so there is no usable session yet. The session was not lost.");

    private static string InProgressError(string connectId, string phase, long elapsedMs, string error) =>
        new JsonObject
        {
            ["error"]     = error,
            ["kind"]      = ErrorKinds.ConnectInProgress,
            ["connectId"] = connectId,
            ["phase"]     = phase,
            ["elapsedMs"] = elapsedMs,
            ["pollWith"]  = PollTool,
            ["hint"]      = $"Call {PollTool} until it reports connected or failed, then retry this call.",
        }.ToJsonString(_json);

    /// <summary>
    /// Clears the running state when the job ends and records <see cref="LastOutcome"/>.
    /// </summary>
    private async Task Track(string id, Stopwatch clock, Task<string> job)
    {
        string? json = null, error = null;
        Exception? thrown = null;
        try { json = await job.ConfigureAwait(false); }
        catch (Exception ex) { error = ex.Message; thrown = ex; }
        clock.Stop();

        ConnectOutcome done;
        string? comm;
        lock (_gate)
        {
            if (_connectId != id) return;
            done = _last = new ConnectOutcome(id, json, error, clock.ElapsedMilliseconds, DateTimeOffset.UtcNow,
                thrown, _phase, Timings(clock.ElapsedMilliseconds));
            comm = _commPath;
            _connectId = null;
            _argsKey   = null;
            _clock     = null;
            _phase     = StartingPhase;
            _message   = null;
            _commPath  = null;
            _phases.Clear();
        }
        RegistryWrite(id, done.Phase, done.ElapsedMs,
            IsFailed(done.ResponseJson) ? ConnectRegistry.StatusFailed : ConnectRegistry.StatusConnected,
            done.CompletedAt, comm);
    }

    /// <summary>Mirrors the connect to the cross-instance registry. Advisory: never throws.</summary>
    private void RegistryWrite(
        string id, string phase, long elapsedMs, string status, DateTimeOffset? completedAt, string? commPath)
    {
        if (_registry is null) return;
        try
        {
            _registry.Write(new ConnectRegistryEntry(
                id, _registry.Pid, _registry.StartedAt, commPath, phase, elapsedMs, status, completedAt));
        }
        catch (Exception) { /* the registry is advisory; it must never fail a connect */ }
    }

    /// <summary>True when a connect's payload is missing or carries a non-empty <c>error</c>.</summary>
    private static bool IsFailed(string? responseJson)
    {
        if (responseJson is null) return true;
        try
        {
            return JsonNode.Parse(responseJson) is JsonObject o && o.TryGetPropertyValue("error", out var e)
                && e is not null && !(e is JsonValue v && v.TryGetValue<string>(out var str) && string.IsNullOrEmpty(str));
        }
        catch (JsonException) { return false; }
    }

    private void Clear(string id)
    {
        string phase;
        string? comm;
        lock (_gate)
        {
            if (_connectId != id) return;
            phase = _phase;
            comm = _commPath;
            _connectId = null;
            _argsKey   = null;
            _clock     = null;
            _phase     = StartingPhase;
            _message   = null;
            _commPath  = null;
            _phases.Clear();
        }
        RegistryWrite(id, phase, 0, ConnectRegistry.StatusFailed, DateTimeOffset.UtcNow, comm);
    }

    private void SetPhase(
        string id, string phase, int? ownerPid = null, string? message = null, string? outcome = null,
        long? durationMs = null, string? error = null)
    {
        long registryElapsed;
        string? registryComm;
        lock (_gate)
        {
            if (_connectId != id || _clock is null) return;
            if (phase == ConnectPhases.LeaseWait && outcome is not null
                && _phases.Count > 0 && _phases[^1].Phase == ConnectPhases.LeaseWait)
            {
                // The closing report of the lease wait: it names how the wait ended, it is not a new phase.
                _phases[^1] = _phases[^1] with { Outcome = outcome };
                return;
            }
            var now = _clock.ElapsedMilliseconds;
            var started = now;
            if (phase == ConnectPhases.CommsRetry && durationMs is { } waited)
            {
                // Reported when the wait has ended: it started `waited` ago (not before the phase it follows began).
                var floor = _phases.Count > 0 ? _phases[^1].StartedMs : 0;
                started = Math.Max(floor, now - Math.Max(0, waited));
            }
            _phase = phase;
            _message = message;
            _phases.Add((phase, started, ownerPid, outcome, durationMs, error));
            registryElapsed = now;
            registryComm = _commPath;
        }
        RegistryWrite(id, phase, registryElapsed, ConnectRegistry.StatusConnecting, null, registryComm);
    }

    /// <summary>Builds the phase timings from the recorded phase starts. Caller holds <c>_gate</c>.</summary>
    private List<PhaseTiming> Timings(long nowMs)
    {
        var list = new List<PhaseTiming>(_phases.Count);
        for (var i = 0; i < _phases.Count; i++)
        {
            var end = i + 1 < _phases.Count ? _phases[i + 1].StartedMs : nowMs;
            var duration = _phases[i].FixedDurationMs ?? Math.Max(0, end - _phases[i].StartedMs);
            list.Add(new PhaseTiming(_phases[i].Phase, _phases[i].StartedMs, duration, _phases[i].OwnerPid, _phases[i].Outcome, _phases[i].Error));
        }
        return list;
    }

    /// <summary>
    /// Builds the <c>logix_connect_status</c> payload: the connect in flight (<c>connecting</c>), else the last
    /// outcome (<c>connected</c> with the sync path's payload, or <c>failed</c> with the original typed error),
    /// else - when another server instance owns a connect in the cross-instance registry - a
    /// <see cref="ErrorKinds.ConnectOwnedByOtherInstance"/> error, else a <see cref="ErrorKinds.NoConnectInProgress"/>
    /// error. Every payload carries <c>serverInstance</c>.
    /// </summary>
    /// <param name="connectId">
    /// A connect id to look up. When this instance has no connect with that id, the registry is searched for it;
    /// <c>null</c> asks for this instance's connect, else the most recent in-progress connect of another instance.
    /// </param>
    /// <returns>The serialized tool payload.</returns>
    public string GetStatusJson(string? connectId = null)
    {
        var o = BuildStatus(connectId);
        o["serverInstance"] = JsonSerializer.SerializeToNode(Instance, _json);
        return o.ToJsonString(_json);
    }

    /// <summary>
    /// Adds <c>serverInstance</c> to a tool payload.
    /// </summary>
    /// <param name="json">The payload; anything that is not a JSON object is returned unchanged.</param>
    /// <returns>The payload with <c>serverInstance</c> {pid, startedAt}.</returns>
    public string WithServerInstance(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return json;
            o["serverInstance"] = JsonSerializer.SerializeToNode(Instance, _json);
            return o.ToJsonString(_json);
        }
        catch (JsonException) { return json; }
    }

    private JsonObject BuildStatus(string? connectId)
    {
        ConnectSnapshot? current;
        ConnectOutcome? last;
        lock (_gate)
        {
            current = Current;
            last    = _last;
        }

        var mismatch = !string.IsNullOrEmpty(connectId)
            && !string.Equals(connectId, current?.ConnectId, StringComparison.Ordinal)
            && !string.Equals(connectId, last?.ConnectId, StringComparison.Ordinal);
        if (mismatch || (current is null && last is null))
        {
            var foreign = FindForeign(connectId);
            if (foreign is not null) return OwnedByOther(foreign);
        }

        if (current is not null)
        {
            var o = Head("connecting", current.ConnectId, current.Phase, current.ElapsedMs, current.PhaseTimings);
            o["pollWith"] = PollTool;
            if (current.Message is { } progressText) o["progress"] = progressText;
            return o;
        }

        if (last is null)
            return new JsonObject
            {
                ["error"] = "No logix_connect is in progress and none has run in this server.",
                ["kind"]  = ErrorKinds.NoConnectInProgress,
                ["hint"]  = "Call logix_connect with backend=\"sdk\" first; logix_connect_status reports a connect that outlived its sync budget.",
            };

        // The payload the connect produced: its own JSON, or the typed error the tool filter makes of its exception.
        var body = last.ResponseJson ?? ToolErrorText(last.Exception, last.Error);
        JsonObject? original = null;
        try { original = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }

        var failed = last.ResponseJson is null || (original is not null && original.TryGetPropertyValue("error", out var e)
            && e is not null && !(e is JsonValue v && v.TryGetValue<string>(out var str) && string.IsNullOrEmpty(str)));
        var result = Head(failed ? "failed" : "connected", last.ConnectId, last.Phase, last.ElapsedMs, last.PhaseTimings);
        if (original is null)
        {
            result["error"] = last.Error ?? body;
            return result;
        }
        foreach (var (name, value) in original.ToArray())
        {
            if (result.ContainsKey(name)) continue;
            original.Remove(name);
            result[name] = value;
        }
        return result;
    }

    /// <summary>The registry entry of a connect owned by another live instance (or finished by one), if any.</summary>
    private ConnectRegistryEntry? FindForeign(string? connectId)
    {
        if (_registry is null) return null;
        try
        {
            if (!string.IsNullOrEmpty(connectId))
            {
                var e = _registry.Read(connectId);
                if (e is null || e.OwnerPid == _registry.Pid) return null;
                // A connect still marked in progress whose owner is gone will never finish: not an owner to report.
                return e.CompletedAt is null && !_registry.IsOwnerAlive(e) ? null : e;
            }
            return _registry.FindMostRecentInProgress();
        }
        catch (Exception) { return null; }
    }

    private static JsonObject OwnedByOther(ConnectRegistryEntry e)
    {
        var finished = e.CompletedAt is not null || e.Status != ConnectRegistry.StatusConnecting;
        var o = new JsonObject
        {
            ["error"]     = finished
                ? $"The connect {e.ConnectId} was run by another lgx-mcp server instance (pid {e.OwnerPid}) and has finished ({e.Status}). This instance has no connection from it."
                : $"A connect is in progress in another lgx-mcp server instance (pid {e.OwnerPid}). This instance cannot use that connection.",
            ["kind"]      = ErrorKinds.ConnectOwnedByOtherInstance,
            ["status"]    = e.Status,
            ["connectId"] = e.ConnectId,
            ["ownerPid"]  = e.OwnerPid,
            ["phase"]     = e.Phase,
            ["elapsedMs"] = e.ElapsedMs,
            ["finished"]  = finished,
        };
        if (e.CommPath is { Length: > 0 } cp) o["commPath"] = cp;
        if (e.CompletedAt is { } at) o["completedAt"] = at;
        o["hint"] = finished
            ? $"Another instance (pid {e.OwnerPid}) finished that connect with status '{e.Status}'; its connection is not usable from this instance. " +
              $"Call {TasksSupport.ConnectToolName} here to connect this instance."
            : "This instance cannot use that connection. Wait for the other instance to finish, then call " +
              $"{TasksSupport.ConnectToolName} here; do not start a parallel upload while it runs.";
        return o;
    }

    private static JsonObject Head(
        string status, string connectId, string phase, long elapsedMs, IReadOnlyList<PhaseTiming>? timings) => new()
    {
        ["status"]       = status,
        ["connectId"]    = connectId,
        ["phase"]        = phase,
        ["elapsedMs"]    = elapsedMs,
        ["phaseTimings"] = JsonSerializer.SerializeToNode(timings ?? [], _json),
    };

    private static string ToolErrorText(Exception? ex, string? message) =>
        ex is null
            ? JsonSerializer.Serialize(new { error = message ?? "logix_connect failed.", kind = ErrorKinds.InternalError }, _json)
            : ((ModelContextProtocol.Protocol.TextContentBlock)ToolErrorFilter.ToErrorResult(TasksSupport.ConnectToolName, ex).Content[0]).Text;

    private static string Serialize(string id, string phase, long elapsedMs) =>
        JsonSerializer.Serialize(new ConnectingResult(id, phase, elapsedMs), _json);

    private sealed class PhaseSink(
        ConnectState owner, string id, IProgress<ConnectProgress>? forward, Func<bool> isDetached)
        : IProgress<ConnectProgress>
    {
        public void Report(ConnectProgress value)
        {
            owner.SetPhase(id, value.Phase, value.OwnerPid,
                value.Phase == ConnectPhases.SdkRestart ? value.Message : null, value.Outcome,
                value.Phase == ConnectPhases.CommsRetry ? value.DurationMs : null,
                value.Phase == ConnectPhases.CommsRetry ? value.Error : null);
            if (forward is not null && !isDetached()) forward.Report(value);
        }
    }
}
