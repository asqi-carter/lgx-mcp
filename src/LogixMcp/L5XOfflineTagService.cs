// ── L5XOfflineTagService — L5Sharp L5X adapter for ITagService ──────────────────
using L5Sharp.Core;

// L5Sharp.Core defines its own Task type (representing an AOI Task component).
// Alias System.Threading.Tasks.Task to avoid the ambiguous-reference error.
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Concrete <see cref="ITagService"/> implementation backed by an
/// <see cref="L5X"/> Logix project loaded in memory via the L5Sharp library.
///
/// <para>
/// Unlike <see cref="LibPlcTagService"/> (which requires a live EtherNet/IP
/// connection), this service operates entirely offline against the project's
/// in-memory tag data.  It is fully substitutable wherever
/// <see cref="LibPlcTagService"/> is used, including inside
/// <see cref="GatedTagService"/>.
/// </para>
///
/// <para>
/// In addition to the atomic types modelled by <see cref="LibPlcTagService"/>
/// (BOOL, SINT, INT, DINT, LINT, REAL), this service extends coverage to the
/// unsigned integer family (USINT, UINT, UDINT, ULINT) and LREAL.  Values are
/// returned as correctly-typed .NET primitives rather than being truncated to
/// <see cref="int"/> or <see cref="double"/>.
/// </para>
///
/// <para>
/// <see cref="ConnectAsync"/> and <see cref="DisconnectAsync"/> manage a simple
/// connected flag; no network I/O is performed.  The L5X project must be
/// supplied at construction time and remains the single source of truth for all
/// tag reads and writes during the lifetime of the service.
/// </para>
/// </summary>
public sealed class L5XOfflineTagService : ITagService, IAliasAware
{
    private readonly L5X _project;
    private bool _connected;

    /// <summary>
    /// Initialises a new <see cref="L5XOfflineTagService"/> backed by the
    /// provided <paramref name="project"/>.
    /// </summary>
    /// <param name="project">
    /// The in-memory L5X project from which tags are read and to which tag
    /// values are written.  Must not be <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="project"/> is <c>null</c>.
    /// </exception>
    public L5XOfflineTagService(L5X project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Aliases  = new AliasResolver(project);
    }

    /// <summary>
    /// The shared alias resolver over this project: a read of an alias tag returns its base's value and real type
    /// (with <c>aliasFor</c> / <c>resolvedFrom</c>), never the alias's own <c>NULL</c> placeholder.
    /// </summary>
    public AliasResolver Aliases { get; }

    AliasResolver? IAliasAware.Aliases => Aliases;

    // ── ITagService ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Sets the connected flag so that subsequent read/write/list calls are
    /// permitted.  No network connection is established; the <paramref name="gateway"/>
    /// and <paramref name="path"/> parameters are accepted but not used — the
    /// backing project was supplied at construction time.
    /// </remarks>
    public Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Clears the connected flag; no network teardown is performed.</remarks>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the tag value from the in-memory L5X project.  Dot-notation member
    /// paths such as <c>"Cmd_Motor.Speed"</c> are resolved by L5Sharp automatically.
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// <see cref="ConnectAsync"/> has not been called, or
    /// <see cref="TagServiceErrorKind.TagNotFound"/> when the tag name cannot
    /// be resolved in the project.
    /// </exception>
    public Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotConnected();
        return Aliases.ReadAsync(tagName, ReadPlainAsync, aliasNameFirst: false, cancellationToken);
    }

    private async Task<TagValue> ReadPlainAsync(string tagName, CancellationToken cancellationToken)
    {
        if (!_project.TryGet<Tag>(tagName, out var tag))
        {
            // Bit of an integer (Flags.5): read the integer and select the bit; an alias can resolve to one.
            if (TryReadBit(tagName, out var bit))
                return new TagValue(tagName, bit, "BOOL");

            // A bit the integer does not have (Bench_Dint.32, Local:1:I.Data.32) is BitOutOfRange, as over CIP.
            await ThrowIfBitOutOfRangeAsync(tagName, cancellationToken).ConfigureAwait(false);

            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Tag '{tagName}' not found in the L5X project.",
                tagName));
        }

        var (value, dataType) = ExtractValue(tag, tagName);
        return new TagValue(tagName, value, dataType);
    }

    private CipTypeResolver? _typeResolver;

    /// <summary>
    /// Throws <see cref="TagServiceErrorKind.BitOutOfRange"/> when <paramref name="tagName"/> is <c>Parent.N</c>,
    /// the parent resolves (project tag, UDT member, alias, module I/O) to an integer type, and N is at or past
    /// its width. Returns normally for anything else so the caller reports its usual error.
    /// </summary>
    /// <param name="tagName">The full tag path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ThrowIfBitOutOfRangeAsync(string tagName, CancellationToken cancellationToken)
    {
        if (!LibPlcTagService.TryParseBitPath(tagName, out var parentPath, out var bit)) return;

        _typeResolver ??= new CipTypeResolver(_project,
            _ => Task.FromResult<IReadOnlyList<TagInfo>>(Array.Empty<TagInfo>()));
        var parent = await _typeResolver.ResolveAsync(parentPath, cancellationToken).ConfigureAwait(false);
        if (!parent.IsResolved || parent.Kind != CipTypeKind.Atomic || parent.TypeName is null) return;

        var type = parent.TypeName.ToUpperInvariant();
        var width = type switch
        {
            "SINT" or "USINT" => 8,
            "INT" or "UINT"   => 16,
            "DINT" or "UDINT" => 32,
            "LINT" or "ULINT" => 64,
            _                 => 0,
        };
        if (width != 0 && bit >= width)
            throw LibPlcTagService.BitOutOfRange(tagName, type, bit);
    }

    /// <summary>Reads <c>Tag.N</c> (N a bit number) as a BOOL from the integer <c>Tag</c>.</summary>
    private bool TryReadBit(string tagName, out bool bit)
    {
        bit = false;
        var dot = tagName.LastIndexOf('.');
        if (dot <= 0 || !int.TryParse(tagName[(dot + 1)..], out var index) || index < 0) return false;
        if (!_project.TryGet<Tag>(tagName[..dot], out var parent)) return false;

        long raw;
        int width;
        switch (parent.Value)
        {
            case SINT  v: raw = (sbyte)v;  width = 8;  break;
            case INT   v: raw = (short)v;  width = 16; break;
            case DINT  v: raw = (int)v;    width = 32; break;
            case LINT  v: raw = (long)v;   width = 64; break;
            case USINT v: raw = (byte)v;   width = 8;  break;
            case UINT  v: raw = (ushort)v; width = 16; break;
            case UDINT v: raw = (uint)v;   width = 32; break;
            case ULINT v: raw = unchecked((long)(ulong)v); width = 64; break;
            default: return false;
        }
        if (index >= width) return false;
        bit = ((raw >> index) & 1L) != 0;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes <paramref name="value"/> into the in-memory L5X project tag data.
    /// Dot-notation member paths such as <c>"Cmd_Motor.Speed"</c> are resolved
    /// by L5Sharp automatically.  Supports all Logix atomic types including the
    /// unsigned integer family (USINT, UINT, UDINT, ULINT) and LREAL.
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/>,
    /// <see cref="TagServiceErrorKind.TagNotFound"/>, or
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> as appropriate.
    /// </exception>
    public Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotConnected();

        // Validate the value type before any project look-up so TypeMismatch is
        // raised immediately without scanning the tag index.
        ValidateValueType(tagName, value);
        WriteTypeRule.RejectNonFinite(tagName, value, "L5XOfflineTagService");

        // A write to an alias lands on its base (an unresolvable alias is AliasUnresolved).
        var target = Aliases.Resolve(tagName)?.ResolvedFrom ?? tagName;
        if (!_project.TryGet<Tag>(target, out var tag))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TagNotFound,
                $"Tag '{target}' not found in the L5X project.",
                tagName));

        ApplyValue(tag, tagName, value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns all controller-scoped tags from the in-memory project.
    /// Program-scoped tags are not included.
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// <see cref="ConnectAsync"/> has not been called.
    /// </exception>
    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotConnected();

        IReadOnlyList<TagInfo> list = _project.Tags
            .Select(t => new TagInfo(t.Name, t.DataType))
            .ToList()
            .AsReadOnly();

        return Task.FromResult(list);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Throws <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> when
    /// <see cref="ConnectAsync"/> has not been called.
    /// </summary>
    private void ThrowIfNotConnected()
    {
        if (!_connected)
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Not connected. Call ConnectAsync before reading or writing."));
    }

    /// <summary>
    /// Extracts a native .NET value and Logix type string from a
    /// <see cref="Tag"/>'s <see cref="LogixData"/> value.
    ///
    /// <para>
    /// Handles all Logix atomic types, including the unsigned integer family
    /// (USINT, UINT, UDINT, ULINT) and LREAL that
    /// <see cref="LibPlcTagService"/> does not model.  For complex (UDT / array)
    /// types the tag's raw string representation is used as the value payload.
    /// </para>
    /// </summary>
    /// <param name="tag">The tag whose value should be extracted.</param>
    /// <param name="tagName">Tag name forwarded to <see cref="TagValue.TagName"/>.</param>
    /// <returns>
    /// A tuple of (<c>Value</c>, <c>DataType</c>) where <c>Value</c> is the
    /// boxed .NET native value and <c>DataType</c> is the Logix type string.
    /// </returns>
    private static (object Value, string DataType) ExtractValue(Tag tag, string tagName)
    {
        return tag.Value switch
        {
            // Signed integer family
            BOOL  b  => ((object)(bool)b,    "BOOL"),
            SINT  s  => ((object)(sbyte)s,   "SINT"),
            INT   i  => ((object)(short)i,   "INT"),
            DINT  d  => ((object)(int)d,     "DINT"),
            LINT  l  => ((object)(long)l,    "LINT"),

            // Unsigned integer family — extended coverage not present in LibPlcTagService
            USINT u  => ((object)(byte)u,    "USINT"),
            UINT  u  => ((object)(ushort)u,  "UINT"),
            UDINT u  => ((object)(uint)u,    "UDINT"),
            ULINT u  => ((object)(ulong)u,   "ULINT"),

            // Floating-point family
            REAL  r  => ((object)(float)r,   "REAL"),
            LREAL lr => ((object)(double)lr, "LREAL"),

            // Complex / unknown types — return the raw string representation
            _ => ((object)tag.Value.ToString()!, tag.DataType),
        };
    }

    /// <summary>
    /// Throws <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.TypeMismatch"/> when <paramref name="value"/>
    /// is not a supported PLC value type.  Called before any project look-up so
    /// that the error is raised immediately.
    ///
    /// <para>
    /// Extends the type set accepted by <see cref="LibPlcTagService"/> to include
    /// the unsigned integer family (<see cref="byte"/> / USINT,
    /// <see cref="ushort"/> / UINT, <see cref="uint"/> / UDINT,
    /// <see cref="ulong"/> / ULINT) and <see cref="sbyte"/> / SINT.
    /// </para>
    /// </summary>
    /// <param name="tagName">Tag name used in the error payload.</param>
    /// <param name="value">Boxed value to validate.</param>
    private static void ValidateValueType(string tagName, object value)
    {
        if (value is not (bool   or sbyte  or short  or int   or long
                       or byte  or ushort or uint   or ulong
                       or float or double))
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Cannot convert value of type '{value.GetType().Name}' to a " +
                $"supported PLC data type " +
                $"(BOOL, SINT, INT, DINT, LINT, USINT, UINT, UDINT, ULINT, REAL, LREAL).",
                tagName));
    }

    /// <summary>
    /// Applies <paramref name="value"/> to <paramref name="tag"/>'s in-memory
    /// buffer using the L5Sharp <see cref="AtomicData"/> implicit operators.
    ///
    /// <para>
    /// Extends the type set handled by <see cref="LibPlcTagService"/> to include
    /// the unsigned integer family and <see cref="sbyte"/> / SINT.
    /// </para>
    /// </summary>
    /// <param name="tag">The tag to update.</param>
    /// <param name="tagName">Tag name used in error payloads.</param>
    /// <param name="value">The value to write; must already have passed
    /// <see cref="ValidateValueType"/> validation.</param>
    private static void ApplyValue(Tag tag, string tagName, object value)
    {
        try
        {
            tag.Value = value switch
            {
                // Signed integer family
                bool   b => (AtomicData)b,
                sbyte  s => (AtomicData)s,
                short  s => (AtomicData)s,
                int    i => (AtomicData)i,
                long   l => (AtomicData)l,

                // Unsigned integer family
                byte   b => (AtomicData)b,
                ushort u => (AtomicData)u,
                uint   u => (AtomicData)u,
                ulong  u => (AtomicData)u,

                // Floating-point family
                float  f => (AtomicData)f,
                double d => (AtomicData)d,

                // ValidateValueType already rejected anything else; this branch is
                // unreachable in practice but satisfies the compiler exhaustiveness check.
                _ => throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.TypeMismatch,
                    $"Cannot convert value of type '{value.GetType().Name}' to a " +
                    $"supported PLC data type.",
                    tagName)),
            };
        }
        catch (TagServiceException)
        {
            throw; // Re-throw structured exceptions without wrapping.
        }
        catch (Exception ex)
        {
            // L5Sharp may throw if the .NET type is incompatible with the tag's
            // declared data type (e.g. writing a double to a BOOL tag).
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.TypeMismatch,
                $"Failed to write value to tag '{tagName}': {ex.Message}",
                tagName), ex);
        }
    }
}
