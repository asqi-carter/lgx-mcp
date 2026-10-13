#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change requirement 8 (task logix-mcp-64f5.8): the rung-edit audit line is written once per call and
/// tells the truth: after the census, with outcome <c>proposed</c>, <c>publish-mismatch</c>,
/// <c>nothing-staged</c> or <c>error</c>; a refusal before the import writes <c>refused</c> and its kind.
/// </summary>
[Collection("StaticHandlerHooks")]
public class RungEditAuditTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string StagedRungFragment =
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\">" +
        "<Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung></RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private const string StagedCoilPendingExport =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"I\"><Text><![CDATA[XIC(StagedTag)OTE(StagedCoil);]]></Text></Rung>" +
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

    /// <summary>Runs <paramref name="body"/> with the audit log redirected and returns its rung_edit lines.</summary>
    private static async Task<List<JsonElement>> CaptureRungEditsAsync(Func<Task> body)
    {
        var auditDir = Path.Combine(Path.GetTempPath(), "lgx-rung-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditDir);
        AuditLogger.OverrideLogDir(auditDir);
        try
        {
            await body();
            var file = Path.Combine(auditDir, "audit.jsonl");
            if (!File.Exists(file))
                return [];
            return File.ReadAllLines(file)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .Where(e => e.GetProperty("event").GetString() == "rung_edit")
                // "sending" intent lines (logix-mcp-qpqy.4) are not a call's outcome line; they are asserted separately.
                .Where(e => e.GetProperty("outcome").GetString() != AuditLogger.RungEditSendingOutcome)
                .ToList();
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(auditDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task CensusMismatch_WritesOnePublishMismatchLine_AndNoProposedLine()
    {
        var (session, _) = await OpenSessionAsync();

        var lines = await CaptureRungEditsAsync(async () =>
        {
            // Stage a rung, then publish while the export shows no pending I/IR rung: the census mismatches.
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));
            var publish = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");
            Assert.Equal("PublishMismatch", Assert.IsType<ProposeRungEditMismatch>(publish).Kind);
        });

        // Two calls (stage, publish) → two lines; the publish call's line is the mismatch.
        Assert.Equal(2, lines.Count);
        Assert.Equal("proposed", lines[0].GetProperty("outcome").GetString());
        Assert.Equal("controller", lines[1].GetProperty("destination").GetString());
        Assert.Equal("publish-mismatch", lines[1].GetProperty("outcome").GetString());
        Assert.DoesNotContain(lines.Where(l => l.GetProperty("destination").GetString() == "controller"),
            l => l.GetProperty("outcome").GetString() == "proposed");
    }

    [Fact]
    public async Task CensusMatch_WritesOneProposedLine_ForThePublishCall()
    {
        var (session, fake) = await OpenSessionAsync();

        var lines = await CaptureRungEditsAsync(async () =>
        {
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));
            fake.ExportContent = StagedCoilPendingExport;
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller"));
        });

        var publishLine = Assert.Single(lines, l => l.GetProperty("destination").GetString() == "controller");
        Assert.Equal("proposed", publishLine.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WrongMnemonicSet_WritesOneRefusedLine_WithTheKind()
    {
        var tags = new TagSessionService();
        tags.SetTargetRevision(new TargetRevisionInfo(
            38, TargetRevision.MnemonicSetFor(38), TargetRevisionSource.MajorRev, null));
        var tools = new LogixSdkTools(new ProjectService(), tags,
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto));
        var (session, fake) = await OpenSessionAsync();

        string json = "";
        var lines = await CaptureRungEditsAsync(async () =>
        {
            json = await tools.GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, null, session,
                rungs: [new RungInput("LES(x,500)OTE(y);")]);
        });

        Assert.Equal("WrongMnemonicSet", JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString());
        Assert.Empty(fake.CapturedImportOptions);
        var line = Assert.Single(lines);
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal("WrongMnemonicSet", line.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task HandlerRefusalBeforeImport_WritesOneRefusedLine_WithTheKind()
    {
        var (session, fake) = await OpenSessionAsync();

        var lines = await CaptureRungEditsAsync(async () =>
        {
            // An invalid XPath is refused before any import.
            var result = await ProposeRungEditHandler.ExecuteAsync(
                "Controller/Programs/Program[@Nme='MainProgram']", 0, 0, StagedRungFragment, session,
                target: "workstation");
            Assert.IsType<ProposeRungEditError>(result);
        });

        Assert.Empty(fake.CapturedImportOptions);
        var line = Assert.Single(lines);
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal("InvalidRoutineXPath", line.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task CapabilityAndConfirmGateRefusals_WriteOneRefusedLine_WithTheKind()
    {
        var (session, fake) = await OpenSessionAsync();

        // Live writes disabled: controller target refused at the capability gate.
        var disabled = new LogixSdkTools(new ProjectService(), new TagSessionService(), new Capabilities(LiveWritesEnabled: false));
        var disabledLines = await CaptureRungEditsAsync(async () =>
            await disabled.GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "controller"));
        var d = Assert.Single(disabledLines);
        Assert.Equal("refused", d.GetProperty("outcome").GetString());
        Assert.Equal("CapabilityDisabled", d.GetProperty("kind").GetString());

        // Confirm mode without confirmedByUser: refused at the confirm gate.
        var confirm = new LogixSdkTools(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Confirm));
        var confirmLines = await CaptureRungEditsAsync(async () =>
            await confirm.GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "controller"));
        var c = Assert.Single(confirmLines);
        Assert.Equal("refused", c.GetProperty("outcome").GetString());
        Assert.Equal("ConfirmationRequired", c.GetProperty("kind").GetString());
        Assert.Empty(fake.CapturedImportOptions);
    }

    [Fact]
    public async Task EachCall_WritesExactlyOneRungEditLine()
    {
        var (session, fake) = await OpenSessionAsync();

        var lines = await CaptureRungEditsAsync(async () =>
        {
            // 1: refused (invalid XPath)   2: staged (proposed)   3: mismatch   4: nothing-staged
            await ProposeRungEditHandler.ExecuteAsync(
                "Controller/Programs/Program[@Nme='MainProgram']", 0, 0, StagedRungFragment, session,
                target: "workstation");
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation");
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");
            session.StagedRungBuffer.Clear();
            session.StagedTagBuffer.Clear();
            session.StagedRungCount   = 0;
            session.HasStagedRungEdit = false;
            await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");
        });

        Assert.Equal(
            ["refused", "proposed", "publish-mismatch", "nothing-staged"],
            lines.Select(l => l.GetProperty("outcome").GetString()).ToArray());
    }

    private static string NewAuditDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgx-rung-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<JsonElement> ReadRungEdits(string dir)
    {
        var file = Path.Combine(dir, "audit.jsonl");
        if (!File.Exists(file)) return [];
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Where(e => e.GetProperty("event").GetString() == "rung_edit")
            .ToList();
    }

    [Fact]
    public async Task SendingLine_WithCorrelationId_IsWrittenBeforeBlockingImportReturns()
    {
        var (session, fake) = await OpenSessionAsync();
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));

            var hang = new TaskCompletionSource();
            fake.BlockImportUntil = hang;
            var publish = ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");

            // The import is blocked inside the fake: the intent line must already be on disk.
            List<JsonElement> during = [];
            for (var i = 0; i < 200 && !during.Any(l => l.GetProperty("outcome").GetString() == "sending"); i++)
            {
                await Task.Delay(25);
                during = ReadRungEdits(dir);
            }
            Assert.False(publish.IsCompleted);
            var sending = Assert.Single(during, l => l.GetProperty("outcome").GetString() == "sending");
            Assert.False(string.IsNullOrEmpty(sending.GetProperty("correlation").GetString()));
            Assert.Equal("controller", sending.GetProperty("destination").GetString());
            Assert.DoesNotContain(during, l => l.GetProperty("destination").GetString() == "controller"
                                              && l.GetProperty("outcome").GetString() != "sending");

            hang.SetResult();
            await publish;
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OutcomeLine_CompletesSendingLine_WithSameCorrelationId()
    {
        var (session, fake) = await OpenSessionAsync();
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));
            fake.ExportContent = StagedCoilPendingExport;
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller"));

            var lines = ReadRungEdits(dir);
            var sending = Assert.Single(lines, l => l.GetProperty("outcome").GetString() == "sending");
            var final = Assert.Single(lines, l => l.GetProperty("destination").GetString() == "controller"
                                                 && l.GetProperty("outcome").GetString() == "proposed");
            Assert.Equal(sending.GetProperty("correlation").GetString(), final.GetProperty("correlation").GetString());
            Assert.Empty(AuditLogger.FindUnfinishedRungEdits());
            // The workstation stage imported nothing to the controller: no intent line, no correlation.
            Assert.DoesNotContain(lines, l => l.GetProperty("destination").GetString() == "workstation"
                                             && l.TryGetProperty("correlation", out _));
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task NextConnect_UnfinishedSendingLineInPendingZone_WritesOrphanedLine_AndWarns()
    {
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            // A server killed mid-import: the intent line exists, no outcome line follows.
            var inZone = AuditLogger.NewRungEditCorrelation();
            var noZone = AuditLogger.NewRungEditCorrelation();
            AuditLogger.LogRungEditSending(ValidXPath, 3, "controller", inZone);
            AuditLogger.LogRungEditSending(
                "Controller/Programs/Program[@Name='Other']/Routines/Routine[@Name='R']", 0, "controller", noZone);

            Task<IReadOnlyList<DoctorZone>> Zones(TagSessionService _, CancellationToken __) =>
                Task.FromResult<IReadOnlyList<DoctorZone>>(
                [
                    new DoctorZone("MainProgram", "MainRoutine", "pending"),
                    new DoctorZone("Other", "R", "none"),
                ]);

            var projects = new ProjectService();
            projects.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
            var connect = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService(),
                pendingZoneReader: Zones);

            var ok = Assert.IsType<ConnectResult>(connect);
            var warning = Assert.Single(ok.Warnings!);
            Assert.Contains("orphaned", warning);
            Assert.Contains(inZone, warning);
            Assert.DoesNotContain(noZone, warning);
            Assert.Contains(warning, ok.Message, StringComparison.Ordinal);

            var orphaned = Assert.Single(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "orphaned");
            Assert.Equal(inZone, orphaned.GetProperty("correlation").GetString());

            // The orphaned line completes the intent: a later connect does not report it again.
            var again = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService(),
                pendingZoneReader: Zones);
            Assert.Null(Assert.IsType<ConnectResult>(again).Warnings);
            Assert.Equal(noZone, Assert.Single(AuditLogger.FindUnfinishedRungEdits()).Correlation);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private const string OtherXPath =
        "Controller/Programs/Program[@Name='Other']/Routines/Routine[@Name='R']";

    private static async Task<ConnectResult> ConnectWithZonesAsync(params DoctorZone[] zones)
    {
        Task<IReadOnlyList<DoctorZone>> Zones(TagSessionService _, CancellationToken __) =>
            Task.FromResult<IReadOnlyList<DoctorZone>>(zones);
        var projects = new ProjectService();
        projects.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X"));
        return Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projects, new TagSessionService(),
            pendingZoneReader: Zones));
    }

    [Fact]
    public async Task P6kSequence_StaleSendingLine_NoOrphanWarning_WritesAbandoned()
    {
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            // The server was killed mid-publish; the zone was removed while it was down; a later normal publish
            // left a different, legitimate zone on the same routine.
            var stale = AuditLogger.NewRungEditCorrelation();
            AuditLogger.LogRungEditSending(ValidXPath, 0, "controller", stale, ["XIC(OldTag)OTE(OldCoil);"]);

            var ok = await ConnectWithZonesAsync(
                new DoctorZone("MainProgram", "MainRoutine", "untested", ["XIC(NewTag)OTE(NewCoil);"]));

            Assert.Null(ok.Warnings);
            var abandoned = Assert.Single(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "abandoned");
            Assert.Equal(stale, abandoned.GetProperty("correlation").GetString());
            Assert.DoesNotContain(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "orphaned");
            Assert.Empty(AuditLogger.FindUnfinishedRungEdits());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task TrueOrphan_RungTextInPendingZone_IsStillDetected()
    {
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            var killed = AuditLogger.NewRungEditCorrelation();
            AuditLogger.LogRungEditSending(ValidXPath, 2, "controller", killed,
                ["XIC(A)OTE(B);", "XIC(C)OTE(D);"]);

            // The zone holds one of the rungs the killed import sent (whitespace differs, as in an export).
            var ok = await ConnectWithZonesAsync(
                new DoctorZone("MainProgram", "MainRoutine", "untested", ["XIC(C) OTE(D);"]));

            var warning = Assert.Single(ok.Warnings!);
            Assert.Contains("orphaned", warning);
            Assert.Contains(killed, warning);
            var orphaned = Assert.Single(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "orphaned");
            Assert.Equal(killed, orphaned.GetProperty("correlation").GetString());
            Assert.DoesNotContain(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "abandoned");
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void CompletePublishLine_ClosesEarlierSendingLines_OfThatRoutineOnly()
    {
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            var earlier = AuditLogger.NewRungEditCorrelation();
            var other   = AuditLogger.NewRungEditCorrelation();
            var later   = AuditLogger.NewRungEditCorrelation();
            AuditLogger.LogRungEditSending(ValidXPath, 0, "controller", earlier, ["XIC(A)OTE(B);"]);
            AuditLogger.LogRungEditSending(OtherXPath, 0, "controller", other, ["XIC(A)OTE(B);"]);
            AuditLogger.LogRungEditSending(ValidXPath, 0, "controller", later, ["XIC(E)OTE(F);"]);
            AuditLogger.LogRungEdit(ValidXPath, 0, "proposed", "online", destination: "controller", correlation: later);

            var abandoned = Assert.Single(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "abandoned");
            Assert.Equal(earlier, abandoned.GetProperty("correlation").GetString());
            // The other routine's line stays open; the completed one is closed by its own line.
            Assert.Equal(other, Assert.Single(AuditLogger.FindUnfinishedRungEdits()).Correlation);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SendingLine_RecordsItsRungTexts()
    {
        var (session, fake) = await OpenSessionAsync();
        var dir = NewAuditDir();
        AuditLogger.OverrideLogDir(dir);
        try
        {
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, StagedRungFragment, session, target: "workstation"));
            fake.ExportContent = StagedCoilPendingExport;
            Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller"));

            var sending = Assert.Single(ReadRungEdits(dir), l => l.GetProperty("outcome").GetString() == "sending");
            var texts = sending.GetProperty("rung_texts").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("XIC(StagedTag)OTE(StagedCoil);", texts);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
#endif
