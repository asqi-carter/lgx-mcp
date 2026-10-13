using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LogixMcp.Tests;

/// <summary>
/// G37: an SDK service restart (LdSdkServer pid change since connect) fails every long wait fast with a typed
/// SessionLost <c>sdk-service-restarted</c>, and logix_doctor reports the 24 h and 7 d crash counts. Pids and the
/// event-log text are fixtures; nothing here reads rung state codes (er / rN / N / e / dN) from a session image.
/// </summary>
public class SdkRestartWatchTests
{
    private sealed class FakeHost
    {
        public volatile int[] ServerPids = [111];
        public SessionLostEvidence Evidence() => new(() => ServerPids, () => false, () => null, 4000);
    }

    private static (TagSessionService Session, FakeHost Host) Connected()
    {
        var host = new FakeHost();
        var session = new TagSessionService
        {
            LostEvidence = host.Evidence(),
            RestartPollInterval = TimeSpan.FromMilliseconds(50),
        };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
        return (session, host);
    }

    private static JsonObject Payload(CallToolResult r) =>
        (JsonObject)JsonNode.Parse(((TextContentBlock)r.Content[0]).Text)!;

    private static void AssertRestartLost(CallToolResult r, string tool)
    {
        Assert.True(r.IsError);
        var p = Payload(r);
        Assert.Equal(ErrorKinds.SessionLost, (string)p["kind"]!);
        Assert.Equal(SessionLostCauses.SdkServiceRestarted, (string)p["cause"]!);
        Assert.Equal(tool, (string)p["tool"]!);
        Assert.Contains("LdSdkServer pid 111", (string)p["evidence"]!);
        Assert.Contains("Reconnect", (string)p["hint"]!);
        Assert.Contains("logix_connect", (string)p["hint"]!);
        Assert.Contains("restarted", (string)p["error"]!);
        Assert.False((bool)p["connected"]!);
    }

    [Fact]
    public async Task FakePidChangeDuringPublishWait_GivesSessionLostSdkServiceRestarted_WithinBound()
    {
        var (session, host) = Connected();
        var neverFinishes = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sw = Stopwatch.StartNew();
        var running = ToolErrorFilter.InvokeAsync("logix_propose_rung_edit", () => new ValueTask<CallToolResult>(neverFinishes.Task),
            tagSession: session).AsTask();
        await Task.Delay(150);
        Assert.False(running.IsCompleted);          // still waiting on the (fake) 81 s publish
        host.ServerPids = [222];                    // the service restarted
        var result = await running.WaitAsync(SessionLostSupport.RestartFailureBound);
        sw.Stop();

        AssertRestartLost(result, "logix_propose_rung_edit");
        Assert.True(sw.Elapsed < SessionLostSupport.RestartFailureBound, $"took {sw.Elapsed}");
        Assert.NotNull(session.SessionLost);
        Assert.False(session.IsConnected);
    }

    [Theory]
    [InlineData("logix_propose_rung_edit")]
    [InlineData("logix_stage_rungs")]
    [InlineData("logix_export_rungs")]
    [InlineData("logix_pending_edits")]
    public async Task FakePidChange_FailsFast_OnPublishStageExportAndPendingPaths(string tool)
    {
        var (session, host) = Connected();
        var neverFinishes = new TaskCompletionSource<CallToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = ToolErrorFilter.InvokeAsync(tool, () => new ValueTask<CallToolResult>(neverFinishes.Task), tagSession: session).AsTask();
        host.ServerPids = [333, 334];

        AssertRestartLost(await running.WaitAsync(SessionLostSupport.RestartFailureBound), tool);
    }

    [Fact]
    public async Task FakePidChange_SessionLostSdkRestartedAt_IsNewProcessStartTime_EarlierThanLostAt()
    {
        var restartedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var host = new FakeHost();
        var session = new TagSessionService
        {
            LostEvidence = new SessionLostEvidence(() => host.ServerPids, () => false, () => null, 4000,
                processStartTime: pid => pid == 222 ? restartedAt : DateTimeOffset.UtcNow.AddHours(-5)),
            RestartPollInterval = TimeSpan.FromMilliseconds(50),
        };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
        host.ServerPids = [222];

        var result = await ToolErrorFilter.InvokeAsync("logix_pending_edits",
            () => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "{}" }] }),
            tagSession: session);

        var p = Payload(result);
        var sdkRestartedAt = DateTimeOffset.Parse((string)p["sdkRestartedAt"]!);
        var lostAt = DateTimeOffset.Parse((string)p["lostAt"]!);
        Assert.Equal(restartedAt, sdkRestartedAt);
        Assert.True(sdkRestartedAt < lostAt);
        Assert.True(lostAt > DateTimeOffset.UtcNow.AddMinutes(-1));   // lostAt stays the detection time
        Assert.Equal(restartedAt, session.SessionLost!.SdkRestartedAt);
    }

    [Fact]
    public async Task PidChangedBeforeCall_FailsWithoutRunningTheTool()
    {
        var (session, host) = Connected();
        host.ServerPids = [];
        var ran = false;

        var result = await ToolErrorFilter.InvokeAsync("logix_pending_edits",
            () => { ran = true; return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "{}" }] }); },
            tagSession: session);

        Assert.False(ran);
        AssertRestartLost(result, "logix_pending_edits");
    }

    [Fact]
    public async Task UnchangedPid_LetsTheCallComplete()
    {
        var (session, _) = Connected();

        var result = await ToolErrorFilter.InvokeAsync("logix_read", async () =>
        {
            await Task.Delay(200);
            return new CallToolResult { Content = [new TextContentBlock { Text = "{\"ok\":true}" }] };
        }, tagSession: session);

        Assert.NotEqual(true, result.IsError);
        Assert.Null(session.SessionLost);
    }

    // ── logix_doctor crash counts ────────────────────────────────────────────

    private static string EventXml(string provider, int id, string time, string data) =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System>" +
        $"<Provider Name='{provider}'/><EventID>{id}</EventID><TimeCreated SystemTime='{time}'/></System>" +
        $"<EventData><Data>{data}</Data></EventData></Event>";

    [Fact]
    public void CountSdkCrashes_FixtureLogQuery_Counts24hAnd7d_FromSystem7031AndApplicationEvents()
    {
        var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var system =
            EventXml("Service Control Manager", 7031, "2026-10-10T11:00:00.0000000Z", "The LdSdkService service terminated unexpectedly.") +
            EventXml("Service Control Manager", 7031, "2026-10-10T09:00:00.0000000Z", "The LdSdkService service terminated unexpectedly.") +
            EventXml("Service Control Manager", 7031, "2026-10-07T09:00:00.0000000Z", "The LdSdkService service terminated unexpectedly.") +
            EventXml("Service Control Manager", 7031, "2026-10-10T08:00:00.0000000Z", "The Spooler service terminated unexpectedly.") +
            EventXml("Service Control Manager", 7036, "2026-10-10T07:00:00.0000000Z", "The LdSdkService service entered the stopped state.") +
            EventXml("Service Control Manager", 7031, "2026-09-01T09:00:00.0000000Z", "The LdSdkService service terminated unexpectedly.");
        // The Application-log pair for the 11:00 crash merges with the System event; 06:00 is its own crash.
        var app =
            EventXml(".NET Runtime", 1026, "2026-10-10T11:00:05.0000000Z", "Application: LdSdkServer.exe") +
            EventXml("Application Error", 1000, "2026-10-10T06:00:00.0000000Z", "Faulting application name: LdSdkServer.exe");

        Assert.Equal(3, DoctorEnvironment.CountSdkCrashes(app, system, now, TimeSpan.FromHours(24)));
        Assert.Equal(4, DoctorEnvironment.CountSdkCrashes(app, system, now, TimeSpan.FromDays(7)));
        Assert.Equal(0, DoctorEnvironment.CountSdkCrashes("", "", now, TimeSpan.FromDays(7)));
        Assert.Null(DoctorEnvironment.CountSdkCrashes(app, "<Event>not closed", now, TimeSpan.FromDays(7)));
    }

    private static Task<DoctorResult> RunDoctor(DoctorHandlerTests.FakeEnv env) =>
        DoctorHandler.ExecuteAsync(null, null, env, new Capabilities(LiveWritesEnabled: false));

    [Fact]
    public async Task Doctor_FormatsCrashCounts24hAnd7d()
    {
        var env = new DoctorHandlerTests.FakeEnv { SdkCrashes = 3, SdkCrashes7d = 9 };

        var c = (await RunDoctor(env)).Checks.Single(x => x.Id == "sdkService");

        Assert.Contains("crashes in the last 24 h: 3", c.Observed);
        Assert.Contains("last 7 d: 9", c.Observed);
        Assert.DoesNotContain("more than 10", c.Observed);
    }

    [Fact]
    public async Task Doctor_AddsNote_OnlyWhen24hCountExceeds10()
    {
        var ten = (await RunDoctor(new DoctorHandlerTests.FakeEnv { SdkCrashes = 10, SdkCrashes7d = 10 }))
            .Checks.Single(x => x.Id == "sdkService");
        Assert.DoesNotContain("more than 10", ten.Observed);

        var eleven = (await RunDoctor(new DoctorHandlerTests.FakeEnv { SdkCrashes = 11, SdkCrashes7d = 40 }))
            .Checks.Single(x => x.Id == "sdkService");
        Assert.Contains("crashes in the last 24 h: 11", eleven.Observed);
        Assert.Contains("last 7 d: 40", eleven.Observed);
        Assert.Contains("more than 10", eleven.Observed);
        Assert.Equal("warn", eleven.Status);
    }

    [Fact]
    public async Task Doctor_ZeroCrashes24h_StillReports7dCount()
    {
        var c = (await RunDoctor(new DoctorHandlerTests.FakeEnv { SdkCrashes = 0, SdkCrashes7d = 2 }))
            .Checks.Single(x => x.Id == "sdkService");

        Assert.Equal("ok", c.Status);
        Assert.Contains("last 24 h: 0; last 7 d: 2", c.Observed);
    }
}
