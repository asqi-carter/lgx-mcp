// ── LibPlcTagService — libplctag adapter for ITagService ─────────────────────
using System.Globalization;
using System.Numerics;
using libplctag;
using libplctag.DataTypes;

// Alias to avoid name collision with the project's own TagInfo record.
using PlcTagInfo = libplctag.DataTypes.TagInfo;

/// <summary>
/// Concrete <see cref="ITagService"/> implementation backed by the
/// <c>libplctag</c> NuGet package (version 1.0.0).
///
/// <para>
/// <see cref="ConnectAsync"/> stores the EtherNet/IP gateway address and CIP
/// backplane path for use by subsequent operations.  Each call to
/// <see cref="ReadAsync"/> or <see cref="WriteAsync"/> creates a short-lived
/// <see cref="Tag"/>, initialises the connection, performs the operation, and
/// disposes the tag.
/// </para>
///
/// <para>
/// UDT member-path notation (e.g. <c>Cmd_Motor.Speed</c>) is passed directly
/// to the library, which resolves dot-notation natively.
/// </para>
/// </summary>
public sealed class LibPlcTagService : ITagService, IAliasAware
{
    private string? _gateway;
    private string? _path;

    /// <summary>Default per-operation timeout passed to libplctag.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Type resolver for this CIP session (project, then controller tag list).
    /// Set by <c>logix_connect</c>; <c>null</c> for a bare service.
    /// </summary>
    public CipTypeResolver? TypeResolver { get; set; }

    /// <summary>
    /// The shared alias resolver for this CIP session, over the project the write gate was derived from. When set, a
    /// read of an alias tag tries the alias name and falls back to the base path, and returns <c>aliasFor</c> /
    /// <c>resolvedFrom</c>. <c>null</c> for a bare service, or when a <see cref="RoutedTagService"/> above this one
    /// already follows aliases.
    /// </summary>
    public AliasResolver? Aliases { get; set; }

    // ── ITagService ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// <paramref name="gateway"/> is null or whitespace.
    /// </exception>
    public Task ConnectAsync(string gateway, string? path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gateway))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Gateway address must not be null or empty."));

        _gateway = gateway;
        _path    = path;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _gateway = null;
        _path    = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
    {
        TagNameValidator.ThrowIfInvalid(tagName, "Read");
        ThrowIfNotConnected();

        if (Aliases is null)
            return await ReadPlainAsync(tagName, cancellationToken);
        return await Aliases.ReadAsync(tagName, ReadPlainAsync, aliasNameFirst: true, cancellationToken);
    }

    /// <summary>
    /// Test seam standing in for the CIP link: when set, a read of a bit-of-integer path asks this function for
    /// the PARENT integer's buffer (by the parent's name) instead of opening a libplctag tag. <c>null</c> in production.
    /// </summary>
    internal Func<string, CancellationToken, Task<ITagBuffer>>? ParentBufferReader { get; set; }

    private async Task<TagValue> ReadPlainAsync(string tagName, CancellationToken cancellationToken)
    {
        // Tag.N / Tag.Member.N on an integer parent: read the parent and extract bit N.
        if (TryParseBitPath(tagName, out var parentPath, out var bit))
        {
            var parent = await ResolveParentAsync(parentPath, cancellationToken);
            if (parent is not null)
                return await ReadBitAsync(tagName, parentPath, bit, parent, cancellationToken);
        }

        using var tag = BuildTag(tagName);

        try
        {
            await SafeInitializeAsync(tag, tagName, cancellationToken);
            await SafeReadAsync(tag, tagName, cancellationToken);
        }
        catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.WriteDenied)
        {
            // The controller refused the read (e.g. a whole module tag such as Local:1:I). That is not a
            // write denial: report why the tag cannot be typed instead.
            var denied = TypeResolver is null
                ? CipTypeResolution.Unresolved("No type resolver is attached to this CIP session.")
                : await TypeResolver.ResolveAsync(tagName, cancellationToken);
            throw RemapReadDenial(ex, tagName, denied);
        }

        // Type the value from the project / controller tag list — never from
        // the buffer size.
        var resolution = TypeResolver is null
            ? CipTypeResolution.Unresolved("No type resolver is attached to this CIP session.")
            : await TypeResolver.ResolveAsync(tagName, cancellationToken);

        return DecodeValue(tagName, resolution, new LibPlcTagBuffer(tag));
    }

    /// <inheritdoc />
    public async Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        TagNameValidator.ThrowIfInvalid(tagName, "Write");
        ThrowIfNotConnected();

        // Reject unsupported .NET value types immediately, then resolve the tag's
        // declared type and convert the value to it — all before any network write.
        ValidateValueType(tagName, value);
        WriteTypeRule.RejectNonFinite(tagName, value, "LibPlcTagService");

        // A bit of an integer (Tag.N) is never written: that would be a read-modify-write of the whole integer.
        // A bit the integer does not have is BitOutOfRange (checked first), not BitWriteUnsupported.
        if (TryParseBitPath(tagName, out var bitParent, out var writeBit)
            && await ResolveParentAsync(bitParent, cancellationToken) is { } bitParentType)
        {
            if (writeBit < 0 || writeBit >= BitWidth(bitParentType))
                throw BitOutOfRange(tagName, bitParentType, writeBit);
            throw BitWriteRefusal(tagName);
        }

        var resolution = TypeResolver is null
            ? CipTypeResolution.Unresolved("No type resolver is attached to this CIP session.")
            : await TypeResolver.ResolveAsync(tagName, cancellationToken);
        var typed = CoerceForWrite(tagName, resolution, value);

        using var tag = BuildTag(tagName);

        await SafeInitializeAsync(tag, tagName, cancellationToken);

        // Read first to populate the native tag buffer (required by libplctag
        // before calling Write so the library knows the correct element size).
        await SafeReadAsync(tag, tagName, cancellationToken);

        ApplyValue(new LibPlcTagWriteBuffer(tag), tagName, resolution.TypeName!, typed);

        try
        {
            await WithTimeoutAsync(tag.WriteAsync, cancellationToken, "write", tagName);
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (LibPlcTagException ex)
        {
            throw MapException(ex, tag.GetStatus(), tagName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw MapNativeLoadFailure(ex);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.Unknown, ex.Message, tagName), ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotConnected();

        using var tag = new Tag<TagInfoPlcMapper, PlcTagInfo[]>
        {
            Gateway  = _gateway,
            Path     = _path,
            Protocol = Protocol.ab_eip,
            PlcType  = PlcType.ControlLogix,
            Name     = "@tags",
            Timeout  = DefaultTimeout,
        };

        // Same single-file guard as SafeInitializeAsync: must precede the first
        // P/Invoke call that tag.InitializeAsync will trigger.
        EnsureNativeInitialized();

        try
        {
            await WithTimeoutAsync(tag.InitializeAsync, cancellationToken, "initialize", "@tags");
            await WithTimeoutAsync(tag.ReadAsync, cancellationToken, "read", "@tags");
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (LibPlcTagException ex)
        {
            throw MapException(ex, tag.GetStatus(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw MapNativeLoadFailure(ex);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.Unknown, ex.Message), ex);
        }

        var plcTags = tag.Value ?? Array.Empty<PlcTagInfo>();

        return plcTags
            .Select(t => new TagInfo(t.Name, MapTypeCode(t.Type)))
            .ToList()
            .AsReadOnly();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Throws <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// <see cref="ConnectAsync"/> has not been called.
    /// </summary>
    private void ThrowIfNotConnected()
    {
        if (_gateway is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Not connected. Call ConnectAsync before reading or writing."));
    }

    /// <summary>
    /// Creates a low-level <see cref="Tag"/> configured with the stored
    /// gateway/path and the Allen-Bradley EtherNet/IP protocol.
    /// </summary>
    private Tag BuildTag(string tagName) => new Tag
    {
        Gateway  = _gateway,
        Path     = _path,
        Protocol = Protocol.ab_eip,
        PlcType  = PlcType.ControlLogix,
        Name     = tagName,
        Timeout  = DefaultTimeout,
    };

    /// <summary>
    /// Runs a libplctag async operation under <see cref="DefaultTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Root cause of the connect hang: libplctag.NET applies <c>Tag.Timeout</c>
    /// only to its synchronous calls. <c>InitializeAsync</c>/<c>ReadAsync</c>
    /// merely await the native completion callback, and against a closed port or
    /// an unroutable address the native library keeps retrying with the tag
    /// status stuck at <c>Pending</c> — so the callback never fires and only the
    /// caller's <see cref="CancellationToken"/> can end the wait. This helper
    /// supplies that deadline via a linked token.
    /// </remarks>
    /// <param name="operation">The libplctag async call, taking a cancellation token.</param>
    /// <param name="ct">Caller cancellation; surfaces as <see cref="OperationCanceledException"/>.</param>
    /// <param name="what">Operation name for the timeout message.</param>
    /// <param name="tagName">Tag name for the error, if any.</param>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> when the deadline elapses.
    /// </exception>
    private static async Task WithTimeoutAsync(Func<CancellationToken, Task> operation,
        CancellationToken ct, string what, string? tagName)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        try
        {
            await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"libplctag {what} timed out after {DefaultTimeout.TotalSeconds:0} s; " +
                "the controller did not respond.",
                tagName));
        }
    }

    /// <summary>Calls <see cref="Tag.InitializeAsync"/> and maps any failure.</summary>
    private static async Task SafeInitializeAsync(Tag tag, string tagName,
        CancellationToken ct)
    {
        // Ensure the native plctag library is extracted and the DllImportResolver is
        // registered before the first P/Invoke call.  This must run before
        // tag.InitializeAsync, which triggers plctag.plc_tag_create() and therefore
        // plctag.ExtractLibraryIfRequired().  Without this guard, single-file builds
        // throw System.NotSupportedException from Assembly.CodeBase inside
        // LibraryExtractor.GetExecutingAssemblyDirectory().
        EnsureNativeInitialized();

        try
        {
            await WithTimeoutAsync(tag.InitializeAsync, ct, "initialize", tagName);
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (LibPlcTagException ex)
        {
            throw MapException(ex, tag.GetStatus(), tagName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw MapNativeLoadFailure(ex);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.Unknown, ex.Message, tagName), ex);
        }
    }

    /// <summary>Calls <see cref="Tag.ReadAsync"/> and maps any failure.</summary>
    private static async Task SafeReadAsync(Tag tag, string tagName,
        CancellationToken ct)
    {
        try
        {
            await WithTimeoutAsync(tag.ReadAsync, ct, "read", tagName);
        }
        catch (TagServiceException)
        {
            throw;
        }
        catch (LibPlcTagException ex)
        {
            throw MapException(ex, tag.GetStatus(), tagName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            throw MapNativeLoadFailure(ex);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.Unknown, ex.Message, tagName), ex);
        }
    }

    // ── Bit of an integer (Tag.N / Tag.Member.N) ──────────────────────────────

    /// <summary>
    /// Splits a path whose last segment is a plain number (<c>Bench_Dint.5</c>, <c>Local:1:I.Data.3</c>) into the
    /// parent path and the bit index. Array indexes (<c>Arr[3]</c>) are not bit selectors. A number too large
    /// for an int is returned as <see cref="int.MaxValue"/> so it is reported as out of range.
    /// </summary>
    /// <param name="tagName">The tag path.</param>
    /// <param name="parentPath">The path without the bit selector; empty when this is not a bit path.</param>
    /// <param name="bit">The bit index.</param>
    /// <returns>True when the last segment is a bit selector.</returns>
    internal static bool TryParseBitPath(string tagName, out string parentPath, out int bit)
    {
        parentPath = "";
        bit = 0;
        var text = tagName.Trim();
        var dot = text.LastIndexOf('.');
        if (dot <= 0 || dot == text.Length - 1) return false;
        var last = text[(dot + 1)..];
        if (!last.All(char.IsAsciiDigit)) return false;
        // The dot must be outside any brackets.
        if (text.IndexOf(']', dot) >= 0) return false;
        parentPath = text[..dot];
        // "Program:Main" alone is not a parent tag.
        if (parentPath.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) && !parentPath.Contains('.'))
            return false;
        bit = int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
        return true;
    }

    /// <summary>
    /// Resolves the parent of a bit path; returns its type name when it is an integer type
    /// (SINT/INT/DINT/LINT and the unsigned types), otherwise <c>null</c> so the caller keeps its normal behaviour.
    /// </summary>
    private async Task<string?> ResolveParentAsync(string parentPath, CancellationToken ct)
    {
        if (TypeResolver is null) return null;
        var r = await TypeResolver.ResolveAsync(parentPath, ct);
        if (!r.IsResolved || r.Kind != CipTypeKind.Atomic || r.TypeName is null) return null;
        var type = r.TypeName.ToUpperInvariant();
        return IntegerRange(type) is null ? null : type;
    }

    /// <summary>Width in bits of an integer type.</summary>
    private static int BitWidth(string type) => AtomicSize(type)!.Value * 8;

    /// <summary>The error for a bit index the integer does not have.</summary>
    internal static TagServiceException BitOutOfRange(string tagName, string parentType, int bit) =>
        new(new TagServiceError(
            TagServiceErrorKind.BitOutOfRange,
            $"Cannot access '{tagName}': bit {(bit == int.MaxValue ? "(too large)" : bit)} does not exist in a " +
            $"{parentType}, which has bits 0 to {BitWidth(parentType) - 1}. Nothing was read or written.",
            tagName));

    /// <summary>The error for a write to one bit of an integer.</summary>
    internal static TagServiceException BitWriteRefusal(string tagName) =>
        new(new TagServiceError(
            TagServiceErrorKind.BitWriteUnsupported,
            $"Cannot write '{tagName}': it is one bit of an integer. Over CIP that is a read-modify-write of the " +
            "whole integer, which can overwrite a change the running program makes in between, so nothing was written. " +
            "Hint: write the whole integer, or use a BOOL member.",
            tagName));

    private async Task<TagValue> ReadBitAsync(string tagName, string parentPath, int bit, string parentType,
        CancellationToken ct)
    {
        if (bit >= BitWidth(parentType))
            throw BitOutOfRange(tagName, parentType, bit);

        if (ParentBufferReader is { } fake)
            return DecodeBit(tagName, parentType, bit, await fake(parentPath, ct));

        using var tag = BuildTag(parentPath);
        try
        {
            await SafeInitializeAsync(tag, parentPath, ct);
            await SafeReadAsync(tag, parentPath, ct);
        }
        catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.WriteDenied)
        {
            throw RemapReadDenial(ex, parentPath, CipTypeResolution.Unresolved("The controller refused the read."));
        }
        return DecodeBit(tagName, parentType, bit, new LibPlcTagBuffer(tag));
    }

    /// <summary>
    /// Extracts bit <paramref name="bit"/> of the parent integer held in <paramref name="buffer"/> as a BOOL.
    /// The buffer must be exactly the parent type's size, so a wrong-width reply is never read as bits.
    /// </summary>
    /// <param name="tagName">The full bit path, for the result and errors.</param>
    /// <param name="parentType">SINT, INT, DINT, LINT or an unsigned counterpart.</param>
    /// <param name="bit">The bit index.</param>
    /// <param name="buffer">The parent integer's buffer.</param>
    /// <returns>A BOOL value.</returns>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.BitOutOfRange"/> beyond the integer's width;
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> when the buffer is not the parent type's size.
    /// </exception>
    internal static TagValue DecodeBit(string tagName, string parentType, int bit, ITagBuffer buffer)
    {
        parentType = parentType.ToUpperInvariant();
        if (bit < 0 || bit >= BitWidth(parentType))
            throw BitOutOfRange(tagName, parentType, bit);

        var expected = AtomicSize(parentType)!.Value;
        var size = buffer.GetSize();
        if (size != expected)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot read '{tagName}': its parent is {parentType} ({expected} byte(s)) but the controller returned {size} byte(s).",
                tagName));

        return new TagValue(tagName, buffer.GetBit(bit), "BOOL");
    }

    /// <summary>Read-only view of a tag's native buffer; lets decoding be unit-tested without a live tag.</summary>
    internal interface ITagBuffer
    {
        /// <summary>Buffer size in bytes.</summary>
        int GetSize();
        /// <summary>Reads a bit.</summary>
        bool GetBit(int offset);
        /// <summary>Reads a signed 8-bit integer.</summary>
        sbyte GetInt8(int offset);
        /// <summary>Reads an unsigned 8-bit integer.</summary>
        byte GetUInt8(int offset);
        /// <summary>Reads a signed 16-bit integer.</summary>
        short GetInt16(int offset);
        /// <summary>Reads an unsigned 16-bit integer.</summary>
        ushort GetUInt16(int offset);
        /// <summary>Reads a signed 32-bit integer.</summary>
        int GetInt32(int offset);
        /// <summary>Reads an unsigned 32-bit integer.</summary>
        uint GetUInt32(int offset);
        /// <summary>Reads a signed 64-bit integer.</summary>
        long GetInt64(int offset);
        /// <summary>Reads an unsigned 64-bit integer.</summary>
        ulong GetUInt64(int offset);
        /// <summary>Reads a 32-bit float.</summary>
        float GetFloat32(int offset);
        /// <summary>Reads a 64-bit float.</summary>
        double GetFloat64(int offset);
    }

    private sealed class LibPlcTagBuffer(Tag tag) : ITagBuffer
    {
        public int GetSize() => tag.GetSize();
        public bool GetBit(int offset) => tag.GetBit(offset);
        public sbyte GetInt8(int offset) => tag.GetInt8(offset);
        public byte GetUInt8(int offset) => tag.GetUInt8(offset);
        public short GetInt16(int offset) => tag.GetInt16(offset);
        public ushort GetUInt16(int offset) => tag.GetUInt16(offset);
        public int GetInt32(int offset) => tag.GetInt32(offset);
        public uint GetUInt32(int offset) => tag.GetUInt32(offset);
        public long GetInt64(int offset) => tag.GetInt64(offset);
        public ulong GetUInt64(int offset) => tag.GetUInt64(offset);
        public float GetFloat32(int offset) => tag.GetFloat32(offset);
        public double GetFloat64(int offset) => tag.GetFloat64(offset);
    }

    /// <summary>
    /// Re-labels a controller refusal of a READ that was mapped to <see cref="TagServiceErrorKind.WriteDenied"/>.
    /// A read is never a write denial: an untypable tag (a whole module tag) gives
    /// <see cref="TagServiceErrorKind.TypeUnknown"/> carrying the resolver's module message; a typable one gives
    /// <see cref="TagServiceErrorKind.ProtocolError"/> with the controller's message.
    /// </summary>
    /// <param name="denial">The refusal raised while initializing or reading the tag.</param>
    /// <param name="tagName">The tag being read.</param>
    /// <param name="resolution">The tag's type resolution.</param>
    /// <returns>The exception to throw instead.</returns>
    internal static TagServiceException RemapReadDenial(
        TagServiceException denial, string tagName, CipTypeResolution resolution)
    {
        if (!resolution.IsResolved && !resolution.IsAliasUnresolved)
            return new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeUnknown,
                $"Cannot read '{tagName}': its data type is unknown. {resolution.Reason} Hint: {TypeUnknownHint}.",
                tagName), denial);

        return new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ProtocolError,
            $"Read of '{tagName}' was refused by the controller: {denial.Error.Message}",
            tagName), denial);
    }

    /// <summary>Hint attached to <see cref="TagServiceErrorKind.TypeUnknown"/> errors.</summary>
    internal const string TypeUnknownHint =
        "open the project (logix_open_project) so member and structure types are known";

    /// <summary>Hint attached to <see cref="TagServiceErrorKind.StructureReadUnsupported"/> errors.</summary>
    internal const string StructureReadHint = "read members individually";

    /// <summary>Size in bytes of a Logix atomic type as carried in a CIP read; <c>null</c> for non-atomic types.</summary>
    private static int? AtomicSize(string type) => type.ToUpperInvariant() switch
    {
        "BOOL" or "SINT" or "USINT"          => 1,
        "INT" or "UINT"                      => 2,
        "DINT" or "UDINT" or "REAL"          => 4,
        "LINT" or "ULINT" or "LREAL"         => 8,
        _                                    => null,
    };

    /// <summary>
    /// Decodes the tag's native buffer by its resolved Logix type: signed and
    /// unsigned integers via GetInt8/16/32/64 and the unsigned variants, REAL
    /// via GetFloat32, LREAL via GetFloat64, BOOL via GetBit(0).  The buffer
    /// size must equal the type's size (BOOL counts as 1).
    /// </summary>
    /// <param name="tagName">Tag name for the result and error payloads.</param>
    /// <param name="resolution">The resolved type; never inferred from the size.</param>
    /// <param name="buffer">The tag's native buffer.</param>
    /// <returns>The typed value; never a partial value.</returns>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.TypeUnknown"/> when unresolved,
    /// <see cref="TagServiceErrorKind.StructureReadUnsupported"/> for a structure or STRING,
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> when the size disagrees with the type.
    /// </exception>
    internal static TagValue DecodeValue(string tagName, CipTypeResolution resolution, ITagBuffer buffer)
    {
        if (resolution.IsAliasUnresolved)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.AliasUnresolved, $"Cannot read '{tagName}': {resolution.Reason}", tagName));

        if (!resolution.IsResolved || resolution.TypeName is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeUnknown,
                $"Cannot read '{tagName}': its data type is unknown. {resolution.Reason} Hint: {TypeUnknownHint}.",
                tagName));

        if (resolution.Kind == CipTypeKind.Structure || AtomicSize(resolution.TypeName) is not int expected)
        {
            var members = resolution.MemberPaths.Count > 0
                ? $" Members: {string.Join(", ", resolution.MemberPaths)}."
                : "";
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.StructureReadUnsupported,
                $"Cannot read '{tagName}' as a whole: it is a structure or STRING (type {resolution.TypeName}).{members} " +
                $"Hint: {StructureReadHint}.",
                tagName));
        }

        var type = resolution.TypeName.ToUpperInvariant();
        var size = buffer.GetSize();
        if (size != expected)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot read '{tagName}' as {type}: {type} is {expected} byte(s) but the controller returned {size} byte(s).",
                tagName));

        object value = type switch
        {
            "BOOL"  => buffer.GetBit(0),
            "SINT"  => buffer.GetInt8(0),
            "USINT" => buffer.GetUInt8(0),
            "INT"   => buffer.GetInt16(0),
            "UINT"  => buffer.GetUInt16(0),
            "DINT"  => buffer.GetInt32(0),
            "UDINT" => buffer.GetUInt32(0),
            "LINT"  => buffer.GetInt64(0),
            "ULINT" => buffer.GetUInt64(0),
            "REAL"  => buffer.GetFloat32(0),
            _       => buffer.GetFloat64(0), // LREAL
        };
        return new TagValue(tagName, value, type);
    }

    /// <summary>
    /// Throws <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> when <paramref name="value"/>
    /// is not a supported PLC value type (bool or a .NET numeric).  Called before
    /// any network I/O so the error is raised without attempting a connection.
    /// </summary>
    private static void ValidateValueType(string tagName, object value)
    {
        if (value is not (bool or sbyte or byte or short or ushort or int or uint
                          or long or ulong or float or double))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot convert value of type '{value.GetType().Name}' to a" +
                $" supported PLC data type (BOOL, SINT..LINT, USINT..ULINT, REAL, LREAL).",
                tagName));
    }

    /// <summary>Write-side view of a tag's native buffer; lets encoding be unit-tested without a live tag.</summary>
    internal interface ITagWriteBuffer
    {
        /// <summary>Buffer size in bytes.</summary>
        int GetSize();
        /// <summary>Writes a bit.</summary>
        void SetBit(int offset, bool value);
        /// <summary>Writes a signed 8-bit integer.</summary>
        void SetInt8(int offset, sbyte value);
        /// <summary>Writes an unsigned 8-bit integer.</summary>
        void SetUInt8(int offset, byte value);
        /// <summary>Writes a signed 16-bit integer.</summary>
        void SetInt16(int offset, short value);
        /// <summary>Writes an unsigned 16-bit integer.</summary>
        void SetUInt16(int offset, ushort value);
        /// <summary>Writes a signed 32-bit integer.</summary>
        void SetInt32(int offset, int value);
        /// <summary>Writes an unsigned 32-bit integer.</summary>
        void SetUInt32(int offset, uint value);
        /// <summary>Writes a signed 64-bit integer.</summary>
        void SetInt64(int offset, long value);
        /// <summary>Writes an unsigned 64-bit integer.</summary>
        void SetUInt64(int offset, ulong value);
        /// <summary>Writes a 32-bit float.</summary>
        void SetFloat32(int offset, float value);
        /// <summary>Writes a 64-bit float.</summary>
        void SetFloat64(int offset, double value);
    }

    private sealed class LibPlcTagWriteBuffer(Tag tag) : ITagWriteBuffer
    {
        public int GetSize() => tag.GetSize();
        public void SetBit(int o, bool v) => tag.SetBit(o, v);
        public void SetInt8(int o, sbyte v) => tag.SetInt8(o, v);
        public void SetUInt8(int o, byte v) => tag.SetUInt8(o, v);
        public void SetInt16(int o, short v) => tag.SetInt16(o, v);
        public void SetUInt16(int o, ushort v) => tag.SetUInt16(o, v);
        public void SetInt32(int o, int v) => tag.SetInt32(o, v);
        public void SetUInt32(int o, uint v) => tag.SetUInt32(o, v);
        public void SetInt64(int o, long v) => tag.SetInt64(o, v);
        public void SetUInt64(int o, ulong v) => tag.SetUInt64(o, v);
        public void SetFloat32(int o, float v) => tag.SetFloat32(o, v);
        public void SetFloat64(int o, double v) => tag.SetFloat64(o, v);
    }

    /// <summary>Inclusive value range of a Logix integer type; <c>null</c> for non-integer types.</summary>
    private static (BigInteger Min, BigInteger Max)? IntegerRange(string type) => type switch
    {
        "SINT"  => (sbyte.MinValue, sbyte.MaxValue),
        "INT"   => (short.MinValue, short.MaxValue),
        "DINT"  => (int.MinValue, int.MaxValue),
        "LINT"  => (long.MinValue, long.MaxValue),
        "USINT" => (byte.MinValue, byte.MaxValue),
        "UINT"  => (ushort.MinValue, ushort.MaxValue),
        "UDINT" => (uint.MinValue, uint.MaxValue),
        "ULINT" => (BigInteger.Zero, new BigInteger(ulong.MaxValue)),
        _       => null,
    };

    /// <summary>
    /// Converts <paramref name="value"/> to the exact .NET type the resolved Logix
    /// type is stored as (sbyte/byte/short/ushort/int/uint/long/ulong for the integer
    /// types, float for REAL, double for LREAL, bool for BOOL).  Pure — performs no I/O,
    /// so it runs before anything is sent to the controller.
    /// <list type="bullet">
    ///   <item>REAL/LREAL accept any numeric value (a JSON integer <c>5</c> is stored as <c>5.0</c>).</item>
    ///   <item>Integer types accept only whole numbers inside the type's range.</item>
    ///   <item>BOOL accepts only <see cref="bool"/> (<see cref="WriteTypeRule"/>).</item>
    /// </list>
    /// </summary>
    /// <param name="tagName">Tag name for the error payload.</param>
    /// <param name="resolution">The resolved type; never inferred from the buffer size.</param>
    /// <param name="value">The value supplied by the caller.</param>
    /// <returns>The value in the type's native .NET representation.</returns>
    /// <exception cref="TagServiceException">
    /// <see cref="TagServiceErrorKind.TypeUnknown"/> when unresolved,
    /// <see cref="TagServiceErrorKind.StructureReadUnsupported"/> for a structure or STRING,
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> for a value the type cannot hold.
    /// </exception>
    internal static object CoerceForWrite(string tagName, CipTypeResolution resolution, object value)
    {
        // NaN / ±Infinity are refused for every type, bounds or not (InvalidValue).
        WriteTypeRule.RejectNonFinite(tagName, value, "LibPlcTagService.CoerceForWrite");

        if (resolution.IsAliasUnresolved)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.AliasUnresolved,
                $"Cannot write '{tagName}': {resolution.Reason} Nothing was written.", tagName));

        if (!resolution.IsResolved || resolution.TypeName is null)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeUnknown,
                $"Cannot write '{tagName}': its data type is unknown, so nothing was written. " +
                $"{resolution.Reason} Hint: {TypeUnknownHint}.",
                tagName));

        if (resolution.Kind == CipTypeKind.Structure || AtomicSize(resolution.TypeName) is null)
        {
            var members = resolution.MemberPaths.Count > 0
                ? $" Members: {string.Join(", ", resolution.MemberPaths)}."
                : "";
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.StructureReadUnsupported,
                $"Cannot write '{tagName}' as a whole: it is a structure or STRING (type {resolution.TypeName}), " +
                $"so nothing was written.{members} Hint: write members individually.",
                tagName));
        }

        var type = resolution.TypeName.ToUpperInvariant();

        if (type == "BOOL")
        {
            WriteTypeRule.Validate(tagName, "BOOL", value);
            return value;
        }

        if (value is bool or not (sbyte or byte or short or ushort or int or uint
                                  or long or ulong or float or double))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot write value of type '{value.GetType().Name}' to tag '{tagName}' declared as {type}. " +
                (IntegerRange(type) is null ? $"{type} accepts any number." : $"{type} accepts whole numbers."),
                tagName));

        if (type == "REAL")
        {
            var f = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            // A finite double beyond float range overflows to ±Infinity; refuse it.
            if (float.IsInfinity(f) && double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)))
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TypeMismatch,
                    $"Value {Convert.ToString(value, CultureInfo.InvariantCulture)} is outside the REAL range " +
                    $"(about ±{float.MaxValue:E2}) for tag '{tagName}'.",
                    tagName));
            return f;
        }

        if (type == "LREAL")
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);

        // Integer types: whole numbers inside the type's range.
        var (min, max) = IntegerRange(type)!.Value;
        var whole = $"{type} accepts whole numbers";
        BigInteger n;
        switch (value)
        {
            case float or double:
                var d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d) || d != Math.Floor(d))
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.TypeMismatch,
                        $"Cannot write {Convert.ToString(value, CultureInfo.InvariantCulture)} to tag '{tagName}': {whole}.",
                        tagName));
                n = new BigInteger(d);
                break;
            case ulong ul:
                n = ul;
                break;
            default:
                n = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
        }

        if (n < min || n > max)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot write {n} to tag '{tagName}': {whole} from {min} to {max}.",
                tagName));

        return type switch
        {
            "SINT"  => (sbyte)n,
            "INT"   => (short)n,
            "DINT"  => (int)n,
            "LINT"  => (long)n,
            "USINT" => (byte)n,
            "UINT"  => (ushort)n,
            "UDINT" => (uint)n,
            _       => (ulong)n, // ULINT
        };
    }

    /// <summary>
    /// Coerces <paramref name="value"/> by the resolved type and stores it in
    /// <paramref name="buffer"/> (see <see cref="CoerceForWrite"/> and <see cref="ApplyValue"/>).
    /// </summary>
    /// <param name="tagName">Tag name for the error payload.</param>
    /// <param name="resolution">The resolved type.</param>
    /// <param name="value">The caller's value.</param>
    /// <param name="buffer">The tag's native buffer.</param>
    internal static void EncodeValue(string tagName, CipTypeResolution resolution,
        object value, ITagWriteBuffer buffer)
    {
        var typed = CoerceForWrite(tagName, resolution, value);
        ApplyValue(buffer, tagName, resolution.TypeName!, typed);
    }

    /// <summary>
    /// Stores an already-coerced value (from <see cref="CoerceForWrite"/>) into the
    /// native buffer with the setter for the resolved <paramref name="type"/>.  The
    /// buffer size must equal the type's size, otherwise nothing is written.
    /// </summary>
    private static void ApplyValue(ITagWriteBuffer buffer, string tagName, string type, object typed)
    {
        type = type.ToUpperInvariant();
        var expected = AtomicSize(type)!.Value;
        var size = buffer.GetSize();
        if (size != expected)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot write '{tagName}' as {type}: {type} is {expected} byte(s) but the controller returned {size} byte(s).",
                tagName));

        switch (type)
        {
            case "BOOL":  buffer.SetBit(0, (bool)typed); break;
            case "SINT":  buffer.SetInt8(0, (sbyte)typed); break;
            case "USINT": buffer.SetUInt8(0, (byte)typed); break;
            case "INT":   buffer.SetInt16(0, (short)typed); break;
            case "UINT":  buffer.SetUInt16(0, (ushort)typed); break;
            case "DINT":  buffer.SetInt32(0, (int)typed); break;
            case "UDINT": buffer.SetUInt32(0, (uint)typed); break;
            case "LINT":  buffer.SetInt64(0, (long)typed); break;
            case "ULINT": buffer.SetUInt64(0, (ulong)typed); break;
            case "REAL":  buffer.SetFloat32(0, (float)typed); break;
            default:      buffer.SetFloat64(0, (double)typed); break; // LREAL
        }
    }

    /// <summary>Message for <see cref="TagServiceErrorKind.CipRuntimeMissing"/>.</summary>
    internal const string CipRuntimeMissingMessage =
        "The CIP library could not load: its native component needs the Microsoft Visual C++ " +
        "2015\u20132022 Redistributable (x64), which is not installed.";

    /// <summary>Hint for <see cref="TagServiceErrorKind.CipRuntimeMissing"/>.</summary>
    internal const string CipRuntimeMissingHint =
        "Install the Microsoft Visual C++ x64 redistributable (vc_redist.x64.exe), " +
        "then restart the MCP server.";

    /// <summary>
    /// Maps a native-library load failure (<see cref="DllNotFoundException"/>) to a
    /// <see cref="TagServiceErrorKind.CipRuntimeMissing"/> error.  The message and
    /// hint deliberately carry no controller-reachability advice: the controller
    /// was never contacted.
    /// </summary>
    /// <param name="ex">The load failure.</param>
    internal static TagServiceException MapNativeLoadFailure(DllNotFoundException ex) =>
        new(new TagServiceError(
            TagServiceErrorKind.CipRuntimeMissing,
            CipRuntimeMissingMessage,
            Hint: CipRuntimeMissingHint), ex);

    /// <summary>
    /// Runs <see cref="LibPlcTagNativeInit.EnsureInitialized"/>, mapping a native
    /// load failure to <see cref="TagServiceErrorKind.CipRuntimeMissing"/>.
    /// </summary>
    private static void EnsureNativeInitialized()
    {
        try { LibPlcTagNativeInit.EnsureInitialized(); }
        catch (DllNotFoundException ex) { throw MapNativeLoadFailure(ex); }
    }

    /// <summary>
    /// Maps a <see cref="LibPlcTagException"/> combined with the current tag
    /// <see cref="Status"/> to a structured <see cref="TagServiceException"/>.
    /// <c>ErrorBadParam</c>/<c>ErrorBadConfig</c>/<c>ErrorBadData</c> map to
    /// <see cref="TagServiceErrorKind.ProtocolError"/> with the raw status name
    /// and tag in the message; <see cref="TagServiceErrorKind.TypeMismatch"/> is
    /// reserved for the server's own pre-wire value conversion failures.
    /// </summary>
    internal static TagServiceException MapException(
        LibPlcTagException ex, Status status, string? tagName)
    {
        var kind = status switch
        {
            Status.ErrorBadGateway
                or Status.ErrorBadConnection
                or Status.ErrorTimeout
                or Status.ErrorOpen
                or Status.ErrorClose    => TagServiceErrorKind.ConnectionFailed,

            Status.ErrorNotFound
                or Status.ErrorNoMatch  => TagServiceErrorKind.TagNotFound,

            Status.ErrorNotAllowed      => TagServiceErrorKind.WriteDenied,

            Status.ErrorOutOfBounds     => TagServiceErrorKind.OutOfBounds,

            Status.ErrorBadParam
                or Status.ErrorBadData
                or Status.ErrorBadConfig => TagServiceErrorKind.ProtocolError,

            // Every other known libplctag error status is a named wire/protocol
            // failure; Unknown is only for Ok/Pending or an undefined value.
            _ when IsKnownErrorStatus(status) => TagServiceErrorKind.ProtocolError,

            _                           => TagServiceErrorKind.Unknown,
        };

        var message = kind == TagServiceErrorKind.OutOfBounds
            ? $"libplctag {status}" +
              (tagName is null ? "" : $" for tag '{tagName}'") +
              $": {ex.Message}"
            : kind == TagServiceErrorKind.ProtocolError
            ? $"libplctag protocol error {status}" +
              (tagName is null ? "" : $" for tag '{tagName}'") +
              $": {ex.Message}"
            : ex.Message;

        return new TagServiceException(
            new TagServiceError(kind, message, tagName), ex);
    }

    /// <summary>
    /// True when <paramref name="status"/> is a defined libplctag error status
    /// (i.e. not <see cref="Status.Ok"/>, <see cref="Status.Pending"/> or an
    /// undefined numeric value).
    /// </summary>
    private static bool IsKnownErrorStatus(Status status) =>
        Enum.IsDefined(typeof(Status), status)
        && status != Status.Ok
        && status != Status.Pending;

    /// <summary>
    /// Converts a ControlLogix tag-type integer returned by
    /// <see cref="PlcTagInfo.Type"/> to a human-readable Logix type string.
    /// Atomic types (BOOL, SINT, INT, DINT, LINT, USINT, UINT, UDINT, ULINT, REAL, LREAL) are
    /// mapped explicitly; all other codes (UDTs, arrays) return <c>null</c>.
    /// </summary>
    internal static string? MapTypeCode(int typeCode)
    {
        // ControlLogix atomic type codes follow the CIP specification.
        // The upper byte distinguishes atomic (0x00) from template/UDT (non-zero).
        return (typeCode & 0xFF) switch
        {
            0xC1 when (typeCode >> 8) == 0 => "BOOL",
            0xC2 when (typeCode >> 8) == 0 => "SINT",
            0xC3 when (typeCode >> 8) == 0 => "INT",
            0xC4 when (typeCode >> 8) == 0 => "DINT",
            0xC5 when (typeCode >> 8) == 0 => "LINT",
            0xC6 when (typeCode >> 8) == 0 => "USINT",
            0xC7 when (typeCode >> 8) == 0 => "UINT",
            0xC8 when (typeCode >> 8) == 0 => "UDINT",
            0xC9 when (typeCode >> 8) == 0 => "ULINT",
            0xCA when (typeCode >> 8) == 0 => "REAL",
            0xCB when (typeCode >> 8) == 0 => "LREAL",
            _ => null,
        };
    }
}
