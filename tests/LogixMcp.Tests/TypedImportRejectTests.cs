#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change item 6 (G26, G27, G29, G30): typed, specific import rejects for External Access on alarm types,
/// motion tags, writes to another program's Output parameter, and the <c>ImportRejectedByImporter</c> hint.
/// The reject texts below stand in for the ones captured in <c>robust-post0167/raw-r6/</c>, which is not in this
/// tree; replace them with the captured text when that directory is available.
/// </summary>
public class TypedImportRejectTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ProgramsXml =
        "<RSLogix5000Content><Controller><Programs>" +
        "<Program Name=\"MainProgram\"><Tags><Tag Name=\"LocalFlag\" DataType=\"BOOL\"/></Tags>" +
        "<Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"/></Routines></Program>" +
        "<Program Name=\"Line_A\"><Tags>" +
        "<Tag Name=\"P6_Out\" DataType=\"BOOL\" Usage=\"Output\"/>" +
        "<Tag Name=\"P6_Pub\" DataType=\"BOOL\" Usage=\"Public\"/>" +
        "</Tags><Routines><Routine Name=\"R\" Type=\"RLL\"/></Routines></Program>" +
        "</Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        fake.ExportContentByXPath["Controller/Tags"] =
            "<RSLogix5000Content><Controller Name=\"C\"><Tags/></Controller></RSLogix5000Content>";
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] = ProgramsXml;
        fake.ExportContentByXPath["Controller/AddOnInstructionDefinitions"] =
            "<RSLogix5000Content><Controller><AddOnInstructionDefinitions/></Controller></RSLogix5000Content>";
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline        = true;
        session.IsCorrelated    = true;
        session.DesignerVersion = new DesignerVersion(38, 0, DesignerVersionSource.ProjectRevision);
        return (session, fake);
    }

    // ── G26: External Access ──────────────────────────────────────────────────

    [Theory]
    [InlineData("ALARM_DIGITAL")]
    [InlineData("ALARM_ANALOG")]
    [InlineData("alarm_digital")]
    public void AlarmTypes_AreCreatedReadWrite_AndReportedInExternalAccessForced(string type)
    {
        Assert.NotNull(RungFragmentBuilder.ReadWriteRequiredReason(type));
        Assert.Equal("Read/Write", RungFragmentBuilder.ExternalAccessFor(type));

        var entries = RungFragmentBuilder.ForcedReadWriteEntries(
            new[] { new TagInput("Alm1", type), new TagInput("Lamp", "BOOL") }, Array.Empty<string>());
        var entry = Assert.Single(entries);
        Assert.StartsWith("Alm1: Read/Write (", entry);
        Assert.Contains(type.ToUpperInvariant(), entry.ToUpperInvariant());
    }

    [Fact]
    public void AlarmTagDeclaration_BuildsReadWriteExternalAccessAttribute()
    {
        const string envelope =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" TargetName=\"MainRoutine\" " +
            "TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\"><Controller Use=\"Context\" Name=\"C\">" +
            "<DataTypes Use=\"Context\"/><Programs Use=\"Context\"><Program Use=\"Context\" Name=\"MainProgram\">" +
            "<Routines Use=\"Context\"><Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent/></Routine>" +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>";
        var doc = System.Xml.Linq.XDocument.Parse(RungFragmentBuilder.BuildOnline(envelope, 0,
            new[] { new RungInput("NOP();") },
            new[] { new TagInput("Dig", "ALARM_DIGITAL"), new TagInput("Ana", "ALARM_ANALOG"), new TagInput("Lamp", "BOOL") }));
        string? Access(string n) => (string?)doc.Descendants("Tags").SelectMany(x => x.Elements("Tag"))
            .Single(t => (string?)t.Attribute("Name") == n).Attribute("ExternalAccess");
        Assert.Equal("Read/Write", Access("Dig"));
        Assert.Equal("Read/Write", Access("Ana"));
        Assert.Equal("Read Only", Access("Lamp"));
    }

    [Fact]
    public void OtherReadOnlyRejectedType_GetsTypedErrorNamingTheType()
    {
        var reasons = new[]
        {
            new RejectReason(0, "Tag Pid_1: Incompatible External Access Value for this data type.",
                "RxCMP_E_INCOMPATIBLE_EXTERNAL_ACCESS"),
        };
        var declared = new Dictionary<string, string> { ["Pid_1"] = "PIDE", ["Lamp"] = "BOOL" };

        var err = ProposeRungEditHandler.ReadOnlyAccessRejection(reasons, [], declared);

        Assert.NotNull(err);
        Assert.Equal(ProposeRungEditHandler.ReadOnlyAccessRejectedKind, err!.Kind);
        Assert.Contains("PIDE", err.Error);
        Assert.DoesNotContain("BOOL", err.Error);
    }

    [Fact]
    public void ReadOnlyReject_NamingTheTypeInTheText_NamesThatType()
    {
        var reasons = new[] { new RejectReason(null, "Incompatible External Access Value for data type FOO_TYPE.") };
        var err = ProposeRungEditHandler.ReadOnlyAccessRejection(reasons, [], new Dictionary<string, string>());
        Assert.Contains("FOO_TYPE", err!.Error);
    }

    [Fact]
    public void ReadOnlyReject_OnATypeAlreadyCreatedReadWrite_IsNotReportedAsUnknown()
    {
        var reasons = new[] { new RejectReason(0, "Tag Hmi_1: Incompatible External Access Value.") };
        Assert.Null(ProposeRungEditHandler.ReadOnlyAccessRejection(
            reasons, [], new Dictionary<string, string> { ["Hmi_1"] = "HMIBC" }));
    }

    [Fact]
    public async Task StageVerify_ReadOnlyRejectOnUnknownType_ReturnsTypedError()
    {
        var (session, _) = await OpenAsync();
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome(
            new[] { new RejectReason(0, "Tag Pid_1: Incompatible External Access Value for this data type.") }, null));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, policy: null, target: "workstation", verifyOnStage: true,
            rungs: new[] { new RungInput("XIC(Pid_1.EN)OTE(Lamp);") },
            tags: new[] { new TagInput("Pid_1", "PIDE"), new TagInput("Lamp", "BOOL") });

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal(ProposeRungEditHandler.ReadOnlyAccessRejectedKind, err.Kind);
        Assert.Contains("PIDE", err.Error);
    }

    // ── G27: motion tags ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("AXIS_VIRTUAL")]
    [InlineData("AXIS_CIP_DRIVE")]
    [InlineData("AXIS_SERVO")]
    [InlineData("MOTION_GROUP")]
    [InlineData("COORDINATE_SYSTEM")]
    public async Task MotionTag_IsRefusedAtStage_WithNoControllerRoundTrip(string type)
    {
        var (session, fake) = await OpenAsync();
        int replays = 0;
        session.StageVerifyReplay = (_, _, _, _, _) => { replays++; return Task.FromResult(new ImportRejectReplayOutcome([], null)); };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, policy: null, target: "workstation",
            rungs: new[] { new RungInput("NOP();") }, tags: new[] { new TagInput("Ax1", type) });

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("MotionTagRequiresOffline", err.Kind);
        Assert.Contains("Ax1", err.Error);
        Assert.Contains(type, err.Error);
        Assert.DoesNotContain("%1!s!", err.Error + err.Hint);
        Assert.Equal(0, replays);
        Assert.Empty(fake.CapturedXPaths);
        Assert.Empty(session.StagedTagBuffer);
    }

    [Theory]
    [InlineData("AXIS_VIRTUAL")]
    [InlineData("MOTION_GROUP")]
    public async Task MotionTag_IsRefusedInVerifyOnStage_BeforeTheOfflineReplay(string type)
    {
        var (session, fake) = await OpenAsync();
        int replays = 0;
        session.StageVerifyReplay = (_, _, _, _, _) => { replays++; return Task.FromResult(new ImportRejectReplayOutcome([], null)); };

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, policy: null, target: "workstation", verifyOnStage: true,
            rungs: new[] { new RungInput("NOP();") }, tags: new[] { new TagInput("Ax1", type) });

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("MotionTagRequiresOffline", err.Kind);
        Assert.Equal(0, replays);
        Assert.Empty(fake.CapturedXPaths);
        Assert.DoesNotContain("%1!s!", System.Text.Json.JsonSerializer.Serialize(err));
    }

    [Fact]
    public async Task MotionTag_InARawFragment_IsRefused_AndContextTagsAreNot()
    {
        var motion = ProposeRungEditHandler.MinimalNopFragment.Replace("<Controller",
            "<Controller").Replace("</Controller>",
            "<Tags><Tag Name=\"Ax1\" TagType=\"Base\" DataType=\"AXIS_VIRTUAL\"/></Tags></Controller>");
        Assert.Equal("MotionTagRequiresOffline", ProposeRungEditHandler.MotionTagRefusal(null, motion)?.Kind);

        var context = ProposeRungEditHandler.MinimalNopFragment.Replace("</Controller>",
            "<Tags Use=\"Context\"><Tag Use=\"Context\" Name=\"Ax1\" DataType=\"AXIS_VIRTUAL\"/></Tags></Controller>");
        Assert.Null(ProposeRungEditHandler.MotionTagRefusal(null, context));
        Assert.Null(ProposeRungEditHandler.MotionTagRefusal(
            new[] { new TagInput("Mi", "MOTION_INSTRUCTION"), new TagInput("T", "TIMER") }, null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnformattedTemplate_IsNeverSurfaced_AndFirstMeaningfulReasonIsChosen()
    {
        var (session, _) = await OpenAsync();
        var reasons = new[]
        {
            new RejectReason(0, "%1!s!", "RxCMP_E_SOMETHING"),
            new RejectReason(0, "Axis tags cannot be created online.", "RxCMP_E_AXIS_ONLINE"),
        };
        session.StageVerifyReplay = (_, _, _, _, _) => Task.FromResult(new ImportRejectReplayOutcome(reasons, null));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "workstation", verifyOnStage: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("StageVerifyFailed", err.Kind);
        Assert.DoesNotContain("%1!s!", System.Text.Json.JsonSerializer.Serialize(err));
        Assert.Contains("Axis tags cannot be created online.", err.Error);
        Assert.Equal("RxCMP_E_AXIS_ONLINE", err.VerifyRejectReasons![0].Mnemonic);
    }

    [Fact]
    public void FormatMessage_FillsKnownArguments_AndNeverLeavesAPlaceholder()
    {
        Assert.Equal("Tag Ax1 is invalid", ImportLogParser.FormatMessage("Tag %1!s! is invalid", "Ax1"));
        Assert.Equal(ImportLogParser.NoDetailText, ImportLogParser.FormatMessage("%1!s!"));
        Assert.DoesNotContain("%", ImportLogParser.FormatMessage("Cannot create %1!s! in %2!s!"));
        Assert.Equal("plain text", ImportLogParser.FormatMessage("plain text"));
    }

    [Fact]
    public void ImportLog_EntryWithOnlyATemplate_IsFormatted()
    {
        var log = ImportLogParser.ParseMessage(
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_X\" HexCode=\"80042c01\"><![CDATA[%1!s!]]></Exception>");
        Assert.DoesNotContain("%1!s!", Assert.Single(log).Text);
    }

    // ── G29: cross-program Output parameter ───────────────────────────────────

    private static ReferenceSymbols Symbols() =>
        new("MainProgram",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Run" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LocalFlag", "Mine_Out" },
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainProgram"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LocalFlag", "Mine_Out" },
                ["Line_A"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P6_Out", "P6_Pub" },
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            OutputParameters: new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MainProgram"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mine_Out" },
                ["Line_A"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P6_Out" },
            });

    [Theory]
    [InlineData("OTE")]
    [InlineData("OTL")]
    [InlineData("OTU")]
    public void WriteToAnotherProgramsOutput_IsRefused_NamingRungIndexAndOperand(string instruction)
    {
        var result = RungReferenceChecker.Check(
            new[] { "XIC(Run)OTE(LocalFlag);", $"XIC(Run){instruction}(\\Line_A.P6_Out);" }, Symbols());

        Assert.True(result.Refused);
        Assert.Equal("CrossProgramOutputNotWritable", result.Kind);
        var f = Assert.Single(result.Findings);
        Assert.Equal(1, f.RungIndex);
        Assert.Equal("\\Line_A.P6_Out", f.Name);
        Assert.Equal(instruction, f.Instruction);
        Assert.Contains("rung 1", result.Message);
        Assert.Contains("\\Line_A.P6_Out", result.Message);
        Assert.Contains("RxCMP_E_DIRECT_WRITE_OUTPUT", result.Message);
    }

    [Fact]
    public void ReadsOfAnotherProgramsOutput_AreAllowed()
    {
        var result = RungReferenceChecker.Check(
            new[] { "XIC(\\Line_A.P6_Out)XIO(\\Line_A.P6_Out)OTE(LocalFlag);" }, Symbols());
        Assert.False(result.Refused);
    }

    [Fact]
    public void WriteToAPublicParameter_OrOwnOutput_IsNotRefused()
    {
        Assert.False(RungReferenceChecker.Check(new[] { "XIC(Run)OTE(\\Line_A.P6_Pub);" }, Symbols()).Refused);
        Assert.False(RungReferenceChecker.Check(new[] { "XIC(Run)OTE(Mine_Out);" }, Symbols()).Refused);
        Assert.False(RungReferenceChecker.Check(new[] { "XIC(Run)OTE(\\MainProgram.Mine_Out);" }, Symbols()).Refused);
    }

    [Fact]
    public void UnknownOutputUsage_IsNeverRefused()
    {
        var noUsage = Symbols() with { OutputParameters = null };
        Assert.False(RungReferenceChecker.Check(new[] { "XIC(Run)OTE(\\Line_A.P6_Out);" }, noUsage).Refused);
    }

    [Fact]
    public void OutputParametersFromExport_ListsOnlyOutputUsage()
    {
        var map = RungReferenceChecker.OutputParametersFromExport(ProgramsXml)!;
        Assert.Equal(new[] { "P6_Out" }, map["Line_A"]);
        Assert.Empty(map["MainProgram"]);
    }

    [Fact]
    public async Task Stage_OfWriteToAnotherProgramsOutput_IsRefusedWithTypedKind_AndRejectReason()
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, policy: null, target: "workstation",
            rungs: new[] { new RungInput("XIC(LocalFlag)OTE(\\Line_A.P6_Out);") });

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("CrossProgramOutputNotWritable", err.Kind);
        Assert.Contains("rung 0", err.Error);
        Assert.Contains("\\Line_A.P6_Out", err.Error);
        var reason = Assert.Single(err.RejectReasons!);
        Assert.Equal(0, reason.RungIndex);
        Assert.Equal("RxCMP_E_DIRECT_WRITE_OUTPUT", reason.Mnemonic);
        Assert.Empty(fake.CapturedXPaths);
    }

    [Fact]
    public async Task Stage_OfReadOfAnotherProgramsOutput_IsAllowed()
    {
        var (session, _) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, null, session, policy: null, target: "workstation",
            rungs: new[] { new RungInput("XIC(\\Line_A.P6_Out)OTE(LocalFlag);") });

        Assert.IsType<ProposeRungEditResult>(result);
    }

    // ── G30: ImportRejectedByImporter hint ────────────────────────────────────

    [Fact]
    public void ImportRejectedHint_IsBuiltFromTheRecognisedMnemonicAndText()
    {
        var reasons = new[]
        {
            new RejectReason(2, "Direct write to an Output parameter of another program is not allowed.",
                "RxCMP_E_DIRECT_WRITE_OUTPUT", "80042c10",
                ImportLogParser.HintFor("RxCMP_E_DIRECT_WRITE_OUTPUT", "")),
        };

        var hint = ProposeRungEditHandler.ImportRejectedHintFor(reasons);

        Assert.Contains("RxCMP_E_DIRECT_WRITE_OUTPUT", hint);
        Assert.Contains("Direct write to an Output parameter", hint);
        Assert.Contains("rung 2", hint);
        Assert.DoesNotContain("undefined tag or routine", hint);
    }

    [Fact]
    public void ImportRejectedHint_SkipsTheGenericAbortedLine_AndPrefersAReasonWithText()
    {
        var reasons = new[]
        {
            new RejectReason(null, "The Import was cancelled due to errors.", "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES"),
            new RejectReason(1, "Instruction FOO is not defined.", "RxCMP_E_AUDIT_INSTRNOTDEFINED", null,
                ImportLogParser.HintFor("RxCMP_E_AUDIT_INSTRNOTDEFINED", "")),
        };
        var hint = ProposeRungEditHandler.ImportRejectedHintFor(reasons);
        Assert.Contains("RxCMP_E_AUDIT_INSTRNOTDEFINED", hint);
        Assert.DoesNotContain("XMLSrv_E_IMPORT_ABORTED_NO_CHANGES", hint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportRejectedHint_FallsBackToUndefinedReference_OnlyWhenNothingIsRecognised(bool withGenericLine)
    {
        var reasons = withGenericLine
            ? new[] { new RejectReason(null, "The Import was cancelled due to errors.", "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES") }
            : Array.Empty<RejectReason>();
        var hint = ProposeRungEditHandler.ImportRejectedHintFor(reasons);
        Assert.Contains("undefined tag or routine", hint);
        Assert.Equal(RockwellErrorHints.ImportRejectedUndefinedReferenceHint, hint);
        Assert.Equal(RockwellErrorHints.ImportRejectedUndefinedReferenceHint, ProposeRungEditHandler.ImportRejectedHintFor(null));
    }

    [Fact]
    public async Task ControllerPublish_RejectedWithANamedCause_HasAHintFromTheCause()
    {
        var (session, fake) = await OpenAsync();
        fake.ThrowOnImport = new InvalidOperationException(
            "XMLSrv_E_IMPORT_ABORTED_NO_CHANGES - The Import was cancelled due to errors. No changes were made.");
        fake.ImportLogEvents.Add((true,
            "<Exception Severity=\"Error\" Mnemonic=\"RxCMP_E_DIRECT_WRITE_OUTPUT\" HexCode=\"80042c10\">" +
            "<![CDATA[Rung 0: Cannot write to an Output parameter of another program.]]></Exception>"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, ProposeRungEditHandler.MinimalNopFragment, session,
            policy: null, target: "controller", confirmedByUser: true);

        var err = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("ImportRejectedByImporter", err.Kind);
        Assert.Contains("RxCMP_E_DIRECT_WRITE_OUTPUT", err.Hint);
        Assert.DoesNotContain("undefined tag or routine", err.Hint);
    }
}
#endif
