#if LOGIX_SDK
using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Stage and publish share one conflict rule, so a buffer the stage accepted can always be
/// published; <c>logix_clear_staged</c> empties the session's staged rungs and tags without touching the controller.
/// </summary>
[Collection("AuditLoggerDir")]
public class ClearStagedTests : IDisposable
{
    private const string RoutineA =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";
    private const string RoutineB =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='OtherRoutine']";

    private const string Fragment =
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        "<Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private readonly string _auditDir;

    public ClearStagedTests()
    {
        _auditDir = Path.Combine(Path.GetTempPath(), "lgx-clear-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditDir);
        AuditLogger.OverrideLogDir(_auditDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_auditDir, recursive: true); } catch { /* best-effort */ }
    }

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_, _) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static void SeedBucket(LogixProjectSession s, string xpath, uint pos, int rungs, uint rc = 0)
    {
        s.StagedRungBuffer[(xpath, pos)] = Enumerable.Range(0, rungs).Select(i => $"XIC(T{pos}_{i})OTE(C{pos}_{i});").ToList();
        if (rc > 0) s.StagedReplaceCounts[(xpath, pos)] = rc;
        s.HasStagedRungEdit = true;
        s.StagedRungCount  += rungs;
    }

    private static async Task<ClearStagedResult> ClearAsync(LogixProjectSession s, string? routine = null, string? program = null) =>
        Assert.IsType<ClearStagedResult>(await ClearStagedHandler.ExecuteAsync(s, routine, program));

    private static void SeedTag(LogixProjectSession s, string name) =>
        s.StagedTagBuffer[name] = new XElement("Tag", new XAttribute("Name", name), new XAttribute("DataType", "BOOL"));

    private List<JsonElement> AuditEvents(string name) =>
        File.ReadAllLines(Path.Combine(_auditDir, "audit.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("event").GetString() == name)
            .ToList();

    // ── Unified conflict rule ────────────────────────────────────────────────

    /// <summary>
    /// The V3 sequence: insert@1, then replace@0 rc=1. The second stage would leave a buffer the publish re-check
    /// refuses ((0, n1, rc1) occupies [0,2) and contains the bucket at 1), so it is refused at stage and the
    /// buffer stays publishable.
    /// </summary>
    [Fact]
    public async Task V3Sequence_InsertAt1ThenReplaceAt0_IsRefusedAtStage_NeverStuck()
    {
        var (session, fake) = await OpenSessionAsync();

        var first = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 1, 0, Fragment, session, target: "workstation");
        Assert.IsNotType<ProposeRungEditError>(first);

        var second = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(second);
        Assert.Equal("StagedPositionConflict", error.Kind);
        Assert.Contains("logix_clear_staged", error.Hint!);

        // Never stuck: whatever the buffer holds, the publish re-check finds no conflict.
        Assert.Empty(ProposeRungEditHandler.FindStagedBufferConflicts(session.StagedRungBuffer, session.StagedReplaceCounts));
        Assert.Single(session.StagedRungBuffer);
        Assert.Contains((RoutineA, 1u), session.StagedRungBuffer.Keys);
    }

    /// <summary>The same two stages in the opposite order are refused by the same rule.</summary>
    [Fact]
    public async Task V3Sequence_ReplaceAt0ThenInsertAt1_IsRefusedAtStage()
    {
        var (session, _) = await OpenSessionAsync();

        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation"));
        var second = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 1, 0, Fragment, session, target: "workstation");

        Assert.Equal("StagedPositionConflict", Assert.IsType<ProposeRungEditError>(second).Kind);
        Assert.Empty(ProposeRungEditHandler.FindStagedBufferConflicts(session.StagedRungBuffer, session.StagedReplaceCounts));
    }

    /// <summary>
    /// Stage-time acceptance equals publish-time consistency: for every pair of buckets, adding the second to the
    /// first is refused by FindStagedBucketConflicts exactly when FindStagedBufferConflicts reports the pair.
    /// </summary>
    [Fact]
    public void StageAndPublishRules_AgreeForEveryBucketPair()
    {
        var shapes = (from p in new uint[] { 0, 1, 2, 3 }
                      from n in new[] { 1, 2 }
                      from rc in new uint[] { 0, 1, 2 }
                      select (p, n, rc)).ToList();

        foreach (var a in shapes)
        foreach (var b in shapes)
        {
            var buffer = new Dictionary<(string, uint), List<string>>();
            var rcs    = new Dictionary<(string, uint), uint>();
            buffer[("/r", a.p)] = Enumerable.Repeat("x", a.n).ToList();
            if (a.rc > 0) rcs[("/r", a.p)] = a.rc;

            var stageRefuses = ProposeRungEditHandler
                .FindStagedBucketConflicts(buffer, rcs, "/r", b.p, b.rc, b.n).Count > 0;

            // Publish view of the same two buckets (distinct keys only; a shared key is one merged bucket).
            if (a.p == b.p) { Assert.True(stageRefuses, $"same position {a} vs {b}"); continue; }
            var both = new Dictionary<(string, uint), List<string>>(buffer) { [("/r", b.p)] = Enumerable.Repeat("x", b.n).ToList() };
            var bothRcs = new Dictionary<(string, uint), uint>(rcs);
            if (b.rc > 0) bothRcs[("/r", b.p)] = b.rc;
            var publishRefuses = ProposeRungEditHandler.FindStagedBufferConflicts(both, bothRcs).Count > 0;

            Assert.True(stageRefuses == publishRefuses, $"stage/publish disagree for {a} then {b}");
        }
    }

    // ── logix_clear_staged ───────────────────────────────────────────────────

    /// <summary>The kept-normal sets follow their bucket: a clear removes them with the replace counts, in scope only.</summary>
    [Fact]
    public async Task ClearStaged_RemovesKeptNormalSetsWithTheReplaceCounts()
    {
        var (session, _) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 2, rc: 1);
        SeedBucket(session, RoutineB, 0, 1, rc: 1);
        session.StagedKeptNormal[(RoutineA, 0u)] = [0];
        session.StagedKeptNormal[(RoutineB, 0u)] = [0];

        await ClearAsync(session, routine: "MainRoutine");

        Assert.DoesNotContain(session.StagedReplaceCounts.Keys, k => k.RoutineXPath == RoutineA);
        Assert.DoesNotContain(session.StagedKeptNormal.Keys, k => k.RoutineXPath == RoutineA);
        Assert.Contains(session.StagedKeptNormal.Keys, k => k.RoutineXPath == RoutineB);

        await ClearAsync(session);
        Assert.Empty(session.StagedReplaceCounts);
        Assert.Empty(session.StagedKeptNormal);
    }

    /// <summary>Clearing one routine removes its rungs and tags, leaves the other routine, reports and audits.</summary>
    [Fact]
    public async Task ClearStaged_OneRoutine_ClearsItsRungsAndTags_ReportsAndAudits()
    {
        var (session, fake) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 2, rc: 1);
        SeedTag(session, "TagA");

        var json = await new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities: null).GetClearStagedResponseAsync("MainRoutine", null, session);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("cleared", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("clearedRungCount").GetInt32());
        Assert.Equal(RoutineA, root.GetProperty("clearedRoutines")[0].GetProperty("routineXPath").GetString());
        Assert.Equal("TagA", root.GetProperty("clearedTagNames")[0].GetString());
        Assert.False(root.GetProperty("controllerTouched").GetBoolean());

        Assert.Empty(session.StagedRungBuffer);
        Assert.Empty(session.StagedReplaceCounts);
        Assert.Empty(session.StagedTagBuffer);
        Assert.False(session.HasStagedRungEdit);
        Assert.Equal(0, session.StagedRungCount);

        var line = Assert.Single(AuditEvents("staged_cleared"));
        Assert.Equal("MainRoutine", line.GetProperty("scope").GetString());
        Assert.Equal(2, line.GetProperty("rung_count").GetInt32());
    }

    /// <summary>A single-routine clear leaves other routines' rungs, and the tags they may depend on, in place.</summary>
    [Fact]
    public async Task ClearStaged_OneRoutine_LeavesOtherRoutinesAndKeepsTags()
    {
        var (session, _) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 1);
        SeedBucket(session, RoutineB, 0, 1);
        SeedTag(session, "TagA");

        var result = await ClearAsync(session, "MainRoutine");

        Assert.Equal(1, result.ClearedRungCount);
        Assert.Equal(1, result.TagsRetained);
        Assert.Empty(result.ClearedTagNames);
        Assert.False(session.StagedRungBuffer.ContainsKey((RoutineA, 0u)));
        Assert.True(session.StagedRungBuffer.ContainsKey((RoutineB, 0u)));
        Assert.Single(session.StagedTagBuffer);
        Assert.True(session.HasStagedRungEdit);
        Assert.Equal(1, session.StagedRungCount);
    }

    /// <summary>Omitting the routine clears every routine and every staged tag.</summary>
    [Fact]
    public async Task ClearStaged_AllRoutines_ClearsEverything()
    {
        var (session, _) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 1);
        SeedBucket(session, RoutineB, 3, 2, rc: 1);
        SeedTag(session, "TagA");
        SeedTag(session, "TagB");
        session.StagedReplacedRungTexts.Add("old");

        var result = await ClearAsync(session);

        Assert.Equal("all", result.Scope);
        Assert.Equal(3, result.ClearedRungCount);
        Assert.Equal(2, result.ClearedRoutines.Count);
        Assert.Equal(new[] { "TagA", "TagB" }, result.ClearedTagNames);
        Assert.Empty(session.StagedRungBuffer);
        Assert.Empty(session.StagedReplaceCounts);
        Assert.Empty(session.StagedTagBuffer);
        Assert.Empty(session.StagedReplacedRungTexts);
        Assert.False(session.HasStagedRungEdit);
        Assert.Equal(0, session.StagedRungCount);
        Assert.Single(AuditEvents("staged_cleared"));
    }

    /// <summary>
    /// After the V3 refusal and a clear, the same two stages can be re-staged at safe positions and published.
    /// The clear rebuilds the image with an upload and verifies it with an export, but sends no import.
    /// </summary>
    [Fact]
    public async Task ClearStaged_DoesNotTouchTheController_AndAllowsRestaging()
    {
        var (session, fake) = await OpenSessionAsync();
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 1, 0, Fragment, session, target: "workstation"));
        var importsBefore = fake.CapturedImportOptions.Count;
        var exportsBefore = fake.ExportCallCount;

        var cleared = await ClearAsync(session, RoutineA);

        Assert.Equal(importsBefore, fake.CapturedImportOptions.Count);   // nothing re-imported: no other routine kept
        Assert.Equal(1, fake.UploadCallCount);                           // the image was rebuilt from the controller
        Assert.True(fake.ExportCallCount > exportsBefore);               // and verified by an export
        Assert.True(cleared.ImageRestored);
        Assert.False(cleared.ControllerTouched);
        Assert.Empty(session.StagedRungBuffer);

        var again = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation");
        Assert.IsNotType<ProposeRungEditError>(again);
    }

    /// <summary>With nothing staged the tool says so and still audits.</summary>
    [Fact]
    public async Task ClearStaged_NothingStaged_ReportsNothingStaged()
    {
        var (session, _) = await OpenSessionAsync();

        var result = await ClearAsync(session, "NoSuchRoutine");

        Assert.Equal("nothing_staged", result.Status);
        Assert.Equal(0, result.ClearedRungCount);
        Assert.False(result.ControllerTouched);
        Assert.Single(AuditEvents("staged_cleared"));
    }

    // ── The session image ───────────────────────────

    private const string CleanExport =
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>The routine as the session image shows it while a staged rung (type e) sits in it.</summary>
    private const string StagedExport =
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"e\"><Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung>" +
        "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private const string KeptRoutineExport =
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"OtherRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"OtherRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"e\"><Text><![CDATA[XIC(KeptTag)OTE(KeptCoil);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private const string KeptFragment =
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"OtherRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        "<Text><![CDATA[XIC(KeptTag)OTE(KeptCoil);]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>A routine staged for real (MainRoutine) plus a kept bucket of another routine (OtherRoutine).</summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> StageMainAndKeepOtherAsync()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent = CleanExport;   // the routine as the controller has it: this is the pre-stage record
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "workstation"));
        SeedBucket(session, RoutineB, 2, 1, rc: 1);
        session.StagedRungBuffer[(RoutineB, 2u)] = new List<string> { "XIC(KeptTag)OTE(KeptCoil);" };
        session.RecordStagedImport(new LogixProjectSession.StagedImport(RoutineB, 2, 1, KeptFragment));
        fake.ExportContentByXPath[RoutineB] = KeptRoutineExport;   // OtherRoutine as the image shows it once the kept bucket is replayed
        fake.ExportContent = StagedExport;             // the image now shows the staged rung
        fake.OnUpload      = () => fake.ExportContent = CleanExport;   // the controller image does not
        return (session, fake);
    }

    /// <summary>
    /// A clear of one routine rebuilds the image from the controller, replays the other routine's kept bucket with
    /// LeaveEdits at its original position, verifies the cleared routine and returns imageRestored:true.
    /// </summary>
    [Fact]
    public async Task ClearStaged_OneRoutineWithAnotherBucketKept_RebuildsImageReplaysKeptBucketAndReportsImageRestored()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        var importsBefore = fake.CapturedXPaths.Count;
        var exportsBefore = fake.ExportXPaths.Count;

        var result = await ClearAsync(session, "MainRoutine");

        Assert.True(result.ImageRestored);
        Assert.Equal(1, result.ReplayedBuckets);
        Assert.Equal(new[] { "offline", "upload", "online" }, fake.RestoreCallOrder);

        // Exactly one import after the rebuild: the kept bucket, at its position and replace count, as LeaveEdits.
        Assert.Equal(importsBefore + 1, fake.CapturedXPaths.Count);
        Assert.Equal(RoutineB, fake.CapturedXPaths[^1]);
        Assert.Equal(2u, fake.CapturedInsertPositions[^1]);
        Assert.Equal(1u, fake.CapturedReplaceCounts[^1]);
        Assert.Equal(RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption.LeaveEdits, fake.CapturedImportOptions[^1]);
        Assert.Contains("KeptTag", fake.LastCapturedXmlContent!);

        // Both the cleared routine and the routine whose bucket was replayed were exported afterwards (verification).
        Assert.Contains(RoutineA, fake.ExportXPaths.Skip(exportsBefore));
        Assert.Contains(RoutineB, fake.ExportXPaths.Skip(exportsBefore));

        // Bookkeeping: the kept bucket and its recorded import stay, the cleared ones are gone.
        Assert.True(session.StagedRungBuffer.ContainsKey((RoutineB, 2u)));
        Assert.False(session.StagedRungBuffer.ContainsKey((RoutineA, 0u)));
        Assert.Equal(RoutineB, Assert.Single(session.StagedImports).RoutineXPath);
        Assert.Empty(session.UnverifiedClears);
        Assert.DoesNotContain("bookkeeping only", result.Note);
        Assert.Contains("rebuilt from the controller", result.Note);
    }

    /// <summary>A staged rung still in the image after the restore is reported, not hidden.</summary>
    [Fact]
    public async Task ClearStaged_LeftoverStagedRungAfterRestore_ReturnsClearIncompleteListingLeftoverRungsAndReconnectHint()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.OnUpload = null;   // the "upload" leaves the staged rung in the image

        var json = await new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities: null)
            .GetClearStagedResponseAsync("MainRoutine", null, session);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("ClearIncomplete", root.GetProperty("kind").GetString());
        Assert.False(root.GetProperty("imageRestored").GetBoolean());
        Assert.Contains("reconnect with logix_connect to discard the session image", root.GetProperty("hint").GetString());
        // The staged rung (type e, where the controller has NOP) and the NOP pushed down to rung 1.
        var leftover = root.GetProperty("leftoverRungs").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.All(leftover, l => Assert.Contains(RoutineA, l));
        Assert.Contains(leftover, l => l.Contains("XIC(StagedTag)OTE(StagedCoil);"));
        Assert.Contains(RoutineA, session.UnverifiedClears.Keys);
    }

    /// <summary>An upload that fails cannot verify anything: ClearIncomplete, and the kept routine is flagged too.</summary>
    [Fact]
    public async Task ClearStaged_UploadFails_ReturnsClearIncomplete()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.ThrowOnUpload = new InvalidOperationException("controller unreachable");

        var error = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));

        Assert.Equal("ClearIncomplete", error.Kind);
        Assert.Contains("controller unreachable", error.Error);
        Assert.Contains("reconnect with logix_connect", error.Hint!);
        Assert.Contains(RoutineA, session.UnverifiedClears.Keys);
        Assert.Contains(RoutineB, session.UnverifiedClears.Keys);   // its replay never happened
    }

    private static string RoutineExport(params (string Type, string Text)[] rungs) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// The restored image differs from the pre-stage image without any rung in an e/er/dN zone (a rung of type N has
    /// other text): the comparison against the recorded controller image catches it, where the old zone check passed it.
    /// </summary>
    [Fact]
    public async Task ClearStaged_ImageDiffersFromPreStageWithoutAnyStagedZoneRung_ReturnsClearIncomplete()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.OnUpload = () => fake.ExportContent = RoutineExport(("N", "XIC(Changed)OTE(Drift);"));

        var error = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));

        Assert.Equal("ClearIncomplete", error.Kind);
        Assert.False(error.ImageRestored);
        var line = Assert.Single(error.LeftoverRungs!);
        Assert.Contains("rung 0 differs", line);
        Assert.Contains("NOP();", line);
        Assert.Contains("XIC(Changed)OTE(Drift);", line);
        Assert.Contains("reconnect with logix_connect", error.Hint!);
        Assert.Contains(RoutineA, session.UnverifiedClears.Keys);
    }

    /// <summary>A rung missing from the restored image is reported too.</summary>
    [Fact]
    public async Task ClearStaged_RestoredImageMissingARung_ReturnsClearIncomplete()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.OnUpload = () => fake.ExportContent = RoutineExport();

        var error = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));

        Assert.Equal("ClearIncomplete", error.Kind);
        Assert.Contains("rung 0 is missing", Assert.Single(error.LeftoverRungs!));
    }

    /// <summary>A kept routine whose staged rung did not survive the replay is not reported as restored.</summary>
    [Fact]
    public async Task ClearStaged_KeptRoutineLosesItsStagedRungInTheReplay_ReturnsClearIncomplete()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.ExportContentByXPath[RoutineB] = KeptRoutineExport.Replace("XIC(KeptTag)OTE(KeptCoil);", "NOP();");

        var error = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));

        Assert.Equal("ClearIncomplete", error.Kind);
        Assert.Contains("a kept staged rung is missing after the replay", Assert.Single(error.LeftoverRungs!));
    }

    /// <summary>
    /// The pre-stage record is read before the stage's import: the export switches to the staged image the moment the
    /// import runs, and the record must still hold the clean rungs.
    /// </summary>
    [Fact]
    public async Task Stage_RecordsPreStageImage_BeforeTheImportChangesIt()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent = CleanExport;
        fake.XmlFileContentCaptor = _ => fake.ExportContent = StagedExport;   // runs inside the import

        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "workstation"));

        var baseline = session.GetRoutineBaseline(RoutineA);
        Assert.NotNull(baseline);
        Assert.DoesNotContain(baseline!, r => r.Type is "e" or "er" || r.Text.Contains("StagedCoil"));
        Assert.Contains(baseline!, r => r.Text.Contains("NOP"));
    }

    /// <summary>The success note says what was compared: the recorded pre-stage image, rung by rung.</summary>
    [Fact]
    public async Task ClearStaged_Success_NoteNamesThePreStageComparison()
    {
        var (session, _) = await StageMainAndKeepOtherAsync();

        var result = await ClearAsync(session, "MainRoutine");

        Assert.Contains("record of its image before it was first staged", result.Note);
        Assert.Contains("rung count, rung types and rung text", result.Note);
        Assert.Contains("not rung by rung", result.Note);   // the kept routine's limit is stated
    }

    /// <summary>With no pre-stage record the check is the narrow one, and the note says so instead of claiming a match.</summary>
    [Fact]
    public async Task ClearStaged_NoPreStageRecord_NoteStatesTheNarrowerCheck()
    {
        var (session, fake) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 1);
        session.RecordStagedImport(new LogixProjectSession.StagedImport(RoutineA, 0, 0, Fragment));
        fake.OnUpload = () => fake.ExportContent = CleanExport;

        var result = await ClearAsync(session, "MainRoutine");

        Assert.True(result.ImageRestored);
        Assert.Contains("no pre-stage image was recorded", result.Note);
        Assert.Contains("would not be caught", result.Note);
        Assert.DoesNotContain("compared with that record", result.Note);
    }

    /// <summary>The comparison is exact on count, type and text, and tolerant of the export's ? placeholders like the publish census.</summary>
    [Fact]
    public void CompareToBaseline_ComparesCountTypeAndText_WithTheCensusComparer()
    {
        var baseline = new[] { new LogixProjectSession.BaselineRung("N", "TON(T,3000,0);") };

        Assert.Empty(ClearStagedHandler.CompareToBaseline(baseline,
            new[] { new LogixProjectSession.BaselineRung("N", "TON(T,?,?);") }));
        Assert.Single(ClearStagedHandler.CompareToBaseline(baseline,
            new[] { new LogixProjectSession.BaselineRung("I", "TON(T,3000,0);") }));
        Assert.Single(ClearStagedHandler.CompareToBaseline(baseline,
            new[] { new LogixProjectSession.BaselineRung("N", "TON(T,3000,0);"), new LogixProjectSession.BaselineRung("N", "NOP();") }));
    }

    // ── Offline-file sessions ────────────────────────────────────────────────

    /// <summary>An offline session cannot stage, so no staged rung was written into the open file; the note says so, with no SDK call.</summary>
    [Fact]
    public async Task ClearStaged_OfflineFileSessionWithNothingStaged_SaysNoStagedRungWasWrittenToTheFile()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("offline.ACD");   // IsOnline stays false

        var result = await ClearAsync(session);

        Assert.Equal("nothing_staged", result.Status);
        Assert.Contains("not online", result.Note);
        Assert.Contains("none were written into the open project file", result.Note);
        Assert.Equal(0, fake.UploadCallCount);
        Assert.Equal(0, fake.ExportCallCount);
    }

    /// <summary>
    /// An offline session that does hold staged state has its working copy reloaded from the file you opened, which
    /// removes the staged rungs the same way; the file itself is never written.
    /// </summary>
    [Fact]
    public async Task ClearStaged_OfflineFileSessionWithStagedWork_ReloadsWorkingCopyAndReportsImageRestored()
    {
        var first   = new ProposeRungEditRecordingFake { ExportContent = StagedExport };
        var second  = new ProposeRungEditRecordingFake { ExportContent = CleanExport };
        var handles = new Queue<ILogixProject>(new ILogixProject[] { first, second });
        var copies  = 0;
        var session = new LogixProjectSession(
            (_, _) => Task.FromResult(handles.Dequeue()),
            _ => $"copy{++copies}.ACD");
        await session.OpenAsync("offline.ACD");
        SeedBucket(session, RoutineA, 0, 1);

        var result = await ClearAsync(session, "MainRoutine");

        Assert.True(result.ImageRestored);
        Assert.Equal(1, first.DisposeCallCount);              // the handle that carried the staged rung is closed
        Assert.Equal(0, first.UploadCallCount);               // offline: no controller upload
        Assert.Contains(RoutineA, second.ExportXPaths);       // verified on the reloaded copy
        Assert.Contains("working copy", result.Note);
        Assert.Contains("never written", result.Note);
    }

    /// <summary>If the working copy cannot be reloaded, the clear reports ClearIncomplete instead of claiming success.</summary>
    [Fact]
    public async Task ClearStaged_OfflineFileSessionReloadFails_ReturnsClearIncomplete()
    {
        // Opened in place (copier returns the same path): there is no separate working copy to reload.
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake), static p => p);
        await session.OpenAsync("offline.ACD");
        SeedBucket(session, RoutineA, 0, 1);

        var error = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));

        Assert.Equal("ClearIncomplete", error.Kind);
        Assert.Contains("working copy", error.Error);
        Assert.Contains(RoutineA, session.UnverifiedClears.Keys);
    }

    // ── Stale image guard ────────────────────────────────────────────────────

    /// <summary>
    /// Until a clear verifies imageRestored:true, a stage or a publish in that program reports ClearIncomplete and
    /// imports nothing; a later clear that verifies lifts the refusal.
    /// </summary>
    [Fact]
    public async Task StageAndPublish_AfterUnverifiedClear_AreRefusedUntilALaterClearVerifies()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.OnUpload = null;
        Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));
        var importsBefore = fake.CapturedXPaths.Count;

        var stage = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "workstation"));
        var publish = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller"));

        Assert.Equal("ClearIncomplete", stage.Kind);
        Assert.Equal("ClearIncomplete", publish.Kind);
        Assert.Equal(importsBefore, fake.CapturedXPaths.Count);

        // The controller image comes back clean; a second clear verifies it and lifts the refusal.
        fake.OnUpload = () => { fake.ExportContent = CleanExport; fake.ExportContentByXPath.Clear(); };
        var second = await ClearAsync(session);
        Assert.True(second.ImageRestored);
        Assert.Empty(session.UnverifiedClears);
        var restaged = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "workstation");
        Assert.True(restaged is not ProposeRungEditError, (restaged as ProposeRungEditError)?.Error);
    }

    // ── The S3 sequence ──────────────────────────────

    private const string S3Envelope =
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>{0}" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string S3Export(params (string Type, string Text)[] rungs) =>
        string.Format(S3Envelope, string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")));

    /// <summary>
    /// Bench-shaped S3 sequence, with a fake that models the session image the way the bench does:
    /// a workstation stage leaves its rungs in the image (type <c>e</c>, the replaced original <c>er</c>), a clear that does not
    /// rebuild the image leaves them there, and the controller publish refuses a program whose image holds rungs the
    /// server no longer tracks (<c>PendingEditsBlockImport</c>). Steps: stage replace(0), a second stage conflicts, the
    /// model follows the hint and calls <c>logix_clear_staged</c>, stages again, and the publish reaches the controller.
    /// It fails if the clear does not restore the image: the clear reports <c>ClearIncomplete</c>, or (with a clear that only
    /// empties the bookkeeping) the publish is refused by the leftover rungs.
    /// </summary>
    [Fact]
    public async Task S3Sequence_StageReplace0_Conflict_Clear_Stage_Publish_Lands()
    {
        var (session, fake) = await OpenSessionAsync();

        var controllerImage = S3Export(("N", "NOP();"));
        var stagedImage     = S3Export(("e", "XIC(StagedTag)OTE(StagedCoil);"), ("er", "NOP();"));
        var landedImage     = S3Export(("I", "XIC(StagedTag)OTE(StagedCoil);"));

        fake.ExportContent               = controllerImage;
        fake.ProgramExportCleanBeforeImport = false;   // the program-wide edit-state read must see the real image
        fake.OnUpload                    = () => fake.ExportContent = controllerImage;   // the rebuild: the controller's image
        fake.OnImportRungs = (option, _, _, _) =>
        {
            if (option == LogixProject.PartialImportOption.LeaveEdits) fake.ExportContent = stagedImage;
            else if (option == LogixProject.PartialImportOption.AcceptEdits) fake.ExportContent = landedImage;
        };

        // 1. Stage a replace of rung 0.
        var first = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation");
        Assert.True(first is not ProposeRungEditError, (first as ProposeRungEditError)?.Error);
        Assert.Equal(stagedImage, fake.ExportContent);

        // 2. A second stage at the same place conflicts, and the hint says what clear_staged does.
        var conflict = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation"));
        Assert.Equal("StagedPositionConflict", conflict.Kind);
        Assert.Contains("logix_clear_staged", conflict.Hint!);
        Assert.Contains("session image", conflict.Hint!);

        // 3. The model follows the hint. The image is rebuilt from the controller and verified.
        var cleared = Assert.IsType<ClearStagedResult>(await ClearStagedHandler.ExecuteAsync(session, RoutineA));
        Assert.True(cleared.ImageRestored);
        Assert.Equal(1, fake.UploadCallCount);
        Assert.Equal(controllerImage, fake.ExportContent);
        Assert.Empty(session.StagedRungBuffer);

        // 4. Stage again: nothing blocks it.
        var restaged = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation");
        Assert.True(restaged is not ProposeRungEditError, (restaged as ProposeRungEditError)?.Error);

        // 5. Publish to the controller: it is not refused for leftover rungs and the import reaches the controller.
        var importsBefore = fake.CapturedImportOptions.Count;
        var publish = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, "", session, target: "controller");
        Assert.IsType<ProposeRungEditResult>(publish);
        Assert.Equal(importsBefore + 1, fake.CapturedImportOptions.Count);
        Assert.Equal(LogixProject.PartialImportOption.AcceptEdits, fake.CapturedImportOptions[^1]);
        Assert.Contains("StagedTag", fake.LastCapturedXmlContent!);
        Assert.Contains("Type=\"I\"><Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);", fake.ExportContent!);   // pending on the controller
        Assert.False(session.HasStagedRungEdit);
        Assert.Empty(session.StagedRungBuffer);
    }

    /// <summary>
    /// The S3 sequence with the restore disabled (the upload leaves the staged rungs in the image, as the old
    /// bookkeeping-only clear did): the clear does not report the image restored, and a publish is not allowed to land.
    /// </summary>
    [Fact]
    public async Task S3Sequence_WithImageRestoreDisabled_ClearIsIncompleteAndPublishDoesNotLand()
    {
        var (session, fake) = await OpenSessionAsync();
        var controllerImage = S3Export(("N", "NOP();"));
        var stagedImage     = S3Export(("e", "XIC(StagedTag)OTE(StagedCoil);"), ("er", "NOP();"));

        fake.ExportContent                  = controllerImage;
        fake.ProgramExportCleanBeforeImport = false;
        fake.OnUpload                       = null;   // the restore is disabled: the image keeps the staged rungs
        fake.OnImportRungs = (option, _, _, _) =>
        {
            if (option == LogixProject.PartialImportOption.LeaveEdits) fake.ExportContent = stagedImage;
        };

        Assert.True(await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation") is not ProposeRungEditError);
        Assert.Equal("StagedPositionConflict", Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation")).Kind);

        var cleared = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, RoutineA));
        Assert.Equal("ClearIncomplete", cleared.Kind);

        var importsBefore = fake.CapturedImportOptions.Count;
        var publish = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, "", session, target: "controller"));
        Assert.Equal(importsBefore, fake.CapturedImportOptions.Count);   // nothing reached the controller
        Assert.NotNull(publish.Kind);
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    /// <summary>The staged_cleared audit line carries imageRestored and replayedBuckets, on success and on ClearIncomplete.</summary>
    [Fact]
    public async Task ClearStaged_Audit_CarriesImageRestoredAndReplayedBuckets()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();

        await ClearAsync(session, "MainRoutine");
        var ok = Assert.Single(AuditEvents("staged_cleared"));
        Assert.True(ok.GetProperty("imageRestored").GetBoolean());
        Assert.Equal(1, ok.GetProperty("replayedBuckets").GetInt32());

        // Same scenario, but the staged rung survives the restore.
        var (bad, badFake) = await StageMainAndKeepOtherAsync();
        badFake.OnUpload = null;
        await ClearStagedHandler.ExecuteAsync(bad, "MainRoutine");
        var failed = AuditEvents("staged_cleared").Last();
        Assert.False(failed.GetProperty("imageRestored").GetBoolean());
        Assert.Equal(1, failed.GetProperty("replayedBuckets").GetInt32());
    }

    // ── Hints ────────────────────────────────────────────────────────────────

    /// <summary>Every refusal that used to say "cancel them" for staged rungs now names logix_clear_staged.</summary>
    [Fact]
    public async Task StagedRungHints_PointAtLogixClearStaged()
    {
        // StagedRungsPresent (live target, staged rungs in the buffer).
        var (live, _) = await OpenSessionAsync();
        SeedBucket(live, RoutineA, 0, 1);
        var liveErr = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            RoutineA, 0, 0, Fragment, live, policy: null, target: "live"));
        Assert.Equal("StagedRungsPresent", liveErr.Kind);
        Assert.Contains("logix_clear_staged", liveErr.Hint!);

        // "buffer holds conflicting buckets" (publish re-check).
        var (pub, _) = await OpenSessionAsync();
        SeedBucket(pub, RoutineA, 0, 1, rc: 1);
        SeedBucket(pub, RoutineA, 1, 1);
        var pubErr = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            RoutineA, 0, 0, null, pub, target: "controller"));
        Assert.Contains("conflicting buckets", pubErr.Error);
        Assert.Contains("logix_clear_staged", pubErr.Hint!);
        Assert.DoesNotContain("cancel the staged", pubErr.Hint!);
    }

    // ── Explicit replace comments are reversible on cancel ───────────

    private const string OriginalComment = "Original rung 0 comment";

    private static string CommentFragment(string comment) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        $"<Comment><![CDATA[{comment}]]></Comment>" +
        "<Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string CommentImage(string comment, params (string Type, string Text)[] rungs) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"C\"><Programs>" +
        "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\">" +
            (i == rungs.Length - 1 && comment.Length > 0 ? $"<Comment><![CDATA[{comment}]]></Comment>" : "") +
            $"<Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>
    /// A session whose controller has NOP() with <see cref="OriginalComment"/> at rung 0. A workstation stage leaves the
    /// staged pair in the image and writes the explicit comment to the original rung immediately, so the controller image
    /// carries it; the rebuild on cancel therefore still shows the new comment until it is written back.
    /// </summary>
    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake, Func<string> Controller)> CommentSessionAsync(string newComment)
    {
        var (session, fake) = await OpenSessionAsync();
        string controllerComment = OriginalComment;
        fake.ProgramExportCleanBeforeImport = false;
        fake.ExportContent = CommentImage(controllerComment, ("N", "NOP();"));
        fake.OnUpload      = () => fake.ExportContent = CommentImage(controllerComment, ("N", "NOP();"));
        fake.OnImportRungs = (option, _, _, fragment) =>
        {
            if (option == LogixProject.PartialImportOption.LeaveEdits)
            {
                controllerComment = newComment;   // the comment lands on the original rung at once
                fake.ExportContent = CommentImage(controllerComment, ("e", "XIC(StagedTag)OTE(StagedCoil);"), ("er", "NOP();"));
            }
            else if (option == LogixProject.PartialImportOption.AcceptEdits)
            {
                var m = System.Text.RegularExpressions.Regex.Match(fragment, @"<Comment><!\[CDATA\[(.*?)\]\]></Comment>");
                controllerComment = m.Success ? m.Groups[1].Value : string.Empty;
                fake.ExportContent = CommentImage(controllerComment, ("N", "NOP();"));
            }
        };
        return (session, fake, () => controllerComment);
    }

    /// <summary>An explicit comment replace followed by cancel restores the original comment; the result said it would.</summary>
    [Fact]
    public async Task ExplicitCommentReplace_ThenCancel_RestoresOriginalComment()
    {
        var (session, fake, controllerComment) = await CommentSessionAsync("Brand new comment");

        var staged = Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, CommentFragment("Brand new comment"), session, target: "workstation"));
        Assert.True(staged.CommentRestorableOnCancel);
        Assert.Contains("\"commentRestorableOnCancel\":true", JsonSerializer.Serialize(staged));
        Assert.Equal("Brand new comment", controllerComment());   // written immediately

        var cleared = await ClearAsync(session, "MainRoutine");

        Assert.True(cleared.ImageRestored);
        Assert.Equal(new[] { RoutineA + "#0" }, cleared.CommentsRestored);
        Assert.True(cleared.ControllerTouched);
        Assert.Equal(OriginalComment, controllerComment());   // rung 0's comment is the original after the cancel
        Assert.Contains(OriginalComment, fake.ExportContent!);
        Assert.Equal(LogixProject.PartialImportOption.AcceptEdits, fake.CapturedImportOptions[^1]);
        Assert.Empty(session.CommentUndos);
    }

    /// <summary>An explicit empty comment ("" clears it) is reversible too: cancel puts the original text back.</summary>
    [Fact]
    public async Task ExplicitEmptyCommentReplace_ThenCancel_RestoresOriginalComment()
    {
        var (session, _, controllerComment) = await CommentSessionAsync(string.Empty);

        var staged = Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(
                RoutineA, 0, 1, null, session, target: "workstation",
                rungs: new[] { new RungInput("XIC(StagedTag)OTE(StagedCoil);", string.Empty) }));
        Assert.True(staged.CommentRestorableOnCancel);
        Assert.Equal(string.Empty, controllerComment());

        await ClearAsync(session, "MainRoutine");

        Assert.Equal(OriginalComment, controllerComment());
    }

    /// <summary>A replace that omits the comment keeps the original and reports no restore flag.</summary>
    [Fact]
    public async Task ReplaceWithoutComment_ReportsNoRestoreFlag_AndCancelWritesNothingBack()
    {
        var (session, fake, _) = await CommentSessionAsync(OriginalComment);

        var staged = Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, Fragment, session, target: "workstation"));
        Assert.Null(staged.CommentRestorableOnCancel);
        Assert.Empty(session.CommentUndos);

        var cleared = await ClearAsync(session, "MainRoutine");
        Assert.Null(cleared.CommentsRestored);
        Assert.False(cleared.ControllerTouched);
        Assert.DoesNotContain(LogixProject.PartialImportOption.AcceptEdits, fake.CapturedImportOptions);
    }

    /// <summary>A comment equal to the original changes nothing: restorable, but there is nothing to undo.</summary>
    [Fact]
    public async Task ExplicitCommentEqualToOriginal_IsRestorable_WithNothingToUndo()
    {
        var (session, _, _) = await CommentSessionAsync(OriginalComment);

        var staged = Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, CommentFragment(OriginalComment), session, target: "workstation"));

        Assert.True(staged.CommentRestorableOnCancel);
        Assert.Empty(session.CommentUndos);
    }

    /// <summary>The controller path cannot write the original comment back on cancel, so an explicit comment that changes it is refused.</summary>
    [Fact]
    public async Task ExplicitCommentReplace_ControllerPath_RefusesCommentNotReversible()
    {
        var (session, fake, _) = await CommentSessionAsync("New");

        var error = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, CommentFragment("New"), session, target: "controller"));

        Assert.Equal("CommentNotReversible", error.Kind);
        Assert.Equal("omit comment to keep the original, or set it after the edit is finalized", error.Hint);
        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>
    /// A workstation replace with an explicit comment, then a publish, then a cancel of the
    /// zone restores the original comment. The undo survives the publish, attached to the zone.
    /// </summary>
    [Fact]
    public async Task WorkstationExplicitComment_ThenPublish_ThenCancel_RestoresOriginalComment()
    {
        var (session, fake, controllerComment) = await CommentSessionAsync("Brand new comment");

        var staged = Assert.IsType<ProposeRungEditResult>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, CommentFragment("Brand new comment"), session, target: "workstation"));
        Assert.True(staged.CommentRestorableOnCancel);
        Assert.Single(session.CommentUndos);

        var publish = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, "", session, target: "controller");
        Assert.IsType<ProposeRungEditResult>(publish);

        // The undo moved from the staged set onto the zone; it was not dropped.
        Assert.Empty(session.CommentUndos);
        var held = Assert.Single(session.ZoneCommentUndos);
        Assert.Equal(OriginalComment, held.OriginalComment);
        Assert.Equal("Brand new comment", controllerComment());

        // Cancel of the zone (Studio discards the pending rungs; the explicit comment stays on the original rung).
        fake.ExportContent = CommentImage(controllerComment(), ("N", "NOP();"));
        var (restored, failure) = await LogixSdkTools.RestoreZoneCommentUndosAsync(session, "MainProgram", default);

        Assert.Null(failure);
        Assert.Equal(new[] { RoutineA + "#0" }, restored);
        Assert.Equal(OriginalComment, controllerComment());
        Assert.Empty(session.ZoneCommentUndos);
    }

    /// <summary>A cancel of another program leaves the zone's comment undos alone.</summary>
    [Fact]
    public async Task ZoneCommentUndos_AreScopedToTheCancelledProgram()
    {
        var (session, fake, controllerComment) = await CommentSessionAsync("Brand new comment");
        await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, CommentFragment("Brand new comment"), session, target: "workstation");
        await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 1, "", session, target: "controller");

        var (restored, failure) = await LogixSdkTools.RestoreZoneCommentUndosAsync(session, "OtherProgram", default);

        Assert.Empty(restored);
        Assert.Null(failure);
        Assert.Single(session.ZoneCommentUndos);
        Assert.Equal("Brand new comment", controllerComment());
    }

    // ── Publish and clear_staged are mutually exclusive ──

    /// <summary>
    /// A clear issued from inside the publish's own import call (the publish is in flight) returns the typed
    /// PublishInProgress, changes nothing, and the publish then succeeds.
    /// </summary>
    [Fact]
    public async Task ClearStagedDuringInFlightPublish_ReturnsPublishInProgress_ChangesNothing_AndPublishSucceeds()
    {
        var (session, fake) = await OpenSessionAsync();
        SeedBucket(session, RoutineB, 0, 2);
        SeedTag(session, "StagedTag");

        object? clearDuringPublish = null;
        bool inFlightSeen = false;
        bool unchangedAfterClear = false;
        fake.OnImportRungs = (_, _, _, _) =>
        {
            inFlightSeen = session.PublishInFlight;
            clearDuringPublish = ClearStagedHandler.ExecuteAsync(session).GetAwaiter().GetResult();
            unchangedAfterClear = session.StagedRungBuffer.TryGetValue((RoutineB, 0u), out var b) && b.Count == 2
                && session.StagedTagBuffer.ContainsKey("StagedTag") && session.UnverifiedClears.Count == 0;
        };

        var publish = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller");

        Assert.True(inFlightSeen);
        var refused = Assert.IsType<ClearStagedError>(clearDuringPublish);
        Assert.Equal("PublishInProgress", refused.Kind);
        Assert.Null(refused.ImageRestored);
        Assert.Null(refused.LeftoverRungs);
        // Nothing the clear could have touched changed while it ran.
        Assert.True(unchangedAfterClear);
        Assert.True(publish is not ProposeRungEditError, (publish as ProposeRungEditError)?.Error);
        Assert.False(session.PublishInFlight);
    }

    /// <summary>A clear refused as PublishInProgress leaves the staged buffers exactly as they were.</summary>
    [Fact]
    public async Task ClearStaged_WhilePublishFlagSet_ChangesNothing()
    {
        var (session, _) = await OpenSessionAsync();
        SeedBucket(session, RoutineA, 0, 2);
        SeedTag(session, "StagedTag");

        session.BeginPublish();
        var refused = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session));
        session.EndPublish();

        Assert.Equal("PublishInProgress", refused.Kind);
        Assert.Equal(2, session.StagedRungBuffer[(RoutineA, 0u)].Count);
        Assert.Contains("StagedTag", session.StagedTagBuffer.Keys);
        Assert.True(session.HasStagedRungEdit);
        Assert.Equal(2, session.StagedRungCount);
        Assert.Empty(session.UnverifiedClears);
    }

    /// <summary>
    /// The post-import state check treats Connected as equivalent to Online: with the edit confirmed pending the
    /// publish succeeds, or (never untyped) returns PostImportCheckInconclusive with the pending rungs filled in.
    /// </summary>
    [Fact]
    public async Task PostImportStateCheck_ConnectedWithConfirmedPendingEdit_SucceedsOrIsTypedInconclusiveWithPending()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ConnectedStateReturnValue = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Connected;

        var result = await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller");

        if (result is ProposeRungEditError err)
        {
            Assert.Equal("PostImportCheckInconclusive", err.Kind);
            Assert.NotNull(err.PendingAfterError);
            Assert.Equal("pending", err.PendingAfterError!.State);
            Assert.NotEmpty(err.PendingAfterError.Rungs!);
        }
        Assert.DoesNotContain("may not have been applied", (result as ProposeRungEditError)?.Error ?? string.Empty);
        Assert.IsNotType<ProposeRungEditError>(result);
    }

    /// <summary>A state that is neither Online nor Connected is a typed PostImportCheckInconclusive carrying the read-back.</summary>
    [Fact]
    public async Task PostImportStateCheck_UnexpectedState_IsTypedInconclusive_NeverUntypedMayNotHaveBeenApplied()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ConnectedStateReturnValue = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Offline;

        var err = Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller"));

        Assert.Equal("PostImportCheckInconclusive", err.Kind);
        Assert.NotNull(err.PendingAfterError);
        Assert.DoesNotContain("may not have been applied", err.Error);
    }

    /// <summary>Neither the refused clear nor the inconclusive publish leaves the session blocked for stage or publish.</summary>
    [Fact]
    public async Task SessionIsNotBlockedForStageOrPublish_AfterEitherPath()
    {
        // Path 1: clear refused mid-publish.
        var (session, fake) = await OpenSessionAsync();
        fake.OnImportRungs = (_, _, _, _) => ClearStagedHandler.ExecuteAsync(session).GetAwaiter().GetResult();
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller"));
        fake.OnImportRungs = null;
        Assert.False(session.PublishInFlight);
        Assert.Empty(session.UnverifiedClears);
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "workstation"));
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session, target: "controller"));

        // Path 2: an inconclusive state check, then stage and publish again once the state reads Online.
        var (session2, fake2) = await OpenSessionAsync();
        fake2.ConnectedStateReturnValue = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Offline;
        Assert.Equal("PostImportCheckInconclusive", Assert.IsType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session2, target: "controller")).Kind);
        fake2.ConnectedStateReturnValue = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState.Online;
        Assert.False(session2.PublishInFlight);
        Assert.Empty(session2.UnverifiedClears);
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session2, target: "workstation"));
        Assert.IsNotType<ProposeRungEditError>(
            await ProposeRungEditHandler.ExecuteAsync(RoutineA, 0, 0, Fragment, session2, target: "controller"));
    }

    // ── SDK fault during a clear ───────────────

    private const string UnhandledFaultText =
        "LgxSrv_E_UNHANDLED_EXCEPTION while rebuilding the image; LgxSrv_E_SERVER_FAULTED";

    /// <summary>
    /// A fault during the clear's rebuild returns ClearIncomplete; every later call fast-fails with SdkServerFaulted and a
    /// hint that names the clear and no longer claims the controller was untouched by "the failed build".
    /// </summary>
    [Fact]
    public async Task ClearStaged_FaultDuringClear_ReturnsClearIncomplete_ThenSdkServerFaultedWithClearHint()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.ThrowOnUpload = new OperationFailedException(UnhandledFaultText, "UploadAsync");

        var first = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));
        Assert.Equal("ClearIncomplete", first.Kind);
        Assert.True(session.IsFaulted);
        Assert.Equal(RockwellErrorHints.OperationClear, session.FaultOperation);

        var callsAfterFault = fake.UploadCallCount + fake.GoOnlineCallCount + fake.GoOfflineCallCount;
        var later = Assert.IsType<ClearStagedError>(await ClearStagedHandler.ExecuteAsync(session, "MainRoutine"));
        Assert.Equal(ErrorKinds.SdkServerFaulted, later.Kind);
        Assert.Contains("during clear", later.Error, StringComparison.Ordinal);
        Assert.Contains("logix_clear_staged", later.Hint!, StringComparison.Ordinal);
        Assert.Contains("logix_connect", later.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("failed build", later.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("controller was not changed", later.Hint!, StringComparison.Ordinal);
        Assert.Equal(callsAfterFault, fake.UploadCallCount + fake.GoOnlineCallCount + fake.GoOfflineCallCount);

        // Other tools on the dead session report the same operation, and a build keeps its own wording.
        var export = Assert.IsType<ExportRungsError>(await ExportRungsHandler.ExecuteAsync(RoutineA, session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, export.Kind);
        Assert.Contains("logix_clear_staged", export.Hint!, StringComparison.Ordinal);
    }

    /// <summary>The hint follows the operation that faulted, and the generic hint makes no unconditional controller claim.</summary>
    [Fact]
    public async Task ServerFaultedHint_NamesTheFaultedOperation_AndMakesNoUnconditionalControllerClaim()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ThrowOnBuild = new OperationFailedException("LgxSrv_E_FATAL_ERROR", "BUILD");

        var build = Assert.IsType<BuildError>(await BuildHandler.ExecuteAsync(null, session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, build.Kind);
        Assert.Equal(RockwellErrorHints.OperationBuild, session.FaultOperation);
        Assert.Contains("failed build did not change the controller", build.Hint!, StringComparison.Ordinal);

        Assert.DoesNotContain("controller was not changed", RockwellErrorHints.ServerFaultedHint, StringComparison.Ordinal);
        Assert.Contains("publish", RockwellErrorHints.ServerFaultedHintFor(RockwellErrorHints.OperationPublish), StringComparison.Ordinal);
        Assert.Contains("may hold", RockwellErrorHints.ServerFaultedHintFor(RockwellErrorHints.OperationPublish), StringComparison.Ordinal);
        Assert.Equal(RockwellErrorHints.ServerFaultedHint, RockwellErrorHints.ServerFaultedHintFor(null));
    }

    /// <summary>A pending-state read on a faulted session is typed SdkServerFaulted, not an untyped error (C2-cancel).</summary>
    [Fact]
    public async Task PendingStateRead_OnFaultedSession_ReturnsSdkServerFaultedKind()
    {
        var (session, fake) = await StageMainAndKeepOtherAsync();
        fake.ThrowOnUpload = new OperationFailedException(UnhandledFaultText, "UploadAsync");
        await ClearStagedHandler.ExecuteAsync(session, "MainRoutine");
        Assert.True(session.IsFaulted);

        var finalize = Assert.IsType<FinalizeEditsError>(
            await FinalizeEditsHandler.ExecuteAsync("MainProgram", "MainRoutine", confirm: true, session));
        Assert.Equal(ErrorKinds.SdkServerFaulted, finalize.Kind);
        Assert.Equal(ErrorKinds.SessionFaulted, finalize.Session);
        Assert.Contains("logix_connect", finalize.Hint!, StringComparison.Ordinal);

        var runnerCalls = 0;
        var cancel = Assert.IsType<CancelPendingEditsError>(await CancelPendingEditsHandler.ExecuteAsync(
            "MainProgram", "MainRoutine", confirm: true,
            pendingEditsReader: (p, r, t) => PendingEditsHandler.ExecuteAsync(p, r, session, t),
            session1Runner: (_, _) => { runnerCalls++; throw new InvalidOperationException("must not launch"); },
            isStudioInstalled: () => true));
        Assert.Equal(ErrorKinds.SdkServerFaulted, cancel.Kind);
        Assert.Contains("logix_connect", cancel.Hint!, StringComparison.Ordinal);
        Assert.False(cancel.Launched);
        Assert.Equal(0, runnerCalls);

        var json = JsonSerializer.Serialize(finalize);
        Assert.Contains("\"kind\":\"SdkServerFaulted\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chosen rebuild order for a clear with two routines staged: the image is rebuilt exactly once for the whole clear
    /// (offline, upload, online), never once per routine; kept buckets are replayed only after that rebuild; and each cleared
    /// routine is verified after the replay, in routine order.
    /// </summary>
    [Fact]
    public async Task ClearStaged_TwoRoutines_RebuildsImageOnceThenReplaysThenVerifies()
    {
        // Clear everything: both routines staged, one rebuild, no replay, both verified afterwards.
        var (all, allFake) = await StageMainAndKeepOtherAsync();
        all.RecordStagedImport(new LogixProjectSession.StagedImport(RoutineB, 3, 0, KeptFragment));
        all.StagedRungBuffer[(RoutineB, 3u)] = new List<string> { "XIC(KeptTag2)OTE(KeptCoil2);" };
        allFake.ExportContentByXPath[RoutineB] = CleanExport;
        var exportsBefore = allFake.ExportXPaths.Count;
        var importsBefore = allFake.CapturedXPaths.Count;
        var allUploadsBefore = allFake.UploadCallCount;

        var clearedAll = await ClearAsync(all);

        Assert.True(clearedAll.ImageRestored);
        Assert.Equal(new[] { "offline", "upload", "online" }, allFake.RestoreCallOrder.TakeLast(3));
        Assert.Equal(allUploadsBefore + 1, allFake.UploadCallCount);        // one rebuild for the whole clear, not one per routine
        Assert.Equal(importsBefore, allFake.CapturedXPaths.Count);          // nothing replayed
        Assert.Equal(new[] { RoutineA, RoutineB }, allFake.ExportXPaths.Skip(exportsBefore).Distinct());   // verified in routine order

        // Clear one of two: one rebuild, then the other routine's bucket replayed, then verification.
        var (one, oneFake) = await StageMainAndKeepOtherAsync();
        var uploadsBefore = oneFake.UploadCallCount;
        var oneExportsBefore = oneFake.ExportXPaths.Count;
        var oneImportsBefore = oneFake.CapturedXPaths.Count;
        string? firstExportAfterReplay = null;
        oneFake.OnImportRungs = (_, _, _, _) => firstExportAfterReplay = $"imports={oneFake.CapturedXPaths.Count};exports={oneFake.ExportXPaths.Count}";

        await ClearAsync(one, "MainRoutine");

        Assert.Equal(uploadsBefore + 1, oneFake.UploadCallCount);
        Assert.Equal(oneImportsBefore + 1, oneFake.CapturedXPaths.Count);
        Assert.NotNull(firstExportAfterReplay);
        Assert.EndsWith($"exports={oneExportsBefore}", firstExportAfterReplay);   // the replay came before any verification export
    }
}
#endif
