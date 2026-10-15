#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

using RequestedBuildTarget = RockwellAutomation.LogixDesigner.LogixProject.RequestedBuildTarget;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The replay-copy phase of a verifyOnStage stage reports a
/// <c>replay-copy/&lt;step&gt;</c> sub-phase for each distinct SDK or IO step. Instrumentation only: the seven
/// phases, their names and their order are unchanged.
/// </summary>
public class ReplayCopySubPhaseTests
{
    private const string StagedRung = "XIC(A)OTE(B);";

    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>A clock that only moves when advanced.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>Raises a build log event's worth of time when the SDK build streams its log.</summary>
    private sealed class BuildClockEvent(Action onBuild) : IOperationEvent
    {
        public void LogProgress(bool logProgressNew) { }
        public void Progress(string projectFile, int value) { }
        public void LogStatus(bool logStatusNew) { }
        public void LogError(bool logErrorNew) { }
        public void Status(string projectFile, string msg) => onBuild();
        public void Error(string projectFile, string msg) { }
    }

    private static string ExportWith(string rung) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{rung}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static (LogixProjectSession Session, ProposeRungEditRecordingFake Online) Open(
        ManualClock clock, TimeSpan open, TimeSpan verify, TimeSpan build)
    {
        var online = new ProposeRungEditRecordingFake { ExportContent = ExportWith(StagedRung) };
        LogixProjectSession? session = null;
        session = new LogixProjectSession((path, _) =>
        {
            if (path == "dummy.ACD") return Task.FromResult<ILogixProject>(online);
            clock.Advance(open);                                       // opening the copy
            var copy = new ProposeRungEditRecordingFake { ExportContent = File.ReadAllText(path) };
            copy.OnExport = _ => clock.Advance(verify);                // reading the copy back for the staged rung
            copy.BuildLogEvents.Add((false, "Build started"));         // streamed to the build handler
            return Task.FromResult<ILogixProject>(copy);
        });
        session.OpenAsync("dummy.ACD").GetAwaiter().GetResult();
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, online);
    }

    private static Task<OfflineBuildOutcome> RunAsync(
        LogixProjectSession session, StagePhaseTimer timer, ManualClock clock, TimeSpan import, TimeSpan build) =>
        OfflineBuildCheck.RunAsync(
            session, RequestedBuildTarget.DefaultTarget,
            [StagedRung], [ValidXPath],
            new BuildClockEvent(() => clock.Advance(build)),
            CancellationToken.None,
            prepareCopy: (_, _) => { clock.Advance(import); return Task.FromResult(true); },
            phases: timer);

    [Fact]
    public async Task ReplayCopy_WithControlledStepDelays_ReportsSubPhasesInOrder_AndTheyFitInsideTheParent()
    {
        var clock = new ManualClock();
        var (session, _) = Open(clock, open: TimeSpan.FromSeconds(3), verify: TimeSpan.FromSeconds(2), build: TimeSpan.FromSeconds(7));
        var timer = new StagePhaseTimer(clock);

        var outcome = await RunAsync(session, timer, clock, import: TimeSpan.FromSeconds(5), build: TimeSpan.FromSeconds(7));
        Assert.Null(outcome.UnavailableReason);

        var subs = timer.SubPhases;
        Assert.Equal(
            new[] { "open-copy", "verify-staged", "import", "build", "close", "delete-copy" }
                .Select(StagePhases.ReplayCopyStep).ToArray(),
            subs.Select(s => s.Phase).ToArray());
        Assert.Equal(3000, subs[0].DurationMs);
        Assert.Equal(2000, subs[1].DurationMs);
        Assert.Equal(5000, subs[2].DurationMs);
        Assert.Equal(7000, subs[3].DurationMs);
        for (var i = 1; i < subs.Count; i++)
            Assert.True(subs[i].StartedMs >= subs[i - 1].StartedMs + subs[i - 1].DurationMs, "sub-phases run one after another");

        var parent = timer.Snapshot().Single(t => t.Phase == StagePhases.ReplayCopy);
        Assert.True(subs.Sum(s => s.DurationMs) <= parent.DurationMs,
            $"sub-phases summed to {subs.Sum(s => s.DurationMs)} ms, parent {parent.DurationMs} ms");
        Assert.True(subs[0].StartedMs >= parent.StartedMs);
        Assert.True(subs[^1].StartedMs + subs[^1].DurationMs <= parent.StartedMs + parent.DurationMs);
        Assert.Equal(17000, parent.DurationMs);   // the parent still spans the whole replay
    }

    [Fact]
    public async Task ReplayCopy_SubPhases_DoNotChangeTheExistingPhaseNamesOrOrder()
    {
        var clock = new ManualClock();
        var (session, _) = Open(clock, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var timer = new StagePhaseTimer(clock);

        await RunAsync(session, timer, clock, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        timer.Enter(StagePhases.OnlineImport);

        // The default snapshot is exactly the seven phases, as before.
        Assert.Equal(StagePhases.All, timer.Snapshot().Select(t => t.Phase).ToArray());

        // Opting in nests the sub-phases right after replay-copy; the seven phases keep their order.
        var nested = timer.Snapshot(includeSubPhases: true);
        Assert.Equal(StagePhases.All, nested.Select(t => t.Phase).Where(p => !p.Contains('/')).ToArray());
        var at = nested.ToList().FindIndex(t => t.Phase == StagePhases.ReplayCopy);
        Assert.StartsWith(StagePhases.ReplayCopySubPhasePrefix, nested[at + 1].Phase);
        Assert.Equal(StagePhases.OnlineImport, nested[at + 1 + timer.SubPhases.Count].Phase);
    }

    [Fact]
    public async Task ReplayCopy_SaveAsFailure_RecordsNoSubPhases()
    {
        var clock = new ManualClock();
        var (session, online) = Open(clock, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        online.ThrowOnSaveAs = new OperationFailedException("cannot save", "SAVE_FAILED");
        var timer = new StagePhaseTimer(clock);

        var outcome = await RunAsync(session, timer, clock, TimeSpan.Zero, TimeSpan.Zero);

        Assert.NotNull(outcome.UnavailableReason);
        Assert.Empty(timer.SubPhases);
    }
}
#endif
