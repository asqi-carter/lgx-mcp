#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the shared <see cref="StudioLifecycleHandler"/>:
/// cancel wording is pinned byte-for-byte, the accept verb runs the same flow, and the
/// scheduled task is <c>logix-mcp-s1-edits</c> with the legacy <c>logix-mcp-s1-cancel</c>
/// task unregistered.
/// </summary>
public class StudioLifecycleHandlerTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

    private static RoutinePendingEditState State(string state) =>
        new(Program, Routine, 2,
            state == "none"
                ? Array.Empty<PendingEditRungEntry>()
                : new[] { new PendingEditRungEntry(0, "I", "OTE(Motor_Run);") },
            state);

    private static Func<string, string, CancellationToken, Task<object>> Reader(
        params RoutinePendingEditState[] sequence)
    {
        var i = 0;
        return (p, r, ct) =>
        {
            var s = sequence[Math.Min(i++, sequence.Length - 1)];
            return Task.FromResult<object>(new PendingEditsResult(
                true, "Run", null, new[] { s },
                new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));
        };
    }

    private static Func<string, CancellationToken, Task<Session1ScriptResult>> Runner(
        List<string> verbs, bool ok = true, string reason = "gesture_sent") =>
        (verb, ct) =>
        {
            verbs.Add(verb);
            return Task.FromResult(new Session1ScriptResult(ok, reason, new[] { "s.png" }));
        };

    [Fact]
    public async Task Cancel_WithNothingPending_ReturnsKindNothingPending()
    {
        var verbs = new List<string>();
        var none = (CancelPendingEditsError)await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, Reader(State("none")), Runner(verbs), () => true,
            planLaunch: () => ("direct", null));

        Assert.Equal("NothingPending", none.Kind);
        Assert.Equal(ErrorKinds.NothingPending, none.Kind);
        Assert.False(none.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task Cancel_NothingPendingOnController_ReturnsNothingPending_WithoutStudioDialogHint()
    {
        // The before-read shows pending edits, Studio shows no dialog (command_not_applicable), and a fresh
        // read finds nothing pending on the controller.
        var verbs = new List<string>();
        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, Reader(State("pending"), State("pending"), State("none")),
            Runner(verbs, ok: false, reason: "command_not_applicable"), () => true,
            planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(ErrorKinds.NothingPending, err.Kind);
        Assert.Null(err.Hint);
        Assert.DoesNotContain("dialog", err.Error + err.Hint, StringComparison.OrdinalIgnoreCase);
    }

    // ── cancel wording is byte-identical to the pre-0098 handler ─────────────

    [Fact]
    public async Task Cancel_Messages_AreByteIdenticalToPreRefactor()
    {
        var (noop, _) = (Runner(new List<string>()), 0);

        var confirm = (CancelPendingEditsError)await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, false, Reader(State("pending")), noop, () => true,
            planLaunch: () => ("direct", null));
        Assert.Equal(
            "logix_cancel_pending_edits refused for 'MainProgram'/'MainRoutine': cancelling " +
            "discards the controller's pending rung edits by driving Studio 5000's GUI " +
            "(Logic -> Online Edits -> Cancel Accepted Program Edits) in the interactive " +
            "desktop session — this is desktop automation, not a reviewable SDK call. " +
            "Pass confirm=true to acknowledge this risk and proceed.",
            confirm.Error);

        var none = (CancelPendingEditsError)await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, Reader(State("none")), noop, () => true,
            planLaunch: () => ("direct", null));
        Assert.Equal(
            "logix_cancel_pending_edits refused for 'MainProgram'/'MainRoutine': " +
            "logix_pending_edits reports state='none' — there is nothing pending to cancel.",
            none.Error);

        var ok = (CancelPendingEditsResult)await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, Reader(State("pending"), State("none")), noop, () => true);
        Assert.Equal("cancelled", ok.Status);
        Assert.Equal(
            "Cancelled pending edits for 'MainProgram'/'MainRoutine' by driving the Studio " +
            "5000 GUI (Logic -> Online Edits -> Cancel Accepted Program Edits) in the " +
            "interactive desktop session via the session-1 script runner (was " +
            "state='pending', now state='none').",
            ok.Message);

        var failed = (CancelPendingEditsError)await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, Reader(State("pending")),
            Runner(new List<string>(), ok: false, reason: "boom"), () => true);
        Assert.Equal(
            "bench/studio-edits.ps1 -Verb cancel reported failure for " +
            "'MainProgram'/'MainRoutine': boom",
            failed.Error);
    }

    // An accept-shaped verb built here (the production Accept instance ships with the
    // accept tool): proves the flow is verb-parameterised, not cancel-hardwired.
    private static readonly StudioLifecycleVerb AcceptLike = new(
        Verb: "accept", ToolName: "accept_tool", Status: "accepted",
        GestureMenu: "Logic -> Online Edits -> Assemble Accepted Program Edits",
        CapitalVerb: "Accept", Gerund: "accepting", PastCapital: "Accepted",
        Consequence: "accepting assembles the pending edits", RequireAfterNone: true,
        Audit: (_, _, _, _, _) => { });

    // ── a second verb runs the same flow ────────────────────────────────────────────

    [Fact]
    public async Task Accept_InvokesRunnerWithVerbAccept_AndReturnsAcceptedWhenAfterNone()
    {
        var verbs = new List<string>();
        var result = await StudioLifecycleHandler.ExecuteAsync(
            AcceptLike, Program, Routine, true,
            Reader(State("accepted"), State("none")), Runner(verbs), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal(new[] { "accept" }, verbs);
        Assert.Equal("accepted", ok.Status);
        Assert.Contains("Assemble Accepted Program Edits", ok.Message);
    }

    [Fact]
    public async Task Accept_GestureOkButStillPending_IsErrorNamingBothStates()
    {
        var result = await StudioLifecycleHandler.ExecuteAsync(
            AcceptLike, Program, Routine, true,
            Reader(State("accepted"), State("accepted")), Runner(new List<string>()), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("state='accepted'", err.Error);
        Assert.Contains("accept_tool", AcceptLike.ToolName);
    }

    [Fact]
    public async Task Accept_NothingPending_RefusedBeforeLaunch()
    {
        var verbs = new List<string>();
        var result = await StudioLifecycleHandler.ExecuteAsync(
            AcceptLike, Program, Routine, true,
            Reader(State("none")), Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
        Assert.StartsWith("accept_tool refused", err.Error);
    }

    // ── task naming ──────────────────────────────────────────────────────────

    [Fact]
    public void EditsTaskName_IsLogixMcpS1Edits()
    {
        Assert.Equal("logix-mcp-s1-edits", Session1ScriptRunner.EditsTaskName);
    }

    [Fact]
    public void BuildCommand_WithEditsTaskName_RegistersEdits_AndUnregistersLeftoverCancel()
    {
        foreach (var verb in new[] { "cancel", "accept" })
        {
            var command = Session1ScriptRunner.BuildCommand(
                @"C:\repo\bench\studio-edits.ps1", verb, @"C:\temp\out.log",
                taskName: Session1ScriptRunner.EditsTaskName);

            Assert.Contains("Register-ScheduledTask -TaskName 'logix-mcp-s1-edits'", command);
            Assert.Contains("Start-ScheduledTask -TaskName 'logix-mcp-s1-edits'", command);
            Assert.Contains(
                "Unregister-ScheduledTask -TaskName 'logix-mcp-s1-cancel' -Confirm:$false",
                command);
            Assert.True(
                command.IndexOf("Unregister-ScheduledTask", StringComparison.Ordinal) <
                command.IndexOf("Register-ScheduledTask", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RunCoreAsync_WithEditsTaskName_ReportsEditsTaskName()
    {
        var result = await Session1ScriptRunner.RunCoreAsync(
            "accept", null, null, null, runner: null, isWindows: () => false,
            CancellationToken.None, Session1ScriptRunner.EditsTaskName);

        Assert.Equal("logix-mcp-s1-edits", result.TaskName);
    }

    // ── studio_modal_open → StudioModalOpen ────────────────

    [Fact]
    public void StudioStateKindOf_MapsStudioModalOpen_ToStudioModalOpen()
    {
        Assert.Equal("StudioModalOpen", StudioLifecycleHandler.StudioStateKindOf("studio_modal_open"));
        Assert.Equal("StudioModalOpen", StudioKinds.StudioModalOpenKind);
        Assert.Equal("Answer or close the open Studio dialog, then retry.", StudioLifecycleHandler.StudioModalOpenHint);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("accept")]
    public async Task StudioModalOpen_IsTypedError_WithHintTitleAndText(string verb)
    {
        var runner = (Func<string, CancellationToken, Task<Session1ScriptResult>>)((v, ct) =>
        {
            var json = System.Text.Json.JsonDocument.Parse(
                "{\"ok\":false,\"reason\":\"studio_modal_open\",\"detail\":{\"dialogTitle\":\"About Logix Designer\"}}").RootElement;
            return Task.FromResult(new Session1ScriptResult(
                false, "studio_modal_open", new[] { "s.png" },
                ParsedResultJson: json, DialogText: "Version 38.0"));
        });

        object result = verb == "cancel"
            ? await CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, Reader(State("pending")), runner, () => true,
                planLaunch: () => ("direct", null))
            : await AcceptPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, Reader(State("pending")), runner, () => true,
                planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("StudioModalOpen", err.Kind);
        Assert.Equal("Answer or close the open Studio dialog, then retry.", err.Hint);
        Assert.Contains("Answer or close the open Studio dialog, then retry.", err.Error, StringComparison.Ordinal);
        Assert.Contains("About Logix Designer", err.Error, StringComparison.Ordinal);
        Assert.Contains("Version 38.0", err.Error, StringComparison.Ordinal);
        Assert.Equal("pending", err.Before?.State);
    }

    // ── command_not_applicable / refusal diagnoses: state x verb ──

    private const string DisabledWhileTested = "disabled while the zone is tested";

    private static RoutinePendingEditState ZoneOf(string zoneState) =>
        State("pending") with
        {
            ZoneState = zoneState,
            ProgramTestedEdits = zoneState switch { "tested" => true, "untested" => false, _ => null },
        };

    [Theory]
    [InlineData("cancel",   "tested",   true,  "ZoneTested")]
    [InlineData("cancel",   "untested", false, "CancelNotApplicable")]
    [InlineData("cancel",   "unknown",  false, "CancelNotApplicable")]
    [InlineData("untest",   "tested",   false, "UntestNotApplicable")]
    [InlineData("untest",   "untested", true,  "NotTested")]
    [InlineData("untest",   "unknown",  false, "UntestNotApplicable")]
    [InlineData("assemble", "tested",   false, "NotTested")]
    [InlineData("assemble", "untested", true,  "NotTested")]
    [InlineData("assemble", "unknown",  false, "NotTested")]
    public async Task CommandNotApplicable_NamesObservedState_AndBlamesTestedZoneOnlyWhenTested(
        string verbName, string zoneState, bool refusedBeforeLaunch, string expectedKind)
    {
        var verb = verbName switch
        {
            "cancel"   => StudioLifecycleVerb.Cancel,
            "untest"   => StudioLifecycleVerb.Untest,
            _          => StudioLifecycleVerb.Assemble,
        };
        var before = ZoneOf(zoneState);
        var verbs = new List<string>();

        var result = await StudioLifecycleHandler.ExecuteAsync(
            verb, Program, Routine, true, Reader(before),
            Runner(verbs, ok: false, reason: "command_not_applicable"), () => true,
            planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal(!refusedBeforeLaunch, err.Launched);
        Assert.Equal(refusedBeforeLaunch ? 0 : 1, verbs.Count);

        var testedEdits = before.ProgramTestedEdits is { } t ? (t ? "true" : "false") : "null";
        Assert.Contains($"zoneState='{zoneState}'", err.Error, StringComparison.Ordinal);
        Assert.Contains($"programTestedEdits={testedEdits}", err.Error, StringComparison.Ordinal);

        if (zoneState == "tested")
            // Cancel's pre-launch refusal words it "disabled in Studio while the zone is tested".
            Assert.Equal(verbName == "cancel", err.Error.Contains("while the zone is tested", StringComparison.Ordinal));
        else
            Assert.DoesNotContain(DisabledWhileTested, err.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancel",   true)]
    [InlineData("untest",   false)]
    [InlineData("assemble", false)]
    public void NotApplicableText_DisabledWhileTestedWording_OnlyForTestedZoneAndCancel(
        string verbName, bool claimsDisabledWhenTested)
    {
        var verb = verbName switch
        {
            "cancel"   => StudioLifecycleVerb.Cancel,
            "untest"   => StudioLifecycleVerb.Untest,
            _          => StudioLifecycleVerb.Assemble,
        };

        foreach (var zoneState in new[] { "tested", "untested", "unknown" })
        {
            var text = StudioLifecycleHandler.NotApplicableText(verb, ZoneOf(zoneState));
            Assert.Equal(
                claimsDisabledWhenTested && zoneState == "tested",
                text.Contains(DisabledWhileTested, StringComparison.Ordinal));
        }
    }

    private const string NoRoutineOpenFormat =
        "or Studio has no routine of program '{0}' open; open one of its routines in Studio and retry";

    [Fact]
    public async Task CancelNotApplicable_Hint_ContainsNoRoutineOpenWording_WithProgramName()
    {
        var verbs = new List<string>();
        var result = await StudioLifecycleHandler.ExecuteAsync(
            StudioLifecycleVerb.Cancel, Program, Routine, true, Reader(ZoneOf("untested")),
            Runner(verbs, ok: false, reason: "command_not_applicable"), () => true,
            planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("CancelNotApplicable", err.Kind);
        var wording = string.Format(NoRoutineOpenFormat, Program);
        Assert.Contains(wording, err.Hint, StringComparison.Ordinal);
        Assert.Contains(wording, err.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test",     "untested", "TestNotApplicable")]
    [InlineData("untest",   "tested",   "UntestNotApplicable")]
    [InlineData("assemble", "tested",   "NotTested")]
    [InlineData("accept",   "untested", "AcceptNotApplicable")]
    public async Task NotApplicableHints_ForTestUntestAssembleAccept_ContainSameWording(
        string verbName, string zoneState, string expectedKind)
    {
        var verb = verbName switch
        {
            "test"     => StudioLifecycleVerb.Test,
            "untest"   => StudioLifecycleVerb.Untest,
            "assemble" => StudioLifecycleVerb.Assemble,
            _          => StudioLifecycleVerb.Accept,
        };
        var verbs = new List<string>();
        var result = await StudioLifecycleHandler.ExecuteAsync(
            verb, Program, Routine, true, Reader(ZoneOf(zoneState)),
            Runner(verbs, ok: false, reason: "command_not_applicable"), () => true,
            planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        var wording = string.Format(NoRoutineOpenFormat, Program);
        Assert.Contains(wording, err.Hint, StringComparison.Ordinal);
        Assert.Contains(wording, err.Error, StringComparison.Ordinal);
    }
}
#endif
