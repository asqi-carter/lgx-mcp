using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A connect waiting for the host upload lease whose owner restarts the SDK service gets one
/// ceiling extension; a crash the connect survived is kept in <c>retries[]</c>/<c>sdkRestarts</c> and named in
/// ConnectBusy; a failed connect over a dead session reports <c>connected:false</c>; <c>oldPid</c> is the pid running
/// when the upload began. Every test uses its own <see cref="HostUploadLease"/> files, a fake clock and fakes only.
/// </summary>
[Collection("AuditLoggerDir")]
public class LeaseWaiterOwnerRestartTests : IDisposable
{
    private const int OwnerPid  = 4242;
    private const int WaiterPid = 1111;
    private const string UnableToComplete = "Unable to complete connection to LdSdkService (127.0.0.1:50051)";

    private readonly IsolatedUploadLease _scope = new();
    private readonly string _auditDir =
        Path.Combine(Path.GetTempPath(), "LogixMcp.Tests", "g50-audit-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Redirects the audit log to a per-test directory.</summary>
    public LeaseWaiterOwnerRestartTests()
    {
        Directory.CreateDirectory(_auditDir);
        AuditLogger.OverrideLogDir(_auditDir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        _scope.Dispose();
        try { Directory.Delete(_auditDir, recursive: true); } catch (IOException) { }
    }

    private HostUploadLease NewLease(int pid, Func<TimeSpan, Task>? onDelay = null, TimeSpan? bound = null) => new(
        _scope.LeasePath, pid, isProcessAlive: _ => true,
        waitBound: bound ?? TimeSpan.FromSeconds(30), retryAfter: TimeSpan.FromSeconds(30),
        pollInterval: TimeSpan.FromSeconds(1), clock: () => _now,
        delay: async (t, _) => { _now += t; if (onDelay is not null) await onDelay(t); });

    // ── (b) lease wait after the owner's SDK restart ─────────────────────────

    [Fact]
    public async Task WaiterWhoseOwnerRestartsThenKeepsProgressing_PastTheBaseCeiling_ProceedsWhenTheOwnerFinishes()
    {
        var start = _now;
        var finishAt = start + TimeSpan.FromSeconds(170);        // well past the 120 s base ceiling
        var owner = NewLease(OwnerPid).TryAcquire()!;
        owner.NoteProgress("upload");
        var waiter = NewLease(WaiterPid, onDelay: _ =>
        {
            var elapsed = (_now - start).TotalSeconds;
            if (_now >= finishAt) owner.Dispose();
            else if (elapsed == 5) owner.NoteProgress(ConnectPhases.SdkRestart);   // the owner's SDK crashed and restarted
            else owner.NoteProgress(elapsed < 5 ? "upload" : "save-l5x");          // keeps moving; a beat on every poll
            return Task.CompletedTask;
        });
        string? outcome = null;

        using var handle = await waiter.AcquireAsync(onWaitEnd: o => outcome = o);

        Assert.Equal(LeaseWaitOutcomes.OwnerFinished, outcome);
        Assert.Equal(WaiterPid, waiter.CurrentOwner()!.Pid);
        Assert.InRange((_now - start).TotalSeconds, 170, 171);
    }

    [Fact]
    public async Task StalledOwnerAfterARestart_StillGivesConnectBusy_NamingThePhaseAndTimeInIt()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        owner.NoteProgress(ConnectPhases.SdkRestart);
        var waiter = NewLease(WaiterPid, bound: TimeSpan.FromSeconds(10), onDelay: _ =>
        {
            // 20 s in the owner moves on to the upload and beats once more, then goes silent.
            if ((_now - start).TotalSeconds == 20) owner.NoteProgress("upload");
            else if ((_now - start).TotalSeconds < 20) owner.NoteProgress();
            return Task.CompletedTask;
        });

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync());

        Assert.Equal(TagServiceErrorKind.ConnectBusy, ex.Error.Kind);
        Assert.InRange((_now - start).TotalSeconds, 35, 37);       // 15 s after the owner's last beat
        Assert.True(ex.Extended);
        Assert.Equal("upload", ex.Owner!.Phase);
        Assert.InRange(ex.OwnerInPhaseFor!.Value.TotalSeconds, 15, 17);
        Assert.Contains("in phase \"upload\" for 1", ex.Error.Message);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task StalledOwnerThatNeverLeftTheRestartPhase_NamesThatPhase()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        owner.NoteProgress(ConnectPhases.SdkRestart);              // one record, then silence
        var waiter = NewLease(WaiterPid, bound: TimeSpan.FromSeconds(10));

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync());

        Assert.InRange((_now - start).TotalSeconds, 15, 17);
        Assert.Contains("in phase \"sdk-restart\" for 1", ex.Error.Message);
        Assert.Equal(OwnerPid, ex.Owner!.Pid);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task OnlyOneCeilingExtension_IsGranted_EvenWhenTheOwnerRestartsAgain()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        var restartAt = new HashSet<double> { 5, 130, 200 };      // three crashes; only the first extends the ceiling
        var waiter = NewLease(WaiterPid, onDelay: _ =>
        {
            var elapsed = (_now - start).TotalSeconds;
            owner.NoteProgress(restartAt.Contains(elapsed) ? ConnectPhases.SdkRestart : "upload");
            return Task.CompletedTask;
        });

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync());

        Assert.Equal(240, (_now - start).TotalSeconds);            // 120 s base ceiling + one 120 s extension
        Assert.True(ex.Extended);
        Assert.Contains("waited 240 s", ex.Error.Message);
        Assert.Contains("extended once", ex.Error.Message);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task OwnerThatDidNotRestart_KeepsTheBase120sCeiling_AndIsNotMarkedExtended()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        var waiter = NewLease(WaiterPid, onDelay: _ => { owner.NoteProgress("upload"); return Task.CompletedTask; });

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync());

        Assert.Equal(120, (_now - start).TotalSeconds);
        Assert.False(ex.Extended);
        Assert.DoesNotContain("extended", ex.Error.Message);
        GC.KeepAlive(owner);
    }

    // ── (d) oldPid chains across restarts ────────────────────────────────────

    private SdkConnectRetryPolicy PolicyOver(Func<int[]> pids, List<string?>? phaseSeenDuringWait = null) => new()
    {
        Recovery = new SdkServiceRecovery($@"Global\LogixMcp.Tests.{Guid.NewGuid():N}", lease: _scope.Lease),
        ServiceState = () => DoctorServiceState.Running,
        ServerPids = () => pids(),
        ServicePing = _ => Task.FromResult(true),
        Clock = () => _now,
        Delay = (t, _) =>
        {
            if (phaseSeenDuringWait is not null) phaseSeenDuringWait.Add(_scope.Lease.CurrentOwner()?.Phase);
            return Task.CompletedTask;
        },
    };

    private static TagServiceException Failure(string sdkMessage) =>
        new(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
            $"The SDK call for controller at 'EmulateEthernet\\192.0.2.10' failed: \"{sdkMessage}\" more text"),
            new InvalidOperationException(sdkMessage));

    [Fact]
    public async Task OldPidOfTwoConsecutiveRestarts_Chains_AtoB_ThenBtoC()
    {
        int[] current = [111];
        var next = new Queue<int[]>([[222], [333]]);
        var calls = 0;
        var policy = PolicyOver(() => current);
        var retries = new List<ConnectRetryEntry>();

        var result = await policy.RunAsync(sink =>
        {
            sink!.Report(ConnectPhases.For(ConnectPhases.Upload));
            if (calls++ < 2)
            {
                current = next.Dequeue();                     // the SDK server crashes and a new one comes up
                return Task.FromException<string>(Failure(UnableToComplete));
            }
            return Task.FromResult("connected");
        }, retries, CancellationToken.None, null, () => ConnectPhases.Upload);

        Assert.Equal("connected", result);
        Assert.Equal(2, retries.Count);
        Assert.Equal((111, 222), (retries[0].SdkRestart!.OldPid, retries[0].SdkRestart!.NewPid));
        Assert.Equal((222, 333), (retries[1].SdkRestart!.OldPid, retries[1].SdkRestart!.NewPid));
    }

    [Fact]
    public async Task OldPid_IsReReadWhenTheUploadStarts_NotTakenBeforeALongLeaseWait()
    {
        int[] current = [111];
        var policy = PolicyOver(() => current);
        var retries = new List<ConnectRetryEntry>();

        // Attempt 1 crashes 111 -> 222. Attempt 2 queues for the lease; while it waits somebody else's crash moves the
        // server 222 -> 333. When its upload starts it runs on 333, and its own crash (333 -> 444) must say so.
        var calls = 0;
        var result = await policy.RunAsync(sink =>
        {
            if (calls++ == 0)
            {
                sink!.Report(ConnectPhases.For(ConnectPhases.Upload));
                current = [222];
                return Task.FromException<string>(Failure(UnableToComplete));
            }
            if (calls == 2)
            {
                sink!.Report(ConnectPhases.LeaseWaitFor(OwnerPid));
                current = [333];                                // restarted by another connect during our wait
                sink.Report(ConnectPhases.LeaseWaitEnded(LeaseWaitOutcomes.OwnerFinished));
                sink.Report(ConnectPhases.For(ConnectPhases.Upload));
                current = [444];
                return Task.FromException<string>(Failure(UnableToComplete));
            }
            return Task.FromResult("connected");
        }, retries, CancellationToken.None, null, () => ConnectPhases.Upload);

        Assert.Equal("connected", result);
        Assert.Equal((111, 222), (retries[0].SdkRestart!.OldPid, retries[0].SdkRestart!.NewPid));
        Assert.Equal((333, 444), (retries[1].SdkRestart!.OldPid, retries[1].SdkRestart!.NewPid));
    }

    [Fact]
    public async Task RestartWait_HoldsTheLease_AsTheSdkRestartPhase_AndReleasesItAfterwards()
    {
        int[] current = [111];
        var seen = new List<string?>();
        var policy = PolicyOver(() => current, seen);
        var states = new Queue<string>(["Stopped", DoctorServiceState.Running]);
        var recordingDelay = policy.Delay;
        policy = new SdkConnectRetryPolicy
        {
            Recovery = policy.Recovery,
            ServiceState = () => states.Count > 0 ? states.Dequeue() : DoctorServiceState.Running,
            ServerPids = () => current,
            ServicePing = _ => Task.FromResult(true),
            Clock = policy.Clock,
            Delay = (t, ct) => { current = [222]; return recordingDelay(t, ct); },
        };
        var calls = 0;

        await policy.RunAsync(sink =>
            calls++ == 0 ? Task.FromException<string>(Failure(UnableToComplete)) : Task.FromResult("ok"),
            [], CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.NotEmpty(seen);
        Assert.All(seen, phase => Assert.Equal(ConnectPhases.SdkRestart, phase));
        Assert.False(_scope.Lease.IsHeld);
    }

    // ── (a) crash then ConnectBusy / another error, (c) dead session ─────────

#if LOGIX_SDK
    /// <summary>Calls 1..n each throw what the script says (null = succeed with a virtual service).</summary>
    private sealed class ScriptedFactory(Func<int, Exception?> script, Action<int>? onCall = null) : ISdkTagServiceFactory
    {
        public int Calls { get; private set; }

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath, IProgress<ConnectProgress>? progress, CancellationToken ct)
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.Upload));   // the upload started: this attempt holds the lease
            return CreateFromControllerAsync(commPath, ct);
        }

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
        {
            var call = ++Calls;
            onCall?.Invoke(call);
            return script(call) is { } failure
                ? Task.FromException<SdkSessionResult>(failure)
                : Task.FromResult(new SdkSessionResult(new VirtualTagService(), false, [], "upload-to-new-project"));
        }
    }

    private static Task<object> Connect(ScriptedFactory factory, SdkConnectRetryPolicy policy, TagSessionService? session = null) =>
        ConnectHandler.ExecuteAsync(
            gateway: "", path: "", useVirtual: false, new ProjectService(), session ?? new TagSessionService(),
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: "EmulateEthernet\\192.0.2.10", sdkConnectRetry: policy);

    private static ConnectBusyException Busy() => new(
        new UploadLeaseOwner(OwnerPid, "t", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "upload"),
        TimeSpan.FromSeconds(125), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(40));

    [Fact]
    public async Task CrashFollowedByConnectBusy_IsInRetries_AndNamedInTheConnectBusyMessage()
    {
        int[] current = [111];
        var factory = new ScriptedFactory(
            call => call == 1 ? Failure(UnableToComplete) : Busy(),
            call => { if (call == 1) current = [222]; });          // the first attempt's SDK server crashes

        var error = Assert.IsType<ConnectBusyError>(await Connect(factory, PolicyOver(() => current)));

        Assert.Equal("ConnectBusy", error.Kind);
        var retry = Assert.Single(error.Retries!);
        Assert.Equal(new SdkRestartRecord(ConnectPhases.Upload, 111, 222, _now), retry.SdkRestart);
        Assert.Single(error.SdkRestarts!);
        Assert.Contains("survived 1 SDK service crash", error.Error);
        Assert.Contains("111 to 222", error.Error);
        Assert.Contains("upload phase", error.Error);
        Assert.Contains("in phase \"upload\" for 40 s", error.Error);    // the owner's phase, from the lease
        var json = JsonSerializer.Serialize(error);
        Assert.Contains("\"retries\":[", json);
        Assert.Contains("\"sdkRestarts\":[", json);
    }

    [Fact]
    public async Task ConnectBusyWithoutACrash_HasNoRetriesOrSdkRestarts_AndNamesNoCrash()
    {
        var factory = new ScriptedFactory(_ => Busy());

        var error = Assert.IsType<ConnectBusyError>(await Connect(factory, PolicyOver(() => [111])));

        Assert.Null(error.Retries);
        Assert.Null(error.SdkRestarts);
        Assert.DoesNotContain("crash", error.Error);
        var json = JsonSerializer.Serialize(error);
        Assert.DoesNotContain("retries", json);
        Assert.DoesNotContain("sdkRestarts", json);
    }

    [Fact]
    public async Task CrashFollowedByAnotherTypedError_StillCarriesTheRetry()
    {
        int[] current = [111];
        var factory = new ScriptedFactory(
            call => call == 1
                ? Failure(UnableToComplete)
                : new TagServiceException(new TagServiceError(TagServiceErrorKind.PendingEditsPresent, "pending edits")),
            call => { if (call == 1) current = [222]; });

        var error = Assert.IsType<ConnectPendingEditsError>(await Connect(factory, PolicyOver(() => current)));

        Assert.Equal(222, Assert.Single(error.Retries!).SdkRestart!.NewPid);
    }

    private sealed class MovableServer
    {
        public int[] Pids { get; set; } = [111];
    }

    private static TagSessionService LiveSessionOn(MovableServer server)
    {
        var session = new TagSessionService
        {
            LostEvidence = new SessionLostEvidence(() => server.Pids, () => false, () => null, ownPid: 1),
        };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true);
        return session;
    }

    [Fact]
    public async Task FailedConnect_WhoseOldSessionIsDead_ReportsConnectedFalse()
    {
        var server = new MovableServer();
        var session = LiveSessionOn(server);
        Assert.True(session.IsConnected);
        // The SDK server this session was attached to is replaced (its crash), then the reconnect gives ConnectBusy.
        var factory = new ScriptedFactory(_ => Busy(), _ => server.Pids = [222]);

        Assert.IsType<ConnectBusyError>(await Connect(factory, PolicyOver(() => server.Pids), session));

        Assert.False(session.IsConnected);
        Assert.Equal(SessionLostCauses.SdkServiceRestarted, session.SessionLost!.Cause);
        using var status = JsonDocument.Parse(SessionLostSupport.AnnotateStatus(session, "{\"status\":\"failed\"}"));
        Assert.False(status.RootElement.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task FailedConnect_WhoseOldSessionIsStillValid_StaysConnected()
    {
        var server = new MovableServer();
        var session = LiveSessionOn(server);
        var factory = new ScriptedFactory(_ => Busy());

        Assert.IsType<ConnectBusyError>(await Connect(factory, PolicyOver(() => server.Pids), session));

        Assert.True(session.IsConnected);                          // same LdSdkServer pid: nothing says it died
        Assert.Null(session.SessionLost);
    }
#endif
}
