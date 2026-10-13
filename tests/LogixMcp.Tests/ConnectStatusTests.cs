using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_connect_status</c>: reports a background connect (connecting / connected / failed), the last
/// outcome when none is in flight, and <c>NoConnectInProgress</c> when none has ever run.
/// Drives <see cref="ConnectState"/> with a fake connect job, so it runs in every build lane.
/// </summary>
public class ConnectStatusTests
{
    private const string SyncPayload =
        """{"status":"connected","gateway":"192.0.2.10","backend":"sdk","gated":true,"message":"ok"}""";

    private static readonly string[] AddedFields = ["connectId", "phase", "elapsedMs", "phaseTimings", "serverInstance"];

    private static JsonObject Status(ConnectState state) =>
        JsonNode.Parse(state.GetStatusJson())!.AsObject();

    /// <summary>Starts a connect that blocks until released, past a zero budget.</summary>
    private static async Task<(string ConnectId, TaskCompletionSource Release)> StartBlocked(
        ConnectState state, Func<IProgress<ConnectProgress>, Task<string>> afterRelease)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = await state.RunAsync(async progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.Upload));
            await release.Task;
            return await afterRelease(progress);
        }, TimeSpan.Zero);
        var connectId = JsonNode.Parse(response)!["connectId"]!.GetValue<string>();
        return (connectId, release);
    }

    /// <summary>While running the status is 'connecting' with the same connectId; afterwards it is 'connected' with the sync path's payload.</summary>
    [Fact]
    public async Task ConnectStatus_AfterConnecting_ReachesConnected_WithSyncPayload()
    {
        var state = new ConnectState();
        var (connectId, release) = await StartBlocked(state, progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.Parse));
            return Task.FromResult(SyncPayload);
        });

        var running = Status(state);
        Assert.Equal("connecting", running["status"]!.GetValue<string>());
        Assert.Equal(connectId, running["connectId"]!.GetValue<string>());
        Assert.True(running["elapsedMs"]!.GetValue<long>() >= 0);
        Assert.NotNull(running["phaseTimings"]);

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var done = Status(state);
        Assert.Equal("connected", done["status"]!.GetValue<string>());
        Assert.Equal(connectId, done["connectId"]!.GetValue<string>());
        Assert.Equal(ConnectPhases.Parse, done["phase"]!.GetValue<string>());
        foreach (var f in AddedFields) done.Remove(f);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SyncPayload), done),
            $"payload differs from the sync path: {done.ToJsonString()}");
    }

    /// <summary>phaseTimings lists each phase reached, in order, with its start and duration.</summary>
    [Fact]
    public async Task ConnectStatus_ReportsPhaseTimings_InOrder()
    {
        var state = new ConnectState();
        var (_, release) = await StartBlocked(state, async progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
            await Task.Delay(20);
            return SyncPayload;
        });
        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var timings = Status(state)["phaseTimings"]!.AsArray();
        Assert.Equal(new[] { ConnectPhases.Upload, ConnectPhases.SaveL5x },
            timings.Select(t => t!["phase"]!.GetValue<string>()).ToArray());
        Assert.All(timings, t => Assert.True(t!["durationMs"]!.GetValue<long>() >= 0));
        Assert.True(timings[0]!["startedMs"]!.GetValue<long>() <= timings[1]!["startedMs"]!.GetValue<long>());
    }

    /// <summary>A connect that returns a typed error payload surfaces that error unchanged as 'failed'.</summary>
    [Fact]
    public async Task ConnectStatus_FailedConnect_SurfacesOriginalTypedError()
    {
        const string typed = """{"error":"Connection failed: gateway unreachable","kind":"ConnectionFailed","hint":"Check the IP."}""";
        var state = new ConnectState();
        var (connectId, release) = await StartBlocked(state, _ => Task.FromResult(typed));
        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var s = Status(state);
        Assert.Equal("failed", s["status"]!.GetValue<string>());
        Assert.Equal(connectId, s["connectId"]!.GetValue<string>());
        Assert.Equal("Connection failed: gateway unreachable", s["error"]!.GetValue<string>());
        Assert.Equal("ConnectionFailed", s["kind"]!.GetValue<string>());
        Assert.Equal("Check the IP.", s["hint"]!.GetValue<string>());
    }

    /// <summary>A connect that throws surfaces the typed error the tool filter makes of the exception.</summary>
    [Fact]
    public async Task ConnectStatus_ThrownException_SurfacesTypedInternalError()
    {
        var state = new ConnectState();
        var (_, release) = await StartBlocked(state,
            _ => throw new InvalidOperationException("fake SDK connect failed"));
        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var s = Status(state);
        Assert.Equal("failed", s["status"]!.GetValue<string>());
        Assert.Equal(ErrorKinds.InternalError, s["kind"]!.GetValue<string>());
        Assert.Equal("InvalidOperationException", s["exceptionType"]!.GetValue<string>());
        Assert.Contains("fake SDK connect failed", s["error"]!.GetValue<string>());
    }

    /// <summary>With no connect in flight the last outcome stays readable, repeatedly.</summary>
    [Fact]
    public async Task ConnectStatus_NoneInFlight_ReturnsLastOutcome()
    {
        var state = new ConnectState();
        var response = await state.RunAsync(_ => Task.FromResult(SyncPayload), TimeSpan.FromSeconds(10));
        Assert.Equal(SyncPayload, response);

        var first  = state.GetStatusJson();
        var second = state.GetStatusJson();
        Assert.Equal(first, second);
        Assert.Equal("connected", JsonNode.Parse(first)!["status"]!.GetValue<string>());
        Assert.Null(state.Current);
    }

    /// <summary>With nothing in flight and nothing ever run the answer is the NoConnectInProgress error.</summary>
    [Fact]
    public void ConnectStatus_NothingEverRan_ReturnsNoConnectInProgress()
    {
        var s = Status(new ConnectState());
        Assert.Equal("NoConnectInProgress", s["kind"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(s["error"]!.GetValue<string>()));
        Assert.Null(s["status"]);
    }

    /// <summary>The tool is registered, named logix_connect_status, takes no arguments and is flagged read-only.</summary>
    [Fact]
    public void ConnectStatus_IsRegisteredAsReadOnlyTool()
    {
        var method = typeof(LogixTagTools).GetMethods()
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "logix_connect_status");
        var attr = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.True(attr.ReadOnly);
        Assert.Empty(method.GetParameters());
        Assert.Equal(ConnectState.PollTool, attr.Name);
    }

    /// <summary>The tool method returns the same JSON as <see cref="ConnectState.GetStatusJson"/> and starts nothing.</summary>
    [Fact]
    public void ConnectStatus_Tool_ReportsSharedState_AndStartsNothing()
    {
        var state = new ConnectState();
        var session = new TagSessionService();
        var tools = new LogixTagTools(
            new ProjectService(), session, new ControllerModeService(), connectState: state);

        // The status payload plus the session's `connected` flag (G33).
        Assert.Equal(SessionLostSupport.AnnotateStatus(session, state.GetStatusJson()), tools.ConnectStatus());
        Assert.Null(state.Current);
        Assert.Null(state.LastOutcome);
    }
}
