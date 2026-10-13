/// <summary>
/// One phase boundary reached during the long SDK connect path (<c>logix_connect</c>,
/// <c>backend="sdk"</c>, <c>sdkMode="online"</c> with a comm path). Reported to an optional
/// <see cref="IProgress{T}"/>; the MCP layer turns each report into a progress notification.
/// </summary>
/// <param name="Phase">Phase name: one of <see cref="ConnectPhases.Upload"/>, <see cref="ConnectPhases.SaveL5x"/>,
/// <see cref="ConnectPhases.Parse"/>, <see cref="ConnectPhases.GateDerivation"/>.</param>
/// <param name="Step">1-based position of the phase in the sequence.</param>
/// <param name="TotalSteps">Number of phases in the sequence (<see cref="ConnectPhases.Total"/>).</param>
/// <param name="Message">Human-readable description of what is starting.</param>
/// <param name="OwnerPid">
/// For <see cref="ConnectPhases.LeaseWait"/>: the process id of the server process holding the host upload lease,
/// when it could be read; otherwise <c>null</c>.
/// </param>
/// <param name="Outcome">
/// For the closing <see cref="ConnectPhases.LeaseWait"/> report: how the wait ended, a <see cref="LeaseWaitOutcomes"/>
/// value; <c>null</c> for every other report.
/// </param>
/// <param name="DurationMs">For the <see cref="ConnectPhases.CommsRetry"/> report: how long the wait ran; <c>null</c> otherwise.</param>
/// <param name="Error">For the <see cref="ConnectPhases.CommsRetry"/> report: the error that caused the retry; <c>null</c> otherwise.</param>
public sealed record ConnectProgress(
    string Phase, int Step, int TotalSteps, string Message, int? OwnerPid = null, string? Outcome = null,
    long? DurationMs = null, string? Error = null);

/// <summary>
/// The phases of the SDK upload-connect path, in order, with a factory for the matching
/// <see cref="ConnectProgress"/> report. Each report is made when the phase <em>starts</em>.
/// </summary>
public static class ConnectPhases
{
    /// <summary>Pulling the controller's program image into a working ACD.</summary>
    public const string Upload = "upload";

    /// <summary>Saving the uploaded project as a detailed L5X.</summary>
    public const string SaveL5x = "save-l5x";

    /// <summary>Parsing the saved L5X for type resolution.</summary>
    public const string Parse = "parse";

    /// <summary>Deriving the write-gate policy from the parsed project.</summary>
    public const string GateDerivation = "gate-derivation";

    /// <summary>
    /// Opening the open project's file with the SDK (<c>backend="sdk"</c>, file-based route). Not part of
    /// <see cref="All"/>: it has no entry in the upload-connect timings, and exists so a slow file-based connect
    /// reports a real phase instead of <c>starting</c>.
    /// </summary>
    public const string OpenProject = "open-project";

    /// <summary>
    /// Waiting for the host-wide upload lease, which another server process holds. Reported only when the connect
    /// had to wait. Not part of <see cref="All"/>: it is reported as <c>lease-wait</c> beside the other timings and is
    /// not counted in their total.
    /// </summary>
    public const string LeaseWait = "lease-wait";

    /// <summary>
    /// The SDK server crashed during a connect and the retry is waiting for <c>LdSdkService</c> (G46).
    /// Not part of <see cref="All"/> and not timed: it is a note on the phase that crashed, shown in progress
    /// notifications and by <c>logix_connect_status</c> so a caller does not think the connect has hung.
    /// </summary>
    public const string SdkRestart = "sdk-restart";

    /// <summary>The progress text of the <see cref="SdkRestart"/> report.</summary>
    public const string SdkRestartText = "sdk-service-restarted; waiting for LdSdkService";

    /// <summary>
    /// The wait before a retry made after a failure with no SDK server restart (G51). Reported once the wait has ended,
    /// with its duration and the error text, so it lands in the live <c>phaseTimings</c> between the two uploads.
    /// Not part of <see cref="All"/> and not a step: a note on the retry, like <see cref="SdkRestart"/>.
    /// </summary>
    public const string CommsRetry = "comms-retry";

    /// <summary>Number of phases.</summary>
    public const int Total = 4;

    /// <summary>The phases in execution order.</summary>
    public static IReadOnlyList<string> All { get; } = [Upload, SaveL5x, Parse, GateDerivation];

    /// <summary>Builds the <see cref="LeaseWait"/> report.</summary>
    /// <param name="ownerPid">Process id of the lease owner, when known.</param>
    /// <returns>The report; its step is 0 because the wait comes before the first phase.</returns>
    public static ConnectProgress LeaseWaitFor(int? ownerPid) => new(
        LeaseWait, 0, Total,
        "Waiting for another upload to the Logix Designer SDK to finish" + (ownerPid is null ? "" : $" (server process {ownerPid})"),
        ownerPid);

    /// <summary>Builds the closing <see cref="LeaseWait"/> report that says how the wait ended.</summary>
    /// <param name="outcome">A <see cref="LeaseWaitOutcomes"/> value.</param>
    /// <returns>The report; it ends the wait, it does not start a new phase.</returns>
    public static ConnectProgress LeaseWaitEnded(string outcome) => new(
        LeaseWait, 0, Total,
        outcome == LeaseWaitOutcomes.OwnerFinished
            ? "The other upload finished; starting this one"
            : "Gave up waiting for the other upload to finish",
        null, outcome);

    /// <summary>Builds the <see cref="SdkRestart"/> report for a crash in <paramref name="crashedPhase"/>.</summary>
    /// <param name="crashedPhase">The phase that was running when the SDK server went away.</param>
    /// <returns>The report; it keeps the step of the crashed phase (1 when unknown) so progress does not go backwards.</returns>
    public static ConnectProgress SdkRestartedFor(string crashedPhase)
    {
        var step = All.ToList().IndexOf(crashedPhase) + 1;
        return new(SdkRestart, Math.Max(1, step), Total, SdkRestartText);
    }

    /// <summary>Builds the <see cref="CommsRetry"/> report for a finished comms-retry wait.</summary>
    /// <param name="commsRetry">The wait: its duration and the error that triggered the retry.</param>
    /// <returns>The report; it keeps the step of the upload (1) so progress does not go backwards.</returns>
    public static ConnectProgress CommsRetryFor(ConnectCommsRetry commsRetry) => new(
        CommsRetry, 1, Total, "Waited for the communications layer before retrying the upload",
        DurationMs: commsRetry.DurationMs, Error: commsRetry.Error);

    /// <summary>Builds the report for <paramref name="phase"/> (one of the constants above).</summary>
    /// <param name="phase">Phase name.</param>
    /// <returns>The report, with its step number and a message.</returns>
    public static ConnectProgress For(string phase) => phase switch
    {
        Upload         => new(Upload,         1, Total, "Uploading the controller's project image"),
        SaveL5x        => new(SaveL5x,        2, Total, "Saving the uploaded project as detailed L5X"),
        Parse          => new(Parse,          3, Total, "Parsing the L5X"),
        GateDerivation => new(GateDerivation, 4, Total, "Deriving the write-gate policy"),
        OpenProject    => new(OpenProject,    1, 1,     "Opening the project with the SDK"),
        LeaseWait      => LeaseWaitFor(null),
        SdkRestart     => SdkRestartedFor(Upload),
        CommsRetry     => CommsRetryFor(new ConnectCommsRetry(0, "")),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown connect phase."),
    };
}

/// <summary>
/// How long each phase of the SDK upload-connect path took, in milliseconds. Carried in the connect
/// result (<c>phaseTimings</c>), in the audit <c>connect</c> event and in <c>logix_doctor</c>
/// (<c>lastConnect</c>). Measurement only: no phase behaves differently because it is timed.
/// </summary>
/// <param name="UploadMs">Pulling the controller's program image into a working ACD.</param>
/// <param name="SaveL5xMs">Saving the uploaded project as a detailed L5X.</param>
/// <param name="ParseMs">Parsing the saved L5X.</param>
/// <param name="GateMs">Gate derivation and attaching the session, excluding <paramref name="IdentityMs"/>.</param>
/// <param name="IdentityMs">Choosing the tag route (CIP connect and same-controller identity check).</param>
/// <param name="TotalMs">The upload phase to the end of connect; equals the sum of the other four (lease wait excluded).</param>
/// <param name="LeaseWait">
/// How long the connect waited for the host-wide upload lease and whose it was; <c>null</c> (and omitted from the JSON)
/// when the lease was free.
/// </param>
/// <param name="SdkRestart">
/// The <c>LdSdkServer</c> restart the connect lived through (phase it hit, pid before and after, when it was seen);
/// <c>null</c> (and omitted from the JSON) when none happened. With several, the last one.
/// </param>
/// <param name="CommsRetry">
/// The <c>comms-retry</c> phase: how long the connect waited for the communications layer before a retry, and the
/// error that caused it; <c>null</c> (and omitted from the JSON) when no such retry happened. With several, the last one.
/// </param>
public sealed record ConnectPhaseTimings(
    [property: System.Text.Json.Serialization.JsonPropertyName("upload")]   long UploadMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("saveL5x")]  long SaveL5xMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("parse")]    long ParseMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("gate")]     long GateMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("identity")] long IdentityMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("total")]    long TotalMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("lease-wait"),
               System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ConnectLeaseWait? LeaseWait = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("sdkRestart"),
               System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SdkRestartRecord? SdkRestart = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("comms-retry"),
               System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ConnectCommsRetry? CommsRetry = null);

/// <summary>The <c>lease-wait</c> phase of a connect that had to wait for the host-wide upload lease.</summary>
/// <param name="DurationMs">Milliseconds from starting to wait until the upload began.</param>
/// <param name="OwnerPid">Process id of the server process that held the lease, or <c>null</c> when it could not be read.</param>
/// <param name="Outcome">
/// How the wait ended: <see cref="LeaseWaitOutcomes.OwnerFinished"/> or <see cref="LeaseWaitOutcomes.Timeout"/>;
/// <c>null</c> (and omitted from the JSON) when the connect ended before the wait did (e.g. cancelled).
/// </param>
public sealed record ConnectLeaseWait(
    [property: System.Text.Json.Serialization.JsonPropertyName("durationMs")] long DurationMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("ownerPid"),
               System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? OwnerPid,
    [property: System.Text.Json.Serialization.JsonPropertyName("outcome"),
               System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Outcome = null);

/// <summary>
/// An <see cref="IProgress{T}"/> that notes when each <see cref="ConnectPhases"/> phase starts (then forwards
/// the report unchanged) so the durations can be read once connect ends.
/// </summary>
public sealed class ConnectPhaseRecorder : IProgress<ConnectProgress>
{
    private readonly IProgress<ConnectProgress>? _inner;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<string, long> _starts = [];
    private long? _leaseWaitStart;
    private int? _leaseOwnerPid;
    private long? _leaseWaitEnd;
    private string? _leaseOutcome;
    private string? _currentPhase;
    private SdkRestartRecord? _sdkRestart;
    private ConnectCommsRetry? _commsRetry;
    private readonly object _gate = new();

    /// <summary>Creates a recorder.</summary>
    /// <param name="inner">Receiver the reports are forwarded to; <c>null</c> forwards nothing.</param>
    public ConnectPhaseRecorder(IProgress<ConnectProgress>? inner = null) => _inner = inner;

    /// <summary>Notes the start of <paramref name="value"/>'s phase and forwards the report.</summary>
    /// <param name="value">The phase report.</param>
    public void Report(ConnectProgress value)
    {
        lock (_gate)
        {
            if (value.Phase == ConnectPhases.LeaseWait)
            {
                _leaseWaitStart ??= _clock.ElapsedMilliseconds;
                _leaseOwnerPid  ??= value.OwnerPid;
                if (value.Outcome is not null)
                {
                    _leaseOutcome = value.Outcome;
                    _leaseWaitEnd ??= _clock.ElapsedMilliseconds;
                }
            }
            else if (value.Phase is ConnectPhases.SdkRestart or ConnectPhases.CommsRetry) { /* a note on the retry, not a phase */ }
            else
            {
                _starts.TryAdd(value.Phase, _clock.ElapsedMilliseconds);
                _currentPhase = value.Phase;
            }
        }
        _inner?.Report(value);
    }

    /// <summary>The real phase last reported (not the lease wait or the restart note); null before the first.</summary>
    public string? CurrentPhase { get { lock (_gate) return _currentPhase; } }

    /// <summary>Records the SDK server restart the connect lived through, to appear in <see cref="Snapshot"/>.</summary>
    /// <param name="restart">The restart; null clears it.</param>
    public void NoteSdkRestart(SdkRestartRecord? restart)
    {
        lock (_gate) _sdkRestart = restart;
    }

    /// <summary>Records the comms-retry phase the connect went through, to appear in <see cref="Snapshot"/>.</summary>
    /// <param name="commsRetry">The phase; null clears it.</param>
    public void NoteCommsRetry(ConnectCommsRetry? commsRetry)
    {
        lock (_gate) _commsRetry = commsRetry;
    }

    /// <summary>
    /// Reads the durations now, treating now as the end of connect.
    /// </summary>
    /// <param name="identityMs">Milliseconds spent choosing the tag route; taken out of the gate phase.</param>
    /// <returns>The timings; a phase that never started is 0.</returns>
    public ConnectPhaseTimings Snapshot(long identityMs = 0)
    {
        long end = _clock.ElapsedMilliseconds;
        lock (_gate)
        {
            long Duration(string phase)
            {
                if (!_starts.TryGetValue(phase, out var start)) return 0;
                var next = ConnectPhases.All.SkipWhile(p => p != phase).Skip(1)
                    .Where(_starts.ContainsKey).Select(p => _starts[p]).DefaultIfEmpty(end).First();
                return Math.Max(0, next - start);
            }
            var gate = Math.Max(0, Duration(ConnectPhases.GateDerivation) - Math.Max(0, identityMs));
            var upload = Duration(ConnectPhases.Upload);
            var save   = Duration(ConnectPhases.SaveL5x);
            var parse  = Duration(ConnectPhases.Parse);
            ConnectLeaseWait? leaseWait = null;
            if (_leaseWaitStart is { } waitStart)
            {
                // The wait ends when the first real phase starts (or, if none did, when connect ended).
                var waitEnd = _leaseWaitEnd
                    ?? ConnectPhases.All.Where(_starts.ContainsKey).Select(p => _starts[p]).DefaultIfEmpty(end).Min();
                leaseWait = new ConnectLeaseWait(Math.Max(0, waitEnd - waitStart), _leaseOwnerPid, _leaseOutcome);
            }
            return new ConnectPhaseTimings(upload, save, parse, gate, Math.Max(0, identityMs),
                upload + save + parse + gate + Math.Max(0, identityMs), leaseWait, _sdkRestart, _commsRetry);
        }
    }
}
