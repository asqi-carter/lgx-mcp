using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// MSG destinations are program-written, member paths are trimmed
/// and normalised before bounds matching, names with control characters are refused as
/// <c>InvalidTagName</c>, and consumed tags are denied. Fixtures are synthetic. The
/// <c>MessageParameters DestinationTag</c> tests were removed: real exports name the local
/// destination in <c>LocalElement</c> (see <c>fixtures/real-shape/MsgRead.L5X</c>).
/// </summary>
public class MsgConsumedGateTests
{
    private static string Tag(string name) =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static L5X Build(string controllerTags, string rungText = "XIC(Cmd_Go)OTE(Cmd_Out);") => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes /><Modules /><AddOnInstructionDefinitions />
            <Tags>{controllerTags}</Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                <Tags />
                <Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text>{rungText}</Text></Rung></RLLContent></Routine></Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    // ── MSG destinations ─────────────────────────────────────────────────────

    [Fact]
    public void MsgDestinationOperand_IsProgramWritten()
    {
        var project = Build(Tag("Msg_Read") + Tag("Cmd_Go") + Tag("Cmd_Out"), "XIC(Cmd_Go)MSG(Msg_Read);");

        var scan = WriteClassifier.ScanRungs(project);

        Assert.Contains("Msg_Read", scan.ProgramWritten.Keys);
        Assert.Contains("MSG", scan.ProgramWritten["Msg_Read"]);
        Assert.DoesNotContain("Msg_Read", scan.UnknownContext.Keys);
    }

    // ── Member path normalisation / InvalidTagName ───────────────────────────

    private static (GatedTagService Gated, VirtualTagService Inner) BuildGated()
    {
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Motor.Speed"] = (0.0, "REAL"),
            ["Cmd_Motor"]       = (0,   "MotorCmd"),
        });
        var policy = new WriteGatePolicy(
            [new GrantEntry("Cmd_Motor", null, [new MemberBounds("Speed", 0, 100)], "test grant")],
            []);
        return (new GatedTagService(inner, policy), inner);
    }

    [Theory]
    [InlineData("Cmd_Motor.Speed\t")]
    [InlineData("Cmd_Motor.Speed  ")]
    [InlineData(" Cmd_Motor . Speed ")]
    public async Task MemberPath_WithWhitespace_IsTrimmedAndMatchedAgainstBounds(string name)
    {
        var (gated, inner) = BuildGated();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(name, 500.0));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains("[0..100]", ex.Message);

        await gated.WriteAsync(name, 50.0);
        Assert.Equal(50.0, Convert.ToDouble((await inner.ReadAsync("Cmd_Motor.Speed")).Value));
    }

    [Theory]
    [InlineData("Cmd_Motor.Spe\ted")]
    [InlineData("Cmd_Motor\n.Speed")]
    [InlineData("Cmd_Motor.Speed\0")]
    [InlineData("\t")]
    public async Task NameWithControlCharacters_IsRefusedAsInvalidTagName(string name)
    {
        var (gated, _) = BuildGated();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(name, 5.0));

        Assert.Equal(TagServiceErrorKind.InvalidTagName, ex.Error.Kind);
    }

    // ── Consumed tags ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(" TagType=\"Consumed\"", "")]
    [InlineData("", "<ConsumeInfo Producer=\"Other\" RemoteTag=\"Cmd_Remote\" RPI=\"20\" />")]
    public void ConsumedTag_IsUnknownWriteContext(string attrs, string body)
    {
        var tagType = attrs.Length > 0 ? "" : " TagType=\"Base\"";
        var consumed = $"<Tag Name=\"Cmd_Remote\"{tagType}{attrs} ExternalAccess=\"Read/Write\" DataType=\"DINT\">{body}</Tag>";
        var project = Build(consumed + Tag("Cmd_Go") + Tag("Cmd_Out"));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Remote");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains("value is produced by another controller", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Remote");
    }

    [Fact]
    public void ConsumedTag_WithoutCommandPrefix_IsStillUnknownWriteContext()
    {
        var consumed = "<Tag Name=\"Remote_Value\" TagType=\"Consumed\" ExternalAccess=\"Read/Write\" DataType=\"DINT\" />";
        var policy = GateEngine.DerivePolicy(Build(consumed + Tag("Cmd_Go") + Tag("Cmd_Out")), GateConfig.Default);

        Assert.Equal(DenyReason.UnknownWriteContext, Assert.Single(policy.Denies, d => d.Tag == "Remote_Value").Reason);
    }
}
