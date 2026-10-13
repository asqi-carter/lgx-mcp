using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Program-written and unknown-write-context status passes between an
/// alias and its base under both the bare and the scoped (<c>Program:P.Tag</c>) key, so a mark
/// recorded against an alias program parameter reaches the base tag it aliases.
/// </summary>
public class GateAliasScopedTests : IDisposable
{
    private readonly string _tempDir;

    public GateAliasScopedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gatealiasscoped-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string BaseTag(string name, string type = "REAL", string usage = "") =>
        $"""<Tag Name="{name}"{(usage.Length > 0 ? $" Usage=\"{usage}\"" : "")} TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="{type}" Radix="Decimal"><Data Format="Decorated"><DataValue DataType="{type}" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static string AliasParam(string name, string target, string usage = "Input") =>
        $"""<Tag Name="{name}" Usage="{usage}" TagType="Alias" AliasFor="{target}" Radix="Decimal" ExternalAccess="Read/Write" />""";

    private static string Program(string name, string tags, string routines = "") =>
        $"""
        <Program Name="{name}" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
          <Tags>{tags}</Tags>
          <Routines>{(routines.Length > 0 ? routines : EmptyRoutine)}</Routines>
        </Program>
        """;

    private const string EmptyRoutine =
        """<Routine Name="R" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine>""";

    private static L5X Project(string programs, string connections = "") =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes />
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags />
                <Programs>{programs}</Programs>
                <Tasks />
                {(connections.Length > 0 ? $"<ParameterConnections>{connections}</ParameterConnections>" : "<ParameterConnections />")}
              </Controller>
            </RSLogix5000Content>
            """);

    private static DenyEntry Denied(WriteGatePolicy policy, string scopedName) =>
        Assert.Single(policy.Denies, d => d.ScopedName == scopedName);

    [Fact]
    public void ConnectedAliasParameter_MarksBase()
    {
        // ProgB.In_Speed (Usage=Input) is an alias of ProgB.Cmd_Speed and is driven by ProgA.
        var project = Project(
            Program("ProgA", BaseTag("Src_Speed", usage: "Output")) +
            Program("ProgB", BaseTag("Cmd_Speed") + AliasParam("In_Speed", "Cmd_Speed")),
            """<ParameterConnection EndPoint1="\ProgA.Src_Speed" EndPoint2="\ProgB.In_Speed" />""");

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        var alias = Denied(policy, "Program:ProgB.In_Speed");
        Assert.Equal(DenyReason.ProgramWritten, alias.Reason);
        var baseDeny = Denied(policy, "Program:ProgB.Cmd_Speed");
        Assert.Equal(DenyReason.ProgramWritten, baseDeny.Reason);
        Assert.Contains("through alias 'In_Speed'", baseDeny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.ScopedName == "Program:ProgB.Cmd_Speed");
    }

    [Fact]
    public void CrossProgramWriteToAliasParameter_MarksBase()
    {
        // ProgA's rung writes \ProgB.In_Run, a parameter that aliases ProgB.Cmd_Run.
        var rung = """<Routine Name="R" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[XIC(Go)OTE(\ProgB.In_Run);]]></Text></Rung></RLLContent></Routine>""";
        var project = Project(
            Program("ProgA", BaseTag("Go", "BOOL"), rung) +
            Program("ProgB", BaseTag("Cmd_Run", "BOOL") + AliasParam("In_Run", "Cmd_Run")));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        var baseDeny = Denied(policy, "Program:ProgB.Cmd_Run");
        Assert.Equal(DenyReason.ProgramWritten, baseDeny.Reason);
        Assert.Contains("ProgA/R[0]", baseDeny.Provenance);
        Assert.Equal(DenyReason.ProgramWritten, Denied(policy, "Program:ProgB.In_Run").Reason);
    }

    [Fact]
    public void EncodedRoutine_MarksAliasParameterBase()
    {
        // An unreadable routine in ProgA could drive any parameter, including ProgB.In_Speed,
        // which has no command prefix but aliases the command-prefixed ProgB.Cmd_Speed.
        var encoded = EmptyRoutine + """<EncodedData EncodedType="Routine" Name="Locked" EncryptionConfig="1" />""";
        var project = Project(
            Program("ProgA", BaseTag("Go", "BOOL"), encoded) +
            Program("ProgB", BaseTag("Cmd_Speed") + AliasParam("In_Speed", "Cmd_Speed") +
                             BaseTag("Plain_In", usage: "Input")));

        var scan = WriteClassifier.ScanRungs(project);
        Assert.Contains("Locked", scan.UnknownContext["Program:ProgB.In_Speed"]);
        Assert.DoesNotContain("Program:ProgB.Plain_In", scan.UnknownContext.Keys);

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var baseDeny = Denied(policy, "Program:ProgB.Cmd_Speed");
        Assert.Equal(DenyReason.UnknownWriteContext, baseDeny.Reason);
        Assert.Contains("Locked", baseDeny.Provenance);
        Assert.Equal(DenyReason.UnknownWriteContext, Denied(policy, "Program:ProgB.In_Speed").Reason);
    }

    [Fact]
    public void UnresolvedAlias_PropagatesToLiteralTarget()
    {
        // ProgB.In_Ref aliases \ProgC.Cmd_Target, a form the engine does not resolve. The
        // connection that drives the alias still reaches the literal target.
        var project = Project(
            Program("ProgA", BaseTag("Src", "BOOL", usage: "Output")) +
            Program("ProgB", AliasParam("In_Ref", @"\ProgC.Cmd_Target")) +
            Program("ProgC", BaseTag("Cmd_Target", "BOOL")),
            """<ParameterConnection EndPoint1="\ProgA.Src" EndPoint2="\ProgB.In_Ref" />""");

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Equal(DenyReason.UnknownWriteContext, Denied(policy, "Program:ProgB.In_Ref").Reason);
        var target = Denied(policy, "Program:ProgC.Cmd_Target");
        Assert.Equal(DenyReason.ProgramWritten, target.Reason);
        Assert.Contains("unresolvable alias 'In_Ref'", target.Provenance);
    }
}
