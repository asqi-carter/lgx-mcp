#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;

using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_build</c> and propose's <c>buildVerify</c> never build inside the online session:
/// they save a copy of the session project under the server work dir, open it offline in a separate handle,
/// build that handle and close it. When the save-as fails with edits pending, or the copy lacks the staged
/// (<c>LeaveEdits</c>) edit, <c>buildVerify</c> is "unavailable" with the reason and no build runs at all.
/// </summary>
public class OfflineBuildCheckTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string StagedRung = "XIC(A)OTE(B);";

    private static string Fragment(string text) =>
        ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", text);

    /// <summary>An online session fake plus a factory that opens each saved copy as its own separate fake.</summary>
    private sealed class Harness
    {
        public required LogixProjectSession Session { get; init; }
        public required ProposeRungEditRecordingFake Online { get; init; }

        /// <summary>Every separate handle opened, in order (one per saved copy).</summary>
        public List<ProposeRungEditRecordingFake> Copies { get; } = new();

        /// <summary>Paths the copies were opened from.</summary>
        public List<string> CopyPaths { get; } = new();

        /// <summary>Whether the saved copy file existed on disk at the moment it was opened.</summary>
        public List<bool> CopyExistedWhenOpened { get; } = new();

        /// <summary>Applied to each copy handle as it is opened (set build outcome, log events, ...).</summary>
        public Action<ProposeRungEditRecordingFake>? ConfigureCopy { get; set; }
    }

    private static async Task<Harness> OpenAsync()
    {
        var online = new ProposeRungEditRecordingFake();
        Harness? h = null;
        var session = new LogixProjectSession((path, _ct) =>
        {
            if (h is null || path == "dummy.ACD")
                return Task.FromResult<ILogixProject>(online);

            // A saved copy: a separate handle whose image is read from the file the save-as wrote.
            var copy = new ProposeRungEditRecordingFake { ExportContent = File.ReadAllText(path) };
            h.CopyPaths.Add(path);
            h.CopyExistedWhenOpened.Add(File.Exists(path));
            h.ConfigureCopy?.Invoke(copy);
            h.Copies.Add(copy);
            return Task.FromResult<ILogixProject>(copy);
        });
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        h = new Harness { Session = session, Online = online };
        return h;
    }

    private static string CopyRoot => Path.Combine(LogixProjectSession.WorkingDirectoryBase, OfflineBuildCheck.CopyDirectoryName);

    private static void AssertNoBuildOnline(Harness h) => Assert.Equal(0, h.Online.BuildCallCount);

    private static void AssertCopyBuiltAndCleanedUp(Harness h)
    {
        var path = Assert.Single(h.Online.SaveAsPaths);
        Assert.StartsWith(Path.GetFullPath(CopyRoot), Path.GetFullPath(path));    // under the server work dir
        Assert.Equal(path, Assert.Single(h.CopyPaths));                           // the copy was opened from it
        Assert.True(Assert.Single(h.CopyExistedWhenOpened));
        var copy = Assert.Single(h.Copies);
        Assert.NotSame(h.Online, copy);                                           // a separate handle
        Assert.Equal(1, copy.BuildCallCount);
        Assert.Equal(1, copy.DisposeCallCount);                                   // closed
        Assert.Empty(copy.Handlers);                                              // log handler removed
        Assert.False(File.Exists(path));                                          // temp copy cleaned up
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        AssertNoBuildOnline(h);
        Assert.Equal(0, h.Online.GoOfflineCallCount);
    }

    // ── logix_build ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LogixBuild_SavesCopyOpensItOfflineBuildsAndClosesIt_WithNoOnlineBuild()
    {
        var h = await OpenAsync();

        var result = await BuildHandler.ExecuteAsync(null, h.Session);

        var ok = Assert.IsType<BuildResult>(result);
        Assert.Equal("passed", ok.Status);
        AssertCopyBuiltAndCleanedUp(h);
        Assert.Equal(DesignerVersionSource.ProjectRevision, h.Session.DesignerVersion!.Source);
    }

    [Fact]
    public async Task LogixBuild_FailedCopyBuild_IsReportedInTheSameShapeAsToday()
    {
        var h = await OpenAsync();
        h.ConfigureCopy = c =>
        {
            c.BuildLogEvents.Add((true, "Error: MainProgram/MainRoutine, Rung 3: Tag 'Undefined_Tag' is not defined (error 1234)"));
            c.BuildLogEvents.Add((true, "Warning: MainProgram/MainRoutine, Rung 7: Unused output"));
            c.ThrowOnBuild = new OperationFailedException("Build failed with 1 error", "BUILD_FAILED");
        };

        var result = await BuildHandler.ExecuteAsync("default", h.Session);

        var failed = Assert.IsType<BuildResult>(result);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("default", failed.Target);
        var error = Assert.Single(failed.Errors);
        Assert.Equal("MainProgram/MainRoutine rung 3", error.Location);
        Assert.Equal("1234", error.Code);
        Assert.Equal(7, Assert.Single(failed.Warnings).Rung);
        AssertCopyBuiltAndCleanedUp(h);
    }

    [Fact]
    public async Task LogixBuild_SaveAsFailsWithEditsPending_IsUnavailableWithReason_AndNoBuildRuns()
    {
        var h = await OpenAsync();
        h.Session.HasStagedRungEdit = true;
        h.Online.ThrowOnSaveAs = new OperationFailedException("Project has pending edits and cannot be saved.", "SAVE_FAILED");

        var result = await BuildHandler.ExecuteAsync("default", h.Session);

        var unavailable = Assert.IsType<BuildError>(result);
        Assert.Equal("unavailable", unavailable.BuildVerify);
        Assert.Contains("with edits pending", unavailable.Error);
        Assert.Contains("pending edits and cannot be saved", unavailable.Error);
        Assert.Contains("\"buildVerify\":\"unavailable\"", System.Text.Json.JsonSerializer.Serialize(unavailable));
        Assert.Empty(h.Copies);                       // no copy was opened
        AssertNoBuildOnline(h);                       // and nothing was built online
    }

    // ── uploaded session (bench 2026-10-06) ──────────────────────────────────
    // An uploaded session's project factory returns its already-open online handle for any path.
    // The saved copy must open in its own handle; otherwise "building the copy" builds online
    // (a Logix Services fatal on the bench) and disposing "the copy" closes the session.

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Online, List<ProposeRungEditRecordingFake> Copies)>
        OpenUploadedAsync()
    {
        var online = new ProposeRungEditRecordingFake();
        var copies = new List<ProposeRungEditRecordingFake>();
        var session = new LogixProjectSession(
            (_, _) => Task.FromResult<ILogixProject>(online),
            path => path,
            (path, _) =>
            {
                var copy = new ProposeRungEditRecordingFake { ExportContent = File.ReadAllText(path) };
                copies.Add(copy);
                return Task.FromResult<ILogixProject>(copy);
            });
        await session.OpenAsync("uploaded.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, online, copies);
    }

    [Fact]
    public async Task UploadedSession_CopyOpensInItsOwnHandle_OnlineHandleNeitherBuiltNorDisposed()
    {
        var (session, online, copies) = await OpenUploadedAsync();

        var ok = Assert.IsType<BuildResult>(await BuildHandler.ExecuteAsync(null, session));

        Assert.Equal("passed", ok.Status);
        Assert.Equal(1, Assert.Single(copies).BuildCallCount);
        Assert.Equal(0, online.BuildCallCount);
        Assert.Equal(0, online.DisposeCallCount);
        Assert.True(session.IsOpen);
    }

    // ── propose buildVerify ───────────────────────────────────────────────────

    [Fact]
    public async Task ProposeBuildVerify_SavesCopyOpensItOfflineBuildsAndClosesIt_WithNoOnlineBuild()
    {
        var h = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session);

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        // A workstation stage is staged in the session image, which Logix does not check.
        Assert.Equal("passed-excluding-staged", ok.BuildVerify);
        AssertCopyBuiltAndCleanedUp(h);
    }

    [Fact]
    public async Task ProposeBuildVerify_FailedCopyBuild_IsReportedAsABuildFailureLikeToday()
    {
        var h = await OpenAsync();
        h.ConfigureCopy = c => c.ThrowOnBuild = new InvalidOperationException("undefined tag reference XIC_Motor — rung 0");

        var result = await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session);

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains("Rung build failed", error.Error);
        Assert.Contains("XIC_Motor", error.Error);
        Assert.Equal(1, Assert.Single(h.Copies).BuildCallCount);
        Assert.Equal(1, h.Copies[0].DisposeCallCount);
        AssertNoBuildOnline(h);
    }

    [Fact]
    public async Task ProposeBuildVerify_SaveAsFailsWithEditsPending_IsUnavailableWithReason_AndNoBuildRuns()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnSaveAs = new OperationFailedException("Project has pending edits and cannot be saved.", "SAVE_FAILED");

        var result = await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session);

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("unavailable", ok.BuildVerify);
        Assert.Contains("could not save a copy", ok.Note);
        Assert.Contains("pending edits and cannot be saved", ok.Note);
        Assert.Empty(h.Copies);
        AssertNoBuildOnline(h);
    }

    /// <summary>
    /// Contractual: a stage whose saved copy lacks the staged edit returns the error
    /// StagedEditLost, not a note, builds nothing, and stages nothing more.
    /// </summary>
    [Fact]
    public async Task StagedEditLost_ReturnedFromCopyCheck_AndNothingMoreIsStaged()
    {
        var h = await OpenAsync();
        h.Online.SaveAsOmitsStagedEdit = true;        // the save-as drops the pending LeaveEdits rung

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment(StagedRung), h.Session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedEditLost", error.Kind);
        Assert.Contains("does not carry the staged edit", error.Error);
        var copy = Assert.Single(h.Copies);           // it was opened and inspected ...
        Assert.True(copy.ExportCallCount >= 1);
        Assert.Equal(0, copy.BuildCallCount);         // ... but never built
        Assert.Equal(1, copy.DisposeCallCount);
        AssertNoBuildOnline(h);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Assert.Single(h.Online.SaveAsPaths))!));
        // Nothing more is staged.
        Assert.Empty(h.Session.StagedRungBuffer);
        Assert.Empty(h.Session.StagedReplaceCounts);
        Assert.Equal(0, h.Session.StagedRungCount);
    }

    /// <summary>
    /// G8: when nothing else is staged, the StagedEditLost hint does not tell the caller to "re-stage them";
    /// when other rungs are staged, it still does.
    /// </summary>
    [Fact]
    public async Task StagedEditLost_Hint_DoesNotSayRestageThem_WhenNothingNeedsRestaging()
    {
        var h = await OpenAsync();
        h.Online.SaveAsOmitsStagedEdit = true;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment(StagedRung), h.Session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StagedEditLost", error.Kind);
        Assert.DoesNotContain("re-stage them", error.Hint, StringComparison.Ordinal);

        Assert.DoesNotContain("re-stage them", ProposeRungEditHandler.StagedEditLostHint(0), StringComparison.Ordinal);
        Assert.Contains("re-stage them", ProposeRungEditHandler.StagedEditLostHint(2), StringComparison.Ordinal);
    }

    // ── the copy carries the staged (LeaveEdits) workstation edit ─────────────

    [Fact]
    public async Task SavedCopy_CarriesTheStagedLeaveEditsWorkstationEdit()
    {
        var h = await OpenAsync();
        var staged = await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session);
        Assert.Equal("passed-excluding-staged", Assert.IsType<ProposeRungEditResult>(staged).BuildVerify);

        // The workstation edit was imported with LeaveEdits into the session handle ...
        Assert.Equal(PartialImportOption.LeaveEdits, Assert.Single(h.Online.CapturedImportOptions));

        // ... and the copy that was built carried it: its image (written by save-as) holds the rung as pending.
        var copy = Assert.Single(h.Copies);
        Assert.NotNull(copy.ExportContent);
        Assert.Contains(StagedRung, copy.ExportContent);
        Assert.Contains("Type=\"I\"", copy.ExportContent);
        Assert.Equal(1, copy.BuildCallCount);
    }

    [Fact]
    public async Task LogixBuild_AfterStagingAWorkstationEdit_BuildsACopyThatCarriesIt()
    {
        var h = await OpenAsync();
        Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session));
        Assert.True(h.Session.HasStagedRungEdit);
        Assert.Contains(ValidXPath, h.Session.StagedRoutineXPaths);

        var result = await BuildHandler.ExecuteAsync("default", h.Session);

        // The build passed, but the session holds staged rungs Logix does not check.
        var built = Assert.IsType<BuildResult>(result);
        Assert.Equal("passed-excluding-staged", built.Status);
        Assert.NotNull(built.Note);
        Assert.Equal(2, h.Copies.Count);              // one per build (propose's verify, then logix_build)
        var copy = h.Copies[1];
        Assert.Contains(StagedRung, copy.ExportContent!);
        Assert.True(copy.ExportCallCount >= 1);       // the staged edit was looked for in the copy
        Assert.Equal(1, copy.BuildCallCount);
        AssertNoBuildOnline(h);
    }

    [Fact]
    public async Task LogixBuild_CopyLacksTheStagedEdit_IsUnavailableWithReason_AndNoBuildRuns()
    {
        var h = await OpenAsync();
        Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment(StagedRung), h.Session));
        h.Copies.Clear();
        h.Online.SaveAsOmitsStagedEdit = true;

        var result = await BuildHandler.ExecuteAsync("default", h.Session);

        var unavailable = Assert.IsType<BuildError>(result);
        Assert.Equal("unavailable", unavailable.BuildVerify);
        Assert.Contains("does not carry the staged edit", unavailable.Error);
        Assert.Equal(0, Assert.Single(h.Copies).BuildCallCount);
        AssertNoBuildOnline(h);
    }
}
#endif
