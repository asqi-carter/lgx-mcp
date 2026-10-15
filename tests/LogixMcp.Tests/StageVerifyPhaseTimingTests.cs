#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A passing verifyOnStage replay is the only offline copy cycle
/// (the post-import build-check copy is skipped and reported as <c>skipped-replay-passed</c>), a failing replay is
/// unchanged, verifyOnStage results carry <c>phaseTimings</c>, and connect removes stale offline copies.
/// </summary>
public class StageVerifyPhaseTimingTests : IDisposable
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lgx-stale-copy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Harness
    {
        public required LogixProjectSession Session { get; init; }
        public required ProposeRungEditRecordingFake Online { get; init; }
        public List<ProposeRungEditRecordingFake> Copies { get; } = new();
        public Action<ProposeRungEditRecordingFake>? ConfigureCopy { get; set; }
    }

    private static async Task<Harness> OpenAsync()
    {
        var online = new ProposeRungEditRecordingFake();
        Harness? h = null;
        var session = new LogixProjectSession((path, _) =>
        {
            if (h is null || path == "dummy.ACD")
                return Task.FromResult<ILogixProject>(online);
            var copy = new ProposeRungEditRecordingFake { ExportContent = File.ReadAllText(path) };
            h.ConfigureCopy?.Invoke(copy);
            h.Copies.Add(copy);
            return Task.FromResult<ILogixProject>(copy);
        });
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        h = new Harness { Session = session, Online = online };
        return h;
    }

    private static Task<object> StageAsync(Harness h, bool verifyOnStage, uint position = 0) =>
        ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, position, 0, ProposeRungEditHandler.MinimalNopFragment, h.Session,
            policy: null, target: "workstation", verifyOnStage: verifyOnStage);

    [Fact]
    public async Task VerifyOnStage_PassingReplay_GivesExactlyOneCopyCycle_AndSkippedReplayPassed()
    {
        var h = await OpenAsync();

        var result = Assert.IsType<ProposeRungEditResult>(await StageAsync(h, verifyOnStage: true));

        Assert.True(result.Verified);
        Assert.Equal("skipped-replay-passed", result.PostImportBuildCheck);
        Assert.Equal("passed", result.BuildVerify);
        Assert.Single(h.Online.SaveAsPaths);       // one save-as: the replay's
        var copy = Assert.Single(h.Copies);        // one copy opened and built
        Assert.Equal(1, copy.BuildCallCount);
        Assert.Equal(0, h.Online.BuildCallCount);
    }

    [Fact]
    public async Task VerifyOnStage_Off_StillRunsThePostImportBuildCheck()
    {
        var h = await OpenAsync();
        h.Online.ExportContent = ExportWith("XIC(A)OTE(B);");

        var result = Assert.IsType<ProposeRungEditResult>(await StageAsync(h, verifyOnStage: false));

        Assert.Null(result.PostImportBuildCheck);
        Assert.Null(result.PhaseTimings);
        Assert.Single(h.Online.SaveAsPaths);       // the post-import check's copy
    }

    private static string ExportWith(string rung) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{rung}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    [Fact]
    public async Task VerifyOnStage_FailingReplay_IsUnchanged_OneCopyAndStageVerifyFailed()
    {
        var h = await OpenAsync();
        h.ConfigureCopy = c =>
        {
            c.BuildLogEvents.Add((true, "Error: MainProgram/MainRoutine, Rung 0: Tag 'Undefined_Tag' is not defined (error 1234)"));
            c.ThrowOnBuild = new OperationFailedException("Build failed with 1 error", "BUILD_FAILED");
        };

        var error = Assert.IsType<ProposeRungEditError>(await StageAsync(h, verifyOnStage: true));

        Assert.Equal("StageVerifyFailed", error.Kind);
        Assert.NotNull(error.VerifyRejectReasons);
        Assert.Single(h.Online.SaveAsPaths);
        Assert.Empty(h.Online.CapturedXPaths);   // nothing was imported online
    }

    [Fact]
    public async Task VerifyOnStage_ReportsSevenPhaseTimingsInOrder()
    {
        var h = await OpenAsync();

        var result = Assert.IsType<ProposeRungEditResult>(await StageAsync(h, verifyOnStage: true));

        var timings = Assert.IsAssignableFrom<IReadOnlyList<PhaseTiming>>(result.PhaseTimings);
        // Sub-phases (replay-copy/<step>) follow replay-copy; the seven top-level phases are unchanged.
        Assert.Equal(
            ["staged-merge", "image-export", "save-as", "replay-copy", "online-import", "post-import-check", "cleanup"],
            timings.Select(t => t.Phase).Where(p => !p.Contains('/')).ToArray());
        Assert.All(timings, t => { Assert.True(t.StartedMs >= 0); Assert.True(t.DurationMs >= 0); });
        for (var i = 1; i < timings.Count; i++)
            Assert.True(timings[i].StartedMs >= timings[i - 1].StartedMs);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var first = doc.RootElement.GetProperty("phaseTimings")[0];
        Assert.Equal("staged-merge", first.GetProperty("phase").GetString());
        Assert.True(first.TryGetProperty("startedMs", out _));
        Assert.True(first.TryGetProperty("durationMs", out _));
    }

    /// <summary>A clock that only moves when advanced.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
    }

    [Fact]
    public async Task VerifyOnStage_WithRungsStaged_PhaseDurationsSumToWithinOneSecondOfWallClock()
    {
        var h     = await OpenAsync();
        var clock = new ManualClock();
        h.Session.StageClock = clock;
        // Reading the session image costs 2 s per export; the offline replay costs 12 s.
        h.Online.OnExport = _ => clock.Advance(TimeSpan.FromSeconds(2));
        h.Session.StageVerifyReplay = (_, _, _, _, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(12));
            return Task.FromResult(new ImportRejectReplayOutcome([], null));
        };

        // Rungs already staged by an earlier call.
        Assert.IsType<ProposeRungEditResult>(await StageAsync(h, verifyOnStage: false));
        Assert.True(h.Session.HasStagedRungEdit);

        var before = clock.Now;
        var result = Assert.IsType<ProposeRungEditResult>(await StageAsync(h, verifyOnStage: true, position: 1));
        var wallMs = (clock.Now - before).TotalMilliseconds;

        var timings = Assert.IsAssignableFrom<IReadOnlyList<PhaseTiming>>(result.PhaseTimings);
        var summed = timings.Sum(t => t.DurationMs);
        Assert.True(Math.Abs(summed - wallMs) <= 1000, $"phases summed to {summed} ms, wall clock {wallMs} ms");
        Assert.True(wallMs >= 12000);
        Assert.True(timings.Single(t => t.Phase == StagePhases.ReplayCopy).DurationMs >= 12000);
        Assert.True(timings.Single(t => t.Phase == StagePhases.StagedMerge).DurationMs >= 2000,
            "the work before the replay (reading the staged image) must be reported as staged-merge");
    }

    [Fact]
    public async Task VerifyOnStage_FailingReplay_ResultIncludesPhaseTimings()
    {
        var h = await OpenAsync();
        h.ConfigureCopy = c =>
        {
            c.BuildLogEvents.Add((true, "Error: MainProgram/MainRoutine, Rung 0: Tag 'Undefined_Tag' is not defined (error 1234)"));
            c.ThrowOnBuild = new OperationFailedException("Build failed with 1 error", "BUILD_FAILED");
        };

        var error = Assert.IsType<ProposeRungEditError>(await StageAsync(h, verifyOnStage: true));

        Assert.Equal("StageVerifyFailed", error.Kind);
        var timings = Assert.IsAssignableFrom<IReadOnlyList<PhaseTiming>>(error.PhaseTimings);
        Assert.Equal(StagePhases.All, timings.Select(t => t.Phase).Where(p => !p.Contains('/')).ToArray());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(error));
        Assert.Equal(StagePhases.All.Count, doc.RootElement.GetProperty("phaseTimings").EnumerateArray()
            .Count(e => !e.GetProperty("phase").GetString()!.Contains('/')));
    }

    [Fact]
    public void StagePhaseTimer_NeverRunPhases_AreReportedWithZeroDuration()
    {
        var timer = new StagePhaseTimer();
        timer.Enter(StagePhases.OnlineImport);

        var timings = timer.Snapshot();

        Assert.Equal(StagePhases.All, timings.Select(t => t.Phase).ToArray());
        Assert.Equal(0, timings[0].DurationMs);
    }

    private string MakeCopy(string name, TimeSpan age, bool locked = false, List<FileStream>? holders = null)
    {
        var dir  = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "verify-copy.ACD");
        File.WriteAllText(file, "x");
        if (locked) holders!.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public void StaleCopyCleanup_SkipsLockedAndRecentCopies_AndReportsTheRemovedCount()
    {
        var holders = new List<FileStream>();
        try
        {
            var stale1  = MakeCopy("aaaa", TimeSpan.FromHours(30));
            var stale2  = MakeCopy("bbbb", TimeSpan.FromHours(48));
            var recent  = MakeCopy("cccc", TimeSpan.FromHours(1));
            var locked  = MakeCopy("dddd", TimeSpan.FromHours(30), locked: true, holders);

            var removed = OfflineBuildCheck.CleanStaleCopies(_root, TimeSpan.FromHours(24), DateTime.UtcNow);

            Assert.Equal(2, removed);
            Assert.False(Directory.Exists(stale1));
            Assert.False(Directory.Exists(stale2));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(locked));
        }
        finally { foreach (var f in holders) f.Dispose(); }
    }

    [Fact]
    public void StaleCopyCleanup_MissingFolder_RemovesNothing() =>
        Assert.Equal(0, OfflineBuildCheck.CleanStaleCopies(
            Path.Combine(_root, "absent"), TimeSpan.FromHours(24), DateTime.UtcNow));
}
#endif
