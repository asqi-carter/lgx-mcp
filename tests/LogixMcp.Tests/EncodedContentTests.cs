using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Fail-closed handling of <c>EncodedData</c> in the real export shape: the element
/// replaces the <c>Routine</c> or <c>AddOnInstructionDefinition</c> element (attributes
/// <c>EncodedType</c>, <c>Name</c>, <c>EncryptionConfig</c>) rather than nesting inside one.
/// Fixtures are synthetic; no customer content.
/// </summary>
public class EncodedContentTests
{
    private static string Tag(string name) =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>""";

    private const string PlainRoutine =
        """<Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[Plain_C := 1;]]></Line></STContent></Routine>""";

    private static L5X Build(string controllerTags, string programTags, string aoiXml,
        string routinesXml, string extraControllerXml = "", string extraProgramsXml = "") => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules />
            <AddOnInstructionDefinitions>{aoiXml}</AddOnInstructionDefinitions>
            <Tags>{controllerTags}</Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags>{programTags}</Tags>
                <Routines>{routinesXml}</Routines>
              </Program>{extraProgramsXml}
            </Programs>
            <Tasks />{extraControllerXml}
          </Controller>
        </RSLogix5000Content>
        """);

    [Fact]
    public void TopLevelEncodedData_RealShape_FailsClosed()
    {
        // The element replaces the Routine inside <Routines>.
        var project = Build(
            Tag("Cmd_Ctl") + Tag("Sim_Ctl") + Tag("Plain_Ctl"),
            Tag("Cmd_Prog"),
            "",
            PlainRoutine +
            """<EncodedData EncodedType="Routine" Name="Locked" EncryptionConfig="1" />""");

        var scan = WriteClassifier.ScanRungs(project);

        foreach (var tag in new[] { "Cmd_Ctl", "Sim_Ctl", "Cmd_Prog" })
        {
            var provenance = scan.UnknownContext[tag];
            Assert.Contains("Main/Locked", provenance);
            Assert.Contains("Name=\"Locked\"", provenance);
            Assert.Contains("EncodedType=\"Routine\"", provenance);
        }
        Assert.DoesNotContain("Plain_Ctl", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Ctl");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains("EncodedType=\"Routine\"", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Sim_Ctl");
    }

    [Fact]
    public void EncodedAddOnInstructionDefinition_EveryCallClassifiesUnknown()
    {
        var project = Build(
            Tag("Cmd_Ctl"), "",
            """<EncodedData EncodedType="AddOnInstructionDefinition" Name="Aoi_Secret" EncryptionConfig="7" />""",
            PlainRoutine);

        var aoiWrites = WriteClassifier.BuildAoiWriteMap(project);

        // Every argument position of every call to the encoded AOI is unknown.
        for (int i = 0; i < 6; i++)
            Assert.Equal("unknown", WriteClassifier.ClassifyKind("Aoi_Secret", i, aoiWrites));

        // The encoded element's enclosing program cannot be determined: every
        // controller-scope Cmd_/Sim_ tag is UnknownWriteContext, naming Name and EncodedType.
        var scan = WriteClassifier.ScanRungs(project);
        var provenance = scan.UnknownContext["Cmd_Ctl"];
        Assert.Contains("Aoi_Secret", provenance);
        Assert.Contains("EncodedType=\"AddOnInstructionDefinition\"", provenance);
    }

    [Fact]
    public void EncodedData_UnknownEnclosingProgram_DeniesEveryControllerCommandTag()
    {
        // Encoded element directly under <Controller>, outside any Program.
        var project = Build(
            Tag("Cmd_A") + Tag("Sim_B") + Tag("Plain_C"), "", "", PlainRoutine,
            """<EncodedData EncodedType="Task" Name="Orphan" EncryptionConfig="2" />""");

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("Orphan", scan.UnknownContext["Cmd_A"]);
        Assert.Contains("EncodedType=\"Task\"", scan.UnknownContext["Sim_B"]);
        Assert.DoesNotContain("Plain_C", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        Assert.Equal(DenyReason.UnknownWriteContext, policy.Denies.Single(d => d.Tag == "Cmd_A").Reason);
        Assert.Equal(DenyReason.UnknownWriteContext, policy.Denies.Single(d => d.Tag == "Sim_B").Reason);
    }

    private const string OtherProgram = """
        <Program Name="Other" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
          <Tags>
            <Tag Name="Cmd_In" Usage="Input" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>
            <Tag Name="Sim_Pub" Usage="Public" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>
            <Tag Name="Plain_In" Usage="Input" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>
          </Tags>
          <Routines><Routine Name="R" Type="ST"><STContent><Line Number="0"><![CDATA[Plain_C := 1;]]></Line></STContent></Routine></Routines>
        </Program>
        """;

    [Fact]
    public void EncodedRoutine_MarksOtherProgramsParameters()
    {
        var project = Build(
            "", "", "",
            PlainRoutine + """<EncodedData EncodedType="Routine" Name="Locked" EncryptionConfig="1" />""",
            extraProgramsXml: OtherProgram);

        var scan = WriteClassifier.ScanRungs(project);

        foreach (var key in new[] { "Program:Other.Cmd_In", "Program:Other.Sim_Pub" })
        {
            var provenance = scan.UnknownContext[key];
            Assert.Contains("Main/Locked", provenance);
            Assert.Contains("Name=\"Locked\"", provenance);
            Assert.Contains("EncodedType=\"Routine\"", provenance);
        }
        Assert.DoesNotContain("Program:Other.Plain_In", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var deny = policy.Denies.Single(d => d.ScopedName == "Program:Other.Cmd_In");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains("Locked", deny.Provenance);
    }

    [Fact]
    public void UnparseableRung_MarksOtherProgramsParameters()
    {
        const string badRung =
            """<Routine Name="R" Type="RLL"><RLLContent><Rung Number="3" Type="N"><Text><![CDATA[OTE(@@);]]></Text></Rung></RLLContent></Routine>""";
        var project = Build("", "", "", badRung, extraProgramsXml: OtherProgram);

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("Main/R[3]", scan.UnknownContext["Program:Other.Cmd_In"]);
        Assert.Contains("Main/R[3]", scan.UnknownContext["Program:Other.Sim_Pub"]);
        Assert.DoesNotContain("Program:Other.Plain_In", scan.UnknownContext.Keys);
    }

    [Fact]
    public void NoUnreadableContent_LeavesOtherProgramsParametersUnmarked()
    {
        var project = Build("", "", "", PlainRoutine, extraProgramsXml: OtherProgram);

        var scan = WriteClassifier.ScanRungs(project);

        Assert.DoesNotContain("Program:Other.Cmd_In", scan.UnknownContext.Keys);
    }
}
