#if LOGIX_SDK
using System.Text;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests STRING and StringFamily user-defined string member decoding in
/// <see cref="TypedSdkTagService"/> (byte-array decode path), using the
/// <c>DemoRecipe.L5X</c> fixture (Recipe1 : Recipe).
/// </summary>
public class TypedSdkTagServiceMemberTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoRecipe.L5X");

    private static MemberLayout LayoutOf(string memberPath)
        => new UdtLayout(L5X.Load(FixturePath)).Resolve("Recipe", memberPath);

    /// <summary>Builds a Recipe byte array with the string member at <paramref name="path"/> filled in.</summary>
    private static byte[] RecipeBytes(string path, int len, byte[] data)
    {
        var layout = LayoutOf(path);
        var bytes  = new byte[LayoutOf("Count").ByteOffset + 4];
        BitConverter.GetBytes(len).CopyTo(bytes, layout.ByteOffset);
        data.CopyTo(bytes, layout.ByteOffset + 4);
        return bytes;
    }

    private static async Task<TypedSdkTagService> OpenAsync(byte[] bytes)
    {
        var fake    = new RecordingFakeLogixProject { ByteArrayReturnValue = ByteString.CopyFrom(bytes) };
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        return new TypedSdkTagService(session, L5X.Load(FixturePath));
    }

    /// <summary>STRING member: LEN bytes of DATA are returned; trailing bytes ignored.</summary>
    [Fact]
    public async Task StringMember_DecodesLenBytes()
    {
        var data = new byte[82];
        Encoding.ASCII.GetBytes("MixXXXXX").CopyTo(data, 0); // only first 3 are valid
        var svc = await OpenAsync(RecipeBytes("Step.Name", 3, data));

        var result = await svc.ReadAsync("Recipe1.Step.Name");

        Assert.Equal("STRING", result.DataType);
        Assert.Equal("Mix", result.Value);
    }

    /// <summary>Bytes 0x80-0xFF decode as Latin-1 and round-trip.</summary>
    [Fact]
    public async Task StringMember_Latin1HighBytesRoundTrip()
    {
        var raw  = new byte[] { 0x41, 0x80, 0xB0, 0xE9, 0xFF };
        var data = new byte[82];
        raw.CopyTo(data, 0);
        var svc = await OpenAsync(RecipeBytes("Step.Name", raw.Length, data));

        var result = await svc.ReadAsync("Recipe1.Step.Name");

        var text = Assert.IsType<string>(result.Value);
        Assert.Equal("A°éÿ", text);
        Assert.Equal(raw, Encoding.Latin1.GetBytes(text));
    }

    /// <summary>LEN &lt; 0 or &gt; DATA length yields StringLengthInvalid naming member and raw LEN.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(83)]
    [InlineData(int.MaxValue)]
    public async Task StringMember_LenOutOfRange_ReturnsStringLengthInvalid(int len)
    {
        var svc = await OpenAsync(RecipeBytes("Step.Name", len, new byte[82]));

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Recipe1.Step.Name"));

        Assert.Equal(TagServiceErrorKind.StringLengthInvalid, ex.Error.Kind);
        Assert.Contains("Recipe1.Step.Name", ex.Error.Message);
        Assert.Contains(len.ToString(), ex.Error.Message);
    }

    /// <summary>User-defined STRING_20 decodes using its declared DATA length and type name.</summary>
    [Fact]
    public async Task UserStringType_String20_DecodesWithDeclaredLength()
    {
        var data = new byte[20];
        Encoding.ASCII.GetBytes("hold 5s").CopyTo(data, 0);
        var svc = await OpenAsync(RecipeBytes("Step.Note", 7, data));

        var result = await svc.ReadAsync("Recipe1.Step.Note");

        Assert.Equal("STRING_20", result.DataType);
        Assert.Equal("hold 5s", result.Value);

        // LEN 21 exceeds the declared 20-byte DATA (though it is < 82).
        var bad = await OpenAsync(RecipeBytes("Step.Note", 21, new byte[20]));
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => bad.ReadAsync("Recipe1.Step.Note"));
        Assert.Equal(TagServiceErrorKind.StringLengthInvalid, ex.Error.Kind);
    }

    // ── Nested-member leaf decoding ─────────────────────

    /// <summary>Builds a zeroed Recipe-sized byte array (Count is the last member).</summary>
    private static byte[] BlankRecipe() => new byte[LayoutOf("Count").ByteOffset + 4];

    /// <summary>Recipe1.Step.PRE (DINT two levels deep) decodes at the nested offset.</summary>
    [Fact]
    public async Task NestedMember_TwoLevel_DecodesAtomicLeaf()
    {
        var bytes = BlankRecipe();
        BitConverter.GetBytes(1500).CopyTo(bytes, LayoutOf("Step.PRE").ByteOffset);
        BitConverter.GetBytes((short)3).CopyTo(bytes, LayoutOf("Count").ByteOffset);
        var svc = await OpenAsync(bytes);

        var result = await svc.ReadAsync("Recipe1.Step.PRE");

        Assert.Equal("DINT", result.DataType);
        Assert.Equal(1500, result.Value);
    }

    /// <summary>Recipe1.Step.Name (STRING two levels deep) decodes the leaf string.</summary>
    [Fact]
    public async Task NestedMember_TwoLevel_DecodesStringLeaf()
    {
        var data = new byte[82];
        Encoding.ASCII.GetBytes("Mix").CopyTo(data, 0);
        var svc = await OpenAsync(RecipeBytes("Step.Name", 3, data));

        var result = await svc.ReadAsync("Recipe1.Step.Name");

        Assert.Equal("STRING", result.DataType);
        Assert.Equal("Mix", result.Value);
    }

    /// <summary>Recipe1.Step.Enable (BOOL packed in a nested hidden host) decodes its bit.</summary>
    [Fact]
    public async Task NestedMember_PackedBool_Decodes()
    {
        var layout = LayoutOf("Step.Enable");
        Assert.Equal("BOOL", layout.LogixType);

        var on = BlankRecipe();
        on[layout.ByteOffset] = (byte)(1 << layout.BitIndex);
        var svcOn = await OpenAsync(on);
        var resultOn = await svcOn.ReadAsync("Recipe1.Step.Enable");
        Assert.Equal("BOOL", resultOn.DataType);
        Assert.Equal(true, resultOn.Value);

        var off = BlankRecipe();
        off[layout.ByteOffset] = (byte)~(1 << layout.BitIndex);
        var svcOff = await OpenAsync(off);
        var resultOff = await svcOff.ReadAsync("Recipe1.Step.Enable");
        Assert.Equal(false, resultOff.Value);
    }

    /// <summary>Reading a member whose type is a UDT returns MemberPathUnsupported, not Unknown.</summary>
    [Fact]
    public async Task StructureMember_ReturnsMemberPathUnsupported()
    {
        var svc = await OpenAsync(BlankRecipe());

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Recipe1.Step"));

        Assert.Equal(TagServiceErrorKind.MemberPathUnsupported, ex.Error.Kind);
        Assert.Equal(
            "member 'Step' is a structure (type RecipeStep); read its atomic or STRING members individually",
            ex.Error.Message);
    }

    /// <summary>
    /// Indexed member inside a nested UDT array (Recipe1.Steps[1].Name) decodes
    /// at index × element size; an out-of-range index is rejected.
    /// </summary>
    [Fact]
    public async Task IndexedMember_NestedUdtArray_DecodesLeaf()
    {
        var xml = File.ReadAllText(FixturePath).Replace(
            "<Member Name=\"Count\" DataType=\"INT\" Dimension=\"0\" Radix=\"Decimal\" Hidden=\"false\" ExternalAccess=\"Read/Write\" />",
            "<Member Name=\"Count\" DataType=\"INT\" Dimension=\"0\" Radix=\"Decimal\" Hidden=\"false\" ExternalAccess=\"Read/Write\" />" +
            "<Member Name=\"Steps\" DataType=\"RecipeStep\" Dimension=\"2\" Radix=\"NullType\" Hidden=\"false\" ExternalAccess=\"Read/Write\" />");
        var tmp = Path.Combine(Path.GetTempPath(), $"DemoRecipeIdx_{Guid.NewGuid():N}.L5X");
        File.WriteAllText(tmp, xml);
        try
        {
            var project = L5X.Load(tmp);
            var layout  = new UdtLayout(project).Resolve("Recipe", "Steps[1].Name");
            Assert.Equal(new UdtLayout(project).Resolve("Recipe", "Steps[0].Name").ByteOffset + 120,
                         layout.ByteOffset);

            var bytes = new byte[layout.ByteOffset + 88 + 120];
            BitConverter.GetBytes(2).CopyTo(bytes, layout.ByteOffset);
            Encoding.ASCII.GetBytes("Hi").CopyTo(bytes, layout.ByteOffset + 4);

            var fake    = new RecordingFakeLogixProject { ByteArrayReturnValue = ByteString.CopyFrom(bytes) };
            var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
            await session.OpenAsync("dummy.ACD");
            var svc = new TypedSdkTagService(session, project);

            var result = await svc.ReadAsync("Recipe1.Steps[1].Name");
            Assert.Equal("Hi", result.Value);

            var ex = await Assert.ThrowsAsync<TagServiceException>(
                () => svc.ReadAsync("Recipe1.Steps[2].Name"));
            Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
        }
        finally { File.Delete(tmp); }
    }
}
#endif
