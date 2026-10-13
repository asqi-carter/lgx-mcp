using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the LdSdkService retry on the SDK upload connect (<see cref="SdkConnectRetryPolicy"/>) and for
/// the LdSdkServer crash count in <c>logix_doctor</c>. Fakes only: no waiting, no service, no event log.
/// </summary>
public class SdkConnectRetryTests : IDisposable
{
    private const string UnableToComplete = "Unable to complete connection to LdSdkService (127.0.0.1:50051)";
    private const string CouldNotReach    = "Could not reach LdSdkService";
    private const string ProjectNotOpen   = "Project is not open.";

    // Per-test lease file: the policy's recovery coordinator must not touch the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();

    /// <summary>A policy whose delay is recorded, not awaited, and whose service state is scripted.</summary>
    private sealed class FakePolicy(HostUploadLease lease)
    {
        private readonly SdkServiceRecovery _recovery =
            new($@"Global\LogixMcp.Tests.{Guid.NewGuid():N}", lease: lease);

        public List<TimeSpan> Delays { get; } = [];
        public Queue<string> States { get; } = new();
        /// <summary>Total time the policy waited; the no-restart back-off is slept in poll-interval steps up to its ceiling.</summary>
        public TimeSpan TotalDelay => TimeSpan.FromTicks(Delays.Sum(d => d.Ticks));
        public string DefaultState { get; set; } = DoctorServiceState.Running;

        public SdkConnectRetryPolicy Policy => new()
        {
            Recovery = _recovery,
            ServiceState = () => States.Count > 0 ? States.Dequeue() : DefaultState,
            Delay = (t, _) => { Delays.Add(t); return Task.CompletedTask; },
        };
    }

    private static TagServiceException Failure(string sdkMessage) =>
        new(new TagServiceError(TagServiceErrorKind.ConnectionFailed,
            $"The SDK call for controller at 'EmulateEthernet\\192.0.2.10' failed: \"{sdkMessage}\" more text"),
            new InvalidOperationException(sdkMessage));

    /// <summary>An attempt that throws the queued failures in turn, then returns "connected".</summary>
    private static (Func<Task<string>> Attempt, Func<int> Calls) Script(params Exception?[] outcomes)
    {
        var calls = 0;
        return (() =>
        {
            var o = outcomes[Math.Min(calls++, outcomes.Length - 1)];
            return o is null ? Task.FromResult("connected") : Task.FromException<string>(o);
        }, () => calls);
    }

    [Theory]
    [InlineData(UnableToComplete)]
    [InlineData(CouldNotReach)]
    [InlineData(ProjectNotOpen)]
    public async Task Retry_EachListedError_RetriesTwice_WithBackoff10sThen20s_AndReportsRetries(string sdkMessage)
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(sdkMessage));   // always fails
        var retries = new List<ConnectRetryEntry>();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => fake.Policy.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Contains(sdkMessage, ex.Error.Message);
        Assert.Equal(3, calls());                                  // first try + 2 retries, never more
        Assert.Equal(TimeSpan.FromSeconds(30), fake.TotalDelay);   // ceilings 10 s + 20 s, polled in 1 s steps (nothing ready)
        Assert.Equal(2, retries.Count);
        Assert.Equal(10_000, retries[0].WaitedMs);
        Assert.Equal(20_000, retries[1].WaitedMs);
        Assert.All(retries, r => Assert.Contains(sdkMessage, r.Error));
    }

    [Fact]
    public async Task Retry_ConnectThatFailsTwiceThenSucceeds_EndsConnectedWithTwoRetries()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(UnableToComplete), Failure(CouldNotReach), null);
        var retries = new List<ConnectRetryEntry>();

        var result = await fake.Policy.RunAsync(attempt, retries, CancellationToken.None);

        Assert.Equal("connected", result);
        Assert.Equal(3, calls());
        Assert.Equal(2, retries.Count);
        Assert.Contains(UnableToComplete, retries[0].Error);
        Assert.Contains(CouldNotReach, retries[1].Error);
    }

    private const string FatalError = "LgxSrv_E_FATAL_ERROR";

    [Fact]
    public async Task Retry_FatalError_IsRetriedExactlyOnce()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(FatalError));   // always fails
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(
            () => fake.Policy.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(2, calls());                             // first try + exactly one retry
        Assert.Single(retries);
        Assert.Contains(FatalError, retries[0].Error);
    }

    [Fact]
    public async Task Retry_FatalErrorThenSuccess_EndsConnected()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(FatalError), null);

        var result = await fake.Policy.RunAsync(attempt, [], CancellationToken.None);

        Assert.Equal("connected", result);
        Assert.Equal(2, calls());
    }

    [Fact]
    public async Task Retry_FirstTrySucceeds_NoRetries_NoWait()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script((Exception?)null);
        var retries = new List<ConnectRetryEntry>();

        Assert.Equal("connected", await fake.Policy.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(1, calls());
        Assert.Empty(retries);
        Assert.Empty(fake.Delays);
    }

    [Theory]
    [InlineData("RxE_COMM_DTLERROR - Failed to call FactoryTalk Linx communication software")]
    [InlineData("Upload from controller failed: something else entirely")]
    [InlineData("RxCL_E_CANNOT_UPLOAD_PHYS_ADDR")]
    public async Task Retry_OtherErrors_AreNotRetried(string sdkMessage)
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(Failure(sdkMessage));
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(
            () => fake.Policy.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(1, calls());
        Assert.Empty(retries);
        Assert.Empty(fake.Delays);
    }

    [Fact]
    public async Task Retry_NonTagServiceException_IsNotRetried()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var (attempt, calls) = Script(new InvalidOperationException(UnableToComplete));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fake.Policy.RunAsync(attempt, [], CancellationToken.None));

        Assert.Equal(1, calls());
    }

    [Fact]
    public async Task Retry_ServiceSeenDownThenRunning_ResumesAtOnce_WithoutTheBackoff()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        // Before retry 1: Stopped, StartPending, then Running. Before retry 2: Running at once.
        fake.States.Enqueue("Stopped");
        fake.States.Enqueue("StartPending");
        fake.States.Enqueue(DoctorServiceState.Running);
        var (attempt, _) = Script(Failure(UnableToComplete), Failure(UnableToComplete), null);
        var retries = new List<ConnectRetryEntry>();

        await fake.Policy.RunAsync(attempt, retries, CancellationToken.None);

        // G46: retry 1 saw the service go down and come back, so it starts as soon as the service is Running (no
        // 10 s back-off). Retry 2 never saw it down, so the 20 s ceiling still applies.
        Assert.Equal(TimeSpan.FromSeconds(2 + 20), fake.TotalDelay);
        Assert.Equal(2_000, retries[0].WaitedMs);    // 2 s polling, no back-off
        Assert.Equal(20_000, retries[1].WaitedMs);
    }

    [Fact]
    public async Task Retry_ServiceNeverRunning_PollsAtMost30s_ThenBacksOffAnyway()
    {
        var fake = new FakePolicy(_leaseScope.Lease) { DefaultState = "Stopped" };
        var (attempt, calls) = Script(Failure(ProjectNotOpen));
        var retries = new List<ConnectRetryEntry>();

        await Assert.ThrowsAsync<TagServiceException>(
            () => fake.Policy.RunAsync(attempt, retries, CancellationToken.None));

        Assert.Equal(3, calls());
        Assert.Equal(30_000 + 10_000, retries[0].WaitedMs);
        Assert.Equal(30_000 + 20_000, retries[1].WaitedMs);
    }

#if LOGIX_SDK
    /// <summary>Factory whose upload fails with the queued errors in turn, then succeeds.</summary>
    private sealed class FlakyUploadFactory(params string?[] outcomes) : ISdkTagServiceFactory
    {
        public int Calls { get; private set; }

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
        {
            var outcome = outcomes[Math.Min(Calls++, outcomes.Length - 1)];
            if (outcome is not null) throw Failure(outcome);
            return Task.FromResult(new SdkSessionResult(
                new VirtualTagService(), false, [], "upload-to-new-project"));
        }
    }

    private static Task<object> ConnectWith(FlakyUploadFactory factory, FakePolicy fake) =>
        ConnectHandler.ExecuteAsync(
            gateway: "", path: "", useVirtual: false, new ProjectService(), new TagSessionService(),
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: "EmulateEthernet\\192.0.2.10", sdkConnectRetry: fake.Policy);

    [Fact]
    public async Task Connect_FailsTwiceThenSucceeds_EndsConnected_WithTwoRetries()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var factory = new FlakyUploadFactory(UnableToComplete, ProjectNotOpen, null);

        var result = Assert.IsType<ConnectResult>(await ConnectWith(factory, fake));

        Assert.Equal("connected", result.Status);
        Assert.Equal(3, factory.Calls);
        Assert.NotNull(result.Retries);
        Assert.Equal(2, result.Retries!.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), fake.TotalDelay);   // ceilings 10 s + 20 s, polled in 1 s steps (nothing ready)
    }

    [Fact]
    public async Task Connect_OtherError_IsNotRetried_AndReportsNoRetries()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var factory = new FlakyUploadFactory("some other upload failure");

        var error = Assert.IsType<ConnectError>(await ConnectWith(factory, fake));

        Assert.Equal(1, factory.Calls);
        Assert.Null(error.Retries);
        Assert.Empty(fake.Delays);
    }

    [Fact]
    public async Task Connect_FatalErrorDuringUpload_IsRetriedOnce_AndMessageNamesTheControllerPath()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var factory = new FlakyUploadFactory(FatalError);   // always fails

        var error = Assert.IsType<ConnectError>(await ConnectWith(factory, fake));

        Assert.Equal(2, factory.Calls);
        Assert.Single(error.Retries!);
        Assert.Contains("EmulateEthernet\\192.0.2.10", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("''", error.Error, StringComparison.Ordinal);
        Assert.Contains(FatalError, error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_StillFailingAfterTwoRetries_ReturnsErrorWithRetries()
    {
        var fake = new FakePolicy(_leaseScope.Lease);
        var factory = new FlakyUploadFactory(CouldNotReach);

        var error = Assert.IsType<ConnectError>(await ConnectWith(factory, fake));

        Assert.Equal(3, factory.Calls);
        Assert.Equal(2, error.Retries!.Count);
        Assert.Contains(CouldNotReach, error.Error);
    }
#endif

    // ── logix_doctor: LdSdkServer crash count ────────────────────────────────

    private static Task<DoctorResult> RunDoctor(DoctorHandlerTests.FakeEnv env) =>
        DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(LiveWritesEnabled: false));

    [Fact]
    public async Task Doctor_ReportsCrashCount_AndAddsHint_WhenAboveZero()
    {
        var env = new DoctorHandlerTests.FakeEnv { SdkCrashes = 3 };

        var c = (await RunDoctor(env)).Checks.Single(x => x.Id == "sdkService");

        Assert.Contains("LdSdkServer.exe crashes in the last 24 h: 3", c.Observed);
        Assert.Equal("warn", c.Status);
        Assert.NotNull(c.Fix);
        Assert.Contains("non-elevated session-1", c.Fix);
        Assert.Contains("ReceivedFile.Dispose()", c.Fix);
    }

    [Fact]
    public async Task Doctor_ShowsNewestCrashTime_AndElevationExplanationOnlyWhenNotElevated()
    {
        var newest = DateTimeOffset.Parse("2026-10-08T11:00:02Z");

        var notElevated = new DoctorHandlerTests.FakeEnv { SdkCrashes = 2, NewestCrash = newest, IsElevated = false };
        var c = (await RunDoctor(notElevated)).Checks.Single(x => x.Id == "sdkService");
        Assert.Contains("crashes in the last 24 h: 2 (newest 2026-10-08 11:00:02Z)", c.Observed);
        Assert.Contains(DoctorHandler.SdkServerCrashHint, c.Fix);
        Assert.Contains("non-elevated session-1", c.Fix);

        var elevated = new DoctorHandlerTests.FakeEnv { SdkCrashes = 2, NewestCrash = newest, IsElevated = true };
        var e = (await RunDoctor(elevated)).Checks.Single(x => x.Id == "sdkService");
        Assert.Contains("(newest 2026-10-08 11:00:02Z)", e.Observed);
        Assert.Contains(DoctorHandler.SdkServerCrashHint, e.Fix);
        Assert.DoesNotContain("non-elevated", e.Fix);
        Assert.DoesNotContain("ReceivedFile.Dispose()", e.Fix);

        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var xml =
            EventXml(".NET Runtime",      "2026-10-08T11:00:00.0000000Z", "Application: LdSdkServer.exe") +
            EventXml("Application Error", "2026-10-08T11:00:02.0000000Z", "Faulting application name: LdSdkServer.exe") +
            EventXml("Application Error", "2026-10-08T10:00:00.0000000Z", "Faulting application name: LdSdkServer.exe");
        Assert.Equal(newest, DoctorEnvironment.NewestSdkServerCrash(xml, now));
        Assert.Null(DoctorEnvironment.NewestSdkServerCrash("", now));
    }

    [Fact]
    public async Task Doctor_ZeroCrashes_ReportsCount_NoHint_StaysOk()
    {
        var env = new DoctorHandlerTests.FakeEnv { SdkCrashes = 0 };

        var c = (await RunDoctor(env)).Checks.Single(x => x.Id == "sdkService");

        Assert.Equal("ok", c.Status);
        Assert.Contains("crashes in the last 24 h: 0", c.Observed);
        Assert.Null(c.Fix);
    }

    [Fact]
    public async Task Doctor_CrashCountUnavailable_ChangesNothing()
    {
        var env = new DoctorHandlerTests.FakeEnv { SdkCrashes = null };

        var c = (await RunDoctor(env)).Checks.Single(x => x.Id == "sdkService");

        Assert.Equal("ok", c.Status);
        Assert.DoesNotContain("crashes", c.Observed);
        Assert.Null(c.Fix);
    }

    [Fact]
    public async Task Doctor_ServiceStopped_KeepsFail_AndAppendsCrashHint()
    {
        var env = new DoctorHandlerTests.FakeEnv { SdkCrashes = 1 };
        env.Services[DoctorHandler.SdkServiceName] = "Stopped";

        var c = (await RunDoctor(env)).Checks.Single(x => x.Id == "sdkService");

        Assert.Equal("fail", c.Status);
        Assert.StartsWith("Restart the LdSdkService Windows service", c.Fix);
        Assert.Contains("ReceivedFile.Dispose()", c.Fix);
    }

    private static string EventXml(string provider, string time, string data) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
        $"<Provider Name='{provider}'/><TimeCreated SystemTime='{time}'/></System>" +
        $"<EventData><Data>{data}</Data></EventData></Event>";

    [Fact]
    public void CountSdkServerCrashes_CountsOnlyLdSdkServerEventsInWindow_AndMergesPairedEvents()
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var xml =
            // one crash, logged by both sources 2 s apart -> counts once
            EventXml(".NET Runtime",      "2026-10-08T11:00:00.0000000Z", "Application: LdSdkServer.exe") +
            EventXml("Application Error", "2026-10-08T11:00:02.0000000Z", "Faulting application name: LdSdkServer.exe") +
            // second crash an hour earlier
            EventXml("Application Error", "2026-10-08T10:00:00.0000000Z", "Faulting application name: LdSdkServer.exe") +
            // another process: ignored
            EventXml("Application Error", "2026-10-08T09:00:00.0000000Z", "Faulting application name: notepad.exe") +
            // older than 24 h: ignored
            EventXml("Application Error", "2026-10-07T08:00:00.0000000Z", "Faulting application name: LdSdkServer.exe") +
            // other provider: ignored
            EventXml("Some Other Source", "2026-10-08T08:00:00.0000000Z", "LdSdkServer.exe");

        Assert.Equal(2, DoctorEnvironment.CountSdkServerCrashes(xml, now));
        Assert.Equal(0, DoctorEnvironment.CountSdkServerCrashes("", now));
        Assert.Null(DoctorEnvironment.CountSdkServerCrashes("<Event>not closed", now));
    }
}
