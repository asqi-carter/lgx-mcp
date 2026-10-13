using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Connect progress heartbeat: while a long connect runs, a progress notification naming the current phase
/// goes out at least every 5 s, on the direct path and on the background-job path, and nothing goes out
/// when the request has no progress token. Time is a fake clock, so a 30 s connect takes no real time.
/// </summary>
public class ConnectHeartbeatTests
{
    /// <summary>Phase starts of the fake 30 s connect, in seconds from the start (gate-derivation ends at 30 s).</summary>
    private static readonly (string Phase, int AtSecond)[] Schedule =
    [
        (ConnectPhases.Upload, 0),
        (ConnectPhases.SaveL5x, 17),
        (ConnectPhases.Parse, 26),
        (ConnectPhases.GateDerivation, 29),
    ];

    private const int ConnectSeconds = 30;

    /// <summary>A clock and timer source that only moves when <see cref="Advance"/> is called.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Seconds since the clock started.</summary>
        public double Seconds => (double)Volatile.Read(ref _ticks) / TimeSpan.TicksPerSecond;

        public int ActiveTimers
        {
            get { lock (_timers) return _timers.Count(t => !t.Disposed); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, Volatile.Read(ref _ticks) + dueTime.Ticks, period.Ticks);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        /// <summary>Moves the clock forward, firing every timer that comes due on the way, in order.</summary>
        public void Advance(TimeSpan by)
        {
            var target = Volatile.Read(ref _ticks) + by.Ticks;
            while (true)
            {
                ManualTimer? next;
                lock (_timers)
                    next = _timers.Where(t => !t.Disposed && t.Due <= target).MinBy(t => t.Due);
                if (next is null) break;
                Volatile.Write(ref _ticks, Math.Max(Volatile.Read(ref _ticks), next.Due));
                next.Fire();
            }
            Volatile.Write(ref _ticks, target);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, long due, long period) : ITimer
    {
        public long Due { get; private set; } = due;
        public bool Disposed { get; private set; }

        public void Fire()
        {
            Due = period > 0 ? Due + period : long.MaxValue;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan periodTime) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    /// <summary>Records each notification with the fake time it was sent at.</summary>
    private sealed class RecordingSink(ManualTimeProvider clock) : IProgress<ProgressNotificationValue>
    {
        public List<(double At, ProgressNotificationValue Value)> Sent { get; } = [];

        public void Report(ProgressNotificationValue value)
        {
            lock (Sent) Sent.Add((clock.Seconds, value));
        }
    }

    private static void AssertHeartbeatQuality(RecordingSink sink, ManualTimeProvider clock)
    {
        var sent = sink.Sent;
        Assert.NotEmpty(sent);

        // Nothing is silent for more than 5 s, from the start of the connect to its end.
        Assert.Equal(0, sent[0].At);
        for (var i = 1; i < sent.Count; i++)
            Assert.True(sent[i].At - sent[i - 1].At <= 5.0,
                $"gap of {sent[i].At - sent[i - 1].At}s between notifications {i - 1} and {i}");
        Assert.True(ConnectSeconds - sent[^1].At <= 5.0, "the connect ended more than 5 s after the last notification");

        // Every message names a real phase from the code, and the phase is the one actually running at that time.
        foreach (var (at, value) in sent)
        {
            var phase = Assert.Single(ConnectPhases.All, p => value.Message!.StartsWith(p + ":", StringComparison.Ordinal));
            var expected = Schedule.Last(s => s.AtSecond <= at).Phase;
            Assert.Equal(expected, phase);
        }

        // MCP: progress strictly increases.
        for (var i = 1; i < sent.Count; i++)
            Assert.True(sent[i].Value.Progress > sent[i - 1].Value.Progress,
                $"progress did not increase at notification {i}");

        // Every phase is announced, and heartbeats are visible as such.
        foreach (var (phase, _) in Schedule)
            Assert.Contains(sent, s => s.Value.Message!.StartsWith(phase + ": ", StringComparison.Ordinal)
                                       && !s.Value.Message.Contains("still running"));
        Assert.Contains(sent, s => s.Value.Message!.Contains("still running"));
    }

    /// <summary>
    /// A 30 s fake connect on the direct path (reports straight into the heartbeat, as the Tasks path does)
    /// produces a notification at least every 5 s, each naming the running phase.
    /// </summary>
    [Fact]
    public void FakeConnect30s_DirectPath_ReportsAtLeastEvery5Seconds_NamingThePhase()
    {
        var clock = new ManualTimeProvider();
        var sink  = new RecordingSink(clock);
        using (var heartbeat = TasksSupport.CreateConnectProgress(sink, timeProvider: clock))
        {
            Assert.NotNull(heartbeat);
            for (var second = 0; second <= ConnectSeconds; second++)
            {
                foreach (var (phase, at) in Schedule)
                    if (at == second) heartbeat!.Report(ConnectPhases.For(phase));
                if (second < ConnectSeconds) clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        AssertHeartbeatQuality(sink, clock);
        Assert.Equal(0, clock.ActiveTimers);
    }

    /// <summary>
    /// The same 30 s fake connect, run as the background job (<see cref="ConnectState.RunAsync"/>) while the
    /// caller is still waiting: phase reports come from the job thread and the heartbeat still keeps pace.
    /// </summary>
    [Fact]
    public async Task FakeConnect30s_BackgroundJobPath_ReportsAtLeastEvery5Seconds_NamingThePhase()
    {
        var clock = new ManualTimeProvider();
        var sink  = new RecordingSink(clock);
        var state = new ConnectState();

        var tick = new SemaphoreSlim(0);   // test -> job: one fake second has passed
        var ack  = new SemaphoreSlim(0);   // job -> test: it has handled that second

        using var heartbeat = TasksSupport.CreateConnectProgress(sink, timeProvider: clock)!;

        var call = state.RunAsync(
            async job =>
            {
                for (var second = 0; second <= ConnectSeconds; second++)
                {
                    if (second > 0) await tick.WaitAsync();
                    foreach (var (phase, at) in Schedule)
                        if (at == second) job.Report(ConnectPhases.For(phase));
                    ack.Release();
                }
                return """{"status":"connected"}""";
            },
            budget: TimeSpan.FromMinutes(5),   // the caller stays attached for the whole connect
            forward: heartbeat);

        await ack.WaitAsync();   // second 0 handled
        for (var second = 1; second <= ConnectSeconds; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            tick.Release();
            await ack.WaitAsync();
        }

        Assert.Equal("""{"status":"connected"}""", await call);
        AssertHeartbeatQuality(sink, clock);
    }

    /// <summary>Once the heartbeat is disposed (the call returned) nothing more is sent, even from a late report.</summary>
    [Fact]
    public void AfterDispose_NothingIsSent()
    {
        var clock = new ManualTimeProvider();
        var sink  = new RecordingSink(clock);
        var heartbeat = new ConnectHeartbeat(sink, timeProvider: clock);
        heartbeat.Report(ConnectPhases.For(ConnectPhases.Upload));
        heartbeat.Dispose();
        var before = sink.Sent.Count;

        clock.Advance(TimeSpan.FromSeconds(20));
        heartbeat.Report(ConnectPhases.For(ConnectPhases.Parse));

        Assert.Equal(before, sink.Sent.Count);
    }

    /// <summary>A request with no progress token gets no sink, so no heartbeat is created and no timer is started.</summary>
    [Fact]
    public void NoProgressToken_NoHeartbeat_NoNotifications()
    {
        var clock = new ManualTimeProvider();

        Assert.Null(TasksSupport.CreateProgress(null));
        Assert.Null(TasksSupport.CreateConnectProgress(null, timeProvider: clock));
        Assert.Equal(0, clock.ActiveTimers);
    }

    /// <summary>No phase reported yet means nothing to name, so the heartbeat stays quiet.</summary>
    [Fact]
    public void BeforeFirstPhase_HeartbeatIsQuiet()
    {
        var clock = new ManualTimeProvider();
        var sink  = new RecordingSink(clock);
        using var heartbeat = new ConnectHeartbeat(sink, timeProvider: clock);

        clock.Advance(TimeSpan.FromSeconds(12));

        Assert.Empty(sink.Sent);
    }

    /// <summary>The default interval is under the 5 s promised to clients.</summary>
    [Fact]
    public void DefaultInterval_IsUnderFiveSeconds() =>
        Assert.True(ConnectHeartbeat.DefaultInterval < TimeSpan.FromSeconds(5));
}
