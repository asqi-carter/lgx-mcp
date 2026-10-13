using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

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
