#if LOGIX_SDK
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Positions in hints, conflicts and inserts count the rungs the user will see after the
/// publish (original rungs not replaced, plus staged rungs). The staged image still holds a replaced original;
/// it is never counted. an earlier change: the real session image codes are er (staged replacement), e (staged insert),
/// rN (replaced original), dN (deleted original); I/R/D appear only in a pending-zone image
/// (bench evidence raw-r5/p6a.jsonl).
/// </summary>
[Collection("AuditLoggerDir")]
public class StagedVisiblePositionTests : IDisposable
{
    private const string Routine =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private readonly string _auditDir;

    public StagedVisiblePositionTests()
    {
        _auditDir = Path.Combine(Path.GetTempPath(), "lgx-visible-pos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditDir);
        AuditLogger.OverrideLogDir(_auditDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_auditDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string Fragment(params string[] texts) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        string.Concat(texts.Select((t, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    /// <summary>An export of MainRoutine with one (Type, Text) pair per rung.</summary>
    private static string Export(params (string Type, string Text)[] rungs) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
        "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines><Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\">" +
        "<RLLContent>" +
        string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

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

    /// <summary>
    /// The F3 sequence: a 2-rung routine, stage replace(0,1) with two rungs, ask for the hinted position, stage
    /// there. The staged image holds [new, new, replaced original, last original]; the user sees three rungs.
    /// The hint is 3 (the end of what the user sees) and a rung staged there lands after the last original rung.
    /// </summary>
    [Fact]
    public async Task F3_StageAtHintedPosition_AfterStagedReplace_LandsAfterLastOriginalRung()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        var first = await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment("XIC(N1)OTE(O1);", "XIC(N2)OTE(O2);"), session, target: "workstation");
        Assert.IsNotType<ProposeRungEditError>(first);

        // What the session image holds after the LeaveEdits replace.
        fake.ExportContent = Export(
            ("er", "XIC(N1)OTE(O1);"), ("e", "XIC(N2)OTE(O2);"), ("rN", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        // Staging on top of the staged rungs is refused; the hint names a position counted in visible rungs.
        var conflict = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Equal("StagedPositionConflict", conflict.Kind);
        var hinted = uint.Parse(Regex.Match(conflict.Hint!, @"safe position is (\d+)").Groups[1].Value);
        Assert.Equal(3u, hinted);

        // The conflict lists the staged image as [{index, text, state}].
        Assert.NotNull(conflict.StagedImage);
        Assert.Equal(4, conflict.StagedImage!.Count);
        Assert.Equal(new StagedImageRung(2, "XIC(A)OTE(OutA);", "rN"), conflict.StagedImage[2]);
        Assert.Equal("er", conflict.StagedImage[0].State);

        // Staging at the hinted position lands after the last original rung: image index 4, not 3.
        var importsBefore = fake.CapturedInsertPositions.Count;
        var second = await ProposeRungEditHandler.ExecuteAsync(
            Routine, hinted, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation");
        Assert.IsNotType<ProposeRungEditError>(second);
        Assert.Equal(importsBefore + 1, fake.CapturedInsertPositions.Count);
        Assert.Equal(4u, fake.CapturedInsertPositions[^1]);
        Assert.Equal(0u, fake.CapturedReplaceCounts[^1]);
    }

    /// <summary>
/// The same sequence with a one-rung replacement, on the pending-zone image (I/D codes, kept):
/// the hint is 2 and the insert lands at image index 3.
/// </summary>
    [Fact]
    public async Task F3_OneRungReplace_HintCountsVisibleRungsOnly()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment("XIC(N1)OTE(O1);"), session, target: "workstation"));
        fake.ExportContent = Export(("I", "XIC(N1)OTE(O1);"), ("D", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        var conflict = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Contains("safe position is 2", conflict.Hint!);

        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 2, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Equal(3u, fake.CapturedInsertPositions[^1]);
    }

    /// <summary>A position inside the visible rungs skips the hidden replaced original: visible 1 is image index 2.</summary>
    [Fact]
    public async Task InsertBeforeLastOriginal_SkipsReplacedOriginal()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment("XIC(N1)OTE(O1);"), session, target: "workstation"));
        fake.ExportContent = Export(("er", "XIC(N1)OTE(O1);"), ("rN", "XIC(A)OTE(OutA);"), ("N", "XIC(B)OTE(OutB);"));

        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 1, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Equal(2u, fake.CapturedInsertPositions[^1]);
    }

    private const string P6aOriginalReplaced = "XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run);";
    private const string P6aReplacement      = "XIC(Cmd_MotorStart)XIC(Safety_OK)XIO(E_Stop)OTE(Motor_Run);";
    private const string P6aLastOriginal     = "XIC(E_Stop)OTE(Motor_Run);";

    private static string P6aImage(params (string Type, string Text)[] extra) =>
        Export(new[] { ("er", P6aReplacement), ("rN", P6aOriginalReplaced), ("N", P6aLastOriginal) }
            .Concat(extra).ToArray());

    /// <summary>The real p6a image [er, rN, N] has a safe visible position of 2, not 3.</summary>
    [Fact]
    public async Task RealImage_ErRnN_SafeVisiblePositionIsTwo()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", P6aOriginalReplaced), ("N", P6aLastOriginal));
        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment(P6aReplacement), session, target: "workstation"));
        fake.ExportContent = P6aImage();

        var conflict = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Equal("StagedPositionConflict", conflict.Kind);
        Assert.Contains("safe position is 2", conflict.Hint!);
        Assert.Contains("rN", conflict.Error);
        Assert.Contains("dN", conflict.Error);
    }

    /// <summary>A stage at the hinted position 2 lands after XIC(E_Stop)OTE(Motor_Run); (image index 3).</summary>
    [Fact]
    public async Task RealImage_StageAtTwo_LandsAfterEStopRung()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", P6aOriginalReplaced), ("N", P6aLastOriginal));
        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment(P6aReplacement), session, target: "workstation"));
        fake.ExportContent = P6aImage();

        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 2, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
        Assert.Equal(3u, fake.CapturedInsertPositions[^1]);
        Assert.Equal(0u, fake.CapturedReplaceCounts[^1]);
    }

    /// <summary>
    /// S3 sequence on the real image: replace(0), then rungs c, d, e each at the hinted position. All three land
    /// after the last original rung (image index 2), in order.
    /// </summary>
    [Fact]
    public async Task RealImage_S3Sequence_ThreeRungsLandAfterLastOriginal()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent  = Export(("N", P6aOriginalReplaced), ("N", P6aLastOriginal));
        Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            Routine, 0, 1, Fragment(P6aReplacement), session, target: "workstation"));

        var staged = new List<(string Type, string Text)>();
        var texts  = new[] { "XIC(C)OTE(OC);", "XIC(D)OTE(OD);", "XIC(E)OTE(OE);" };
        for (int i = 0; i < texts.Length; i++)
        {
            fake.ExportContent = P6aImage(staged.ToArray());

            // The hint on the current image names the end of the visible routine: 2 + rungs staged so far.
            var conflict = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
                Routine, 0, 0, Fragment("XIC(X)OTE(OX);"), session, target: "workstation"));
            var hinted = uint.Parse(Regex.Match(conflict.Hint!, @"safe position is (\d+)").Groups[1].Value);
            Assert.Equal((uint)(2 + i), hinted);

            Assert.IsNotType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
                Routine, hinted, 0, Fragment(texts[i]), session, target: "workstation"));
            Assert.Equal((uint)(3 + i), fake.CapturedInsertPositions[^1]);   // after image index 2 (the last original)
            staged.Add(("e", texts[i]));
        }
    }

    /// <summary>A deleted original (dN) is hidden and a staged insert (e) is counted.</summary>
    [Fact]
    public void DeletedOriginal_DN_IsHiddenInVisibleCount()
    {
        var image = new List<StagedImageRung>
        {
            new(0, "a", "N"), new(1, "b", "dN"), new(2, "c", "e"), new(3, "d", "N"),
        };

        Assert.Equal(3, ProposeRungEditHandler.VisibleRungCount(image));
        Assert.Equal((0u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 0, 0));
        Assert.Equal((2u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 1, 0));
        Assert.Equal((3u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 2, 0));
        Assert.Equal((4u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 3, 0));
    }

    /// <summary>Pending-zone image (I/R/D, kept): R and D hidden, I counted.</summary>
    [Fact]
    public void PendingZoneImage_IRD_HiddenOriginalsSkipped()
    {
        var image = new List<StagedImageRung>
        {
            new(0, "a", "I"), new(1, "b", "R"), new(2, "c", "N"), new(3, "d", "D"), new(4, "e", "N"),
        };

        Assert.Equal(3, ProposeRungEditHandler.VisibleRungCount(image));
        Assert.Equal((2u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 1, 0));
        Assert.Equal((4u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 2, 0));
    }

    /// <summary>Unit check of the visible-to-image translation, with and without hidden rungs.</summary>
    [Fact]
    public void ToImageCoordinates_SkipsHiddenReplacedOriginals()
    {
        var image = new List<StagedImageRung>
        {
            new(0, "a", "er"), new(1, "b", "rN"), new(2, "c", "N"), new(3, "d", "dN"), new(4, "e", "N"),
        };

        Assert.Equal((0u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 0, 0));
        Assert.Equal((2u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 1, 0));
        Assert.Equal((4u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 2, 0));
        Assert.Equal((5u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 3, 0));   // end of the routine
        Assert.Equal((6u, 0u), ProposeRungEditHandler.ToImageCoordinates(image, 4, 0));   // past the end keeps its distance
        Assert.Equal((2u, 3u), ProposeRungEditHandler.ToImageCoordinates(image, 1, 2));   // replaces c, (hidden d), e
        Assert.Equal(3, ProposeRungEditHandler.VisibleRungCount(image));
    }
}
#endif
