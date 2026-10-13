using System.Buffers.Binary;

namespace LogixMcp.Tests;

/// <summary>Tests for <see cref="FaultRecordDecoder"/>.</summary>
public class FaultRecordDecoderTests
{
    /// <summary>
    /// Real Get_Attributes_All reply of CIP class 0x73 instance 1 (144 bytes), recorded fixture captured from
    /// the Echo controller faulted by an array-subscript fault (4/20).
    /// </summary>
    internal const string BenchS32p112Hex =
        "1000000000000000010000004A318E8DBE2303000400140001000000534E00002A8C0000797A040035F30E0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Builds a synthetic record on the real layout: UINT32 at 0 (0x10 on the bench), UINT32 at 4,
    /// count@8, timestamp@12, type@20, code@22, then <paramref name="extraDwords"/> of extended info from 24.
    /// </summary>
    private static byte[] Record(uint count, ulong ts, ushort type, ushort code, int extraDwords = 2)
    {
        var b = new byte[24 + extraDwords * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), count);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), ts);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), code);
        return b;
    }

    [Fact]
    public void AllZeros_NotFaulted()
    {
        Assert.Null(FaultRecordDecoder.Decode(new byte[40]));
    }

    [Fact]
    public void ParsesCountTimestampTypeCode_AndKeepsRaw()
    {
        var bytes = Record(1, 1_790_000_000_123_456UL, 4, 20);
        var fault = FaultRecordDecoder.Decode(bytes);

        Assert.NotNull(fault);
        Assert.Equal(1u, fault!.Count);
        Assert.Equal(1_790_000_000_123_456UL, fault.Timestamp);
        Assert.Equal(4, fault.Type);
        Assert.Equal(20, fault.Code);
        Assert.Equal(Convert.ToHexString(bytes), fault.Raw);
    }

    [Fact]
    public void UnknownCode_NameUnknown_PointsAtPublication()
    {
        var fault = FaultRecordDecoder.Decode(Record(1, 5, 99, 77));
        Assert.Equal("unknown", fault!.Name);
        Assert.Equal("see 1756-PM014 for type 99 code 77", fault.Meaning);
    }

    [Fact]
    public void DecodesRealBenchBytes_S32p112_Type4Code20()
    {
        // Exact reply bytes from bench S32-p112; not synthesized.
        var bytes = Convert.FromHexString(BenchS32p112Hex);
        Assert.Equal(144, bytes.Length);
        var fault = FaultRecordDecoder.Decode(bytes);

        Assert.NotNull(fault);
        Assert.Equal(4, fault!.Type);
        Assert.Equal(20, fault.Code);
        Assert.Equal(1u, fault.Count);
        Assert.Equal(883_726_255_796_554UL, fault.Timestamp);
        Assert.NotEqual("unknown", fault.Name);
        Assert.Contains("1756-PM014", fault.Meaning);
        Assert.Equal(BenchS32p112Hex, fault.Raw);
        Assert.StartsWith("10000000" + "00000000", fault.Raw);
    }

    [Fact]
    public void TimestampIsRawMicroseconds_NotConverted()
    {
        // Epoch is not bench-verified (reads as 1998 if taken as Unix µs), so the value is reported raw.
        var fault = FaultRecordDecoder.Decode(Convert.FromHexString(BenchS32p112Hex))!;
        Assert.IsType<ulong>(fault.Timestamp);
        Assert.Equal(883_726_255_796_554UL, fault.Timestamp);

        var json = System.Text.Json.JsonSerializer.Serialize(fault);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ts = doc.RootElement.GetProperty("timestamp");
        Assert.Equal(System.Text.Json.JsonValueKind.Number, ts.ValueKind);
        Assert.Equal(883_726_255_796_554UL, ts.GetUInt64());
    }

    [Fact]
    public void UnknownCode_RawKept_NameUnknown()
    {
        var bytes = Record(2, 42, 4, 99);
        var fault = FaultRecordDecoder.Decode(bytes);

        Assert.Equal("unknown", fault!.Name);
        Assert.Equal("see 1756-PM014 for type 4 code 99", fault.Meaning);
        Assert.Equal(Convert.ToHexString(bytes), fault.Raw);
    }

    [Fact]
    public void Table_HasExactlyTheNineListedEntries_EachCitingPublication()
    {
        var expected = new HashSet<(int, int)>
            { (4, 20), (4, 16), (4, 21), (4, 31), (4, 34), (4, 42), (6, 1), (1, 1), (3, 16) };

        Assert.True(expected.SetEquals(FaultRecordDecoder.Table.Keys));
        foreach (var (key, entry) in FaultRecordDecoder.Table)
        {
            Assert.Contains("1756-PM014", entry.Meaning);
            Assert.Contains("Major, Minor and I/O Faults", entry.Meaning);
            var fault = FaultRecordDecoder.Decode(Record(1, 1, (ushort)key.Type, (ushort)key.Code));
            Assert.Equal(entry.Name, fault!.Name);
        }
    }

    [Fact]
    public void ShortReply_Unparsed_RawKept()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };
        var fault = FaultRecordDecoder.Decode(bytes);

        Assert.NotNull(fault);
        Assert.Equal("unparsed", fault!.Name);
        Assert.True(fault.IsUnparsed);
        Assert.Equal(Convert.ToHexString(bytes), fault.Raw);
        Assert.Equal(0, fault.Type);
        Assert.Equal(0, fault.Code);
        Assert.Equal(0u, fault.Count);
    }

    [Fact]
    public void ExactlyTwentyFourBytes_IsParsed_WithEmptyExtended()
    {
        var fault = FaultRecordDecoder.Decode(Record(1, 7, 4, 20, extraDwords: 0));
        Assert.Equal("array subscript or control structure invalid", fault!.Name);
        Assert.Equal("", fault.Extended);
    }

    [Fact]
    public void Offsets0And4_AreNotLabeled_StayInsideRaw_ExtendedFrom24()
    {
        var bytes = Record(3, 9, 4, 16, extraDwords: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x11223344);
        bytes[24] = 0xEE;

        var fault = FaultRecordDecoder.Decode(bytes)!;
        Assert.Equal(3u, fault.Count);
        Assert.StartsWith("DDCCBBAA44332211", fault.Raw);
        Assert.Equal("EE000000", fault.Extended);
    }

    [Fact]
    public void CountZero_NotFaulted()
    {
        var bytes = Record(0, 9, 4, 20);
        bytes[0] = 1;
        Assert.Null(FaultRecordDecoder.Decode(bytes));
    }
}

/// <summary>Tests for <see cref="IdentityStatus"/> and the ListIdentity status word.</summary>
public class IdentityStatusTests
{
    [Fact]
    public void MajorRecoverableBit_Faulted()
    {
        Assert.True(IdentityStatus.IsFaulted(0x3450));
        Assert.False(IdentityStatus.IsFaulted(0x3060));
        Assert.Equal("0x3450", IdentityStatus.Format(0x3450));
    }

    [Fact]
    public void ListIdentityReply_StatusWordIsParsed()
    {
        var item = new List<byte>();
        void U16(int v) { item.Add((byte)v); item.Add((byte)(v >> 8)); }
        U16(1); item.AddRange(new byte[16]); U16(1); U16(14); U16(0x55);
        item.Add(38); item.Add(11);
        U16(0x3450);
        item.AddRange(new byte[] { 1, 2, 3, 4 });
        var name = "Emulate 5580 Controller";
        item.Add((byte)name.Length); item.AddRange(System.Text.Encoding.ASCII.GetBytes(name)); item.Add(3);

        var data = new List<byte> { 1, 0, 0x0C, 0 , (byte)item.Count, (byte)(item.Count >> 8) };
        data.AddRange(item);
        var reply = new byte[24 + data.Count];
        reply[0] = 0x63;
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), (ushort)data.Count);
        data.CopyTo(reply, 24);

        Assert.Equal((ushort)0x3450, EmulatorIdentity.ParseReply(reply).Status);
    }
}

/// <summary>Tests for <see cref="IdentityStatus.Decode"/>.</summary>
public class IdentityStatusDecodeTests
{
    [Fact]
    public void Healthy_0x3060_RunRemoteNoFlag()
    {
        var w = IdentityStatus.Decode(0x3060);
        Assert.Equal("run", w.DeviceStatus);
        Assert.Equal("0110", w.DeviceStatusBits);
        Assert.Empty(w.Faults);
        Assert.Equal("remote", w.Keyswitch);
        Assert.Equal("none", w.ModeFlag);
        Assert.Equal("Rockwell Logix Designer help, Access the ControllerDevice object, Status", w.Source);
    }

    [Fact]
    public void Faulted_0x3450_FaultedModesRecoverableMajor()
    {
        var w = IdentityStatus.Decode(0x3450);
        Assert.Equal("faultedModes", w.DeviceStatus);
        Assert.Equal("0101", w.DeviceStatusBits);
        Assert.Equal(new[] { "recoverableMajor" }, w.Faults);
        Assert.Equal("remote", w.Keyswitch);
        Assert.Equal("none", w.ModeFlag);
    }

    [Fact]
    public void ModeFlags_AllFourValues()
    {
        Assert.Equal("none", IdentityStatus.Decode(0x3060).ModeFlag);
        Assert.Equal("changingModes", IdentityStatus.Decode(0x3060 | 0x4000).ModeFlag);
        Assert.Equal("debugMode", IdentityStatus.Decode(0x3060 | 0x8000).ModeFlag);
        Assert.Equal("unknown", IdentityStatus.Decode(unchecked((ushort)(0x3060 | 0xC000))).ModeFlag);
    }

    [Fact]
    public void Keyswitch_AllFourValues()
    {
        Assert.Equal("unknown", IdentityStatus.Decode(0x0060).Keyswitch);
        Assert.Equal("run", IdentityStatus.Decode(0x1060).Keyswitch);
        Assert.Equal("program", IdentityStatus.Decode(0x2060).Keyswitch);
        Assert.Equal("remote", IdentityStatus.Decode(0x3060).Keyswitch);
    }

    [Fact]
    public void ReservedDeviceNibble_IsReserved()
    {
        var a = IdentityStatus.Decode(0x0000);
        Assert.Equal("reserved", a.DeviceStatus);
        Assert.Equal("0000", a.DeviceStatusBits);
        var b = IdentityStatus.Decode(0x0020);
        Assert.Equal("reserved", b.DeviceStatus);
        Assert.Equal("0010", b.DeviceStatusBits);
    }
}

/// <summary>Frame-level tests for <see cref="ControllerFaultReader"/> (no network).</summary>
public class ControllerFaultReaderTests
{
    [Fact]
    public void Request_IsGetAttributesAllOnClass0x73Instance1_AndNothingElse()
    {
        var frame = ControllerFaultReader.BuildSendRRData(0x1234);
        Assert.Equal(0x6F, frame[0]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x20, 0x73, 0x24, 0x01 }, frame[^6..]);
    }

    [Fact]
    public void ParsesReply_ReturnsAttributeData()
    {
        var payload = new byte[] { 9, 8, 7, 6 };
        var cip = new List<byte> { 0x81, 0, 0, 0 };
        cip.AddRange(payload);
        var data = new List<byte> { 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0xB2, 0, (byte)cip.Count, 0 };
        data.AddRange(cip);
        var reply = new byte[24 + data.Count];
        reply[0] = 0x6F;
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), (ushort)data.Count);
        data.CopyTo(reply, 24);

        Assert.Equal(payload, ControllerFaultReader.ParseSendRRDataReply(reply));
    }

    [Fact]
    public void CipErrorStatus_Throws()
    {
        var cip = new byte[] { 0x81, 0, 0x05, 0 };
        var data = new List<byte> { 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0xB2, 0, 4, 0 };
        data.AddRange(cip);
        var reply = new byte[24 + data.Count];
        reply[0] = 0x6F;
        data.CopyTo(reply, 24);
        Assert.Throws<FaultRecordException>(() => ControllerFaultReader.ParseSendRRDataReply(reply));
    }
}
