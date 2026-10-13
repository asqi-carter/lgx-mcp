// ── UdtLayout — pure byte-offset resolver for Logix UDT member paths ──────────
using L5Sharp.Core;

/// <summary>
/// Result of a <see cref="UdtLayout.Resolve"/> call.
/// </summary>
/// <param name="ByteOffset">
/// Byte offset of the member from the start of the parent UDT's byte array.
/// For BOOL members backed by a hidden host this is the byte offset of the host
/// (the full host word is the minimum addressable unit for byte-array slicing).
/// </param>
/// <param name="ByteSize">
/// Number of bytes occupied by the member.  For BOOL members this is the size
/// of the hidden host (1 for SINT host, 4 for DINT host).
/// </param>
/// <param name="LogixType">
/// The Logix declared type string (e.g. <c>"DINT"</c>, <c>"INT"</c>,
/// <c>"BOOL"</c>).
/// </param>
/// <param name="BitIndex">
/// For BOOL members packed into a hidden host: the zero-based bit position
/// within the host word at <see cref="ByteOffset"/>.  <c>-1</c> for all
/// non-BOOL members (full-byte addressable).
/// </param>
/// <param name="StringDataLength">
/// For members whose type is <c>STRING</c> or a StringFamily user-defined
/// string type: the declared capacity of the <c>DATA</c> character array
/// (82 for <c>STRING</c>, e.g. 20 for <c>STRING_20</c>); decoders must clamp
/// the <c>LEN</c> field to this value.  <c>-1</c> for non-string members.
/// </param>
public record MemberLayout(
    int    ByteOffset,
    int    ByteSize,
    string LogixType,
    int    BitIndex = -1,
    int    StringDataLength = -1);

/// <summary>
/// Pure, SDK-independent byte-offset resolver for Logix UDT member paths.
///
/// <para>
/// Computes the byte offset and size of any member expression within a UDT
/// using the <c>DataType</c> definitions present in an <see cref="L5X"/>
/// project document.  No controller connection is required.
/// </para>
///
/// <para>
/// <b>Layout rules</b> (matching Studio 5000 Data Monitor):
/// <list type="bullet">
///   <item>
///     Atomic sizes: BOOL=1, SINT/USINT=1, INT/UINT=2, DINT/UDINT/REAL=4,
///     LINT/ULINT/LREAL=8, STRING=88.
///   </item>
///   <item>
///     Alignment: BOOL/SINT/USINT=1, INT/UINT=2, DINT/UDINT/REAL=4,
///     LINT/ULINT/LREAL=4, UDT/STRING=4.
///   </item>
///   <item>
///     Structures are aligned to 4 bytes and padded to a multiple of 4.
///   </item>
///   <item>
///     BOOL members are packed into hidden SINT/DINT host members identified
///     by the <c>Target</c> and <c>BitNumber</c> L5X attributes.
///   </item>
///   <item>
///     Arrays are laid out as contiguous elements with element alignment.
///   </item>
///   <item>
///     Nested UDTs are resolved recursively.
///   </item>
///   <item>
///     User-defined string types (L5X <c>Family="StringFamily"</c>, e.g.
///     <c>STRING_20</c> with <c>LEN</c> DINT and <c>DATA</c> SINT[20]) are
///     sized and aligned from their declared members; see
///     <see cref="StringDataLength"/>.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// Accepted member path forms: <c>"Member"</c>, <c>"Member.Sub"</c>,
/// <c>"Member[i]"</c>, <c>"Member[i].Sub"</c>.
/// </para>
/// </summary>
public sealed class UdtLayout
{
    private readonly L5X _project;

    /// <summary>Per-type layout cache: typeName → (memberName → LayoutMemberInfo).</summary>
    private readonly Dictionary<string, Dictionary<string, LayoutMemberInfo>>
        _layoutCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Internal per-member metadata ─────────────────────────────────────────

    /// <summary>
    /// Layout metadata for a single member within a type.
    /// </summary>
    private record LayoutMemberInfo(
        /// <summary>Byte offset from the type base.</summary>
        int    Offset,
        /// <summary>Size of one element in bytes.</summary>
        int    ElementSize,
        /// <summary>Logix type name of the element (e.g. <c>"INT"</c>, <c>"MyUdt"</c>).</summary>
        string ElementType,
        /// <summary>
        /// For BOOL members packed into a host: the zero-based bit position.
        /// <c>-1</c> for non-BOOL members.
        /// </summary>
        int    BitIndex = -1,
        /// <summary>Number of elements (1 for scalars).</summary>
        int    ElementCount = 1);

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new <see cref="UdtLayout"/> backed by
    /// <paramref name="project"/>'s DataType definitions.
    /// </summary>
    /// <param name="project">
    /// The L5X project document containing the UDT definitions to resolve
    /// against.  Must not be <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="project"/> is <c>null</c>.
    /// </exception>
    public UdtLayout(L5X project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the byte offset and size of <paramref name="memberPath"/>
    /// within the UDT type <paramref name="typeName"/>.
    /// </summary>
    /// <param name="typeName">
    /// The Logix type name of the parent tag (e.g. <c>"UDT_Recipe"</c>).
    /// Must be a user-defined type present in the project.
    /// </param>
    /// <param name="memberPath">
    /// Dot-separated member path within <paramref name="typeName"/>, optionally
    /// including array subscripts (e.g. <c>"TotalDoseCount"</c>,
    /// <c>"SetupValues[2]"</c>, <c>"Inner.Speed"</c>).
    /// Must not be <c>null</c> or empty.
    /// </param>
    /// <returns>
    /// A <see cref="MemberLayout"/> record with the byte offset, byte size, and
    /// Logix type of the resolved member.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="typeName"/> or <paramref name="memberPath"/>
    /// is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="memberPath"/> is empty or whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a segment of the member path cannot be found in its
    /// containing type, or when the DataType definition is missing from the
    /// project.
    /// </exception>
    public MemberLayout Resolve(string typeName, string memberPath)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(memberPath);
        if (string.IsNullOrWhiteSpace(memberPath))
            throw new ArgumentException(
                "Member path must not be empty or whitespace.", nameof(memberPath));

        var segments = ParsePath(memberPath);

        var currentTypeName = typeName;
        int cumulativeOffset = 0;
        LayoutMemberInfo? lastInfo = null;

        foreach (var (segName, arrayIndex) in segments)
        {
            var typeMembers = GetMemberLayout(currentTypeName);

            if (!typeMembers.TryGetValue(segName, out var info))
                throw new InvalidOperationException(
                    $"Member '{segName}' not found in type '{currentTypeName}'.");

            if (arrayIndex >= 0)
            {
                if (arrayIndex >= info.ElementCount)
                    throw new InvalidOperationException(
                        $"Array index {arrayIndex} out of range for member '{segName}' " +
                        $"in type '{currentTypeName}' (length {info.ElementCount}).");
                // Array element: member's base offset + index × element size
                cumulativeOffset += info.Offset + arrayIndex * info.ElementSize;
            }
            else
            {
                cumulativeOffset += info.Offset;
            }

            lastInfo = info;
            currentTypeName = info.ElementType;
        }

        // segments is guaranteed non-empty (guarded by IsNullOrWhiteSpace above)
        if (lastInfo!.BitIndex >= 0)
        {
            // BOOL member packed into a hidden host
            return new MemberLayout(cumulativeOffset, lastInfo.ElementSize, "BOOL", lastInfo.BitIndex);
        }

        return new MemberLayout(
            cumulativeOffset, lastInfo.ElementSize, currentTypeName,
            StringDataLength: StringDataLength(currentTypeName) ?? -1);
    }

    /// <summary>
    /// Returns the declared <c>DATA</c> character capacity of
    /// <paramref name="typeName"/> when it is <c>STRING</c> (82) or a
    /// StringFamily user-defined string type (the dimension of its
    /// <c>DATA</c> member); otherwise <c>null</c>.
    /// </summary>
    /// <param name="typeName">Logix type name to inspect.</param>
    /// <returns>The DATA array length, or <c>null</c> if not a string type.</returns>
    public int? StringDataLength(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        if (string.Equals(typeName, "STRING", StringComparison.OrdinalIgnoreCase))
            return 82;

        var dt = FindStringFamilyType(typeName);
        if (dt is null) return null;

        var data = dt.Members.FirstOrDefault(
            m => string.Equals(m.Name, "DATA", StringComparison.OrdinalIgnoreCase));
        if (data is null) return null;

        int dim = GetDimension(data);
        return dim <= 0 ? 1 : dim;
    }

    /// <summary>
    /// Returns the size in bytes of one element of <paramref name="typeName"/>
    /// (the stride of a tag array of that type): fixed for atomic types and
    /// <c>STRING</c>, laid out and padded to 4 bytes for user-defined types.
    /// </summary>
    /// <param name="typeName">Logix type name of the element.</param>
    /// <returns>The element size in bytes.</returns>
    public int SizeOf(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return AtomicOrUdtSize(typeName);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a dot-separated member path into (name, arrayIndex) tuples.
    /// Array subscripts are extracted from bracket notation: <c>"Values[2]"</c>
    /// yields <c>("Values", 2)</c>; a plain name yields index <c>-1</c>.
    /// </summary>
    private static (string Name, int Index)[] ParsePath(string path)
    {
        var parts = path.Split('.');
        var result = new (string Name, int Index)[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var bracketOpen = part.IndexOf('[');
            if (bracketOpen >= 0)
            {
                var name   = part[..bracketOpen];
                var end    = part.IndexOf(']', bracketOpen);
                var idxStr = part[(bracketOpen + 1)..end];
                result[i]  = (name, int.Parse(idxStr));
            }
            else
            {
                result[i] = (part, -1);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the cached member layout for <paramref name="typeName"/>,
    /// computing it on first access.
    /// </summary>
    private Dictionary<string, LayoutMemberInfo> GetMemberLayout(string typeName)
    {
        if (_layoutCache.TryGetValue(typeName, out var cached))
            return cached;

        var layout = ComputeMemberLayout(typeName);
        _layoutCache[typeName] = layout;
        return layout;
    }

    /// <summary>
    /// Computes the byte-offset layout for all members of
    /// <paramref name="typeName"/> by walking the DataType definition's member
    /// list in declaration order and applying Logix alignment rules.
    /// </summary>
    private Dictionary<string, LayoutMemberInfo> ComputeMemberLayout(string typeName)
    {
        // Look up the DataType definition in the project.
        var dataType = _project.DataTypes
            .AsEnumerable()
            .FirstOrDefault(dt => string.Equals(dt.Name, typeName, StringComparison.OrdinalIgnoreCase));

        if (dataType is null)
            throw new InvalidOperationException(
                $"DataType '{typeName}' not found in the L5X project. " +
                "Ensure the project contains this user-defined type.");

        // Hidden-member registry: populated as hidden members are visited, used
        // to resolve the offset/size of BOOL members that reference them.
        var hiddenInfo = new Dictionary<string, (int Offset, int Size)>(
            StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, LayoutMemberInfo>(
            StringComparer.OrdinalIgnoreCase);

        int offset = 0;

        foreach (var member in dataType.Members)
        {
            if (member.Hidden)
            {
                // Hidden member — backing storage for packed BOOLs.
                // Lay it out with normal alignment/size rules.
                int align    = AlignmentOf(member.DataType);
                offset       = AlignUp(offset, align);
                int elemSize = AtomicOrUdtSize(member.DataType);
                int dim      = GetDimension(member);
                int elemCount = dim <= 0 ? 1 : dim;

                hiddenInfo[member.Name] = (offset, elemSize);
                // Also record in result so nested navigation works if needed
                result[member.Name] = new LayoutMemberInfo(offset, elemSize, member.DataType);

                offset += elemSize * elemCount;
            }
            else if (member.Target is not null && member.BitNumber.HasValue)
            {
                // BOOL member backed by a hidden host.
                // Its byte address is the host's byte address; bit extraction
                // comes from BitNumber.
                if (hiddenInfo.TryGetValue(member.Target, out var host))
                {
                    result[member.Name] = new LayoutMemberInfo(
                        host.Offset, host.Size, "BOOL", member.BitNumber.Value);
                }
                // BOOL members consume no additional structural space.
            }
            else
            {
                // Regular scalar or array member.
                int align    = AlignmentOf(member.DataType);
                offset       = AlignUp(offset, align);
                int elemSize = AtomicOrUdtSize(member.DataType);
                int dim      = GetDimension(member);
                int elemCount = dim <= 0 ? 1 : dim;

                result[member.Name] = new LayoutMemberInfo(
                    offset, elemSize, member.DataType, ElementCount: elemCount);
                offset += elemSize * elemCount;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the size in bytes of one element of <paramref name="typeName"/>.
    /// For atomic types the size is fixed; for user-defined types the structure
    /// is laid out recursively and padded to a multiple of 4.
    /// </summary>
    private int AtomicOrUdtSize(string typeName) =>
        typeName.ToUpperInvariant() switch
        {
            "BOOL"   => 1,
            "SINT"   => 1,
            "USINT"  => 1,
            "INT"    => 2,
            "UINT"   => 2,
            "DINT"   => 4,
            "UDINT"  => 4,
            "REAL"   => 4,
            "LINT"   => 8,
            "ULINT"  => 8,
            "LREAL"  => 8,
            "STRING" => 88,
            _        => ComputeUdtTotalSize(typeName),
        };

    /// <summary>
    /// Computes the total byte size (padded to a multiple of 4) of the UDT
    /// <paramref name="typeName"/> by simulating its member layout.
    /// </summary>
    private int ComputeUdtTotalSize(string typeName)
    {
        var dt = _project.DataTypes
            .AsEnumerable()
            .FirstOrDefault(d => string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase));

        if (dt is null) return 4; // defensive fallback

        // StringFamily types (LEN + DATA[n]) are sized from their declared
        // members: LEN at 0 (4-byte aligned), DATA[n] immediately after,
        // total padded to a multiple of 4 (e.g. STRING_20 → 24, STRING → 88).
        if (FindStringFamilyType(typeName) is not null)
            return StringFamilySize(dt);

        int offset = 0;
        foreach (var member in dt.Members)
        {
            // BOOL members backed by a host don't add structural space themselves.
            if (member.Target is not null) continue;

            int align    = AlignmentOf(member.DataType);
            offset       = AlignUp(offset, align);
            int elemSize = AtomicOrUdtSize(member.DataType);
            int dim      = GetDimension(member);
            int count    = dim <= 0 ? 1 : dim;
            offset += elemSize * count;
        }

        // Structures are padded to a multiple of 4 bytes.
        return AlignUp(offset, 4);
    }

    /// <summary>
    /// Returns the DataType named <paramref name="typeName"/> when it is a
    /// user-defined string type (<c>Family="StringFamily"</c>), else <c>null</c>.
    /// </summary>
    private DataType? FindStringFamilyType(string typeName)
    {
        var dt = _project.DataTypes
            .AsEnumerable()
            .FirstOrDefault(d => string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase));
        return dt is not null && dt.Family == DataTypeFamily.String ? dt : null;
    }

    /// <summary>
    /// Sizes a StringFamily <paramref name="dt"/> from its declared members
    /// (typically <c>LEN</c> DINT and <c>DATA</c> SINT[n]), padded to 4 bytes.
    /// </summary>
    private int StringFamilySize(DataType dt)
    {
        int offset = 0;
        foreach (var member in dt.Members)
        {
            if (member.Target is not null) continue;
            offset = AlignUp(offset, AlignmentOf(member.DataType));
            int dim = GetDimension(member);
            offset += AtomicOrUdtSize(member.DataType) * (dim <= 0 ? 1 : dim);
        }
        return AlignUp(offset, 4);
    }

    /// <summary>
    /// Returns the natural alignment in bytes for <paramref name="typeName"/>.
    /// Eight-byte atomic types (LINT, ULINT, LREAL) use 4-byte alignment in
    /// Logix UDTs (matching Studio 5000 Data Monitor behaviour).
    /// UDTs and STRING always align to 4 bytes.
    /// </summary>
    private static int AlignmentOf(string typeName) =>
        typeName.ToUpperInvariant() switch
        {
            "BOOL"   => 1,
            "SINT"   => 1,
            "USINT"  => 1,
            "INT"    => 2,
            "UINT"   => 2,
            "DINT"   => 4,
            "UDINT"  => 4,
            "REAL"   => 4,
            "LINT"   => 4, // 8-byte, but Logix aligns to 4 in UDTs
            "ULINT"  => 4,
            "LREAL"  => 4,
            "STRING" => 4,
            _        => 4, // UDTs align to 4
        };

    /// <summary>
    /// Rounds <paramref name="value"/> up to the nearest multiple of
    /// <paramref name="alignment"/>.
    /// </summary>
    private static int AlignUp(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// Extracts the numeric array dimension from a
    /// <see cref="DataTypeMember"/>.  Returns <c>0</c> for scalar members
    /// (a dimension of "0" in the L5X means one element).
    /// </summary>
    private static int GetDimension(DataTypeMember member)
    {
        var dimStr = member.Dimension.ToString();
        return int.TryParse(dimStr, out var dim) ? dim : 0;
    }
}
