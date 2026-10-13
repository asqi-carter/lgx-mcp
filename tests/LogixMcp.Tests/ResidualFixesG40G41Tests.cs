#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Residual fixes G40 (a reference to a program that does not exist is an <c>UnknownProgram</c> warning at stage
/// time, and a typed refusal under verifyOnStage) and G41 (the connect's phase timings carry a <c>lease-wait</c>
/// phase when the host-wide upload lease made it wait).
/// </summary>
public class ResidualFixesG40G41Tests : IDisposable
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string TagsXml =
        "<RSLogix5000Content><Controller Name=\"C\"><Tags>" +
        "<Tag Name=\"Lamp\" TagType=\"Base\" DataType=\"BOOL\"/></Tags></Controller></RSLogix5000Content>";

    private const string ProgramsXml =
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Tags/><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"/></Routines></Program>" +
        "<Program Name=\"Line_A\"><Tags><Tag Name=\"P6_Loc\" DataType=\"BOOL\" Usage=\"Public\"/></Tags>" +
        "<Routines><Routine Name=\"R\" Type=\"RLL\"/></Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"g40g41-{Guid.NewGuid():N}");

    public ResidualFixesG40G41Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        fake.ExportContentByXPath["Controller/Tags"] = TagsXml;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] = ProgramsXml;
        fake.ExportContentByXPath["Controller/AddOnInstructionDefinitions"] =
            "<RSLogix5000Content><Controller><AddOnInstructionDefinitions/></Controller></RSLogix5000Content>";
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, fake);
    }

    private static Task<object> StageAsync(LogixProjectSession session, string rung, bool verifyOnStage) =>
        ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", rung), session,
            verifyOnStage: verifyOnStage);

    // ── G40 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownProgram_StageWarns_NamingRungAndProgram()
    {
        var (session, fake) = await OpenAsync();

        var ok = Assert.IsType<ProposeRungEditResult>(await StageAsync(session, "XIC(\\Line_Z.P6_Loc)OTE(Lamp);", verifyOnStage: false));

        Assert.Single(fake.CapturedImportOptions);   // let through: only a warning without verifyOnStage
        Assert.NotNull(ok.Warnings);
        var warning = Assert.Single(ok.Warnings!, w => w.StartsWith("UnknownProgram", StringComparison.Ordinal));
        Assert.Contains("rung 0", warning, StringComparison.Ordinal);
        Assert.Contains("Line_Z", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownProgram_VerifyOnStage_IsATypedRefusal_NothingStaged()
    {
        var (session, fake) = await OpenAsync();

        var error = Assert.IsType<ProposeRungEditError>(await StageAsync(session, "XIC(\\Line_Z.P6_Loc)OTE(Lamp);", verifyOnStage: true));

        Assert.Equal("UnknownProgram", error.Kind);
        Assert.Contains("Line_Z", error.Error, StringComparison.Ordinal);
        Assert.Contains("rung 0", error.Error, StringComparison.Ordinal);
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(session.StagedRungBuffer);
    }

    [Fact]
    public async Task ExistingProgram_IsNotFlagged()
    {
        var (session, _) = await OpenAsync();

        var ok = Assert.IsType<ProposeRungEditResult>(await StageAsync(session, "XIC(\\Line_A.P6_Loc)OTE(Lamp);", verifyOnStage: false));

        Assert.True(ok.Warnings is null || ok.Warnings.All(w => !w.StartsWith("UnknownProgram", StringComparison.Ordinal)));
    }

    // ── G41 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectLeaseWait_IsReportedInPhaseTimings_WithDurationAndOwnerPid()
    {
        var path   = Path.Combine(_dir, "upload.lease");
        var holder = new HostUploadLease(path, pid: 4242, isProcessAlive: _ => true);
        var held   = holder.TryAcquire();
        Assert.NotNull(held);

        // The delay stands in for the wait: the owner finishes during the first poll.
        var waiter = new HostUploadLease(path, pid: 1111, isProcessAlive: _ => true,
            delay: async (t, _) => { await Task.Delay(60); held!.Dispose(); });
        var recorder = new ConnectPhaseRecorder();

        using (await waiter.AcquireAsync(onWait: owner => recorder.Report(ConnectPhases.LeaseWaitFor(owner?.Pid))))
        {
            recorder.Report(ConnectPhases.For(ConnectPhases.Upload));
            recorder.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
        }
        var timings = recorder.Snapshot();

        Assert.NotNull(timings.LeaseWait);
        Assert.Equal(4242, timings.LeaseWait!.OwnerPid);
        Assert.InRange(timings.LeaseWait.DurationMs, 40, 5000);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(timings));
        var leaseWait = json.RootElement.GetProperty("lease-wait");
        Assert.Equal(4242, leaseWait.GetProperty("ownerPid").GetInt32());
        Assert.True(leaseWait.GetProperty("durationMs").GetInt64() >= 40);
    }

    [Fact]
    public async Task ConnectLeaseWait_IsAbsent_WhenTheLeaseWasFree()
    {
        var lease = new HostUploadLease(Path.Combine(_dir, "free.lease"), pid: 1111, isProcessAlive: _ => true);
        var recorder = new ConnectPhaseRecorder();
        var waited = false;

        using (await lease.AcquireAsync(onWait: _ => waited = true))
            recorder.Report(ConnectPhases.For(ConnectPhases.Upload));
        var timings = recorder.Snapshot();

        Assert.False(waited);
        Assert.Null(timings.LeaseWait);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(timings));
        Assert.False(json.RootElement.TryGetProperty("lease-wait", out _));
    }

    [Fact]
    public void ConnectState_PhaseList_IncludesLeaseWait_WithOwnerPid()
    {
        // The status-poll list: lease-wait is a phase entry that ends when the upload phase starts.
        var progress = ConnectPhases.LeaseWaitFor(4242);
        Assert.Equal(ConnectPhases.LeaseWait, progress.Phase);
        Assert.Equal(4242, progress.OwnerPid);
        Assert.DoesNotContain(ConnectPhases.LeaseWait, ConnectPhases.All);
    }
}
#endif
