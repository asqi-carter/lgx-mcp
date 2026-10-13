using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LogixMcp.Tests;

/// <summary>Tests for <see cref="EmulatorIdentity"/> (ListIdentity client).</summary>
public class EmulatorIdentityTests
{
    private static byte[] BuildReply(string name, byte major, byte minor)
    {
        var item = new List<byte>();
        void U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); item.AddRange(b); }
        U16(1);                                   // encap protocol version
        item.AddRange(new byte[16]);              // socket address
        U16(1); U16(14); U16(0x55);               // vendor, device type, product code
        item.Add(major); item.Add(minor);
        U16(0);                                   // status
        item.AddRange(new byte[] { 1, 2, 3, 4 }); // serial
        item.Add((byte)name.Length);
        item.AddRange(Encoding.ASCII.GetBytes(name));
        item.Add(3);                              // state

        var data = new List<byte>();
        data.AddRange(new byte[] { 1, 0 });       // item count
        data.AddRange(new byte[] { 0x0C, 0x00 }); // type id
        data.Add((byte)item.Count); data.Add((byte)(item.Count >> 8));
        data.AddRange(item);

        var reply = new byte[24 + data.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(reply, 0x0063);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), (ushort)data.Count);
        data.CopyTo(reply, 24);
        return reply;
    }

    [Fact]
    public void ParsesListIdentityReply_ProductNameAndRevision()
    {
        var info = EmulatorIdentity.ParseReply(BuildReply("Emulate 5580 Controller", 36, 11));

        Assert.Equal("Emulate 5580 Controller", info.ProductName);
        Assert.Equal(36, info.RevisionMajor);
        Assert.Equal(11, info.RevisionMinor);
        Assert.Equal("36.11", info.Revision);
        Assert.True(info.IsEmulator);
    }

    [Fact]
    public void NonEmulatorProduct_IsNotEmulator()
    {
        var info = EmulatorIdentity.ParseReply(BuildReply("1756-L83E/B", 33, 1));
        Assert.False(info.IsEmulator);
    }

    [Fact]
    public void TruncatedReply_ThrowsIdentityUnavailable()
    {
        var reply = BuildReply("Emulate 5580 Controller", 36, 11);
        var ex = Assert.Throws<EmulatorIdentityException>(() => EmulatorIdentity.ParseReply(reply.AsSpan(0, 40)));
        Assert.Equal(ErrorKinds.IdentityUnavailable, ex.Kind);
    }

    [Fact]
    public void WrongCommand_Throws()
    {
        var reply = BuildReply("X", 1, 0);
        reply[0] = 0x65;
        Assert.Throws<EmulatorIdentityException>(() => EmulatorIdentity.ParseReply(reply));
    }

    [Fact]
    public async Task ReadAsync_ConnectionRefused_ThrowsIdentityUnavailable()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        var ex = await Assert.ThrowsAsync<EmulatorIdentityException>(
            () => EmulatorIdentity.ReadAsync("127.0.0.1", port, TimeSpan.FromSeconds(2)));
        Assert.Equal(ErrorKinds.IdentityUnavailable, ex.Kind);
    }

    [Fact]
    public async Task ReadAsync_AgainstFakeServer_ReturnsIdentity()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var req = new byte[24];
            await s.ReadExactlyAsync(req);
            Assert.Equal(0x63, req[0]);
            await s.WriteAsync(BuildReply("Emulate 5580 Controller", 36, 11));
        });

        var info = await EmulatorIdentity.ReadAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));
        await server;
        l.Stop();
        Assert.Equal("Emulate 5580 Controller", info.ProductName);
    }
}
