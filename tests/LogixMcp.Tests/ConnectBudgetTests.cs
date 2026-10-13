using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Background SDK connect with a synchronous budget: a connect that finishes inside the budget returns
/// today's payload; a longer one returns <c>status:"connecting"</c> and keeps running in the server.
/// Uses a fake SDK session factory whose connect takes a configurable time.
/// </summary>
[Collection("ProcessEnvironment")]
public class ConnectBudgetTests
{
#if LOGIX_SDK
    private const string CommPath = "EmulateEthernet\\192.0.2.10";

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="BudgetController" TargetType="Controller" ContainsContext="false">
          <Controller Name="BudgetController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags />
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// Fake SDK session: reports upload, waits <c>connectTime</c>, reports parse, then returns a canned
    /// session (or throws when <c>fail</c> is set).
    /// </summary>
    private sealed class SlowFactory(TimeSpan connectTime, bool fail = false) : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => CreateFromControllerAsync(commPath, progress: null, ct);

        public async Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath, IProgress<ConnectProgress>? progress, CancellationToken ct)
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.Upload));
            await Task.Delay(connectTime, ct);
            if (fail) throw new InvalidOperationException("fake SDK connect failed");
            progress?.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
            return new SdkSessionResult(
                Service:           new VirtualTagService(),
                DownloadPerformed: false,
                ModeTransitions:   [],
                CorrelationPath:   "upload-to-new-project",
                TypeProject:       L5X.Parse(MinimalL5Xml),
                AcdPath:           Path.Combine(Path.GetTempPath(), "budget-test-uploaded.ACD"));
        }
    }

    /// <summary>The connect payload without its measured <c>phaseTimings</c>.</summary>
    private static string WithoutTimings(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("phaseTimings");
        return node.ToJsonString();
    }

    private static (LogixTagTools Tools, ConnectState State) Make(ISdkTagServiceFactory factory)
    {
        var state = new ConnectState();
        var tools = new LogixTagTools(
            new ProjectService(), new TagSessionService(), new ControllerModeService(),
            capabilities: null, sdkFactory: factory, connectState: state);
        return (tools, state);
    }

    private static Task<string> Connect(LogixTagTools tools, TimeSpan budget) =>
        tools.GetBudgetedConnectResponseAsync(
            budget, backend: "sdk", sdkMode: "online", sdkCommPath: CommPath);

    /// <summary>A connect inside the budget returns exactly the unbudgeted payload and no 'connecting'.</summary>
    [Fact]
    public async Task ConnectWithinBudget_ReturnsTodaysPayload_WithoutConnectingStatus()
    {
        var (budgeted, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(30)));
        var (plain, _)        = Make(new SlowFactory(TimeSpan.FromMilliseconds(30)));

        var withBudget = await Connect(budgeted, TimeSpan.FromSeconds(30));
        var today      = await plain.GetConnectResponseAsync(
            backend: "sdk", sdkMode: "online", sdkCommPath: CommPath);

        // phaseTimings are measured durations and differ run to run; everything else must match.
        Assert.Equal(WithoutTimings(today), WithoutTimings(withBudget));
        using var json = JsonDocument.Parse(withBudget);
        Assert.Equal("connected", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.TryGetProperty("connectId", out _));
        Assert.Null(state.Current);
    }

    /// <summary>
    /// A connect longer than the budget returns the 'connecting' payload and keeps running; its final
    /// payload lands in <see cref="ConnectState.LastOutcome"/> and the session is attached.
    /// </summary>
    [Fact]
    public async Task ConnectOverBudget_ReturnsConnecting_AndKeepsRunning()
    {
        var (tools, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(400)));

        var response = await Connect(tools, TimeSpan.FromMilliseconds(50));

        using var json = JsonDocument.Parse(response);
        var root = json.RootElement;
        Assert.Equal("connecting", root.GetProperty("status").GetString());
        var connectId = root.GetProperty("connectId").GetString();
        Assert.False(string.IsNullOrEmpty(connectId));
        // The connect runs on a pool thread, so the fake may not have reported 'upload' yet when the budget expires.
        Assert.Contains(root.GetProperty("phase").GetString(), new[] { "starting", ConnectPhases.Upload });
        Assert.True(root.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Equal("logix_connect" + "_status", root.GetProperty("pollWith").GetString());

        var current = state.Current;
        Assert.NotNull(current);
        Assert.Equal(connectId, current!.ConnectId);
        Assert.Null(state.LastOutcome);

        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(state.Current);
        var outcome = state.LastOutcome;
        Assert.NotNull(outcome);
        Assert.Equal(connectId, outcome!.ConnectId);
        Assert.Null(outcome.Error);
        using var final = JsonDocument.Parse(outcome.ResponseJson!);
        Assert.Equal("connected", final.RootElement.GetProperty("status").GetString());
    }

    /// <summary>A second connect while one is running reports the running one instead of starting another.</summary>
    [Fact]
    public async Task ConnectWhileConnecting_ReturnsSameConnectId()
    {
        var (tools, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(400)));

        using var first  = JsonDocument.Parse(await Connect(tools, TimeSpan.FromMilliseconds(30)));
        using var second = JsonDocument.Parse(await Connect(tools, TimeSpan.FromSeconds(30)));

        Assert.Equal("connecting", second.RootElement.GetProperty("status").GetString());
        Assert.Equal(first.RootElement.GetProperty("connectId").GetString(),
                     second.RootElement.GetProperty("connectId").GetString());
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A background connect that throws is recorded as the last outcome with its error.</summary>
    [Fact]
    public async Task BackgroundConnectFailure_IsRecordedAsLastOutcome()
    {
        var (tools, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(200), fail: true));

        using var json = JsonDocument.Parse(await Connect(tools, TimeSpan.FromMilliseconds(30)));
        Assert.Equal("connecting", json.RootElement.GetProperty("status").GetString());

        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(state.Current);
        Assert.NotNull(state.LastOutcome);
        Assert.NotNull(state.LastOutcome!.Error);
        Assert.Null(state.LastOutcome.ResponseJson);
    }

    /// <summary>After 'connecting', logix_connect_status reaches 'connected' with the sync path's payload.</summary>
    [Fact]
    public async Task ConnectStatus_AfterConnecting_ReachesConnected_SameAsSyncPath()
    {
        var (tools, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(300)));
        var (plain, _)     = Make(new SlowFactory(TimeSpan.FromMilliseconds(30)));

        using var first = JsonDocument.Parse(await Connect(tools, TimeSpan.FromMilliseconds(30)));
        Assert.Equal("connecting", first.RootElement.GetProperty("status").GetString());
        using var running = JsonDocument.Parse(tools.ConnectStatus());
        Assert.Equal("connecting", running.RootElement.GetProperty("status").GetString());

        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var today = System.Text.Json.Nodes.JsonNode.Parse(await plain.GetConnectResponseAsync(
            backend: "sdk", sdkMode: "online", sdkCommPath: CommPath))!.AsObject();
        var done = System.Text.Json.Nodes.JsonNode.Parse(tools.ConnectStatus())!.AsObject();
        Assert.Equal("connected", done["status"]!.GetValue<string>());
        Assert.Equal(first.RootElement.GetProperty("connectId").GetString(), done["connectId"]!.GetValue<string>());
        foreach (var f in new[] { "connectId", "phase", "elapsedMs", "phaseTimings", "connected", "serverInstance" }) done.Remove(f);
        today.Remove("phaseTimings");
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(today, done));
    }

    /// <summary>A failing background connect surfaces as 'failed' with its typed error.</summary>
    [Fact]
    public async Task ConnectStatus_FailingConnect_SurfacesTypedError()
    {
        var (tools, state) = Make(new SlowFactory(TimeSpan.FromMilliseconds(100), fail: true));

        await Connect(tools, TimeSpan.FromMilliseconds(10));
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        using var json = JsonDocument.Parse(tools.ConnectStatus());
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("kind").GetString()));
    }

#endif

    /// <summary>The budget defaults to 20 s and is overridden by LGX_CONNECT_SYNC_BUDGET_S.</summary>
    [Fact]
    public void SyncBudget_DefaultsTo20Seconds_AndCanBeOverridden()
    {
        Assert.Equal("LGX_CONNECT_SYNC_BUDGET_S", ConnectState.BudgetEnvVar);
        Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.DefaultSyncBudget);
        Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.ResolveSyncBudget(null));
        Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.ResolveSyncBudget(""));
        Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.ResolveSyncBudget("abc"));
        Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.ResolveSyncBudget("-3"));
        Assert.Equal(TimeSpan.FromSeconds(5), ConnectState.ResolveSyncBudget("5"));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ConnectState.ResolveSyncBudget("1.5"));
        Assert.Equal(TimeSpan.Zero, ConnectState.ResolveSyncBudget("0"));
    }

    /// <summary>A huge budget such as "1e9" is clamped to 600 s instead of throwing.</summary>
    [Fact]
    public void SyncBudget_1e9_IsClampedTo600Seconds_WithoutThrowing()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), ConnectState.ResolveSyncBudget("1e9"));
        Assert.Equal(TimeSpan.FromSeconds(600), ConnectState.ResolveSyncBudget("601"));
        Assert.Equal(TimeSpan.FromSeconds(600), ConnectState.ResolveSyncBudget("600"));
        Assert.Equal(TimeSpan.FromSeconds(600), ConnectState.ResolveSyncBudget("Infinity"));
        Assert.Equal(TimeSpan.FromSeconds(20),  ConnectState.ResolveSyncBudget("NaN"));
    }

    /// <summary>A wait that throws after the job started yields the 'connecting' payload, not an error.</summary>
    [Fact]
    public async Task WaitException_AfterJobStarted_ReturnsConnectingPayload()
    {
        var state   = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A budget beyond Task.Delay's range makes the wait itself throw ArgumentOutOfRangeException.
        var response = await state.RunAsync(
            async _ => { await release.Task; return "{}"; },
            TimeSpan.FromDays(100));

        using var json = JsonDocument.Parse(response);
        Assert.Equal("connecting", json.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("connectId").GetString()));

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(state.LastOutcome);
    }

    /// <summary>The environment variable is what <see cref="ConnectState.SyncBudgetFromEnvironment"/> reads.</summary>
    [Fact]
    public void SyncBudgetFromEnvironment_ReadsTheVariable()
    {
        var saved = Environment.GetEnvironmentVariable(ConnectState.BudgetEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ConnectState.BudgetEnvVar, null);
            Assert.Equal(TimeSpan.FromSeconds(20), ConnectState.SyncBudgetFromEnvironment());
            Environment.SetEnvironmentVariable(ConnectState.BudgetEnvVar, "7");
            Assert.Equal(TimeSpan.FromSeconds(7), ConnectState.SyncBudgetFromEnvironment());
        }
        finally { Environment.SetEnvironmentVariable(ConnectState.BudgetEnvVar, saved); }
    }
}
