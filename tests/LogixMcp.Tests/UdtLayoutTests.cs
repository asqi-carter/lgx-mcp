// ── UdtLayoutTests — unit tests for UdtLayout byte-offset resolver ────────────
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="UdtLayout"/>.
///
/// <para>
/// All tests use inline L5X strings whose offsets were computed by applying
/// Studio 5000 alignment rules by hand. Expected offsets are derived by
/// applying the Logix alignment rules stated in the class summary; live
/// cross-checking against a real controller is performed by
/// <c>HardwareTests.MemberRead_OnEcho_TotalDoseCount_MatchesCipRead</c>
/// when the Echo bench is reachable.
/// </para>
///
/// <para>Coverage:</para>
/// <list type="bullet">
///   <item>Scalar DINT/INT members at expected byte offsets.</item>
///   <item>INT array element offsets (base + 2 × index).</item>
///   <item>BOOL members packed into a hidden DINT host by bit position.</item>
///   <item>Nested UDT aligned to 4 bytes with structural padding.</item>
/// </list>
/// </summary>
public class UdtLayoutTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Inline L5X for layout tests against a "RecipeTagsType" UDT.
    ///
    /// <para>
    /// Expected layout (Studio 5000 rules):
    /// <code>
    /// Offset  Size  Member
    /// ──────  ────  ──────────────────────
    ///      0     4  RecipeNameLen  (DINT)
    ///      4     2  TotalDoseCount (INT)
    ///      6    10  SetupValues[5] (INT[5])  — SetupValues[i] at 6 + 2i
    /// ──────  ────
    ///     16        (already multiple of 4, no tail pad)
    /// </code>
    /// </para>
    /// </summary>
    private const string RecipeTagsL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="LayoutTestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="LayoutTestController" MajorRev="36" MinorRev="1"
                      ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="RecipeTagsType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="RecipeNameLen"      DataType="DINT" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="TotalDoseCount"  DataType="INT"  Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="SetupValues"         DataType="INT"  Dimension="5"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules /><AddOnInstructionDefinitions />
            <Tags /><Programs /><Tasks />
            <ParameterConnections /><Trends /><QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// Inline L5X for BOOL-packing tests.
    ///
    /// <para>
    /// BoolHostType contains a hidden DINT host at offset 0 followed by two
    /// BOOL members referencing that host at bits 0 and 1.
    /// </para>
    /// </summary>
    private const string BoolPackL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="BoolPackController" TargetType="Controller" ContainsContext="false">
          <Controller Name="BoolPackController" MajorRev="36" MinorRev="1"
                      ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="BoolHostType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="ZzzFoo_D00_0" DataType="DINT" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="true" />
                  <Member Name="Flag0" DataType="BOOL" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false"
                          Target="ZzzFoo_D00_0" BitNumber="0" />
                  <Member Name="Flag1" DataType="BOOL" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false"
                          Target="ZzzFoo_D00_0" BitNumber="1" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules /><AddOnInstructionDefinitions />
            <Tags /><Programs /><Tasks />
            <ParameterConnections /><Trends /><QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// Inline L5X for nested-UDT alignment tests.
    ///
    /// <para>
    /// NestedOuterType layout:
    /// <code>
    /// Offset  Size  Member
    /// ──────  ────  ──────────────────────
    ///      0     1  Alpha   (SINT)
    ///   1–3     3  (padding to 4-byte alignment for nested UDT)
    ///      4     4  Inner   (NestedInnerType — 2×INT = 4 bytes)
    /// ──────  ────
    ///      8        (already multiple of 4)
    /// </code>
    /// </para>
    /// <para>
    /// NestedInnerType: X(INT)=0, Y(INT)=2, size=4.
    /// Therefore Inner.X is at outer-offset 4, Inner.Y at 6.
    /// </para>
    /// </summary>
    private const string NestedUdtL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="NestedController" TargetType="Controller" ContainsContext="false">
          <Controller Name="NestedController" MajorRev="36" MinorRev="1"
                      ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="NestedInnerType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="X" DataType="INT" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Y" DataType="INT" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
              <DataType Name="NestedOuterType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Alpha" DataType="SINT"            Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Inner" DataType="NestedInnerType" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules /><AddOnInstructionDefinitions />
            <Tags /><Programs /><Tasks />
            <ParameterConnections /><Trends /><QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// Inline L5X with a user-defined string type (Family="StringFamily") nested
    /// in an outer UDT after a SINT, plus an array of them.
    /// </summary>
    private const string StringFamilyL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="StrController" TargetType="Controller" ContainsContext="false">
          <Controller Name="StrController" MajorRev="36" MinorRev="1"
                      ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="STRING_20" Family="StringFamily" Class="User">
                <Members>
                  <Member Name="LEN"  DataType="DINT" Dimension="0"
                          Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="DATA" DataType="SINT" Dimension="20"
                          Radix="ASCII" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
              <DataType Name="StrOuterType" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Flag"  DataType="SINT"      Dimension="0"
                          Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Name"  DataType="STRING_20" Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Tags"  DataType="STRING_20" Dimension="2"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Std"   DataType="STRING"    Dimension="0"
                          Radix="NullType" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="After" DataType="INT"       Dimension="0"
                          Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules /><AddOnInstructionDefinitions />
            <Tags /><Programs /><Tasks />
            <ParameterConnections /><Trends /><QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static L5X ParseRecipeTags()  => L5X.Parse(RecipeTagsL5Xml);
    private static L5X ParseBoolPack()    => L5X.Parse(BoolPackL5Xml);
    private static L5X ParseNestedUdt()   => L5X.Parse(NestedUdtL5Xml);

    // ── RecipeTags tests ─────────────────────────────────────────────────────

    /// <summary>
    /// The first declared member <c>RecipeNameLen</c> is a DINT; because it is
    /// the first member with 4-byte alignment at offset 0, it must resolve to
    /// byte-offset 0 with size 4 and type <c>"DINT"</c>.
    /// </summary>
    [Fact]
    public void RecipeTags_RecipeNameLen_IsAtOffsetZero_Dint()
    {
        var layout = new UdtLayout(ParseRecipeTags());

        var result = layout.Resolve("RecipeTagsType", "RecipeNameLen");

        Assert.Equal(0, result.ByteOffset);
        Assert.Equal(4, result.ByteSize);
        Assert.Equal("DINT", result.LogixType);
        Assert.Equal(-1, result.BitIndex);  // not a BOOL bit member
    }

    /// <summary>
    /// <c>TotalDoseCount</c> (INT, 2 bytes) follows the 4-byte DINT
    /// <c>RecipeNameLen</c>, so its byte offset must be exactly 4.
    /// The expected offset is derived by applying the Logix alignment rules;
    /// live cross-checking against a real controller is performed by
    /// <c>HardwareTests.MemberRead_OnEcho_TotalDoseCount_MatchesCipRead</c>
    /// when the Echo bench is reachable.
    /// </summary>
    [Fact]
    public void RecipeTags_TotalDoseCount_OffsetMatchesFixture()
    {
        var layout = new UdtLayout(ParseRecipeTags());

        var result = layout.Resolve("RecipeTagsType", "TotalDoseCount");

        // After 4-byte DINT at offset 0, INT at offset 4 (no padding needed —
        // offset 4 is already 2-byte aligned).
        Assert.Equal(4, result.ByteOffset);
        Assert.Equal(2, result.ByteSize);
        Assert.Equal("INT", result.LogixType);
    }

    /// <summary>
    /// <c>SetupValues</c> is an <c>INT[5]</c> array.  Its base offset is 6
    /// (after DINT[4 bytes] + INT[2 bytes]).  Each INT element is 2 bytes, so
    /// <c>SetupValues[i]</c> must be at byte offset <c>6 + 2 × i</c>.
    /// </summary>
    [Fact]
    public void RecipeTags_SetupValuesIndex_OffsetIsBasePlusTwoI()
    {
        var layout = new UdtLayout(ParseRecipeTags());

        // The base offset of SetupValues is 6 (= 0+4 DINT + 4+2 INT).
        // Each element is 2 bytes (INT), so [i] is at 6 + 2×i.
        for (int i = 0; i < 5; i++)
        {
            var result = layout.Resolve("RecipeTagsType", $"SetupValues[{i}]");

            Assert.Equal(6 + 2 * i, result.ByteOffset);
            Assert.Equal(2, result.ByteSize);
            Assert.Equal("INT", result.LogixType);
        }
    }

    // ── BOOL packing test ────────────────────────────────────────────────────

    /// <summary>
    /// BOOL members packed into a hidden DINT host must resolve to the host's
    /// byte offset (not a separate address), with the host's size (4 bytes),
    /// and must carry the correct bit index from the <c>BitNumber</c> attribute.
    /// </summary>
    [Fact]
    public void BoolMembers_PackIntoHiddenHost_ByBit()
    {
        var layout = new UdtLayout(ParseBoolPack());

        // The hidden DINT host is the first member — offset 0, size 4.
        var flag0 = layout.Resolve("BoolHostType", "Flag0");
        var flag1 = layout.Resolve("BoolHostType", "Flag1");

        // Both BOOLs share the same host word at offset 0.
        Assert.Equal(0, flag0.ByteOffset);
        Assert.Equal(4, flag0.ByteSize);   // host is DINT (4 bytes)
        Assert.Equal("BOOL", flag0.LogixType);
        Assert.Equal(0, flag0.BitIndex);   // bit 0 in the DINT host

        Assert.Equal(0, flag1.ByteOffset);
        Assert.Equal(4, flag1.ByteSize);
        Assert.Equal("BOOL", flag1.LogixType);
        Assert.Equal(1, flag1.BitIndex);   // bit 1 in the DINT host
    }

    // ── Nested UDT test ──────────────────────────────────────────────────────

    /// <summary>
    /// A nested UDT member must be aligned to a 4-byte boundary in the outer
    /// struct (the 1-byte SINT <c>Alpha</c> is followed by 3 bytes of implicit
    /// padding), and the outer struct must be padded to a multiple of 4 bytes.
    /// Navigating <c>Inner.X</c> must yield the outer UDT's 4-byte-aligned
    /// base for <c>Inner</c> plus the inner type's offset for <c>X</c>.
    /// </summary>
    [Fact]
    public void NestedUdt_AlignsToFour_AndPads()
    {
        var layout = new UdtLayout(ParseNestedUdt());

        // NestedOuterType: Alpha(SINT)=offset 0, Inner(NestedInnerType)=offset 4.
        // NestedInnerType: X(INT)=offset 0, Y(INT)=offset 2.
        // Therefore Inner.X → outer offset 4 + inner offset 0 = 4.
        var innerX = layout.Resolve("NestedOuterType", "Inner.X");
        var innerY = layout.Resolve("NestedOuterType", "Inner.Y");

        // Inner is aligned to 4 (SINT Alpha at 0 → pad 3 bytes → Inner at 4).
        Assert.Equal(4, innerX.ByteOffset);
        Assert.Equal(2, innerX.ByteSize);
        Assert.Equal("INT", innerX.LogixType);

        // Y is 2 bytes after X within NestedInnerType.
        Assert.Equal(6, innerY.ByteOffset);
        Assert.Equal(2, innerY.ByteSize);
        Assert.Equal("INT", innerY.LogixType);
    }

    // ── StringFamily tests ───────────────────────────────────────────────────

    /// <summary>
    /// A StringFamily type (STRING_20: LEN DINT + DATA SINT[20]) is sized from
    /// its declared members (24 bytes), aligned to 4 inside the outer UDT,
    /// exposes its DATA capacity, and does not disturb built-in STRING (88).
    /// </summary>
    [Fact]
    public void StringFamilyType_SizedFromDeclaredMembers()
    {
        var layout = new UdtLayout(L5X.Parse(StringFamilyL5Xml));

        // Flag SINT @0; Name aligned to 4 → @4, size 24.
        var name = layout.Resolve("StrOuterType", "Name");
        Assert.Equal(4, name.ByteOffset);
        Assert.Equal(24, name.ByteSize);
        Assert.Equal("STRING_20", name.LogixType);
        Assert.Equal(20, name.StringDataLength);

        // Inside the string: LEN @4, DATA @8.
        Assert.Equal(4, layout.Resolve("StrOuterType", "Name.LEN").ByteOffset);
        var data = layout.Resolve("StrOuterType", "Name.DATA");
        Assert.Equal(8, data.ByteOffset);
        Assert.Equal(-1, data.StringDataLength);

        // Array of strings: Tags @28, stride 24.
        Assert.Equal(28, layout.Resolve("StrOuterType", "Tags[0]").ByteOffset);
        Assert.Equal(52, layout.Resolve("StrOuterType", "Tags[1]").ByteOffset);

        // Built-in STRING: 88 bytes, 82 data chars; follows Tags (28 + 48 = 76).
        var std = layout.Resolve("StrOuterType", "Std");
        Assert.Equal(76, std.ByteOffset);
        Assert.Equal(88, std.ByteSize);
        Assert.Equal(82, std.StringDataLength);

        // Member after the strings: 76 + 88 = 164.
        var after = layout.Resolve("StrOuterType", "After");
        Assert.Equal(164, after.ByteOffset);
        Assert.Equal(-1, after.StringDataLength);

        Assert.Equal(20, layout.StringDataLength("STRING_20"));
        Assert.Equal(82, layout.StringDataLength("STRING"));
        Assert.Null(layout.StringDataLength("StrOuterType"));
    }
}
