using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Cross-program references (<c>\Prog.Tag</c>) in the write gate.
/// The tests that built program parameters as a <c>&lt;Program&gt;&lt;Parameters&gt;</c> element
/// and <c>&lt;ParameterConnection EndPoint1/EndPoint2&gt;</c> were removed: real exports declare
/// parameters as <c>&lt;Tag Usage="..."&gt;</c> in the program's <c>&lt;Tags&gt;</c>
/// (see <c>fixtures/real-shape/UsageParameters.L5X</c>) and no real export proves the
/// connection shape. Fixtures are synthetic.
/// </summary>
public class ProgramParameterTests
{
    private static string Tag(string name) =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data></Tag>""";

    private const string EmptyMain = """
        <Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text>XIC(Cmd_Read)OTE(Cmd_Out);</Text></Rung></RLLContent></Routine>
        """;

    private static string Program(string name, string routines = EmptyMain, string tags = "") => $"""
        <Program Name="{name}" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
          <Tags>{tags}</Tags>
          <Routines>{routines}</Routines>
        </Program>
        """;

    private static L5X Build(string programs) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions />
            <Tags>{Tag("Cmd_Read")}{Tag("Cmd_Out")}{Tag("Cmd_Ctl")}{Tag("Sim_Ctl")}{Tag("Cmd_Untouched")}</Tags>
            <Programs>{programs}</Programs>
            <Tasks />
            <ParameterConnections />
          </Controller>
        </RSLogix5000Content>
        """);

    [Fact]
    public void CrossProgramReference_ResolvesToProgramScope()
    {
        const string writer = """
            <Routine Name="MainRoutine" Type="RLL"><RLLContent>
              <Rung Number="0" Type="N"><Text>XIC(Cmd_Read)OTE(\ProgA.Cmd_Local);</Text></Rung>
            </RLLContent></Routine>
            """;
        const string stWriter = """
            <Routine Name="StRoutine" Type="ST"><STContent>
              <Line Number="0"><![CDATA[\ProgA.Sim_Local := 1;]]></Line>
            </STContent></Routine>
            """;
        var project = Build(
            Program("ProgA", tags: Tag("Cmd_Local") + Tag("Sim_Local") + Tag("Cmd_Other")) +
            Program("ProgB", writer + stWriter));

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Equal("ProgB/MainRoutine[0] via OTE", scan.ProgramWritten["Program:ProgA.Cmd_Local"]);
        Assert.Contains("Program:ProgA.Sim_Local", scan.ProgramWritten.Keys);
        // Not reduced to the program name, nor to the bare tag name.
        Assert.DoesNotContain("ProgA", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("\\ProgA", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Cmd_Local", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var deny = policy.Denies.Single(d => d.ScopedName == "Program:ProgA.Cmd_Local");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        Assert.Contains("ProgB/MainRoutine", deny.Provenance);
        Assert.Equal(DenyReason.ProgramWritten,
            policy.Denies.Single(d => d.ScopedName == "Program:ProgA.Sim_Local").Reason);
        Assert.Contains(policy.Grants, g => g.ScopedName == "Program:ProgA.Cmd_Other");
    }

    [Fact]
    public void CrossProgramReference_DoesNotDenyControllerTagOfSameName()
    {
        const string writer = """
            <Routine Name="MainRoutine" Type="RLL"><RLLContent>
              <Rung Number="0" Type="N"><Text>OTE(\ProgA.Cmd_Ctl);</Text></Rung>
            </RLLContent></Routine>
            """;
        var project = Build(
            Program("ProgA", tags: Tag("Cmd_Ctl")) + Program("ProgB", writer));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Contains(policy.Denies, d => d.ScopedName == "Program:ProgA.Cmd_Ctl");
        Assert.Contains(policy.Grants, g => g.ScopedName == "Cmd_Ctl");
    }
}
