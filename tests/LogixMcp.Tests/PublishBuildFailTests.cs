#if LOGIX_SDK
using System.Text.RegularExpressions;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A build check that really fails after an import must report what was observed.
/// A controller (AcceptEdits) or live (FinalizeEdits) import cannot be rolled back by going
/// offline, so no GoOffline is made and the pending rungs are read back; the workstation path
/// keeps its GoOffline rollback and uses the "nothing pending" wording only after it succeeded.
/// </summary>
public class PublishBuildFailTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

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

    private static string Fragment(string text) =>
        ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", text);

    [Fact]
    public async Task ControllerTarget_RealBuildFailure_NoGoOffline_ReportsPendingRungs()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException("undefined tag reference XIC_Motor — rung 0");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("BuildFailedEditPending", error.Kind);
        Assert.Equal(0, fake.GoOfflineCallCount);
        Assert.NotNull(error.PendingAfterError);
        Assert.Equal("pending", error.PendingAfterError!.State);
        Assert.Contains("XIC(A)OTE(B);", error.PendingAfterError.Rungs!);
        Assert.Contains("XIC(A)OTE(B);", error.Error);
        Assert.Contains("Cancel Accepted Program Edits", error.Hint);
        Assert.Contains("logix_cancel_pending_edits", error.Hint);
        Assert.DoesNotContain("Nothing was left pending", error.Error + error.Hint);
        Assert.DoesNotContain("nothing pending", (error.Error + error.Hint).ToLowerInvariant());
    }

    [Fact]
    public async Task WorkstationTarget_RealBuildFailure_GoOfflineThenNothingPending()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException("undefined tag reference XIC_Motor — rung 0");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session);

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal(1, fake.GoOfflineCallCount);
        Assert.Contains("Nothing was left pending", error.Hint);
        Assert.Equal("none", error.PendingAfterError?.State);
        Assert.NotEqual("BuildFailedEditPending", error.Kind);
    }

    [Fact]
    public async Task WorkstationTarget_GoOfflineFails_DoesNotClaimNothingPending()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild   = new InvalidOperationException("undefined tag reference");
        fake.ThrowOnGoOffline = new InvalidOperationException("offline refused");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session);

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal(1, fake.GoOfflineCallCount);
        Assert.DoesNotContain("Nothing was left pending", error.Error + error.Hint);
        Assert.NotNull(error.PendingAfterError);
        Assert.Contains("offline refused", error.Error);
    }

    [Fact]
    public async Task ReadbackFails_ReportsUnknownNotNothing()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException("undefined tag reference");
        fake.ClearExportContentAfterBuild = true; // after the build every export throws: the read-back cannot be made

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("BuildFailedEditPending", error.Kind);
        Assert.Equal("unknown", error.PendingAfterError!.State);
        Assert.False(string.IsNullOrWhiteSpace(error.PendingAfterError.ReadbackError));
        Assert.Null(error.PendingAfterError.Rungs);
        Assert.Contains("unknown", error.Error);
        Assert.DoesNotContain("Nothing was left pending", error.Error + error.Hint);
        Assert.Equal(0, fake.GoOfflineCallCount);
    }

    [Fact]
    public async Task LiveTarget_RealBuildFailure_NoGoOffline_SaysLiveNotPending()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnBuild = new InvalidOperationException("undefined tag reference");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, Fragment("XIC(LiveIn)OTE(LiveOut);"), session, policy: null, target: "live");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("BuildFailedEditPending", error.Kind);
        Assert.Equal(0, fake.GoOfflineCallCount);
        Assert.Contains("live on the controller", error.Error);
        Assert.NotNull(error.PendingAfterError);
    }

    [Fact]
    public async Task PostImportStateCheckFails_IncludesPendingAfterError()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnReadConnectedState = new InvalidOperationException("link dropped");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.NotNull(error.PendingAfterError);
    }

    [Fact]
    public async Task PostImportNotOnline_IncludesPendingAfterError()
    {
        var (session, fake) = await OpenAsync();
        fake.ConnectedStateReturnValue = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Offline;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.NotNull(error.PendingAfterError);
    }

    /// <summary>
    /// Source scan: the phrases "Nothing was left pending" and "no pending edit" may appear in the
    /// handler's code only in the single constant that is used after a successful GoOffline.
    /// </summary>
    [Fact]
    public void HandlerSource_NothingPendingWording_OnlyInTheGoOfflineConstant()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "LogixMcp.SdkAdapter", "ProposeRungEditHandler.cs");

        var hits = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("//")) continue;
            if (Regex.IsMatch(line, "Nothing was left pending|no pending edit(?!-)", RegexOptions.IgnoreCase)
               )
                hits.Add(line);
        }

        // Exactly one string literal says it: the constant used only after GoOffline succeeded.
        Assert.Single(hits);
        Assert.Contains("Nothing was left pending", hits[0]);
        Assert.Contains("NothingPendingAfterGoOfflineText", File.ReadAllText(path));
    }
}
#endif
