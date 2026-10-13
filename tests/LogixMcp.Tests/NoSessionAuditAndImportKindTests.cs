#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// logix-mcp-r7k0.8 (N8, N9): a NoActiveSession refusal leaves a <c>refused</c> audit line; the self-closed
/// raw-fragment refusal carries kind <c>EmptyRoutineEnvelope</c>; raw SDK import mnemonics map to kinds on every path.
/// </summary>
[Collection("AuditLoggerDir")]
public class NoSessionAuditAndImportKindTests : IDisposable
{
    private const string Xpath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string SelfClosedFragment =
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\"/>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private readonly string _auditDir;

    public NoSessionAuditAndImportKindTests()
    {
        _auditDir = Path.Combine(Path.GetTempPath(), "lgx-nosession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_auditDir);
        AuditLogger.OverrideLogDir(_auditDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_auditDir, recursive: true); } catch { /* best-effort */ }
    }

    private List<JsonElement> AuditLines() =>
        File.Exists(Path.Combine(_auditDir, "audit.jsonl"))
            ? File.ReadAllLines(Path.Combine(_auditDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .ToList()
            : [];

    private static string RungsL5X() =>
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ImportFailureClassificationFake Fake)> OpenSessionAsync()
    {
        var fake    = new ImportFailureClassificationFake { ExportContent = RungsL5X() };
        var session = new LogixProjectSession(
            (_, _) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    // ── N8 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoActiveSessionRefusal_WritesRefusedAuditLine()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var finalizeJson = await tools.FinalizeEdits("MainProgram", "MainRoutine", confirm: true);
        var clearJson    = await tools.ClearStaged();
        var writeResult  = await WriteTagHandler.ExecuteAsync("Motor_Run", "1", new TagSessionService());

        Assert.Equal(ErrorKinds.NoActiveSession,
            JsonDocument.Parse(finalizeJson).RootElement.GetProperty("kind").GetString());
        Assert.Equal(ErrorKinds.NoActiveSession,
            JsonDocument.Parse(clearJson).RootElement.GetProperty("kind").GetString());
        Assert.IsType<WriteTagError>(writeResult);

        var refused = AuditLines()
            .Where(e => e.GetProperty("event").GetString() == "tool_refused")
            .ToList();
        Assert.All(refused, e =>
        {
            Assert.Equal("refused", e.GetProperty("outcome").GetString());
            Assert.Equal(ErrorKinds.NoActiveSession, e.GetProperty("kind").GetString());
        });
        Assert.Equal(
            new[] { "logix_clear_staged", "logix_finalize_edits", "logix_write" },
            refused.Select(e => e.GetProperty("operation").GetString()!).OrderBy(x => x).ToArray());
        var finalize = refused.Single(e => e.GetProperty("operation").GetString() == "logix_finalize_edits");
        Assert.Equal("MainProgram", finalize.GetProperty("program").GetString());
        Assert.Equal("MainRoutine", finalize.GetProperty("routine").GetString());
    }

    [Fact]
    public async Task NoActiveSessionRungEditRefusal_WritesRefusedRungEditLine()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService());

        var json = await tools.GetProposeRungEditResponseAsync(
            Xpath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, target: "controller", confirmedByUser: false);

        // Whatever gate answers first, the call writes exactly one refused rung_edit line with the kind it returned.
        var kind = JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString();
        var line = Assert.Single(AuditLines(), e => e.GetProperty("event").GetString() == "rung_edit");
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal(kind, line.GetProperty("kind").GetString());
    }

    // ── N8: EmptyRoutineEnvelope ────────────────────────────────────────────

    [Fact]
    public void SelfClosedRawFragmentRefusal_HasKindEmptyRoutineEnvelope()
    {
        var error = ProposeRungEditHandler.ValidateFragmentEnvelope(SelfClosedFragment);

        Assert.NotNull(error);
        Assert.Equal("EmptyRoutineEnvelope", error!.Kind);
        Assert.Contains("self-closed", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfClosedRawFragmentRefusal_ThroughHandler_IsAuditedWithKind()
    {
        var (session, fake) = await OpenSessionAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Xpath, 0, 0, SelfClosedFragment, session, policy: null, target: "controller");

        Assert.Equal("EmptyRoutineEnvelope", Assert.IsType<ProposeRungEditError>(result).Kind);
        Assert.Equal(0, fake.ImportCallCount);
        var line = Assert.Single(AuditLines(), e => e.GetProperty("event").GetString() == "rung_edit");
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal("EmptyRoutineEnvelope", line.GetProperty("kind").GetString());
    }

    // ── N9 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PieControllerEditsExist_MapsToPendingEditsBlockImport_OnAnyPath()
    {
        // The tier-1 read finds nothing pending (the zone is not visible in the export), so only the SDK's own
        // mnemonic can name the cause. It is on the exception text for one path and only in the ImportLog for the other.
        const string pie = "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM";

        var (session1, fake1) = await OpenSessionAsync();
        fake1.ThrowOnImport = new OperationFailedException($"{pie} - accepted edits exist.", Xpath);
        var r1 = await ProposeRungEditHandler.ExecuteAsync(
            Xpath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session1, policy: null, target: "controller");
        Assert.Equal("PendingEditsBlockImport", Assert.IsType<ProposeRungEditError>(r1).Kind);

        var (session2, fake2) = await OpenSessionAsync();
        fake2.ImportLogMessagesToRaise = new[]
        {
            $"<Exception Severity='Error' Mnemonic='{pie}'>accepted edits exist.</Exception>",
        };
        fake2.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors.", Xpath);
        var r2 = await ProposeRungEditHandler.ExecuteAsync(
            Xpath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session2, policy: null, target: "controller");
        Assert.Equal("PendingEditsBlockImport", Assert.IsType<ProposeRungEditError>(r2).Kind);

        // The shared mapping used by the other import paths (create routine) agrees.
        Assert.Equal("PendingEditsBlockImport", RockwellErrorHints.ImportFailureKind($"{pie} - x"));
        Assert.Equal("PendingEditsBlockImport", RockwellErrorHints.PendingEditsBlockImportKind);
    }

    private const string PieMnemonic = "XMLSrv_E_PIE_CONTROLLER_EDITS_EXIST_FOR_PROGRAM";

    /// <summary>Stages or publishes with a fake that throws the PIE mnemonic from the import, on the given target.</summary>
    private static async Task<ProposeRungEditError> ThrowPieAsync(string target, bool verifyOnStage = false)
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ThrowOnImport = new OperationFailedException(
            $"{PieMnemonic} - The program contains accepted logic edits.", Xpath);
        if (verifyOnStage)
            session.StageVerifyReplay = (_, _, _, _, _) =>
                Task.FromResult(new ImportRejectReplayOutcome(Array.Empty<RejectReason>(), null));
        var result = await ProposeRungEditHandler.ExecuteAsync(
            Xpath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: target, verifyOnStage: verifyOnStage);
        return Assert.IsType<ProposeRungEditError>(result);
    }

    [Fact]
    public async Task PieControllerEditsExist_OnWorkstationPath_MapsToPendingEditsBlockImport()
    {
        var err = await ThrowPieAsync("workstation");
        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.DoesNotContain("export the rungs", err.Hint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("PendingEditsBlockImport:", err.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PieControllerEditsExist_OnWorkstationPlusVerifyOnStage_MapsToPendingEditsBlockImport()
    {
        var err = await ThrowPieAsync("workstation", verifyOnStage: true);
        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.DoesNotContain("Fix the rung", err.Hint ?? string.Empty);
    }

    [Fact]
    public async Task PieControllerEditsExist_OnControllerAndLivePaths_MapsToPendingEditsBlockImport()
    {
        var controller = await ThrowPieAsync("controller");
        var live       = await ThrowPieAsync("live");
        Assert.Equal("PendingEditsBlockImport", controller.Kind);
        Assert.Equal("PendingEditsBlockImport", live.Kind);
    }

    [Fact]
    public async Task PieControllerEditsExist_MessageAndHint_AreIdenticalAcrossAllFourPaths()
    {
        var controller = await ThrowPieAsync("controller");
        var live       = await ThrowPieAsync("live");
        var workstn    = await ThrowPieAsync("workstation");
        var verified   = await ThrowPieAsync("workstation", verifyOnStage: true);

        foreach (var other in new[] { live, workstn, verified })
        {
            Assert.Equal(controller.Error, other.Error);
            Assert.Equal(controller.Hint, other.Hint);
            Assert.Equal(controller.Kind, other.Kind);
        }
    }

    [Fact]
    public async Task ImportAbortedNoChanges_WithNoOtherCause_MapsToImportRejectedByImporter_WithCapturedLog()
    {
        var (session, fake) = await OpenSessionAsync();
        const string logEntry =
            "<Exception Severity='Error' Mnemonic='XMLSrv_E_IMPORT_ABORTED_NO_CHANGES'>" +
            "The Import was cancelled due to errors. No changes were made.</Exception>";
        fake.ImportLogMessagesToRaise = new[] { logEntry };
        fake.ThrowOnImport = new OperationFailedException(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors.", Xpath);

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Xpath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, policy: null, target: "controller");

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ImportRejectedByImporter", err.Kind);
        Assert.Contains(logEntry, err.Error, StringComparison.Ordinal);

        Assert.Equal("ImportRejectedByImporter",
            RockwellErrorHints.ImportFailureKind("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - x"));
        Assert.Null(RockwellErrorHints.ImportFailureKind("some other failure"));
    }
}
#endif
