using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// While a background connect is in flight: session-requiring tools answer <c>ConnectInProgress</c> (not
/// <c>NoActiveSession</c>), a repeat connect with the same arguments returns the in-flight status, and a
/// connect with different arguments is refused. No second connect ever starts.
/// </summary>
public class ConnectInProgressTests
{
    private const string NoSession =
        """{"error":"No active session. Call logix_connect first.","kind":"NoActiveSession"}""";

    private static async Task<(ConnectState State, TaskCompletionSource Release, Func<int> Starts)> StartBlocked(
        string argsKey = "a")
    {
        var state   = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts  = 0;
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.RunAsync(async progress =>
        {
            Interlocked.Increment(ref starts);
            progress.Report(ConnectPhases.For(ConnectPhases.Upload));
            reported.SetResult();
            await release.Task;
            return "{}";
        }, TimeSpan.Zero, argsKey: argsKey);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (state, release, () => Volatile.Read(ref starts));
    }

    private static CallToolResult Text(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    /// <summary>A file-based SDK connect reports its real phase to logix_connect_status, not "starting".</summary>
    [Fact]
    public async Task FileBasedConnect_ReportsOpenProjectPhase_NotStarting()
    {
        var state   = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.RunAsync(async progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.OpenProject));
            reported.SetResult();
            await release.Task;
            return "{}";
        }, TimeSpan.Zero, argsKey: "file");
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var o = JsonNode.Parse(state.GetStatusJson())!.AsObject();
        Assert.Equal("connecting", o["status"]!.GetValue<string>());
        Assert.Equal(ConnectPhases.OpenProject, o["phase"]!.GetValue<string>());
        Assert.NotEqual(ConnectState.StartingPhase, o["phase"]!.GetValue<string>());

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A session-requiring tool called during a connect returns ConnectInProgress with phase and elapsedMs.</summary>
    [Fact]
    public async Task OtherTool_DuringConnect_ReturnsConnectInProgress_NotNoActiveSession()
    {
        var (state, release, _) = await StartBlocked();
        var r = await ToolErrorFilter.InvokeAsync(
            "logix_read", () => new ValueTask<CallToolResult>(Text(NoSession)), connectState: state);

        Assert.True(r.IsError);
        var o = JsonNode.Parse(((TextContentBlock)r.Content[0]).Text)!.AsObject();
        Assert.Equal(ErrorKinds.ConnectInProgress, o["kind"]!.GetValue<string>());
        Assert.Equal(ConnectPhases.Upload, o["phase"]!.GetValue<string>());
        Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);
        Assert.False(string.IsNullOrEmpty(o["connectId"]!.GetValue<string>()));

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // Connect finished: NoActiveSession is reported unchanged again.
        var after = await ToolErrorFilter.InvokeAsync(
            "logix_read", () => new ValueTask<CallToolResult>(Text(NoSession)), connectState: state);
        Assert.Equal(ErrorKinds.NoActiveSession,
            JsonNode.Parse(((TextContentBlock)after.Content[0]).Text)!["kind"]!.GetValue<string>());
    }

    /// <summary>Other errors and successful results are not touched during a connect.</summary>
    [Fact]
    public async Task OtherTool_DuringConnect_LeavesUnrelatedResultsAlone()
    {
        var (state, release, _) = await StartBlocked();
        const string other = """{"error":"nope","kind":"InvalidValue"}""";
        var r = await ToolErrorFilter.InvokeAsync(
            "logix_write", () => new ValueTask<CallToolResult>(Text(other)), connectState: state);
        Assert.Equal(other, ((TextContentBlock)r.Content[0]).Text);
        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A second connect with the same arguments returns the in-flight status and starts no second connect.</summary>
    [Fact]
    public async Task DuplicateConnect_SameArguments_ReturnsInFlightStatus_StartsNoSecondConnect()
    {
        var (state, release, starts) = await StartBlocked("a");
        var first = JsonNode.Parse(state.GetStatusJson())!["connectId"]!.GetValue<string>();

        var second = await state.RunAsync(_ => Task.FromResult("{}"), TimeSpan.Zero, argsKey: "a");
        var o = JsonNode.Parse(second)!.AsObject();
        Assert.Equal("connecting", o["status"]!.GetValue<string>());
        Assert.Equal(first, o["connectId"]!.GetValue<string>());
        Assert.Equal(1, starts());

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, starts());
    }

    /// <summary>A connect with different arguments during a connect is refused with ConnectInProgress.</summary>
    [Fact]
    public async Task DifferentArgumentsConnect_DuringConnect_IsRefusedWithConnectInProgress()
    {
        var (state, release, starts) = await StartBlocked("a");
        var ran = false;

        var refused = await state.RunAsync(_ => { ran = true; return Task.FromResult("{}"); },
            TimeSpan.Zero, argsKey: "b");
        var o = JsonNode.Parse(refused)!.AsObject();
        Assert.Equal(ErrorKinds.ConnectInProgress, o["kind"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(o["error"]!.GetValue<string>()));
        Assert.Equal(ConnectPhases.Upload, o["phase"]!.GetValue<string>());
        Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);
        Assert.False(ran);
        Assert.Equal(1, starts());

        // The public Guard used by non-budgeted connects behaves the same way.
        Assert.Equal(ErrorKinds.ConnectInProgress,
            JsonNode.Parse(state.Guard(ConnectState.ArgsKeyFor("x", "y")) ?? "{}")!["kind"]?.GetValue<string>());

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(state.Guard("b"));
    }
}

/// <summary>
/// The connect handler marks its own connect as started in <see cref="ConnectState"/> before any
/// pre-work (here the outgoing-session disposal), so a status poll in that window answers connecting/starting and never
/// ConnectOwnedByOtherInstance, even with a dead instance's finished connect in the registry.
/// </summary>
[Collection("StaticHandlerHooks")]
public class OwnConnectVisibilityTests
{
    [Fact]
    public async Task StatusPollIssuedBeforeThePreWorkFinishesAnswersConnectingStarting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-own-connect-" + Guid.NewGuid().ToString("N"));
        var saved = CloseProjectHandler.SdkSessionInspector;
        var inPreWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new ManualResetEventSlim(false);
        try
        {
            const int deadPid = 52001, ownPid = 52002;
            var start = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
            new ConnectRegistry(dir, deadPid, start, _ => true).Write(new ConnectRegistryEntry(
                "deadconnect", deadPid, start, "1,0", "upload", 10, ConnectRegistry.StatusFailed, DateTimeOffset.UtcNow)
                { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5) });
            var state = new ConnectState(new ConnectRegistry(dir, ownPid, start, p => p == ownPid));

            CloseProjectHandler.SdkSessionInspector = _ =>
            {
                inPreWork.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(20));
                return null;
            };

            var run = Task.Run(() => ConnectHandler.ExecuteAsync(
                "", "", useVirtual: true, new ProjectService(), new TagSessionService(), "cip",
                connectState: state));
            await inPreWork.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var o = JsonNode.Parse(state.GetStatusJson())!.AsObject();
            Assert.Equal("connecting", o["status"]!.GetValue<string>());
            Assert.Equal(ConnectState.StartingPhase, o["phase"]!.GetValue<string>());
            Assert.NotEqual(ErrorKinds.ConnectOwnedByOtherInstance, o["kind"]?.GetValue<string>());
            Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);

            release.Set();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            var error = Assert.IsType<ConnectError>(result);
            Assert.Equal(ErrorKinds.NoProjectLoaded, error.Kind);
            Assert.Null(state.Current);   // the mark is cleared when the connect returns
        }
        finally
        {
            release.Set();
            CloseProjectHandler.SdkSessionInspector = saved;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OwnConnectMark_IsANoOpInsideRunAsync()
    {
        var state = new ConnectState();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? innerId = null;
        var response = await state.RunAsync(async _ =>
        {
            using (state.BeginOwnConnect()) innerId = state.Current?.ConnectId;
            entered.SetResult();
            await release.Task;
            return "{}";
        }, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The inner mark did not replace or clear the RunAsync connect.
        Assert.Equal(JsonNode.Parse(response)!["connectId"]!.GetValue<string>(), innerId);
        Assert.Equal(innerId, state.Current?.ConnectId);
        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static RequestContext<CallToolRequestParams> NoServerContext() =>
        (RequestContext<CallToolRequestParams>)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));

    /// <summary>
    /// The logix_connect tool marks the connect as its first statement. An id-less status poll issued
    /// while the tool is blocked before its first await answers connecting/starting with no connectId; once released
    /// the connect finishes under the same start time and a connectId, and a finished status follows.
    /// </summary>
    [Fact]
    public async Task ConnectTool_MarksOwnConnectBeforeItsFirstAwait()
    {
        var saved = CloseProjectHandler.SdkSessionInspector;
        var inPreWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new ManualResetEventSlim(false);
        try
        {
            var state = new ConnectState();
            var tools = new LogixTagTools(new ProjectService(), new TagSessionService(), new ControllerModeService(),
                connectState: state);
            CloseProjectHandler.SdkSessionInspector = _ =>
            {
                inPreWork.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(20));
                return null;
            };

            var run = Task.Run(() => tools.Connect(NoServerContext(), useVirtual: true));
            await inPreWork.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var o = JsonNode.Parse(tools.GetConnectStatusResponse())!.AsObject();
            Assert.Equal("connecting", o["status"]!.GetValue<string>());
            Assert.Equal(ConnectState.StartingPhase, o["phase"]!.GetValue<string>());
            Assert.True(o["elapsedMs"]!.GetValue<long>() >= 0);
            Assert.Null(o["connectId"]);
            Assert.NotEqual(ErrorKinds.NoConnectInProgress, o["kind"]?.GetValue<string>());

            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(state.Current);   // a mark nothing adopted is cleared when the tool returns
            Assert.NotEqual("connecting",
                JsonNode.Parse(tools.GetConnectStatusResponse())!["status"]?.GetValue<string>());
        }
        finally
        {
            release.Set();
            CloseProjectHandler.SdkSessionInspector = saved;
        }
    }

    /// <summary>
    /// A budgeted (sdk) connect through the tool: RunAsync adopts the tool-entry mark, so the connect that finishes has
    /// a connectId and the start time taken when the tool was entered (one registry entry, no second start).
    /// </summary>
    [Fact]
    public async Task ConnectTool_BudgetedConnect_AdoptsTheMark_SameStartedAtAndConnectId()
    {
        var saved = CloseProjectHandler.SdkSessionInspector;
        var dir = Path.Combine(Path.GetTempPath(), "lgx-tool-mark-" + Guid.NewGuid().ToString("N"));
        var inPreWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new ManualResetEventSlim(false);
        try
        {
            var t0 = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
            var ticks = -1;
            // Every reading of the clock is a later instant, so a start time taken twice would differ.
            var registry = new ConnectRegistry(dir, 54001, t0, p => p == 54001,
                clock: () => t0.AddSeconds(Interlocked.Increment(ref ticks)));
            var state = new ConnectState(registry);
            var tools = new LogixTagTools(new ProjectService(), new TagSessionService(), new ControllerModeService(),
                connectState: state);
            CloseProjectHandler.SdkSessionInspector = _ =>
            {
                inPreWork.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(20));
                return null;
            };

            var run = Task.Run(() => tools.Connect(NoServerContext(), backend: "sdk", path: "1,0"));
            await inPreWork.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var o = JsonNode.Parse(tools.GetConnectStatusResponse())!.AsObject();
            Assert.Equal("connecting", o["status"]!.GetValue<string>());
            var connectId = o["connectId"]!.GetValue<string>();   // adopted: the id is stable from here on

            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

            var done = JsonNode.Parse(tools.GetConnectStatusResponse())!.AsObject();
            Assert.NotEqual("connecting", done["status"]!.GetValue<string>());
            Assert.Equal(connectId, done["connectId"]!.GetValue<string>());
            Assert.Equal(t0, registry.Read(connectId)!.StartedAt);   // the mark's start time, not a second one
            Assert.Single(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            release.Set();
            CloseProjectHandler.SdkSessionInspector = saved;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A second logix_connect arriving while the first is still in its pre-work gets the in-flight status for the same
    /// arguments and is refused for different ones; neither starts a second connect.
    /// </summary>
    [Fact]
    public async Task ConnectTool_DuringProvisionalMark_DuplicateAnswersInFlight_DifferentIsRefused()
    {
        var saved = CloseProjectHandler.SdkSessionInspector;
        var inPreWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new ManualResetEventSlim(false);
        var inspections = 0;
        try
        {
            var state = new ConnectState();
            var tools = new LogixTagTools(new ProjectService(), new TagSessionService(), new ControllerModeService(),
                connectState: state);
            CloseProjectHandler.SdkSessionInspector = _ =>
            {
                Interlocked.Increment(ref inspections);
                inPreWork.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(20));
                return null;
            };

            var first = Task.Run(() => tools.Connect(NoServerContext(), useVirtual: true, path: "1,0"));
            await inPreWork.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(state.Current!.Provisional);

            var same = JsonNode.Parse(await tools.Connect(NoServerContext(), useVirtual: true, path: "1,0"))!.AsObject();
            Assert.Equal("connecting", same["status"]!.GetValue<string>());

            var other = JsonNode.Parse(await tools.Connect(NoServerContext(), useVirtual: true, path: "2,0"))!.AsObject();
            Assert.Equal(ErrorKinds.ConnectInProgress, other["kind"]!.GetValue<string>());

            Assert.Equal(1, inspections);
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            CloseProjectHandler.SdkSessionInspector = saved;
        }
    }

    /// <summary>A provisional mark whose tool call throws before the handler is cleared by its scope.</summary>
    [Fact]
    public void ProvisionalMark_IsClearedWhenTheScopeIsDisposed()
    {
        var state = new ConnectState();
        try
        {
            using var mark = state.BeginOwnConnect("1,0");
            Assert.NotNull(state.Current);
            Assert.True(state.Current!.Provisional);
            throw new InvalidOperationException("tool call failed before the handler");
        }
        catch (InvalidOperationException) { }
        Assert.Null(state.Current);
        Assert.Null(state.Guard("a"));
    }

    /// <summary>RunAsync adopts the provisional mark: one id and start time across the budget return and the registry.</summary>
    [Fact]
    public async Task RunAsync_AdoptsTheProvisionalMark_KeepingIdAndStartedAt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-adopt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
            var registry = new ConnectRegistry(dir, 53001, start, p => p == 53001);
            var state = new ConnectState(registry);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var mark = state.BeginOwnConnect("1,0");
            var provisionalId = state.Current!.ConnectId;
            Assert.Null(state.Guard("a"));   // a provisional mark lets a call through

            var response = await state.RunAsync(async _ => { await release.Task; return "{}"; },
                TimeSpan.Zero, argsKey: "a");
            var o = JsonNode.Parse(response)!.AsObject();
            Assert.Equal(provisionalId, o["connectId"]!.GetValue<string>());
            Assert.False(state.Current!.Provisional);
            Assert.Equal(provisionalId, state.Current.ConnectId);
            Assert.Single(Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains(provisionalId)));

            // Once adopted, the scope is a no-op and duplicates/different arguments are guarded as before.
            mark.Dispose();
            Assert.Equal(provisionalId, state.Current?.ConnectId);
            Assert.Equal("connecting", JsonNode.Parse(state.Guard("a")!)!["status"]!.GetValue<string>());
            Assert.Equal(ErrorKinds.ConnectInProgress, JsonNode.Parse(state.Guard("b")!)!["kind"]!.GetValue<string>());

            release.SetResult();
            await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var done = JsonNode.Parse(state.GetStatusJson(provisionalId))!.AsObject();
            Assert.Equal("connected", done["status"]!.GetValue<string>());
            Assert.Equal(provisionalId, done["connectId"]!.GetValue<string>());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>With every argument defaulted the raw-argument key equals the tool's key of the defaults.</summary>
    [Fact]
    public void ArgsKeyFromArguments_Defaults_EqualsConnectArgsKeyOfDefaults()
    {
        var expected = LogixTagTools.ConnectArgsKey("", "", false, "cip", "offline", false, null, false);
        Assert.Equal(expected, ConnectState.ArgsKeyFromArguments(null));
        Assert.Equal(expected, ConnectState.ArgsKeyFromArguments(new Dictionary<string, System.Text.Json.JsonElement>()));
    }

    /// <summary>Explicit sdk arguments map to the same key as the tool builds from them.</summary>
    [Fact]
    public void ArgsKeyFromArguments_SdkArguments_EqualsConnectArgsKey()
    {
        var args = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["backend"] = System.Text.Json.JsonDocument.Parse("\"sdk\"").RootElement.Clone(),
            ["sdkMode"] = System.Text.Json.JsonDocument.Parse("\"online\"").RootElement.Clone(),
            ["sdkCommPath"] = System.Text.Json.JsonDocument.Parse("\"X\"").RootElement.Clone(),
        };
        Assert.Equal(
            LogixTagTools.ConnectArgsKey("", "", false, "sdk", "online", false, "X", false),
            ConnectState.ArgsKeyFromArguments(args));
    }

    /// <summary>The ambient scope is only offered while this state's filter mark is still provisional.</summary>
    [Fact]
    public void AmbientOwnConnect_IsOfferedWhileProvisional_AndClearedOnDispose()
    {
        var state = new ConnectState();
        Assert.Null(state.AmbientOwnConnect());
        var scope = state.BeginOwnConnect("1,0");
        Assert.Same(scope, state.AmbientOwnConnect());
        Assert.Null(new ConnectState().AmbientOwnConnect());
        scope.Dispose();
        Assert.Null(state.AmbientOwnConnect());
    }
}
