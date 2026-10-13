using System.Text.Json.Serialization;

/// <summary>
/// One retry <c>logix_connect</c> made after the SDK upload failed because <c>LdSdkService</c> was
/// unreachable straight after an earlier upload.
/// </summary>
/// <param name="Error">The error that triggered the retry (the failed attempt's message).</param>
/// <param name="WaitedMs">Milliseconds waited before the retry: the service-running poll plus any back-off.</param>
/// <param name="SdkRestart">
/// The LdSdkServer restart seen while waiting for this retry (which phase it hit and the pids before and after);
/// <c>null</c> (omitted) when the wait saw no restart.
/// </param>
public sealed record ConnectRetryEntry(
    [property: JsonPropertyName("error")]    string Error,
    [property: JsonPropertyName("waitedMs")] long   WaitedMs,
    [property: JsonPropertyName("sdkRestart"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SdkRestartRecord? SdkRestart = null,
    [property: JsonPropertyName("commsRetry"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ConnectCommsRetry? CommsRetry = null);

/// <summary>
/// The <c>comms-retry</c> phase: the wait before a retry made after a failure with no SDK server restart (the
/// communications layer may have faulted), which polls <see cref="SdkConnectRetryPolicy.CommsReady"/> instead of
/// sleeping a fixed time.
/// </summary>
/// <param name="DurationMs">Milliseconds waited until the fake or probe reported ready, or the ceiling ran out.</param>
/// <param name="Error">The error text that triggered the retry.</param>
public sealed record ConnectCommsRetry(
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("error")]      string Error);

/// <summary>
/// An <c>LdSdkServer</c> restart (crash and service recovery) seen while a <c>logix_connect</c> was running (G46).
/// </summary>
/// <param name="Phase">The connect phase the crash hit (a <see cref="ConnectPhases"/> name); <c>unknown</c> when none was reported yet.</param>
/// <param name="OldPid">The <c>LdSdkServer</c> pid before the crash; <c>null</c> when none was running or it could not be read.</param>
/// <param name="NewPid">The <c>LdSdkServer</c> pid after the restart; <c>null</c> when the server had not come back (the wait ran out).</param>
/// <param name="DetectedAt">When the connect noticed the restart (UTC).</param>
public sealed record SdkRestartRecord(
    [property: JsonPropertyName("phase")]      string Phase,
    [property: JsonPropertyName("oldPid")]     int? OldPid,
    [property: JsonPropertyName("newPid")]     int? NewPid,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt);

/// <summary>
/// What <see cref="SdkConnectRetryPolicy.RunAsync{T}"/> saw when it gave up after its crash-retry limit (G48).
/// Pass an instance to <c>RunAsync</c> to have the crash that triggers the give-up recorded.
/// </summary>
public sealed class SdkConnectGiveUp
{
    /// <summary>True when the retry limit ran out on a dead-service failure (the connect gave up after crashes).</summary>
    public bool GaveUp { get; internal set; }

    /// <summary>
    /// The LdSdkServer restart seen after the final failed attempt (the crash that triggered the give-up);
    /// <c>null</c> when none was seen. It is not part of the retries list, which holds the retries made.
    /// </summary>
    public SdkRestartRecord? FinalRestart { get; internal set; }
}

/// <summary>
/// Retry policy for the SDK upload-from-controller connect (<c>logix_connect</c>). When the upload fails with
/// one of three dead-service messages (<c>Unable to complete connection to LdSdkService</c>,
/// <c>Could not reach LdSdkService</c>, <c>Project is not open.</c>) the connect is retried up to
/// <see cref="Backoffs"/>.Count times. Before each retry it polls <c>LdSdkService</c> until it is Running
/// (up to <see cref="ServicePollTimeout"/>). When the poll saw the SDK server go away and come back (the service was
/// down, or the <c>LdSdkServer</c> pid changed) and it answers <see cref="ServicePing"/>, the retry starts at once and
/// the restart is recorded as an <see cref="SdkRestartRecord"/> (G46). Otherwise it backs off (10 s before the first
/// retry, 20 s before the second). Every other error is returned at once. The service state, pids, ping, clock and
/// delay are injectable so tests never wait.
/// </summary>
public sealed class SdkConnectRetryPolicy
{
    /// <summary>Back-off before each retry; its length is the maximum number of retries.</summary>
    public IReadOnlyList<TimeSpan> Backoffs { get; init; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    /// <summary>Longest the service-running poll waits before each retry.</summary>
    public TimeSpan ServicePollTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval between service-state polls.</summary>
    public TimeSpan ServicePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Returns the <c>LdSdkService</c> state (a <see cref="DoctorServiceState"/> value or a <c>ServiceControllerStatus</c> name).</summary>
    public Func<string> ServiceState { get; init; } = DefaultServiceState;

    /// <summary>Returns the pids of the running <c>LdSdkServer</c> processes; replaced in tests.</summary>
    public Func<IReadOnlyCollection<int>> ServerPids { get; init; } = DefaultServerPids;

    /// <summary>
    /// Asked once the service is Running after a restart: true when the SDK server answers. The default accepts any
    /// running service; a pipe or RPC ping can be supplied here.
    /// </summary>
    public Func<CancellationToken, Task<bool>> ServicePing { get; init; } = static _ => Task.FromResult(true);

    /// <summary>
    /// Readiness probe for the retry made when no SDK server restart was seen (the communications layer may have
    /// faulted). The wait before that retry polls it every <see cref="ServicePollInterval"/> and the retry starts as
    /// soon as it answers true; the wait never exceeds the back-off for that retry (the ceiling), which still applies
    /// when it never answers. The wait is always recorded as <see cref="ConnectRetryEntry.CommsRetry"/>.
    /// When <c>null</c> (the default) a built-in probe is used: it answers true once <see cref="ServiceState"/> is
    /// Running, an <c>LdSdkServer</c> process is present with the same pids as on the previous poll (so a server that
    /// is still coming up or being replaced is not ready), and <see cref="ServicePing"/> answers. It needs two polls, so
    /// the earliest it resumes is one <see cref="ServicePollInterval"/> in; when the service cannot be observed it never
    /// answers and the whole ceiling is waited, as the fixed back-off did.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? CommsReady { get; init; }

    /// <summary>Time source for <see cref="SdkRestartRecord.DetectedAt"/>; replaced in tests.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>Waits for the given time; replaced in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    /// <summary>Remembers recovery restarts; a "No valid license" right after one is retried. Replaced in tests.</summary>
    public SdkServiceRecovery Recovery { get; init; } = SdkServiceRecovery.Shared;

    /// <summary>The policy used when the caller supplies none.</summary>
    public static SdkConnectRetryPolicy Default { get; } = new();

    private static IReadOnlyCollection<int> DefaultServerPids() =>
        new SessionLostEvidence(leaseOwner: static () => null).SnapshotServerPids();

    private static string DefaultServiceState() =>
        new DoctorEnvironment(null).GetServiceState(DoctorHandler.SdkServiceName);

    /// <summary>
    /// True when <paramref name="ex"/> (message or inner chain) carries one of the three retryable messages.
    /// Any other error, including <c>RxE_COMM_*</c> comm-layer faults, is not retryable.
    /// </summary>
    /// <param name="ex">The failure raised by the upload connect.</param>
    public static bool IsRetryable(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (RockwellErrorHints.IsMatch(current.Message, RockwellErrorHintIds.ServiceUnreachable)
                || RockwellErrorHints.IsMatch(current.Message, RockwellErrorHintIds.ProjectNotOpen))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="ex"/> (message or inner chain) names <c>LgxSrv_E_FATAL_ERROR</c>.</summary>
    /// <param name="ex">The failure raised by the upload connect.</param>
    public static bool IsFatalError(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current.Message.Contains("LgxSrv_E_FATAL_ERROR", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Runs <paramref name="attempt"/>; on a retryable failure waits and runs it again, up to the policy's limit.
    /// </summary>
    /// <typeparam name="T">The attempt's result type.</typeparam>
    /// <param name="attempt">The connect attempt.</param>
    /// <param name="retries">Receives one entry per retry made (also when the final attempt fails).</param>
    /// <param name="ct">Cancellation token; cancels the waits.</param>
    /// <param name="progress">
    /// Receives <see cref="ConnectPhases.SdkRestartedFor"/> once per retry in which an SDK server restart is seen, so
    /// status polls show that the connect is waiting for the service and has not hung.
    /// </param>
    /// <param name="currentPhase">Returns the connect phase now running; names the phase a crash hit. Null reports <c>unknown</c>.</param>
    /// <param name="giveUp">
    /// When supplied and the retry limit runs out on a dead-service failure, the service is waited on once more so the
    /// crash behind the final failure gets its restart record (<see cref="SdkConnectGiveUp.FinalRestart"/>).
    /// </param>
    /// <returns>The first successful result. The last failure propagates when retries run out or the error is not retryable.</returns>
    public Task<T> RunAsync<T>(
        Func<Task<T>> attempt, List<ConnectRetryEntry> retries, CancellationToken ct,
        IProgress<ConnectProgress>? progress = null, Func<string?>? currentPhase = null,
        SdkConnectGiveUp? giveUp = null) =>
        RunAsync(_ => attempt(), retries, ct, progress, currentPhase, giveUp);

    /// <summary>
    /// Same as the overload without a sink, but the attempt is handed the progress sink to report to. The sink forwards
    /// to <paramref name="progress"/> and, once the attempt reports its first real phase (that is, once it holds the
    /// upload lease and its upload starts, after any <c>lease-wait</c>), re-reads the <c>LdSdkServer</c> pids. A crash
    /// during that attempt is then recorded against the pid that was running when the upload began, not one read before
    /// a long lease wait during which the server may already have been restarted by someone else (G50).
    /// </summary>
    /// <typeparam name="T">The attempt's result type.</typeparam>
    /// <param name="attempt">The connect attempt; it must report its progress to the sink it is given.</param>
    /// <param name="retries">Receives one entry per retry made (also when the final attempt fails).</param>
    /// <param name="ct">Cancellation token; cancels the waits.</param>
    /// <param name="progress">Where the sink forwards; also told of each restart seen.</param>
    /// <param name="currentPhase">Returns the connect phase now running; names the phase a crash hit.</param>
    /// <param name="giveUp">See the overload without a sink.</param>
    /// <returns>The first successful result. The last failure propagates when retries run out or the error is not retryable.</returns>
    public async Task<T> RunAsync<T>(
        Func<IProgress<ConnectProgress>?, Task<T>> attempt, List<ConnectRetryEntry> retries, CancellationToken ct,
        IProgress<ConnectProgress>? progress = null, Func<string?>? currentPhase = null,
        SdkConnectGiveUp? giveUp = null)
    {
        var fatalRetried = false;
        for (var n = 0; ; n++)
        {
            var pidsBefore = SafePids();
            var sink = new PidRefreshProgress(progress, () => pidsBefore = SafePids());
            try
            {
                return await attempt(sink).ConfigureAwait(false);
            }
            catch (TagServiceException raw) when (!fatalRetried && IsFatalError(raw))
            {
                // G31: LgxSrv_E_FATAL_ERROR during the upload is retried exactly once, after the service is
                // Running again and one back-off, whatever the dead-service retry budget is.
                fatalRetried = true;
                ConnectCommsRetry? commsRetry = null;
                var recovery = await WaitForServiceAsync(pidsBefore, currentPhase?.Invoke(), progress, ct).ConfigureAwait(false);
                var fatalWait = recovery.Waited;
                if (!recovery.Resumed)
                {
                    var fatalDelay = Backoffs.Count > 0 ? Backoffs[Math.Min(n, Backoffs.Count - 1)] : TimeSpan.Zero;
                    var (fatalBackoff, fatalComms) = await BackOffAsync(fatalDelay, raw.Error.Message, ct, progress).ConfigureAwait(false);
                    fatalWait += fatalBackoff;
                    commsRetry = fatalComms;
                }
                retries.Add(new ConnectRetryEntry(raw.Error.Message, (long)fatalWait.TotalMilliseconds, recovery.Restart, commsRetry));
            }
            catch (TagServiceException raw)
            {
                // "No valid license" straight after a recovery restart is the restart's side effect, not a
                // licensing problem: say so, and treat it as retryable like the dead-service messages.
                var afterRestart = SdkServiceRecovery.IsNoValidLicense(raw) && Recovery.RestartedRecently();
                var ex = afterRestart ? AnnotateRestart(raw) : raw;
                if (n >= Backoffs.Count || !(afterRestart || IsRetryable(raw)))
                {
                    if (giveUp is not null && n >= Backoffs.Count && (afterRestart || IsRetryable(raw)))
                    {
                        // G48: the crash that triggers the give-up gets its sdkRestart record like every other.
                        giveUp.GaveUp = true;
                        var last = await WaitForServiceAsync(pidsBefore, currentPhase?.Invoke(), progress, ct).ConfigureAwait(false);
                        giveUp.FinalRestart = last.Restart;
                    }
                    if (afterRestart) throw ex;
                    throw;
                }

                var recovery = await WaitForServiceAsync(pidsBefore, currentPhase?.Invoke(), progress, ct).ConfigureAwait(false);
                var waited = recovery.Waited;
                ConnectCommsRetry? commsRetry = null;
                if (!recovery.Resumed)
                {
                    var (backedOff, comms) = await BackOffAsync(Backoffs[n], ex.Error.Message, ct, progress).ConfigureAwait(false);
                    waited += backedOff;
                    commsRetry = comms;
                }
                retries.Add(new ConnectRetryEntry(ex.Error.Message, (long)waited.TotalMilliseconds, recovery.Restart, commsRetry));
            }
        }
    }

    /// <summary>
    /// The wait before a retry that follows no observed SDK restart: polls <see cref="CommsReady"/> (or the built-in
    /// probe; first check at once, then every <see cref="ServicePollInterval"/>) and stops as soon as it answers true,
    /// or at <paramref name="ceiling"/>. Always returns the <c>comms-retry</c> phase.
    /// </summary>
    private async Task<(TimeSpan Waited, ConnectCommsRetry Phase)> BackOffAsync(
        TimeSpan ceiling, string error, CancellationToken ct, IProgress<ConnectProgress>? progress = null)
    {
        var probe = CommsReady ?? BuiltInCommsProbe();
        var waited = TimeSpan.Zero;
        while (true)
        {
            bool ready;
            try { ready = await probe(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ready = false; }
            if (ready || waited >= ceiling) break;

            var step = ServicePollInterval < ceiling - waited ? ServicePollInterval : ceiling - waited;
            await Delay(step, ct).ConfigureAwait(false);
            waited += step;
        }
        var phase = new ConnectCommsRetry((long)waited.TotalMilliseconds, error);
        progress?.Report(ConnectPhases.CommsRetryFor(phase));   // G51: into the live phaseTimings, between the uploads
        return (waited, phase);
    }

    /// <summary>
    /// The default <see cref="CommsReady"/>: stateful, one instance per wait. Ready when the service is Running, an
    /// <c>LdSdkServer</c> is present with the same pids as the previous poll, and <see cref="ServicePing"/> answers.
    /// </summary>
    private Func<CancellationToken, Task<bool>> BuiltInCommsProbe()
    {
        HashSet<int>? previous = null;
        return async ct =>
        {
            string state;
            try { state = ServiceState(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { previous = null; return false; }
            var pids = SafePids();
            if (state is not DoctorServiceState.Running || pids.Count == 0) { previous = null; return false; }

            var current = new HashSet<int>(pids);
            var stable = previous is not null && previous.SetEquals(current);
            previous = current;
            return stable && await ServicePing(ct).ConfigureAwait(false);
        };
    }

    /// <summary>Rewrites a "No valid license" failure so its message names the recovery restart behind it.</summary>
    private static TagServiceException AnnotateRestart(TagServiceException raw) =>
        new(raw.Error with
        {
            Message = "LdSdkService was restarted by service recovery just before this call, and its license check " +
                      "reported \"No valid license\" straight after the restart. This is a transient side effect " +
                      "of the restart, not a licensing fault, and it is retried. " +
                      $"SDK detail: {raw.Error.Message}",
        }, raw);

    /// <summary>
    /// Forwards progress and, on the first report that is not a lease-wait or restart note, runs <c>refresh</c> once:
    /// the attempt now holds the lease and is starting its work.
    /// </summary>
    private sealed class PidRefreshProgress(IProgress<ConnectProgress>? inner, Action refresh)
        : IProgress<ConnectProgress>
    {
        private int _refreshed;

        public void Report(ConnectProgress value)
        {
            if (value.Phase is not (ConnectPhases.LeaseWait or ConnectPhases.SdkRestart or ConnectPhases.CommsRetry)
                && Interlocked.Exchange(ref _refreshed, 1) == 0)
                refresh();
            inner?.Report(value);
        }
    }

    /// <summary>
    /// Takes the host upload lease for the length of a restart wait, when it is free, so a connect queued behind us sees
    /// an owner in the <c>sdk-restart</c> phase (and may extend its ceiling) and no other upload starts on a server that
    /// is coming back. Best effort: <c>null</c> when the lease is held or cannot be written.
    /// </summary>
    private IUploadLeaseHandle? TryHoldLease()
    {
        try { return Recovery.Lease.TryAcquire(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What <see cref="WaitForServiceAsync"/> saw.</summary>
    /// <param name="Waited">Time spent polling.</param>
    /// <param name="Restart">The restart seen, or null.</param>
    /// <param name="Resumed">True when a restart was seen and the service is back and answering: the retry needs no back-off.</param>
    private readonly record struct ServiceWait(TimeSpan Waited, SdkRestartRecord? Restart, bool Resumed);

    private IReadOnlyCollection<int> SafePids()
    {
        try { return ServerPids(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    /// <summary>
    /// Polls until the service is Running (or cannot be observed). A restart is seen when the service was down or the
    /// <c>LdSdkServer</c> pid from the failed attempt is gone; the first time, <paramref name="progress"/> is told. Once
    /// the service is Running, a server process is present (when pids are readable) and <see cref="ServicePing"/>
    /// answers, the wait ends with <see cref="ServiceWait.Resumed"/> so the caller retries at once. The poll never runs
    /// past <see cref="ServicePollTimeout"/>.
    /// </summary>
    private async Task<ServiceWait> WaitForServiceAsync(
        IReadOnlyCollection<int> pidsBefore, string? phase, IProgress<ConnectProgress>? progress, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        var sawDown = false;
        DateTimeOffset? detectedAt = null;
        var phaseName = string.IsNullOrEmpty(phase) ? "unknown" : phase;
        IUploadLeaseHandle? held = null;
        try
        {
            while (true)
            {
                string state;
                try { state = ServiceState(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return new(waited, null, false); }

                // Not installed / not queryable: nothing to wait for; the back-off still applies.
                if (state is DoctorServiceState.NotInstalled or DoctorServiceState.Unsupported)
                    return new(waited, null, false);

                var pids = SafePids();
                if (state is not DoctorServiceState.Running) sawDown = true;
                var pidGone = pidsBefore.Count > 0 && !pidsBefore.Any(pids.Contains);
                var restarting = sawDown || pidGone;

                if (restarting && detectedAt is null)
                {
                    detectedAt = Clock();
                    progress?.Report(ConnectPhases.SdkRestartedFor(phaseName));
                    held = TryHoldLease();
                }
                if (restarting) held?.NoteProgress(ConnectPhases.SdkRestart);

                if (state is DoctorServiceState.Running && !restarting)
                    return new(waited, null, false);

                if (state is DoctorServiceState.Running && (pidsBefore.Count == 0 || pids.Count > 0))
                {
                    bool answers;
                    try { answers = await ServicePing(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { answers = false; }
                    if (answers)
                    {
                        // Down a moment ago and Running now: the service was restarted (by Windows service recovery or
                        // an operator) while we waited. Only this observed transition counts as a restart.
                        Recovery.NoteRestart();
                        return new(waited, Record(), true);
                    }
                }

                if (waited >= ServicePollTimeout)
                    return new(waited, Record(), false);

                await Delay(ServicePollInterval, ct).ConfigureAwait(false);
                waited += ServicePollInterval;

                SdkRestartRecord Record() => new(
                    phaseName,
                    pidsBefore.Count > 0 ? pidsBefore.First() : null,
                    pids.Count > 0 ? pids.First() : null,
                    detectedAt ?? Clock());
            }
        }
        finally
        {
            held?.Dispose();
        }
    }
}
