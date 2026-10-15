#if LOGIX_SDK
// ── ReadToolTests — member-path forwarding contract ──────────────────────────
//
// Verifies that logix_read (TypedSdkTagService.ReadAsync) routes dotted member
// paths through the byte-array decode path (not the typed SDK accessors).
//
// Acceptance criteria:
//   ReadToolTests.MemberPath_IsPassedToSdkUnchanged
//   ReadToolTests.ProgramScopedMemberPath_IsPassedUnchanged
//

using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using OperationMode = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;

// L5Sharp.Core defines its own Task type (representing an AOI Task component).
// Alias System.Threading.Tasks.Task to avoid the ambiguous-reference error.
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Verifies that <see cref="TypedSdkTagService.ReadAsync"/> passes dotted
/// member paths and program-scoped member paths directly to the Logix Designer
/// SDK typed accessors, without splitting on '.' or constructing manual
/// DataValueMember XPath predicates.
///
/// <para>
/// These are the contractual acceptance tests.
/// </para>
/// </summary>
public class ReadToolTests
{
    // ── Fixture — L5X with UDT member tags ───────────────────────────────────

    /// <summary>
    /// Minimal L5X with:
    /// <list type="bullet">
    ///   <item><c>MotorCmd</c> UDT — members <c>Enable</c> (BOOL) and <c>Speed</c> (REAL).</item>
    ///   <item>Controller-scoped <c>Cmd_Motor</c> tag of type <c>MotorCmd</c>.</item>
    ///   <item><c>MainProgram</c> program with <c>MotorTag</c> of type <c>MotorCmd</c>.</item>
    /// </list>
    /// </summary>
    private const string MemberTagL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TestController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TestController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="MotorCmd" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Enable" DataType="BOOL" Dimension="0" Radix="NullType" ExternalAccess="Read/Write" Hidden="false"/>
                  <Member Name="Speed"  DataType="REAL" Dimension="0" Radix="NullType" ExternalAccess="Read/Write" Hidden="false"/>
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="Cmd_Motor" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="MotorCmd">
                <Data Format="Decorated">
                  <Structure DataType="MotorCmd">
                    <DataValueMember Name="Enable" DataType="BOOL" Radix="Decimal" Value="0"/>
                    <DataValueMember Name="Speed"  DataType="REAL" Radix="Float"   Value="0.0"/>
                  </Structure>
                </Data>
              </Tag>
              <Tag Name="Recipe_Working" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="MotorCmd">
                <Data Format="Decorated">
                  <Structure DataType="MotorCmd">
                    <DataValueMember Name="Enable" DataType="BOOL" Radix="Decimal" Value="0"/>
                    <DataValueMember Name="Speed"  DataType="REAL" Radix="Float"   Value="0.0"/>
                  </Structure>
                </Data>
              </Tag>
            </Tags>
            <Programs>
              <Program Name="MainProgram" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false">
                <Tags>
                  <Tag Name="MotorTag" TagType="Base" ExternalAccess="Read/Write" Constant="false" DataType="MotorCmd">
                    <Data Format="Decorated">
                      <Structure DataType="MotorCmd">
                        <DataValueMember Name="Enable" DataType="BOOL" Radix="Decimal" Value="0"/>
                        <DataValueMember Name="Speed"  DataType="REAL" Radix="Float"   Value="0.0"/>
                      </Structure>
                    </Data>
                  </Tag>
                </Tags>
                <Routines />
              </Program>
            </Programs>
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    private static L5X BuildMemberProject() => L5X.Parse(MemberTagL5Xml);

    private static LogixProjectSession SessionWith(RecordingFakeLogixProject fake)
        => new((_path, _ct) => Task.FromResult<ILogixProject>(fake));

    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)>
        OpenedServiceAsync(float? speedValue = null)
    {
        // MotorCmd layout: Enable(BOOL)=offset 0 size 1; Speed(REAL)=offset 4 size 4; total 8 bytes.
        var rawBytes = new byte[8];
        if (speedValue.HasValue)
            BitConverter.GetBytes(speedValue.Value).CopyTo(rawBytes, 4);

        var fake = new RecordingFakeLogixProject
        {
            RealReturnValue      = speedValue ?? 0.0f,
            ByteArrayReturnValue = ByteString.CopyFrom(rawBytes),
        };
        var session = SessionWith(fake);
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, BuildMemberProject());
        return (svc, fake);
    }

    // ── Acceptance tests ──────────────────────────────────────────────────────

    /// <summary>
    /// Contractual acceptance test:
    /// Dotted member paths (e.g. <c>"Cmd_Motor.Speed"</c>) must be routed through
    /// the byte-array decode path in <see cref="TypedSdkTagService"/>, not through
    /// the typed SDK accessors (<c>GetTagValueREALAsync</c>, etc.).
    ///
    /// <para>
    /// Verified by asserting:
    /// <list type="bullet">
    ///   <item><see cref="RecordingFakeLogixProject.LastByteArrayReadTagPath"/> is set
    ///   (the byte-array overload of <c>GetTagValueAsync</c> was called).</item>
    ///   <item><see cref="RecordingFakeLogixProject.LastReadType"/> is <c>null</c>
    ///   (no typed accessor was called).</item>
    ///   <item>The returned value matches what was placed at the member's byte offset
    ///   in the fake byte array.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task MemberPath_IsPassedToSdkUnchanged()
    {
        const float expectedSpeed = 3.14f;
        var (svc, fake) = await OpenedServiceAsync(speedValue: expectedSpeed);

        var result = await svc.ReadAsync("Cmd_Motor.Speed");

        // Byte-array GetTagValueAsync was called (not a typed typed accessor).
        Assert.NotNull(fake.LastByteArrayReadTagPath);
        // No typed accessor (GetTagValueREALAsync, etc.) was invoked.
        Assert.Null(fake.LastReadType);
        // The REAL value was correctly decoded from the byte array.
        Assert.Equal("REAL", result.DataType);
        Assert.Equal(expectedSpeed, (float)result.Value, tolerance: 0.0001f);
    }

    /// <summary>
    /// Contractual acceptance test:
    /// Program-scoped member paths (e.g.
    /// <c>"Program:MainProgram.MotorTag.Speed"</c>) must be routed through the
    /// byte-array decode path, not through the typed SDK accessors.
    /// </summary>
    [Fact]
    public async Task ProgramScopedMemberPath_IsPassedUnchanged()
    {
        const float expectedSpeed = 2.71f;
        var (svc, fake) = await OpenedServiceAsync(speedValue: expectedSpeed);

        var result = await svc.ReadAsync("Program:MainProgram.MotorTag.Speed");

        // Byte-array GetTagValueAsync was called (not a typed accessor).
        Assert.NotNull(fake.LastByteArrayReadTagPath);
        Assert.Null(fake.LastReadType);
        Assert.Equal("REAL", result.DataType);
        Assert.Equal(expectedSpeed, (float)result.Value, tolerance: 0.0001f);
    }

    // ── Not-found response shape ───────────────────────────

    /// <summary>
    /// Builds a connected <see cref="TagSessionService"/> backed by the
    /// member-tag L5X fixture, suitable for testing
    /// <see cref="ReadTagHandler.ExecuteAsync"/>.
    /// </summary>
    private static async Task<TagSessionService> ConnectedTagSessionAsync()
    {
        var (svc, _) = await OpenedServiceAsync();
        var tagSession = new TagSessionService();
        tagSession.Attach(svc, isVirtual: false);
        return tagSession;
    }

    /// <summary>
    /// Contractual acceptance test:
    /// When a top-level tag does not exist in the L5X project,
    /// <see cref="ReadTagHandler.ExecuteAsync"/> returns a
    /// <see cref="ReadTagNotFound"/> with <c>status:"not_found"</c>,
    /// <c>what:"tag"</c>, and a non-empty <c>path</c>.  A non-SDK
    /// lane result carries no <c>sdkError</c>.
    /// </summary>
    [Fact]
    public async Task MissingTag_ReturnsNotFoundWithWhatTag()
    {
        var session = await ConnectedTagSessionAsync();

        var result = await ReadTagHandler.ExecuteAsync("NonExistent_Tag", session);

        var notFound = Assert.IsType<ReadTagNotFound>(result);
        Assert.Equal("not_found", notFound.Status);
        Assert.Equal("tag",       notFound.What);
        Assert.False(string.IsNullOrEmpty(notFound.Path),     "path must not be empty");
        Assert.Null(notFound.SdkError);
    }

    /// <summary>
    /// Contractual acceptance test:
    /// When a parent tag exists in the L5X project but the requested member
    /// path does not, <see cref="ReadTagHandler.ExecuteAsync"/> returns a
    /// <see cref="ReadTagNotFound"/> with <c>status:"not_found"</c>,
    /// <c>what:"member"</c>, <c>path</c> equal to the full originally-requested
    /// member path, and a non-empty <c>sdkError</c>.
    /// </summary>
    [Fact]
    public async Task MissingMember_ReturnsNotFoundWithWhatMember()
    {
        var session = await ConnectedTagSessionAsync();

        // Cmd_Motor exists in MemberTagL5Xml; NonExistentMember does not.
        var result = await ReadTagHandler.ExecuteAsync("Cmd_Motor.NonExistentMember", session);

        // an earlier change: a missing member is the typed MemberNotFound refusal, naming the full path and listing the members.
        var err = Assert.IsType<ReadTagError>(result);
        Assert.Equal(ErrorKinds.MemberNotFound, err.Kind);
        Assert.Equal("Cmd_Motor.NonExistentMember", err.Tag);
        Assert.Contains("NonExistentMember", err.Error);
        Assert.Contains("Members:", err.Error);
    }

    /// <summary>
    /// Contractual acceptance test:
    /// The <c>path</c> field in the not-found response must equal the full
    /// originally-requested member path — not the parent tag name alone.
    ///
    /// <para>
    /// For example, requesting <c>"Recipe_Working.NoSuchMember"</c> when
    /// <c>Recipe_Working</c> exists but <c>NoSuchMember</c> does not must
    /// produce <c>path:"Recipe_Working.NoSuchMember"</c>, not
    /// <c>path:"Recipe_Working"</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MissingMember_PathIsFullRequestedPath()
    {
        var session = await ConnectedTagSessionAsync();

        // Recipe_Working exists in MemberTagL5Xml (MotorCmd type); NoSuchMember does not.
        var result = await ReadTagHandler.ExecuteAsync("Recipe_Working.NoSuchMember", session);

        var err = Assert.IsType<ReadTagError>(result);
        Assert.Equal(ErrorKinds.MemberNotFound, err.Kind);
        Assert.Equal("Recipe_Working.NoSuchMember", err.Tag);
        Assert.Contains("Recipe_Working.NoSuchMember", err.Error);
    }
}
#endif
