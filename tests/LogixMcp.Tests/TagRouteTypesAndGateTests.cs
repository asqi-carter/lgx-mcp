using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The CIP route resolves types from the SDK session's own lookup, every write goes
/// through the same gate policy object on both routes, and a publish that creates tags refreshes the lookup
/// so a read of a just-created tag is typed, not <c>TypeUnknown</c>.
/// </summary>
public class TagRouteTypesAndGateTests
{
    private static readonly Capabilities LiveWritesOn = new(LiveWritesEnabled: true);

    private const string ProjectXml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>
              <DataType Name="Drive_UDT" Family="NoFamily" Class="User">
                <Members>
                  <Member Name="Speed" DataType="REAL" Dimension="0" Radix="Float" ExternalAccess="Read/Write" Hidden="false" />
                  <Member Name="Count" DataType="DINT" Dimension="0" Radix="Decimal" ExternalAccess="Read/Write" Hidden="false" />
                </Members>
              </DataType>
            </DataTypes>
            <Modules />
            <AddOnInstructionDefinitions>
              <AddOnInstructionDefinition Name="MoveAxis" Class="Standard" Revision="1.0" Vendor="" ExecutePrescan="false" ExecutePostscan="false" ExecuteEnableInFalse="false" CreatedDate="2024-01-01T00:00:00.000Z" CreatedBy="t" EditedDate="2024-01-01T00:00:00.000Z" EditedBy="t" SoftwareRevision="v36.00">
                <Parameters>
                  <Parameter Name="Speed" TagType="Base" DataType="REAL" Usage="Input" Radix="Float" Required="true" Visible="true" ExternalAccess="Read/Write" />
                </Parameters>
                <LocalTags />
                <Routines>
                  <Routine Name="Logic" Type="RLL">
                    <RLLContent>
                      <Rung Number="0" Type="N"><Text><![CDATA[MAS(Axis1,MasMotion,0,Units per sec2,100,Units per sec2,0,0,% of Time,0,0,% of Time,0,0);]]></Text></Rung>
                    </RLLContent>
                  </Routine>
                </Routines>
              </AddOnInstructionDefinition>
            </AddOnInstructionDefinitions>
            <Tags>
              <Tag Name="Speed" TagType="Base" DataType="DINT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write">
                <Data Format="L5K"><![CDATA[0]]></Data>
              </Tag>
              <Tag Name="Drive" TagType="Base" DataType="Drive_UDT" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Run_Timer" TagType="Base" DataType="TIMER" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="Axis" TagType="Base" DataType="MoveAxis" Constant="false" ExternalAccess="Read/Write" />
              <Tag Name="History" TagType="Base" DataType="DINT" Dimensions="4" Radix="Decimal" Constant="false" ExternalAccess="Read/Write" />
            </Tags>
            <Programs>
              <Program Name="Main" TestEdits="false" MainRoutineName="MainRoutine" Disabled="false" UseAsFolder="false">
                <Tags>
                  <Tag Name="Prog_Speed" TagType="Base" DataType="REAL" Radix="Float" Constant="false" ExternalAccess="Read/Write" />
                </Tags>
                <Routines />
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>What a partial re-export of the controller returns for a tag a publish created.</summary>
    private static Tag ExportedTag(string name, string dataType)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes /><Modules /><AddOnInstructionDefinitions />
                <Tags>
                  <Tag Name="{name}" TagType="Base" DataType="{dataType}" Radix="Decimal" Constant="false" ExternalAccess="Read Only">
                    <Data Format="L5K"><![CDATA[0]]></Data>
                  </Tag>
                </Tags>
                <Programs /><Tasks />
              </Controller>
            </RSLogix5000Content>
            """;
        return L5X.Parse(xml).Tags.First();
    }

    /// <summary>
    /// Stands for the SDK service of a session: the type lookup both routes share, with the controller's
    /// tags a partial re-export can return. Counts the re-exports.
    /// </summary>
    private sealed class SdkSide : ITagService, ISessionTypeSource
    {
        private readonly L5X _project = L5X.Parse(ProjectXml);
        private readonly object _lock = new();
        private readonly Dictionary<string, Tag> _controller = new(StringComparer.OrdinalIgnoreCase);
        private readonly VirtualTagService _values = new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Start"] = (false, "BOOL"),
            ["Motor_Run"] = (false, "BOOL"),
            ["Speed"]     = (5, "DINT"),
        });

        public List<string> Exports { get; } = new();

        /// <summary>The controller gains a tag (a publish created it); the lookup does not know yet.</summary>
        public void ControllerCreates(string name, string dataType) => _controller[name] = ExportedTag(name, dataType);

        public TResult Lookup<TResult>(Func<L5X, TResult> lookup)
        {
            lock (_lock) return lookup(_project);
        }

        public Task<bool> RefreshAsync(string tagPath, CancellationToken cancellationToken = default)
        {
            var name = tagPath.Split('.', '[')[0];
            lock (_lock)
            {
                if (_project.Tags.Any(t => string.Equals(t.Name.ToString(), name, StringComparison.OrdinalIgnoreCase)))
                    return Task.FromResult(true);
                Exports.Add(name);
                if (!_controller.TryGetValue(name, out var tag)) return Task.FromResult(false);
                _project.Tags.Add(tag);
                return Task.FromResult(true);
            }
        }

        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default) => _values.ReadAsync(t, ct);
        public Task WriteAsync(string t, object v, CancellationToken ct = default) => _values.WriteAsync(t, v, ct);
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => _values.ListTagsAsync(ct);
    }

    private sealed class Buffer(params byte[] bytes) : LibPlcTagService.ITagBuffer
    {
        public int GetSize() => bytes.Length;
        public bool GetBit(int o) => (bytes[o / 8] & (1 << (o % 8))) != 0;
        public sbyte GetInt8(int o) => (sbyte)bytes[o];
        public byte GetUInt8(int o) => bytes[o];
        public short GetInt16(int o) => BitConverter.ToInt16(bytes, o);
        public ushort GetUInt16(int o) => BitConverter.ToUInt16(bytes, o);
        public int GetInt32(int o) => BitConverter.ToInt32(bytes, o);
        public uint GetUInt32(int o) => BitConverter.ToUInt32(bytes, o);
        public long GetInt64(int o) => BitConverter.ToInt64(bytes, o);
        public ulong GetUInt64(int o) => BitConverter.ToUInt64(bytes, o);
        public float GetFloat32(int o) => BitConverter.ToSingle(bytes, o);
        public double GetFloat64(int o) => BitConverter.ToDouble(bytes, o);
    }

    /// <summary>
    /// A CIP channel that types a read the way <see cref="LibPlcTagService"/> does: the resolver decides the
    /// type, <see cref="LibPlcTagService.DecodeValue"/> decodes the bytes (DINT 7), and an unknown type is
    /// <c>TypeUnknown</c>. Writes land in a log.
    /// </summary>
    private sealed class CipChannel(CipTypeResolver resolver) : ITagService
    {
        public List<string> Writes { get; } = new();
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async Task<TagValue> ReadAsync(string t, CancellationToken ct = default)
            => LibPlcTagService.DecodeValue(t, await resolver.ResolveAsync(t, ct), new Buffer(7, 0, 0, 0));
        public Task WriteAsync(string t, object v, CancellationToken ct = default) { Writes.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TagInfo>>(Array.Empty<TagInfo>());
    }

    private static WriteGatePolicy Policy() => new(
        Grants:
        [
            new GrantEntry("Cmd_Start", null, null, "command prefix 'Cmd_'"),
            new GrantEntry("Speed",     null, null, "granted for test"),
        ],
        Denies:
        [
            new DenyEntry("Motor_Run", DenyReason.ProgramWritten, "Tag 'Motor_Run' is written by program logic at MainRoutine rung 1."),
        ]);

    /// <summary>A session built the way an SDK connect builds it: one gate over the router over the SDK service.</summary>
    private static (TagSessionService Session, RoutedTagService Router, SdkSide Sdk, CipChannel Cip) SessionOf(TagRouteMode mode)
    {
        var sdk     = new SdkSide();
        var cip     = new CipChannel(CipTypeResolver.ForSession(sdk, sdk.ListTagsAsync));
        var router  = new RoutedTagService(
            sdk, mode, mode == TagRouteMode.Sdk ? null : _ => Task.FromResult<ITagService>(cip));
        var session = new TagSessionService();
        session.Attach(new GatedTagService(router, Policy()), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        return (session, router, sdk, cip);
    }

    private static string Describe(object result) => result switch
    {
        WriteTagResult       => "written",
        WriteTagError e      => $"refused:{e.Kind}",
        _                    => result.GetType().Name,
    };

    // ── The gate: one policy, both routes ─────────────────────────────────────

    [Fact]
    public async Task Gate_GivesSameDecision_ForSameTag_OnBothRoutes()
    {
        string[] tags = ["Cmd_Start", "Speed", "Motor_Run", "Not_In_Policy", "Missing_Tag"];

        var viaSdk = SessionOf(TagRouteMode.Sdk);
        var viaCip = SessionOf(TagRouteMode.Cip);

        foreach (var tag in tags)
        {
            var onSdk = Describe(await WriteTagHandler.ExecuteAsync(tag, "1", viaSdk.Session, capabilities: LiveWritesOn));
            var onCip = Describe(await WriteTagHandler.ExecuteAsync(tag, "1", viaCip.Session, capabilities: LiveWritesOn));
            Assert.True(onSdk == onCip, $"'{tag}': the SDK route gave '{onSdk}' but the CIP route gave '{onCip}'");
        }

        // The same policy object governs both sessions' gates, and it let through and refused the same tags.
        Assert.Equal(TagRoutes.Sdk, viaSdk.Router.ActiveRoute);
        Assert.Equal(TagRoutes.Cip, viaCip.Router.ActiveRoute);
        Assert.Contains("Cmd_Start", viaCip.Cip.Writes);
        Assert.DoesNotContain("Motor_Run", viaCip.Cip.Writes);
        Assert.DoesNotContain("Not_In_Policy", viaCip.Cip.Writes);

        var allowedOnSdk = Describe(await WriteTagHandler.ExecuteAsync("Cmd_Start", "1", viaSdk.Session, capabilities: LiveWritesOn));
        Assert.Equal("written", allowedOnSdk);
        Assert.StartsWith("refused:", Describe(await WriteTagHandler.ExecuteAsync("Motor_Run", "1", viaCip.Session, capabilities: LiveWritesOn)));
    }

    // ── Types: the SDK session's lookup, no L5Sharp index ─────────────────────

    [Theory]
    [InlineData("Speed",                  "DINT")]   // atomic
    [InlineData("Drive.Speed",            "REAL")]   // UDT member
    [InlineData("Drive.Count",            "DINT")]
    [InlineData("History[2]",             "DINT")]   // array element
    [InlineData("Program:Main.Prog_Speed", "REAL")]  // program-scoped
    [InlineData("Run_Timer.ACC",          "DINT")]   // predefined type member, as on the SDK route
    [InlineData("Run_Timer.DN",           "BOOL")]
    [InlineData("Axis.Speed",             "REAL")]   // AOI parameter, as on the SDK route
    public async Task CipTypeResolution_DoesNotUseL5SharpIndex(string path, string expected)
    {
        // The project holds an AOI rung ("Units per sec2") that L5Sharp's whole-project index cannot tokenise.
        // Types are still resolved, from the project's own collections.
        var sdk      = new SdkSide();
        var resolver = CipTypeResolver.ForSession(sdk, sdk.ListTagsAsync);

        // The fixture really is one the index cannot build over.
        Assert.ThrowsAny<Exception>(() => L5X.Parse(ProjectXml).Tags.First(t => t.Name.ToString() == "Speed").Description);

        var resolved = await resolver.ResolveAsync(path);

        Assert.True(resolved.IsResolved, resolved.Reason);
        Assert.Equal(expected, resolved.TypeName);
        Assert.Equal(CipTypeSource.Project, resolved.Source);
        Assert.Empty(sdk.Exports);
    }

    [Fact]
    public async Task CipTypeResolution_SharesTheSdkSessionsLookup_NotACopy()
    {
        var sdk      = new SdkSide();
        var resolver = CipTypeResolver.ForSession(sdk, sdk.ListTagsAsync);
        sdk.ControllerCreates("Early_Tag", "DINT");

        // The SDK side learns the tag (its own type-miss refresh); the CIP resolver sees it with no export of its own.
        Assert.True(await sdk.RefreshAsync("Early_Tag"));
        var resolved = await resolver.ResolveAsync("Early_Tag");

        Assert.True(resolved.IsResolved, resolved.Reason);
        Assert.Equal("DINT", resolved.TypeName);
        Assert.Equal(["Early_Tag"], sdk.Exports);
    }

    // ── Type refresh after tag creation ───────────────────────────────────────

    [Fact]
    public async Task TypeRefresh_AfterTagCreation_ResolvesTheNewTag_WithOneReExport()
    {
        var sdk      = new SdkSide();
        var resolver = CipTypeResolver.ForSession(sdk, sdk.ListTagsAsync);
        sdk.ControllerCreates("Created_Dint", "DINT");

        var first  = await resolver.ResolveAsync("Created_Dint");
        var second = await resolver.ResolveAsync("Created_Dint");

        Assert.True(first.IsResolved, first.Reason);
        Assert.Equal("DINT", first.TypeName);
        Assert.Equal(CipTypeSource.Project, first.Source);
        Assert.True(second.IsResolved);
        Assert.Equal(["Created_Dint"], sdk.Exports);   // the second resolve is a lookup hit
    }

    [Fact]
    public async Task TypeRefresh_AfterPublish_RefreshesTheLookup_BeforeAnyRead()
    {
        var (session, _, sdk, _) = SessionOf(TagRouteMode.Cip);
        sdk.ControllerCreates("Cmd_New", "DINT");
        sdk.ControllerCreates("Other_New", "REAL");

        // What logix_propose_rung_edit does after a publish that created tags.
        var known = await session.RefreshTypesForCreatedTagsAsync(["Cmd_New", "Other_New", "Cmd_New", "Never_Created"]);

        Assert.Equal(["Cmd_New", "Other_New"], known);
        Assert.Equal(["Cmd_New", "Other_New", "Never_Created"], sdk.Exports);   // one partial re-export per tag
        var resolver = CipTypeResolver.ForSession(sdk, sdk.ListTagsAsync);
        Assert.Equal("REAL", (await resolver.ResolveAsync("Other_New")).TypeName);
        Assert.Equal(3, sdk.Exports.Count);                                      // reads add no export
    }

    [Fact]
    public async Task TypeRefresh_WithNoSessionLookup_DoesNothing()
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: true, isLiveController: false);

        Assert.Empty(await session.RefreshTypesForCreatedTagsAsync(["Anything"]));
    }

    [Fact]
    public async Task Read_OfJustCreatedTag_DoesNotReturnTypeUnknown()
    {
        var (session, router, sdk, _) = SessionOf(TagRouteMode.Cip);
        sdk.ControllerCreates("Created_Dint", "DINT");

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Created_Dint", session));

        Assert.Equal(TagRoutes.Cip, router.ActiveRoute);
        Assert.Equal(7, read.Value);
        Assert.Equal("DINT", read.DataType);
    }

    [Fact]
    public async Task Read_OfJustCreatedTag_AfterPublishRefresh_DoesNotReturnTypeUnknown()
    {
        var (session, _, sdk, _) = SessionOf(TagRouteMode.Auto);
        sdk.ControllerCreates("Created_Dint", "DINT");
        await session.RefreshTypesForCreatedTagsAsync(["Created_Dint"]);

        var read = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync("Created_Dint", session));

        Assert.Equal(7, read.Value);
        Assert.Equal(["Created_Dint"], sdk.Exports);
    }

    [Fact]
    public async Task Read_OfTagNobodyDeclares_StaysTypeUnknown_NeverGuessedFromSize()
    {
        var (session, _, sdk, _) = SessionOf(TagRouteMode.Cip);

        var err = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync("Ghost_Tag", session));

        Assert.Equal(nameof(TagServiceErrorKind.TypeUnknown), err.Kind);
        Assert.Equal(["Ghost_Tag"], sdk.Exports);   // asked the SDK session once, found nothing
    }
}
