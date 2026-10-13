#if LOGIX_SDK
using Google.Protobuf;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// SDK-route reads of array elements and program-scoped members: the base tag is read
/// as a byte array and the element or member is selected from it.
/// </summary>
public class TypedSdkTagServiceElementReadTests
{
    private const string ProjectXml =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
        "<DataTypes><DataType Name=\"Cfg\" Family=\"NoFamily\" Class=\"User\"><Members>" +
        "<Member Name=\"Speed\" DataType=\"DINT\" Dimension=\"0\" Radix=\"Decimal\" Hidden=\"false\" ExternalAccess=\"Read/Write\" />" +
        "<Member Name=\"Gain\" DataType=\"REAL\" Dimension=\"0\" Radix=\"Float\" Hidden=\"false\" ExternalAccess=\"Read/Write\" />" +
        "</Members></DataType></DataTypes>" +
        "<Tags>" +
        "<Tag Name=\"Counts\" TagType=\"Base\" DataType=\"DINT\" Dimensions=\"4\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "<Tag Name=\"Grid\" TagType=\"Base\" DataType=\"INT\" Dimensions=\"2 3\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "</Tags>" +
        "<Programs><Program Name=\"Main\"><Tags>" +
        "<Tag Name=\"OneShots\" TagType=\"Base\" DataType=\"BOOL\" Dimensions=\"40\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "<Tag Name=\"Vals\" TagType=\"Base\" DataType=\"DINT\" Dimensions=\"3\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "<Tag Name=\"Cfg1\" TagType=\"Base\" DataType=\"Cfg\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "<Tag Name=\"Cfgs\" TagType=\"Base\" DataType=\"Cfg\" Dimensions=\"2\" Constant=\"false\" ExternalAccess=\"Read/Write\"/>" +
        "</Tags><Routines/></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(TypedSdkTagService Svc, RecordingFakeLogixProject Fake)> OpenAsync(byte[] bytes)
    {
        var fake    = new RecordingFakeLogixProject { ByteArrayReturnValue = ByteString.CopyFrom(bytes) };
        var session = new LogixProjectSession((_, _) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        return (new TypedSdkTagService(session, L5X.Parse(ProjectXml)), fake);
    }

    private static byte[] Dints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    /// <summary>Program:Main.OneShots[1] reads the base tag and selects bit 1.</summary>
    [Fact]
    public async Task ProgramScopedBoolArrayElement_ReadsBaseTagAndSelectsBit()
    {
        var (svc, fake) = await OpenAsync(Dints(0b10));

        var one = await svc.ReadAsync("Program:Main.OneShots[1]");
        Assert.Equal(true, one.Value);
        Assert.Equal("BOOL", one.DataType);
        Assert.Equal("Program:Main.OneShots[1]", one.TagName);
        Assert.Equal("Controller/Programs/Program[@Name='Main']/Tags/Tag[@Name='OneShots']",
                     fake.LastByteArrayReadTagPath);

        var zero = await svc.ReadAsync("Program:Main.OneShots[0]");
        Assert.Equal(false, zero.Value);
    }

    /// <summary>A BOOL array element past the first DINT selects the right host word.</summary>
    [Fact]
    public async Task ProgramScopedBoolArrayElement_SecondWord()
    {
        var (svc, _) = await OpenAsync(Dints(0, 1 << 3, 0));
        Assert.Equal(true, (await svc.ReadAsync("Program:Main.OneShots[35]")).Value);
        Assert.Equal(false, (await svc.ReadAsync("Program:Main.OneShots[34]")).Value);
    }

    /// <summary>Program:Main.Vals[2] selects the third DINT.</summary>
    [Fact]
    public async Task ProgramScopedAtomicArrayElement_SelectsElement()
    {
        var (svc, fake) = await OpenAsync(Dints(10, 20, 30));

        var result = await svc.ReadAsync("Program:Main.Vals[2]");

        Assert.Equal(30, result.Value);
        Assert.Equal("DINT", result.DataType);
        Assert.Equal("Controller/Programs/Program[@Name='Main']/Tags/Tag[@Name='Vals']",
                     fake.LastByteArrayReadTagPath);
    }

    /// <summary>Program:Main.Cfg1.Gain reads the base tag and selects the member.</summary>
    [Fact]
    public async Task ProgramScopedMember_ReadsBaseTagAndSelectsMember()
    {
        var bytes = new byte[8];
        BitConverter.GetBytes(7).CopyTo(bytes, 0);
        BitConverter.GetBytes(2.5f).CopyTo(bytes, 4);
        var (svc, fake) = await OpenAsync(bytes);

        var result = await svc.ReadAsync("Program:Main.Cfg1.Gain");

        Assert.Equal(2.5f, result.Value);
        Assert.Equal("REAL", result.DataType);
        Assert.Equal("Controller/Programs/Program[@Name='Main']/Tags/Tag[@Name='Cfg1']",
                     fake.LastByteArrayReadTagPath);
    }

    /// <summary>Program:Main.Cfgs[1].Gain: element stride applies before the member offset.</summary>
    [Fact]
    public async Task ProgramScopedUdtArrayElementMember_SelectsMember()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(9).CopyTo(bytes, 8);
        BitConverter.GetBytes(1.5f).CopyTo(bytes, 12);
        var (svc, _) = await OpenAsync(bytes);

        Assert.Equal(1.5f, (await svc.ReadAsync("Program:Main.Cfgs[1].Gain")).Value);
        Assert.Equal(9, (await svc.ReadAsync("Program:Main.Cfgs[1].Speed")).Value);
    }

    /// <summary>Controller-scope Counts[3] reads the base tag and selects the element.</summary>
    [Fact]
    public async Task ControllerScopedArrayElement_ReadsBaseTagAndSelectsElement()
    {
        var (svc, fake) = await OpenAsync(Dints(1, 2, 3, 4));

        var result = await svc.ReadAsync("Counts[3]");

        Assert.Equal(4, result.Value);
        Assert.Equal("DINT", result.DataType);
        Assert.Equal("Controller/Tags/Tag[@Name='Counts']", fake.LastByteArrayReadTagPath);
    }

    /// <summary>Two-dimensional element Grid[1,2] is row-major over dims "2 3".</summary>
    [Fact]
    public async Task ControllerScopedTwoDimensionalElement_UsesRowMajorIndex()
    {
        var bytes = new byte[12];
        BitConverter.GetBytes((short)77).CopyTo(bytes, 10); // linear index 1*3+2 = 5
        var (svc, _) = await OpenAsync(bytes);

        Assert.Equal((short)77, (await svc.ReadAsync("Grid[1,2]")).Value);
        Assert.Equal((short)77, (await svc.ReadAsync("Grid[1][2]")).Value);
    }

    /// <summary>An index past the declared length is a typed TagNotFound, not Unknown.</summary>
    [Theory]
    [InlineData("Counts[4]")]
    [InlineData("Counts[0,1]")]
    [InlineData("Program:Main.Vals[3]")]
    [InlineData("Program:Main.Cfg1[0]")]
    public async Task ArrayElement_OutOfRangeOrWrongRank_ReturnsTagNotFound(string name)
    {
        var (svc, _) = await OpenAsync(new byte[64]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync(name));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    /// <summary>A nonexistent tag returns the typed TagNotFound kind for every path form.</summary>
    [Theory]
    [InlineData("Nope[1]")]
    [InlineData("Nope.Member")]
    [InlineData("Program:Main.Nope[1]")]
    [InlineData("Program:Main.Nope.Member")]
    [InlineData("Program:Missing.OneShots[1]")]
    public async Task NonexistentTag_ReturnsTagNotFoundKind(string name)
    {
        var (svc, _) = await OpenAsync(new byte[64]);

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync(name));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    /// <summary>A byte array shorter than the selected element is not decoded past its end.</summary>
    [Fact]
    public async Task ShortByteArray_IsRejectedNotOverrun()
    {
        var (svc, _) = await OpenAsync(Dints(1, 2));

        var ex = await Assert.ThrowsAsync<TagServiceException>(() => svc.ReadAsync("Counts[3]"));

        Assert.Equal(TagServiceErrorKind.Unknown, ex.Error.Kind);
    }
}
#endif
