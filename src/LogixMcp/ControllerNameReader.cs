using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

// ── ControllerNameReader ──────────────────────────────────────────────────────

/// <summary>
/// Reads the controller (program) name over EtherNet/IP with the read-only CIP
/// <c>Get_Attribute_List (0x03)</c> on class 0x64, instance 1, attribute 1, as pycomm3
/// <c>get_plc_name</c> does. The request is wrapped in an Unconnected_Send to the Connection Manager so
/// it is routed along the derived CIP path (for example <c>1,0</c>: backplane port 1, slot 0).
/// A failed read yields <c>null</c>, never an exception.
/// </summary>
public static class ControllerNameReader
{
    /// <summary>CIP service Get_Attribute_List.</summary>
    public const byte GetAttributeList = 0x03;

    /// <summary>Controller-name class.</summary>
    public const byte NameClass = 0x64;

    private const byte UnconnectedSend = 0x52;

    /// <summary>Builds the bare Get_Attribute_List request: class 0x64 instance 1, one attribute (1).
    /// The path size byte counts 16-bit words: <c>20 64 24 01</c> is 2 words. (0151c sent 3; the controller
    /// rejected every request and the CIP route was refused on every project without a serial.)</summary>
    public static byte[] BuildCipRequest() =>
        [GetAttributeList, 0x02, 0x20, NameClass, 0x24, 0x01, 0x01, 0x00, 0x01, 0x00];

    /// <summary>
    /// Parses a CIP route path such as <c>1,0</c> into route-path bytes (port, link address pairs).
    /// </summary>
    /// <param name="path">Comma-separated decimal numbers, an even count.</param>
    /// <returns>The route bytes, or <c>null</c> when the path is empty, odd-length or not numeric.</returns>
    public static byte[]? ParseRoute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length % 2 != 0) return null;
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i], out bytes[i])) return null;
        return bytes;
    }

    /// <summary>
    /// Builds the routed request: Unconnected_Send (0x52) to the Connection Manager (class 6 instance 1)
    /// carrying <see cref="BuildCipRequest"/> and the route path. An empty route sends the bare request.
    /// </summary>
    /// <param name="route">Route-path bytes from <see cref="ParseRoute"/>.</param>
    public static byte[] BuildRoutedRequest(byte[] route)
    {
        var inner = BuildCipRequest();
        if (route.Length == 0) return inner;
        var pad = inner.Length % 2;
        var req = new List<byte> { UnconnectedSend, 0x02, 0x20, 0x06, 0x24, 0x01, 0x0A, 0xF0 };
        req.Add((byte)(inner.Length & 0xFF));
        req.Add((byte)(inner.Length >> 8));
        req.AddRange(inner);
        if (pad == 1) req.Add(0x00);
        req.Add((byte)(route.Length / 2));
        req.Add(0x00);
        req.AddRange(route);
        return [.. req];
    }

    /// <summary>
    /// Parses the CIP reply to <see cref="BuildCipRequest"/> (the item data of the SendRRData reply):
    /// service 0x83, reserved, general status, extended-status size, then attribute count (UINT),
    /// attribute id (UINT), attribute status (UINT) and the name as a UINT length followed by the characters.
    /// </summary>
    /// <param name="cip">CIP reply bytes.</param>
    /// <returns>The controller name, or <c>null</c> when the reply is an error or malformed.</returns>
    public static string? ParseCipReply(ReadOnlySpan<byte> cip)
    {
        if (cip.Length < 4 || cip[0] != (GetAttributeList | 0x80) || cip[2] != 0) return null;
        var offset = 4 + cip[3] * 2;
        var data = cip.Length >= offset ? cip[offset..] : default;
        // count(2) id(2) status(2) nameLength(2) name...
        if (data.Length < 8) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) < 1) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 1) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0) return null;
        int len = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (len == 0 || data.Length < 8 + len) return null;
        return Encoding.ASCII.GetString(data.Slice(8, len));
    }

    /// <summary>Parses a full SendRRData reply frame; <c>null</c> on any failure.</summary>
    /// <param name="reply">Full reply frame including the encapsulation header.</param>
    public static string? ParseSendRRDataReply(ReadOnlySpan<byte> reply)
    {
        try { return ParseCipReply(ControllerFaultReader.ExtractCipItem(reply)); }
        catch (FaultRecordException) { return null; }
    }

    /// <summary>
    /// Reads the controller name from <paramref name="host"/> over <paramref name="path"/>.
    /// </summary>
    /// <returns>The name, or <c>null</c> when the read fails for any reason (including cancellation by timeout).</returns>
    public static async Task<string?> ReadAsync(
        string host, string path, int port = EmulatorIdentity.DefaultPort, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var route = ParseRoute(path);
        if (route is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(ControllerFaultReader.BuildRegisterSession(), cts.Token).ConfigureAwait(false);
            var regReply = await ControllerFaultReader.ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(8)) != 0) return null;
            var handle = BinaryPrimitives.ReadUInt32LittleEndian(regReply.AsSpan(4));

            await stream.WriteAsync(
                ControllerFaultReader.BuildSendRRData(handle, BuildRoutedRequest(route)), cts.Token).ConfigureAwait(false);
            var reply = await ControllerFaultReader.ReadFrameAsync(stream, cts.Token).ConfigureAwait(false);
            return ParseSendRRDataReply(reply);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }
}
