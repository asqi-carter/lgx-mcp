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
/// Tests for <see cref="TestPendingEditsHandler"/> (<c>logix_test_pending_edits</c>)
/// and its capability gate in <see cref="LogixSdkTools"/>.
/// </summary>
[Collection("StaticHandlerHooks")]
public class TestPendingEditsToolTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

    /// <summary>Each test starts with a ledger that knows the zone this server "published", isolated from other tests' connects.</summary>
    public TestPendingEditsToolTests() => ZoneLedger.TestOverride = KnownZoneLedger(Program, Routine);

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
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false, Reader(RoutineState("accepted")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("confirm=true", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_test_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Contains("execute on a running controller", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RefusesWhenAutomationDisabled_CapabilityDisabled()
    {
        var capabilities = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities);

        var json = await tools.GetTestPendingEditsResponseAsync(Program, Routine, confirm: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", root.GetProperty("hint").GetString());
        Assert.Contains("logix_test_pending_edits", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RefusesWhenNothingPending()
    {
        var verbs = new List<string>();
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("none")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("nothing pending", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("none", err.Before?.State);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RefusesWhenStudioNotInstalled()
    {
        var verbs = new List<string>();
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(verbs), () => false, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("Studio 5000", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task Ok_CarriesDialogWarnings_AndVerifiedMessage()
    {
        var warnings = new[] { "Outputs in 'D' and 'R' rung edit zones stay in their last state.", "SFC routines are reset." };
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("untested"), ZoneState("tested")),
            Runner(warnings: warnings), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.DoesNotContain("tested\"", ok.Status, StringComparison.Ordinal);
        Assert.NotEqual("tested", ok.Status);
        Assert.DoesNotContain("does not distinguish tested from untested", ok.Message, StringComparison.Ordinal);
        Assert.Contains("verified: program TestEdits=true", ok.Message, StringComparison.Ordinal);
        Assert.Contains("logix_read", ok.Message, StringComparison.Ordinal);
        Assert.Equal(warnings, ok.DialogWarnings);
        Assert.Equal("dialog text", ok.DialogText);
        Assert.Equal("posted", ok.GestureTier);
        Assert.NotEqual("none", ok.After.State);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(2, doc.RootElement.GetProperty("dialogWarnings").GetArrayLength());
        Assert.Equal("dialog text", doc.RootElement.GetProperty("dialogText").GetString());
        Assert.Equal("posted", doc.RootElement.GetProperty("gestureTier").GetString());
    }

    [Fact]
    public async Task Ok_WithNoWarnings_StillCarriesEmptyDialogWarnings()
    {
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("untested"), ZoneState("tested")),
            Runner(), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.NotNull(ok.DialogWarnings);
        Assert.Empty(ok.DialogWarnings!);
    }

    [Fact]
    public async Task AfterNone_IsError_NotReportedAsTested()
    {
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("state='none'", err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
    }

    [Fact]
    public async Task RunnerInvokedWithVerbTest()
    {
        var verbs = new List<string>();
        await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("untested"), ZoneState("tested")),
            Runner(verbs), () => true);

        Assert.Equal(new[] { "test" }, verbs);
    }

    private static RoutinePendingEditState ZoneState(string zone) =>
        RoutineState("pending") with { ZoneState = zone, ProgramTestedEdits = zone == "tested" };

    [Fact]
    public async Task AlreadyTested_RefusedBeforeRunner()
    {
        var verbs = new List<string>();
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(ZoneState("tested")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("AlreadyTested", err.Kind);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
        Assert.Contains(
            "the zone is already tested; read behaviour with logix_read, then " +
            "logix_assemble_pending_edits or logix_untest_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Equal("tested", err.Before?.ZoneState);
        Assert.True(err.Before?.ProgramTestedEdits);
        Assert.NotEmpty(err.Before!.PendingRungs);
    }

    [Fact]
    public async Task Ok_ReportsVerifiedProgramTestEditsTrue()
    {
        var result = await TestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("untested"), ZoneState("tested")),
            Runner(), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("tested", ok.After.ZoneState);
        Assert.Contains("verified: program TestEdits=true", ok.Message, StringComparison.Ordinal);
        Assert.Contains("logix_read", ok.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not distinguish tested from untested", ok.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ok_AfterStateSettlesToTested_WhenFirstReadIsStale()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var result = await TestPendingEditsHandler.ExecuteAsync(
                Program, Routine, confirm: true,
                Reader(ZoneState("untested"), ZoneState("untested"), ZoneState("tested")),
                Runner(), () => true);

            Assert.Equal("tested", Assert.IsType<CancelPendingEditsResult>(result).After.ZoneState);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public async Task AfterStateNotTested_IsTestStateNotConfirmed()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var result = await TestPendingEditsHandler.ExecuteAsync(
                Program, Routine, confirm: true,
                Reader(ZoneState("untested")),
                Runner(), () => true);

            var err = Assert.IsType<CancelPendingEditsError>(result);
            Assert.Equal("TestStateNotConfirmed", err.Kind);
            Assert.True(err.Launched);
            Assert.Equal("untested", err.Before?.ZoneState);
            Assert.Contains("zoneState='untested'", err.Error, StringComparison.Ordinal);
            Assert.Contains("expected 'tested'", err.Error, StringComparison.Ordinal);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }
}
#endif
