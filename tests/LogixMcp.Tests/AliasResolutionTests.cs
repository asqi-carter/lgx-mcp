using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Alias tags (<c>TagType="Alias"</c>, no <c>DataType</c>) are followed to their base for type lookup,
/// CIP reads and write gating: controller tag, UDT member, bit, chains up to depth 4, and an
/// <c>AliasUnresolved</c> error naming the chain for a cycle or a missing target.
/// </summary>
public class AliasResolutionTests
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

    private static L5X Project() => L5X.Parse($"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
          <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes>{Profile}</DataTypes>
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              {Base("Cmd_Speed", "DINT")}{Base("Cmd_Flags", "DINT")}{Base("Cmd_BenchSetpoint", "REAL")}
              {Alias("Alias_ToCmd", "Cmd_BenchSetpoint")}{Alias("Alias_ToOut", "Local:1:O.Data.0")}{Alias("Alias_ToOutVia", "Alias_ToOut")}{Base("_ChangeProfile", "Profile_UDT")}
              {Alias("SpeedAlias", "Cmd_Speed")}
              {Alias("MandrelAlias", "_ChangeProfile.Mandrel")}
              {Alias("BitAlias", "Cmd_Flags.5")}
              {Alias("Chain4a", "Chain4b")}{Alias("Chain4b", "Chain4c")}{Alias("Chain4c", "Chain4d")}{Alias("Chain4d", "Cmd_Speed")}
              {Alias("Chain5a", "Chain5b")}{Alias("Chain5b", "Chain5c")}{Alias("Chain5c", "Chain5d")}{Alias("Chain5d", "Chain5e")}{Alias("Chain5e", "Cmd_Speed")}
              {Alias("CycleA", "CycleB")}{Alias("CycleB", "CycleC")}{Alias("CycleC", "CycleA")}
              {Alias("Ghost", "No_Such_Tag")}
            </Tags>
            <Programs />
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """);

    private static CipTypeResolver Resolver() =>
        CipTypeResolver.ForSession(new StaticTypeSource(Project()), _ => Task.FromResult<IReadOnlyList<TagInfo>>([]));

    [Fact]
    public async Task AliasToControllerTag_ResolvesBaseType_ReportsResolvedFrom()
    {
        var r = await Resolver().ResolveAsync("SpeedAlias");

        Assert.True(r.IsResolved);
        Assert.Equal("DINT", r.TypeName);
        Assert.Equal(CipTypeKind.Atomic, r.Kind);
        Assert.Equal("Cmd_Speed", r.ResolvedFrom);
    }

    [Fact]
    public async Task AliasToUdtMember_ResolvesMemberType()
    {
        var r = await Resolver().ResolveAsync("MandrelAlias");

        Assert.True(r.IsResolved);
        Assert.Equal("REAL", r.TypeName);
        Assert.Equal("_ChangeProfile.Mandrel", r.ResolvedFrom);
    }

    [Fact]
    public async Task AliasToBit_ResolvesToBool()
    {
        var r = await Resolver().ResolveAsync("BitAlias");

        Assert.True(r.IsResolved);
        Assert.Equal("BOOL", r.TypeName);
        Assert.Equal("Cmd_Flags.5", r.ResolvedFrom);
    }

    [Fact]
    public async Task ChainedAlias_ResolvesUpToDepth4()
    {
        var ok = await Resolver().ResolveAsync("Chain4a");
        Assert.True(ok.IsResolved);
        Assert.Equal("DINT", ok.TypeName);
        Assert.Equal("Cmd_Speed", ok.ResolvedFrom);

        // Five aliases deep is past the limit.
        var tooDeep = await Resolver().ResolveAsync("Chain5a");
        Assert.True(tooDeep.IsAliasUnresolved);
        Assert.Contains("deeper than 4", tooDeep.Reason);
        Assert.Contains("Chain5a -> Chain5b -> Chain5c -> Chain5d -> Chain5e", tooDeep.Reason);
    }

    [Fact]
    public async Task AliasCycle_ReturnsAliasUnresolved_NamingChain()
    {
        var r = await Resolver().ResolveAsync("CycleA");

        Assert.False(r.IsResolved);
        Assert.True(r.IsAliasUnresolved);
        Assert.Contains("CycleA -> CycleB -> CycleC -> CycleA", r.Reason);
    }

    [Fact]
    public async Task AliasMissingTarget_ReturnsAliasUnresolved()
    {
        var r = await Resolver().ResolveAsync("Ghost");

        Assert.False(r.IsResolved);
        Assert.True(r.IsAliasUnresolved);
        Assert.Contains("No_Such_Tag", r.Reason);
        Assert.Contains("Ghost", r.Reason);
    }

    [Fact]
    public async Task NonAlias_IsNotMarkedResolvedFrom()
    {
        var r = await Resolver().ResolveAsync("Cmd_Speed");
        Assert.Equal("DINT", r.TypeName);
        Assert.Null(r.ResolvedFrom);
    }

    // ── CIP reads and write gating ────────────────────────────────────────────

    /// <summary>A fake route that records reads/writes; names in <c>byName</c> are served, others are not found.</summary>
    private sealed class Channel(Dictionary<string, object> served, ISessionTypeSource? types = null)
        : ITagService, ISessionTypeSource
    {
        public List<string> Reads { get; } = new();
        public List<string> Writes { get; } = new();
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default)
        {
            Reads.Add(t);
            return served.TryGetValue(t, out var v)
                ? Task.FromResult(new TagValue(t, v, "DINT"))
                : throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TagNotFound, $"'{t}' not found", t, NotFoundWhat: "tag", NotFoundPath: t));
        }
        public Task WriteAsync(string t, object v, CancellationToken ct = default) { Writes.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TagInfo>>([]);
        public TResult Lookup<TResult>(Func<L5X, TResult> lookup) => types!.Lookup(lookup);
        public Task<bool> RefreshAsync(string p, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Theory]
    [InlineData(true,  "SpeedAlias")]   // the route serves the alias by name
    [InlineData(false, "Cmd_Speed")]    // it does not: the base path is read
    public async Task CipRead_ReadsAliasNameFirst_ThenBase_AndReportsResolvedFrom(bool servesAlias, string expectedLastRead)
    {
        var types = new StaticTypeSource(Project());
        var served = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Cmd_Speed"] = 40 };
        if (servesAlias) served["SpeedAlias"] = 40;
        var sdk = new Channel(new(), types);
        var cip = new Channel(served, types);
        var router = new RoutedTagService(sdk, TagRouteMode.Cip, _ => Task.FromResult<ITagService>(cip));

        var result = await router.ReadAsync("SpeedAlias");

        Assert.Equal("SpeedAlias", result.TagName);
        Assert.Equal(40, result.Value);
        Assert.Equal("Cmd_Speed", result.ResolvedFrom);
        Assert.Equal("Cmd_Speed", result.AliasFor);
        Assert.Equal("SpeedAlias", cip.Reads[0]);
        Assert.Equal(expectedLastRead, cip.Reads[^1]);
        Assert.Equal(servesAlias ? 1 : 2, cip.Reads.Count);
        Assert.Empty(sdk.Reads);
    }

    [Fact]
    public async Task Read_OfCycle_ThrowsAliasUnresolved_AndReadsNothing()
    {
        var types = new StaticTypeSource(Project());
        var cip = new Channel(new(), types);
        var router = new RoutedTagService(new Channel(new(), types), TagRouteMode.Cip, _ => Task.FromResult<ITagService>(cip));

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => router.ReadAsync("CycleA"));

        Assert.Equal(TagServiceErrorKind.AliasUnresolved, ex.Error.Kind);
        Assert.Contains("CycleA -> CycleB -> CycleC -> CycleA", ex.Error.Message);
        Assert.Empty(cip.Reads);
    }

    private static GatedTagService Gated(Channel inner, params string[] granted) =>
        new(inner, new WriteGatePolicy(
            granted.Select(g => new GrantEntry(g, null, null, "test grant")).ToList(),
            [new DenyEntry("Cmd_Flags", DenyReason.ProgramWritten, "test deny")]),
            inner);

    [Fact]
    public async Task AliasWrite_IsGatedAsItsBase_AndForwardedToTheBase()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        // Only the base is granted; the alias name itself has no grant.
        var gate = Gated(inner, "Cmd_Speed");

        await gate.WriteAsync("SpeedAlias", 5);

        Assert.Equal(["Cmd_Speed"], inner.Writes);
        Assert.Equal("Cmd_Speed", gate.ResolveAliasForWrite("SpeedAlias")!.ResolvedFrom);
    }

    [Fact]
    public async Task AliasWrite_ToDeniedBase_IsRefused_NamingBothTags()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        var gate = Gated(inner, "Cmd_Speed", "BitAlias"); // a grant on the alias name must not help

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gate.WriteAsync("BitAlias", true));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains("BitAlias", ex.Error.Message);
        Assert.Contains("Cmd_Flags.5", ex.Error.Message);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task AliasWrite_Unresolved_IsRefusedAsAliasUnresolved()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        var gate = Gated(inner, "Ghost", "CycleA");

        foreach (var name in new[] { "Ghost", "CycleA" })
        {
            var ex = await Assert.ThrowsAsync<TagServiceException>(() => gate.WriteAsync(name, 1));
            Assert.Equal(TagServiceErrorKind.AliasUnresolved, ex.Error.Kind);
        }
        Assert.Empty(inner.Writes);
    }

    private static GatedTagService GatedWithAllowlist(Channel inner, string allowlistContent, params string[] granted)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, allowlistContent);
        try
        {
            var gate = Gated(inner, granted);
            gate.ApplyAllowlist(Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(path)));
            return gate;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Alias_ToCmd_to_Cmd_BenchSetpoint_with_Cmd_star_allowed_is_permitted()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        // The alias name does not match Cmd_*; only its resolved base does.
        var gate = GatedWithAllowlist(inner, "Cmd_*\n", "Cmd_BenchSetpoint");

        await gate.WriteAsync("Alias_ToCmd", 1.5);

        Assert.Equal(["Cmd_BenchSetpoint"], inner.Writes);
    }

    [Fact]
    public async Task AliasWrite_BaseNotOnAllowlist_IsRefused_EvenIfAliasNameIs()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        var gate = GatedWithAllowlist(inner, "Alias_*\n", "Cmd_BenchSetpoint");

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gate.WriteAsync("Alias_ToCmd", 1.5));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task Write_to_module_O_and_to_an_alias_of_it_stay_refused()
    {
        var inner = new Channel(new(), new StaticTypeSource(Project()));
        // Even a grant and an allowlist entry for every name involved cannot open module I/O.
        var gate = GatedWithAllowlist(inner, "Alias_*\n", "Local:1:O", "Local:1:O.Data.0", "Alias_ToOut", "Alias_ToOutVia");

        foreach (var name in new[] { "Local:1:O.Data.0", "Alias_ToOut", "Alias_ToOutVia" })
        {
            var ex = await Assert.ThrowsAsync<TagServiceException>(() => gate.WriteAsync(name, 1));
            Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        }
        Assert.Empty(inner.Writes);
    }
}
