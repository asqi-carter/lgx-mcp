#if LOGIX_SDK
using System.Collections.Generic;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// After a controller or live publish the handler reads
/// <c>&lt;tag&gt;.PRE</c> for each literal preset of a table instruction and reports
/// <c>presetsVerified</c> or <c>PresetMismatch</c>, never a <c>PublishMismatch</c>.
/// </summary>
public class PresetVerificationTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string TimerRung = "XIC(Run)TON(FtsTmr,3000,0);";

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

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_p, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>Fake tag service holding <c>.PRE</c> values by tag path; records every read.</summary>
    private sealed class FakePresetTagService(Dictionary<string, object> values) : ITagService
    {
        public List<string> Reads { get; } = new();
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(string t, object v, CancellationToken ct = default) =>
            throw new InvalidOperationException("The preset check must never write.");
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default)
        {
            Reads.Add(t);
            return values.TryGetValue(t, out var v)
                ? Task.FromResult(new TagValue(t, v, "DINT"))
                : throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TagNotFound, $"'{t}' not found", t));
        }
    }

    [Fact]
    public async Task PresetMatches_ListsPresetsVerified_AndNoMismatch()
    {
        var (session, fake) = await OpenAsync();
        // The export writes the preset as `?`; the tag holds the real value.
        fake.ExportContent = Export(("I", "XIC(Run)TON(FtsTmr,?,?);"));
        var tags = new FakePresetTagService(new() { ["Program:MainProgram.FtsTmr.PRE"] = 3000 });

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment(TimerRung), session, target: "controller", presetReader: tags);

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("proposed", success.Status);
        Assert.Equal("verified", success.Verification);
        var verified = Assert.Single(success.PresetsVerified!);
        Assert.Equal(new PresetVerified("FtsTmr", 3000), verified);
        Assert.Null(success.PresetMismatches);
        Assert.Null(success.PresetsUnread);
        Assert.Contains("Program:MainProgram.FtsTmr.PRE", tags.Reads);
        Assert.Contains("\"presetsVerified\":[{\"tag\":\"FtsTmr\",\"pre\":3000}]",
            System.Text.Json.JsonSerializer.Serialize(success));
    }

    [Fact]
    public async Task PresetDiffers_ReportsPresetMismatch_WithoutPublishMismatch()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContent = Export(("I", "XIC(Run)TON(FtsTmr,?,?);"));
        var tags = new FakePresetTagService(new() { ["Program:MainProgram.FtsTmr.PRE"] = 1000 });

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment(TimerRung), session, target: "controller", presetReader: tags);

        // Not a ProposeRungEditMismatch: the rung text matched.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.Null(success.PresetsVerified);
        var mismatch = Assert.Single(success.PresetMismatches!);
        Assert.Equal("PresetMismatch", mismatch.Kind);
        Assert.Equal("FtsTmr", mismatch.Tag);
        Assert.Equal(3000, mismatch.Expected);
        Assert.Equal("1000", mismatch.Found);
        Assert.Contains("PresetMismatch", success.Message);
        // PublishMismatchUnresolved is only set by a rung-text PublishMismatch.
        Assert.Empty(session.UnresolvedPublishMismatches);
    }

    [Fact]
    public async Task PresetFallsBackToControllerScope_AndUnreadableIsNeitherVerifiedNorMismatch()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContent = Export(("I", "TON(CtlTmr,?,?);TON(Gone,?,?);"));
        var tags = new FakePresetTagService(new() { ["CtlTmr.PRE"] = 500 });

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("TON(CtlTmr,500,0);TON(Gone,200,0);"), session,
            target: "controller", presetReader: tags);

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(new PresetVerified("CtlTmr", 500), Assert.Single(success.PresetsVerified!));
        Assert.Null(success.PresetMismatches);
        Assert.Equal(["Gone"], success.PresetsUnread);
    }

    [Fact]
    public async Task NoPresetReader_OrNoLiteralPreset_OmitsPresetFields()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContent = Export(("I", "XIC(Run)TON(FtsTmr,?,?);"));

        var noReader = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment(TimerRung), session, target: "controller"));
        Assert.Null(noReader.PresetsVerified);
        Assert.Null(noReader.PresetMismatches);

        var (session2, fake2) = await OpenAsync();
        fake2.ExportContent = Export(("I", "TON(FtsTmr,PresetTag,0);"));
        var tags = new FakePresetTagService(new());
        var noLiteral = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("TON(FtsTmr,PresetTag,0);"), session2, target: "controller", presetReader: tags));
        Assert.Null(noLiteral.PresetsVerified);
        Assert.Empty(tags.Reads);
    }

    [Fact]
    public void ExtractLiteralPresets_FindsNestedTableInstructions_AndSkipsNonLiterals()
    {
        var found = PresetVerifier.ExtractLiteralPresets(
        [
            "[XIC(A)TON(T1,3000,0),XIC(B)CTU(C1,10,0)];",
            "TON(T2,Preset,0);XIC(T3);TON(T1,3000,0);",
        ]);

        Assert.Equal([("T1", 3000L), ("C1", 10L)], found);
    }
}
#endif
