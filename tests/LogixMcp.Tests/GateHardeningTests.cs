using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Gate hardening: AOI names first, firmware-written types,
/// equipment-phase programs, tag-name grammar. Fixtures are synthetic.
/// </summary>
public class GateHardeningTests
{
    private static string Tag(string name, string type = "BOOL") =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="{type}" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="{type}" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static string StRoutine(string line) =>
        $"""<Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[{line}]]></Line></STContent></Routine>""";

    private static L5X Build(string controllerTags, string aoiXml, string programsXml) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules />
            <AddOnInstructionDefinitions>{aoiXml}</AddOnInstructionDefinitions>
            <Tags>{controllerTags}</Tags>
            <Programs>{programsXml}</Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private static string NormalProgram(string routine) =>
        $"""<Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R"><Tags /><Routines>{routine}</Routines></Program>""";

    private static string EncodedAoi(string name) =>
        $"""<EncodedData EncodedType="AddOnInstructionDefinition" Name="{name}" EncryptionConfig="7" />""";

    // ── (a) encoded AOI named like a pure function / type conversion ──────────

    [Theory]
    [InlineData("MAX")]
    [InlineData("INT_TO_REAL")]
    public void EncodedAoi_NamedLikePureFunction_StOutputArgsAreNotReads(string aoiName)
    {
        var project = Build(Tag("Cmd_Out"), EncodedAoi(aoiName),
            NormalProgram(StRoutine($"{aoiName}(Inst, Cmd_Out);")));

        var aoiWrites = WriteClassifier.BuildAoiWriteMap(project);
        Assert.Equal("unknown", WriteClassifier.ClassifyKind(aoiName, 1, aoiWrites));

        var scan = WriteClassifier.ScanRungs(project);
        Assert.True(
            scan.UnknownContext.ContainsKey("Cmd_Out") || scan.ProgramWritten.ContainsKey("Cmd_Out"),
            "Cmd_Out passed to an encoded AOI must not be treated as a read");
    }

    [Fact]
    public void PureFunctionNotDefinedByProject_StillReads()
    {
        var project = Build(Tag("Cmd_Out"), "", NormalProgram(StRoutine("X := MAX(1, Cmd_Out);")));
        var scan = WriteClassifier.ScanRungs(project);
        Assert.DoesNotContain("Cmd_Out", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Cmd_Out", scan.ProgramWritten.Keys);
    }

    // ── (b) firmware-written types ────────────────────────────────────────────

    [Theory]
    [InlineData("PHASE")]
    [InlineData("AXIS_CIP_DRIVE")]
    [InlineData("AXIS_VIRTUAL")]
    [InlineData("MOTION_GROUP")]
    [InlineData("SFC_STEP")]
    [InlineData("SFC_ACTION")]
    [InlineData("MESSAGE")]
    [InlineData("COORDINATE_SYSTEM")]
    public void CmdTag_OfFirmwareWrittenType_IsUnknownWriteContext(string type)
    {
        var project = Build(Tag("Cmd_Fw", type) + Tag("Cmd_Plain", "DINT"), "",
            NormalProgram(StRoutine("Plain := 1;")));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Fw");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains("type is written by controller firmware", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Fw");
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Plain");
    }

    // ── (c) equipment-phase programs ──────────────────────────────────────────

    [Fact]
    public void EquipmentPhaseProgram_IsScannedLikeNormalProgram()
    {
        var phase =
            """<Program Name="Ph1" Type="EquipmentPhase" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R"><Tags /><Routines><Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[Cmd_Phase := 1;]]></Line></STContent></Routine></Routines></Program>""";
        var project = Build(Tag("Cmd_Phase"), "", NormalProgram(StRoutine("Plain := 1;")) + phase);

        var scan = WriteClassifier.ScanRungs(project);
        Assert.Contains("Cmd_Phase", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_Phase").Reason);
    }

    [Fact]
    public void BareEquipmentPhaseElement_IsScannedLikeNormalProgram()
    {
        var phase =
            """<EquipmentPhase Name="Ph1" MainRoutineName="R"><Tags /><Routines><Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[Cmd_Phase := 1;]]></Line></STContent></Routine></Routines></EquipmentPhase>""";
        var project = Build(Tag("Cmd_Phase"), "", NormalProgram(StRoutine("Plain := 1;")) + phase);

        var scan = WriteClassifier.ScanRungs(project);
        Assert.Contains("Cmd_Phase", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten, policy.Denies.Single(d => d.Tag == "Cmd_Phase").Reason);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Phase");
    }

    // ── (d) tag-name grammar ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Tag&elem_count=100")]
    [InlineData("Tag&protocol=modbus")]
    [InlineData("Cmd_A B")]
    [InlineData("Tag;x")]
    [InlineData("1Tag")]
    [InlineData("Tag[]")]
    [InlineData("Tag[a]")]
    [InlineData("Tag.")]
    [InlineData("")]
    public void InvalidTagNames_AreRejected(string name) => Assert.False(TagNameValidator.IsValid(name));

    [Theory]
    [InlineData("Tag")]
    [InlineData("_Tag1")]
    [InlineData("Cmd_Motor.Speed")]
    [InlineData("Arr[3]")]
    [InlineData("Arr[1,2].Member[4]")]
    [InlineData("Word.31")]
    [InlineData("Program:Main.Cmd_X")]
    [InlineData("Local:1:I.Data[0]")]
    public void ValidTagNames_AreAccepted(string name) => Assert.True(TagNameValidator.IsValid(name));

    [Fact]
    public async System.Threading.Tasks.Task LibPlcTagService_AmpersandName_ReadAndWrite_InvalidTagNameBeforeLibplctag()
    {
        var svc = new LibPlcTagService();   // never connected: nothing may reach libplctag

        var read = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Tag&elem_count=9"));
        Assert.Equal(TagServiceErrorKind.InvalidTagName, read.Error.Kind);

        var write = await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("Tag&elem_count=9", 1));
        Assert.Equal(TagServiceErrorKind.InvalidTagName, write.Error.Kind);
    }

    [Fact]
    public async System.Threading.Tasks.Task GatedTagService_AmpersandName_ReadAndWrite_InvalidTagName()
    {
        var inner = new VirtualTagService();
        var gated = new GatedTagService(inner, new WriteGatePolicy(
            new[] { new GrantEntry("Cmd_A", null, null, "test") }, Array.Empty<DenyEntry>()));

        var read = await Assert.ThrowsAsync<TagServiceException>(() => gated.ReadAsync("Cmd_A&x=1"));
        Assert.Equal(TagServiceErrorKind.InvalidTagName, read.Error.Kind);

        var write = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync("Cmd_A&x=1", 1));
        Assert.Equal(TagServiceErrorKind.InvalidTagName, write.Error.Kind);
    }
}
