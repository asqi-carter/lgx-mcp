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

    /// <summary>
    /// While this instance's own connect is in flight, even before it reports a single phase, an id-less
    /// poll answers connecting/starting from its own state and never reports a dead instance's old finished connect.
    /// </summary>
    [Fact]
    public async Task OwnConnectBeforeAnyPhase_IdlessPollAnswersConnectingStarting_NotAForeignConnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-status-own-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int deadPid = 51001, ownPid = 51002;
            var start = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
            // A failed connect of an instance that has exited, written moments ago: the A-st-own entry.
            new ConnectRegistry(dir, deadPid, start, _ => true).Write(new ConnectRegistryEntry(
                "deadconnect", deadPid, start, "1,0", "upload", 10, ConnectRegistry.StatusFailed, DateTimeOffset.UtcNow)
                { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5) });
            var state = new ConnectState(new ConnectRegistry(dir, ownPid, start, p => p == ownPid));

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = await state.RunAsync(async _ =>
            {
                entered.SetResult();   // the handler is in its pre-work: no phase has been reported
                await release.Task;
                return SyncPayload;
            }, TimeSpan.Zero);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var o = Status(state);
            Assert.Equal("connecting", o["status"]!.GetValue<string>());
            Assert.Equal(ConnectState.StartingPhase, o["phase"]!.GetValue<string>());
            Assert.Equal(JsonNode.Parse(response)!["connectId"]!.GetValue<string>(), o["connectId"]!.GetValue<string>());
            Assert.NotEqual(ErrorKinds.ConnectOwnedByOtherInstance, o["kind"]?.GetValue<string>());
            Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);

            release.SetResult();
            await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A provisional own mark (set at tool entry, before any job) answers an id-less poll as
    /// connecting/starting with elapsedMs from the mark's clock and no connectId, never NoConnectInProgress and never
    /// the registry's finished connect of another instance.
    /// </summary>
    [Fact]
    public void ProvisionalOwnMark_IdlessPoll_AnswersConnectingStarting_WithoutConnectId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-status-prov-" + Guid.NewGuid().ToString("N"));
        try
        {
            const int deadPid = 52001, ownPid = 52002;
            var start = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
            new ConnectRegistry(dir, deadPid, start, _ => true).Write(new ConnectRegistryEntry(
                "deadconnect", deadPid, start, "1,0", "upload", 10, ConnectRegistry.StatusFailed, DateTimeOffset.UtcNow)
                { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5) });
            var state = new ConnectState(new ConnectRegistry(dir, ownPid, start, p => p == ownPid));

            using (state.BeginOwnConnect("1,0"))
            {
                var o = Status(state);
                Assert.Equal("connecting", o["status"]!.GetValue<string>());
                Assert.Equal(ConnectState.StartingPhase, o["phase"]!.GetValue<string>());
                Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);
                Assert.Null(o["connectId"]);
                Assert.False(string.IsNullOrEmpty(o["hint"]?.GetValue<string>()));
                Assert.NotEqual(ErrorKinds.NoConnectInProgress, o["kind"]?.GetValue<string>());
                Assert.NotEqual(ErrorKinds.ConnectOwnedByOtherInstance, o["kind"]?.GetValue<string>());
            }

            // Disposed without being adopted: the mark is gone, so the poll is back to the registry's view.
            Assert.Null(state.Current);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
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
    public async Task ConnectStatus_Tool_ReportsSharedState_AndStartsNothing()
    {
        var state = new ConnectState();
        var session = new TagSessionService();
        var tools = new LogixTagTools(
            new ProjectService(), session, new ControllerModeService(), connectState: state);

        // The status payload plus the session's `connected` flag (G33).
        Assert.Equal(SessionLostSupport.AnnotateStatus(session, state.GetStatusJson()), await tools.ConnectStatus());
        Assert.Null(state.Current);
        Assert.Null(state.LastOutcome);
    }

    private static async Task WithSettle(TimeSpan settle, Func<Task> body)
    {
        var saved = ConnectState.NeverConnectedSettle;
        ConnectState.NeverConnectedSettle = settle;
        try { await body(); }
        finally { ConnectState.NeverConnectedSettle = saved; }
    }

    /// <summary>A poll that starts before the mark exists answers connecting once the mark appears within the settle.</summary>
    [Fact]
    public async Task StatusAsync_PollStartedBeforeBeginOwnConnect_AnswersConnecting()
    {
        await WithSettle(TimeSpan.FromMilliseconds(50), async () =>
        {
            var state = new ConnectState();
            var poll = state.GetStatusJsonAsync();
            Assert.False(poll.IsCompleted);
            using var mark = state.BeginOwnConnect("1,0");
            var o = JsonNode.Parse(await poll.WaitAsync(TimeSpan.FromSeconds(10)))!.AsObject();
            Assert.Equal("connecting", o["status"]!.GetValue<string>());
            Assert.Null(o["connectId"]);
        });
    }

    /// <summary>With no mark and no outcome the poll answers NoConnectInProgress after about the settle, not before.</summary>
    [Fact]
    public async Task StatusAsync_NoMark_AnswersNoConnectInProgressAfterTheSettle()
    {
        await WithSettle(TimeSpan.FromMilliseconds(50), async () =>
        {
            var state = new ConnectState();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var o = JsonNode.Parse(await state.GetStatusJsonAsync())!.AsObject();
            sw.Stop();
            Assert.Equal("NoConnectInProgress", o["kind"]!.GetValue<string>());
            Assert.True(sw.ElapsedMilliseconds >= 45, $"answered after {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 5000, $"answered after {sw.ElapsedMilliseconds} ms");
        });
    }

    /// <summary>A last outcome is answered at once, without waiting for the settle.</summary>
    [Fact]
    public async Task StatusAsync_WithLastOutcome_AnswersAtOnce()
    {
        await WithSettle(TimeSpan.FromSeconds(30), async () =>
        {
            var state = new ConnectState();
            await state.RunAsync(_ => Task.FromResult(SyncPayload), TimeSpan.FromSeconds(10));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var o = JsonNode.Parse(await state.GetStatusJsonAsync())!.AsObject();
            Assert.Equal("connected", o["status"]!.GetValue<string>());
            Assert.True(sw.ElapsedMilliseconds < 5000, $"answered after {sw.ElapsedMilliseconds} ms");
        });
    }

    // ── phaseTimingsSummary on every connected payload ──────────────

    private static ConnectResult ConnectedWithTimings() => new(
        "connected", "192.0.2.10", "", true, false, "sdk", 3, true, "sdk-online", true, false, [], "upload-to-new-project", "ok")
    {
        PhaseTimings = new ConnectPhaseTimings(1200, 300, 40, 5, 7, 1552),
    };

    /// <summary>
    /// A connect that finishes inside the sync budget returns its own JSON; that JSON carries
    /// <c>phaseTimingsSummary</c> holding the same object as <c>phaseTimings</c>, as the status poll does.
    /// </summary>
    [Fact]
    public async Task SyncBudgetPayload_CarriesPhaseTimingsSummary_SameObjectAsPhaseTimings()
    {
        var state = new ConnectState();
        var json = JsonSerializer.Serialize(ConnectedWithTimings());

        var response = await state.RunAsync(_ => Task.FromResult(json), TimeSpan.FromSeconds(10));

        var o = JsonNode.Parse(response)!.AsObject();
        Assert.NotNull(o["phaseTimingsSummary"]);
        Assert.Equal(o["phaseTimings"]!.ToJsonString(), o["phaseTimingsSummary"]!.ToJsonString());
        Assert.Equal(1552, o["phaseTimingsSummary"]!["total"]!.GetValue<long>());
    }

    /// <summary>The status poll of the same connect carries the summary too, equal to the sync payload's.</summary>
    [Fact]
    public async Task StatusPoll_CarriesPhaseTimingsSummary_EqualToTheSyncPayloads()
    {
        var state = new ConnectState();
        var json = JsonSerializer.Serialize(ConnectedWithTimings());
        var sync = JsonNode.Parse(await state.RunAsync(_ => Task.FromResult(json), TimeSpan.FromSeconds(10)))!.AsObject();

        var poll = Status(state);

        Assert.Equal("connected", poll["status"]!.GetValue<string>());
        Assert.Equal(sync["phaseTimingsSummary"]!.ToJsonString(), poll["phaseTimingsSummary"]!.ToJsonString());
    }

    /// <summary>A result with no timings (a virtual connect) omits the summary rather than emitting null.</summary>
    [Fact]
    public void ConnectResult_WithoutTimings_OmitsPhaseTimingsSummary()
    {
        var o = JsonNode.Parse(JsonSerializer.Serialize(ConnectedWithTimings() with { PhaseTimings = null }))!.AsObject();

        Assert.False(o.ContainsKey("phaseTimingsSummary"));
        Assert.False(o.ContainsKey("phaseTimings"));
    }
}
