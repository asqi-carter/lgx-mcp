using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="L5XOfflineTagService"/> — the L5Sharp-backed
/// <see cref="ITagService"/> implementation.
///
/// Coverage areas:
///   * Construction — null guard, valid project
///   * Interface assignability
///   * ConnectAsync / DisconnectAsync — connected-flag contract
///   * Pre-connect guards — ReadAsync, WriteAsync, ListTagsAsync must throw
///     ConnectionFailed when ConnectAsync has not been called
///   * ReadAsync — happy path (BOOL, REAL), TagNotFound for unknown tags,
///     and post-disconnect guard
///   * ReadAsync — typed extraction for every atomic type (BOOL, SINT, INT,
///     DINT, LINT, USINT, UINT, UDINT, ULINT, REAL, LREAL); verifies both
///     the DataType string and the .NET type of Value
///   * ReadAsync — UDT member dot-notation paths (Cmd_Motor.Enable,
///     Cmd_Motor.Speed) resolved via L5Sharp's tag index
///   * WriteAsync — happy path, TypeMismatch for unsupported value types,
///     TagNotFound for unknown tags, and post-disconnect guard
///   * WriteAsync → ReadAsync round-trips — confirms in-memory mutation
///   * ListTagsAsync — returns correct entries from the fixture project
///   * Type-coverage assertions — validates that the unsigned family and LREAL
///     are accepted by WriteAsync (not rejected as TypeMismatch)
/// </summary>
public class L5XOfflineTagServiceTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>Path to the shared Demo.L5X fixture.</summary>
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// A minimal in-memory L5X project that has one tag per supported atomic
    /// type.  Used by typed-extraction tests so Demo.L5X (which only has BOOL
    /// and REAL tags) does not need to be extended.
    /// </summary>
    private const string AllTypesL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TypeTestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TypeTestController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="BoolTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="BOOL"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="BOOL"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="SintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="SINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="SINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="IntTag"    TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="INT"   Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="INT"   Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="DintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="DINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="DINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="LintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="LINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="LINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UsintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="USINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="USINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UintTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="UINT"  Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="UINT"  Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UdintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="UDINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="UDINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="UlintTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="ULINT" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="ULINT" Radix="Decimal" Value="0"/></Data>
              </Tag>
              <Tag Name="RealTag"   TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="REAL"  Radix="Float">
                <Data Format="Decorated"><DataValue DataType="REAL"  Radix="Float"   Value="0.0"/></Data>
              </Tag>
              <Tag Name="LrealTag"  TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="LREAL" Radix="Float">
                <Data Format="Decorated"><DataValue DataType="LREAL" Radix="Float"   Value="0.0"/></Data>
              </Tag>
            </Tags>
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>
    /// Loads and returns a fresh <see cref="L5X"/> project for each test to
    /// prevent state leakage between tests.
    /// </summary>
    private static L5X LoadProject() => L5X.Load(FixturePath);

    /// <summary>
    /// Parses the in-memory all-types L5X project, providing one tag per
    /// supported atomic type.
    /// </summary>
    private static L5X BuildAllTypesProject() => L5X.Parse(AllTypesL5Xml);

    /// <summary>
    /// Builds a connected <see cref="L5XOfflineTagService"/> backed by the
    /// Demo.L5X fixture.
    /// </summary>
    private static async Task<L5XOfflineTagService> ConnectedServiceAsync()
    {
        var svc = new L5XOfflineTagService(LoadProject());
        await svc.ConnectAsync("unused-gateway", "1,0");
        return svc;
    }

    /// <summary>
    /// Builds a connected <see cref="L5XOfflineTagService"/> backed by the
    /// all-types in-memory project for typed-extraction tests.
    /// </summary>
    private static async Task<L5XOfflineTagService> ConnectedServiceWithAllTypesAsync()
    {
        var svc = new L5XOfflineTagService(BuildAllTypesProject());
        await svc.ConnectAsync("unused-gateway", "1,0");
        return svc;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullProject_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new L5XOfflineTagService(null!));
    }

    [Fact]
    public void Constructor_ValidProject_DoesNotThrow()
    {
        var project = LoadProject();

        // Must not throw.
        var svc = new L5XOfflineTagService(project);

        Assert.NotNull(svc);
    }

    // ── Interface contract ─────────────────────────────────────────────────────

    [Fact]
    public void L5XOfflineTagService_ImplementsITagService()
    {
        ITagService svc = new L5XOfflineTagService(LoadProject());

        Assert.NotNull(svc);
    }

    // ── ConnectAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_DoesNotThrow()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        // No exception expected; gateway/path are ignored for offline projects.
        await svc.ConnectAsync("198.51.100.1", "1,0");
    }

    [Fact]
    public async Task ConnectAsync_WithEmptyGateway_DoesNotThrow()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        // Unlike LibPlcTagService, L5XOfflineTagService does not validate the
        // gateway address — the backing data comes from the in-memory project.
        await svc.ConnectAsync(string.Empty, string.Empty);
    }

    [Fact]
    public async Task ConnectAsync_CalledTwice_DoesNotThrow()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        await svc.ConnectAsync("198.51.100.1", "1,0");
        await svc.ConnectAsync("10.0.0.5", "1,2");   // re-connect is a no-op
    }

    // ── DisconnectAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        await svc.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectAsync_AfterConnect_DoesNotThrow()
    {
        var svc = new L5XOfflineTagService(LoadProject());
        await svc.ConnectAsync("198.51.100.1", "1,0");

        await svc.DisconnectAsync();
    }

    // ── Pre-connect guards ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_BeforeConnect_ThrowsConnectionFailed()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_AfterDisconnect_ThrowsConnectionFailed()
    {
        var svc = new L5XOfflineTagService(LoadProject());
        await svc.ConnectAsync("198.51.100.1", "1,0");
        await svc.DisconnectAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_MotorStart"));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_BeforeConnect_ThrowsConnectionFailed()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_AfterDisconnect_ThrowsConnectionFailed()
    {
        var svc = new L5XOfflineTagService(LoadProject());
        await svc.ConnectAsync("198.51.100.1", "1,0");
        await svc.DisconnectAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", true));

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    [Fact]
    public async Task ListTagsAsync_BeforeConnect_ThrowsConnectionFailed()
    {
        var svc = new L5XOfflineTagService(LoadProject());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ListTagsAsync());

        Assert.Equal(TagServiceErrorKind.ConnectionFailed, ex.Error.Kind);
    }

    // ── ReadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_BoolTag_ReturnsCorrectValueAndType()
    {
        var svc = await ConnectedServiceAsync();

        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal("Cmd_MotorStart", result.TagName);
        Assert.Equal("BOOL", result.DataType);
        // Demo.L5X initialises Cmd_MotorStart to 0 (false).
        Assert.Equal(false, result.Value);
    }

    [Fact]
    public async Task ReadAsync_RealTag_ReturnsCorrectValueAndType()
    {
        var svc = await ConnectedServiceAsync();

        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal("Cmd_SpeedSetpoint", result.TagName);
        Assert.Equal("REAL", result.DataType);
    }

    [Fact]
    public async Task ReadAsync_UnknownTag_ThrowsTagNotFound()
    {
        var svc = await ConnectedServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("NonExistent_Tag"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("NonExistent_Tag", ex.Error.TagName);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNonNullTagValue()
    {
        var svc = await ConnectedServiceAsync();

        var result = await svc.ReadAsync("Safety_OK");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ReadAsync_TagNameCaseInsensitive()
    {
        var svc = await ConnectedServiceAsync();

        // L5Sharp tag look-up should be case-insensitive.
        var result = await svc.ReadAsync("cmd_motorstart");

        Assert.NotNull(result);
        Assert.Equal("BOOL", result.DataType);
    }

    // ── WriteAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_BoolTag_DoesNotThrow()
    {
        var svc = await ConnectedServiceAsync();

        await svc.WriteAsync("Cmd_MotorStart", true);
    }

    [Fact]
    public async Task WriteAsync_RealTag_DoesNotThrow()
    {
        var svc = await ConnectedServiceAsync();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 42.0f);
    }

    [Fact]
    public async Task WriteAsync_UnknownTag_ThrowsTagNotFound()
    {
        var svc = await ConnectedServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NoSuch_Tag", 42));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("NoSuch_Tag", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_UnsupportedValueType_ThrowsTypeMismatch()
    {
        var svc = await ConnectedServiceAsync();

        // A plain string is not a supported PLC value type.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Cmd_MotorStart", "not-a-plc-value"));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
        Assert.Equal("Cmd_MotorStart", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_TypeMismatch_IsRaisedBeforeTagLookup()
    {
        var svc = await ConnectedServiceAsync();

        // Even for a tag that doesn't exist, TypeMismatch should be raised
        // because value validation happens before the tag look-up.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NonExistent_Tag", new object()));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    // ── ReadAsync — typed extraction for all atomic types ─────────────────────
    //
    // Verifies that L5XOfflineTagService.ExtractValue returns the correct .NET
    // type and the correct Logix DataType string for every supported atomic type.
    // Uses the in-memory AllTypesL5Xml fixture so no change to Demo.L5X is needed.

    [Theory]
    [InlineData("BoolTag",  "BOOL",  "Boolean")]   // BOOL  → bool
    [InlineData("SintTag",  "SINT",  "SByte")]      // SINT  → sbyte
    [InlineData("IntTag",   "INT",   "Int16")]      // INT   → short
    [InlineData("DintTag",  "DINT",  "Int32")]      // DINT  → int
    [InlineData("LintTag",  "LINT",  "Int64")]      // LINT  → long
    [InlineData("UsintTag", "USINT", "Byte")]       // USINT → byte
    [InlineData("UintTag",  "UINT",  "UInt16")]     // UINT  → ushort
    [InlineData("UdintTag", "UDINT", "UInt32")]     // UDINT → uint
    [InlineData("UlintTag", "ULINT", "UInt64")]     // ULINT → ulong
    [InlineData("RealTag",  "REAL",  "Single")]     // REAL  → float
    [InlineData("LrealTag", "LREAL", "Double")]     // LREAL → double
    public async Task ReadAsync_ExtractsCorrectNativeType_ForAllAtomicTypes(
        string tagName, string expectedDataType, string expectedNetTypeName)
    {
        var svc = await ConnectedServiceWithAllTypesAsync();

        var result = await svc.ReadAsync(tagName);

        Assert.Equal(expectedDataType,   result.DataType);
        Assert.Equal(expectedNetTypeName, result.Value.GetType().Name);
    }

    [Theory]
    [InlineData("BoolTag",  "BOOL")]
    [InlineData("SintTag",  "SINT")]
    [InlineData("IntTag",   "INT")]
    [InlineData("DintTag",  "DINT")]
    [InlineData("LintTag",  "LINT")]
    [InlineData("UsintTag", "USINT")]
    [InlineData("UintTag",  "UINT")]
    [InlineData("UdintTag", "UDINT")]
    [InlineData("UlintTag", "ULINT")]
    [InlineData("RealTag",  "REAL")]
    [InlineData("LrealTag", "LREAL")]
    public async Task ReadAsync_ReturnsCorrectDataTypeString_ForAllAtomicTypes(
        string tagName, string expectedDataType)
    {
        var svc = await ConnectedServiceWithAllTypesAsync();

        var result = await svc.ReadAsync(tagName);

        // DataType string must match the Logix type name exactly.
        Assert.Equal(expectedDataType, result.DataType);
    }

    [Theory]
    [InlineData("BoolTag")]
    [InlineData("SintTag")]
    [InlineData("IntTag")]
    [InlineData("DintTag")]
    [InlineData("LintTag")]
    [InlineData("UsintTag")]
    [InlineData("UintTag")]
    [InlineData("UdintTag")]
    [InlineData("UlintTag")]
    [InlineData("RealTag")]
    [InlineData("LrealTag")]
    public async Task ReadAsync_TagName_MatchesRequestedName_ForAllAtomicTypes(
        string tagName)
    {
        var svc = await ConnectedServiceWithAllTypesAsync();

        var result = await svc.ReadAsync(tagName);

        // TagValue.TagName must echo back the name supplied to ReadAsync.
        Assert.Equal(tagName, result.TagName);
    }

    // ── ReadAsync — UDT (complex) tag and dot-notation path ──────────────────
    //
    // Demo.L5X defines a MotorCmd UDT tag named "Cmd_Motor" with Enable (BOOL)
    // and Speed (REAL) members.
    //
    // L5XOfflineTagService uses a single TryGet call (without the dot-notation
    // fallback that TypedSdkTagService adds).  L5Sharp's TryGet resolves the
    // base tag "Cmd_Motor" correctly, but does not resolve the full member path
    // "Cmd_Motor.Enable" from the tag index — that path returns TagNotFound.
    // TypedSdkTagService adds an explicit member-navigation fallback for this
    // case; L5XOfflineTagService intentionally does not (it is an offline, L5X-
    // only adapter where UDT member access is expected to be done via the base
    // tag).

    [Fact]
    public async Task ReadAsync_ComplexUdtTag_ReturnsNonNullResult()
    {
        var svc = await ConnectedServiceAsync();

        // Reading the base UDT tag returns a string representation of the
        // complex value — not null — because ExtractValue falls through to the
        // default case for non-atomic types.
        var result = await svc.ReadAsync("Cmd_Motor");

        Assert.NotNull(result);
        Assert.Equal("Cmd_Motor", result.TagName);
    }

    [Fact]
    public async Task ReadAsync_ComplexUdtTag_DataTypeIsUdtName()
    {
        var svc = await ConnectedServiceAsync();

        var result = await svc.ReadAsync("Cmd_Motor");

        // Complex UDT tags report their declared DataType (the UDT name).
        Assert.Equal("MotorCmd", result.DataType);
    }

    [Fact]
    public async Task ReadAsync_UdtMemberDotNotation_ThrowsTagNotFound()
    {
        var svc = await ConnectedServiceAsync();

        // L5XOfflineTagService uses a single TryGet call; L5Sharp does not
        // resolve "Cmd_Motor.Enable" as a top-level tag entry, so the service
        // returns TagNotFound for dot-notation member paths.  Callers that need
        // member-level access should use TypedSdkTagService, which has an
        // explicit member-navigation fallback.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Cmd_Motor.Enable"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        Assert.Equal("Cmd_Motor.Enable", ex.Error.TagName);
    }

    // ── WriteAsync → ReadAsync round-trips ────────────────────────────────────
    //
    // Confirms that WriteAsync mutates the in-memory L5X project so a
    // subsequent ReadAsync on the same service instance returns the new value.

    [Fact]
    public async Task WriteAsync_ThenReadAsync_BoolTag_PersistsTrue()
    {
        var svc = await ConnectedServiceAsync();

        // Cmd_MotorStart is initialised to false in Demo.L5X.
        await svc.WriteAsync("Cmd_MotorStart", true);
        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(true, result.Value);
        Assert.Equal("BOOL", result.DataType);
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RealTag_PersistsValue()
    {
        var svc = await ConnectedServiceAsync();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 123.45f);
        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal("REAL", result.DataType);
        Assert.IsType<float>(result.Value);
        Assert.Equal(123.45f, (float)result.Value, tolerance: 0.001f);
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_BoolTag_CanToggleFalseAfterTrue()
    {
        var svc = await ConnectedServiceAsync();

        await svc.WriteAsync("Cmd_MotorStart", true);
        await svc.WriteAsync("Cmd_MotorStart", false);
        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(false, result.Value);
    }

    // ── ListTagsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTagsAsync_ReturnsNonNullList()
    {
        var svc = await ConnectedServiceAsync();

        var tags = await svc.ListTagsAsync();

        Assert.NotNull(tags);
    }

    [Fact]
    public async Task ListTagsAsync_ReturnsIReadOnlyList()
    {
        var svc = await ConnectedServiceAsync();

        var tags = await svc.ListTagsAsync();

        Assert.IsAssignableFrom<IReadOnlyList<TagInfo>>(tags);
    }

    [Fact]
    public async Task ListTagsAsync_ContainsDemoTags()
    {
        var svc = await ConnectedServiceAsync();

        var tags = await svc.ListTagsAsync();
        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Cmd_MotorStart",    names);
        Assert.Contains("Cmd_SpeedSetpoint", names);
        Assert.Contains("Sim_LineSpeed",     names);
        Assert.Contains("Safety_OK",         names);
        Assert.Contains("Motor_Run",         names);
    }

    [Fact]
    public async Task ListTagsAsync_TagsHaveDataType()
    {
        var svc = await ConnectedServiceAsync();

        var tags = await svc.ListTagsAsync();

        // All tags in Demo.L5X carry an explicit DataType.
        Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.DataType)));
    }

    [Fact]
    public async Task ListTagsAsync_TagsHaveName()
    {
        var svc = await ConnectedServiceAsync();

        var tags = await svc.ListTagsAsync();

        Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
    }

    // ── Extended type coverage ────────────────────────────────────────────────
    //
    // These tests verify that L5XOfflineTagService accepts the unsigned integer
    // family and additional types that LibPlcTagService rejects as TypeMismatch.
    // The write calls are expected to fail with TagNotFound (not TypeMismatch)
    // because Demo.L5X has no USINT / UINT / UDINT / ULINT / LREAL tags — but
    // the key assertion is that the error kind is TagNotFound, proving the type
    // was accepted and the look-up was attempted.

    [Theory]
    [InlineData((byte)255)]          // USINT
    [InlineData((ushort)65535)]      // UINT
    [InlineData((uint)4294967295u)]  // UDINT
    [InlineData((ulong)ulong.MaxValue)] // ULINT
    [InlineData((sbyte)-128)]        // SINT
    [InlineData((double)3.14)]       // LREAL (double)
    public async Task WriteAsync_ExtendedTypes_AreAccepted_NotRejectedAsTypeMismatch(
        object value)
    {
        var svc = await ConnectedServiceAsync();

        // The type should pass validation and fail only at tag look-up.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NonExistent_Extended_Tag", value));

        // Must be TagNotFound, not TypeMismatch — proving the type was accepted.
        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    [Theory]
    [InlineData(true)]           // bool → BOOL
    [InlineData((short)100)]     // short → INT
    [InlineData(42)]             // int → DINT
    [InlineData(100L)]           // long → LINT
    [InlineData(3.14f)]          // float → REAL
    public async Task WriteAsync_StandardSignedTypes_AreAccepted(object value)
    {
        var svc = await ConnectedServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NonExistent_Standard_Tag", value));

        // Must be TagNotFound, not TypeMismatch.
        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    // ── TagServiceException carries structured error ───────────────────────────

    [Fact]
    public async Task TagServiceException_ErrorProperty_IsPopulated()
    {
        var svc = new L5XOfflineTagService(LoadProject());   // not connected

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("AnyTag"));

        Assert.NotNull(ex.Error);
        Assert.NotEmpty(ex.Message);
    }

    // ── Out-of-range bits ──────────────────────────────────────────

    private const string BitRangeL5Xml = """
        <RSLogix5000Content SchemaRevision="1.0"><Controller Name="C" MajorRev="38" MinorRev="11" ProcessorType="1756-L83E">
        <DataTypes>
        <DataType Name="Bench_Rec_T" Family="NoFamily" Class="User"><Members>
        <Member Name="Level" DataType="DINT" Dimension="0" Radix="Decimal" Hidden="false" ExternalAccess="Read/Write"/>
        </Members></DataType>
        </DataTypes>
        <Modules>
        <Module Name="Local" CatalogNumber="1756-L83E" ParentModule="Local" ParentModPortId="1"><Ports>
        <Port Id="1" Address="0" Type="ICP" Upstream="false"><Bus Size="7"/></Port></Ports></Module>
        <Module Name="IB16D_S1" CatalogNumber="1756-IB16D" ParentModule="Local" ParentModPortId="1">
        <Ports><Port Id="1" Address="1" Type="ICP" Upstream="true"/></Ports>
        <Communications><Connections><Connection Name="Data" Type="Input"><InputTag ExternalAccess="Read/Write">
        <Data Format="Decorated"><Structure DataType="AB:1756_DI:I:0">
        <DataValueMember Name="Fault" DataType="DINT" Radix="Binary" Value="2#0000_0000_0000_0000_0000_0000_0000_0000"/>
        <DataValueMember Name="Data" DataType="DINT" Radix="Binary" Value="2#0000_0000_0000_0000_0000_0000_0000_1000"/>
        </Structure></Data></InputTag></Connection></Connections></Communications></Module>
        </Modules>
        <Tags>
        <Tag Name="Bench_Bits" TagType="Base" DataType="DINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write">
        <Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="5"/></Data></Tag>
        <Tag Name="Bench_Lint" TagType="Base" DataType="LINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write">
        <Data Format="Decorated"><DataValue DataType="LINT" Radix="Decimal" Value="0"/></Data></Tag>
        <Tag Name="Bench_Rec" TagType="Base" DataType="Bench_Rec_T" Constant="false" ExternalAccess="Read/Write">
        <Data Format="Decorated"><Structure DataType="Bench_Rec_T">
        <DataValueMember Name="Level" DataType="DINT" Radix="Decimal" Value="0"/></Structure></Data></Tag>
        </Tags>
        <Programs/></Controller></RSLogix5000Content>
        """;

    private static async Task<L5XOfflineTagService> ConnectedBitRangeServiceAsync()
    {
        var svc = new L5XOfflineTagService(L5X.Parse(BitRangeL5Xml));
        await svc.ConnectAsync("unused-gateway", "1,0");
        return svc;
    }

    [Fact]
    public async Task OutOfRangeBit_OnDint_OnL5x_ReturnsBitOutOfRange_WithRange()
    {
        var svc = await ConnectedBitRangeServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Bits.32"));

        Assert.Equal(TagServiceErrorKind.BitOutOfRange, ex.Error.Kind);
        Assert.Contains("bits 0 to 31", ex.Error.Message);
        // The last valid bit still reads.
        Assert.Equal(false, (await svc.ReadAsync("Bench_Bits.31")).Value);
    }

    [Fact]
    public async Task OutOfRangeBit_OnLintAndUdtMember_OnL5x_ReturnsBitOutOfRange()
    {
        var svc = await ConnectedBitRangeServiceAsync();

        var lint = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Lint.64"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, lint.Error.Kind);
        Assert.Contains("bits 0 to 63", lint.Error.Message);

        var member = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Rec.Level.32"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, member.Error.Kind);
        Assert.Contains("bits 0 to 31", member.Error.Message);
    }

    [Fact]
    public async Task OutOfRangeBit_OnModuleIo_OnL5x_ReturnsBitOutOfRange()
    {
        var svc = await ConnectedBitRangeServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Local:1:I.Data.32"));

        Assert.Equal(TagServiceErrorKind.BitOutOfRange, ex.Error.Kind);
        Assert.Contains("bits 0 to 31", ex.Error.Message);
    }

    [Fact]
    public async Task BitOfUnknownTag_OnL5x_StaysTagNotFound()
    {
        var svc = await ConnectedBitRangeServiceAsync();

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("No_Such_Tag.40"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }
}
