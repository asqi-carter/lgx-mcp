using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// <see cref="GateEngine.DerivePolicy"/> resolves aliases to their base tag: an alias is
/// denied when its base is denied, takes its bounds from the base, and rung writes
/// through an alias make the base program-written. Projects are built inline.
/// </summary>
public class GateAliasTests : IDisposable
{
    private readonly string _tempDir;

    public GateAliasTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gatealias-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private const string Udt = """
        <DataType Name="MotorCmd" Family="NoFamily" Class="User"><Members>
          <Member Name="Enable" DataType="BOOL" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
          <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false"><Description><![CDATA[range 0..100]]></Description></Member>
        </Members></DataType>
        """;

    private static string BaseTag(string name, string type, string access = "Read/Write", string description = "") =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="{access}" Constant="false" DataType="{type}" Radix="Decimal">{(description.Length > 0 ? $"<Description><![CDATA[{description}]]></Description>" : "")}<Data Format="Decorated"><Structure DataType="{type}" /></Data></Tag>""";

    private static string AliasTag(string name, string target, string access = "Read/Write") =>
        $"""<Tag Name="{name}" TagType="Alias" AliasFor="{target}" Radix="Decimal" ExternalAccess="{access}" />""";

    private static L5X Project(string controllerTags, string rungs = "", string programTags = "")
    {
        var program = rungs.Length == 0 && programTags.Length == 0
            ? ""
            : $"""
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags>{programTags}</Tags>
                <Routines><Routine Name="R" Type="RLL"><RLLContent>{rungs}</RLLContent></Routine></Routines>
              </Program>
              """;
        return L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes>{Udt}</DataTypes>
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>{controllerTags}</Tags>
                <Programs>{program}</Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);
    }

    private static string Rung(int n, string text) =>
        $"""<Rung Number="{n}" Type="N"><Text><![CDATA[{text}]]></Text></Rung>""";

    [Fact]
    public void PaddedAliasFor_ResolvesLikeUnpadded()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Motor_Run", "BOOL") + BaseTag("Cmd_In", "BOOL") +
            AliasTag("Cmd_RunAlias", "  Cmd_Motor_Run "),
            Rung(0, "XIC(Cmd_In)OTE(Cmd_Motor_Run);")), GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_RunAlias");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        Assert.Contains("alias of 'Cmd_Motor_Run'", deny.Provenance);
    }

    [Fact]
    public void UnresolvableAliasFor_UnknownWriteContext()
    {
        var policy = GateEngine.DerivePolicy(Project(
            AliasTag("Cmd_Ghost", " Cmd_Missing ")), GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Ghost");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
    }

    [Fact]
    public void AliasOfSafetyTag_Denied()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Safety_OK", "BOOL") + AliasTag("Cmd_Safe", "Safety_OK")), GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Safe");
        Assert.Equal(DenyReason.SafetyTag, deny.Reason);
        Assert.Contains("alias of 'Safety_OK'", deny.Provenance);
        Assert.Contains("safety prefix", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Safe");
    }

    [Fact]
    public void AliasOfProgramWrittenTag_Denied()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Motor_Run", "BOOL") + BaseTag("Cmd_In", "BOOL") +
            AliasTag("Cmd_RunAlias", "Cmd_Motor_Run") +
            "",
            Rung(0, "XIC(Cmd_In)OTE(Cmd_Motor_Run);")), GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_RunAlias");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        Assert.Contains("alias of 'Cmd_Motor_Run'", deny.Provenance);
        Assert.Contains("Main/R[0] via OTE", deny.Provenance);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_In");
    }

    [Fact]
    public void RungWritingAlias_MakesBaseProgramWritten()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Target", "BOOL") + BaseTag("Cmd_Other", "BOOL") +
            AliasTag("Cmd_TargetAlias", "Cmd_Target") +
            AliasTag("Cmd_TargetAlias2", "Cmd_TargetAlias"),
            Rung(0, "XIC(Cmd_Other)OTE(Cmd_TargetAlias);")), GateConfig.Default);

        // The base, the written alias, and every other alias of the same storage are denied.
        foreach (var name in new[] { "Cmd_Target", "Cmd_TargetAlias", "Cmd_TargetAlias2" })
        {
            var deny = Assert.Single(policy.Denies, d => d.Tag == name);
            Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
        }
        var baseDeny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Target");
        Assert.Contains("through alias 'Cmd_TargetAlias'", baseDeny.Provenance);
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Other");
    }

    [Fact]
    public void AliasOfModuleIo_UnknownWriteContext()
    {
        var policy = GateEngine.DerivePolicy(Project(
            AliasTag("Cmd_Out", "Local:1:O.Data") +
            AliasTag("Cmd_Out0", "Local:1:O.Data[0]") +
            AliasTag("Cmd_Out1", "Local:1:O.Data.1") +
            AliasTag("Cmd_Missing", "No_Such_Tag")), GateConfig.Default);

        foreach (var name in new[] { "Cmd_Out", "Cmd_Out0", "Cmd_Out1", "Cmd_Missing" })
        {
            var deny = Assert.Single(policy.Denies, d => d.Tag == name);
            Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
            Assert.Contains("alias of", deny.Provenance);
        }
        Assert.Empty(policy.Grants);
    }

    [Fact]
    public void AliasBoundsComeFromBase()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Motor", "MotorCmd") +
            BaseTag("Cmd_Speed", "REAL", description: "range 0..50") +
            AliasTag("Cmd_MotorAlias", "Cmd_Motor") +
            AliasTag("Cmd_MotorSpeed", "Cmd_Motor.Speed") +
            AliasTag("Cmd_SpeedAlias", "Cmd_Speed") +
            AliasTag("Cmd_Enable", "Cmd_Motor.Enable")), GateConfig.Default);

        var whole = Assert.Single(policy.Grants, g => g.Tag == "Cmd_MotorAlias");
        Assert.Contains(whole.Bounds!, b => b.Member == "Speed" && b.Min == 0 && b.Max == 100);

        var member = Assert.Single(policy.Grants, g => g.Tag == "Cmd_MotorSpeed");
        Assert.Equal(new ScalarBounds(0, 100), member.TagBounds);

        var scalar = Assert.Single(policy.Grants, g => g.Tag == "Cmd_SpeedAlias");
        Assert.Equal(new ScalarBounds(0, 50), scalar.TagBounds);

        var unbounded = Assert.Single(policy.Grants, g => g.Tag == "Cmd_Enable");
        Assert.Null(unbounded.TagBounds);
        Assert.Null(unbounded.Bounds);
    }

    [Fact]
    public void BitAliasOfRangeCheckedValue_Denied()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Speed", "DINT", description: "range 0..100") +
            BaseTag("Cmd_Motor", "MotorCmd") +
            AliasTag("Cmd_SpeedBit", "Cmd_Speed.3") +
            AliasTag("Cmd_MotorSpeedBit", "Cmd_Motor.Speed.3")), GateConfig.Default);

        Assert.Equal(DenyReason.UnknownWriteContext,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_SpeedBit").Reason);
        Assert.Equal(DenyReason.UnknownWriteContext,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_MotorSpeedBit").Reason);
    }

    [Fact]
    public void ExternalAccess_IsStricterOfAliasAndBase()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_RO", "BOOL", access: "Read Only") +
            BaseTag("Cmd_RW", "BOOL") +
            AliasTag("Cmd_AliasOfRO", "Cmd_RO") +
            AliasTag("Cmd_AliasNone", "Cmd_RW", access: "None")), GateConfig.Default);

        var fromBase = Assert.Single(policy.Denies, d => d.Tag == "Cmd_AliasOfRO");
        Assert.Equal(DenyReason.ExternalAccessNotReadWrite, fromBase.Reason);
        Assert.Equal("Read Only", fromBase.ExternalAccess);

        var fromAlias = Assert.Single(policy.Denies, d => d.Tag == "Cmd_AliasNone");
        Assert.Equal(DenyReason.ExternalAccessNotReadWrite, fromAlias.Reason);
        Assert.Equal("None", fromAlias.ExternalAccess);
    }

    [Fact]
    public void ProgramScopeAlias_ResolvesToControllerBase()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Safety_OK", "BOOL"),
            Rung(0, "NOP();"),
            programTags: AliasTag("Cmd_LocalSafe", "Safety_OK")), GateConfig.Default);

        Assert.Equal(DenyReason.SafetyTag,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_LocalSafe").Reason);
    }

    [Fact]
    public void AliasOfGrantedTag_IsGranted_AndCircularAliasDenied()
    {
        var policy = GateEngine.DerivePolicy(Project(
            BaseTag("Cmd_Free", "DINT") +
            AliasTag("Cmd_FreeAlias", "Cmd_Free") +
            AliasTag("Cmd_LoopA", "Cmd_LoopB") +
            AliasTag("Cmd_LoopB", "Cmd_LoopA")), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_FreeAlias");
        Assert.Equal(DenyReason.UnknownWriteContext,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_LoopA").Reason);
        Assert.Equal(DenyReason.UnknownWriteContext,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_LoopB").Reason);
    }
}
