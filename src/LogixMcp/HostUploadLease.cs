using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Who holds the host-wide upload lease.</summary>
/// <param name="Pid">Process id of the owner.</param>
/// <param name="Token">Unique id of this particular lease, so only its owner releases it.</param>
/// <param name="AcquiredAt">When the lease was taken.</param>
/// <param name="Phase">The connect phase the owner last recorded; <c>null</c> until it records one.</param>
/// <param name="HeartbeatAt">
/// When the owner last recorded progress (a phase change or a heartbeat); <c>null</c> until it records any. A waiter
/// treats an owner whose last record is recent as still making progress (G49).
/// </param>
/// <param name="PhaseSince">When <paramref name="Phase"/> last changed; <c>null</c> until a phase is recorded.</param>
public sealed record UploadLeaseOwner(
    [property: JsonPropertyName("pid")]        int            Pid,
    [property: JsonPropertyName("token")]      string         Token,
    [property: JsonPropertyName("acquiredAt")] DateTimeOffset AcquiredAt,
    [property: JsonPropertyName("phase"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Phase = null,
    [property: JsonPropertyName("heartbeatAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? HeartbeatAt = null,
    [property: JsonPropertyName("phaseSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? PhaseSince = null);

/// <summary>How a wait for the host-wide upload lease ended (the <c>lease-wait</c> phase's outcome, G49).</summary>
public static class LeaseWaitOutcomes
{
    /// <summary>The owner released the lease and this caller took it.</summary>
    public const string OwnerFinished = "owner-finished";

    /// <summary>The wait gave up: the owner stalled or the ceiling passed. The caller gets ConnectBusy.</summary>
    public const string Timeout = "timeout";
}

/// <summary>A held host-wide upload lease. Disposing releases it.</summary>
public interface IUploadLeaseHandle : IDisposable
{
    /// <summary>Records progress in the lease file so waiters can see the owner is alive and moving.</summary>
    /// <param name="phase">The phase now running; <c>null</c> keeps the last recorded one (a pure heartbeat).</param>
    void NoteProgress(string? phase = null);

    /// <summary>Records a heartbeat every <paramref name="interval"/> until the lease is released.</summary>
    /// <param name="interval">Time between heartbeats.</param>
    void StartHeartbeat(TimeSpan interval);
}

/// <summary>
/// Thrown when the host-wide upload lease is still held by another upload after the bounded wait (G33).
/// A typed <see cref="TagServiceErrorKind.ConnectBusy"/> error with a retry-after.
/// </summary>
public sealed class ConnectBusyException : TagServiceException
{
    /// <summary>The lease owner at the time the wait gave up, when it could be read.</summary>
    public UploadLeaseOwner? Owner { get; }

    /// <summary>How long the owner had been in its current phase when the wait gave up; <c>null</c> when unknown.</summary>
    public TimeSpan? OwnerInPhaseFor { get; }

    /// <summary>True when the wait had been granted its one ceiling extension because the owner restarted the SDK service.</summary>
    public bool Extended { get; }

    /// <summary>How long the caller should wait before trying again.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="owner">Current lease owner, if known.</param>
    /// <param name="waited">How long this caller waited.</param>
    /// <param name="retryAfter">Suggested wait before retrying.</param>
    /// <param name="ownerInPhaseFor">How long the owner had been in its current phase; named in the message with the phase.</param>
    /// <param name="extended">True when the wait had its one ceiling extension (the owner restarted the SDK service).</param>
    public ConnectBusyException(
        UploadLeaseOwner? owner, TimeSpan waited, TimeSpan retryAfter,
        TimeSpan? ownerInPhaseFor = null, bool extended = false)
        : base(new TagServiceError(
            TagServiceErrorKind.ConnectBusy,
            $"Another upload to the Logix Designer SDK is in progress on this host" +
            (owner is null ? "" : $" (server process {owner.Pid}, since {owner.AcquiredAt:O}" +
                (owner.Phase is null ? "" : $", in phase \"{owner.Phase}\"" +
                    (ownerInPhaseFor is { } inPhase ? $" for {inPhase.TotalSeconds:0.#} s" : "")) + ")") +
            $"; waited {waited.TotalSeconds:0.#} s and gave up" +
            (extended ? " (the wait was extended once because that upload restarted the SDK service)" : "") +
            ". Nothing was uploaded and the other session was not disturbed.",
            Hint: $"Retry logix_connect after {(int)Math.Ceiling(retryAfter.TotalSeconds)} s.",
            RetryAfterSeconds: (int)Math.Ceiling(retryAfter.TotalSeconds)))
    {
        Owner           = owner;
        RetryAfter      = retryAfter;
        OwnerInPhaseFor = ownerInPhaseFor;
        Extended        = extended;
    }
}

/// <summary>
/// Host-wide lease that serialises SDK upload/open across server processes (G33). One lease file holds the owner's
/// pid; it is created exclusively, so at most one upload on the host runs at a time. A second caller waits up to
/// <see cref="WaitBound"/>, and keeps waiting past it while the owner is making progress (its recorded phase or
/// heartbeat is less than <see cref="StallWindow"/> old), up to <see cref="Ceiling"/>. It then fails with
/// <see cref="ConnectBusyException"/> carrying a retry-after.
/// A lease whose owner process is gone (or that is older than <see cref="MaxAge"/>) is stale and is taken over.
/// While any process holds the lease, <see cref="SdkServiceRecovery"/> will not restart <c>LdSdkService</c>.
/// Process liveness, the clock and the delay are injectable so tests can stand in for two server processes.
/// </summary>
public sealed class HostUploadLease
{
    /// <summary>Default longest wait for the lease before refusing with ConnectBusy.</summary>
    public static readonly TimeSpan DefaultWaitBound = TimeSpan.FromSeconds(30);

    /// <summary>Default time without a phase change or heartbeat after which the owner counts as stalled.</summary>
    public static readonly TimeSpan DefaultStallWindow = TimeSpan.FromSeconds(15);

    /// <summary>Default absolute longest wait, however much progress the owner makes.</summary>
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(120);

    /// <summary>Default extra wait granted once when the owner records an <c>sdk-restart</c> phase.</summary>
    public static readonly TimeSpan DefaultRestartExtension = TimeSpan.FromSeconds(120);

    /// <summary>Default retry-after reported with ConnectBusy.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>Default age after which a lease is treated as stale even if its owner pid is alive.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new();

    private readonly string _path;
    private readonly int _pid;
    private readonly Func<int, bool> _isAlive;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollInterval;

    /// <summary>How long <see cref="AcquireAsync"/> waits before refusing.</summary>
    public TimeSpan WaitBound { get; }

    /// <summary>How recent the owner's last recorded progress must be for the wait to run past <see cref="WaitBound"/>.</summary>
    public TimeSpan StallWindow { get; }

    /// <summary>Absolute longest wait (never less than <see cref="WaitBound"/>).</summary>
    public TimeSpan Ceiling { get; }

    /// <summary>
    /// How much the ceiling grows, once per wait, when the owner records an <c>sdk-restart</c> phase during it (G50). The
    /// extra time still runs only while the owner keeps making progress under <see cref="StallWindow"/>.
    /// </summary>
    public TimeSpan RestartExtension { get; }

    /// <summary>Retry-after reported when the wait runs out.</summary>
    public TimeSpan RetryAfter { get; }

    /// <summary>Age after which a lease is stale regardless of its owner.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>The lease shared by every upload in this process.</summary>
    public static HostUploadLease Default { get; } = new();

    /// <summary>Creates a lease handle; the arguments exist so tests can use their own file, pids and clock.</summary>
    /// <param name="path">Lease file; default is a fixed file under the temp directory.</param>
    /// <param name="pid">Owner pid written to the file; default the current process.</param>
    /// <param name="isProcessAlive">Liveness check for an owner pid; default <see cref="Process"/> lookup.</param>
    /// <param name="waitBound">Longest wait; default <see cref="DefaultWaitBound"/>.</param>
    /// <param name="retryAfter">Retry-after for ConnectBusy; default <see cref="DefaultRetryAfter"/>.</param>
    /// <param name="maxAge">Staleness age; default <see cref="DefaultMaxAge"/>.</param>
    /// <param name="pollInterval">Poll interval while waiting; default 250 ms.</param>
    /// <param name="clock">Time source; default <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="delay">Waits between polls; default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="stallWindow">Owner progress age that counts as stalled; default <see cref="DefaultStallWindow"/>.</param>
    /// <param name="ceiling">Absolute longest wait; default <see cref="DefaultCeiling"/>.</param>
    /// <param name="restartExtension">One-time ceiling extension for an owner that restarted the SDK; default <see cref="DefaultRestartExtension"/>.</param>
    public HostUploadLease(
        string? path = null, int? pid = null, Func<int, bool>? isProcessAlive = null,
        TimeSpan? waitBound = null, TimeSpan? retryAfter = null, TimeSpan? maxAge = null,
        TimeSpan? pollInterval = null, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? stallWindow = null, TimeSpan? ceiling = null, TimeSpan? restartExtension = null)
    {
        _path         = path ?? Path.Combine(Path.GetTempPath(), "LogixMcp", "ld-sdk-upload.lease");
        _pid          = pid ?? Environment.ProcessId;
        _isAlive      = isProcessAlive ?? DefaultIsAlive;
        WaitBound     = waitBound ?? DefaultWaitBound;
        RetryAfter    = retryAfter ?? DefaultRetryAfter;
        MaxAge        = maxAge ?? DefaultMaxAge;
        StallWindow   = stallWindow ?? DefaultStallWindow;
        var cap       = ceiling ?? DefaultCeiling;
        Ceiling       = cap > WaitBound ? cap : WaitBound;
        RestartExtension = restartExtension ?? DefaultRestartExtension;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _clock        = clock ?? (() => DateTimeOffset.UtcNow);
        _delay        = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    private static bool DefaultIsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The live owner of the lease (any process on the host, including this one), or <c>null</c> when it is free.
    /// A stale lease reads as free.
    /// </summary>
    public UploadLeaseOwner? CurrentOwner()
    {
        var owner = Read();
        return owner is not null && !IsStale(owner) ? owner : null;
    }

    /// <summary>True when any process on the host holds a live upload lease.</summary>
    public bool IsHeld => CurrentOwner() is not null;

    /// <summary>Takes the lease if it is free right now; otherwise returns <c>null</c>.</summary>
    /// <returns>A handle that releases the lease when disposed, or <c>null</c>.</returns>
    public IUploadLeaseHandle? TryAcquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var mine = new UploadLeaseOwner(_pid, Guid.NewGuid().ToString("N"), _clock());
            try
            {
                using var fs = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(fs, mine, Json);
                return new Handle(this, mine.Token);
            }
            catch (IOException) when (File.Exists(_path))
            {
                var owner = Read();
                // An unreadable file may be another process mid-write: only junk older than a few seconds is stale.
                var junk = owner is null && DateTime.UtcNow - File.GetLastWriteTimeUtc(_path) > TimeSpan.FromSeconds(5);
                if (junk || (owner is not null && IsStale(owner)))
                {
                    // Owner gone (crashed) or the file is unreadable junk: clear it and try again.
                    try { File.Delete(_path); } catch (IOException) { /* lost the race; loop */ }
                    continue;
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Takes the lease, waiting up to <see cref="WaitBound"/> for another upload to finish, and past it for as long as
    /// the owner keeps recording progress (a phase change or heartbeat within <see cref="StallWindow"/>), up to
    /// <see cref="Ceiling"/>. An owner that has recorded no progress, or has stopped, is not waited for past the bound.
    /// When the owner records an <c>sdk-restart</c> phase during the wait, the ceiling is raised once by
    /// <see cref="RestartExtension"/>; the extra time runs only while the owner keeps making progress, and a second
    /// restart does not extend it again (G50).
    /// </summary>
    /// <param name="ct">Cancels the wait.</param>
    /// <param name="onWait">
    /// Called once, with the current owner (when it can be read), when the lease is not free on the first try and
    /// the caller starts to wait. Not called when the lease is taken at once.
    /// </param>
    /// <param name="onWaitEnd">
    /// Called once when a wait that began ends, with a <see cref="LeaseWaitOutcomes"/> value: owner-finished when the
    /// lease was taken, timeout just before <see cref="ConnectBusyException"/> is thrown. Not called for a cancelled wait.
    /// </param>
    /// <returns>A handle that releases the lease when disposed.</returns>
    /// <exception cref="ConnectBusyException">The owner stalled after the wait bound, or the ceiling passed.</exception>
    public async Task<IUploadLeaseHandle> AcquireAsync(
        CancellationToken ct = default, Action<UploadLeaseOwner?>? onWait = null, Action<string>? onWaitEnd = null)
    {
        var waited = TimeSpan.Zero;
        var notified = false;
        var ceiling = Ceiling;
        var extended = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryAcquire() is { } handle)
            {
                if (notified) onWaitEnd?.Invoke(LeaseWaitOutcomes.OwnerFinished);
                return handle;
            }
            if (!notified)
            {
                notified = true;
                onWait?.Invoke(CurrentOwner());
            }
            var current = CurrentOwner();
            if (!extended && current?.Phase == ConnectPhases.SdkRestart)
            {
                // The owner's SDK server was restarted (by the SDK itself) while it held the lease: its upload will start over, so this wait
                // gets one more ceiling's worth of time, still gated by the owner's progress.
                extended = true;
                ceiling  = Ceiling + RestartExtension;
            }
            if (waited >= ceiling || (waited >= WaitBound && !OwnerMakingProgress()))
            {
                onWaitEnd?.Invoke(LeaseWaitOutcomes.Timeout);
                var last = CurrentOwner();
                TimeSpan? inPhase = last?.PhaseSince is { } since ? _clock() - since : null;
                throw new ConnectBusyException(last, waited, RetryAfter, inPhase, extended);
            }

            var limit = waited < WaitBound ? WaitBound - waited : ceiling - waited;
            var step = _pollInterval < limit ? _pollInterval : limit;
            await _delay(step, ct).ConfigureAwait(false);
            waited += step;
        }
    }

    /// <summary>
    /// True when the lease is free (the next try will take it) or its owner recorded a phase or heartbeat within
    /// <see cref="StallWindow"/>. An owner that never recorded progress is not making any.
    /// </summary>
    private bool OwnerMakingProgress()
    {
        var owner = CurrentOwner();
        if (owner is null) return true;
        return owner.HeartbeatAt is { } at && _clock() - at <= StallWindow;
    }

    /// <summary>Rewrites the lease file with fresh progress, only while <paramref name="token"/> still owns it.</summary>
    private void RecordProgress(string token, string? phase)
    {
        try
        {
            var current = Read();
            if (current is null || current.Token != token) return;
            var now = _clock();
            var phaseChanged = phase is not null && phase != current.Phase;
            var updated = current with
            {
                Phase       = phase ?? current.Phase,
                HeartbeatAt = now,
                PhaseSince  = phaseChanged || (phase is not null && current.PhaseSince is null) ? now : current.PhaseSince,
            };
            var tmp = _path + "." + token + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(updated, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a missed record only makes this owner look a little older to waiters.
        }
    }

    private bool IsStale(UploadLeaseOwner owner) =>
        _clock() - owner.AcquiredAt > MaxAge || !_isAlive(owner.Pid);

    private UploadLeaseOwner? Read()
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<UploadLeaseOwner>(fs, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Release(string token)
    {
        // Only the owner of this exact lease removes the file; a lease taken over as stale stays with its new owner.
        if (Read()?.Token != token) return;
        try { File.Delete(_path); } catch (IOException) { /* best effort */ }
    }

    private sealed class Handle(HostUploadLease owner, string token) : IUploadLeaseHandle
    {
        private readonly object _gate = new();
        private int _disposed;
        private Timer? _heartbeat;

        public void NoteProgress(string? phase = null)
        {
            lock (_gate)
            {
                if (_disposed == 0) owner.RecordProgress(token, phase);
            }
        }

        public void StartHeartbeat(TimeSpan interval)
        {
            NoteProgress();
            lock (_gate)
            {
                if (_disposed != 0 || _heartbeat is not null) return;
                _heartbeat = new Timer(_ => NoteProgress(), null, interval, interval);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate)
            {
                _heartbeat?.Dispose();
                _heartbeat = null;
                owner.Release(token);
            }
        }
    }
}

/// <summary>
/// Structured <c>logix_connect</c> error returned when the host-wide upload lease stayed held past the bounded wait.
/// </summary>
/// <param name="Error">What happened.</param>
/// <param name="Kind">Always <c>"ConnectBusy"</c>.</param>
/// <param name="RetryAfterSeconds">Seconds to wait before calling <c>logix_connect</c> again.</param>
/// <param name="OwnerPid">Process id of the server process holding the lease, when known.</param>
/// <param name="Hint">What to do next.</param>
public sealed record ConnectBusyError(
    [property: JsonPropertyName("error")]             string Error,
    [property: JsonPropertyName("kind")]              string Kind,
    [property: JsonPropertyName("retryAfterSeconds")] int    RetryAfterSeconds,
    [property: JsonPropertyName("ownerPid")]          int?   OwnerPid,
    [property: JsonPropertyName("hint")]              string Hint)
{
    /// <summary>
    /// Retries this connect made before it queued for the lease (each with the SDK restart it saw); omitted when none.
    /// </summary>
    [JsonPropertyName("retries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConnectRetryEntry>? Retries { get; init; }

    /// <summary>Every SDK restart this connect survived before it gave up, in order; omitted when none (G50).</summary>
    [JsonPropertyName("sdkRestarts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SdkRestartRecord>? SdkRestarts { get; init; }

    /// <summary>Builds the payload from the typed exception.</summary>
    /// <param name="ex">A <see cref="TagServiceErrorKind.ConnectBusy"/> failure.</param>
    /// <param name="crashes">SDK restarts this connect survived; the message names each one.</param>
    public static ConnectBusyError From(TagServiceException ex, IReadOnlyList<SdkRestartRecord>? crashes = null) => new(
        ex.Error.Message + CrashSentence(crashes), ErrorKinds.ConnectBusy, ex.Error.RetryAfterSeconds ?? 30,
        (ex as ConnectBusyException)?.Owner?.Pid,
        ex.Error.Hint ?? "Retry logix_connect shortly.");

    /// <summary>The sentence that names the crashes a connect survived; empty when there were none.</summary>
    private static string CrashSentence(IReadOnlyList<SdkRestartRecord>? crashes)
    {
        if (crashes is not { Count: > 0 }) return "";
        var each = crashes.Select(c =>
            $"in the {c.Phase} phase (LdSdkServer pid {c.OldPid?.ToString() ?? "unknown"} to {c.NewPid?.ToString() ?? "unknown"}, " +
            $"detected {c.DetectedAt:O})");
        return $" Before it waited, this connect survived {crashes.Count} SDK service crash{(crashes.Count == 1 ? "" : "es")}: " +
               string.Join("; ", each) + ".";
    }
}
