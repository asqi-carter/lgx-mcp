#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A publish on Logix Designer v36: the SDK cannot run the build check there, which
/// is not a build failure. The publish continues to the normal post-import verification and the
/// result says the build check was unavailable.
/// </summary>
public class PublishV36Tests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string UnsupportedMessage = "Operation not supported on Logix Designer version 36.5.";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    [Fact]
    public async Task BuildUnsupported_ControllerTarget_ReturnsProposedWithBuildUnavailable()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new OperationFailedException(UnsupportedMessage, "OPERATION_NOT_SUPPORTED");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", ok.Status);
        Assert.Equal("unavailable", ok.BuildVerify);
        Assert.Equal("36.5", ok.DesignerVersion);
        Assert.Contains(DesignerVersionInfo.BuildUnavailableNote, ok.Note);
        Assert.Equal("verified", ok.Verification);           // pending census still ran
        Assert.Equal(0, fake.GoOfflineCallCount);            // no rollback claim, no GoOffline
        Assert.Equal(1, fake.BuildCallCount);
        Assert.Equal("36.5", session.DesignerVersion?.ToString());

        var json = JsonSerializer.Serialize(ok);
        Assert.Contains("\"buildVerify\":\"unavailable\"", json);
        Assert.Contains("\"designerVersion\":\"36.5\"", json);
    }

    [Fact]
    public async Task BuildUnsupported_LiveTarget_RunsLiveVerification()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException(UnsupportedMessage);

        var fragment = ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", "XIC(LiveIn)OTE(LiveOut);");
        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, fragment, session, policy: null, target: "live");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("live", ok.Status);
        Assert.Equal("unavailable", ok.BuildVerify);
        Assert.Equal("verified", ok.Verification);           // live re-export verification ran
        Assert.True(ok.Finalized);
        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    [Fact]
    public async Task BuildUnsupported_SecondPublish_SkipsTheBuildCall()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException(UnsupportedMessage);

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, target: "controller");
        Assert.Equal(1, fake.BuildCallCount);
        // The first publish's zone is resolved (accepted or cancelled) before the second one: one publish per cycle.
        fake.ExportContent = fake.ExportContent!.Replace("Type=\"I\"", "Type=\"N\"");

        var again = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", "XIC(A)OTE(B);"),
            session, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(again);
        Assert.Equal("unavailable", ok.BuildVerify);
        Assert.Equal(1, fake.BuildCallCount);                // known below v37: not called again
    }

    [Fact]
    public async Task BuildPassed_ReportsPassed()
    {
        var (session, _) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, target: "controller");

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("passed", ok.BuildVerify);
        Assert.DoesNotContain(DesignerVersionInfo.BuildUnavailableNote, ok.Note ?? string.Empty);
    }

    [Fact]
    public async Task BuildUnsupported_AuditRecordsBuildVerifyAndDesignerVersion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"audit-v36-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (session, fake) = await OpenAsync();
            fake.ThrowOnBuild = new InvalidOperationException(UnsupportedMessage);

            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, target: "controller");

            var rec = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .First(e => e.GetProperty("event").GetString() == "rung_edit"
                            && e.GetProperty("outcome").GetString() != "sending");
            Assert.Equal("proposed", rec.GetProperty("outcome").GetString());
            Assert.Equal("unavailable", rec.GetProperty("build_verify").GetString());
            Assert.Equal("36.5", rec.GetProperty("designer_version").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
#endif
