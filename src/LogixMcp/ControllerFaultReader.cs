using System.Buffers.Binary;
using System.Net.Sockets;

// ── ControllerFaultReader ─────────────────────────────────────────────────────

/// <summary>
/// Small EtherNet/IP explicit-messaging client (TCP 44818, RegisterSession then unconnected SendRRData)
/// that issues one read-only CIP <c>Get_Attributes_All (0x01)</c> on class 0x73 instance 1, the
/// controller fault record. Sits beside <see cref="EmulatorIdentity"/>; it needs no session.
/// It sends no write service and no mode change. The request goes straight to the target address
/// (no backplane routing), as on the Echo bench.
/// </summary>
public static class ControllerFaultReader
{
    private const ushort RegisterSessionCommand = 0x0065;
    private const ushort UnregisterSessionCommand = 0x0066;
    private const ushort SendRRDataCommand = 0x006F;
    private const int HeaderLength = 24;

    /// <summary>CIP service Get_Attributes_All.</summary>
    public const byte GetAttributesAll = 0x01;

    /// <summary>Fault record class.</summary>
    public const byte FaultClass = 0x73;

    /// <summary>Builds the 28-byte RegisterSession request.</summary>
    public static byte[] BuildRegisterSession()
    {
        var req = new byte[HeaderLength + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(req, RegisterSessionCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(HeaderLength), 1); // protocol version
        return req;
    }

    /// <summary>Builds the CIP request: Get_Attributes_All, path class 0x73 instance 1.</summary>
    public static byte[] BuildCipRequest() =>
        [GetAttributesAll, 0x02, 0x20, FaultClass, 0x24, 0x01];

    /// <summary>Builds an unconnected SendRRData frame carrying <see cref="BuildCipRequest"/>.</summary>
    /// <param name="sessionHandle">Session handle returned by RegisterSession.</param>
    public static byte[] BuildSendRRData(uint sessionHandle) => BuildSendRRData(sessionHandle, BuildCipRequest());

    /// <summary>Builds an unconnected SendRRData frame carrying an arbitrary CIP request.</summary>
    /// <param name="sessionHandle">Session handle returned by RegisterSession.</param>
    /// <param name="cip">The CIP request bytes.</param>
    internal static byte[] BuildSendRRData(uint sessionHandle, byte[] cip)
    {
        // interface handle(4) timeout(2) itemCount(2) nullAddr(4) dataItem hdr(4) + cip
        var data = new byte[4 + 2 + 2 + 4 + 4 + cip.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);       // item count
        // null address item: type 0, length 0 (bytes 8..11 already zero)
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 0x00B2); // unconnected data item
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(data, 16);

        var req = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(req, SendRRDataCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(4), sessionHandle);
        data.CopyTo(req, HeaderLength);
        return req;
    }

    /// <summary>
    /// Extracts the attribute data from a SendRRData reply.
    /// </summary>
    /// <param name="reply">Full reply: 24-byte encapsulation header plus data.</param>
    /// <returns>The Get_Attributes_All payload (after the CIP reply header).</returns>
    /// <exception cref="FaultRecordException">Encapsulation or CIP error, or a malformed reply.</exception>
    public static byte[] ParseSendRRDataReply(ReadOnlySpan<byte> reply) =>
        ParseCipReply(ExtractCipItem(reply));

    /// <summary>Returns the unconnected data item (type 0x00B2) of a SendRRData reply.</summary>
    internal static ReadOnlySpan<byte> ExtractCipItem(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < HeaderLength)
            throw new FaultRecordException("SendRRData reply shorter than encapsulation header.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(reply) != SendRRDataCommand)
            throw new FaultRecordException("Reply is not a SendRRData response.");
        var status = BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]);
        if (status != 0)
            throw new FaultRecordException($"SendRRData returned encapsulation status 0x{status:X}.");

        var data = reply[HeaderLength..];
        if (data.Length < 8 + 4)
            throw new FaultRecordException("SendRRData reply truncated.");
        int items = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var p = data[8..];
        for (var i = 0; i < items; i++)
        {
            if (p.Length < 4) throw new FaultRecordException("SendRRData item list truncated.");
            int type = BinaryPrimitives.ReadUInt16LittleEndian(p);
            int len  = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
            if (p.Length < 4 + len) throw new FaultRecordException("SendRRData item truncated.");
            if (type == 0x00B2)
                return p.Slice(4, len);
            p = p[(4 + len)..];
        }
        throw new FaultRecordException("SendRRData reply has no unconnected data item.");
    }

    private static byte[] ParseCipReply(ReadOnlySpan<byte> cip)
    {
        // service(1) reserved(1) generalStatus(1) extStatusWords(1) extStatus(2*n) data...
        if (cip.Length < 4)
            throw new FaultRecordException("CIP reply truncated.");
        if (cip[0] != (GetAttributesAll | 0x80))
            throw new FaultRecordException($"Unexpected CIP reply service 0x{cip[0]:X2}.");
        if (cip[2] != 0)
            throw new FaultRecordException($"CIP general status 0x{cip[2]:X2} reading the fault record.");
        var offset = 4 + cip[3] * 2;
        if (cip.Length < offset)
            throw new FaultRecordException("CIP reply extended status truncated.");
        return cip[offset..].ToArray();
    }

    /// <summary>
    /// Reads the controller fault record from <paramref name="host"/>:<paramref name="port"/>.
    /// </summary>
    /// <returns>Raw attribute data of class 0x73 instance 1.</returns>
    /// <exception cref="FaultRecordException">Connection, I/O, timeout, CIP or parse failure.</exception>
    public static async Task<byte[]> ReadAsync(
        string host, int port = EmulatorIdentity.DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(BuildRegisterSession(), cts.Token).ConfigureAwait(false);
            var regReply = await ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt16LittleEndian(regReply) != RegisterSessionCommand
                || BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(8)) != 0)
                throw new FaultRecordException("RegisterSession was refused.");
            var handle = BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(4));

            await stream.WriteAsync(BuildSendRRData(handle), cts.Token).ConfigureAwait(false);
            var reply = await ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);

            // Best-effort polite close; failure is irrelevant.
            try
            {
                var bye = new byte[HeaderLength];
                BinaryPrimitives.WriteUInt16LittleEndian(bye, UnregisterSessionCommand);
                BinaryPrimitives.WriteUInt32LittleEndian(bye.AsSpan(4), handle);
                await stream.WriteAsync(bye, cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* ignore */ }

            return ParseSendRRDataReply(reply);
        }
        catch (FaultRecordException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FaultRecordException($"Fault record read from {host}:{port} timed out.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException or ObjectDisposedException)
        {
            throw new FaultRecordException($"Fault record read from {host}:{port} failed: {ex.Message}", ex);
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
