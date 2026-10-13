#if LOGIX_SDK
using System;
using System.Threading.Tasks;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A publish that loses a race against another client fails with <c>RxE_ILLEGAL_OPERATIONS</c>
/// (<c>80042021</c>). With no rung-level reason it is a concurrency error, not an importer reject: pending edits
/// afterwards give <c>PendingEditsBlockImport</c>, none give a retryable <c>ControllerBusy</c>. A rung-level reason
/// keeps <c>ImportRejectedByImporter</c>. Every test builds its own session and fake; no host-wide state is used.
/// </summary>
public class PublishRaceClassificationTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string IllegalOperations =
        "RxE_ILLEGAL_OPERATIONS - Sequence of operations not allowed (80042021)";

    private static string RungsL5X(params (int Number, string Type)[] rungs)
    {
        var rungXml = string.Join(string.Empty, Array.ConvertAll(rungs, r =>
            $"<Rung Number=\"{r.Number}\" Type=\"{r.Type}\"><Text><![CDATA[NOP();]]></Text></Rung>"));
        return "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs>" +
               "<Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\"><RLLContent>" +
               rungXml +
               "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    }

    private static async Task<(LogixProjectSession Session, ImportFailureClassificationFake Fake)> OpenAsync()
    {
        var fake = new ImportFailureClassificationFake();
        var session = new LogixProjectSession(
            (_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static Task<object> PublishAsync(LogixProjectSession session) =>
        ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, policy: null, target: "controller");

    [Fact]
    public async Task IllegalOperations_NoReasons_WithPendingEdits_IsPendingEditsBlockImport()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new OperationFailedException(IllegalOperations, ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"), (1, "I"));   // the winner's edit is pending once the import fails

        var err = Assert.IsType<ProposeRungEditError>(await PublishAsync(session));

        Assert.Equal("PendingEditsBlockImport", err.Kind);
        Assert.Null(err.RetryAfterSeconds);
    }

    [Fact]
    public async Task IllegalOperations_NoReasons_NoPendingEdits_IsRetryableControllerBusy()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new OperationFailedException(IllegalOperations, ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));

        var err = Assert.IsType<ProposeRungEditError>(await PublishAsync(session));

        Assert.Equal("ControllerBusy", err.Kind);
        Assert.True(err.RetryAfterSeconds > 0);
        Assert.Contains("was editing this program at the same moment", err.Hint, StringComparison.OrdinalIgnoreCase);
        Assert.Null(err.RejectReasons);
    }

    [Fact]
    public async Task IllegalOperations_NamedOnlyInImportLog_NoPendingEdits_IsControllerBusy()
    {
        var (session, fake) = await OpenAsync();
        fake.ImportLogMessagesToRaise = new[]
        {
            "<Exception Severity='Error' Mnemonic='RxE_ILLEGAL_OPERATIONS' HexCode='80042021'>Sequence of operations not allowed</Exception>",
        };
        fake.ThrowOnImport = new OperationFailedException("The import failed.", ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));

        var err = Assert.IsType<ProposeRungEditError>(await PublishAsync(session));

        Assert.Equal("ControllerBusy", err.Kind);
    }

    [Fact]
    public async Task IllegalOperations_NoReasons_PendingEditsReadFails_IsRetryableControllerBusy()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new OperationFailedException(IllegalOperations, ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));
        fake.ThrowOnExportAfterImport = new InvalidOperationException("export failed");   // the re-check read fails

        var err = Assert.IsType<ProposeRungEditError>(await PublishAsync(session));

        // An unreadable edit state is reported as the retryable busy error, never as a pending-edit claim.
        Assert.Equal("ControllerBusy", err.Kind);
        Assert.True(err.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task IllegalOperations_WithRungLevelReasons_StaysImportRejectedByImporter()
    {
        var (session, fake) = await OpenAsync();
        fake.ImportLogMessagesToRaise = new[]
        {
            "<Exception Severity='Error' Mnemonic='RxCMP_E_OPERAND_TAGNOTFOUND' HexCode='80042c01'>Rung 0: Referenced tag is undefined.</Exception>",
        };
        fake.ThrowOnImport = new OperationFailedException(IllegalOperations, ValidXPath);
        fake.ExportContent = RungsL5X((0, "N"));

        var err = Assert.IsType<ProposeRungEditError>(await PublishAsync(session));

        Assert.Equal("ImportRejectedByImporter", err.Kind);
        Assert.Contains(err.RejectReasons!, r => r.Mnemonic == "RxCMP_E_OPERAND_TAGNOTFOUND");
    }
}
#endif
