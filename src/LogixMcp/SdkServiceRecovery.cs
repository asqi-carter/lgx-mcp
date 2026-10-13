using System.Text.RegularExpressions;

/// <summary>What <see cref="SdkServiceRecovery.TryRestartAsync"/> decided.</summary>
public enum SdkRestartOutcome
{
    /// <summary>The restart action ran.</summary>
    Restarted,
    /// <summary>Not restarted: a connect in this process is uploading from the controller.</summary>
    SkippedUploadInProgress,
    /// <summary>Not restarted: an upload in ANOTHER server process holds the host-wide upload lease (G33).</summary>
    SkippedHostUploadLease,
    /// <summary>Not restarted: another server process holds the host-wide restart lock and did not release it in time.</summary>
    SkippedLockBusy,
    /// <summary>Not restarted: once the lock was ours the service no longer needed a restart (another process recovered it).</summary>
    SkippedNotNeeded,
}

/// <summary>
/// Guards the decision to restart the shared <c>LdSdkService</c> so concurrent connects cannot break each other.
/// Two connects once crashed <c>LdSdkServer</c>; the recovery restarted the service while the
/// other client was mid-upload, and that client's retry then failed with a misleading "No valid license".
/// <list type="bullet">
///   <item>A connect brackets its upload with <see cref="BeginUpload"/>; while any is open in this process
///         no restart is made.</item>
///   <item>A connect also takes the host-wide <see cref="HostUploadLease"/> (<see cref="AcquireUploadAsync"/>), so
///         uploads are serialised across server processes, and no restart is made while ANY process on the host
///         holds it (G33).</item>
///   <item>The decision runs inside a host-wide named mutex (<see cref="DefaultMutexName"/>), so a second
///         server process waits (up to the lock timeout) or skips.</item>
///   <item>A restart (made through <see cref="TryRestartAsync"/>, or observed because Windows service recovery
///         did it) is remembered with <see cref="NoteRestart"/>, so a "No valid license" straight after it is
///         recognised as the restart's side effect and retried.</item>
/// </list>
/// The server itself never runs a service-control command: the restart action is supplied by the
/// caller, and none is registered in production, so in production this type only vetoes and records.
/// </summary>
public sealed class SdkServiceRecovery
{
    /// <summary>Name of the host-wide mutex that guards the restart decision.</summary>
    public const string DefaultMutexName = @"Global\LogixMcp.LdSdkService.Recovery";

    /// <summary>How long after a restart a "No valid license" is still attributed to it.</summary>
    public static readonly TimeSpan DefaultRestartWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long a second server process waits for the host-wide lock before it skips.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex NoValidLicense = new("No valid license", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _mutexName;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _restartWindow;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HostUploadLease _lease;
    private readonly object _gate = new();
    private int _activeUploads;
    private DateTimeOffset? _lastRestart;

    /// <summary>The instance every connect in this process shares.</summary>
    public static SdkServiceRecovery Shared { get; } = new();

    /// <summary>Creates a coordinator; the arguments exist so tests can use their own mutex, timeouts and clock.</summary>
    /// <param name="mutexName">Name of the host-wide mutex; default <see cref="DefaultMutexName"/>.</param>
    /// <param name="lockTimeout">Longest wait for the mutex; default <see cref="DefaultLockTimeout"/>.</param>
    /// <param name="restartWindow">How long a restart explains a license error; default <see cref="DefaultRestartWindow"/>.</param>
    /// <param name="clock">Time source; default <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <param name="lease">Host-wide upload lease; default <see cref="HostUploadLease.Default"/>.</param>
    public SdkServiceRecovery(
        string? mutexName = null, TimeSpan? lockTimeout = null, TimeSpan? restartWindow = null,
        Func<DateTimeOffset>? clock = null, HostUploadLease? lease = null)
    {
        _lease         = lease ?? HostUploadLease.Default;
        _mutexName     = mutexName ?? DefaultMutexName;
        _lockTimeout   = lockTimeout ?? DefaultLockTimeout;
        _restartWindow = restartWindow ?? DefaultRestartWindow;
        _clock         = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Number of uploads open in this process.</summary>
    public int ActiveUploads { get { lock (_gate) return _activeUploads; } }

    /// <summary>Marks an upload as started; dispose the result when the upload ends (success or failure).</summary>
    /// <returns>A scope that ends the upload when disposed. Disposing twice is harmless.</returns>
    public IDisposable BeginUpload()
    {
        lock (_gate) _activeUploads++;
        return new UploadScope(this);
    }

    /// <summary>The host-wide upload lease this coordinator takes and consults.</summary>
    public HostUploadLease Lease => _lease;

    /// <summary>
    /// Takes the host-wide upload lease (waiting up to its bound for another process's upload) and marks an upload
    /// as started in this process. Dispose the result when the upload ends, success or failure.
    /// </summary>
    /// <param name="ct">Cancels the wait for the lease.</param>
    /// <param name="onWait">Called once, with the lease owner, when the lease is held and this caller starts to wait.</param>
    /// <param name="onWaitEnd">Called once when a wait ends, with a <see cref="LeaseWaitOutcomes"/> value.</param>
    /// <returns>A scope that releases the lease and ends the upload when disposed; it also records progress in the lease.</returns>
    /// <exception cref="ConnectBusyException">The owner stalled past the wait bound or the wait ceiling passed; nothing was started.</exception>
    public async Task<IUploadLeaseHandle> AcquireUploadAsync(
        CancellationToken ct = default, Action<UploadLeaseOwner?>? onWait = null, Action<string>? onWaitEnd = null)
    {
        var lease = await _lease.AcquireAsync(ct, onWait, onWaitEnd).ConfigureAwait(false);
        return new CompositeScope(BeginUpload(), lease);
    }

    /// <summary>Records that <c>LdSdkService</c> was restarted just now (by us or by Windows service recovery).</summary>
    public void NoteRestart()
    {
        lock (_gate) _lastRestart = _clock();
    }

    /// <summary>True when a restart was recorded within the restart window.</summary>
    public bool RestartedRecently()
    {
        lock (_gate)
            return _lastRestart is { } t && _clock() - t <= _restartWindow;
    }

    /// <summary>True when the exception chain says "No valid license".</summary>
    /// <param name="ex">The failure to inspect.</param>
    public static bool IsNoValidLicense(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (NoValidLicense.IsMatch(e.Message))
                return true;
        return false;
    }

    /// <summary>
    /// Decides whether to restart the service and, if so, runs <paramref name="restart"/>. The decision is made
    /// while holding the host-wide mutex, so only one server process on the host decides at a time.
    /// </summary>
    /// <param name="restart">The restart action. It runs on the thread that holds the mutex.</param>
    /// <param name="restartStillNeeded">
    /// Asked once the lock is held; return false when the service is already healthy (another process
    /// recovered it while we waited). Null means "still needed".
    /// </param>
    /// <param name="ct">Cancellation token for the wait and the restart action.</param>
    /// <returns>What was decided; <see cref="SdkRestartOutcome.Restarted"/> only when the action ran and returned.</returns>
    public Task<SdkRestartOutcome> TryRestartAsync(
        Func<CancellationToken, Task> restart, Func<bool>? restartStillNeeded = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restart);
        if (ActiveUploads > 0)
            return Task.FromResult(SdkRestartOutcome.SkippedUploadInProgress);
        if (_lease.IsHeld)
            return Task.FromResult(SdkRestartOutcome.SkippedHostUploadLease);

        // A Mutex belongs to the thread that took it, so take, decide, restart and release on one thread.
        return Task.Factory.StartNew(
            () => DecideUnderMutex(restart, restartStillNeeded, ct),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private SdkRestartOutcome DecideUnderMutex(
        Func<CancellationToken, Task> restart, Func<bool>? restartStillNeeded, CancellationToken ct)
    {
        using var mutex = new Mutex(false, _mutexName);
        bool held;
        try { held = mutex.WaitOne(_lockTimeout); }
        catch (AbandonedMutexException) { held = true; }   // the previous holder died; the lock is ours
        if (!held)
            return SdkRestartOutcome.SkippedLockBusy;

        try
        {
            // Re-check under the lock: an upload may have started while we waited.
            if (ActiveUploads > 0)
                return SdkRestartOutcome.SkippedUploadInProgress;
            if (_lease.IsHeld)
                return SdkRestartOutcome.SkippedHostUploadLease;
            if (restartStillNeeded is not null && !restartStillNeeded())
                return SdkRestartOutcome.SkippedNotNeeded;

            restart(ct).GetAwaiter().GetResult();
            NoteRestart();
            return SdkRestartOutcome.Restarted;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private sealed class CompositeScope(IDisposable upload, IUploadLeaseHandle lease) : IUploadLeaseHandle
    {
        public void NoteProgress(string? phase = null) => lease.NoteProgress(phase);

        public void StartHeartbeat(TimeSpan interval) => lease.StartHeartbeat(interval);

        public void Dispose()
        {
            upload.Dispose();
            lease.Dispose();
        }
    }

    private sealed class UploadScope(SdkServiceRecovery owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (owner._gate) owner._activeUploads--;
        }
    }
}
