using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Bit-of-integer paths (<c>Tag.N</c>, <c>Tag.Member.N</c>) over CIP: a read fetches the parent integer and returns
/// bit N as a BOOL; a bit beyond the integer's width is <c>BitOutOfRange</c>; a write to one bit is refused.
/// The CIP link is a fake that serves the parent integer's bytes and records which tags were asked for.
/// </summary>
public class BitOfIntegerTests
{
    private static readonly string RealShapePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "ModuleSnippets.L5X");

    private sealed class BytesBuffer(params byte[] bytes) : LibPlcTagService.ITagBuffer
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

    private static CipTypeResolver TagListResolver(string name, string type) =>
        CipTypeResolver.ForSession(
            new StaticTypeSource(L5X.Load(RealShapePath)),
            _ => Task.FromResult<IReadOnlyList<TagInfo>>([new TagInfo(name, type)]));

    private static async Task<(LibPlcTagService Svc, List<string> Asked)> Fake(
        CipTypeResolver resolver, byte[] parentBytes)
    {
        var asked = new List<string>();
        var svc = new LibPlcTagService
        {
            TypeResolver = resolver,
            ParentBufferReader = (name, _) =>
            {
                asked.Add(name);
                return Task.FromResult<LibPlcTagService.ITagBuffer>(new BytesBuffer(parentBytes));
            },
        };
        await svc.ConnectAsync("198.51.100.1", "1,0");
        return (svc, asked);
    }

    [Fact]
    public async Task BenchDint_Bit5_ReturnsBoolExtractedFromDint()
    {
        // 0x20 = bit 5 set.
        var (svc, asked) = await Fake(TagListResolver("Bench_Dint", "DINT"), [0x20, 0x00, 0x00, 0x00]);

        var v = await svc.ReadAsync("Bench_Dint.5");

        Assert.Equal(true, v.Value);
        Assert.Equal("BOOL", v.DataType);
        Assert.Equal("Bench_Dint.5", v.TagName);
        Assert.Equal(["Bench_Dint"], asked); // the parent is read, never the bit path
    }

    [Fact]
    public async Task BenchDint_ClearBit_ReturnsFalse()
    {
        var (svc, _) = await Fake(TagListResolver("Bench_Dint", "DINT"), [0x20, 0x00, 0x00, 0x00]);

        Assert.Equal(false, (await svc.ReadAsync("Bench_Dint.4")).Value);
    }

    [Fact]
    public async Task BenchDint_Bit31_ReadsHighBit()
    {
        var (svc, _) = await Fake(TagListResolver("Bench_Dint", "DINT"), [0x00, 0x00, 0x00, 0x80]);

        Assert.Equal(true, (await svc.ReadAsync("Bench_Dint.31")).Value);
    }

    [Fact]
    public async Task LocalModuleData_Bit3_ReturnsBool()
    {
        var resolver = CipTypeResolver.ForSession(
            new StaticTypeSource(L5X.Load(RealShapePath)),
            _ => Task.FromResult<IReadOnlyList<TagInfo>>([]));
        var (svc, asked) = await Fake(resolver, [0x08, 0x00, 0x00, 0x00]);

        var v = await svc.ReadAsync("Local:1:I.Data.3");

        Assert.Equal(true, v.Value);
        Assert.Equal("BOOL", v.DataType);
        Assert.Equal(["Local:1:I.Data"], asked);
    }

    [Fact]
    public async Task Bit40OfDint_GivesBitOutOfRange_WithoutReadingTheController()
    {
        var (svc, asked) = await Fake(TagListResolver("Bench_Dint", "DINT"), [0xFF, 0xFF, 0xFF, 0xFF]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Bench_Dint.40"));

        Assert.Equal(TagServiceErrorKind.BitOutOfRange, ex.Error.Kind);
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("SINT", 1, 8)]
    [InlineData("INT", 2, 16)]
    [InlineData("DINT", 4, 32)]
    [InlineData("LINT", 8, 64)]
    public async Task FirstBitPastTheWidth_IsBitOutOfRange_LastBitIsReadable(string type, int bytes, int width)
    {
        var data = Enumerable.Repeat((byte)0xFF, bytes).ToArray();
        var (svc, _) = await Fake(TagListResolver("Parent", type), data);

        Assert.Equal(true, (await svc.ReadAsync($"Parent.{width - 1}")).Value);
        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync($"Parent.{width}"));
        Assert.Equal(TagServiceErrorKind.BitOutOfRange, ex.Error.Kind);
    }

    [Fact]
    public void DecodeBit_WrongWidthReply_IsTypeMismatch_NotGarbageBits()
    {
        var ex = Assert.Throws<TagServiceException>(() =>
            LibPlcTagService.DecodeBit("Bench_Dint.5", "DINT", 5, new BytesBuffer(0x20)));

        Assert.Equal(TagServiceErrorKind.TypeMismatch, ex.Error.Kind);
    }

    [Theory]
    [InlineData("Bench_Dint.5", "Bench_Dint", 5)]
    [InlineData("Local:1:I.Data.3", "Local:1:I.Data", 3)]
    [InlineData("Udt.Member.12", "Udt.Member", 12)]
    public void TryParseBitPath_SplitsParentAndBit(string path, string parent, int bit)
    {
        Assert.True(LibPlcTagService.TryParseBitPath(path, out var p, out var b));
        Assert.Equal(parent, p);
        Assert.Equal(bit, b);
    }

    [Theory]
    [InlineData("Bench_Dint")]
    [InlineData("Arr[3]")]
    [InlineData("Udt.Member")]
    [InlineData("Program:Main.5")]
    public void TryParseBitPath_NotABitPath(string path) =>
        Assert.False(LibPlcTagService.TryParseBitPath(path, out _, out _));

    [Fact]
    public async Task WriteToBitOfInteger_IsRefusedWithTypedError_AndNothingIsWritten()
    {
        // The fake would hand out the parent's bytes if a read-modify-write were attempted.
        var (svc, asked) = await Fake(TagListResolver("Bench_Dint", "DINT"), [0x00, 0x00, 0x00, 0x00]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("Bench_Dint.5", true));

        Assert.Equal(TagServiceErrorKind.BitWriteUnsupported, ex.Error.Kind);
        Assert.Contains("nothing was written", ex.Error.Message);
        Assert.Empty(asked); // refused before any CIP traffic, so the whole integer is never written
    }

    [Fact]
    public async Task WriteToOutOfRangeBit_ReturnsBitOutOfRange_NotBitWriteUnsupported()
    {
        var (svc, asked) = await Fake(TagListResolver("Cmd_Bits", "DINT"), [0x00, 0x00, 0x00, 0x00]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("Cmd_Bits.40", true));

        Assert.Equal(TagServiceErrorKind.BitOutOfRange, ex.Error.Kind);
        Assert.Contains("bits 0 to 31", ex.Error.Message);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task WriteToModuleDataBit_IsRefusedWithTypedError()
    {
        var resolver = CipTypeResolver.ForSession(
            new StaticTypeSource(L5X.Load(RealShapePath)),
            _ => Task.FromResult<IReadOnlyList<TagInfo>>([]));
        var (svc, _) = await Fake(resolver, new byte[4]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.WriteAsync("Local:1:I.Data.3", false));

        Assert.Equal(TagServiceErrorKind.BitWriteUnsupported, ex.Error.Kind);
    }
}
