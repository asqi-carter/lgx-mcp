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
/// Tests for <see cref="UntestPendingEditsHandler"/> (<c>logix_untest_pending_edits</c>).
/// </summary>
[Collection("StaticHandlerHooks")]
public class UntestPendingEditsToolTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

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
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false, Reader(RoutineState("accepted")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("confirm=true", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_untest_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Contains("reverts the tested logic", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RefusesWhenAutomationDisabled_CapabilityDisabled()
    {
        var capabilities = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities);

        var json = await tools.GetUntestPendingEditsResponseAsync(Program, Routine, confirm: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", root.GetProperty("hint").GetString());
        Assert.Contains("logix_untest_pending_edits", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RefusesWhenNothingPending()
    {
        var verbs = new List<string>();
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("none")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("nothing pending", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("none", err.Before?.State);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RunnerInvokedWithVerbUntest_OkWhenStillPending()
    {
        var verbs = new List<string>();
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("tested"), ZoneState("untested")),
            Runner(verbs, warnings: new[] { "Outputs in 'I' zones..." }), () => true);

        Assert.Equal(new[] { "untest" }, verbs);
        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.DoesNotContain("does not distinguish tested from untested", ok.Message, StringComparison.Ordinal);
        Assert.Contains("verified: program TestEdits=false", ok.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "Outputs in 'I' zones..." }, ok.DialogWarnings);
        Assert.NotEqual("none", ok.After.State);
    }

    [Fact]
    public async Task AfterNone_IsError()
    {
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(), () => true);

        Assert.Contains("state='none'", Assert.IsType<CancelPendingEditsError>(result).Error, StringComparison.Ordinal);
    }

    private static RoutinePendingEditState ZoneState(string zone) =>
        RoutineState("pending") with { ZoneState = zone, ProgramTestedEdits = zone == "tested" };

    [Fact]
    public async Task Untested_RefusedNotTested_BeforeRunner()
    {
        var verbs = new List<string>();
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(ZoneState("untested")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("NotTested", err.Kind);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
        Assert.Contains("nothing to untest: the zone is not tested", err.Error, StringComparison.Ordinal);
        Assert.Equal("untested", err.Before?.ZoneState);
        Assert.False(err.Before?.ProgramTestedEdits);
        Assert.NotEmpty(err.Before!.PendingRungs);
    }

    [Fact]
    public async Task Ok_ReportsVerifiedProgramTestEditsFalse()
    {
        var result = await UntestPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(ZoneState("tested"), ZoneState("untested")),
            Runner(), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("untested", ok.After.ZoneState);
        Assert.Contains("verified: program TestEdits=false", ok.Message, StringComparison.Ordinal);
        Assert.Contains("logix_read", ok.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not distinguish tested from untested", ok.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterStateStillTested_IsUntestStateNotConfirmed()
    {
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var result = await UntestPendingEditsHandler.ExecuteAsync(
                Program, Routine, confirm: true,
                Reader(ZoneState("tested")),
                Runner(), () => true);

            var err = Assert.IsType<CancelPendingEditsError>(result);
            Assert.Equal("UntestStateNotConfirmed", err.Kind);
            Assert.Equal("tested", err.Before?.ZoneState);
            Assert.Contains("zoneState='tested'", err.Error, StringComparison.Ordinal);
            Assert.Contains("expected 'untested'", err.Error, StringComparison.Ordinal);
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }
}
#endif
