using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A connect waiting for the host-wide upload lease keeps waiting while the owner's connect is
/// making progress (its recorded phase or heartbeat advanced in the last 15 s), up to a 120 s ceiling, and gets
/// ConnectBusy for an owner that stalled or for the ceiling. Every test uses its own isolated <see cref="HostUploadLease"/>
/// files and a fake clock, so nothing waits in real time and no host-wide state is touched.
/// </summary>
public class UploadLeaseProgressWaitTests : IDisposable
{
    private const int OwnerPid  = 4242;
    private const int WaiterPid = 1111;

    private readonly IsolatedUploadLease _scope = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public void Dispose() => _scope.Dispose();

    private HostUploadLease NewLease(int pid, Func<TimeSpan, Task>? onDelay = null, TimeSpan? bound = null) => new(
        _scope.LeasePath, pid, isProcessAlive: _ => true,
        waitBound: bound ?? TimeSpan.FromSeconds(30), retryAfter: TimeSpan.FromSeconds(30),
        pollInterval: TimeSpan.FromSeconds(1), clock: () => _now,
        delay: async (t, _) => { _now += t; if (onDelay is not null) await onDelay(t); });

    [Fact]
    public async Task OwnerMakingProgress_FinishingAt40s_LetsTheWaiterProceed()
    {
        var start = _now;
        var finishAt = start + TimeSpan.FromSeconds(40);
        var owner = NewLease(OwnerPid).TryAcquire();
        Assert.NotNull(owner);
        owner!.StartHeartbeat(TimeSpan.FromDays(1));          // records the first heartbeat; the test beats by hand

        var waiter = NewLease(WaiterPid, onDelay: _ =>
        {
            if (_now >= finishAt) owner.Dispose();
            else if ((_now - start).TotalSeconds % 5 == 0) owner.NoteProgress();   // the owner's 5 s heartbeat
            return Task.CompletedTask;
        });
        string? outcome = null;

        using var handle = await waiter.AcquireAsync(onWaitEnd: o => outcome = o);

        Assert.Equal(LeaseWaitOutcomes.OwnerFinished, outcome);
        Assert.Equal(WaiterPid, waiter.CurrentOwner()!.Pid);
        Assert.InRange((_now - start).TotalSeconds, 40, 41);   // it waited past the 30 s base bound
    }

    [Fact]
    public async Task OwnerChangingPhase_CountsAsProgress_WithoutAHeartbeatTimer()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        var phases = new[] { "upload", "save-l5x", "parse", "gate-derivation" };
        var waiter = NewLease(WaiterPid, onDelay: _ =>
        {
            var elapsed = (_now - start).TotalSeconds;
            if (elapsed >= 60) owner.Dispose();
            else if (elapsed % 12 == 0) owner.NoteProgress(phases[(int)(elapsed / 12) % phases.Length]);
            return Task.CompletedTask;
        });

        using var handle = await waiter.AcquireAsync();

        Assert.InRange((_now - start).TotalSeconds, 60, 61);
    }

    [Fact]
    public async Task StalledOwner_GivesConnectBusy_AfterTheStallWindow()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        owner.StartHeartbeat(TimeSpan.FromDays(1));            // one heartbeat at t=0, then the owner goes silent
        var waiter = NewLease(WaiterPid, bound: TimeSpan.FromSeconds(10));
        string? outcome = null;

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync(onWaitEnd: o => outcome = o));

        // Past the 10 s bound the owner's last record is the only thing keeping the wait alive; it ages out at 15 s.
        var waited = (_now - start).TotalSeconds;
        Assert.InRange(waited, 15, 17);
        Assert.Equal(LeaseWaitOutcomes.Timeout, outcome);
        Assert.Equal(OwnerPid, ex.Owner!.Pid);
        Assert.Equal(TagServiceErrorKind.ConnectBusy, ex.Error.Kind);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task OwnerThatNeverRecordsProgress_GetsConnectBusy_AtTheBaseBound()
    {
        var start = _now;
        using var owner = NewLease(OwnerPid).TryAcquire();
        var waiter = NewLease(WaiterPid);

        await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync());

        Assert.Equal(30, (_now - start).TotalSeconds);
    }

    [Fact]
    public async Task Ceiling_HoldsAt120s_EvenWhileTheOwnerKeepsMakingProgress()
    {
        var start = _now;
        var owner = NewLease(OwnerPid).TryAcquire()!;
        var waiter = NewLease(WaiterPid, onDelay: _ =>
        {
            owner.NoteProgress("upload");                     // a heartbeat on every poll: never stalled
            return Task.CompletedTask;
        });
        string? outcome = null;

        var ex = await Assert.ThrowsAsync<ConnectBusyException>(() => waiter.AcquireAsync(onWaitEnd: o => outcome = o));

        Assert.Equal(120, (_now - start).TotalSeconds);
        Assert.Contains("waited 120 s", ex.Error.Message);
        Assert.Equal(LeaseWaitOutcomes.Timeout, outcome);
        Assert.Equal("upload", ex.Owner!.Phase);
        GC.KeepAlive(owner);
    }

    [Fact]
    public async Task FreeLease_ReportsNoWaitEnd()
    {
        var waiter = NewLease(WaiterPid);
        var ended = false;

        using var handle = await waiter.AcquireAsync(onWaitEnd: _ => ended = true);

        Assert.False(ended);                                   // no wait began, so none ended
    }

    [Fact]
    public void NoteProgress_ByAReplacedOwner_DoesNotOverwriteTheNewOwnersLease()
    {
        var crashedLease = NewLease(OwnerPid);
        var crashed = crashedLease.TryAcquire()!;
        var alive = true;
        var takeover = new HostUploadLease(_scope.LeasePath, WaiterPid, isProcessAlive: p => p != OwnerPid || alive,
            clock: () => _now);
        alive = false;                                          // the first owner's process is gone
        using var mine = takeover.TryAcquire();
        Assert.NotNull(mine);

        crashed.NoteProgress("upload");                         // late write from the dead owner's handle

        Assert.Equal(WaiterPid, takeover.CurrentOwner()!.Pid);
        Assert.Null(takeover.CurrentOwner()!.Phase);
    }

    [Fact]
    public void LeaseWaitPhase_ReportsOwnerFinished_WhenTheUploadWentOn()
    {
        var recorder = new ConnectPhaseRecorder();
        recorder.Report(ConnectPhases.LeaseWaitFor(OwnerPid));
        recorder.Report(ConnectPhases.LeaseWaitEnded(LeaseWaitOutcomes.OwnerFinished));
        recorder.Report(ConnectPhases.For(ConnectPhases.Upload));

        var wait = recorder.Snapshot().LeaseWait;

        Assert.Equal(LeaseWaitOutcomes.OwnerFinished, wait!.Outcome);
        Assert.Equal(OwnerPid, wait.OwnerPid);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(recorder.Snapshot()));
        Assert.Equal("owner-finished", json.RootElement.GetProperty("lease-wait").GetProperty("outcome").GetString());
    }

    [Fact]
    public void LeaseWaitPhase_ReportsTimeout_WhenTheWaitGaveUp()
    {
        var recorder = new ConnectPhaseRecorder();
        recorder.Report(ConnectPhases.LeaseWaitFor(OwnerPid));
        recorder.Report(ConnectPhases.LeaseWaitEnded(LeaseWaitOutcomes.Timeout));

        var wait = recorder.Snapshot().LeaseWait;

        Assert.Equal(LeaseWaitOutcomes.Timeout, wait!.Outcome);
    }
}
