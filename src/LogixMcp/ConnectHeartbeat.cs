using ModelContextProtocol;
using ModelContextProtocol.Protocol;

/// <summary>
/// Turns connect-phase reports into MCP progress notifications and keeps them coming while a phase is
/// slow: the upload and the detailed L5X save can each take tens of seconds without any phase boundary,
/// and a client that sees no notification for that long may time the request out. Every
/// <see cref="Interval"/> the current phase is reported again (same real phase name, with the elapsed time).
/// </summary>
/// <remarks>
/// MCP requires <c>progress</c> to increase with every notification, so a heartbeat reports
/// <c>step + 0.9 * n / (n + 1)</c> for its n-th repeat in a phase: strictly increasing, and always below the
/// next phase's boundary value (<c>step + 1</c>). Dispose the instance when the tool call returns; after
/// that nothing more is sent. Create it only when the request carries a progress token (see
/// <see cref="TasksSupport.CreateConnectProgress"/>).
/// </remarks>
public sealed class ConnectHeartbeat : IProgress<ConnectProgress>, IDisposable
{
    /// <summary>Default time between heartbeats; kept under the 5 s the clients are promised.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(4);

    private readonly IProgress<ProgressNotificationValue> _sink;
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly ITimer _timer;
    private readonly object _gate = new();

    private ConnectProgress? _current;
    private int _beats;
    private float _lastProgress = float.NegativeInfinity;
    private bool _disposed;

    /// <summary>Time between heartbeats.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Starts the heartbeat clock. Nothing is sent until the first phase is reported.</summary>
    /// <param name="sink">Receives the progress notifications.</param>
    /// <param name="interval">Time between heartbeats; <see cref="DefaultInterval"/> when <c>null</c>.</param>
    /// <param name="timeProvider">Clock and timer source; <see cref="TimeProvider.System"/> when <c>null</c>.</param>
    public ConnectHeartbeat(
        IProgress<ProgressNotificationValue> sink, TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _sink      = sink ?? throw new ArgumentNullException(nameof(sink));
        Interval   = interval ?? DefaultInterval;
        if (Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        _time      = timeProvider ?? TimeProvider.System;
        _startedAt = _time.GetTimestamp();
        _timer     = _time.CreateTimer(_ => Beat(), null, Interval, Interval);
    }

    /// <summary>Reports a phase boundary and restarts the repeat count for that phase.</summary>
    /// <param name="value">The phase that is starting.</param>
    public void Report(ConnectProgress value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _current = value;
            _beats   = 0;
            _sink.Report(new ProgressNotificationValue
            {
                Progress = Increasing(value.Step),
                Total    = value.TotalSteps,
                Message  = $"{value.Phase}: {value.Message}",
            });
        }
    }

    /// <summary>MCP wants progress to rise with every notification; a phase reported again (a retry) must not repeat or lower it.</summary>
    private float Increasing(float progress)
    {
        if (progress <= _lastProgress) progress = MathF.BitIncrement(_lastProgress);
        _lastProgress = progress;
        return progress;
    }

    private void Beat()
    {
        lock (_gate)
        {
            if (_disposed || _current is not { } phase) return;
            _beats++;
            var elapsed = (int)_time.GetElapsedTime(_startedAt).TotalSeconds;
            _sink.Report(new ProgressNotificationValue
            {
                Progress = Increasing((float)(phase.Step + 0.9 * _beats / (_beats + 1))),
                Total    = phase.TotalSteps,
                Message  = $"{phase.Phase}: {phase.Message} (still running, {elapsed}s elapsed)",
            });
        }
    }

    /// <summary>Stops the heartbeat; later reports are dropped.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _timer.Dispose();
    }
}
