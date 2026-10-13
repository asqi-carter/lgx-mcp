using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="ImportLogParser"/> and for the <c>importLog</c> array on a failed import result.
/// NOTE: the message shapes below (attribute layout, CDATA wording) are INFERRED from the mnemonics and
/// hex codes named in the task; no captured live ImportLog sample exists in the repo. They remain
/// unconfirmed until a live SDK capture is compared against them (pending bench verification).
/// </summary>
public class ImportLogParserTests
{
    private const string DeprecatedInstruction =
        "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION\" HexCode=\"80042cae\">" +
        "<![CDATA[Rung 0: Instruction 'GEQ' is deprecated. Update instruction to LT.]]></Exception>";

    private const string DeprecatedInExpression =
        "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION\" HexCode=\"80042cb2\">" +
        "<![CDATA[Rung 1: Deprecated instruction in expression. Update instruction to GRT.]]></Exception>";

    private const string InstrNotDefined =
        "<Exception Severity='Error' Mnemonic='RxCMP_E_AUDIT_INSTRNOTDEFINED' HexCode='80042c66'>" +
        "<![CDATA[Rung 2: Unknown instruction]]></Exception>";

    [Fact]
    public void Parse_AuditDeprecatedInstruction_YieldsFieldsAndHintQuotingReplacement()
    {
        var entry = Assert.Single(ImportLogParser.ParseMessage(DeprecatedInstruction));

        Assert.Equal("Error", entry.Severity);
        Assert.Equal("RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION", entry.Mnemonic);
        Assert.Equal("80042cae", entry.Hex);
        Assert.Equal("Rung 0: Instruction 'GEQ' is deprecated. Update instruction to LT.", entry.Text);
        Assert.NotNull(entry.Hint);
        Assert.Contains("'Update instruction to LT.'", entry.Hint);
    }

    [Fact]
    public void Parse_DeprecatedInstrInExpression_Works()
    {
        var entry = Assert.Single(ImportLogParser.ParseMessage(DeprecatedInExpression));

        Assert.Equal("Error", entry.Severity);
        Assert.Equal("RxCMP_E_DEPRCATED_INSTR_IN_EXPRESSION", entry.Mnemonic);
        Assert.Equal("80042cb2", entry.Hex);
        Assert.Contains("Update instruction to GRT.", entry.Text);
        Assert.Contains("'Update instruction to GRT.'", entry.Hint);
    }

    [Fact]
    public void Parse_AuditInstrNotDefined_UnknownInstruction_Works()
    {
        var entry = Assert.Single(ImportLogParser.ParseMessage(InstrNotDefined));

        Assert.Equal("Error", entry.Severity);
        Assert.Equal("RxCMP_E_AUDIT_INSTRNOTDEFINED", entry.Mnemonic);
        Assert.Equal("80042c66", entry.Hex);
        Assert.Equal("Rung 2: Unknown instruction", entry.Text);
        Assert.Contains("Unknown instruction", entry.Hint);
    }

    [Fact]
    public void Parse_KeepsErrorTextAndOnlyStructuredStatusMessages_WithoutDuplicates()
    {
        var log = ImportLogParser.Parse(
            new[] { "plain error line", DeprecatedInstruction },
            new[] { "Importing routine MainRoutine", DeprecatedInstruction, InstrNotDefined });

        Assert.Equal(3, log.Count);
        Assert.Null(log[0].Mnemonic);
        Assert.Equal("plain error line", log[0].Text);
        Assert.Equal("RxCMP_E_AUDIT_DEPRECATED_INSTRUCTION", log[1].Mnemonic);
        Assert.Equal("RxCMP_E_AUDIT_INSTRNOTDEFINED", log[2].Mnemonic);
    }

    [Fact]
    public void Parse_ExceptionOpenTagAndCDataOnSeparateStatusLines_YieldsOneEntry()
    {
        var log = ImportLogParser.Parse(Array.Empty<string>(), new[]
        {
            "Partial Import Rungs From Xml File started",
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_OPERAND_TAGNOTFOUND\" HexCode=\"80042c01\">",
            "<![CDATA[Referenced tag is undefined.]]>",
            "<Summary Errors=\"1\" Warnings=\"0\"/>",
        });

        var entry = Assert.Single(log);
        Assert.Equal("RxCMP_E_OPERAND_TAGNOTFOUND", entry.Mnemonic);
        Assert.Equal("80042c01", entry.Hex);
        Assert.Equal("Referenced tag is undefined.", entry.Text);
    }

    // The exact Status sequence from the run (XIC(NoSuchTag_X)OTE(Alarm_Light)).
    private static readonly string[] BenchStatus =
    {
        "Partial Import Rungs From Xml File started",
        "ImportLog <Exception Severity='Error' Mnemonic='RxCMP_E_OPERAND_TAGNOTFOUND' HexCode='80042c01'>",
        "ImportLog         <![CDATA[Referenced tag is undefined.]]>",
        "ImportLog <Summary Warnings=\"0\" Errors=\"1\"/>",
    };

    private const string BenchException =
        "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The import was aborted; no changes were made.";

    [Fact]
    public void Parse_BenchSequence_CapturesCdataTextMnemonicHexAndRungIndexZero()
    {
        var log = ImportLogParser.Parse(Array.Empty<string>(), BenchStatus, fragmentRungCount: 1);

        var entry = Assert.Single(log);
        Assert.Equal("Referenced tag is undefined.", entry.Text);
        Assert.Equal("RxCMP_E_OPERAND_TAGNOTFOUND", entry.Mnemonic);
        Assert.Equal("80042c01", entry.Hex);
        Assert.Equal(0, entry.RungIndex);
    }

    [Fact]
    public void Parse_Output_HasNoAllNullEntry()
    {
        var log = ImportLogParser.Parse(new[] { BenchException, "ImportLog   " }, BenchStatus, fragmentRungCount: 1);

        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, e => e.Severity is null && e.Mnemonic is null && e.Hex is null);
    }

    [Fact]
    public void Parse_XmlSrvMnemonicInExceptionString_IsItsOwnEntry()
    {
        var log = ImportLogParser.Parse(new[] { BenchException }, BenchStatus);

        Assert.Equal(2, log.Count);
        var xmlSrv = Assert.Single(log, e => e.Mnemonic == "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES");
        Assert.Equal("Error", xmlSrv.Severity);
        Assert.Contains("RxCMP_E_OPERAND_TAGNOTFOUND", log.Select(e => e.Mnemonic));
    }

    [Fact]
    public void Parse_RungIndex_FromRungNAndRungNumberAttribute_AndDefaultsToZeroForSingleRung()
    {
        var named = Assert.Single(ImportLogParser.ParseMessage(DeprecatedInExpression)); // "Rung 1: ..."
        Assert.Equal(1, named.RungIndex);

        var viaAttr = Assert.Single(ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_X\" HexCode=\"1\"><![CDATA[Bad <Rung Number=\"4\" Type=\"N\">]]></Exception>"));
        Assert.Equal(4, viaAttr.RungIndex);

        // No rung named: unknown for a multi-rung/unknown fragment, 0 for a single-rung one.
        Assert.Null(Assert.Single(ImportLogParser.Parse(Array.Empty<string>(), BenchStatus)).RungIndex);
        Assert.Null(Assert.Single(ImportLogParser.Parse(Array.Empty<string>(), BenchStatus, 3)).RungIndex);
        Assert.Equal(0, Assert.Single(ImportLogParser.Parse(Array.Empty<string>(), BenchStatus, 1)).RungIndex);

        // A named rung wins over the single-rung default.
        Assert.Equal(1, Assert.Single(ImportLogParser.Parse(new[] { DeprecatedInExpression }, Array.Empty<string>(), 1)).RungIndex);

        Assert.Equal(1, ImportLogParser.CountRungs("<RLLContent><Rung Number=\"0\" Type=\"N\"><Text/></Rung></RLLContent>"));
        Assert.Equal(2, ImportLogParser.CountRungs("<Rung Number=\"0\"/><Rung Number=\"1\"/>"));
    }

    [Fact]
    public void Parse_UnrecognisedMnemonic_HasNoHint()
    {
        var entry = Assert.Single(ImportLogParser.ParseMessage(
            "<Exception Severity=\"Warning\" Mnemonic=\"XMLSrv_E_OTHER\" HexCode=\"1\">text</Exception>"));
        Assert.Equal("text", entry.Text);
        Assert.Null(entry.Hint);
        Assert.DoesNotContain("hint", JsonSerializer.Serialize(entry));
    }

#if LOGIX_SDK
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string Export =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake = new ProposeRungEditRecordingFake { ExportContent = Export };
        var session = new LogixProjectSession((_, _) => System.Threading.Tasks.Task.FromResult<RockwellAutomation.LogixDesigner.ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, fake);
    }

    [Fact]
    public async Task FailedImport_IncludesImportLogArray_WithParsedEntriesAndHint()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new RockwellAutomation.LogixDesigner.OperationFailedException("RxCL_E_SOMETHING - failed", ValidXPath);
        fake.ImportLogEvents.Add((false, DeprecatedInstruction));
        fake.ImportLogEvents.Add((true, InstrNotDefined));

        var error = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, policy: null, target: "controller"));

        Assert.NotNull(error.ImportLog);
        Assert.Equal(2, error.ImportLog!.Count);
        Assert.Contains(error.ImportLog, e => e.Hex == "80042cae" && e.Hint!.Contains("Update instruction to LT."));
        Assert.Contains(error.ImportLog, e => e.Hex == "80042c66");
        Assert.Contains("\"importLog\"", JsonSerializer.Serialize(error));
    }

    [Fact]
    public async Task FailedImport_WithNothingCaptured_StillIncludesEmptyImportLog()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new RockwellAutomation.LogixDesigner.OperationFailedException("RxCL_E_SOMETHING - failed", ValidXPath);

        var error = Assert.IsType<ProposeRungEditError>(await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session, policy: null, target: "controller"));

        Assert.NotNull(error.ImportLog);
        Assert.Empty(error.ImportLog!);
        Assert.Contains("\"importLog\":[]", JsonSerializer.Serialize(error));
    }
#endif
}
