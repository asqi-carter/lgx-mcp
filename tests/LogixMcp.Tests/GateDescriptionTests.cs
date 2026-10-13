using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Range descriptions are read from the tag's / member's own XML, so a project containing an
/// AOI rung that L5Sharp's whole-project index cannot tokenise (a MAS instruction with
/// "Units per sec2" and "% of Time") still derives a gate. Projects are inline.
/// </summary>
public class GateDescriptionTests : IDisposable
{
    private readonly string _tempDir;

    public GateDescriptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gatedesc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private const string MasAoi = """
        <AddOnInstructionDefinition Name="MoveAxis" Class="Standard" Revision="1.0" Vendor="" ExecutePrescan="false" ExecutePostscan="false" ExecuteEnableInFalse="false" CreatedDate="2024-01-01T00:00:00.000Z" CreatedBy="t" EditedDate="2024-01-01T00:00:00.000Z" EditedBy="t" SoftwareRevision="v36.00">
          <Parameters>
            <Parameter Name="EnableIn" TagType="Base" DataType="BOOL" Usage="Input" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
            <Parameter Name="EnableOut" TagType="Base" DataType="BOOL" Usage="Output" Radix="Decimal" Required="false" Visible="false" ExternalAccess="Read Only" />
          </Parameters>
          <LocalTags />
          <Routines>
            <Routine Name="Logic" Type="RLL">
              <RLLContent>
                <Rung Number="0" Type="N"><Text><![CDATA[MAS(Axis1,MasMotion,0,Units per sec2,100,Units per sec2,0,0,% of Time,0,0,% of Time,0,0);]]></Text></Rung>
              </RLLContent>
            </Routine>
          </Routines>
        </AddOnInstructionDefinition>
        """;

    private const string SpeedUdt = """
        <DataType Name="Drive" Family="NoFamily" Class="User"><Members>
          <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false"><Description><![CDATA[range 0..50]]></Description></Member>
          <Member Name="Accel" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false"><Comments><Comment Operand="Accel"><![CDATA[range 1..9]]></Comment></Comments></Member>
        </Members></DataType>
        """;

    private const string Tags = """
        <Tag Name="Cmd_Speed" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="REAL" Radix="Float"><Description><![CDATA[range 0..100]]></Description><Data Format="L5K"><![CDATA[0.0]]></Data></Tag>
        <Tag Name="Cmd_Drive" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="Drive"><Data Format="L5K"><![CDATA[[0.0,0.0]]]></Data></Tag>
        """;

    private static L5X Project(string aoi = MasAoi) => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>{SpeedUdt}</DataTypes>
            <Modules />
            <AddOnInstructionDefinitions>{aoi}</AddOnInstructionDefinitions>
            <Tags>{Tags}</Tags>
            <Programs />
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private static GateConfig Config(string? raw) =>
        Assert.IsType<GateConfig>(GateConfigResolver.Resolve(raw));

    [Fact]
    public void RangeFromOwnXml_WhenProjectIndexWouldThrow()
    {
        var project = Project();

        foreach (var config in new[] { GateConfig.Default, Config("*") })
        {
            var policy = GateEngine.DerivePolicy(project, config);

            var speed = policy.Grants.Single(g => g.Tag == "Cmd_Speed");
            Assert.Equal(0.0, speed.TagBounds!.Min);
            Assert.Equal(100.0, speed.TagBounds!.Max);

            var drive = policy.Grants.Single(g => g.Tag == "Cmd_Drive");
            Assert.Contains(drive.Bounds!, b => b.Member == "Speed" && b.Min == 0 && b.Max == 50);
            Assert.Contains(drive.Bounds!, b => b.Member == "Accel" && b.Min == 1 && b.Max == 9);
        }
    }

    [Fact]
    public void MemberComment_FallsBackWhenNoDescriptionElement()
    {
        var member = System.Xml.Linq.XElement.Parse(
            """<Member Name="Accel"><Comments><Comment Operand=".Accel">range 1..9</Comment></Comments></Member>""");
        Assert.Equal("range 1..9", GateDescriptions.MemberDescription(member));
    }

    [Fact]
    public void MemberDescriptionElement_TakesPrecedenceOverComment()
    {
        var member = System.Xml.Linq.XElement.Parse(
            """<Member Name="A"><Description>range 0..1</Description><Comments><Comment Operand="A">range 5..6</Comment></Comments></Member>""");
        Assert.Equal("range 0..1", GateDescriptions.MemberDescription(member));
    }
}
