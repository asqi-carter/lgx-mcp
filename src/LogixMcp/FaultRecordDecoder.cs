using System.Buffers.Binary;
using System.Text.Json.Serialization;

// ── FaultRecordDecoder ────────────────────────────────────────────────────────

/// <summary>A decoded controller major fault.</summary>
/// <param name="Type">Major fault type (UINT16).</param>
/// <param name="Code">Major fault code (UINT16).</param>
/// <param name="Name">Short fault name, or <c>"unknown"</c> when the code is not in the decode table.</param>
/// <param name="Meaning">One-sentence meaning; for unknown codes a pointer to 1756-PM014.</param>
/// <param name="Timestamp">Fault timestamp as the raw 8-byte value, in microseconds.</param>
/// <param name="Raw">The whole record as upper-case hex.</param>
/// <param name="Count">Record count (UINT32) at offset 8 of the record.</param>
/// <param name="Extended">Bytes from offset 24 onward as upper-case hex (raw extended info); empty when there are none.</param>
public sealed record MajorFault(
    [property: JsonPropertyName("type")]      int    Type,
    [property: JsonPropertyName("code")]      int    Code,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("meaning")]   string Meaning,
    [property: JsonPropertyName("timestamp")] ulong  Timestamp,
    [property: JsonPropertyName("raw")]       string Raw,
    [property: JsonPropertyName("count")]     uint   Count,
    [property: JsonPropertyName("extended")]  string Extended = "")
{
    /// <summary>True when the reply was too short to parse and only <see cref="Raw"/> is meaningful.</summary>
    [JsonIgnore]
    public bool IsUnparsed => Name == FaultRecordDecoder.UnparsedName;
}

/// <summary>Raised when a fault record is too short to parse.</summary>
public sealed class FaultRecordException : Exception
{
    /// <summary>Creates the exception with a message and optional cause.</summary>
    public FaultRecordException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>Machine-readable error kind: always <see cref="ErrorKinds.FaultRecordUnavailable"/>.</summary>
    public string Kind => ErrorKinds.FaultRecordUnavailable;
}

/// <summary>
/// Decodes the Get_Attributes_All reply of CIP class 0x73 instance 1 (the controller fault record).
/// Layout (bench bytes): offsets 0 and 4 are not understood and are not labeled (they stay
/// inside the raw hex); UINT32 count at 8, UINT64 timestamp (raw µs) at 12, UINT16 type at 20,
/// UINT16 code at 22, then extended info from 24. Faulted means count &gt; 0; an all-zero record means
/// the controller is not faulted.
/// </summary>
public static class FaultRecordDecoder
{
    /// <summary>Minimum parseable record length: through the code field at offset 22 (2 bytes).</summary>
    public const int MinimumLength = 24;

    /// <summary>Name given to a reply too short to parse; only the raw bytes are returned.</summary>
    public const string UnparsedName = "unparsed";

    private const int CountOffset = 8, TimestampOffset = 12, TypeOffset = 20, CodeOffset = 22, ExtendedOffset = 24;

    /// <summary>
    /// Decodes a fault record.
    /// </summary>
    /// <param name="record">Attribute data from the Get_Attributes_All reply (no CIP reply header).</param>
    /// <returns>
    /// <c>null</c> when the record is all zeros or its count is 0 (not faulted); a fault named
    /// <see cref="UnparsedName"/> carrying only the raw hex when the record is shorter than
    /// <see cref="MinimumLength"/>; otherwise the decoded fault.
    /// </returns>
    public static MajorFault? Decode(ReadOnlySpan<byte> record)
    {
        if (record.Length < MinimumLength)
            return new MajorFault(0, 0, UnparsedName,
                $"Fault record is {record.Length} bytes; at least {MinimumLength} are required to parse it. See raw.",
                0, Convert.ToHexString(record), 0);

        if (!record.ContainsAnyExcept((byte)0))
            return null;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(record[CountOffset..]);
        if (count == 0)
            return null;

        var ts   = BinaryPrimitives.ReadUInt64LittleEndian(record[TimestampOffset..]);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(record[TypeOffset..]);
        var code = BinaryPrimitives.ReadUInt16LittleEndian(record[CodeOffset..]);
        var (name, meaning) = Lookup(type, code);
        return new MajorFault(type, code, name, meaning, ts, Convert.ToHexString(record), count,
            Convert.ToHexString(record[ExtendedOffset..]));
    }

    private const string Pub = "Rockwell publication 1756-PM014 (Major, Minor and I/O Faults)";

    /// <summary>
    /// The deliberately small decode table: exactly the type/code pairs below and nothing else.
    /// Each meaning cites 1756-PM014 by title. Entries the publication does not state must not be added.
    /// </summary>
    public static IReadOnlyDictionary<(int Type, int Code), (string Name, string Meaning)> Table { get; } =
        new Dictionary<(int, int), (string, string)>
        {
            [(4, 20)] = ("array subscript or control structure invalid",
                $"Array subscript too large, or control structure .POS/.LEN invalid, per {Pub}."),
            [(4, 16)] = ("unknown instruction",
                $"The program contains an unknown instruction, per {Pub}."),
            [(4, 21)] = ("control structure .LEN or .POS negative",
                $"Control structure .LEN or .POS is less than 0, per {Pub}."),
            [(4, 31)] = ("JSR parameter mismatch",
                $"JSR parameter mismatch, per {Pub}."),
            [(4, 34)] = ("timer .PRE or .ACC negative",
                $"Timer .PRE or .ACC value is negative, per {Pub}."),
            [(4, 42)] = ("JMP to missing label",
                $"JMP to a label that does not exist, per {Pub}."),
            [(6, 1)]  = ("task watchdog expired",
                $"Task watchdog expired, per {Pub}."),
            [(1, 1)]  = ("power lost while in Run",
                $"Power was lost while the controller was in Run mode, per {Pub}."),
            [(3, 16)] = ("I/O connection lost",
                $"I/O connection lost with major fault on connection loss set, per {Pub}."),
        };

    /// <summary>
    /// Looks up a type/code pair. Pairs outside <see cref="Table"/> are reported as unknown with a
    /// pointer to the publication. Never guesses.
    /// </summary>
    private static (string Name, string Meaning) Lookup(int type, int code) =>
        Table.TryGetValue((type, code), out var e)
            ? e
            : ("unknown", $"see 1756-PM014 for type {type} code {code}");
}

// ── IdentityStatus ────────────────────────────────────────────────────────────

/// <summary>The Identity/ControllerDevice status word decoded with Rockwell's published bit table.</summary>
/// <param name="DeviceStatus">Bits 7..4: flashUpdateInProgress, flashBad, faultedModes, run, program or reserved.</param>
/// <param name="DeviceStatusBits">The raw bits 7..4 nibble as four binary digits, e.g. <c>"0110"</c>.</param>
/// <param name="Faults">Bits 11..8: recoverableMinor, unrecoverableMinor, recoverableMajor, unrecoverableMajor; empty when none.</param>
/// <param name="Keyswitch">Bits 13..12: run, program, remote, or unknown (00).</param>
/// <param name="ModeFlag">Bits 15..14: none (00), changingModes (01), debugMode (10), unknown (11).</param>
/// <param name="Source">Fixed citation of the Rockwell help page the table comes from.</param>
public sealed record StatusWord(
    [property: JsonPropertyName("deviceStatus")]     string DeviceStatus,
    [property: JsonPropertyName("deviceStatusBits")] string DeviceStatusBits,
    [property: JsonPropertyName("faults")]           IReadOnlyList<string> Faults,
    [property: JsonPropertyName("keyswitch")]        string Keyswitch,
    [property: JsonPropertyName("modeFlag")]         string ModeFlag,
    [property: JsonPropertyName("source")]           string Source = IdentityStatus.StatusSource);

/// <summary>Interprets the CIP Identity status word returned by ListIdentity.</summary>
public static class IdentityStatus
{
    /// <summary>Major recoverable fault bit (0x0400).</summary>
    public const ushort MajorRecoverableFault = 0x0400;

    /// <summary>Major unrecoverable fault bit (0x0800).</summary>
    public const ushort MajorUnrecoverableFault = 0x0800;

    /// <summary>True when the status word has a major recoverable or unrecoverable fault bit set.</summary>
    /// <param name="status">The identity status word.</param>
    public static bool IsFaulted(ushort status) =>
        (status & (MajorRecoverableFault | MajorUnrecoverableFault)) != 0;

    /// <summary>Formats the status word as <c>0xNNNN</c>.</summary>
    /// <param name="status">The identity status word.</param>
    public static string Format(ushort status) => $"0x{status:X4}";

    /// <summary>Fixed source citation for the decoded status word.</summary>
    public const string StatusSource =
        "Rockwell Logix Designer help, Access the ControllerDevice object, Status";

    /// <summary>
    /// Decodes the status word using only Rockwell's documented bit table. <c>debugMode</c> is reported as
    /// such and is not interpreted as forces.
    /// </summary>
    /// <param name="status">The identity status word.</param>
    /// <returns>The decoded <see cref="StatusWord"/>.</returns>
    public static StatusWord Decode(ushort status)
    {
        var nibble = (status >> 4) & 0xF;
        var device = nibble switch
        {
            0b0001 => "flashUpdateInProgress",
            0b0100 => "flashBad",
            0b0101 => "faultedModes",
            0b0110 => "run",
            0b0111 => "program",
            _ => "reserved",
        };

        var faults = new List<string>();
        if ((status & 0x0100) != 0) faults.Add("recoverableMinor");
        if ((status & 0x0200) != 0) faults.Add("unrecoverableMinor");
        if ((status & 0x0400) != 0) faults.Add("recoverableMajor");
        if ((status & 0x0800) != 0) faults.Add("unrecoverableMajor");

        var keyswitch = ((status >> 12) & 0x3) switch
        {
            1 => "run",
            2 => "program",
            3 => "remote",
            _ => "unknown",
        };

        var modeFlag = ((status >> 14) & 0x3) switch
        {
            0 => "none",
            1 => "changingModes",
            2 => "debugMode",
            _ => "unknown",
        };

        return new StatusWord(device, Convert.ToString(nibble, 2).PadLeft(4, '0'), faults, keyswitch, modeFlag);
    }
}
