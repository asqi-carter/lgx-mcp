using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

// ── EmulatorIdentity ──────────────────────────────────────────────────────────

/// <summary>Identity fields parsed from an EtherNet/IP ListIdentity reply.</summary>
/// <param name="ProductName">Product name, e.g. <c>Emulate 5580 Controller</c>.</param>
/// <param name="RevisionMajor">Major firmware revision.</param>
/// <param name="RevisionMinor">Minor firmware revision.</param>
/// <param name="VendorId">CIP vendor ID.</param>
/// <param name="DeviceType">CIP device type.</param>
/// <param name="ProductCode">CIP product code.</param>
/// <param name="SerialNumber">Device serial number.</param>
/// <param name="Status">
/// CIP Identity status word (0x3060 healthy, 0x3450 major recoverable fault on the Echo bench).
/// Defaults to 0 when the caller does not supply it.
/// </param>
public sealed record EmulatorIdentityInfo(
    string ProductName,
    int RevisionMajor,
    int RevisionMinor,
    int VendorId,
    int DeviceType,
    int ProductCode,
    uint SerialNumber,
    ushort Status = 0)
{
    /// <summary>Revision formatted as <c>major.minor</c>.</summary>
    public string Revision => $"{RevisionMajor}.{RevisionMinor}";

    /// <summary>True when the product name starts with <c>Emulate</c>.</summary>
    public bool IsEmulator =>
        ProductName.StartsWith("Emulate", StringComparison.Ordinal);
}

/// <summary>
/// Raised when the ListIdentity exchange fails (connect, I/O, timeout or malformed reply).
/// Distinct from a successful read of a non-emulator product, so callers can map it to
/// <see cref="ErrorKinds.IdentityUnavailable"/>.
/// </summary>
public sealed class EmulatorIdentityException : Exception
{
    /// <summary>Creates the exception with a message and optional cause.</summary>
    public EmulatorIdentityException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>Machine-readable error kind: always <see cref="ErrorKinds.IdentityUnavailable"/>.</summary>
    public string Kind => ErrorKinds.IdentityUnavailable;
}

/// <summary>
/// Raw EtherNet/IP ListIdentity (encapsulation command 0x63, TCP 44818) client.
/// Reusable by <c>logix_run_test</c> and <c>logix_doctor</c>.
/// </summary>
public static class EmulatorIdentity
{
    /// <summary>Standard EtherNet/IP explicit-messaging port.</summary>
    public const int DefaultPort = 44818;

    private const ushort ListIdentityCommand = 0x0063;
    private const ushort IdentityItemType = 0x000C;
    private const int HeaderLength = 24;

    /// <summary>Builds the 24-byte ListIdentity request (no data).</summary>
    public static byte[] BuildRequest()
    {
        var req = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(req, ListIdentityCommand);
        return req;
    }

    /// <summary>
    /// Parses a complete ListIdentity reply (24-byte encapsulation header plus data).
    /// </summary>
    /// <exception cref="EmulatorIdentityException">The reply is truncated or malformed.</exception>
    public static EmulatorIdentityInfo ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < HeaderLength)
            throw new EmulatorIdentityException("ListIdentity reply shorter than encapsulation header.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(reply) != ListIdentityCommand)
            throw new EmulatorIdentityException("Reply is not a ListIdentity response.");
        var status = BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]);
        if (status != 0)
            throw new EmulatorIdentityException($"ListIdentity returned encapsulation status 0x{status:X}.");

        var data = reply[HeaderLength..];
        // item count(2) + type(2) + length(2) + fixed identity fields up to name length byte.
        const int fixedBeforeName = 2 + 2 + 2 + 2 + 16 + 2 + 2 + 2 + 1 + 1 + 2 + 4;
        if (data.Length < fixedBeforeName + 1)
            throw new EmulatorIdentityException("ListIdentity reply truncated.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) < 1)
            throw new EmulatorIdentityException("ListIdentity reply contains no items.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != IdentityItemType)
            throw new EmulatorIdentityException("ListIdentity item is not a CIP Identity item.");

        var p = data[6..];                       // start of identity item body
        // p: protoVer(2) sockaddr(16) vendor(2) devType(2) prodCode(2) revMaj(1) revMin(1) status(2) serial(4) nameLen(1)
        var vendor  = BinaryPrimitives.ReadUInt16LittleEndian(p[18..]);
        var devType = BinaryPrimitives.ReadUInt16LittleEndian(p[20..]);
        var code    = BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
        var major   = p[24];
        var minor   = p[25];
        var status16 = BinaryPrimitives.ReadUInt16LittleEndian(p[26..]);
        var serial  = BinaryPrimitives.ReadUInt32LittleEndian(p[28..]);
        int nameLen = p[32];
        if (p.Length < 33 + nameLen)
            throw new EmulatorIdentityException("ListIdentity product name truncated.");
        var name = Encoding.ASCII.GetString(p.Slice(33, nameLen));
        return new EmulatorIdentityInfo(name, major, minor, vendor, devType, code, serial, status16);
    }

    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/>, sends ListIdentity and parses the reply.
    /// </summary>
    /// <exception cref="EmulatorIdentityException">Connection, I/O, timeout or parse failure.</exception>
    public static async Task<EmulatorIdentityInfo> ReadAsync(
        string host, int port = DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(BuildRequest(), cts.Token).ConfigureAwait(false);

            var header = new byte[HeaderLength];
            await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var full = new byte[HeaderLength + len];
            header.CopyTo(full, 0);
            await stream.ReadExactlyAsync(full.AsMemory(HeaderLength), cts.Token).ConfigureAwait(false);
            return ParseReply(full);
        }
        catch (EmulatorIdentityException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmulatorIdentityException($"ListIdentity to {host}:{port} timed out.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            throw new EmulatorIdentityException($"ListIdentity to {host}:{port} failed: {ex.Message}", ex);
        }
    }
}
