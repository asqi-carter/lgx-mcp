#if LOGIX_SDK
using System.Collections.Generic;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Every controller publish reports an
/// explicit <c>verification</c> status; workstation stages omit it.
/// </summary>
public class ProposeRungEditVerificationTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static string Fragment(params string[] rungTexts)
    {
        var rungs = string.Concat(rungTexts.Select((t, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>"));
        return
            "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\"><RLLContent>" + rungs +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    }

    private static string Export(params (string Type, string Text)[] rungs)
    {
        var body = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"));
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
            "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
            "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" + body +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    }

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
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
    public async Task CensusUnreadable_ReturnsVerificationUnverified()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = null; // export throws -> census unreadable
        // Name resolution still needs the program listing.
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
        Assert.Equal("unverified", success.Verification);
        Assert.Null(success.VerifiedPendingRungs);
        Assert.Contains("could not be read back", success.Message);
        Assert.Contains("Logic → Online Edits", success.Message);
        Assert.Contains("Studio 5000", success.Message);
    }

    [Fact]
    public async Task CensusMatched_ReturnsVerificationVerified()
    {
        // Direct controller publish, no prior workstation stage: the fragment's own
        // rung must be verified against the census.
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export(("I", "XIC(A)OTE(B);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.Equal(1, success.VerifiedPendingRungs);
        Assert.Null(success.UnexpectedPendingRungs);
    }

    [Fact]
    public async Task DirectPublish_FragmentRungMissingFromCensus_ReturnsMismatch()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export(("N", "NOP();"));
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        // None of the expected rungs landed: ImportAddedNoRungs (a partial landing stays PublishMismatch).
        var mismatch = Assert.IsType<ProposeRungEditMismatch>(result);
        Assert.Equal("ImportAddedNoRungs", mismatch.Kind);
    }

    [Fact]
    public async Task EmptyFragmentStagedCounterOnly_ReturnsNotApplicable()
    {
        var (session, _) = await BuildOpenSessionAsync();
        session.StagedRungCount = 1;   // staged work exists (counter only), so the publish is not NothingStaged

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("notApplicable", success.Verification);
    }

    [Fact]
    public async Task NewPendingRungNotExpectedNorPreexisting_IsCountedAndDoesNotFail()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export(("I", "XIC(A)OTE(B);"), ("I", "XIC(Other)OTE(Z);"));

        // Baseline export (before import) has no pending rungs; the post-publish
        // export has an extra I rung nobody asked for. Simulate by switching export
        // content when the import happens.
        var baseline = Export(("N", "NOP();"));
        var after    = Export(("I", "XIC(A)OTE(B);"), ("I", "XIC(Other)OTE(Z);"));
        fake.ExportContent = baseline;
        fake.XmlFileContentCaptor = _ => fake.ExportContent = after;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.Equal(1, success.UnexpectedPendingRungs);
    }

    [Fact]
    public async Task WorkstationTarget_OmitsVerification()
    {
        var (session, _) = await BuildOpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "workstation");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Null(success.Verification);
        Assert.Null(success.UnexpectedPendingRungs);
        var json = System.Text.Json.JsonSerializer.Serialize(success);
        Assert.DoesNotContain("verification", json);
    }

    /// <summary>
    /// A rung requested at position 2 that the post-publish census finds at index 1
    /// is reported at index 1 — the message uses the census, not the requested position.
    /// </summary>
    [Fact]
    public async Task PublishMessage_RequestedPosition2LandedAtIndex1_ReportsIndex1()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export(("N", "NOP();"), ("I", "XIC(A)OTE(B);"), ("N", "NOP();"));
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 2, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.Contains("landed at index 1", success.Message);
        Assert.DoesNotContain("insert position 2", success.Message);
    }

    /// <summary>When the census agrees with the requested position the message and insertPosition name that same index.</summary>
    [Fact]
    public async Task PublishMessage_LandedAtRequestedPosition_KeepsInsertPosition()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export(("N", "NOP();"), ("I", "XIC(A)OTE(B);"));
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 1, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Contains("rung landed at index 1", success.Message);   // always derived from landedIndices
        Assert.Equal(1u, success.InsertPosition);
    }
}
#endif
