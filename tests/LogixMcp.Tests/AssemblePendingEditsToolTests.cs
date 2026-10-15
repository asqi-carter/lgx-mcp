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
/// Tests for <see cref="AssemblePendingEditsHandler"/> (<c>logix_assemble_pending_edits</c>).
/// </summary>
public class AssemblePendingEditsToolTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

    /// <summary>Each test starts with a ledger that knows the zone this server "published", isolated from other tests' connects.</summary>
    public AssemblePendingEditsToolTests() => ZoneLedger.TestOverride = KnownZoneLedger(Program, Routine);

    private static ZoneLedger KnownZoneLedger(string program, string routine)
    {
        var ledger = new ZoneLedger();
        ledger.Record(program, new[] { routine });
        return ledger;
    }

    private static RoutinePendingEditState RoutineState(string state) =>
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
        List<string>? verbs = null, bool ok = true, string reason = "gesture_sent",
        IReadOnlyList<string>? warnings = null) =>
        (verb, ct) =>
        {
            verbs?.Add(verb);
            return Task.FromResult(new Session1ScriptResult(
                ok, reason, new[] { "shot1.png" },
                GestureTier: "posted",
                DialogText: "dialog text",
                DialogWarnings: warnings));
        };

    [Fact]
    public async Task RefusesWithoutConfirm()
    {
        var verbs = new List<string>();
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false, Reader(RoutineState("accepted")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("confirm=true", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_assemble_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Contains("irreversible", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RefusesWhenAutomationDisabled_CapabilityDisabled()
    {
        var capabilities = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities);

        var json = await tools.GetAssemblePendingEditsResponseAsync(Program, Routine, confirm: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", root.GetProperty("hint").GetString());
        Assert.Contains("logix_assemble_pending_edits", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RefusesWhenNothingPending()
    {
        var verbs = new List<string>();
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("none")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("nothing pending", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("none", err.Before?.State);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task CommandNotApplicable_IsNotTested_NamesTestAndAcceptTools()
    {
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(ok: false, reason: "command_not_applicable"), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("NotTested", err.Kind);
        Assert.Contains(
            "Assemble needs a tested zone: call logix_test_pending_edits first, or " +
            "logix_accept_pending_edits to finalize in one step",
            err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal("NotTested", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OtherDriverFailure_HasNoKind()
    {
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(ok: false, reason: "confirmation_dialog_still_open"), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Null(err.Kind);
        Assert.Contains("confirmation_dialog_still_open", err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GestureOkButStillPending_IsError()
    {
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("pending")),
            Runner(), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("state='accepted'", err.Error, StringComparison.Ordinal);
        Assert.Contains("state='pending'", err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
    }

    [Fact]
    public async Task AfterNone_ReturnsOkWithBeforeAfter()
    {
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("assembled", ok.Status);
        Assert.Equal("accepted", ok.Before.State);
        Assert.Equal("none", ok.After.State);
        Assert.Empty(ok.After.PendingRungs);
        Assert.Equal(new[] { "shot1.png" }, ok.Screenshots);
        Assert.Equal("posted", ok.GestureTier);
        Assert.True(ok.Launched);
    }

    [Fact]
    public async Task RunnerInvokedWithVerbAssemble()
    {
        var verbs = new List<string>();
        await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(verbs), () => true);

        Assert.Equal(new[] { "assemble" }, verbs);
    }

    private static RoutinePendingEditState ZoneState(string zone) =>
        RoutineState("pending") with { ZoneState = zone, ProgramTestedEdits = zone == "tested" };

    [Fact]
    public async Task Untested_RefusedNotTested_BeforeRunner()
    {
        var verbs = new List<string>();
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(ZoneState("untested")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("NotTested", err.Kind);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
        Assert.Contains(
            "Assemble needs a tested zone: call logix_test_pending_edits first, or " +
            "logix_accept_pending_edits to finalize in one step", err.Error, StringComparison.Ordinal);
        Assert.Equal("untested", err.Before?.ZoneState);
        Assert.False(err.Before?.ProgramTestedEdits);
        Assert.NotEmpty(err.Before!.PendingRungs);
    }

    [Fact]
    public async Task Tested_IsNotRefused()
    {
        var verbs = new List<string>();
        var result = await AssemblePendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("tested"), RoutineState("none")),
            Runner(verbs), () => true);

        Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Single(verbs);
    }
}
#endif
