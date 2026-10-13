using System.Buffers.Binary;
using System.Text;

namespace LogixMcp.Tests;

/// <summary>Tests for <see cref="ControllerNameReader"/>: Get_Attribute_List class 0x64 controller name read.</summary>
public class ControllerNameReaderTests
{
    /// <summary>CIP reply with a length-prefixed name: 0x83, status 0, count 1, attr 1, status 0, UINT length, name.</summary>
    private static byte[] NameReply(string name, byte generalStatus = 0)
    {
        var n = Encoding.ASCII.GetBytes(name);
        var b = new List<byte> { 0x83, 0x00, generalStatus, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00 };
        b.Add((byte)n.Length); b.Add(0x00);
        b.AddRange(n);
        return [.. b];
    }

    private static byte[] Frame(byte[] cip)
    {
        var data = new byte[16 + cip.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(data, 16);
        var frame = new byte[24 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x006F);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)data.Length);
        data.CopyTo(frame, 24);
        return frame;
    }

    /// <summary>The bare request is Get_Attribute_List, class 0x64, instance 1, one attribute (1).</summary>
    [Fact]
    public void BuildCipRequest_GetAttributeList_Class64_Instance1_Attribute1()
    {
        Assert.Equal(
            new byte[] { 0x03, 0x02, 0x20, 0x64, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00 },
            ControllerNameReader.BuildCipRequest());
    }

    /// <summary>The routed request wraps the bare request in Unconnected_Send with the route path 1,0.</summary>
    [Fact]
    public void BuildRoutedRequest_WrapsInUnconnectedSend_WithRoute()
    {
        var req = ControllerNameReader.BuildRoutedRequest(ControllerNameReader.ParseRoute("1,0")!);
        Assert.Equal(
            new byte[]
            {
                0x52, 0x02, 0x20, 0x06, 0x24, 0x01, 0x0A, 0xF0, 0x0A, 0x00,
                0x03, 0x02, 0x20, 0x64, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00,
                0x01, 0x00, 0x01, 0x00,
            },
            req);
    }

    /// <summary>Route parsing accepts port,address pairs and rejects malformed paths.</summary>
    [Theory]
    [InlineData("1,0", new byte[] { 1, 0 })]
    [InlineData("1,4", new byte[] { 1, 4 })]
    [InlineData("1", null)]
    [InlineData("a,b", null)]
    [InlineData("", null)]
    public void ParseRoute_Cases(string path, byte[]? expected) =>
        Assert.Equal(expected, ControllerNameReader.ParseRoute(path));

    /// <summary>A reply built with a length-prefixed name parses to that name.</summary>
    [Fact]
    public void ParseCipReply_LengthPrefixedName_ReturnsName() =>
        Assert.Equal("Echo_PLC", ControllerNameReader.ParseCipReply(NameReply("Echo_PLC")));

    /// <summary>Path size is in 16-bit words: the 4-byte path 20 64 24 01 is 2 words.</summary>
    [Fact]
    public void BuildCipRequest_PathSizeIsWordsOfThePath()
    {
        var req = ControllerNameReader.BuildCipRequest();
        Assert.Equal(4, req[1] * 2);
        Assert.Equal(new byte[] { 0x20, 0x64, 0x24, 0x01 }, req[2..(2 + req[1] * 2)]);
    }

    /// <summary>The reply data an Echo 5580 returned to Get_Attribute_List attr 1 (bench, 2026-10-06), after the 0x83 header.</summary>
    [Fact]
    public void ParseCipReply_RecordedEchoReply_ReturnsName()
    {
        var data = new byte[] { 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x00 }
            .Concat(Encoding.ASCII.GetBytes("PLC_LN02_Template")).ToArray();
        byte[] reply = [0x83, 0x00, 0x00, 0x00, .. data];
        Assert.Equal("PLC_LN02_Template", ControllerNameReader.ParseCipReply(reply));
    }

    /// <summary>The name also parses out of a full SendRRData frame.</summary>
    [Fact]
    public void ParseSendRRDataReply_LengthPrefixedName_ReturnsName() =>
        Assert.Equal("Echo_PLC", ControllerNameReader.ParseSendRRDataReply(Frame(NameReply("Echo_PLC"))));

    /// <summary>Error status, truncation and a wrong service all yield null rather than throwing.</summary>
    [Fact]
    public void ParseCipReply_ErrorOrMalformed_ReturnsNull()
    {
        Assert.Null(ControllerNameReader.ParseCipReply(NameReply("X", generalStatus: 0x05)));
        Assert.Null(ControllerNameReader.ParseCipReply([0x83, 0x00, 0x00, 0x00, 0x01]));
        Assert.Null(ControllerNameReader.ParseCipReply(NameReply("Echo_PLC")[..^3]));
        Assert.Null(ControllerNameReader.ParseCipReply([0xD2, 0x00, 0x01, 0x00]));
        Assert.Null(ControllerNameReader.ParseSendRRDataReply([1, 2, 3]));
    }

    /// <summary>A failed read (nothing listening) gives a null name, not an exception.</summary>
    [Fact]
    public async Task ReadAsync_Unreachable_ReturnsNull()
    {
        var name = await ControllerNameReader.ReadAsync("127.0.0.1", "1,0", port: 1, timeout: TimeSpan.FromSeconds(1));
        Assert.Null(name);
        Assert.Null(await ControllerNameReader.ReadAsync("127.0.0.1", "bad", port: 1));
    }
}
