#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A controller publish carries the comment each landed rung has on the
/// controller (<c>PublishedRoutine.PublishedComments</c>, not serialized), and stage responses list the
/// <c>replay-copy/&lt;step&gt;</c> sub-phases after <c>replay-copy</c> when verifyOnStage ran the replay.
/// </summary>
[Collection("AuditLoggerDir")]
public class PublishCommentsAndSubPhasesTests
{
    private const string MainXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string OldRung = "XIC(Old)OTE(OldOut);";
    private const string NewRung = "XIC(A)OTE(B);";

    private static string Fragment(string text, string? comment = null) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        (comment is null ? "" : $"<Comment><![CDATA[{comment}]]></Comment>") +
        $"<Text><![CDATA[{text}]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string Export(params (string Type, string Text, string? Comment)[] rungs) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\">" +
            (r.Comment is null ? "" : $"<Comment><![CDATA[{r.Comment}]]></Comment>") +
            $"<Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, fake);
    }

    [Fact]
    public async Task Publish_ReplaceWithPreservedComment_CarriesThePreservedComment()
    {
        var (session, fake) = await OpenAsync();
        // The pre-publish export has the commented original; the post-publish export has the replacement landed.
        fake.ExportContent = Export(("N", OldRung, "Original comment"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 1, Fragment(NewRung), session, target: "controller");

        // After the import the controller holds the replacement carrying the preserved comment.
        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(new[] { 0 }, success.CommentsPreserved);
        var published = Assert.Single(success.PublishedRoutines!);
        Assert.Equal(new[] { NewRung }, published.PublishedTexts);
        Assert.NotNull(published.PublishedComments);
        Assert.Equal("Original comment", published.PublishedComments![0]);
        Assert.DoesNotContain("PublishedComments", JsonSerializer.Serialize(success), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publish_ExplicitNewComment_CarriesTheExplicitComment()
    {
        var (session, fake) = await OpenAsync();
        // The export shows the landed rung without a comment: the explicit comment comes from the fragment itself.
        fake.ExportContent = Export(("I", NewRung, null));
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, Fragment(NewRung, "Explicit comment"), session, target: "controller");

        var success   = Assert.IsType<ProposeRungEditResult>(result);
        var published = Assert.Single(success.PublishedRoutines!);
        Assert.NotNull(published.PublishedComments);
        Assert.Equal("Explicit comment", Assert.Single(published.PublishedComments!));
    }

    [Fact]
    public async Task Publish_NoComment_GivesNullEntry()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContent = Export(("I", NewRung, null));
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, Fragment(NewRung), session, target: "controller");

        var success   = Assert.IsType<ProposeRungEditResult>(result);
        var published = Assert.Single(success.PublishedRoutines!);
        Assert.NotNull(published.PublishedComments);
        Assert.Null(Assert.Single(published.PublishedComments!));
    }

    /// <summary>A clock that only moves when advanced.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static async Task<(LogixProjectSession Session, ManualClock Clock)> OpenWithSlowCopyAsync()
    {
        var clock  = new ManualClock();
        var online = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((path, _) =>
        {
            if (path == "dummy.ACD") return Task.FromResult<ILogixProject>(online);
            clock.Advance(TimeSpan.FromSeconds(3));                    // opening the copy takes time
            var copy = new ProposeRungEditRecordingFake { ExportContent = File.ReadAllText(path) };
            copy.OnExport = _ => clock.Advance(TimeSpan.FromSeconds(2));   // so does reading it back
            return Task.FromResult<ILogixProject>(copy);
        });
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        session.StageClock      = clock;
        return (session, clock);
    }

    [Fact]
    public async Task Stage_WithVerifyOnStage_ResponseJsonListsReplayCopySubPhasesAfterReplayCopy()
    {
        var (session, _) = await OpenWithSlowCopyAsync();

        var result = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "workstation", verifyOnStage: true));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var names = doc.RootElement.GetProperty("phaseTimings").EnumerateArray()
            .Select(e => e.GetProperty("phase").GetString()!).ToList();

        // The seven top-level phases keep their names and order.
        Assert.Equal(StagePhases.All, names.Where(n => !n.Contains('/')).ToArray());
        var at = names.IndexOf(StagePhases.ReplayCopy);
        Assert.True(at >= 0);
        var subs = names.Where(n => n.StartsWith(StagePhases.ReplayCopySubPhasePrefix, StringComparison.Ordinal)).ToList();
        Assert.Contains(StagePhases.ReplayCopyStep("open-copy"), subs);
        // Every sub-phase follows replay-copy directly, before the next top-level phase.
        Assert.Equal(subs, names.Skip(at + 1).Take(subs.Count).ToList());
        Assert.Equal(StagePhases.OnlineImport, names[at + 1 + subs.Count]);
        var openCopy = doc.RootElement.GetProperty("phaseTimings").EnumerateArray()
            .Single(e => e.GetProperty("phase").GetString() == StagePhases.ReplayCopyStep("open-copy"));
        Assert.Equal(3000, openCopy.GetProperty("durationMs").GetInt64());
    }

    [Fact]
    public async Task Stage_WithoutVerifyOnStage_HasNoSubPhaseEntries()
    {
        var (session, _) = await OpenWithSlowCopyAsync();

        var result = Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "workstation", verifyOnStage: false));

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(StagePhases.ReplayCopySubPhasePrefix, json, StringComparison.Ordinal);
        if (result.PhaseTimings is { } timings)
            Assert.DoesNotContain(timings, t => t.Phase.Contains('/'));
    }
}
#endif
