using System.Text.Json;
using System.Text.Json.Nodes;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// G46: an LdSdkServer crash during <c>logix_connect</c> is recorded (<c>sdkRestart</c>), shown while the retry waits
/// (<c>sdk-service-restarted; waiting for LdSdkService</c>), recovered from as soon as the service is back, and counted
/// by <c>logix_doctor</c>. Fakes only: no service, no processes, no waiting. Every instance is per-test (0175 pattern).
/// </summary>
[Collection("AuditLoggerDir")]
public class SdkConnectCrashRecoveryTests : IDisposable
{
    private const string UnableToComplete = "Unable to complete connection to LdSdkService (127.0.0.1:50051)";

    private readonly IsolatedUploadLease _leaseScope = new();
    private readonly string _auditDir =
        Path.Combine(Path.GetTempPath(), "LogixMcp.Tests", "g46-audit-" + Guid.NewGuid().ToString("N"));

    /// <summary>Redirects the audit log to a per-test directory.</summary>
    public SdkConnectCrashRecoveryTests()
    {
        Directory.CreateDirectory(_auditDir);
        AuditLogger.OverrideLogDir(_auditDir);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        _leaseScope.Dispose();
        try { Directory.Delete(_auditDir, recursive: true); } catch (IOException) { }
    }

    private sealed class Progress : IProgress<ConnectProgress>
    {
        public List<ConnectProgress> Reports { get; } = [];
        public void Report(ConnectProgress value) => Reports.Add(value);
    }

    /// <summary>A policy with scripted service states, pids, ping and clock; delays are recorded, never awaited.</summary>
    private sealed class FakeCrashHost(HostUploadLease lease)
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

        private readonly SdkServiceRecovery _recovery = new($@"Global\LogixMcp.Tests.{Guid.NewGuid():N}", lease: lease);
        public List<TimeSpan> Delays { get; } = [];
        public Queue<string> States { get; } = new();
        public Queue<int[]> Pids { get; } = new();
        public int[] LastPids { get; set; } = [111];
        public int PingCalls { get; private set; }

        public SdkConnectRetryPolicy Policy => new()
        {
            Recovery = _recovery,
            ServiceState = () => States.Count > 0 ? States.Dequeue() : DoctorServiceState.Running,
            ServerPids = () => Pids.Count > 0 ? LastPids = Pids.Dequeue() : LastPids,
            ServicePing = _ => { PingCalls++; return Task.FromResult(true); },
            Clock = () => Now,
            Delay = (t, _) => { Delays.Add(t); return Task.CompletedTask; },
        };
    }

    private static TagServiceException Failure(string sdkMessage) =>
        new(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
            $"The SDK call for controller at 'EmulateEthernet\\192.0.2.10' failed: \"{sdkMessage}\" more text"),
            new InvalidOperationException(sdkMessage));

    private static (Func<Task<string>> Attempt, Func<int> Calls) Script(params Exception?[] outcomes)
    {
        var calls = 0;
        return (() =>
        {
            var o = outcomes[Math.Min(calls++, outcomes.Length - 1)];
            return o is null ? Task.FromResult("connected") : Task.FromException<string>(o);
        }, () => calls);
    }

    [Fact]
    public async Task FakeCrashInUploadPhase_GivesSdkRestartRecord_AndTheProgressText()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        host.Pids.Enqueue([111]);                   // attempt 1 starts: LdSdkServer pid 111
        host.Pids.Enqueue([]);                      // poll 1: the server is gone
        host.Pids.Enqueue([]);                      // poll 2: still down
        host.Pids.Enqueue([222]);                   // poll 3: the restarted server
        host.States.Enqueue("Stopped");
        host.States.Enqueue("StartPending");
        host.States.Enqueue(DoctorServiceState.Running);
        var (attempt, calls) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();
        var progress = new Progress();

        var result = await host.Policy.RunAsync(attempt, retries, CancellationToken.None, progress, () => ConnectPhases.Upload);

        Assert.Equal("connected", result);
        Assert.Equal(2, calls());
        var restart = Assert.Single(retries).SdkRestart;
        Assert.Equal(new SdkRestartRecord(ConnectPhases.Upload, 111, 222, FakeCrashHost.Now), restart);

        var report = Assert.Single(progress.Reports);
        Assert.Equal(ConnectPhases.SdkRestart, report.Phase);
        Assert.Equal("sdk-service-restarted; waiting for LdSdkService", report.Message);
        Assert.Equal(ConnectPhases.For(ConnectPhases.Upload).Step, report.Step);   // progress never goes backwards
    }

    [Fact]
    public async Task FakeCrash_SdkRestartRecord_ReachesPhaseTimingsJson_AndStatusPoll()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        host.Pids.Enqueue([111]);
        host.Pids.Enqueue([333]);
        host.States.Enqueue("Stopped");
        host.States.Enqueue(DoctorServiceState.Running);
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();
        var state = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectPhaseTimings? timings = null;

        // The background connect: reports upload, crashes, waits for the service; a status poll sees the text.
        var response = await state.RunAsync(async progress =>
        {
            var recorder = new ConnectPhaseRecorder(progress);
            recorder.Report(ConnectPhases.For(ConnectPhases.Upload));
            await host.Policy.RunAsync(attempt, retries, CancellationToken.None, recorder, () => recorder.CurrentPhase);
            observed.SetResult();
            await release.Task;
            recorder.NoteSdkRestart(retries.Single().SdkRestart);
            timings = recorder.Snapshot();
            return "{}";
        }, TimeSpan.Zero);

        await observed.Task;
        var connecting = JsonNode.Parse(state.GetStatusJson())!.AsObject();
        release.SetResult();
        await state.Completion;

        Assert.Equal("connecting", (string)connecting["status"]!);
        var phases = connecting["phaseTimings"]!.AsArray().Select(p => (string)p!["phase"]!).ToList();
        Assert.Contains(ConnectPhases.SdkRestart, phases);   // the restart is visible in the phase list of the poll

        // The restart is part of the connect's phaseTimings JSON.
        Assert.NotNull(timings);
        var json = JsonNode.Parse(JsonSerializer.Serialize(timings))!["sdkRestart"]!.AsObject();
        Assert.Equal("upload", (string)json["phase"]!);
        Assert.Equal(111, (int)json["oldPid"]!);
        Assert.Equal(333, (int)json["newPid"]!);
        Assert.NotNull(json["detectedAt"]);
        Assert.Contains("\"sdkRestart\"", JsonSerializer.Serialize(retries));
    }

    [Fact]
    public async Task StatusPoll_WhileRetryWaits_ShowsTheRestartProgressText()
    {
        var state = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.RunAsync(async progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.Upload));
            progress.Report(ConnectPhases.SdkRestartedFor(ConnectPhases.Upload));
            reported.SetResult();
            await release.Task;
            return "{}";
        }, TimeSpan.Zero);
        await reported.Task;

        var waiting = JsonNode.Parse(state.GetStatusJson())!.AsObject();
        Assert.Equal("sdk-service-restarted; waiting for LdSdkService", (string)waiting["progress"]!);

        release.SetResult();
        await state.Completion;
    }

    [Fact]
    public async Task Retry_ResumesWhenFakeServiceReportsRunning_BeforeTheCeiling_WithoutBackoff()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        host.Pids.Enqueue([111]);
        for (var i = 0; i < 4; i++) { host.States.Enqueue("Stopped"); host.Pids.Enqueue([]); }
        host.States.Enqueue(DoctorServiceState.Running);
        host.Pids.Enqueue([444]);
        var (attempt, calls) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await host.Policy.RunAsync(attempt, retries, CancellationToken.None, new Progress(), () => ConnectPhases.SaveL5x);

        Assert.Equal(2, calls());
        var ceiling = host.Policy.ServicePollTimeout;
        Assert.Equal(4, host.Delays.Count);                                      // four 1 s polls, then Running
        Assert.All(host.Delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));  // no 10 s back-off
        Assert.Equal(4_000, retries[0].WaitedMs);
        Assert.True(retries[0].WaitedMs < ceiling.TotalMilliseconds);
        Assert.Equal(1, host.PingCalls);                                         // pinged once the service was Running
        Assert.Equal("save-l5x", retries[0].SdkRestart!.Phase);
    }

    [Fact]
    public async Task Retry_ServiceRunningButPingFails_KeepsPollingUntilItAnswers()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var answers = new Queue<bool>([false, false, true]);
        host.Pids.Enqueue([111]);
        host.Pids.Enqueue([555]);
        host.States.Enqueue("Stopped");
        var policy = host.Policy;
        var pinging = new SdkConnectRetryPolicy
        {
            Recovery = policy.Recovery, ServiceState = policy.ServiceState, ServerPids = policy.ServerPids,
            Clock = policy.Clock, Delay = policy.Delay,
            ServicePing = _ => Task.FromResult(answers.Dequeue()),
        };
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await pinging.RunAsync(attempt, retries, CancellationToken.None);

        Assert.Empty(answers);
        Assert.Equal(3_000, retries[0].WaitedMs);   // Stopped, then two unanswered pings, then the answer
        Assert.Equal(555, retries[0].SdkRestart!.NewPid);
    }

    [Fact]
    public async Task Retry_ServerNeverComesBack_StopsAtTheCeiling_AndStillRecordsTheRestart()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var policy = host.Policy;
        var pidReads = 0;
        var stuck = new SdkConnectRetryPolicy
        {
            Recovery = policy.Recovery, ServiceState = () => "Stopped",
            ServerPids = () => pidReads++ == 0 ? [111] : [],   // the server is up for attempt 1, then gone for good
            Clock = policy.Clock, Delay = policy.Delay,
        };
        var (attempt, _) = Script(Failure(UnableToComplete));
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(() => stuck.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(2, retries.Count);
        Assert.Equal(30_000 + 10_000, retries[0].WaitedMs);        // ceiling, then the back-off as before
        Assert.Null(retries[0].SdkRestart!.NewPid);
    }

    [Fact]
    public async Task Retry_NoRestartSeen_OmitsSdkRestart_AndPollsForReadiness()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();
        var progress = new Progress();

        await host.Policy.RunAsync(attempt, retries, CancellationToken.None, progress, () => ConnectPhases.Upload);

        Assert.Null(retries.Single().SdkRestart);
        var report = Assert.Single(progress.Reports);                  // only the comms-retry note, no sdk-restart
        Assert.Equal(ConnectPhases.CommsRetry, report.Phase);
        Assert.Equal(1_000, report.DurationMs);
        Assert.Contains(UnableToComplete, report.Error);
        Assert.Equal([TimeSpan.FromSeconds(1)], host.Delays);
        Assert.DoesNotContain("sdkRestart", JsonSerializer.Serialize(retries));
    }

    /// <summary>A no-restart policy whose readiness probe answers from a script (default: not ready); delays are recorded.</summary>
    private static SdkConnectRetryPolicy CommsPolicy(
        FakeCrashHost host, Func<int, bool> readyOnPoll, Action? onPoll = null)
    {
        var polls = 0;
        var basePolicy = host.Policy;
        return new SdkConnectRetryPolicy
        {
            Recovery = basePolicy.Recovery, ServiceState = basePolicy.ServiceState, ServerPids = basePolicy.ServerPids,
            Clock = basePolicy.Clock, Delay = basePolicy.Delay,
            CommsReady = _ => { onPoll?.Invoke(); return Task.FromResult(readyOnPoll(++polls)); },
        };
    }

    [Fact]
    public async Task CommsRetry_ResumesAsSoonAsFakeReportsReady_WithoutWaitingTheFull10s()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var policy = CommsPolicy(host, poll => poll >= 4);           // ready on the 4th check: after three 1 s polls
        var (attempt, calls) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        var result = await policy.RunAsync(attempt, retries, CancellationToken.None);

        Assert.Equal("connected", result);
        Assert.Equal(2, calls());
        Assert.Equal(3, host.Delays.Count);
        Assert.All(host.Delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));   // polled, no 10 s sleep
        Assert.Equal(3_000, retries.Single().WaitedMs);
        Assert.True(retries.Single().WaitedMs < 10_000);
    }

    [Fact]
    public async Task CommsRetry_ReadyAtOnce_StartsTheRetryWithoutAnyWait()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await CommsPolicy(host, _ => true).RunAsync(attempt, retries, CancellationToken.None);

        Assert.Empty(host.Delays);
        Assert.Equal(0, retries.Single().WaitedMs);
        Assert.Equal(0, retries.Single().CommsRetry!.DurationMs);
    }

    [Fact]
    public async Task CommsRetry_FakeNeverReady_StillStopsAtTheExistingCeiling()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(UnableToComplete));      // always fails
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(() =>
            CommsPolicy(host, _ => false).RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(3, calls());                                       // the retry limit is unchanged
        Assert.Equal(10_000, retries[0].WaitedMs);                      // the 10 s ceiling, then 20 s
        Assert.Equal(20_000, retries[1].WaitedMs);
        Assert.Equal(10_000, retries[0].CommsRetry!.DurationMs);
        Assert.Equal(30, host.Delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), TimeSpan.FromTicks(host.Delays.Sum(d => d.Ticks)));
    }

    [Fact]
    public async Task StatusAfterFakeCommsFault_ListsCommsRetryBetweenTheTwoUploads_AndAgreesWithTheAudit()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var policy = CommsPolicy(host, poll => poll >= 3);              // ready after two 1 s polls
        var (inner, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();
        var state = new ConnectState();
        ConnectPhaseTimings? audit = null;

        await state.RunAsync(async progress =>
        {
            var recorder = new ConnectPhaseRecorder(progress);
            await policy.RunAsync(() =>
            {
                recorder.Report(ConnectPhases.For(ConnectPhases.Upload));   // each attempt starts with its upload
                return inner();
            }, retries, CancellationToken.None, recorder, () => recorder.CurrentPhase);
            recorder.NoteCommsRetry(retries.LastOrDefault(r => r.CommsRetry is not null)?.CommsRetry);
            audit = recorder.Snapshot();
            return "{}";
        }, TimeSpan.FromSeconds(30));
        await state.Completion;

        var status = JsonNode.Parse(state.GetStatusJson())!.AsObject();
        var live = status["phaseTimings"]!.AsArray();
        Assert.Equal(["upload", "comms-retry", "upload"], live.Select(p => (string)p!["phase"]!).ToArray());

        var element = live[1]!.AsObject();
        Assert.Equal(2_000, (long)element["durationMs"]!);
        Assert.Contains(UnableToComplete, (string)element["error"]!);
        Assert.NotNull(element["startedMs"]);

        // The audit's phaseTimings["comms-retry"] and the live element say the same thing.
        var auditJson = JsonNode.Parse(JsonSerializer.Serialize(audit))!["comms-retry"]!.AsObject();
        Assert.Equal((long)auditJson["durationMs"]!, (long)element["durationMs"]!);
        Assert.Equal((string)auditJson["error"]!, (string)element["error"]!);
        Assert.DoesNotContain("error", JsonSerializer.Serialize(live[0]));   // only comms-retry carries an error
    }

    [Fact]
    public async Task CommsRetry_IsRecordedInPhaseTimings_WithDurationAndErrorText()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();
        var recorder = new ConnectPhaseRecorder();
        recorder.Report(ConnectPhases.For(ConnectPhases.Upload));

        await CommsPolicy(host, poll => poll >= 3).RunAsync(attempt, retries, CancellationToken.None, recorder);
        recorder.NoteCommsRetry(retries.LastOrDefault(r => r.CommsRetry is not null)?.CommsRetry);
        var timings = recorder.Snapshot();

        Assert.Equal(2_000, timings.CommsRetry!.DurationMs);
        Assert.Contains(UnableToComplete, timings.CommsRetry.Error);
        var json = JsonNode.Parse(JsonSerializer.Serialize(timings))!["comms-retry"]!.AsObject();
        Assert.Equal(2_000, (long)json["durationMs"]!);
        Assert.Contains(UnableToComplete, (string)json["error"]!);
    }

    [Fact]
    public async Task CommsRetry_DefaultProbe_ResumesAfterOneStablePoll_AndRecordsThePhase()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await host.Policy.RunAsync(attempt, retries, CancellationToken.None);   // no CommsReady: built-in probe

        // First poll sets the baseline, the second sees the same server pids: ready after one 1 s interval.
        Assert.Equal([TimeSpan.FromSeconds(1)], host.Delays);
        var phase = retries.Single().CommsRetry!;
        Assert.Equal(1_000, phase.DurationMs);
        Assert.Contains(UnableToComplete, phase.Error);
        Assert.Contains("commsRetry", JsonSerializer.Serialize(retries));
    }

    [Fact]
    public async Task CommsRetry_DefaultProbe_PidChangeDuringWait_IsNotReadyUntilStable()
    {
        var host = new FakeCrashHost(_leaseScope.Lease);
        host.Pids.Enqueue([111]);          // attempt start
        host.Pids.Enqueue([111]);          // WaitForService: unchanged, no restart seen
        host.Pids.Enqueue([111]);          // probe baseline
        host.Pids.Enqueue([222]);          // probe: pids moved -> not ready
        // then 222 repeats -> stable
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await host.Policy.RunAsync(attempt, retries, CancellationToken.None);

        Assert.Equal(2, host.Delays.Count);
        Assert.Equal(2_000, retries.Single().CommsRetry!.DurationMs);
    }

    [Fact]
    public async Task CommsRetry_DefaultProbe_ServerNeverObservable_WaitsTheFullCeiling_AndRecordsIt()
    {
        var host = new FakeCrashHost(_leaseScope.Lease) { LastPids = [] };
        var (attempt, _) = Script(Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await host.Policy.RunAsync(attempt, retries, CancellationToken.None);

        Assert.Equal(10_000, retries.Single().WaitedMs);
        Assert.Equal(10_000, retries.Single().CommsRetry!.DurationMs);
        Assert.Contains(UnableToComplete, retries.Single().CommsRetry!.Error);
    }

    [Fact]
    public void AuditLog_CountsRestartsByPhase_OverTheLast24h_Only()
    {
        var old = DateTimeOffset.UtcNow.AddHours(-30);
        var restart = new SdkRestartRecord("upload", 1, 2, DateTimeOffset.UtcNow);
        AuditLogger.LogConnectSdkRestart(restart);
        AuditLogger.LogConnectSdkRestart(restart with { Phase = "save-l5x" });
        AuditLogger.LogConnectSdkRestart(restart);
        File.AppendAllText(Path.Combine(_auditDir, "audit.jsonl"),
            $$"""{"ts":"{{old:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}","event":"connect_sdk_restart","phase":"parse"}""" + "\n");

        var counts = SdkConnectCrashes.From(AuditLogger.ReadConnectSdkRestartPhases(DateTimeOffset.UtcNow.AddHours(-24)));

        Assert.Equal(3, counts.Total);
        Assert.Equal(2, counts.ByPhase["upload"]);
        Assert.Equal(1, counts.ByPhase["save-l5x"]);
        Assert.DoesNotContain("parse", counts.ByPhase.Keys);
        Assert.Equal(counts.Total, new DoctorEnvironment(null).SdkCrashesDuringConnect24h!.Total);
    }

    [Fact]
    public async Task Doctor_FormatsInConnectCrashCountsAndPhases()
    {
        var env = new DoctorHandlerTests.FakeEnv
        {
            SdkCrashes = 4,
            SdkCrashesDuringConnect24h = SdkConnectCrashes.From(["upload", "upload", "save-l5x"]),
        };

        var check = (await DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(false, false)))
            .Checks.Single(c => c.Id == "sdkService");

        Assert.Contains("LdSdkServer.exe crashes in the last 24 h: 4", check.Observed);
        Assert.Contains("LdSdkServer crashes inside a connect (last 24 h): 3 (upload 2, save-l5x 1)", check.Observed);
    }

    [Fact]
    public async Task Doctor_NoInConnectCrashes_SaysZero_AndEventLogUnreadable_StillShowsConnectLog()
    {
        var clean = new DoctorHandlerTests.FakeEnv { SdkCrashes = 0, SdkCrashesDuringConnect24h = SdkConnectCrashes.From([]) };
        var cleanCheck = (await DoctorHandler.ExecuteAsync(null, null, clean, new Capabilities(false, false)))
            .Checks.Single(c => c.Id == "sdkService");
        Assert.Contains("crashes inside a connect (last 24 h): 0", cleanCheck.Observed);

        var noEventLog = new DoctorHandlerTests.FakeEnv
        {
            SdkCrashes = null, SdkCrashesDuringConnect24h = SdkConnectCrashes.From(["parse"]),
        };
        var check = (await DoctorHandler.ExecuteAsync(null, null, noEventLog, new Capabilities(false, false)))
            .Checks.Single(c => c.Id == "sdkService");
        Assert.Contains("crashes inside a connect (last 24 h): 1 (parse 1)", check.Observed);
    }

    // ── G48: typed give-up, every crash recorded ─────────────────────────────

    /// <summary>
    /// A policy whose LdSdkServer pid changes once per crash: the pid read before an attempt and the one read while
    /// waiting after it differ, and the next attempt starts from the new pid.
    /// </summary>
    private SdkConnectRetryPolicy CrashingPolicy(FakeCrashHost host)
    {
        var reads = 0;
        var basePolicy = host.Policy;
        return new SdkConnectRetryPolicy
        {
            Recovery = basePolicy.Recovery,
            ServiceState = () => DoctorServiceState.Running,
            ServerPids = () => [100 + (reads++ + 1) / 2],
            ServicePing = basePolicy.ServicePing,
            Clock = basePolicy.Clock,
            Delay = basePolicy.Delay,
        };
    }

    [Fact]
    public async Task GiveUp_CrashesPastRetryLimit_RecordsTheFinalCrash_ForEveryAttempt()
    {
        var policy = CrashingPolicy(new FakeCrashHost(_leaseScope.Lease));
        var (attempt, calls) = Script(Failure(UnableToComplete));
        var retries = new List<ConnectRetryEntry>();
        var giveUp = new SdkConnectGiveUp();

        await Assert.ThrowsAsync<TagServiceException>(() =>
            policy.RunAsync(attempt, retries, CancellationToken.None, null, () => ConnectPhases.Upload, giveUp));

        Assert.Equal(3, calls());
        Assert.Equal(2, retries.Count);
        Assert.True(giveUp.GaveUp);
        Assert.Equal(new SdkRestartRecord(ConnectPhases.Upload, 102, 103, FakeCrashHost.Now), giveUp.FinalRestart);
    }

#if LOGIX_SDK
    private sealed class CrashingUploadFactory(int failures) : ISdkTagServiceFactory
    {
        public int Calls { get; private set; }

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
        {
            if (Calls++ < failures) throw Failure(UnableToComplete);
            return Task.FromResult(new SdkSessionResult(new VirtualTagService(), false, [], "upload-to-new-project"));
        }
    }

    private static Task<object> ConnectWith(CrashingUploadFactory factory, SdkConnectRetryPolicy policy) =>
        ConnectHandler.ExecuteAsync(
            gateway: "", path: "", useVirtual: false, new ProjectService(), new TagSessionService(),
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: "EmulateEthernet\\192.0.2.10", sdkConnectRetry: policy);

    [Fact]
    public async Task Connect_CrashesPastRetryLimit_ReturnsSdkServiceUnstable_WithOneRecordPerRestart()
    {
        var factory = new CrashingUploadFactory(failures: 99);

        var error = Assert.IsType<ConnectSdkServiceUnstableError>(
            await ConnectWith(factory, CrashingPolicy(new FakeCrashHost(_leaseScope.Lease))));

        Assert.Equal("SdkServiceUnstable", error.Kind);
        Assert.Equal(3, factory.Calls);
        Assert.Equal(3, error.SdkRestarts.Count);                       // one per crash, the third included
        Assert.Equal([101, 102, 103], error.SdkRestarts.Select(r => r.NewPid!.Value));
        Assert.Contains("restarted 3 times in ", error.Hint);
        Assert.Contains(" seconds", error.Hint);
        Assert.Contains("wait a minute", error.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect", error.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logix_doctor", error.Hint);
        var json = JsonSerializer.Serialize(error);
        Assert.Contains("\"kind\":\"SdkServiceUnstable\"", json);
        Assert.Contains("\"sdkRestarts\":[", json);
    }

    [Fact]
    public async Task Connect_SdkServiceUnstable_ErrorAndHintCarrySameWaitGuidance()
    {
        var error = Assert.IsType<ConnectSdkServiceUnstableError>(
            await ConnectWith(new CrashingUploadFactory(failures: 99), CrashingPolicy(new FakeCrashHost(_leaseScope.Lease))));

        Assert.Contains("wait a minute, then reconnect", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wait a minute", error.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect", error.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a few seconds", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildError_RewritesLegacyTail_ToWaitAMinuteThenReconnect()
    {
        var text = ConnectSdkServiceUnstableError.BuildError("p",
            "x. Recovery will restart it \u2014 wait a few seconds and retry.");

        Assert.EndsWith("wait a minute, then reconnect.", text);
        Assert.DoesNotContain("a few seconds", text);
    }

    [Fact]
    public void BuildError_CommsFaultGiveUp_DropsManualRestartAdvice_AndKeepsOneGuidance()
    {
        // The comms-fault text said "restart the LdSdkService service" next to "Recovery will restart it".
        var text = ConnectSdkServiceUnstableError.BuildError("p",
            "Could not extract the project uploaded from 'p'. The SDK reported: \"x\" The Logix SDK service (LdSdkService) is " +
            "running but its communications layer may have faulted. Check the Windows Application event log for " +
            "LdSdkServer.exe errors and restart the LdSdkService service, then retry.");

        Assert.DoesNotContain("restart the LdSdkService service", text);
        Assert.DoesNotContain("then retry", text);
        Assert.Contains("Recovery will restart it \u2014 wait a minute, then reconnect.", text);
        Assert.EndsWith("If it keeps failing, check the Windows Application event log for LdSdkServer.exe errors.", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "wait a minute"));
    }

    [Fact]
    public async Task Connect_GiveUpCrash_IsInTheAuditLog_AndDoctorCountsIt()
    {
        await ConnectWith(new CrashingUploadFactory(failures: 99), CrashingPolicy(new FakeCrashHost(_leaseScope.Lease)));

        var counts = SdkConnectCrashes.From(AuditLogger.ReadConnectSdkRestartPhases(DateTimeOffset.UtcNow.AddHours(-24)));

        Assert.Equal(3, counts.Total);
        Assert.Equal(3, new DoctorEnvironment(null).SdkCrashesDuringConnect24h!.Total);
    }

    [Fact]
    public async Task Connect_CommsRetry_AppearsInTheResultPhaseTimings()
    {
        var factory = new CrashingUploadFactory(failures: 1);
        var policy = CommsPolicy(new FakeCrashHost(_leaseScope.Lease), poll => poll >= 2);

        var result = Assert.IsType<ConnectResult>(await ConnectWith(factory, policy));

        Assert.Equal(2, factory.Calls);
        Assert.Equal(1_000, result.PhaseTimings!.CommsRetry!.DurationMs);
        Assert.Contains(UnableToComplete, result.PhaseTimings.CommsRetry.Error);
        Assert.Contains("\"comms-retry\"", JsonSerializer.Serialize(result.PhaseTimings));
    }

    [Fact]
    public async Task Connect_RecoversOnLastAllowedRetry_BehavesAsBefore()
    {
        var factory = new CrashingUploadFactory(failures: 2);

        var result = Assert.IsType<ConnectResult>(
            await ConnectWith(factory, CrashingPolicy(new FakeCrashHost(_leaseScope.Lease))));

        Assert.Equal("connected", result.Status);
        Assert.Equal(3, factory.Calls);
        Assert.Equal(2, result.Retries!.Count);
        Assert.All(result.Retries, r => Assert.NotNull(r.SdkRestart));
        Assert.Equal(2, SdkConnectCrashes.From(AuditLogger.ReadConnectSdkRestartPhases(DateTimeOffset.UtcNow.AddHours(-24))).Total);
    }
#endif
}
