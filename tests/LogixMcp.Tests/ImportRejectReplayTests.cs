#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A rejected online import says why. <c>Status</c> callbacks are captured apart
/// from the error log and the ones that look like an import or verify message are quoted; when the importer
/// answers <c>XMLSrv_E_IMPORT_ABORTED_NO_CHANGES</c> and nothing names a cause, the same fragment is replayed
/// as an offline import plus verify on a saved copy of the session project and the verify errors come back as
/// <c>rejectReasons</c> with <c>rejectReasonSource</c> "offline-replay" (or "unavailable" with the reason).
/// </summary>
public class ImportRejectReplayTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string Aborted =
        "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors. No changes were made.";

    private static string Fragment => ProposeRungEditHandler.MinimalNopFragment.Replace("NOP();", "XIC(Undefined_Tag)OTE(B);");

    private const string Export =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private sealed class Harness
    {
        public required LogixProjectSession Session { get; init; }
        public required ProposeRungEditRecordingFake Online { get; init; }
        public List<ProposeRungEditRecordingFake> Copies { get; } = new();
        public Action<ProposeRungEditRecordingFake>? ConfigureCopy { get; set; }
    }

    private static async Task<Harness> OpenAsync()
    {
        var online = new ProposeRungEditRecordingFake { ExportContent = Export };
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
        session.IsOnline     = true;
        session.IsCorrelated = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        h = new Harness { Session = session, Online = online };
        return h;
    }

    private static Task<object> PublishAsync(Harness h) =>
        ProposeRungEditHandler.ExecuteAsync(ValidXPath, 0, 0, Fragment, h.Session, policy: null, target: "controller");

    private static OperationFailedException AbortedException() => new(Aborted, ValidXPath);

    // Message shapes inferred from the bench report (XIC(NoSuchTag_X)OTE(Alarm_Light)); pending live capture.
    private static readonly string[] TagNotFoundStatusLines =
    {
        "Partial Import Rungs From Xml File started",
        "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_OPERAND_TAGNOTFOUND\" HexCode=\"80042c01\">",
        "<![CDATA[Referenced tag is undefined.]]>",
        "<Summary Errors=\"1\" Warnings=\"0\"/>",
    };

    [Fact]
    public async Task CapturedImportLogCause_SurfacesAsRejectReasons_WithSourceController()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        foreach (var line in TagNotFoundStatusLines) h.Online.ImportLogEvents.Add((false, line));

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("ImportRejectedByImporter", error.Kind);
        Assert.Equal("controller", error.RejectReasonSource);
        var reason = Assert.Single(error.RejectReasons!);
        Assert.Equal("RxCMP_E_OPERAND_TAGNOTFOUND", reason.Mnemonic);
        Assert.Equal("80042c01", reason.Hex);
        Assert.Equal("Referenced tag is undefined.", reason.Text);
        Assert.Empty(h.Copies);                      // a cause was captured: no offline replay
        Assert.Empty(h.Online.SaveAsPaths);
    }

    [Fact]
    public async Task ControllerRejectReasons_CarryRungIndexWhenTextNamesARung_ElseZeroForSingleRungWithSourceController()
    {
        // Text names no rung, single-rung fragment: rungIndex 0, source "controller".
        var unnamed = await OpenAsync();
        unnamed.Online.ThrowOnImport = AbortedException();
        foreach (var line in TagNotFoundStatusLines) unnamed.Online.ImportLogEvents.Add((false, line));
        var unnamedError = Assert.IsType<ProposeRungEditError>(await PublishAsync(unnamed));
        Assert.Equal("controller", unnamedError.RejectReasonSource);
        Assert.Equal(0, Assert.Single(unnamedError.RejectReasons!).RungIndex);

        // Text names a rung: rungIndex is that rung.
        var named = await OpenAsync();
        named.Online.ThrowOnImport = AbortedException();
        named.Online.ImportLogEvents.Add((false, "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_OPERAND_TAGNOTFOUND\" HexCode=\"80042c01\">"));
        named.Online.ImportLogEvents.Add((false, "<![CDATA[Rung 2: Referenced tag is undefined.]]>"));
        named.Online.ImportLogEvents.Add((false, "<Summary Errors=\"1\" Warnings=\"0\"/>"));
        var namedError = Assert.IsType<ProposeRungEditError>(await PublishAsync(named));
        Assert.Equal("controller", namedError.RejectReasonSource);
        Assert.Equal(2, Assert.Single(namedError.RejectReasons!).RungIndex);
    }

    private static string TwoRungFragment => Fragment.Replace("</RLLContent>",
        "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent>");

    [Fact]
    public async Task SingleRungImportLogReject_GivesRungIndexZero()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        foreach (var line in TagNotFoundStatusLines) h.Online.ImportLogEvents.Add((false, line));

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal(0, Assert.Single(error.RejectReasons!).RungIndex);
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(error));
        Assert.Equal(0, doc.RootElement.GetProperty("rejectReasons")[0].GetProperty("rungIndex").GetInt32());
    }

    [Fact]
    public async Task SingleRungControllerReject_GivesRungIndexZero()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        foreach (var line in TagNotFoundStatusLines) h.Online.ImportLogEvents.Add((false, line));

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("controller", error.RejectReasonSource);
        Assert.All(error.RejectReasons!, r => Assert.Equal(0, r.RungIndex));
    }

    [Fact]
    public async Task MultiRungControllerReject_WithNoRungNamed_GivesRungIndexNull()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        foreach (var line in TagNotFoundStatusLines) h.Online.ImportLogEvents.Add((false, line));

        var error = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, TwoRungFragment, h.Session, policy: null, target: "controller"));

        Assert.Equal("controller", error.RejectReasonSource);
        Assert.Null(Assert.Single(error.RejectReasons!).RungIndex);
    }

    [Fact]
    public void ImportLogAttribution_IsPreferredOverSingleRungDefault()
    {
        var reasons = new[]
        {
            new RejectReason(2, "names rung 2", "RxCMP_E_OPERAND_TAGNOTFOUND"),
            new RejectReason(null, "names none", "RxCMP_E_OPERAND_TAGNOTFOUND"),
        };

        var single = ProposeRungEditHandler.AttributeSingleRung(reasons, 1);
        Assert.Equal(2, single[0].RungIndex);
        Assert.Equal(0, single[1].RungIndex);

        var multi = ProposeRungEditHandler.AttributeSingleRung(reasons, 2);
        Assert.Equal(2, multi[0].RungIndex);
        Assert.Null(multi[1].RungIndex);
    }

    [Fact]
    public async Task NoCapturedCause_UsesOfflineReplay_WithSourceOfflineReplay()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.Online.ImportLogEvents.Add((false, "Partial Import Rungs From Xml File started"));
        h.ConfigureCopy = c =>
        {
            c.BuildLogEvents.Add((true, "Error: MainProgram/MainRoutine, Rung 0: Tag 'Undefined_Tag' is not defined (error 1234)"));
            c.ThrowOnBuild = new OperationFailedException("Build failed with 1 errors", "BUILD_FAILED");
        };

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("offline-replay", error.RejectReasonSource);
        Assert.Single(error.RejectReasons!);
        Assert.Null(error.RejectReasons![0].Mnemonic);
        Assert.Single(h.Copies);
    }

    [Fact]
    public async Task StatusCallbacks_AreCapturedSeparately_AndMatchingMessagesAreQuotedInTheClassifiedError()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.Online.ImportLogEvents.Add((false, "Importing routine MainRoutine"));
        h.Online.ImportLogEvents.Add((false, "Rung 3: unknown instruction XYZ"));
        h.Online.ImportLogEvents.Add((true, "Mnemonic='XMLSrv_E_SOMETHING' error-channel line"));

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("ImportRejectedByImporter", error.Kind);
        Assert.Contains("Status: \"Rung 3: unknown instruction XYZ\"", error.Error);
        Assert.DoesNotContain("Importing routine MainRoutine", error.Error);   // no keyword: not quoted
        Assert.Contains("ImportLog:", error.Error);                              // the error list is still its own list
        Assert.DoesNotContain("Status: \"Mnemonic", error.Error);                // error-channel text stays out of the status quote
        Assert.Empty(h.Copies);                                                  // a cause is named, so no replay
    }

    [Theory]
    [InlineData("rung 4 is odd")]
    [InlineData("INSTRUCTION not allowed")]
    [InlineData("Error in fragment")]
    [InlineData("Unknown thing")]
    [InlineData("tag Undefined")]
    [InlineData("Invalid operand")]
    public void StatusMessages_WithAnyOfTheSixWords_AreSelected_CaseInsensitively(string message)
    {
        var selected = ProposeRungEditHandler.SelectCauseStatusMessages(new[] { "Starting import", message });
        Assert.Equal(new[] { message }, selected);
    }

    [Fact]
    public async Task Replay_IsInvokedOnlyWhenImportAbortedNoChangesHasNoCapturedCause()
    {
        // No cause captured: the replay runs (one copy saved, opened, closed).
        var none = await OpenAsync();
        none.Online.ThrowOnImport = AbortedException();
        await PublishAsync(none);
        Assert.Single(none.Copies);
        Assert.Single(none.Online.SaveAsPaths);

        // A Status message names a cause: no replay.
        var status = await OpenAsync();
        status.Online.ThrowOnImport = AbortedException();
        status.Online.ImportLogEvents.Add((false, "invalid rung reference"));
        await PublishAsync(status);
        Assert.Empty(status.Copies);
        Assert.Empty(status.Online.SaveAsPaths);

        // An Error callback names a cause: no replay.
        var log = await OpenAsync();
        log.Online.ThrowOnImport = AbortedException();
        log.Online.ImportLogEvents.Add((true, "<Exception Severity='Error' Mnemonic='XMLSrv_E_BAD_TAG'>Tag X not found</Exception>"));
        await PublishAsync(log);
        Assert.Empty(log.Copies);

        // A different failure than ImportAbortedNoChanges: no replay.
        var other = await OpenAsync();
        other.Online.ThrowOnImport = new OperationFailedException("RxCL_E_SOMETHING_ELSE - failed", ValidXPath);
        await PublishAsync(other);
        Assert.Empty(other.Copies);
        Assert.Empty(other.Online.SaveAsPaths);
    }

    [Fact]
    public async Task ReplayVerifyErrors_SurfaceAsRejectReasons_WithSourceOfflineReplay()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.ConfigureCopy = c =>
        {
            c.BuildLogEvents.Add((true, "Error: MainProgram/MainRoutine, Rung 2: Tag 'Undefined_Tag' is not defined (error 1234)"));
            c.BuildLogEvents.Add((true, "Error: unlocated verify problem"));
            c.ThrowOnBuild = new OperationFailedException("Build failed with 2 errors", "BUILD_FAILED");
        };

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("ImportRejectedByImporter", error.Kind);            // original classification kept
        Assert.Contains(Aborted, error.Error);                           // original SDK detail kept
        Assert.Equal("offline-replay", error.RejectReasonSource);
        Assert.NotNull(error.RejectReasons);
        Assert.Equal(2, error.RejectReasons!.Count);
        Assert.Equal(2, error.RejectReasons[0].RungIndex);
        Assert.Contains("Undefined_Tag", error.RejectReasons[0].Text);
        Assert.Null(error.RejectReasons[1].RungIndex);
        Assert.Contains("unlocated verify problem", error.RejectReasons[1].Text);

        // The copy got the same fragment, offline (LeaveEdits), at the same place, then was verified.
        var copy = Assert.Single(h.Copies);
        Assert.Equal(ValidXPath, Assert.Single(copy.CapturedXPaths));
        Assert.Equal(0u, Assert.Single(copy.CapturedInsertPositions));
        Assert.Contains("XIC(Undefined_Tag)OTE(B);", copy.LastCapturedXmlContent);
        Assert.Equal(RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption.LeaveEdits, Assert.Single(copy.CapturedImportOptions));
        Assert.Equal(1, copy.BuildCallCount);
    }

    [Fact]
    public async Task ReplayImportErrorsOnTheCopy_SurfaceAsRejectReasons_WithoutBuilding()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.ConfigureCopy = c =>
        {
            c.ImportLogEvents.Add((true, "Rung 1: unknown instruction FOO"));
            c.ThrowOnImport = AbortedException();
        };

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("offline-replay", error.RejectReasonSource);
        var reason = Assert.Single(error.RejectReasons!);
        Assert.Equal(1, reason.RungIndex);
        Assert.Contains("unknown instruction FOO", reason.Text);
        Assert.Equal(0, Assert.Single(h.Copies).BuildCallCount);
    }

    [Fact]
    public async Task ReplayTimeout_YieldsUnavailableSourceWithReason_AndKeepsOriginalError()
    {
        var h = await OpenAsync();
        h.Session.ImportRejectReplayTimeout = TimeSpan.FromMilliseconds(200);   // production bound is 60 s
        h.Online.ThrowOnImport = AbortedException();
        var hang = new TaskCompletionSource();
        h.ConfigureCopy = c => c.BlockImportUntil = hang;

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("unavailable", error.RejectReasonSource);
        Assert.Null(error.RejectReasons);
        Assert.Contains("did not finish within", error.Error);
        Assert.Equal("ImportRejectedByImporter", error.Kind);
        Assert.Contains(Aborted, error.Error);
        hang.SetResult();
    }

    [Fact]
    public void DefaultReplayBound_IsSixtySeconds()
    {
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(new ProposeRungEditRecordingFake()));
        Assert.Equal(TimeSpan.FromSeconds(60), session.ImportRejectReplayTimeout);
    }

    [Fact]
    public async Task ReplayFailure_SaveAsFails_YieldsUnavailableWithReason_AndKeepsOriginalError()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.Online.ThrowOnSaveAs = new OperationFailedException("Project has pending edits and cannot be saved.", "SAVE_FAILED");

        var error = Assert.IsType<ProposeRungEditError>(await PublishAsync(h));

        Assert.Equal("unavailable", error.RejectReasonSource);
        Assert.Contains("pending edits and cannot be saved", error.Error);
        Assert.Contains(Aborted, error.Error);
        Assert.Empty(h.Copies);
    }

    [Fact]
    public async Task FakeSession_ReplayPerformsNoOnlineChanges_AndClosingTheCopyDoesNotCloseTheSession()
    {
        var h = await OpenAsync();
        h.Online.ThrowOnImport = AbortedException();
        h.ConfigureCopy = c => c.BuildLogEvents.Add((true, "Error: Rung 0: boom"));

        await PublishAsync(h);

        // The only import the live handle saw is the original rejected one; the replay imported into the copy.
        Assert.Empty(h.Online.CapturedXPaths.Skip(1));
        var copy = Assert.Single(h.Copies);
        Assert.Single(copy.CapturedXPaths);
        Assert.NotSame(h.Online, copy);

        // Nothing built, went offline, or was closed online; the copy was closed and its file removed.
        Assert.Equal(0, h.Online.BuildCallCount);
        Assert.Equal(0, h.Online.GoOfflineCallCount);
        Assert.Equal(0, h.Online.DisposeCallCount);
        Assert.Equal(1, copy.DisposeCallCount);
        Assert.False(File.Exists(Assert.Single(h.Online.SaveAsPaths)));

        // The session is still open and usable after the copy was closed.
        Assert.Equal("Online", ConnectedStateName(h));
    }

    private static string ConnectedStateName(Harness h) =>
        h.Session.ExecuteAsync(p => p.ReadConnectedStateAsync(), CancellationToken.None).GetAwaiter().GetResult().ToString();
}
#endif
