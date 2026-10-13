#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="AcceptPendingEditsHandler"/> (the <c>logix_accept_pending_edits</c>
/// tool's pure handler) and its capability gate in <see cref="LogixSdkTools"/>.
/// Uses a fake pending-edits reader and a fake session-1 runner, like
/// <see cref="CancelPendingEditsToolTests"/>.
/// </summary>
[Collection("StaticHandlerHooks")]
public class AcceptPendingEditsToolTests
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
        List<string>? verbs = null, bool ok = true, string reason = "accept_gesture_sent") =>
        (verb, ct) =>
        {
            verbs?.Add(verb);
            return Task.FromResult(new Session1ScriptResult(ok, reason, new[] { "shot1.png" }));
        };

    [Fact]
    public async Task RefusesWithoutConfirm()
    {
        var verbs = new List<string>();
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: false, Reader(RoutineState("accepted")),
            Runner(verbs), () => true, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("confirm=true", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_accept_pending_edits", err.Error, StringComparison.Ordinal);
        Assert.Contains("execute on a running controller", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task RefusesWhenAutomationDisabled_CapabilityDisabled()
    {
        var capabilities = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities);

        var json = await tools.GetAcceptPendingEditsResponseAsync(Program, Routine, confirm: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
        Assert.Contains("LGX_ENABLE_STUDIO_AUTOMATION", root.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task RefusesWhenNothingPending()
    {
        var verbs = new List<string>();
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
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
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(verbs), () => false, planLaunch: () => ("direct", null));

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("Studio 5000", err.Error, StringComparison.Ordinal);
        Assert.Null(err.Before);
        Assert.False(err.Launched);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task GestureOkButStillPending_IsError()
    {
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("pending")),
            Runner(), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Contains("state='accepted'", err.Error, StringComparison.Ordinal);
        Assert.Contains("state='pending'", err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
    }

    /// <summary>
    /// Bench S20-p102 (2026-10-01): the first after-read right after the driver returned
    /// still showed the zone while the controller was already all N. The after-state is
    /// re-read until it settles, so a late "none" is a success, not a false failure.
    /// </summary>
    [Fact]
    public async Task AfterStateSettlesOnLaterRead_IsOk_NotFalseFailure()
    {
        var reads = 0;
        var states = new[] { RoutineState("pending"), RoutineState("pending"), RoutineState("pending"), RoutineState("none") };
        Func<string, string, CancellationToken, Task<object>> reader = (p, r, ct) =>
        {
            var s = states[Math.Min(reads++, states.Length - 1)];
            return Task.FromResult<object>(new PendingEditsResult(
                true, "Run", null, new[] { s },
                new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));
        };
        var interval = StudioLifecycleHandler.AfterSettleInterval;
        StudioLifecycleHandler.AfterSettleInterval = TimeSpan.Zero;
        try
        {
            var result = await AcceptPendingEditsHandler.ExecuteAsync(
                Program, Routine, confirm: true, reader, Runner(), () => true);

            var ok = Assert.IsType<CancelPendingEditsResult>(result);
            Assert.Equal("none", ok.After.State);
            Assert.Equal(4, reads); // before + three after-reads
        }
        finally { StudioLifecycleHandler.AfterSettleInterval = interval; }
    }

    [Fact]
    public async Task AfterNone_ReturnsOkWithBeforeAfter()
    {
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(), () => true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("accepted", ok.Status);
        Assert.Equal("accepted", ok.Before.State);
        Assert.Equal("none", ok.After.State);
        Assert.Empty(ok.After.PendingRungs);
        Assert.Equal(new[] { "shot1.png" }, ok.Screenshots);
        Assert.True(ok.Launched);
    }

    [Fact]
    public async Task RunnerPreLaunchRefusal_SaysNotLaunched_LaunchedFalse()
    {
        Func<string, CancellationToken, Task<Session1ScriptResult>> refusing = (verb, ct) =>
            Task.FromResult(new Session1ScriptResult(
                false, "studio_not_in_this_session", Array.Empty<string>(),
                LaunchMode: "direct", Launched: false));

        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            refusing, () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.StartsWith("Not launched", err.Error, StringComparison.Ordinal);
        Assert.Contains("studio_not_in_this_session", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        Assert.Equal("direct", err.LaunchMode);
    }

    [Fact]
    public async Task RunnerInvokedWithVerbAccept()
    {
        var verbs = new List<string>();
        await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(verbs), () => true);

        Assert.Equal(new[] { "accept" }, verbs);
    }

    /// <summary>
    /// The runner result's gestureTier is passed through additively on the accept payload;
    /// when the runner reports none the field is absent from the JSON.
    /// </summary>
    [Fact]
    public async Task RunnerResult_PassesGestureTierThrough()
    {
        Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (verb, ct) =>
            Task.FromResult(new Session1ScriptResult(
                true, "accept_gesture_sent", new[] { "shot1.png" }, GestureTier: "posted"));

        var result = Assert.IsType<CancelPendingEditsResult>(await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            runner, () => true));

        Assert.Equal("posted", result.GestureTier);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(result)))
            Assert.Equal("posted", doc.RootElement.GetProperty("gestureTier").GetString());

        var without = Assert.IsType<CancelPendingEditsResult>(await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true,
            Reader(RoutineState("accepted"), RoutineState("none")),
            Runner(), () => true));

        Assert.Null(without.GestureTier);
        Assert.DoesNotContain("gestureTier", JsonSerializer.Serialize(without), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StudioOffline_TypedError_WithBefore()
    {
        const string driverReason = "studio_offline";
        const string expectedKind = "StudioOffline";
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(ok: false, reason: driverReason), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal("accepted", err.Before?.State);
        Assert.Contains("go online by Upload", err.Error, StringComparison.Ordinal);
        Assert.Contains("never launches Studio", err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal(expectedKind, doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task StudioNotRunning_TypedError_WithBefore()
    {
        const string driverReason = "studio_not_running";
        const string expectedKind = "StudioNotRunning";
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            Runner(ok: false, reason: driverReason), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal("accepted", err.Before?.State);
        Assert.Contains("go online by Upload", err.Error, StringComparison.Ordinal);
        Assert.Contains("never launches Studio", err.Error, StringComparison.Ordinal);
        Assert.True(err.Launched);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal(expectedKind, doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task StudioNotRunning_PreLaunch_TypedError_WithBefore()
    {
        const string expectedKind = "StudioNotRunning";
        var preLaunch = new Session1ScriptResult(
            false,
            "studio_not_running: " + StudioLifecycleHandler.StudioEngineerMessage + ". Nothing was launched.",
            Array.Empty<string>(),
            Launched: false,
            Kind: expectedKind);
        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            Program, Routine, confirm: true, Reader(RoutineState("accepted")),
            (_, _) => Task.FromResult(preLaunch), () => true);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal(expectedKind, err.Kind);
        Assert.Equal("accepted", err.Before?.State);
        Assert.Contains("go online by Upload", err.Error, StringComparison.Ordinal);
        Assert.Contains("never launches Studio", err.Error, StringComparison.Ordinal);
        Assert.False(err.Launched);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        Assert.Equal(expectedKind, doc.RootElement.GetProperty("kind").GetString());
    }
}
#endif
