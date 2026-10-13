using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// A range annotation is either applied to the grant or the tag is denied; a grant never
/// silently drops it. The data type is read from the tag's DataType attribute, so a tag
/// carrying only L5K data (no decorated value) is bounded like any other. Projects are inline.
/// </summary>
public class TagBoundsTests : IDisposable
{
    private readonly string _tempDir;

    public TagBoundsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tagbounds-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string UdtXml(string name, string speedDescription) => $"""
        <DataType Name="{name}" Family="NoFamily" Class="User"><Members>
          <Member Name="Enable" DataType="BOOL" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
          <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false"><Description><![CDATA[{speedDescription}]]></Description></Member>
        </Members></DataType>
        """;

    private static string L5kTag(string name, string type, string description, string l5k = "0") =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="{type}" Radix="Decimal"><Description><![CDATA[{description}]]></Description><Data Format="L5K"><![CDATA[{l5k}]]></Data></Tag>""";

    private static string DecoratedTag(string name, string type, string description) =>
        $"""<Tag Name="{name}" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="{type}" Radix="Decimal"><Description><![CDATA[{description}]]></Description><Data Format="Decorated"><DataValue DataType="{type}" Radix="Decimal" Value="0" /></Data></Tag>""";

    private static L5X Project(string tags, string dataTypes = "") => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>{dataTypes}</DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>{tags}</Tags>
            <Programs />
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private const string CouldNotApply = "range annotation could not be applied";

    [Fact]
    public void L5kOnlyTag_BoundsAppliedOrDenied()
    {
        var policy = GateEngine.DerivePolicy(
            Project(L5kTag("Cmd_Speed", "REAL", "range 0..100", "0.0")), GateConfig.Default);

        var grant = policy.Grants.SingleOrDefault(g => g.Tag == "Cmd_Speed");
        if (grant is not null)
        {
            // Never an unbounded grant for an annotated tag.
            Assert.NotNull(grant.TagBounds);
            Assert.Equal(0.0, grant.TagBounds!.Min);
            Assert.Equal(100.0, grant.TagBounds!.Max);
        }
        else
        {
            var deny = policy.Denies.Single(d => d.Tag == "Cmd_Speed");
            Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
            Assert.Contains(CouldNotApply, deny.Provenance);
        }

        // The data type is read from the attribute, so the bounds are in fact applied.
        Assert.NotNull(grant);
    }

    [Fact]
    public void L5kOnlyUdtTag_MemberBoundsApplied()
    {
        var policy = GateEngine.DerivePolicy(
            Project(L5kTag("Cmd_Motor", "MotorCmd", "", "[0,0.0]"), UdtXml("MotorCmd", "range 0..100")),
            GateConfig.Default);

        var grant = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        Assert.Contains(grant.Bounds!, b => b.Member == "Speed" && b.Min == 0 && b.Max == 100);
    }

    [Theory]
    [InlineData("range 0 to 100")]
    [InlineData("range: 0 to 100")]
    [InlineData("range 100..0")]
    public void ScalarWithUnappliableRange_IsDenied(string description)
    {
        var policy = GateEngine.DerivePolicy(
            Project(DecoratedTag("Cmd_Speed", "REAL", description)), GateConfig.Default);

        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Speed");
        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Speed");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains(CouldNotApply, deny.Provenance);
    }

    [Fact]
    public void ScalarRangeOnNonNumericType_IsDenied()
    {
        var policy = GateEngine.DerivePolicy(
            Project(DecoratedTag("Cmd_Flag", "BOOL", "range 0..1")), GateConfig.Default);

        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Flag");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains(CouldNotApply, deny.Provenance);
    }

    [Fact]
    public void UdtMemberWithUnappliableRange_DeniesTagsOfThatType()
    {
        var policy = GateEngine.DerivePolicy(
            Project(DecoratedTag("Cmd_Motor", "BadCmd", ""), UdtXml("BadCmd", "range 0 to 100")),
            GateConfig.Default);

        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Motor");
        var deny = policy.Denies.Single(d => d.Tag == "Cmd_Motor");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
        Assert.Contains(CouldNotApply, deny.Provenance);
    }

    [Fact]
    public void ScalarWithoutAnnotation_StillGrantedUnbounded()
    {
        var policy = GateEngine.DerivePolicy(
            Project(DecoratedTag("Cmd_Speed", "REAL", "Speed setpoint")), GateConfig.Default);

        var grant = policy.Grants.Single(g => g.Tag == "Cmd_Speed");
        Assert.Null(grant.TagBounds);
    }
}
