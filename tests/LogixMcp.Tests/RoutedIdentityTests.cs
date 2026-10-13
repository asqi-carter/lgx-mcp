using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace LogixMcp.Tests;

/// <summary>Tests for <see cref="RoutedIdentity"/>. Hex fixtures are the bench bytes verbatim.</summary>
public class RoutedIdentityTests
{
    private const uint Handle40 = 0x400000E1; // session handle bytes e1 00 00 40
    private const uint Handle40E7 = 0x400000E7;

    private const string RegisterReply =
        "65000400e10000400000000000000000000000000000000001000000";
    private const string Slot0Request =
        "6f002400e100004000000000000000000000000000000000000000000000020000000000b20014005202200624010af0060001022001240101000100";
    private const string Slot1Request =
        "6f002400e100004000000000000000000000000000000000000000000000020000000000b20014005202200624010af0060001022001240101000101";
    private const string Slot0Reply =
        "6f003a00e100004000000000000000000000000000000000000000000000020000000000b2002a008100000001000e002001260b60306db41f6017456d756c617465203535383020436f6e74726f6c6c6572";
    private const string Slot1Reply =
        "6f001800e100004000000000000000000000000000000000000000000000020000000000b2000800d200010104020100";
    private const string EthUnreachableRequest =
        "6f002e00e700004000000000000000000000000000000000000000000000020000000000b2001e005202200624010af006000102200124010600120a3139322e302e322e3130";
    private const string EthUnreachableReply =
        "6f001800e700004000000000000000000000000000000000000000000000020000000000b2000800d200010104020600";
    private const string EthLoopbackRequest =
        "6f002e00e700004000000000000000000000000000000000000000000000020000000000b2001e005202200624010af00600010220012401060012093132372e302e302e3100";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void BuildRequest_BackplaneSlot0_MatchesBenchBytes()
    {
        var req = RoutedIdentity.BuildRequest(Handle40, RoutedIdentity.BackplaneRoute(0));
        Assert.Equal(Hex(Slot0Request), req);
    }

    [Fact]
    public void BuildRequest_BackplaneSlot1_MatchesBenchBytes()
    {
        var req = RoutedIdentity.BuildRequest(Handle40, RoutedIdentity.BackplaneRoute(1));
        Assert.Equal(Hex(Slot1Request), req);
    }

    [Fact]
    public void BuildRequest_EthernetPort_Ip_MatchesBenchBytes()
    {
        // 192.0.2.10 is 10 chars (no pad); 127.0.0.1 is 9 chars (one pad byte).
        Assert.Equal(Hex(EthUnreachableRequest),
            RoutedIdentity.BuildRequest(Handle40E7, RoutedIdentity.EthernetRoute("192.0.2.10")));
        Assert.Equal(Hex(EthLoopbackRequest),
            RoutedIdentity.BuildRequest(Handle40E7, RoutedIdentity.EthernetRoute("127.0.0.1")));
    }

    [Fact]
    public void ParseReply_Present_DecodesIdentity()
    {
        var r = RoutedIdentity.ParseReply(Hex(Slot0Reply));

        Assert.Equal(0, r.GeneralStatus);
        Assert.Equal("responding", r.Connection);
        var id = Assert.IsType<RoutedIdentityInfo>(r.Identity);
        Assert.Equal(1, id.VendorId);
        Assert.Equal(0x0e, id.DeviceType);
        Assert.Equal(288, id.ProductCode);
        Assert.Equal("38.11", id.Revision);
        Assert.Equal(0x3060, id.Status);
        Assert.Equal(0x601fb46dU, id.Serial); // bytes 6d b4 1f 60, little-endian (pycomm3: 601fb46d)
        Assert.Equal("Emulate 5580 Controller", id.ProductName);
    }

    [Fact]
    public void ParseReply_Absent_NotResponding_0x01_0x0204()
    {
        foreach (var hex in new[] { Slot1Reply, EthUnreachableReply })
        {
            var r = RoutedIdentity.ParseReply(Hex(hex));
            Assert.Equal(0x01, r.GeneralStatus);
            Assert.Equal((ushort)0x0204, r.ExtendedStatus);
            Assert.Null(r.Identity);
            Assert.True(r.IsUnconnectedSendTimeout);
            Assert.Equal("not_responding", r.Connection);
            Assert.Contains("0x01/0x0204", r.Detail);
        }
    }

    [Fact]
    public void ParseReply_OtherStatus_IsError()
    {
        var bytes = Hex(Slot1Reply);
        var cipStart = Array.IndexOf(bytes, (byte)0xd2);
        bytes[cipStart + 2] = 0x05;
        var r = RoutedIdentity.ParseReply(bytes);
        Assert.Equal("error", r.Connection);
    }

    [Fact]
    public void ParseReply_Truncated_Throws()
    {
        Assert.Throws<RoutedIdentityException>(() => RoutedIdentity.ParseReply(Hex(Slot0Reply)[..40]));
        Assert.Throws<RoutedIdentityException>(() => RoutedIdentity.ParseReply(Hex(RegisterReply)));
    }

    [Fact]
    public void EthernetRoute_PadsToEven()
    {
        Assert.Equal(new byte[] { 0x12, 3, (byte)'1', (byte)'.', (byte)'2', 0 }, RoutedIdentity.EthernetRoute("1.2"));
        Assert.Equal(new byte[] { 0x12, 4, (byte)'1', (byte)'.', (byte)'2', (byte)'3' }, RoutedIdentity.EthernetRoute("1.23"));
    }

    [Fact]
    public async Task ReadAsync_FakeController_RegistersReadsAndUnregisters()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<ushort>();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            var h = new byte[24];
            await s.ReadExactlyAsync(h);
            await s.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(2))]);
            seen.Add(BinaryPrimitives.ReadUInt16LittleEndian(h));
            await s.WriteAsync(Hex(RegisterReply));
            await s.ReadExactlyAsync(h);
            await s.ReadExactlyAsync(new byte[BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(2))]);
            seen.Add(BinaryPrimitives.ReadUInt16LittleEndian(h));
            await s.WriteAsync(Hex(Slot0Reply));
            await s.ReadExactlyAsync(h);
            seen.Add(BinaryPrimitives.ReadUInt16LittleEndian(h));
        });

        var r = await RoutedIdentity.ReadAsync("127.0.0.1", RoutedIdentity.BackplaneRoute(0), port);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();

        Assert.Equal("responding", r.Connection);
        Assert.Equal(new ushort[] { 0x65, 0x6F, 0x66 }, seen);
    }

    [Fact]
    public async Task ReadAsync_NoReply_MapsToNotResponding()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await Task.Delay(1500); // never answers
        });

        var r = await RoutedIdentity.ReadAsync(
            "127.0.0.1", RoutedIdentity.BackplaneRoute(0), port, TimeSpan.FromMilliseconds(300));
        listener.Stop();

        Assert.True(r.TimedOut);
        Assert.Equal("not_responding", r.Connection);
    }
}
