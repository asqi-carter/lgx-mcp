using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Module I/O tags (<c>&lt;Module&gt;:I</c>, <c>:O</c>, <c>:C</c>) are typed from the module's connection
/// structure in the L5X, and are read-only through the write gate.
/// </summary>
public class ModuleIoTypeTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoModuleIo.L5X");

    private static CipTypeResolver Resolver() =>
        CipTypeResolver.ForSession(
            new StaticTypeSource(L5X.Load(FixturePath)),
            _ => Task.FromResult<IReadOnlyList<TagInfo>>([]));

    // ── Real Logix v38 shape: connection tags carry no Name attribute; the name is derived ──

    private static readonly string RealShapePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "ModuleSnippets.L5X");

    private static CipTypeResolver RealShapeResolver() =>
        CipTypeResolver.ForSession(
            new StaticTypeSource(L5X.Load(RealShapePath)),
            _ => Task.FromResult<IReadOnlyList<TagInfo>>([]));

    [Fact]
    public async Task RealExport_LocalSlotInputData_ResolvesToTypedValue()
    {
        var r = await RealShapeResolver().ResolveAsync("Local:1:I.Data");

        Assert.True(r.IsResolved, r.Reason);
        Assert.Equal(CipTypeKind.Atomic, r.Kind);
        Assert.Equal("DINT", r.TypeName);
    }

    [Fact]
    public async Task RealExport_LocalSlotBitAndOutputData_Resolve()
    {
        var resolver = RealShapeResolver();

        var bit = await resolver.ResolveAsync("Local:1:I.Data.3");
        Assert.True(bit.IsResolved, bit.Reason);
        Assert.Equal("BOOL", bit.TypeName);

        var output = await resolver.ResolveAsync("Local:2:O.Data");
        Assert.True(output.IsResolved, output.Reason);
        Assert.Equal("DINT", output.TypeName);
    }

    [Fact]
    public async Task RealExport_RemoteModuleInputMember_Resolves()
    {
        var resolver = RealShapeResolver();

        var ready = await resolver.ResolveAsync("VFD_Remote:I.Ready");
        Assert.True(ready.IsResolved, ready.Reason);
        Assert.Equal("BOOL", ready.TypeName);

        var freq = await resolver.ResolveAsync("VFD_Remote:I.OutputFreq");
        Assert.Equal("INT", freq.TypeName);
    }

    [Fact]
    public async Task RealExport_AliasToLocalSlotBit_Resolves()
    {
        var r = await RealShapeResolver().ResolveAsync("Sensor_Alias");

        Assert.True(r.IsResolved, r.Reason);
        Assert.Equal("BOOL", r.TypeName);
        Assert.Equal("Local:1:I.Data.3", r.ResolvedFrom);
    }

    [Fact]
    public async Task RealExport_ConfigTag_AndWrongSlot_AreHandled()
    {
        var resolver = RealShapeResolver();

        var config = await resolver.ResolveAsync("Local:1:C.FilterOffOn");
        Assert.True(config.IsResolved, config.Reason);
        Assert.Equal("INT", config.TypeName);

        // Slot 1 has no output tag; slot 7 holds no module.
        Assert.False((await resolver.ResolveAsync("Local:1:O.Data")).IsResolved);
        Assert.False((await resolver.ResolveAsync("Local:7:I.Data")).IsResolved);
    }

    [Fact]
    public async Task RemoteModuleInputMember_ResolvesFromConnectionStructure()
    {
        var r = await Resolver().ResolveAsync("VFD_MainDrive:I.Ready");

        Assert.True(r.IsResolved, r.Reason);
        Assert.Equal(CipTypeKind.Atomic, r.Kind);
        Assert.Equal("BOOL", r.TypeName);
    }

    [Fact]
    public async Task LocalSlotPointData_ResolvesFromConnectionStructure()
    {
        var r = await Resolver().ResolveAsync("Local:1:I.Pt00.Data");

        Assert.True(r.IsResolved, r.Reason);
        Assert.Equal("BOOL", r.TypeName);
    }

    [Fact]
    public async Task ModuleOutputArrayElement_AndOtherMembers_Resolve()
    {
        var resolver = Resolver();

        var element = await resolver.ResolveAsync("VFD_MainDrive:O.Data[0]");
        Assert.True(element.IsResolved, element.Reason);
        Assert.Equal("DINT", element.TypeName);

        var real = await resolver.ResolveAsync("VFD_MainDrive:I.OutputFreq");
        Assert.Equal("REAL", real.TypeName);

        var bit = await resolver.ResolveAsync("VFD_MainDrive:I.Status.3");
        Assert.Equal("BOOL", bit.TypeName);

        var whole = await resolver.ResolveAsync("Local:1:I");
        Assert.Equal(CipTypeKind.Structure, whole.Kind);
        Assert.Contains("Pt00.Data", whole.MemberPaths);
    }

    [Fact]
    public async Task UnknownMemberOfKnownStructure_IsUnresolved_NotBlobMessage()
    {
        var r = await Resolver().ResolveAsync("VFD_MainDrive:I.NoSuchMember");

        Assert.False(r.IsResolved);
        Assert.Contains("NoSuchMember", r.Reason);
        Assert.DoesNotContain("not in this export", r.Reason);
    }

    [Theory]
    [InlineData("Legacy_Rack_IO:I.Data", "Legacy_Rack_IO")]
    [InlineData("Legacy_Rack_IO:O.Data[0]", "Legacy_Rack_IO")]
    [InlineData("Ghost_Module:I.Ready", "Ghost_Module")]
    public async Task ModuleWithNoStructure_ReturnsTypeUnknown_NamingModule(string path, string module)
    {
        var r = await Resolver().ResolveAsync(path);

        Assert.False(r.IsResolved);
        Assert.Contains(module, r.Reason);
        Assert.Contains("module I/O structure not in this export", r.Reason);
        Assert.DoesNotContain("NULL", r.Reason);
    }

    [Fact]
    public void ModuleWithNoStructure_ReadSurfacesTypeUnknown_WithModuleName()
    {
        var resolution = Resolver().ResolveAsync("Legacy_Rack_IO:I.Data").GetAwaiter().GetResult();
        var buffer = new EmptyBuffer();

        var ex = Assert.Throws<TagServiceException>(
            () => LibPlcTagService.DecodeValue("Legacy_Rack_IO:I.Data", resolution, buffer));

        Assert.Equal(TagServiceErrorKind.TypeUnknown, ex.Error.Kind);
        Assert.Contains("Legacy_Rack_IO", ex.Error.Message);
        Assert.Contains("module I/O structure not in this export", ex.Error.Message);
    }

    private sealed class EmptyBuffer : LibPlcTagService.ITagBuffer
    {
        public int GetSize() => 4;
        public bool GetBit(int o) => false;
        public sbyte GetInt8(int o) => 0;
        public byte GetUInt8(int o) => 0;
        public short GetInt16(int o) => 0;
        public ushort GetUInt16(int o) => 0;
        public int GetInt32(int o) => 0;
        public uint GetUInt32(int o) => 0;
        public long GetInt64(int o) => 0;
        public ulong GetUInt64(int o) => 0;
        public float GetFloat32(int o) => 0;
        public double GetFloat64(int o) => 0;
    }

    // ── Write refusal ─────────────────────────────────────────────────────────

    private sealed class RecordingService : ITagService
    {
        public List<string> Writes { get; } = new();
        public Task ConnectAsync(string g, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<TagValue> ReadAsync(string t, CancellationToken ct = default) => Task.FromResult(new TagValue(t, 0, "DINT"));
        public Task WriteAsync(string t, object v, CancellationToken ct = default) { Writes.Add(t); return Task.CompletedTask; }
        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TagInfo>>([]);
    }

    [Theory]
    [InlineData("VFD_MainDrive:O.Start")]
    [InlineData("VFD_MainDrive:O.Data[0]")]
    [InlineData("Local:2:O.Data")]
    [InlineData("VFD_MainDrive:I.Ready")]
    public async Task WriteToModuleIoTag_IsRefused_EvenWhenGranted(string path)
    {
        var inner = new RecordingService();
        var root  = path[..path.IndexOfAny(['.', '['])];
        var gate  = new GatedTagService(inner,
            new WriteGatePolicy([new GrantEntry(root, null, null, "test grant"), new GrantEntry(path, null, null, "test grant")], []));

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => gate.WriteAsync(path, true));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        Assert.Contains("module I/O", ex.Error.Message);
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public async Task WriteToOrdinaryTag_StillGoesThrough()
    {
        var inner = new RecordingService();
        var gate  = new GatedTagService(inner,
            new WriteGatePolicy([new GrantEntry("Run_Cmd", null, null, "test grant")], []));

        await gate.WriteAsync("Run_Cmd", true);

        Assert.Equal(["Run_Cmd"], inner.Writes);
    }
}
