using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Program parameters are <c>&lt;Tag Usage="..."&gt;</c> in a program's
/// <c>&lt;Tags&gt;</c>. Built from <c>fixtures/real-shape/UsageParameters.L5X</c>.
/// </summary>
public class ProgramParameterRealShapeTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "UsageParameters.L5X"));

    private const string Writer = """
        <Program Use="Target" Name="Line_B" TestEdits="false" MainRoutineName="Main" Disabled="false" UseAsFolder="false">
        <Tags />
        <Routines><Routine Use="Target" Name="Main" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[OTE(\Line_A.Cmd_Pub);]]></Text></Rung></RLLContent></Routine></Routines>
        </Program>
        """;

    [Fact]
    public void OutputUsage_ProgramWritten()
    {
        var scan = WriteClassifier.ScanRungs(L5X.Parse(Fixture()));

        Assert.Contains("Program:Line_A.Cmd_Out", scan.ProgramWritten.Keys);
        Assert.Contains("Usage=Output", scan.ProgramWritten["Program:Line_A.Cmd_Out"]);
        Assert.Contains("Program:Line_A.Cmd_Both", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Cmd_In", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Cmd_Local", scan.ProgramWritten.Keys);
        Assert.Empty(scan.UnknownContext);
    }

    [Fact]
    public void ConnectedInput_ProgramWritten()
    {
        var xml = Fixture().Replace("<ParameterConnections />",
            """<ParameterConnections><ParameterConnection EndPoint1="\Line_A.Cmd_In" EndPoint2="\Line_A.Cmd_Out" /></ParameterConnections>""");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Cmd_In", scan.ProgramWritten.Keys);
        Assert.Contains("ParameterConnection", scan.ProgramWritten["Program:Line_A.Cmd_In"]);
        Assert.DoesNotContain("Program:Line_A.Cmd_Local", scan.ProgramWritten.Keys);

        var policy = GateEngine.DerivePolicy(L5X.Parse(xml), GateConfig.Default);
        Assert.Equal(DenyReason.ProgramWritten,
            policy.Denies.Single(d => d.ScopedName == "Program:Line_A.Cmd_In").Reason);
    }

    [Fact]
    public void CrossProgramWriteToPublic_ProgramWritten()
    {
        var xml = Fixture()
            .Replace("Usage=\"Input\"", "Usage=\"Public\"").Replace("Cmd_In", "Cmd_Pub")
            .Replace("</Programs>", Writer + "</Programs>");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Cmd_Pub", scan.ProgramWritten.Keys);
        Assert.Contains("Line_B/Main[0]", scan.ProgramWritten["Program:Line_A.Cmd_Pub"]);
        Assert.DoesNotContain("Program:Line_A.Cmd_Pub", scan.UnknownContext.Keys);
    }

    [Fact]
    public void UnconnectedPublicOrInput_NotWritten()
    {
        var xml = Fixture().Replace("Usage=\"Input\"", "Usage=\"Public\"");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.DoesNotContain("Program:Line_A.Cmd_In", scan.ProgramWritten.Keys);
        Assert.DoesNotContain("Program:Line_A.Cmd_In", scan.UnknownContext.Keys);
    }

    [Theory]
    [InlineData("Frobnicate")]
    [InlineData("")]
    public void UnknownUsage_UnknownWriteContext(string usage)
    {
        var xml = Fixture().Replace("Usage=\"Input\"", $"Usage=\"{usage}\"");

        var scan = WriteClassifier.ScanRungs(L5X.Parse(xml));

        Assert.Contains("Program:Line_A.Cmd_In", scan.UnknownContext.Keys);
        Assert.DoesNotContain("Program:Line_A.Cmd_In", scan.ProgramWritten.Keys);
    }
}
