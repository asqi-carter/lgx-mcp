using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Reads of program-scoped aliases (<c>Program:P.Alias</c>) follow <c>AliasFor</c> on both the SDK
/// and the CIP route, return the target's type and value with <c>aliasFor</c>, and report an alias
/// that cannot be resolved as a typed error, never <c>kind: Unknown</c>.
/// </summary>
public class AliasReadTests
{
    private const string Profile = """
        <DataType Name="Profile_UDT" Family="NoFamily" Class="User">
          <Members>
            <Member Name="Mandrel" DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write" />
          </Members>
        </DataType>
        """;

    private static string Base(string name, string type) =>
        $"""<Tag Name="{name}" TagType="Base" DataType="{type}" Radix="Decimal" ExternalAccess="Read/Write" />""";

    private static string Alias(string name, string target) =>
        $"""<Tag Name="{name}" TagType="Alias" AliasFor="{target}" Radix="Decimal" ExternalAccess="Read/Write" />""";

    private static L5X Project(string controllerTags, string programTags) =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <DataTypes>{Profile}</DataTypes>
                <Modules />
                <AddOnInstructionDefinitions />
                <Tags>{controllerTags}</Tags>
                <Programs>
                  <Program Name="Main" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="R">
                    <Tags>{programTags}</Tags>
                    <Routines><Routine Name="R" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

    private static L5X StandardProject() => Project(
        Base("_ChangeProfile", "Profile_UDT") + Base("Ctl_Speed", "DINT"),
        Base("Local_Count", "DINT")
        + Alias("Mandrel", "_ChangeProfile.Mandrel")        // controller-scope member target
        + Alias("Speed", "Ctl_Speed")                        // controller-scope whole tag
        + Alias("Count", "Local_Count")                      // program-scope target
        + Alias("Count2", "Count")                           // alias of an alias
        + Alias("Ghost", "No_Such_Tag")                      // target does not exist
        + Alias("Io", "Local:1:I.Data")                      // module I/O
        + Alias("LoopA", "LoopB") + Alias("LoopB", "LoopA")  // circular
        + Alias("D1", "D2") + Alias("D2", "D3") + Alias("D3", "D4") + Alias("D4", "D5") + Alias("D5", "Ctl_Speed")  // 5 aliases deep
        + Alias("E1", "E2") + Alias("E2", "E3") + Alias("E3", "E4") + Alias("E4", "Ctl_Speed"));                    // exactly 4 deep

    /// <summary>A route: the reads it served by path, with values typed by path.</summary>
    private sealed class Channel(string label, ISessionTypeSource? types = null) : ITagService, ISessionTypeSource
    {
        private readonly Dictionary<string, TagValue> _values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["_ChangeProfile.Mandrel"]        = new("_ChangeProfile.Mandrel", 12.5f, "REAL"),
            ["Ctl_Speed"]                     = new("Ctl_Speed", 40, "DINT"),
            ["Program:Main.Local_Count"]      = new("Program:Main.Local_Count", 7, "DINT"),
        };
        public string Label { get; } = label;
        public List<string> Reads { get; } = new();
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default)
        {
            Reads.Add(t);
            return _values.TryGetValue(t, out var v)
                ? Task.FromResult(v)
                : throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TagNotFound, $"'{t}' not found", t, NotFoundWhat: "tag", NotFoundPath: t));
        }
        public Task WriteAsync(string t, object v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public TResult Lookup<TResult>(Func<L5X, TResult> lookup) => types!.Lookup(lookup);
        public Task<bool> RefreshAsync(string p, CancellationToken ct = default) => Task.FromResult(false);
    }

    private static (TagSessionService Session, Channel Sdk, Channel Cip) SessionOf(TagRouteMode mode)
    {
        var types = new StaticTypeSource(StandardProject());
        var sdk   = new Channel("sdk", types);
        var cip   = new Channel("cip", types);
        var router = new RoutedTagService(sdk, mode, _ => Task.FromResult<ITagService>(cip));
        var session = new TagSessionService();
        session.Attach(new GatedTagService(router, new WriteGatePolicy([], [])),
            isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");
        return (session, sdk, cip);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, o.GetType());

    [Fact]
    public async Task ProgramAlias_ResolvesViaAliasFor_OnSdkRoute()
    {
        var (session, sdk, cip) = SessionOf(TagRouteMode.Sdk);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.Mandrel", session));

        Assert.Equal("Program:Main.Mandrel", result.Tag);
        Assert.Equal(12.5f, result.Value);
        Assert.Equal("REAL", result.DataType);
        Assert.Equal("_ChangeProfile.Mandrel", result.AliasFor);
        Assert.Equal("sdk", result.Route);
        Assert.Equal(["_ChangeProfile.Mandrel"], sdk.Reads);
        Assert.Empty(cip.Reads);
        Assert.Equal("_ChangeProfile.Mandrel", Json(result).GetProperty("aliasFor").GetString());
    }

    [Fact]
    public async Task ProgramAlias_ResolvesViaAliasFor_OnCipRoute()
    {
        var (session, sdk, cip) = SessionOf(TagRouteMode.Cip);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.Mandrel", session));

        Assert.Equal(12.5f, result.Value);
        Assert.Equal("REAL", result.DataType);
        Assert.Equal("_ChangeProfile.Mandrel", result.AliasFor);
        Assert.Equal("cip", result.Route);
        Assert.Equal("_ChangeProfile.Mandrel", result.ResolvedFrom);
        // The alias's own name is read first; this channel cannot serve it, so the base path follows.
        Assert.Equal(["Program:Main.Mandrel", "_ChangeProfile.Mandrel"], cip.Reads);
        Assert.Empty(sdk.Reads);
    }

    [Theory]
    [InlineData("Program:Main.Speed",  "Ctl_Speed",              "Ctl_Speed",                40)]
    [InlineData("Program:Main.Count",  "Local_Count",            "Program:Main.Local_Count", 7)]
    [InlineData("Program:Main.Count2", "Count",                  "Program:Main.Local_Count", 7)]
    public async Task ProgramAlias_FollowsScopeAndChains(string path, string aliasFor, string readPath, int expected)
    {
        foreach (var mode in new[] { TagRouteMode.Sdk, TagRouteMode.Cip })
        {
            var (session, sdk, cip) = SessionOf(mode);
            var result = Assert.IsType<ReadTagResult>(await ReadTagHandler.ExecuteAsync(path, session));

            Assert.Equal(expected, result.Value);
            Assert.Equal("DINT", result.DataType);
            Assert.Equal(aliasFor, result.AliasFor);
            Assert.Equal(mode == TagRouteMode.Sdk ? [readPath] : [path, readPath],
                (mode == TagRouteMode.Sdk ? sdk : cip).Reads);
        }
    }

    [Fact]
    public async Task NonAliasRead_IsUnchanged_AndCarriesNoAliasFor()
    {
        var (session, sdk, _) = SessionOf(TagRouteMode.Sdk);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.Local_Count", session));

        Assert.Null(result.AliasFor);
        Assert.False(Json(result).TryGetProperty("aliasFor", out _));
        Assert.Equal(["Program:Main.Local_Count"], sdk.Reads);
    }

    [Theory]
    [InlineData("Program:Main.Ghost", "does not exist")]
    [InlineData("Program:Main.Io",    "module I/O")]
    [InlineData("Program:Main.LoopA", "circular")]
    [InlineData("Program:Main.D1",    "deeper than 4")]
    public async Task UnresolvableAlias_ReturnsTypedError_NeverUnknown(string path, string why)
    {
        foreach (var mode in new[] { TagRouteMode.Sdk, TagRouteMode.Cip })
        {
            var (session, sdk, cip) = SessionOf(mode);

            var error = Assert.IsType<ReadTagError>(await ReadTagHandler.ExecuteAsync(path, session));

            Assert.Equal(nameof(TagServiceErrorKind.AliasUnresolved), error.Kind);
            Assert.NotEqual(nameof(TagServiceErrorKind.Unknown), error.Kind);
            Assert.Contains(why, error.Error);
            Assert.Equal(path, error.Tag);
            Assert.Empty(sdk.Reads);
            Assert.Empty(cip.Reads);
        }
    }

    [Fact]
    public void Resolver_ComposesMemberSuffixOntoTarget()
    {
        var project = Project(
            Base("_ChangeProfile", "Profile_UDT"),
            Alias("Prof", "_ChangeProfile"));

        var r = AliasReadResolver.Resolve(project, "Program:Main.Prof.Mandrel");

        Assert.NotNull(r);
        Assert.True(r.IsResolved);
        Assert.Equal("_ChangeProfile.Mandrel", r.Target);
        Assert.Equal("_ChangeProfile", r.AliasFor);
    }

    [Theory]
    [InlineData(TagRouteMode.Sdk)]
    [InlineData(TagRouteMode.Cip)]
    public async Task AliasRead_ReturnsResolvedFromAndAliasFor(TagRouteMode mode)
    {
        var (session, _, _) = SessionOf(mode);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.Count2", session));

        Assert.Equal("Program:Main.Count2", result.Tag);
        Assert.Equal("Count", result.AliasFor);
        Assert.Equal("Program:Main.Local_Count", result.ResolvedFrom);
        var json = Json(result);
        Assert.Equal("Count", json.GetProperty("aliasFor").GetString());
        Assert.Equal("Program:Main.Local_Count", json.GetProperty("resolvedFrom").GetString());
    }

    [Fact]
    public async Task AliasRead_AtExactlyDepth4_Resolves()
    {
        var (session, _, _) = SessionOf(TagRouteMode.Sdk);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.E1", session));

        Assert.Equal(40, result.Value);
        Assert.Equal("Ctl_Speed", result.ResolvedFrom);
    }

    [Fact]
    public async Task NonAliasRead_CarriesNoResolvedFrom()
    {
        var (session, _, _) = SessionOf(TagRouteMode.Sdk);

        var result = Assert.IsType<ReadTagResult>(
            await ReadTagHandler.ExecuteAsync("Program:Main.Local_Count", session));

        Assert.Null(result.ResolvedFrom);
        Assert.False(Json(result).TryGetProperty("resolvedFrom", out _));
    }
}
