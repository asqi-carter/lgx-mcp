using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

// ── RoutedIdentity ────────────────────────────────────────────────────────────

/// <summary>Identity attributes returned by a CIP Identity Get_Attributes_All (class 1, instance 1).</summary>
/// <param name="VendorId">CIP vendor ID.</param>
/// <param name="DeviceType">CIP device type.</param>
/// <param name="ProductCode">CIP product code.</param>
/// <param name="RevisionMajor">Major firmware revision.</param>
/// <param name="RevisionMinor">Minor firmware revision.</param>
/// <param name="Status">Identity status word (e.g. 0x3060).</param>
/// <param name="Serial">Serial number, decoded as a little-endian UInt32 as CIP transmits it.</param>
/// <param name="ProductName">Product name, e.g. <c>Emulate 5580 Controller</c>.</param>
public sealed record RoutedIdentityInfo(
    ushort VendorId,
    ushort DeviceType,
    ushort ProductCode,
    byte RevisionMajor,
    byte RevisionMinor,
    ushort Status,
    uint Serial,
    string ProductName)
{
    /// <summary>Revision formatted as <c>major.minor</c>.</summary>
    public string Revision => $"{RevisionMajor}.{RevisionMinor}";
}

/// <summary>Result of a routed Identity read: CIP status plus the identity when the module answered.</summary>
/// <param name="GeneralStatus">CIP general status (0 = success).</param>
/// <param name="ExtendedStatus">First extended status word, when the reply carried one.</param>
/// <param name="Identity">Decoded identity; non-null only when <paramref name="GeneralStatus"/> is 0.</param>
/// <param name="TimedOut">True when no reply arrived within the per-module socket timeout.</param>
public sealed record RoutedIdentityResult(
    byte GeneralStatus,
    ushort? ExtendedStatus,
    RoutedIdentityInfo? Identity,
    bool TimedOut = false)
{
    /// <summary>General status 0x01 with extended status 0x0204: Unconnected Send timed out.</summary>
    public bool IsUnconnectedSendTimeout => GeneralStatus == 0x01 && ExtendedStatus == 0x0204;

    /// <summary>
    /// <c>responding</c> (status 0), <c>not_responding</c> (0x01/0x0204 or socket timeout),
    /// otherwise <c>error</c>.
    /// </summary>
    public string Connection =>
        TimedOut || IsUnconnectedSendTimeout ? "not_responding"
        : GeneralStatus == 0 ? "responding"
        : "error";

    /// <summary>Human-readable CIP status text.</summary>
    public string Detail =>
        TimedOut ? "no reply within the per-module timeout (module absent or not responding)"
        : IsUnconnectedSendTimeout ? "0x01/0x0204 Unconnected Send timed out (module absent or not responding)"
        : GeneralStatus == 0 ? "CIP general status 0x00 success"
        : ExtendedStatus is { } ext ? $"CIP general status 0x{GeneralStatus:X2}, extended status 0x{ext:X4}"
        : $"CIP general status 0x{GeneralStatus:X2}";
}

/// <summary>
/// Raised when the routed Identity exchange fails at the transport level (connect, I/O,
/// refused session) or the reply is malformed. A module that is merely absent is NOT an
/// exception; it is a <see cref="RoutedIdentityResult"/> with <c>not_responding</c>.
/// </summary>
public sealed class RoutedIdentityException : Exception
{
    /// <summary>Creates the exception with a message and optional cause.</summary>
    public RoutedIdentityException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Read-only routed CIP Identity client. Opens an EtherNet/IP session to a controller
/// (TCP 44818), then sends an Unconnected Send (service 0x52, class 6 instance 1) that carries
/// Get_Attributes_All on Identity (class 1, instance 1) along a route through the controller to a
/// module. It sends no other CIP service. Sits beside <see cref="EmulatorIdentity"/>.
/// </summary>
public static class RoutedIdentity
{
    private const ushort RegisterSessionCommand = 0x0065;
    private const ushort UnregisterSessionCommand = 0x0066;
    private const ushort SendRRDataCommand = 0x006F;
    private const ushort UnconnectedDataItem = 0x00B2;
    private const int HeaderLength = 24;

    private const byte UnconnectedSend = 0x52;
    private const byte GetAttributesAll = 0x01;

    /// <summary>Default per-module reply timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Route for a module in a backplane slot: <c>[0x01, slot]</c> (port 1 plus slot).</summary>
    public static byte[] BackplaneRoute(byte slot) => [0x01, slot];

    /// <summary>
    /// Route for a module reached over Ethernet port 2: <c>[0x12, len, ascii ip, pad to even]</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The address is empty, longer than 255 bytes or not ASCII.</exception>
    public static byte[] EthernetRoute(string ip)
    {
        var ascii = Encoding.ASCII.GetBytes(ip ?? "");
        if (ascii.Length == 0 || ascii.Length > 255 || Encoding.ASCII.GetString(ascii) != ip)
            throw new ArgumentException("Ethernet route address must be 1-255 ASCII characters.", nameof(ip));
        var route = new List<byte> { 0x12, (byte)ascii.Length };
        route.AddRange(ascii);
        if (route.Count % 2 != 0) route.Add(0x00);
        return route.ToArray();
    }

    /// <summary>Builds the 28-byte RegisterSession request.</summary>
    public static byte[] BuildRegisterSession()
    {
        var req = new byte[HeaderLength + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(req, RegisterSessionCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(HeaderLength), 1); // protocol version
        return req;
    }

    /// <summary>Builds the 24-byte UnRegisterSession message.</summary>
    public static byte[] BuildUnregisterSession(uint sessionHandle)
    {
        var req = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(req, UnregisterSessionCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), sessionHandle);
        return req;
    }

    /// <summary>
    /// Builds a SendRRData frame (CPF null address item plus unconnected data item) carrying an
    /// Unconnected Send that wraps Get_Attributes_All on Identity (class 1, instance 1).
    /// </summary>
    /// <param name="sessionHandle">Session handle returned by RegisterSession.</param>
    /// <param name="routePath">Route path bytes, e.g. <see cref="BackplaneRoute"/> or <see cref="EthernetRoute"/>.</param>
    /// <exception cref="ArgumentException">The route path is empty, odd-length or too long.</exception>
    public static byte[] BuildRequest(uint sessionHandle, ReadOnlySpan<byte> routePath)
    {
        if (routePath.Length == 0 || routePath.Length % 2 != 0 || routePath.Length > 2 * 255)
            throw new ArgumentException("Route path must be a non-empty, even-length byte sequence.", nameof(routePath));

        // Embedded request: Get_Attributes_All, path size 2 words, class 1 instance 1.
        ReadOnlySpan<byte> embedded = [GetAttributesAll, 0x02, 0x20, 0x01, 0x24, 0x01];

        var cip = new List<byte>
        {
            UnconnectedSend, 0x02, 0x20, 0x06, 0x24, 0x01, // service, path: Connection Manager class 6 instance 1
            0x0A, 0xF0,                                    // priority/time tick, timeout ticks
            (byte)embedded.Length, 0x00,                   // embedded message size
        };
        cip.AddRange(embedded.ToArray());                  // (even length: no pad byte)
        cip.Add((byte)(routePath.Length / 2));             // route path size in words
        cip.Add(0x00);                                     // reserved
        cip.AddRange(routePath.ToArray());

        // interface handle(4) timeout(2) itemCount(2) nullAddr(4) dataItem hdr(4) + cip
        var data = new byte[4 + 2 + 2 + 4 + 4 + cip.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), UnconnectedDataItem);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)cip.Count);
        cip.CopyTo(data, 16);

        var req = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(req, SendRRDataCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), sessionHandle);
        data.CopyTo(req, HeaderLength);
        return req;
    }

    /// <summary>
    /// Parses a complete SendRRData reply (24-byte encapsulation header plus data) to a routed
    /// Identity request. A CIP error (e.g. 0x01/0x0204) is returned as a result, not thrown.
    /// </summary>
    /// <exception cref="RoutedIdentityException">The reply is truncated or malformed.</exception>
    public static RoutedIdentityResult ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < HeaderLength)
            throw new RoutedIdentityException("SendRRData reply shorter than encapsulation header.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(reply) != SendRRDataCommand)
            throw new RoutedIdentityException("Reply is not a SendRRData response.");
        var encapStatus = BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]);
        if (encapStatus != 0)
            throw new RoutedIdentityException($"SendRRData returned encapsulation status 0x{encapStatus:X}.");

        var data = reply[HeaderLength..];
        if (data.Length < 8)
            throw new RoutedIdentityException("SendRRData reply truncated.");
        int items = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var p = data[8..];
        for (var i = 0; i < items; i++)
        {
            if (p.Length < 4) throw new RoutedIdentityException("SendRRData item list truncated.");
            int type = BinaryPrimitives.ReadUInt16LittleEndian(p);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
            if (p.Length < 4 + len) throw new RoutedIdentityException("SendRRData item truncated.");
            if (type == UnconnectedDataItem)
                return ParseCipReply(p.Slice(4, len));
            p = p[(4 + len)..];
        }
        throw new RoutedIdentityException("SendRRData reply has no unconnected data item.");
    }

    private static RoutedIdentityResult ParseCipReply(ReadOnlySpan<byte> cip)
    {
        // service(1) reserved(1) generalStatus(1) extStatusWords(1) extStatus(2*n) data...
        if (cip.Length < 4)
            throw new RoutedIdentityException("CIP reply truncated.");
        var service = cip[0];
        if (service != (GetAttributesAll | 0x80) && service != (UnconnectedSend | 0x80))
            throw new RoutedIdentityException($"Unexpected CIP reply service 0x{service:X2}.");
        var general = cip[2];
        int extWords = cip[3];
        var offset = 4 + extWords * 2;
        if (cip.Length < offset)
            throw new RoutedIdentityException("CIP reply extended status truncated.");
        ushort? ext = extWords > 0 ? BinaryPrimitives.ReadUInt16LittleEndian(cip[4..]) : null;
        if (general != 0)
            return new RoutedIdentityResult(general, ext, null);

        // vendor(2) devType(2) prodCode(2) revMaj(1) revMin(1) status(2) serial(4) nameLen(1) name
        var p = cip[offset..];
        if (p.Length < 15)
            throw new RoutedIdentityException("Identity reply truncated.");
        int nameLen = p[14];
        if (p.Length < 15 + nameLen)
            throw new RoutedIdentityException("Identity product name truncated.");
        var identity = new RoutedIdentityInfo(
            VendorId: BinaryPrimitives.ReadUInt16LittleEndian(p),
            DeviceType: BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            ProductCode: BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            RevisionMajor: p[6],
            RevisionMinor: p[7],
            Status: BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            Serial: BinaryPrimitives.ReadUInt32LittleEndian(p[10..]),
            ProductName: Encoding.ASCII.GetString(p.Slice(15, nameLen)));
        return new RoutedIdentityResult(0, ext, identity);
    }

    /// <summary>
    /// Registers a session with <paramref name="host"/>:<paramref name="port"/>, sends one routed
    /// Identity read along <paramref name="routePath"/>, then sends UnRegisterSession. A reply
    /// that does not arrive within <paramref name="timeout"/> (default 2 s) maps to a
    /// <c>not_responding</c> result with <see cref="RoutedIdentityResult.TimedOut"/> set.
    /// </summary>
    /// <exception cref="RoutedIdentityException">Connection, I/O, refused session or parse failure.</exception>
    public static async Task<RoutedIdentityResult> ReadAsync(
        string host, ReadOnlyMemory<byte> routePath, int port = EmulatorIdentity.DefaultPort,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(BuildRegisterSession(), cts.Token).ConfigureAwait(false);
            var regReply = await ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt16LittleEndian(regReply) != RegisterSessionCommand
                || BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(8)) != 0)
                throw new RoutedIdentityException("RegisterSession was refused.");
            var handle = BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(4));

            RoutedIdentityResult result;
            try
            {
                await stream.WriteAsync(BuildRequest(handle, routePath.Span), cts.Token).ConfigureAwait(false);
                var reply = await ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
                result = ParseReply(reply);
            }
            finally
            {
                // Best-effort polite close; failure is irrelevant.
                try { await stream.WriteAsync(BuildUnregisterSession(handle), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* ignore */ }
            }
            return result;
        }
        catch (RoutedIdentityException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RoutedIdentityResult(0x01, null, null, TimedOut: true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return new RoutedIdentityResult(0x01, null, null, TimedOut: true);
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            throw new RoutedIdentityException($"Routed Identity read from {host}:{port} failed: {ex.Message}", ex);
        }
    }

    internal static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        var full = new byte[HeaderLength + len];
        header.CopyTo(full, 0);
        await stream.ReadExactlyAsync(full.AsMemory(HeaderLength), ct).ConfigureAwait(false);
        return full;
    }
}

/// <summary>
/// Reads module identities one routed request at a time. Implementations are sequential and
/// read-only; tests inject a responder fake.
/// </summary>
public interface IModuleIdentityReader : IAsyncDisposable
{
    /// <summary>
    /// Sends one routed Identity read along <paramref name="routePath"/>. A module that does not
    /// answer within <paramref name="timeout"/> yields a result with
    /// <see cref="RoutedIdentityResult.TimedOut"/>; transport failures throw
    /// <see cref="RoutedIdentityException"/>.
    /// </summary>
    Task<RoutedIdentityResult> ReadAsync(byte[] routePath, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// One EtherNet/IP session to a controller (a single RegisterSession) over which many routed
/// Identity reads are sent sequentially, then UnRegisterSession on dispose. After a socket timeout
/// the connection is discarded (a late reply would desynchronise it) and re-registered on the next read.
/// </summary>
public sealed class RoutedIdentitySession : IModuleIdentityReader
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private uint _handle;

    private RoutedIdentitySession(string host, int port) { _host = host; _port = port; }

    /// <summary>Opens the TCP connection and registers the session.</summary>
    /// <exception cref="RoutedIdentityException">Connect, I/O or refused session.</exception>
    public static async Task<RoutedIdentitySession> OpenAsync(
        string host, int port = EmulatorIdentity.DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var session = new RoutedIdentitySession(host, port);
        try
        {
            await session.ConnectAsync(timeout ?? RoutedIdentity.DefaultTimeout, ct).ConfigureAwait(false);
            return session;
        }
        catch (Exception ex) when (ex is RoutedIdentityException) { await session.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException
                                      or ObjectDisposedException or OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new RoutedIdentityException($"Cannot register an EtherNet/IP session with {host}:{port}: {ex.Message}", ex);
        }
    }

    private async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        _stream = _client.GetStream();
        await _stream.WriteAsync(RoutedIdentity.BuildRegisterSession(), cts.Token).ConfigureAwait(false);
        var reg = await RoutedIdentity.ReadFrameAsync(_stream, cts.Token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt16LittleEndian(reg) != 0x0065
            || BinaryPrimitives.ReadUInt32LittleEndian(reg.AsSpan(8)) != 0)
            throw new RoutedIdentityException("RegisterSession was refused.");
        _handle = BinaryPrimitives.ReadUInt32LittleEndian(reg.AsSpan(4));
    }

    /// <inheritdoc />
    public async Task<RoutedIdentityResult> ReadAsync(byte[] routePath, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            if (_stream is null) await ConnectAsync(timeout, ct).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await _stream!.WriteAsync(RoutedIdentity.BuildRequest(_handle, routePath), cts.Token).ConfigureAwait(false);
            var reply = await RoutedIdentity.ReadFrameAsync(_stream, cts.Token).ConfigureAwait(false);
            return RoutedIdentity.ParseReply(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Discard();
            return new RoutedIdentityResult(0x01, null, null, TimedOut: true);
        }
        catch (RoutedIdentityException) { Discard(); throw; }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            Discard();
            throw new RoutedIdentityException($"Routed Identity read from {_host}:{_port} failed: {ex.Message}", ex);
        }
    }

    private void Discard()
    {
        try { _client?.Dispose(); } catch (Exception) { /* ignore */ }
        _client = null;
        _stream = null;
    }

    /// <summary>Sends UnRegisterSession (best effort) and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try { await _stream.WriteAsync(RoutedIdentity.BuildUnregisterSession(_handle), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* ignore */ }
        }
        Discard();
    }
}
