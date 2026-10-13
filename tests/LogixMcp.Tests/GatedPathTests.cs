using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Bit and indexed writes under a range-checked tag or member fail closed (rule 2).
/// Policies are built inline; no customer content.
/// </summary>
public class GatedPathTests : IDisposable
{
    private readonly string _tempDir;

    public GatedPathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gatedpath-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static GatedTagService Build(out VirtualTagService inner)
    {
        inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Speed"]       = (0,   "DINT"),
            ["Cmd_Free"]        = (0,   "DINT"),
            ["Cmd_Motor"]       = (0,   "MotorCmd"),
            ["Cmd_Motor.Enable"] = (false, "BOOL"),
            ["Cmd_Free.3"]      = (false, "BOOL"),
            ["Cmd_Motor.Sub.Speed"] = (0.0, "REAL"),
        });
        var policy = new WriteGatePolicy(
            [
                new GrantEntry("Cmd_Speed", new ScalarBounds(0, 100), null, "Cmd_ prefix. range 0..100"),
                new GrantEntry("Cmd_Free",  null, null, "Cmd_ prefix."),
                new GrantEntry("Cmd_Motor", null,
                [
                    new MemberBounds("Speed", 0, 100),
                    new MemberBounds("Speeds", 0, 100),
                    new MemberBounds("Sub.Speed", 0, 100),
                ], "Cmd_ prefix. Bounds from UDT 'MotorCmd'"),
            ],
            []);
        return new GatedTagService(inner, policy);
    }

    private static async Task AssertRefused(GatedTagService gated, string path, object value)
    {
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(path, value));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    private static async Task AssertRangeCheckedRefusal(GatedTagService gated, string path, object value, string bounded)
    {
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(path, value));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains(bounded, ex.Error.Message);
        Assert.Contains("[0..100]", ex.Error.Message);
        Assert.Contains("bit and element writes to a range-checked value are refused; write the whole value",
            ex.Error.Message);
    }

    [Fact]
    public async Task BitOfBoundedScalar_Refused()
    {
        var gated = Build(out _);
        await AssertRangeCheckedRefusal(gated, "Cmd_Speed.31", true, "Cmd_Speed");
        await AssertRangeCheckedRefusal(gated, "cmd_speed.0", true, "cmd_speed");
        // The whole-value write is still range-checked, not refused outright.
        await gated.WriteAsync("Cmd_Speed", 50);
        await AssertRefused(gated, "Cmd_Speed", 500);
    }

    [Fact]
    public async Task BitOrIndexOfBoundedMember_Refused()
    {
        var gated = Build(out _);
        await AssertRangeCheckedRefusal(gated, "Cmd_Motor.Speed.31", true, "Cmd_Motor.Speed");
        await AssertRangeCheckedRefusal(gated, "Cmd_Motor.Speeds[2]", 5, "Cmd_Motor.Speeds");
        await AssertRangeCheckedRefusal(gated, "Cmd_Motor.SPEEDS[2].3", true, "Cmd_Motor.Speeds");
        await AssertRangeCheckedRefusal(gated, "Cmd_Motor.Sub.Speed.4", true, "Cmd_Motor.Sub.Speed");
        // Malformed paths are outside the tag-name grammar: InvalidTagName, refused before the gate.
        foreach (var bad in new[] { "Cmd_Motor.Speed.", "Cmd_Motor.Speeds[" })
        {
            var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(bad, 5));
            Assert.Equal(TagServiceErrorKind.InvalidTagName, ex.Error.Kind);
        }
    }

    [Fact]
    public async Task NestedMemberBounds_Enforced()
    {
        var gated = Build(out _);
        await AssertRefused(gated, "Cmd_Motor.Sub.Speed", 500.0);
        await AssertRefused(gated, "cmd_motor.sub.speed", 500.0);
        await AssertRefused(gated, "Cmd_Motor.Speed", 500.0);   // first-level exact match kept

        await gated.WriteAsync("Cmd_Motor.Sub.Speed", 50.0);
    }

    [Fact]
    public void NestedMemberBounds_DerivedFromUdtDefinitions()
    {
        var project = L5X.Parse("""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes>
                  <DataType Name="Inner" Family="NoFamily" Class="User"><Members>
                    <Member Name="Speed" DataType="REAL" Dimension="0" Radix="NullType" ExternalAccess="Read/Write" Hidden="false"><Description><![CDATA[range 0..100]]></Description></Member>
                  </Members></DataType>
                  <DataType Name="Outer" Family="NoFamily" Class="User"><Members>
                    <Member Name="Sub" DataType="Inner" Dimension="0" Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  </Members></DataType>
                </DataTypes>
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>
                  <Tag Name="Cmd_Outer" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="Outer"><Data Format="Decorated"><Structure DataType="Outer" /></Data></Tag>
                </Tags>
                <Programs />
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);
        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var grant = Assert.Single(policy.Grants, g => g.Tag == "Cmd_Outer");
        Assert.Contains(grant.Bounds!, b => b.Member == "Sub.Speed" && b.Max == 100);
    }

    [Fact]
    public async Task UnboundedBitWrite_StillAllowed()
    {
        var gated = Build(out _);
        // A bit of an unbounded granted DINT.
        await gated.WriteAsync("Cmd_Free.3", true);
        // An unbounded BOOL member of a UDT with other bounded members.
        await gated.WriteAsync("Cmd_Motor.Enable", true);
        // An unbounded bit write is not logged as a refusal.
        Assert.DoesNotContain("BoundedPathWrite",
            File.ReadAllText(Path.Combine(_tempDir, "audit.jsonl")));
    }

    [Fact]
    public async Task NonNumericValueForBoundedTag_Refused()
    {
        var gated = Build(out _);
        foreach (var path in new[] { "Cmd_Speed", "Cmd_Motor.Speed", "Cmd_Motor.Sub.Speed" })
        foreach (var bad in new object[] { "abc", "", new object() })
        {
            var ex = await Assert.ThrowsAsync<TagServiceException>(() => gated.WriteAsync(path, bad));
            Assert.Equal(TagServiceErrorKind.InvalidValue, ex.Error.Kind);
            Assert.Contains("not numeric", ex.Error.Message);
        }
        // Numeric values (including numeric strings) still go through the range check.
        await gated.WriteAsync("Cmd_Speed", 50);
        // Unbounded tags are unaffected.
        await gated.WriteAsync("Cmd_Free", 7);
    }
}
