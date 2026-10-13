using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LogixMcp.Tests;

/// <summary>
/// G36: a session call during this process's own connect/reconnect is ConnectInProgress, never SessionLost; each
/// SessionLost cause is chosen only from its evidence and the message text matches; connect_status reports
/// connected:false while connecting. These paths never read or write rung state codes in the session image
/// (er / rN / N / e / dN), so no test here has one to exercise; they drive connect state, pids, SCM and lease evidence.
/// </summary>
public class SessionLostCauseTests
{
    private const int OwnPid = 4000, OtherPid = 5000;
    private const string ProjectNotOpen = "Project is not open.";

    /// <summary>Fake host: mutable SDK server pids, SCM state and lease owner.</summary>
    private sealed class FakeHost
    {
        public int[] ServerPids = [111];
        public bool ServiceStopped;
        public UploadLeaseOwner? Lease;

        public SessionLostEvidence Evidence() =>
            new(() => ServerPids, () => ServiceStopped, () => Lease, OwnPid);
    }

    private static (TagSessionService Session, LogixTagTools Tools, ConnectState State, FakeHost Host) Connected()
    {
        var host    = new FakeHost();
        var session = new TagSessionService { LostEvidence = host.Evidence() };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
        var state = new ConnectState();
        var tools = new LogixTagTools(new ProjectService(), session, new ControllerModeService(), connectState: state);
        return (session, tools, state, host);
    }

    private static JsonObject Payload(CallToolResult r) =>
        (JsonObject)JsonNode.Parse(((TextContentBlock)r.Content[0]).Text)!;

    private static ValueTask<CallToolResult> Closed(TagSessionService session, ConnectState state) =>
        ToolErrorFilter.InvokeAsync("logix_read",
            () => throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, ProjectNotOpen)),
            connectState: state, tagSession: session);

    private static async Task<TaskCompletionSource> StartReconnect(ConnectState state)
    {
        var release  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.RunAsync(async progress =>
        {
            progress.Report(ConnectPhases.For(ConnectPhases.OpenProject));
            reported.SetResult();
            await release.Task;
            return "{}";
        }, TimeSpan.Zero, argsKey: "a");
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return release;
    }

    [Fact]
    public async Task OwnReconnectInFlight_EverySessionCall_ReturnsConnectInProgress_NeverSessionLost()
    {
        var (session, _, state, host) = Connected();
        // Even with evidence that would otherwise name a cause, an own reconnect wins.
        host.Lease = new UploadLeaseOwner(OtherPid, "t", DateTimeOffset.UtcNow);
        host.ServerPids = [222];
        var release = await StartReconnect(state);
        var connectId = JsonNode.Parse(state.GetStatusJson())!["connectId"]!.GetValue<string>();

        // Thrown "Project is not open.", the payload forms, and NoActiveSession on a session already lost.
        var thrown = await Closed(session, state);
        var asPayload = await ToolErrorFilter.InvokeAsync("logix_pending_edits",
            () => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"error\":\"Read failed: Project is not open.\",\"kind\":\"ConnectionFailed\"}" }],
            }), connectState: state, tagSession: session);
        var noSession = await ToolErrorFilter.InvokeAsync("logix_read",
            () => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "{\"error\":\"No active session.\",\"kind\":\"NoActiveSession\"}" }],
            }), connectState: state, tagSession: session);

        foreach (var r in new[] { thrown, asPayload, noSession })
        {
            var p = Payload(r);
            Assert.True(r.IsError);
            Assert.Equal(ErrorKinds.ConnectInProgress, (string)p["kind"]!);
            Assert.Equal(connectId, (string)p["connectId"]!);
            Assert.Equal(ConnectState.PollTool, (string)p["pollWith"]!);
            Assert.Contains(ConnectState.PollTool, (string)p["hint"]!);
        }
        Assert.Null(session.SessionLost);

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ConnectStatus_DuringReconnect_ReportsConnectedFalse_Connecting_ThenConnectedTrueAfterCompletion()
    {
        var (session, tools, state, _) = Connected();
        Assert.True(session.IsConnected);
        var release = await StartReconnect(state);

        var during = JsonNode.Parse(tools.GetConnectStatusResponse())!;
        Assert.False((bool)during["connected"]!);
        Assert.Equal("connecting", (string)during["status"]!);

        release.SetResult();
        await state.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var after = JsonNode.Parse(tools.GetConnectStatusResponse())!;
        Assert.True((bool)after["connected"]!);
        Assert.Equal("connected", (string)after["status"]!);
    }

    [Fact]
    public async Task Cause_SdkServerPidChanged_IsSdkServiceRestarted_WithMatchingText()
    {
        var (session, _, state, host) = Connected();
        host.ServerPids = [222];                       // LdSdkServer 111 at connect, 222 now
        host.Lease = new UploadLeaseOwner(OtherPid, "t", DateTimeOffset.UtcNow); // weaker evidence; restart wins

        var p = Payload(await Closed(session, state));
        Assert.Equal(ErrorKinds.SessionLost, (string)p["kind"]!);
        Assert.Equal(SessionLostCauses.SdkServiceRestarted, (string)p["cause"]!);
        var text = (string)p["error"]!;
        Assert.Contains("LdSdkService) was restarted", text);
        Assert.Contains("111", text);
        Assert.DoesNotContain("another server process", text);
    }

    [Fact]
    public async Task Cause_ScmStop_IsSdkServiceRestarted()
    {
        var (session, _, state, host) = Connected();
        host.ServiceStopped = true;

        var p = Payload(await Closed(session, state));
        Assert.Equal(SessionLostCauses.SdkServiceRestarted, (string)p["cause"]!);
        Assert.Contains("service control manager", (string)p["error"]!);
    }

    [Fact]
    public async Task Cause_LeaseHeldByAnotherPid_IsOtherProcessReconnect_WithMatchingText()
    {
        var (session, _, state, host) = Connected();
        host.Lease = new UploadLeaseOwner(OtherPid, "t", DateTimeOffset.UtcNow);

        var p = Payload(await Closed(session, state));
        Assert.Equal(SessionLostCauses.OtherProcessReconnect, (string)p["cause"]!);
        var text = (string)p["error"]!;
        Assert.Contains("another server process", text);
        Assert.Contains(OtherPid.ToString(), text);
        Assert.DoesNotContain("was restarted", text);
    }

    [Fact]
    public async Task Cause_LeaseHeldByThisProcess_IsNotOtherProcessReconnect()
    {
        var (session, _, state, host) = Connected();
        host.Lease = new UploadLeaseOwner(OwnPid, "t", DateTimeOffset.UtcNow);

        var p = Payload(await Closed(session, state));
        Assert.Equal(SessionLostCauses.ProjectClosedUnknown, (string)p["cause"]!);
    }

    [Fact]
    public async Task Cause_NoEvidence_IsProjectClosedUnknown_AndTextBlamesNobody()
    {
        var (session, _, state, _) = Connected();

        var p = Payload(await Closed(session, state));
        Assert.Equal(SessionLostCauses.ProjectClosedUnknown, (string)p["cause"]!);
        var text = (string)p["error"]!;
        Assert.Contains("nothing observed shows why", text);
        Assert.DoesNotContain("was restarted", text);
        Assert.DoesNotContain("another server process's", text);
        Assert.Equal(ProjectNotOpen, (string)p["observed"]!);
    }

    [Fact]
    public void Cause_NoServerPidRecordedAtConnect_GivesNoRestartEvidence()
    {
        var host = new FakeHost { ServerPids = [] };
        var session = new TagSessionService { LostEvidence = host.Evidence() };
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true);
        host.ServerPids = [999];   // a server appeared; nothing was known at connect

        Assert.Equal(SessionLostCauses.ProjectClosedUnknown, session.MarkSessionLost(ProjectNotOpen)!.Cause);
    }
}
