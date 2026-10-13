using L5Sharp.Core;
#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
#endif
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The shared <see cref="AliasResolver"/> behaves the same on every tag backend: CIP (through the routed fake CIP
/// service and <see cref="BuildFileGate"/>-gated writes), SDK offline, and L5X. An alias read returns the base's
/// value and real type with <c>aliasFor</c> / <c>resolvedFrom</c>; a cycle, a missing target or a chain deeper than 4
/// gives <c>AliasUnresolved</c>; a module I/O alias resolves through the module types.
/// </summary>
public class AliasSharedResolverTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"aliasshared-{Guid.NewGuid():N}");

    public AliasSharedResolverTests()
    {
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private const string ModuleIoFixture = "fixtures/real-shape/ModuleSnippets.L5X";

    private static string Dint(string name, int value) =>
        $"""<Tag Name="{name}" TagType="Base" DataType="DINT" Radix="Decimal" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[{value}]]></Data><Data Format="Decorated"><DataValue DataType="DINT" Radix="Decimal" Value="{value}"/></Data></Tag>""";

    private static string Real(string name, float value) =>
        $"""<Tag Name="{name}" TagType="Base" DataType="REAL" Radix="Float" ExternalAccess="Read/Write"><Data Format="L5K"><![CDATA[{value}]]></Data><Data Format="Decorated"><DataValue DataType="REAL" Radix="Float" Value="{value}"/></Data></Tag>""";

    private static string Alias(string name, string target) =>
        $"""<Tag Name="{name}" TagType="Alias" AliasFor="{target}" Radix="Decimal" ExternalAccess="Read/Write" />""";

    private const string Udt = """
        <DataType Name="Profile_UDT" Family="NoFamily" Class="User"><Members>
          <Member Name="Mandrel" DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write" />
        </Members></DataType>
        """;

    private static L5X Project() => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>{Udt}</DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              {Dint("Cmd_Speed", 36)}{Real("Cmd_BenchSetpoint", 2.5f)}
              <Tag Name="_ChangeProfile" TagType="Base" DataType="Profile_UDT" ExternalAccess="Read/Write"><Data Format="Decorated"><Structure DataType="Profile_UDT"><DataValueMember Name="Mandrel" DataType="REAL" Radix="Float" Value="12.5"/></Structure></Data></Tag>
              {Alias("Alias_ToCmd", "Cmd_BenchSetpoint")}
              {Alias("SpeedAlias", "Cmd_Speed")}{Alias("MandrelAlias", "_ChangeProfile.Mandrel")}{Alias("BitAlias", "Cmd_Speed.5")}
              {Alias("ChainA", "ChainB")}{Alias("ChainB", "SpeedAlias")}
              {Alias("CycleA", "CycleB")}{Alias("CycleB", "CycleA")}{Alias("Ghost", "No_Such_Tag")}
              {Alias("Deep1", "Deep2")}{Alias("Deep2", "Deep3")}{Alias("Deep3", "Deep4")}{Alias("Deep4", "Deep5")}{Alias("Deep5", "Cmd_Speed")}
            </Tags>
            <Programs>
              <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                <Tags>{Dint("Local_Count", 7)}{Alias("Count", "Local_Count")}</Tags>
                <Routines><Routine Name="R" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private static L5X ModuleProject() => L5X.Load(Path.Combine(AppContext.BaseDirectory, ModuleIoFixture));

    /// <summary>A fake CIP/SDK route: serves reads by path, remembers them, can refuse chosen paths.</summary>
    private sealed class FakeRoute(ISessionTypeSource? types = null, params string[] refuse) : ITagService, ISessionTypeSource, IAliasAware
    {
        private readonly HashSet<string> _refuse = new(refuse, StringComparer.OrdinalIgnoreCase);
        public List<string> Reads  { get; } = new();
        public List<string> Writes { get; } = new();
        public Dictionary<string, TagValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Alias_ToCmd"]       = new("Alias_ToCmd", 2.5f, "REAL"),
            ["Cmd_BenchSetpoint"] = new("Cmd_BenchSetpoint", 2.5f, "REAL"),
            ["Cmd_Speed"]         = new("Cmd_Speed", 36, "DINT"),
        };
        public AliasResolver? Aliases { get; init; }
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default)
        {
            Reads.Add(t);
            if (!_refuse.Contains(t) && Values.TryGetValue(t, out var v)) return Task.FromResult(v);
            throw new TagServiceException(new TagServiceError(TagServiceErrorKind.TagNotFound, $"'{t}' not found", t));
        }
        public Task WriteAsync(string t, object v, CancellationToken ct = default) { Writes.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public TResult Lookup<TResult>(Func<L5X, TResult> lookup) => types!.Lookup(lookup);
        public Task<bool> RefreshAsync(string p, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static async Task<TagServiceException> ErrorOf(Func<Task> act) => await Assert.ThrowsAsync<TagServiceException>(async () => await act());

    // ── CIP ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AliasRead_ThroughFakeCipService_ReturnsResolvedFromAndAliasFor()
    {
        var types = new StaticTypeSource(Project());
        var sdk = new FakeRoute(types);
        var cip = new FakeRoute(types);
        var router = new RoutedTagService(sdk, TagRouteMode.Cip, _ => Task.FromResult<ITagService>(cip));

        var v = await router.ReadAsync("Alias_ToCmd");

        Assert.Equal("Alias_ToCmd", v.TagName);
        Assert.Equal("Cmd_BenchSetpoint", v.AliasFor);
        Assert.Equal("Cmd_BenchSetpoint", v.ResolvedFrom);
        Assert.Equal("REAL", v.DataType);
        Assert.Equal(2.5f, v.Value);
        Assert.Equal(["Alias_ToCmd"], cip.Reads);   // the controller resolves aliases by name
        Assert.Empty(sdk.Reads);
    }

    [Fact]
    public async Task AliasRead_OnCip_FallsBackToBasePath_WhenAliasNameIsRefused()
    {
        var types = new StaticTypeSource(Project());
        var cip = new FakeRoute(types, refuse: "Alias_ToCmd");
        var router = new RoutedTagService(new FakeRoute(types), TagRouteMode.Cip, _ => Task.FromResult<ITagService>(cip));

        var v = await router.ReadAsync("Alias_ToCmd");

        Assert.Equal(["Alias_ToCmd", "Cmd_BenchSetpoint"], cip.Reads);
        Assert.Equal("Alias_ToCmd", v.TagName);
        Assert.Equal("Cmd_BenchSetpoint", v.ResolvedFrom);
        Assert.Equal(2.5f, v.Value);
    }

    [Fact]
    public async Task AliasWriteGate_ResolvesToBase_OnCip()
    {
        var cip = new FakeRoute { Aliases = new AliasResolver(Project()) };
        var policy = new WriteGatePolicy(
            [new GrantEntry("Cmd_BenchSetpoint", null, null, "test")], []);
        var gated = new GatedTagService(cip, policy);   // alias source comes from the CIP service

        await gated.WriteAsync("Alias_ToCmd", 3.0f);

        Assert.Equal(["Cmd_BenchSetpoint"], cip.Writes);
    }

    [Fact]
    public async Task AliasWriteGate_BuildFileGate_ResolvesToBase_ForEveryInnerBackend()
    {
        // BuildFileGate supplies the open file as the alias source even when the inner service has none.
        var project = new ProjectService();
        var tmp = Path.Combine(_tempDir, "p.L5X");
        Project().Save(tmp);
        project.Load(tmp);
        var inner = new FakeRoute();   // no resolver of its own
        var gate = ConnectHandler.BuildFileGate(inner, project, allowlistSha256: null);

        var result = await Record.ExceptionAsync(() => gate.Service.WriteAsync("Alias_ToCmd", 3.0f));

        // Either written as the base, or refused naming the base: never judged or written as the alias.
        Assert.DoesNotContain("Alias_ToCmd", inner.Writes);
        if (result is TagServiceException tse)
            Assert.Contains("Cmd_BenchSetpoint", tse.Error.Message);
        else
            Assert.Equal(["Cmd_BenchSetpoint"], inner.Writes);
    }

    [Fact]
    public async Task ModuleIoAlias_ResolvesThroughModuleTypes_AndWriteGateStaysReadOnly()
    {
        var resolver = new AliasResolver(ModuleProject());

        var r = resolver.Resolve("Sensor_Alias");

        Assert.NotNull(r);
        Assert.Equal("Local:1:I.Data.3", r!.ResolvedFrom);
        Assert.Equal("Local:1:I.Data.3", r.AliasFor);
        Assert.Equal("BOOL", r.DataType);
        Assert.True(r.IsModuleIo);

        var cip = new FakeRoute { Aliases = resolver };
        var gated = new GatedTagService(cip, new WriteGatePolicy([], []));
        var ex = await ErrorOf(() => gated.WriteAsync("Sensor_Alias", true));
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Empty(cip.Writes);
    }

    [Theory]
    [InlineData("CycleA")]
    [InlineData("Ghost")]
    [InlineData("Deep1")]
    public async Task UnresolvableAlias_GivesAliasUnresolved_OnEveryBackend(string alias)
    {
        var project = Project();
        var types = new StaticTypeSource(project);

        // CIP (routed)
        var router = new RoutedTagService(new FakeRoute(types), TagRouteMode.Cip, _ => Task.FromResult<ITagService>(new FakeRoute(types)));
        Assert.Equal(TagServiceErrorKind.AliasUnresolved, (await ErrorOf(() => router.ReadAsync(alias))).Error.Kind);

        // SDK route
        var sdkRouter = new RoutedTagService(new FakeRoute(types), TagRouteMode.Sdk, null);
        Assert.Equal(TagServiceErrorKind.AliasUnresolved, (await ErrorOf(() => sdkRouter.ReadAsync(alias))).Error.Kind);

        // L5X
        var l5x = new L5XOfflineTagService(project);
        await l5x.ConnectAsync("", "");
        Assert.Equal(TagServiceErrorKind.AliasUnresolved, (await ErrorOf(() => l5x.ReadAsync(alias))).Error.Kind);

        // Write gate
        var gated = new GatedTagService(new FakeRoute { Aliases = new AliasResolver(project) }, new WriteGatePolicy([], []));
        Assert.Equal(TagServiceErrorKind.AliasUnresolved, (await ErrorOf(() => gated.WriteAsync(alias, 1))).Error.Kind);

#if LOGIX_SDK
        // SDK offline
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(new RecordingFakeLogixProject()));
        await session.OpenAsync("dummy.ACD");
        var offline = new TypedSdkTagService(session, project);
        Assert.Equal(TagServiceErrorKind.AliasUnresolved, (await ErrorOf(() => offline.ReadAsync(alias))).Error.Kind);
#endif
    }

    // ── L5X ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SpeedAlias", 36, "DINT", "Cmd_Speed")]
    [InlineData("MandrelAlias", 12.5f, "REAL", "_ChangeProfile.Mandrel")]
    [InlineData("BitAlias", true, "BOOL", "Cmd_Speed.5")]
    [InlineData("Program:Main.Count", 7, "DINT", "Program:Main.Local_Count")]
    [InlineData("ChainA", 36, "DINT", "Cmd_Speed")]
    public async Task L5X_AliasRead_ReturnsBaseValueAndRealType_NeverNull(
        string alias, object expected, string type, string resolvedFrom)
    {
        var l5x = new L5XOfflineTagService(Project());
        await l5x.ConnectAsync("", "");

        var v = await l5x.ReadAsync(alias);

        Assert.Equal(alias, v.TagName);
        Assert.Equal(expected, v.Value);
        Assert.Equal(type, v.DataType);
        Assert.NotEqual("NULL", v.DataType);
        Assert.Equal(resolvedFrom, v.ResolvedFrom);
        Assert.False(string.IsNullOrEmpty(v.AliasFor));
    }

    [Fact]
    public async Task L5X_NonAliasRead_CarriesNoAliasFields()
    {
        var l5x = new L5XOfflineTagService(Project());
        await l5x.ConnectAsync("", "");

        var v = await l5x.ReadAsync("Cmd_Speed");

        Assert.Null(v.AliasFor);
        Assert.Null(v.ResolvedFrom);
        Assert.Equal(36, v.Value);
    }

    [Fact]
    public async Task L5X_ModuleIoAlias_ResolvesThroughModuleTypes_AndNeverReadsAsNull()
    {
        var l5x = new L5XOfflineTagService(ModuleProject());
        await l5x.ConnectAsync("", "");

        var v = await l5x.ReadAsync("Sensor_Alias");

        Assert.Equal("Sensor_Alias", v.TagName);
        Assert.Equal("Local:1:I.Data.3", v.ResolvedFrom);
        Assert.Equal("Local:1:I.Data.3", v.AliasFor);
        Assert.Equal("BOOL", v.DataType);
        Assert.IsType<bool>(v.Value);
    }

    // ── SDK offline ───────────────────────────────────────────────────────────

#if LOGIX_SDK
    [Fact]
    public async Task SdkOffline_AliasRead_ReadsThroughTheBase()
    {
        var fake = new RecordingFakeLogixProject { DintReturnValue = 36 };
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        var svc = new TypedSdkTagService(session, Project());

        var v = await svc.ReadAsync("SpeedAlias");

        Assert.Equal("SpeedAlias", v.TagName);
        Assert.Equal(36, v.Value);
        Assert.Equal("DINT", v.DataType);
        Assert.Equal("Cmd_Speed", v.ResolvedFrom);
        Assert.Equal("Cmd_Speed", v.AliasFor);
        Assert.Contains("Cmd_Speed", fake.LastReadTagPath);
        Assert.DoesNotContain("SpeedAlias", fake.LastReadTagPath);
    }
#endif

    // ── Resolver ──────────────────────────────────────────────────────────────

    [Fact]
    public void Resolver_ReturnsNull_ForNonAlias_AndReportsTypeForAlias()
    {
        var resolver = new AliasResolver(Project());

        Assert.Null(resolver.Resolve("Cmd_Speed"));
        Assert.Null(resolver.Resolve("No_Such_Tag"));
        var r = resolver.Resolve("MandrelAlias");
        Assert.Equal(new AliasResolution("_ChangeProfile.Mandrel", "_ChangeProfile.Mandrel", "REAL"), r);
    }
}
