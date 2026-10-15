#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="CancelPendingEditsHandler"/> (the <c>logix_cancel_pending_edits</c>
/// tool's pure handler) and its wiring in
/// <see cref="LogixSdkTools"/>.
///
/// <para>
/// Unlike <see cref="FinalizeEditsHandler"/>, which is exercised against a fake
/// <c>ILogixProject</c>, <see cref="CancelPendingEditsHandler"/> depends on two
/// injected delegates — a pending-edits reader and a session-1 script runner —
/// so these tests supply fakes for both rather than standing up a fake SDK
/// session. This mirrors the shape called for by the acceptance
/// criteria ("fake pending-edits reader and fake session-1 runner").
/// </para>
/// </summary>
[Collection("StaticHandlerHooks")]
public class CancelPendingEditsToolTests
{
    // ── Test helpers ─────────────────────────────────────────────────────────

    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

    private static RoutinePendingEditState RoutineState(string state, int pendingCount = 1) =>
        new(
            Program:      Program,
            Routine:      Routine,
            Rungs:        pendingCount + 1,
            PendingRungs: state == "none"
                ? Array.Empty<PendingEditRungEntry>()
                : new[] { new PendingEditRungEntry(0, "I", "OTE(Motor_Run);") },
            State:        state);

    private static Func<string, string, CancellationToken, Task<object>> FakeReader(
        RoutinePendingEditState state) =>
        (p, r, ct) => Task.FromResult<object>(new PendingEditsResult(
            Connected:      true,
            ControllerMode: "Run",
            Note:           null,
            Routines:       new[] { state },
            Staged:         new StagedEditsState(0, Array.Empty<string>()),
            ObservedAt:     DateTimeOffset.UtcNow));

    /// <summary>
    /// A reader that returns a different state on the first call (the
    /// "before" read) than on every subsequent call (the "after" read),
    /// simulating a cancel that actually changed the controller's edit-zone
    /// state.
    /// </summary>
    private static Func<string, string, CancellationToken, Task<object>> FakeReaderSequence(
        RoutinePendingEditState first, RoutinePendingEditState thenAfter)
    {
        var callCount = 0;
        return (p, r, ct) =>
        {
            callCount++;
            var state = callCount == 1 ? first : thenAfter;
            return Task.FromResult<object>(new PendingEditsResult(
                Connected:      true,
                ControllerMode: "Run",
                Note:           null,
                Routines:       new[] { state },
                Staged:         new StagedEditsState(0, Array.Empty<string>()),
                ObservedAt:     DateTimeOffset.UtcNow));
        };
    }

    private static Func<string, CancellationToken, Task<Session1ScriptResult>> FakeRunner(
        bool ok, string reason, params string[] screenshots) =>
        (verb, ct) => Task.FromResult(new Session1ScriptResult(ok, reason, screenshots));

    private static Func<string, CancellationToken, Task<Session1ScriptResult>> ThrowingRunner(
        Exception ex) =>
        (verb, ct) => throw ex;

    // ── CancelPendingEdits_RefusesWithoutConfirm ────────────────────────────

    /// <summary>
    /// Bench S14-p102 (2026-10-01): cancel used to report status='cancelled' while its own
    /// after-read still showed the zone. A cancel now requires the after-state to settle to
    /// 'none', like accept; a zone that never clears is an error naming both states.
    /// </summary>
    [Fact]
    public async Task CancelPendingEdits_AfterStillPending_IsError_NotCancelled()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var result = await CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, confirm: true,
                pendingEditsReader: FakeReader(RoutineState("pending")),
                session1Runner:     FakeRunner(true, "cancel_gesture_sent", "shot1.png"),
                isStudioInstalled:  () => true);

            var error = Assert.IsType<CancelPendingEditsError>(result);
            Assert.Contains("still reports state='pending'", error.Error, StringComparison.Ordinal);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public async Task CancelPendingEdits_RefusesWithoutConfirm()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = FakeRunner(true, "cancel_gesture_sent", "shot1.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("confirm=true", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── RefusalText_NamesCancelAcceptedProgramEdits ─────────────────────────

    /// <summary>
    /// The confirm-refusal text must name the real
    /// Studio 5000 menu transition, "Cancel Accepted Program Edits" (the
    /// non-existent "Cancel All Edits" was named before this fix), and must
    /// state that the tool drives Studio 5000's GUI in the interactive
    /// session.
    /// </summary>
    [Fact]
    public async Task RefusalText_NamesCancelAcceptedProgramEdits()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = FakeRunner(true, "cancel_gesture_sent", "shot1.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("Cancel Accepted Program Edits", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancel All Edits", error.Error, StringComparison.Ordinal);
        Assert.Contains("interactive", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── CancelPendingEdits_RefusesWhenNothingPending ────────────────────────

    [Fact]
    public async Task CancelPendingEdits_RefusesWhenNothingPending()
    {
        var reader = FakeReader(RoutineState("none"));
        var runner = FakeRunner(true, "cancel_gesture_sent", "shot1.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("nothing pending", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("none", error.Before?.State);
    }

    // ── Cancel outcome and program-level gate ─────────────────────

    private static PendingEditsResult Snapshot(params RoutinePendingEditState[] routines) =>
        new(Connected:      true,
            ControllerMode: "Run",
            Note:           null,
            Routines:       routines,
            Staged:         new StagedEditsState(0, Array.Empty<string>()),
            ObservedAt:     DateTimeOffset.UtcNow);

    [Fact]
    public async Task CancelPendingEdits_Succeeded_IsNotReportedAsError()
    {
        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: FakeReaderSequence(RoutineState("pending"), RoutineState("none")),
            session1Runner:     FakeRunner(true, "cancel_gesture_sent", "shot1.png"),
            isStudioInstalled:  () => true);

        var success = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("cancelled", success.Status);
        Assert.Equal("none", success.After.State);
    }

    [Fact]
    public async Task CancelPendingEdits_OtherRoutineOfProgramWithPendingEdits_IsAllowed()
    {
        const string other = "OtherRoutine";
        var otherNone    = RoutineState("none") with { Routine = other };
        var mainPending  = RoutineState("pending");
        var mainCleared  = RoutineState("none");
        var cancelled    = false;

        Task<object> Reader(string p, string r, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(r))   // program-scope read
                return Task.FromResult<object>(Snapshot(otherNone, cancelled ? mainCleared : mainPending));
            return Task.FromResult<object>(Snapshot(otherNone));
        }

        Task<Session1ScriptResult> Runner(string verb, CancellationToken ct)
        {
            cancelled = true;
            return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", new[] { "shot1.png" }));
        }

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, other, confirm: true,
            pendingEditsReader: Reader,
            session1Runner:     Runner,
            isStudioInstalled:  () => true);

        var success = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("cancelled", success.Status);
        Assert.True(cancelled);
    }

    // ── CancelPendingEdits_RefusesWhenStudioNotInstalled ────────────────────

    [Fact]
    public async Task CancelPendingEdits_RefusesWhenStudioNotInstalled()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = FakeRunner(true, "cancel_gesture_sent", "shot1.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => false);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("Studio 5000", error.Error, StringComparison.Ordinal);
        Assert.Null(error.Before);

        // The refusal must happen before either fake is ever consulted — a
        // host without Studio 5000 can never drive its GUI, so neither the
        // pending-edits read nor the session-1 script should run.
    }

    // ── Studio-not-installed precedes both fakes ────────────────────────────

    [Fact]
    public async Task CancelPendingEdits_RefusesWhenStudioNotInstalled_NeverConsultsReaderOrRunner()
    {
        var readerCalled = false;
        var runnerCalled = false;

        Func<string, string, CancellationToken, Task<object>> reader = (p, r, ct) =>
        {
            readerCalled = true;
            return Task.FromResult<object>(new PendingEditsResult(true, "Run", null,
                new[] { RoutineState("pending") },
                new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));
        };
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, ct) =>
        {
            runnerCalled = true;
            return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>()));
        };

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => false);

        Assert.IsType<CancelPendingEditsError>(result);
        Assert.False(readerCalled);
        Assert.False(runnerCalled);
    }

    // ── Success path: before/after states and screenshots surfaced ─────────

    [Fact]
    public async Task CancelPendingEdits_Success_ReturnsBeforeAfterAndScreenshots()
    {
        var reader = FakeReaderSequence(
            first:     RoutineState("pending"),
            thenAfter: RoutineState("none"));
        var runner = FakeRunner(true, "cancel_gesture_sent", "shot1.png", "shot2.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var success = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("cancelled", success.Status);
        Assert.Equal("pending", success.Before.State);
        Assert.Equal("none", success.After.State);
        Assert.Equal(new[] { "shot1.png", "shot2.png" }, success.Screenshots);
    }

    // ── Response_CarriesDriverResultJsonVerbatim ────────────────────────────

    /// <summary>
    /// The tool's response must surface the driver's RESULT_JSON output
    /// verbatim — both the raw line as printed and its parsed document — in a
    /// top-level 'driver' field, so a driver defect is never silently
    /// swallowed and misread as a bench fact by the caller.
    /// </summary>
    [Fact]
    public async Task Response_CarriesDriverResultJsonVerbatim()
    {
        var reader = FakeReaderSequence(
            first:     RoutineState("pending"),
            thenAfter: RoutineState("none"));

        const string rawLine =
            "RESULT_JSON: {\"ok\":true,\"verb\":\"cancel\",\"sessionId\":1," +
            "\"reason\":\"cancel_gesture_sent\",\"screenshots\":[\"shot1.png\"],\"detail\":{}}";

        var parsed = Session1ScriptRunner.ParseResultJson(rawLine + "\n");
        Assert.NotNull(parsed);
        Assert.False(parsed!.Malformed);

        Func<string, CancellationToken, Task<Session1ScriptResult>> runner =
            (verb, ct) => Task.FromResult(parsed!);

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var success = Assert.IsType<CancelPendingEditsResult>(result);

        Assert.NotNull(success.Driver);
        Assert.Equal(rawLine, success.Driver!.ResultJsonLine);
        Assert.False(success.Driver.Malformed);

        Assert.NotNull(success.Driver.Parsed);
        var parsedDoc = success.Driver.Parsed!.Value;
        Assert.True(parsedDoc.GetProperty("ok").GetBoolean());
        Assert.Equal("cancel_gesture_sent", parsedDoc.GetProperty("reason").GetString());
        Assert.Equal(1, parsedDoc.GetProperty("sessionId").GetInt32());
    }

    // ── Malformed driver output never produces a success response ──────────

    /// <summary>
    /// When the driver's RESULT_JSON line exists but cannot be parsed as
    /// valid JSON with the expected shape (e.g. bad JSON, or a non-boolean
    /// 'ok' field), that is a driver defect, not a legitimate ok=false
    /// report. The tool must refuse — never a
    /// <see cref="CancelPendingEditsResult"/> — and must surface the raw
    /// line and the malformed flag so the defect is visible to the caller.
    /// </summary>
    [Fact]
    public async Task CancelPendingEdits_MalformedDriverOutput_DoesNotSilentlyProduceSuccess()
    {
        var reader = FakeReader(RoutineState("pending"));

        var malformed = Session1ScriptRunner.ParseResultJson("RESULT_JSON: {not valid json\n");
        Assert.NotNull(malformed);
        Assert.True(malformed!.Malformed);
        Assert.False(malformed.Ok);

        Func<string, CancellationToken, Task<Session1ScriptResult>> runner =
            (verb, ct) => Task.FromResult(malformed!);

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        Assert.IsNotType<CancelPendingEditsResult>(result);
        var error = Assert.IsType<CancelPendingEditsError>(result);

        Assert.Contains("RESULT_JSON", error.Error, StringComparison.Ordinal);
        Assert.NotNull(error.Driver);
        Assert.True(error.Driver!.Malformed);
        Assert.Equal("RESULT_JSON: {not valid json", error.Driver.ResultJsonLine);
        Assert.Null(error.Driver.Parsed);
    }

    // ── Session-1 script failure surfaces before-state and screenshots ─────

    [Fact]
    public async Task CancelPendingEdits_SessionScriptFails_ReturnsBeforeStateAndScreenshots()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = FakeRunner(false, "cancel_confirmation_dialog_not_found", "shot1.png");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("cancel_confirmation_dialog_not_found", error.Error, StringComparison.Ordinal);
        Assert.Equal("pending", error.Before?.State);
        Assert.Equal(new[] { "shot1.png" }, error.Screenshots);
    }

    [Fact]
    public async Task CancelPendingEdits_SessionScriptThrows_ReturnsErrorWithBeforeState()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = ThrowingRunner(new InvalidOperationException("scheduled task registration failed"));

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("scheduled task registration failed", error.Error, StringComparison.Ordinal);
        Assert.Equal("pending", error.Before?.State);
    }

    // ── Input validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task CancelPendingEdits_BlankProgram_ReturnsError()
    {
        var reader = FakeReader(RoutineState("pending"));
        var runner = FakeRunner(true, "cancel_gesture_sent");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            "", Routine, confirm: true,
            pendingEditsReader: reader,
            session1Runner:     runner,
            isStudioInstalled:  () => true);

        var error = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("program", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Session1ScriptRunner pure-function tests ────────────────────────────

    [Fact]
    public void Session1ScriptRunner_ParseResultJson_ParsesOkTrueWithScreenshots()
    {
        var output =
            "studio-edits.ps1: running from C:\\bench\\studio-edits.ps1, Verb=cancel\n" +
            "    screenshot (project-open): C:\\Users\\Example\\ctmp\\shot1.png\n" +
            "RESULT_JSON: {\"ok\":true,\"verb\":\"cancel\",\"sessionId\":1,\"reason\":\"cancel_gesture_sent\"," +
            "\"screenshots\":[\"C:\\\\Users\\\\Example\\\\ctmp\\\\shot1.png\",\"C:\\\\Users\\\\Example\\\\ctmp\\\\shot2.png\"],\"detail\":{}}\n";

        var result = Session1ScriptRunner.ParseResultJson(output);

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal("cancel_gesture_sent", result.Reason);
        Assert.Equal(2, result.Screenshots.Count);
    }

    [Fact]
    public void Session1ScriptRunner_ParseResultJson_NoResultLine_ReturnsNull()
    {
        var result = Session1ScriptRunner.ParseResultJson("nothing of interest here\n");
        Assert.Null(result);
    }

    [Fact]
    public void Session1ScriptRunner_BuildCommand_UsesRegisterAndStartScheduledTaskWithInteractivePrincipal()
    {
        var command = Session1ScriptRunner.BuildCommand(
            @"C:\repo\bench\studio-edits.ps1", "cancel", @"C:\temp\out.log");

        Assert.Contains("Register-ScheduledTask", command, StringComparison.Ordinal);
        Assert.Contains("Start-ScheduledTask", command, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskPrincipal", command, StringComparison.Ordinal);
        Assert.Contains("-LogonType Interactive", command, StringComparison.Ordinal);
        Assert.Contains("'logix-mcp-s1-cancel'", command, StringComparison.Ordinal);
        // The scheduled task's own -Argument value (the action string Task
        // Scheduler hands to CreateProcess at run time) must use double
        // quotes around the -File/-Verb/redirect values, never single quotes
        // — see BuildActionArgument's remarks for why.
        Assert.Contains("-Verb ''cancel''", command, StringComparison.Ordinal); // inside -Command, PowerShell single quotes (doubled for the outer PS literal)
    }

    [Fact]
    public async Task Session1ScriptRunner_RunAsync_OnNonWindows_ReportsNotWindows()
    {
        if (OperatingSystem.IsWindows())
            return; // this invariant only applies off Windows.

        var result = await Session1ScriptRunner.RunAsync("cancel");

        Assert.False(result.Ok);
        Assert.Contains("not_windows", result.Reason, StringComparison.Ordinal);
    }

    // ── LogixSdkTools wiring ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCancelPendingEditsResponseAsync_NoActiveSession_ReturnsError()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetCancelPendingEditsResponseAsync(Program, Routine, confirm: true);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    private sealed class PlanProbe : IEnvironmentProbe
    {
        public int CurrentSessionId { get; init; }
        public bool IsElevated { get; init; }
        public IReadOnlyList<int> LogixDesignerSessionIds { get; init; } = Array.Empty<int>();
        public bool StudioIntegrityHigherThanCurrent { get; init; }
    }

    private static readonly (string? Mode, string? Level) TaskPlan = ("scheduled_task", "Limited");

    /// <summary>The "No active SDK session" payload reports launchMode/runLevel and launched=false.</summary>
    [Fact]
    public async Task NoSession_ErrorCarriesLaunchModeAndLaunchedFalse()
    {
        var caps = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: true);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), caps);

        var direct = await tools.GetCancelPendingEditsResponseAsync(
            Program, Routine, confirm: true, probe: new PlanProbe { CurrentSessionId = 1 });
        var d = JsonDocument.Parse(direct).RootElement;
        Assert.Contains("No active SDK session", d.GetProperty("error").GetString());
        Assert.Equal("direct", d.GetProperty("launchMode").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("runLevel").ValueKind);
        Assert.False(d.GetProperty("launched").GetBoolean());

        var task = await tools.GetCancelPendingEditsResponseAsync(
            Program, Routine, confirm: true, probe: new PlanProbe { CurrentSessionId = 0, IsElevated = true });
        var t = JsonDocument.Parse(task).RootElement;
        Assert.Equal("scheduled_task", t.GetProperty("launchMode").GetString());
        Assert.Equal("Highest", t.GetProperty("runLevel").GetString());
        Assert.False(t.GetProperty("launched").GetBoolean());
    }

    /// <summary>Every handler refusal that occurs before the runner is invoked carries the plan and launched=false.</summary>
    [Fact]
    public async Task EveryHandlerRefusal_CarriesLaunchModeAndLaunchedFalse()
    {
        var runnerCalls = 0;
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (v, c) =>
        {
            runnerCalls++;
            return Task.FromResult(new Session1ScriptResult(true, "x", Array.Empty<string>()));
        };
        Func<(string?, string?)> plan = () => TaskPlan;

        Func<string, string, CancellationToken, Task<object>> missingRoutine =
            (p, r, c) => Task.FromResult<object>(new PendingEditsResult(
                true, "Run", null, Array.Empty<RoutinePendingEditState>(),
                new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));
        Func<string, string, CancellationToken, Task<object>> readerError =
            (p, r, c) => Task.FromResult<object>(new PendingEditsError("read failed"));

        var cases = new List<(string Name, Task<object> Call)>
        {
            ("empty program", CancelPendingEditsHandler.ExecuteAsync(
                "", Routine, true, FakeReader(RoutineState("pending")), runner, () => true, default, plan)),
            ("empty routine", CancelPendingEditsHandler.ExecuteAsync(
                Program, " ", true, FakeReader(RoutineState("pending")), runner, () => true, default, plan)),
            ("quote", CancelPendingEditsHandler.ExecuteAsync(
                "Main'Program", Routine, true, FakeReader(RoutineState("pending")), runner, () => true, default, plan)),
            ("studio not installed", CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, FakeReader(RoutineState("pending")), runner, () => false, default, plan)),
            ("confirm missing", CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, false, FakeReader(RoutineState("pending")), runner, () => true, default, plan)),
            ("reader error", CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, readerError, runner, () => true, default, plan)),
            ("routine not found", CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, missingRoutine, runner, () => true, default, plan)),
            ("nothing pending", CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, FakeReader(RoutineState("none")), runner, () => true, default, plan)),
        };

        foreach (var (name, call) in cases)
        {
            var error = Assert.IsType<CancelPendingEditsError>(await call);
            Assert.True(error.LaunchMode == "scheduled_task", $"{name}: launchMode");
            Assert.True(error.RunLevel == "Limited", $"{name}: runLevel");
            Assert.True(error.Launched == false, $"{name}: launched");
            var json = JsonDocument.Parse(JsonSerializer.Serialize(error)).RootElement;
            Assert.False(json.GetProperty("launched").GetBoolean());
            Assert.Equal("scheduled_task", json.GetProperty("launchMode").GetString());
        }
        Assert.Equal(0, runnerCalls);
    }

    /// <summary>Once the runner is invoked, every payload (success or failure) carries launched=true.</summary>
    [Fact]
    public async Task RunnerResult_CarriesLaunchedTrue()
    {
        Func<(string?, string?)> plan = () => TaskPlan;

        var ok = Assert.IsType<CancelPendingEditsResult>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true,
            FakeReaderSequence(RoutineState("pending"), RoutineState("none")),
            FakeRunner(true, "cancel_gesture_sent"), () => true, default, plan));
        Assert.True(ok.Launched);

        var failed = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, FakeReader(RoutineState("pending")),
            FakeRunner(false, "boom"), () => true, default, plan));
        Assert.True(failed.Launched);

        var threw = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, FakeReader(RoutineState("pending")),
            ThrowingRunner(new InvalidOperationException("x")), () => true, default, plan));
        Assert.True(threw.Launched);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok)).RootElement;
        Assert.True(json.GetProperty("launched").GetBoolean());
    }

    [Fact]
    public async Task CancelPendingEdits_NoActiveSession_ReturnsErrorJson()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.CancelPendingEdits(Program, Routine, confirm: true);
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            $"Expected 'error' property when no active session is registered. Response: {json}");
    }

    [Fact]
    public void LogixSdkTools_CancelPendingEdits_IsRegisteredWithCorrectName()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.CancelPendingEdits));
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
        Assert.NotNull(toolAttr);

        var name = toolAttr!.GetType().GetProperty("Name")?.GetValue(toolAttr) as string;
        Assert.Equal("logix_cancel_pending_edits", name);
    }

    /// <summary>
    /// Capability honesty: the tool description must explicitly
    /// state that it drives the Studio 5000 GUI, not the SDK, so a caller
    /// reading only the tool description understands this is desktop
    /// automation before ever calling it.
    /// </summary>
    [Fact]
    public void LogixSdkTools_CancelPendingEdits_DescriptionStatesItDrivesStudioGui()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.CancelPendingEdits));
        Assert.NotNull(method);

        var descAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "DescriptionAttribute");
        Assert.NotNull(descAttr);

        var description = descAttr!.GetType().GetProperty("Description")?.GetValue(descAttr) as string;
        Assert.NotNull(description);

        Assert.Contains("STUDIO 5000 GUI", description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("desktop automation", description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session 1", description!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Studio-automation descriptions describe the real mechanism (posted menu
    /// command, no keyboard or coordinate fallback) and no longer claim calibration
    /// on a single 1280x800 bench machine.
    /// </summary>
    [Theory]
    [InlineData(nameof(LogixSdkTools.CancelPendingEdits))]
    [InlineData(nameof(LogixSdkTools.AcceptPendingEdits))]
    public void LogixSdkTools_StudioAutomationDescriptions_DescribePostedMenuCommandNotCalibration(string methodName)
    {
        var method = typeof(LogixSdkTools).GetMethod(methodName);
        Assert.NotNull(method);

        var descAttr = method!.GetCustomAttributes(inherit: false)
            .First(a => a.GetType().Name == "DescriptionAttribute");
        var description = (string)descAttr.GetType().GetProperty("Description")!.GetValue(descAttr)!;

        Assert.DoesNotContain("1280", description);
        Assert.DoesNotContain("calibrated", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("menu command", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no keyboard fallback", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screen-coordinate fallback", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("can still depend on the screen", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A runner refusal before the driver started (Launched=false) is reported as
    /// "Not launched", never as the driver script having "reported failure", and
    /// the payload carries launched=false.
    /// </summary>
    [Fact]
    public async Task RunnerPreLaunchRefusal_SaysNotLaunched_LaunchedFalse()
    {
        Func<(string?, string?)> plan = () => ("direct", null);
        Func<string, CancellationToken, Task<Session1ScriptResult>> refusing = (verb, ct) =>
            Task.FromResult(new Session1ScriptResult(
                false, "studio_elevated_driver_not: Studio 5000 is running elevated", Array.Empty<string>(),
                LaunchMode: "direct", Launched: false));

        var err = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, FakeReader(RoutineState("pending")), refusing, () => true, default, plan));

        Assert.False(err.Launched);
        Assert.Equal("direct", err.LaunchMode);
        Assert.StartsWith("Not launched:", err.Error, StringComparison.Ordinal);
        Assert.Contains("studio_elevated_driver_not", err.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("reported failure", err.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner result's gestureTier is passed through additively on the cancel payload
    /// (success and post-launch failure); when the runner reports none the field is absent.
    /// </summary>
    [Fact]
    public async Task RunnerResult_PassesGestureTierThrough()
    {
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, ct) =>
            Task.FromResult(new Session1ScriptResult(
                true, "cancel_gesture_sent", new[] { "shot1.png" }, GestureTier: "posted"));

        var result = Assert.IsType<CancelPendingEditsResult>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true,
            FakeReaderSequence(RoutineState("pending"), RoutineState("none")),
            runner, () => true));

        Assert.Equal("posted", result.GestureTier);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(result)))
            Assert.Equal("posted", doc.RootElement.GetProperty("gestureTier").GetString());

        Func<string, CancellationToken, Task<Session1ScriptResult>> failing = (verb, ct) =>
            Task.FromResult(new Session1ScriptResult(
                false, "cancel_confirmation_dialog_not_found", Array.Empty<string>(), GestureTier: "posted"));

        var error = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true, FakeReader(RoutineState("pending")), failing, () => true));
        Assert.Equal("posted", error.GestureTier);

        var without = Assert.IsType<CancelPendingEditsResult>(await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, true,
            FakeReaderSequence(RoutineState("pending"), RoutineState("none")),
            FakeRunner(true, "cancel_gesture_sent", "shot1.png"), () => true));
        Assert.Null(without.GestureTier);
        Assert.DoesNotContain("gestureTier", JsonSerializer.Serialize(without), StringComparison.Ordinal);
    }

    // ── State-based preconditions ─────────────────────────────────

    [Fact]
    public async Task ZoneTested_RefusedBeforeRunner_NoStudioCommand()
    {
        var verbs = new List<string>();
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, ct) =>
        {
            verbs.Add(verb);
            return Task.FromResult(new Session1ScriptResult(true, "cancel_gesture_sent", Array.Empty<string>()));
        };
        var tested = RoutineState("pending") with { ZoneState = "tested", ProgramTestedEdits = true };

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, FakeReader(tested), runner, () => true,
            planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("ZoneTested", err.Kind);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
        Assert.Contains(
            "Cancel Accepted is disabled in Studio while the zone is tested. Call " +
            "logix_untest_pending_edits (the controller goes back to the original logic), then " +
            "logix_cancel_pending_edits.", err.Error, StringComparison.Ordinal);
        Assert.Equal("tested", err.Before?.ZoneState);
        Assert.True(err.Before?.ProgramTestedEdits);
        Assert.NotEmpty(err.Before!.PendingRungs);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal("ZoneTested", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("tested", doc.RootElement.GetProperty("before").GetProperty("zoneState").GetString());
    }

    [Fact]
    public async Task CommandNotApplicable_IsCancelNotApplicable_OnUntestedZone_DoesNotBlameTestedState()
    {
        var untested = RoutineState("pending") with { ZoneState = "untested", ProgramTestedEdits = false };

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, FakeReader(untested),
            FakeRunner(false, "command_not_applicable"), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("CancelNotApplicable", err.Kind);
        Assert.True(err.Launched);
        Assert.DoesNotContain("disabled while the zone is tested", err.Error, StringComparison.Ordinal);
        Assert.Contains("zoneState='untested'", err.Error, StringComparison.Ordinal);
        Assert.Contains("programTestedEdits=false", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_cancel_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Contains("command_not_applicable", err.Error, StringComparison.Ordinal);
        Assert.Contains("already finalized or cancelled from Logix Designer", err.Hint, StringComparison.Ordinal);
        Assert.Contains("shows its Errors pane as an auto-hide flyout", err.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("restart the", err.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZoneUntested_IsNotRefused_Cancel()
    {
        var untested = RoutineState("pending") with { ZoneState = "untested", ProgramTestedEdits = false };
        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            FakeReaderSequence(untested, RoutineState("none")),
            FakeRunner(true, "cancel_gesture_sent"), () => true);

        Assert.IsType<CancelPendingEditsResult>(result);
    }

    [Fact]
    public async Task StudioNotRunning_TypedError_WithBefore()
    {
        const string driverReason = "studio_not_running";
        const string expectedKind = "StudioNotRunning";
        var pending = RoutineState("pending");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, FakeReader(pending),
            FakeRunner(false, driverReason), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal("pending", err.Before?.State);
        Assert.NotEmpty(err.Before!.PendingRungs);
        Assert.Contains(
            "ask the engineer to open the project in Studio and go online by Upload; " +
            "lgx-mcp never launches Studio or goes online through it",
            err.Error, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal(expectedKind, doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("pending", doc.RootElement.GetProperty("before").GetProperty("state").GetString());
    }

    [Fact]
    public async Task StudioOffline_TypedError_WithBefore()
    {
        const string driverReason = "studio_offline";
        const string expectedKind = "StudioOffline";
        var pending = RoutineState("pending");

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, FakeReader(pending),
            FakeRunner(false, driverReason), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal("pending", err.Before?.State);
        Assert.NotEmpty(err.Before!.PendingRungs);
        Assert.Contains(
            "ask the engineer to open the project in Studio and go online by Upload; " +
            "lgx-mcp never launches Studio or goes online through it",
            err.Error, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal(expectedKind, doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("pending", doc.RootElement.GetProperty("before").GetProperty("state").GetString());
    }
}
#endif
