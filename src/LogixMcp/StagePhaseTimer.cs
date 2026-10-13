/// <summary>
/// Names of the phases a <c>verifyOnStage</c> stage reports in <c>phaseTimings</c>, in the order they run.
/// </summary>
public static class StagePhases
{
    /// <summary>
    /// Everything before the image export: resolving names, reading the staged session image, merging this call
    /// with the rungs and tags already staged, and the pending-edit census. Grows with the staged set.
    /// </summary>
    public const string StagedMerge = "staged-merge";

    /// <summary>Exporting the session image's staged declarations into the file the replay imports.</summary>
    public const string ImageExport = "image-export";

    /// <summary>Saving the offline copy of the session project (SDK save-as).</summary>
    public const string SaveAs = "save-as";

    /// <summary>Opening the copy, importing the fragment into it and building it.</summary>
    public const string ReplayCopy = "replay-copy";

    /// <summary>Prefix of a sub-phase of <see cref="ReplayCopy"/>: <c>replay-copy/&lt;step&gt;</c>.</summary>
    public const string ReplayCopySubPhasePrefix = ReplayCopy + "/";

    /// <summary>Builds the name of a replay-copy sub-phase, <c>replay-copy/&lt;step&gt;</c>.</summary>
    /// <param name="step">The step name (for example <c>open-copy</c>).</param>
    /// <returns>The sub-phase name.</returns>
    public static string ReplayCopyStep(string step) => ReplayCopySubPhasePrefix + step;

    /// <summary>The online (workstation) import of the fragment.</summary>
    public const string OnlineImport = "online-import";

    /// <summary>The state read-back and the post-import build check (skipped when the replay passed).</summary>
    public const string PostImportCheck = "post-import-check";

    /// <summary>Deleting the temporary files the stage created.</summary>
    public const string Cleanup = "cleanup";

    /// <summary>Every phase, in reporting order.</summary>
    public static readonly IReadOnlyList<string> All =
        [StagedMerge, ImageExport, SaveAs, ReplayCopy, OnlineImport, PostImportCheck, Cleanup];
}

/// <summary>
/// Times the phases of a <c>verifyOnStage</c> stage. Phases run one after another: <see cref="Enter"/> ends the
/// phase that was running and starts the next. <see cref="Snapshot"/> reports all seven phases in
/// <see cref="StagePhases.All"/> order in the same shape as connect's <see cref="PhaseTiming"/>; a phase that
/// never ran is reported with a zero duration starting where the previous phase ended. Measurement only.
/// </summary>
public sealed class StagePhaseTimer
{
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly Dictionary<string, (long Start, long End)> _spans = [];
    private readonly object _gate = new();
    private readonly List<(string Name, long Start, long End)> _subSpans = [];
    private string? _current;
    private int _currentSub = -1;

    /// <summary>Starts the clock.</summary>
    /// <param name="timeProvider">Clock source; <see cref="TimeProvider.System"/> when <c>null</c>.</param>
    public StagePhaseTimer(TimeProvider? timeProvider = null)
    {
        _time           = timeProvider ?? TimeProvider.System;
        _startTimestamp = _time.GetTimestamp();
    }

    private long ElapsedMs() => (long)_time.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    /// <summary>Ends the running phase and starts <paramref name="phase"/>. A phase entered twice keeps its first start.</summary>
    /// <param name="phase">A <see cref="StagePhases"/> value.</param>
    public void Enter(string phase)
    {
        lock (_gate)
        {
            var now = ElapsedMs();
            EndCurrent(now);
            _current = phase;
            if (!_spans.ContainsKey(phase)) _spans[phase] = (now, now);
        }
    }

    /// <summary>Ends the running phase, if any.</summary>
    public void End()
    {
        lock (_gate) EndCurrent(ElapsedMs());
    }

    /// <summary>
    /// Starts a sub-phase of the running phase (<c>replay-copy/&lt;step&gt;</c>), ending the sub-phase that was running.
    /// Sub-phases never alter <see cref="Snapshot()"/>; read them with <see cref="SubPhases"/> or
    /// <see cref="Snapshot(bool)"/>.
    /// </summary>
    /// <param name="name">Full sub-phase name, normally from <see cref="StagePhases.ReplayCopyStep"/>.</param>
    public void EnterSubPhase(string name)
    {
        lock (_gate)
        {
            var now = ElapsedMs();
            EndCurrentSub(now);
            _subSpans.Add((name, now, now));
            _currentSub = _subSpans.Count - 1;
        }
    }

    /// <summary>Ends the running sub-phase, if any.</summary>
    public void EndSubPhase()
    {
        lock (_gate) EndCurrentSub(ElapsedMs());
    }

    private void EndCurrentSub(long now)
    {
        if (_currentSub < 0) return;
        var span = _subSpans[_currentSub];
        _subSpans[_currentSub] = (span.Name, span.Start, Math.Max(span.End, now));
        _currentSub = -1;
    }

    /// <summary>
    /// The sub-phases recorded so far, in the order they ran, each with <c>startedMs</c> and <c>durationMs</c> on the
    /// same clock as <see cref="Snapshot()"/>. Ends the running sub-phase.
    /// </summary>
    public IReadOnlyList<PhaseTiming> SubPhases
    {
        get
        {
            lock (_gate)
            {
                EndCurrentSub(ElapsedMs());
                return _subSpans.Select(s => new PhaseTiming(s.Name, s.Start, Math.Max(0, s.End - s.Start))).ToList();
            }
        }
    }

    /// <summary>
    /// Like <see cref="Snapshot()"/>, optionally with each recorded sub-phase listed right after its parent phase.
    /// The default (<paramref name="includeSubPhases"/> <c>false</c>) is exactly <see cref="Snapshot()"/>.
    /// </summary>
    /// <param name="includeSubPhases">Whether to nest the <c>replay-copy/&lt;step&gt;</c> entries after <c>replay-copy</c>.</param>
    /// <returns>The timings.</returns>
    public IReadOnlyList<PhaseTiming> Snapshot(bool includeSubPhases)
    {
        var phases = Snapshot();
        if (!includeSubPhases) return phases;
        var subs   = SubPhases;
        var result = new List<PhaseTiming>(phases.Count + subs.Count);
        foreach (var p in phases)
        {
            result.Add(p);
            if (p.Phase == StagePhases.ReplayCopy) result.AddRange(subs);
        }
        return result;
    }

    private void EndCurrent(long now)
    {
        if (_current is null) return;
        EndCurrentSub(now);
        var span = _spans[_current];
        _spans[_current] = (span.Start, Math.Max(span.End, now));
        _current = null;
    }

    /// <summary>The seven phases in order, each with <c>startedMs</c> and <c>durationMs</c>; ends the running phase.</summary>
    /// <returns>The timings.</returns>
    public IReadOnlyList<PhaseTiming> Snapshot()
    {
        lock (_gate)
        {
            EndCurrent(ElapsedMs());
            var result = new List<PhaseTiming>(StagePhases.All.Count);
            long cursor = 0;
            foreach (var phase in StagePhases.All)
            {
                if (_spans.TryGetValue(phase, out var span))
                {
                    var start = Math.Max(cursor, span.Start);
                    result.Add(new PhaseTiming(phase, start, Math.Max(0, span.End - span.Start)));
                    cursor = Math.Max(cursor, span.End);
                }
                else
                    result.Add(new PhaseTiming(phase, cursor, 0));
            }
            return result;
        }
    }
}
