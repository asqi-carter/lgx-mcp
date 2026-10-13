using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// A tag declared <c>Class="Safety"</c> is denied as <see cref="DenyReason.SafetyTag"/>, like the
/// <c>Safety_</c> name prefix, whatever its name.
/// </summary>
public class GateSafetyClassTests
{
    private static L5X Project(string tags) =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes />
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>{tags}</Tags>
                <Programs />
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

    private static string BaseTag(string name, string cls) =>
        $"""<Tag Name="{name}" TagType="Base" DataType="BOOL" Class="{cls}" Radix="Decimal" ExternalAccess="Read/Write" Constant="false"><Data Format="L5K"><![CDATA[0]]></Data></Tag>""";

    [Fact]
    public void SafetyClassTag_Denied()
    {
        var policy = GateEngine.DerivePolicy(Project(BaseTag("Cmd_Guard", "Safety")), GateConfig.Default);

        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Guard");
        Assert.Equal(DenyReason.SafetyTag, deny.Reason);
        Assert.Contains("Class", deny.Provenance);
        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Guard");
    }

    [Fact]
    public void StandardClassTag_StillGranted()
    {
        var policy = GateEngine.DerivePolicy(Project(BaseTag("Cmd_Start", "Standard")), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Start");
    }
}
