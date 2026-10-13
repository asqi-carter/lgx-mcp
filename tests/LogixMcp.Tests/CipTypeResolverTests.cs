namespace LogixMcp.Tests;

/// <summary>Unit tests for <see cref="CipTypeResolver"/> (no network).</summary>
public class CipTypeResolverTests
{
    private const string ProjectXml = """
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="Inner_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Value" DataType="LINT" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
              <DataType Name="Probe_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Count" DataType="DINT" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Inner" DataType="Inner_UDT" Dimension="0" Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Hist" DataType="INT" Dimension="4" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
              <DataType Name="Flags_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="ZZZZZZZZZZFlags_UDT0" DataType="SINT" Dimension="0" Radix="Decimal" Hidden="true" ExternalAccess="Read/Write" />
                  <Member Name="Run" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZFlags_UDT0" BitNumber="0" ExternalAccess="Read/Write" />
                  <Member Name="Fault" DataType="BIT" Dimension="0" Radix="Decimal" Hidden="false" Target="ZZZZZZZZZZFlags_UDT0" BitNumber="1" ExternalAccess="Read/Write" />
                </Members>
              </DataType>
              <DataType Name="Outer_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Probes" DataType="Probe_UDT" Dimension="3" Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Speed_SP" TagType="Base" DataType="REAL" Radix="Float" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><DataValue DataType="REAL" Radix="Float" Value="0.0"/></Data>
              </Tag>
              <Tag Name="Probe_Udt" TagType="Base" DataType="Probe_UDT" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Structure DataType="Probe_UDT" /></Data>
              </Tag>
              <Tag Name="Probes" TagType="Base" DataType="Probe_UDT" Dimensions="3" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Array DataType="Probe_UDT" Dimensions="3"><Element Index="[0]"><Structure DataType="Probe_UDT" /></Element></Array></Data>
              </Tag>
              <Tag Name="Flags" TagType="Base" DataType="Flags_UDT" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Structure DataType="Flags_UDT" /></Data>
              </Tag>
              <Tag Name="Outer" TagType="Base" DataType="Outer_UDT" Constant="false" ExternalAccess="Read/Write">
                <Data Format="Decorated"><Structure DataType="Outer_UDT" /></Data>
              </Tag>
            </Tags>
            <Programs />
          </Controller>
        </RSLogix5000Content>
        """;

    private static CipTypeResolver ProjectResolver() =>
        new(L5Sharp.Core.L5X.Parse(ProjectXml), _ => throw new InvalidOperationException("tag list must not be read"));

    private static CipTypeResolver TagListResolver(out int[] calls, params TagInfo[] tags)
    {
        var counter = new int[1];
        calls = counter;
        return new CipTypeResolver(null, _ =>
        {
            counter[0]++;
            return Task.FromResult<IReadOnlyList<TagInfo>>(tags);
        });
    }

    [Fact]
    public async Task ProjectTag_DeclaredType()
    {
        var r = await ProjectResolver().ResolveAsync("Speed_SP");

        Assert.Equal(CipTypeKind.Atomic, r.Kind);
        Assert.Equal("REAL", r.TypeName);
        Assert.Equal(CipTypeSource.Project, r.Source);

        var s = await ProjectResolver().ResolveAsync("Probe_Udt");
        Assert.Equal(CipTypeKind.Structure, s.Kind);
        Assert.True(s.IsResolved, s.Reason);
        Assert.Equal("Probe_UDT", s.TypeName);
        Assert.Contains("Count", s.MemberPaths);
        Assert.Contains("Speed", s.MemberPaths);
        Assert.Contains("Inner.Value", s.MemberPaths);
    }

    [Fact]
    public async Task MemberPath_WalksUdtMembers_IncludingNestedAndArrayElement()
    {
        var resolver = ProjectResolver();

        Assert.Equal("REAL", (await resolver.ResolveAsync("Probe_Udt.Speed")).TypeName);
        Assert.Equal("DINT", (await resolver.ResolveAsync("Probe_Udt.Count")).TypeName);
        Assert.Equal("LINT", (await resolver.ResolveAsync("Probe_Udt.Inner.Value")).TypeName);
        Assert.Equal("INT",  (await resolver.ResolveAsync("Probe_Udt.Hist[2]")).TypeName);

        // Array element of a UDT, then a member.
        Assert.Equal("REAL", (await resolver.ResolveAsync("Probes[3].Speed")).TypeName);
        Assert.Equal("Probe_UDT", (await resolver.ResolveAsync("Probes[1]")).TypeName);

        // UDT array member inside a UDT, then nested member.
        Assert.Equal("LINT", (await resolver.ResolveAsync("Outer.Probes[0].Inner.Value")).TypeName);

        // Bit selector on an integer member.
        var bit = await resolver.ResolveAsync("Probe_Udt.Count.5");
        Assert.Equal("BOOL", bit.TypeName);

        // A UDT BOOL member is declared BIT in the project; it reads as BOOL (bench 2026-10-06:
        // a packed BOOL member was refused as a structure over CIP).
        var packed = await resolver.ResolveAsync("Flags.Fault");
        Assert.Equal(CipTypeKind.Atomic, packed.Kind);
        Assert.Equal("BOOL", packed.TypeName);

        // Failures stay unresolved: unknown member, whole array, bad index count.
        Assert.False((await resolver.ResolveAsync("Probe_Udt.Nope")).IsResolved);
        Assert.False((await resolver.ResolveAsync("Probes")).IsResolved);
        Assert.False((await resolver.ResolveAsync("Probe_Udt.Speed[1]")).IsResolved);
        Assert.False((await resolver.ResolveAsync("Probe_Udt.Speed.3")).IsResolved);
    }

    [Fact]
    public async Task NoProject_AtomicControllerTag_FromTagList()
    {
        var resolver = TagListResolver(out var calls,
            new TagInfo("Count_DINT", "DINT"),
            new TagInfo("Level", "REAL"),
            new TagInfo("Big", "ULINT"),
            new TagInfo("Probe_Udt", null));

        var level = await resolver.ResolveAsync("Level");
        Assert.Equal(CipTypeKind.Atomic, level.Kind);
        Assert.Equal("REAL", level.TypeName);
        Assert.Equal(CipTypeSource.TagList, level.Source);

        Assert.Equal("ULINT", (await resolver.ResolveAsync("Big")).TypeName);
        Assert.Equal("DINT", (await resolver.ResolveAsync("count_dint")).TypeName);

        // Non-atomic (null type) is unresolved, not a guess.
        Assert.False((await resolver.ResolveAsync("Probe_Udt")).IsResolved);

        // Read once per session and cached.
        Assert.Equal(1, calls[0]);
    }

    [Fact]
    public async Task NoProject_MemberPath_Unresolved()
    {
        var resolver = TagListResolver(out _,
            new TagInfo("Probe_Udt", null),
            new TagInfo("Level", "REAL"));

        foreach (var path in new[] { "Probe_Udt.Speed", "Level.3", "Arr[2]", "Program:Main.Level" })
        {
            var r = await resolver.ResolveAsync(path);
            Assert.Equal(CipTypeKind.Unresolved, r.Kind);
            Assert.Null(r.TypeName);
            Assert.NotNull(r.Reason);
        }
    }

    [Fact]
    public async Task NoProject_TagListFailure_IsUnresolved_AndRetried()
    {
        var attempts = 0;
        var resolver = new CipTypeResolver(null, _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new TagServiceException(new TagServiceError(TagServiceErrorKind.ConnectionFailed, "down"));
            return Task.FromResult<IReadOnlyList<TagInfo>>(new[] { new TagInfo("Level", "REAL") });
        });

        Assert.False((await resolver.ResolveAsync("Level")).IsResolved);
        Assert.Equal("REAL", (await resolver.ResolveAsync("Level")).TypeName);
    }

    [Fact]
    public void MapTypeCode_AllAtomicCodes()
    {
        var expected = new Dictionary<int, string>
        {
            [0xC1] = "BOOL",  [0xC2] = "SINT",  [0xC3] = "INT",   [0xC4] = "DINT",
            [0xC5] = "LINT",  [0xC6] = "USINT", [0xC7] = "UINT",  [0xC8] = "UDINT",
            [0xC9] = "ULINT", [0xCA] = "REAL",  [0xCB] = "LREAL",
        };

        foreach (var (code, name) in expected)
            Assert.Equal(name, LibPlcTagService.MapTypeCode(code));

        // Not atomic: arrays/structures (upper byte set) and unknown codes.
        Assert.Null(LibPlcTagService.MapTypeCode(0x20C4));
        Assert.Null(LibPlcTagService.MapTypeCode(0x8FCA));
        Assert.Null(LibPlcTagService.MapTypeCode(0xCC));
        Assert.Null(LibPlcTagService.MapTypeCode(0));
    }
}
