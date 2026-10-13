using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Concurrent connects must not break each other during SDK recovery. Fakes only: no service,
/// no waiting; each test uses its own mutex name so tests never contend with each other or a real server.
/// </summary>
public class SdkServiceRecoveryTests : IDisposable
{
    private const string NoValidLicense = "No valid license";

    // Each test gets its own lease file (xunit builds one instance per test), so a lease held by a real server or by
    // another test on this machine can never turn a restart into SkippedHostUploadLease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();

    private SdkServiceRecovery NewRecovery(TimeSpan? lockTimeout = null, Func<DateTimeOffset>? clock = null) =>
        new($@"Global\LogixMcp.Tests.{Guid.NewGuid():N}", lockTimeout ?? TimeSpan.FromMilliseconds(200), clock: clock,
            lease: _leaseScope.Lease);

    private static SdkConnectRetryPolicy NewPolicy(SdkServiceRecovery recovery, List<TimeSpan> delays) => new()
    {
        Recovery = recovery,
        ServiceState = () => DoctorServiceState.Running,
        Delay = (t, _) => { delays.Add(t); return Task.CompletedTask; },
    };

    private static TagServiceException LicenseFailure() =>
        new(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
            $"The SDK call failed: \"{NoValidLicense}\""), new InvalidOperationException(NoValidLicense));

    [Fact]
    public async Task FakeSecondConnectMidUpload_CausesNoServiceRestart()
    {
        var recovery = NewRecovery();
        var restarts = 0;

        using (recovery.BeginUpload())   // the second connect is mid-upload
        {
            var outcome = await recovery.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; });

            Assert.Equal(SdkRestartOutcome.SkippedUploadInProgress, outcome);
            Assert.Equal(0, restarts);
        }

        // Upload over: recovery may restart again.
        Assert.Equal(0, recovery.ActiveUploads);
        Assert.Equal(SdkRestartOutcome.Restarted,
            await recovery.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; }));
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task UploadStartingWhileWaitingForTheLock_StillVetoesTheRestart()
    {
        var name = $@"Global\LogixMcp.Tests.{Guid.NewGuid():N}";
        var recovery = new SdkServiceRecovery(name, TimeSpan.FromSeconds(5), lease: _leaseScope.Lease);
        var restarts = 0;
        using var holder = new Mutex(false, name);
        holder.WaitOne();                                     // another server process holds the lock

        var pending = recovery.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; });
        using var upload = recovery.BeginUpload();            // a connect starts uploading meanwhile
        holder.ReleaseMutex();

        Assert.Equal(SdkRestartOutcome.SkippedUploadInProgress, await pending);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task RestartDecision_IsGuardedByHostWideNamedMutex_SecondProcessSkips()
    {
        var name = $@"Global\LogixMcp.Tests.{Guid.NewGuid():N}";
        var recovery = new SdkServiceRecovery(name, TimeSpan.FromMilliseconds(100), lease: _leaseScope.Lease);
        var restarts = 0;

        // Stand-in for another server process: holds the same named mutex on its own thread.
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var other = Task.Factory.StartNew(() =>
        {
            using var m = new Mutex(false, name);
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        }, TaskCreationOptions.LongRunning);
        held.Wait();

        var outcome = await recovery.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; });
        release.Set();
        await other;

        Assert.Equal(SdkRestartOutcome.SkippedLockBusy, outcome);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task RestartDecision_WaitsForTheLock_ThenSkipsWhenTheOtherProcessAlreadyRecovered()
    {
        var name = $@"Global\LogixMcp.Tests.{Guid.NewGuid():N}";
        var recovery = new SdkServiceRecovery(name, TimeSpan.FromSeconds(5), lease: _leaseScope.Lease);
        var restarts = 0;
        var healthy = false;
        using var holder = new Mutex(false, name);
        holder.WaitOne();

        var pending = recovery.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; }, () => !healthy);
        healthy = true;                                       // the other process restarted it while we waited
        holder.ReleaseMutex();

        Assert.Equal(SdkRestartOutcome.SkippedNotNeeded, await pending);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task NoValidLicenseAfterRestart_IsRetriedWithinTheTwoRetryBudget()
    {
        var recovery = NewRecovery();
        recovery.NoteRestart();
        var delays = new List<TimeSpan>();
        var calls = 0;
        var retries = new List<ConnectRetryEntry>();

        var result = await NewPolicy(recovery, delays).RunAsync(
            () => ++calls < 3 ? Task.FromException<string>(LicenseFailure()) : Task.FromResult("connected"),
            retries, CancellationToken.None);

        Assert.Equal("connected", result);
        Assert.Equal(3, calls);                                          // first try + exactly 2 retries
        Assert.Equal(TimeSpan.FromSeconds(30), TimeSpan.FromTicks(delays.Sum(d => d.Ticks)));   // 10 s + 20 s ceilings
        Assert.Equal(2, retries.Count);
        Assert.All(retries, r => Assert.Contains("restart", r.Error, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoValidLicenseAfterRestart_StillFailingAfterTwoRetries_StopsAndNamesTheRestart()
    {
        var recovery = NewRecovery();
        recovery.NoteRestart();
        var calls = 0;

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => NewPolicy(recovery, []).RunAsync(
            () => { calls++; return Task.FromException<string>(LicenseFailure()); }, [], CancellationToken.None));

        Assert.Equal(3, calls);
        Assert.Contains("restarted by service recovery", ex.Error.Message);
        Assert.Contains(NoValidLicense, ex.Error.Message);   // the SDK detail is kept
    }

    [Fact]
    public async Task NoValidLicenseWithoutARecentRestart_IsNotRetried_AndNotRelabelled()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => NewPolicy(NewRecovery(), []).RunAsync(
            () => { calls++; return Task.FromException<string>(LicenseFailure()); }, [], CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.DoesNotContain("restart", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoValidLicense_RestartOlderThanTheWindow_IsNotRetried()
    {
        var now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        var recovery = NewRecovery(clock: () => now);
        recovery.NoteRestart();
        now = now.AddMinutes(10);
        var calls = 0;

        await Assert.ThrowsAsync<TagServiceException>(() => NewPolicy(recovery, []).RunAsync(
            () => { calls++; return Task.FromException<string>(LicenseFailure()); }, [], CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ServiceObservedDownThenRunning_IsRecordedAsARestart_AndLicenseErrorIsRetried()
    {
        var recovery = NewRecovery();
        var states = new Queue<string>(["Stopped", DoctorServiceState.Running]);
        var policy = new SdkConnectRetryPolicy
        {
            Recovery = recovery,
            ServiceState = () => states.Count > 1 ? states.Dequeue() : states.Peek(),
            Delay = (_, _) => Task.CompletedTask,
        };
        var calls = 0;

        // First failure is a dead service (retryable already); the retry then gets "No valid license".
        var result = await policy.RunAsync(() => ++calls switch
        {
            1 => Task.FromException<string>(new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed, "Could not reach LdSdkService"))),
            2 => Task.FromException<string>(LicenseFailure()),
            _ => Task.FromResult("connected"),
        }, [], CancellationToken.None);

        Assert.Equal("connected", result);
        Assert.Equal(3, calls);
        Assert.True(recovery.RestartedRecently());
    }

    [Fact]
    public async Task ServiceObservedDownOnly_IsNotRecordedAsARestart()
    {
        var recovery = NewRecovery();
        var policy = new SdkConnectRetryPolicy
        {
            Recovery = recovery,
            ServiceState = () => "Stopped",
            Delay = (_, _) => Task.CompletedTask,
        };

        await Assert.ThrowsAsync<TagServiceException>(() => policy.RunAsync(
            () => Task.FromException<string>(new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed, "Could not reach LdSdkService"))),
            [], CancellationToken.None));

        Assert.False(recovery.RestartedRecently());
    }
}

/// <summary>
/// G33: uploads are serialised host-wide by a lease file with the owner pid; a restart is vetoed while ANY process
/// holds it; a closed project becomes a typed SessionLost. Two server processes are stood in for by two
/// <see cref="HostUploadLease"/> handles (different pids) over one lease file; no real wait happens.
/// </summary>
public class HostUploadLeaseTests : IDisposable
{
    private const int PidA = 1001;   // the "first" server process
    private const int PidB = 2002;   // the "second" server process

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LogixMcp.Tests", Guid.NewGuid().ToString("N"));
    private readonly HashSet<int> _alive = [PidA, PidB];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HostUploadLease Process(int pid, TimeSpan? bound = null, TimeSpan? retryAfter = null) => new(
        Path.Combine(_dir, "upload.lease"), pid, _alive.Contains, bound ?? TimeSpan.FromSeconds(2),
        retryAfter ?? TimeSpan.FromSeconds(30), pollInterval: TimeSpan.FromMilliseconds(500),
        clock: () => _now, delay: (t, _) => { _now += t; return Task.CompletedTask; });

    private SdkServiceRecovery RecoveryOf(HostUploadLease lease) =>
        new($@"Global\LogixMcp.Tests.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200), clock: () => _now, lease: lease);

    [Fact]
    public async Task SecondProcessUploadLease_BlocksFirstProcessServiceRestart()
    {
        var first  = RecoveryOf(Process(PidA));
        var second = RecoveryOf(Process(PidB));
        var restarts = 0;

        // The first process has no upload of its own (ActiveUploads is 0); the second process is mid-upload.
        using (await second.AcquireUploadAsync())
        {
            Assert.Equal(0, first.ActiveUploads);
            var outcome = await first.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; });

            Assert.Equal(SdkRestartOutcome.SkippedHostUploadLease, outcome);
            Assert.Equal(0, restarts);
        }

        // Second process done: the lease is free and the restart goes ahead.
        Assert.Equal(SdkRestartOutcome.Restarted,
            await first.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; }));
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task LeaseTakenWhileWaitingForTheRestartLock_StillVetoesTheRestart()
    {
        var name = $@"Global\LogixMcp.Tests.{Guid.NewGuid():N}";
        var first = new SdkServiceRecovery(name, TimeSpan.FromSeconds(5), lease: Process(PidA));
        var restarts = 0;
        using var holder = new Mutex(false, name);
        holder.WaitOne();                                           // another process holds the restart lock

        var pending = first.TryRestartAsync(_ => { restarts++; return Task.CompletedTask; });
        using var lease = Process(PidB).TryAcquire();                // the other process starts an upload meanwhile
        holder.ReleaseMutex();

        Assert.Equal(SdkRestartOutcome.SkippedHostUploadLease, await pending);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task ConnectBusyWithRetryAfter_IsReturnedWhenLeaseHeldPastTheBound()
    {
        var holder = RecoveryOf(Process(PidB));
        var waiter = RecoveryOf(Process(PidA, bound: TimeSpan.FromSeconds(2), retryAfter: TimeSpan.FromSeconds(45)));

        using (await holder.AcquireUploadAsync())
        {
            var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireUploadAsync());

            Assert.Equal(TagServiceErrorKind.ConnectBusy, ex.Error.Kind);
            Assert.Equal(45, ex.Error.RetryAfterSeconds);
            Assert.Equal(TimeSpan.FromSeconds(45), ex.RetryAfter);
            Assert.Equal(PidB, ex.Owner!.Pid);
            Assert.Equal(0, waiter.ActiveUploads);                 // nothing was started

            var payload = ConnectBusyError.From(ex);
            Assert.Equal("ConnectBusy", payload.Kind);
            Assert.Equal(45, payload.RetryAfterSeconds);
            Assert.Equal(PidB, payload.OwnerPid);
        }

        // Once the holder finishes, the same caller gets the lease.
        using (await waiter.AcquireUploadAsync())
            Assert.Equal(1, waiter.ActiveUploads);
    }

    [Fact]
    public async Task WaiterAcquiresWhenTheHolderReleasesInsideTheBound()
    {
        var holderLease = Process(PidB);
        var held = holderLease.TryAcquire();
        var waiter = Process(PidA, bound: TimeSpan.FromSeconds(5));
        var polls = 0;
        var lease = new HostUploadLease(Path.Combine(_dir, "upload.lease"), PidA, _alive.Contains,
            TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromMilliseconds(500), clock: () => _now,
            delay: (t, _) => { _now += t; if (++polls == 2) held!.Dispose(); return Task.CompletedTask; });

        using var handle = await lease.AcquireAsync();

        Assert.Equal(2, polls);
        Assert.Equal(PidA, lease.CurrentOwner()!.Pid);
        Assert.NotNull(waiter);
    }

    [Fact]
    public async Task LeaseOfADeadProcess_IsStale_AndTakenOver()
    {
        var crashed = Process(PidB).TryAcquire();                   // never disposed: the process died
        Assert.NotNull(crashed);
        _alive.Remove(PidB);

        var first = Process(PidA);
        Assert.False(first.IsHeld);
        using (await first.AcquireAsync())
            Assert.Equal(PidA, first.CurrentOwner()!.Pid);
    }

    [Fact]
    public void StaleOwnerDisposing_DoesNotReleaseANewOwnersLease()
    {
        var crashed = Process(PidB).TryAcquire();
        _alive.Remove(PidB);
        var a = Process(PidA);
        using var mine = a.TryAcquire();

        crashed!.Dispose();                                          // late dispose by the dead owner's handle

        Assert.Equal(PidA, a.CurrentOwner()!.Pid);
    }

    [Fact]
    public async Task ConnectBusy_IsNotRetriedByTheConnectRetryPolicy()
    {
        var calls = 0;
        var policy = new SdkConnectRetryPolicy
        {
            Recovery = RecoveryOf(Process(PidA)),
            ServiceState = () => DoctorServiceState.Running, Delay = (_, _) => Task.CompletedTask,
        };

        await Assert.ThrowsAsync<ConnectBusyException>(() => policy.RunAsync<string>(
            () => { calls++; throw new ConnectBusyException(null, TimeSpan.Zero, TimeSpan.FromSeconds(30)); },
            [], CancellationToken.None));

        Assert.Equal(1, calls);
    }

    private static (TagSessionService Session, LogixTagTools Tools) ConnectedSession()
    {
        // No SDK service, no lease holder: nothing is observed, so a closed project has an unknown cause.
        var session = new TagSessionService
        {
            LostEvidence = new SessionLostEvidence(() => [], () => false, () => null, ownPid: 1),
        };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
        var tools = new LogixTagTools(new ProjectService(), session, new ControllerModeService(), connectState: new ConnectState());
        return (session, tools);
    }

    private static JsonObject Payload(CallToolResult r) =>
        (JsonObject)JsonNode.Parse(((TextContentBlock)r.Content[0]).Text)!;

    [Fact]
    public async Task ClosedProject_YieldsTypedSessionLost_AndStatusReportsConnectedFalse()
    {
        var (session, tools) = ConnectedSession();
        Assert.True((bool)JsonNode.Parse(tools.GetConnectStatusResponse())!["connected"]!);

        // A call finds its project closed by another process's reconnect.
        var result = await ToolErrorFilter.InvokeAsync("logix_read",
            () => throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "Project is not open.")),
            tagSession: session);

        var p = Payload(result);
        Assert.True(result.IsError);
        Assert.Equal(ErrorKinds.SessionLost, (string)p["kind"]!);
        Assert.Equal(SessionLostCauses.ProjectClosedUnknown, (string)p["cause"]!);
        Assert.Equal("Project is not open.", (string)p["observed"]!);
        Assert.False((bool)p["connected"]!);
        Assert.False(session.IsConnected);
        Assert.NotNull(session.SessionLost);

        var status = JsonNode.Parse(tools.GetConnectStatusResponse())!;
        Assert.False((bool)status["connected"]!);
        Assert.Equal(SessionLostCauses.ProjectClosedUnknown, (string)status["sessionLost"]!["cause"]!);
    }

    [Fact]
    public async Task ClosedProjectReportedInAToolPayload_IsAlsoSessionLost_AndLaterCallsStaySessionLost()
    {
        var (session, _) = ConnectedSession();

        var first = await ToolErrorFilter.InvokeAsync("logix_pending_edits",
            () => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"error\":\"Read failed: Project is not open.\",\"kind\":\"ConnectionFailed\"}" }],
            }), tagSession: session);
        Assert.Equal(ErrorKinds.SessionLost, (string)Payload(first)["kind"]!);

        // A later call sees no connected session (NoActiveSession); it is told why instead.
        var later = await ToolErrorFilter.InvokeAsync("logix_read",
            () => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"error\":\"No active session.\",\"kind\":\"NoActiveSession\"}" }],
            }), tagSession: session);
        Assert.Equal(ErrorKinds.SessionLost, (string)Payload(later)["kind"]!);
    }

    [Fact]
    public async Task ProjectNotOpenOnAConnectOrWithoutASession_IsNotSessionLost()
    {
        var (session, _) = ConnectedSession();
        var noSession = new TagSessionService();
        var closed = new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "Project is not open."));

        var connect = await ToolErrorFilter.InvokeAsync("logix_connect", () => throw closed, tagSession: session);
        var none = await ToolErrorFilter.InvokeAsync("logix_read", () => throw closed, tagSession: noSession);

        Assert.NotEqual(ErrorKinds.SessionLost, (string)Payload(connect)["kind"]!);
        Assert.NotEqual(ErrorKinds.SessionLost, (string)Payload(none)["kind"]!);
        Assert.True(session.IsConnected);
        Assert.Null(noSession.SessionLost);
    }

    [Fact]
    public void AttachingANewSession_ClearsSessionLost()
    {
        var (session, tools) = ConnectedSession();
        session.MarkSessionLost("Project is not open.");
        Assert.False(session.IsConnected);

        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true);

        Assert.True(session.IsConnected);
        Assert.Null(session.SessionLost);
        Assert.True((bool)JsonNode.Parse(tools.GetConnectStatusResponse())!["connected"]!);
    }

    [Fact]
    public async Task SdkConnectRetry_DoesNotLoopOnProjectNotOpen_PastTheExistingBudget()
    {
        var delays = new List<TimeSpan>();
        var policy = new SdkConnectRetryPolicy
        {
            ServiceState = () => DoctorServiceState.Running,
            Delay = (t, _) => { delays.Add(t); return Task.CompletedTask; },
        };
        var calls = 0;
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(() => policy.RunAsync<string>(
            () =>
            {
                calls++;
                throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "Project is not open."));
            },
            retries, CancellationToken.None));

        // One try plus exactly the budgeted retries (10 s and 20 s back-offs), then it gives up.
        Assert.Equal(1 + policy.Backoffs.Count, calls);
        Assert.Equal(policy.Backoffs.Count, retries.Count);
        Assert.Equal(policy.Backoffs.Sum(b => b.Ticks), delays.Sum(d => d.Ticks));   // ceilings, polled in steps
    }
}

/// <summary>
/// No test may fall back to the host-wide <see cref="HostUploadLease.Default"/> file. Every per-test lease
/// has a unique path that is removed afterwards, and a source scan keeps new tests honest.
/// </summary>
public class UploadLeaseIsolationTests
{
    [Fact]
    public async Task IsolatedUploadLease_UsesAUniquePath_AndRemovesItsDirectoryOnDispose()
    {
        string path;
        using (var a = new IsolatedUploadLease())
        using (var b = new IsolatedUploadLease())
        {
            Assert.NotEqual(a.LeasePath, b.LeasePath);
            path = a.LeasePath;

            using (await a.Lease.AcquireAsync())
            {
                Assert.True(File.Exists(path));          // the lease really is backed by the per-test file
                Assert.Null(b.Lease.CurrentOwner());     // and is invisible to a second test's lease
            }
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void EveryTestThatBuildsALeaseReader_PassesAnExplicitPerTestLease()
    {
        var repo = FixtureMnemonicGuardTests.RepoRoot();
        Assert.NotNull(repo);
        var testsDir = Path.Combine(repo!, "tests", "LogixMcp.Tests");
        var keys = new[] { "new SdkConnect" + "Factory(", "new SdkService" + "Recovery(", "new HostUpload" + "Lease(" };
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(testsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(file);
            foreach (var key in keys)
                for (var at = text.IndexOf(key, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(key, at + key.Length, StringComparison.Ordinal))
                {
                    var lineStart = text.LastIndexOf('\n', at) + 1;
                    if (text[lineStart..at].TrimStart().StartsWith("//")) continue;   // comment / doc text
                    var depth = 1;
                    var end = at + key.Length;
                    for (; end < text.Length && depth > 0; end++)
                        depth += text[end] == '(' ? 1 : text[end] == ')' ? -1 : 0;
                    var args = text[(at + key.Length)..end];
                    // HostUploadLease takes an explicit path; the other two must be handed a lease.
                    var ok = key.Contains("HostUpload") ? args.Trim().Length > 0 && !args.TrimStart().StartsWith(")")
                                                        : args.Contains("lease", StringComparison.OrdinalIgnoreCase);
                    if (!ok) offenders.Add($"{Path.GetFileName(file)}: {key}");
                }
        }

        Assert.Empty(offenders);
    }
}
