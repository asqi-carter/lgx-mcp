// ── CipTypeResolver — resolves a CIP tag path to a Logix type name ───────────
using L5Sharp.Core;

/// <summary>How a tag path's type was classified by <see cref="CipTypeResolver"/>.</summary>
public enum CipTypeKind
{
    /// <summary>A Logix atomic type (BOOL, SINT, INT, DINT, LINT, USINT, UINT, UDINT, ULINT, REAL, LREAL).</summary>
    Atomic,

    /// <summary>A structure, user-defined type, predefined structure or STRING family type.</summary>
    Structure,

    /// <summary>The type could not be determined. Callers must never guess it from the tag size.</summary>
    Unresolved,
}

/// <summary>Where a <see cref="CipTypeResolution"/> came from.</summary>
public enum CipTypeSource
{
    /// <summary>No source could answer.</summary>
    None,

    /// <summary>The loaded project's declared data types.</summary>
    Project,

    /// <summary>The controller's <c>@tags</c> listing (atomic whole controller tags only).</summary>
    TagList,
}

/// <summary>
/// Outcome of <see cref="CipTypeResolver.ResolveAsync"/>.
/// </summary>
/// <param name="Kind">Atomic, structure, or unresolved.</param>
/// <param name="TypeName">Logix type name (e.g. <c>REAL</c>, <c>Probe_UDT</c>); <c>null</c> when unresolved.</param>
/// <param name="Source">Which source produced the answer.</param>
/// <param name="MemberPaths">
/// For a structure whose definition is in the project: the member paths below it
/// (e.g. <c>Count</c>, <c>Speed</c>, <c>Inner.Value</c>). Empty otherwise.
/// </param>
/// <param name="Reason">Why the path is unresolved; <c>null</c> when resolved.</param>
/// <param name="ResolvedFrom">
/// When the path names an alias tag: the base path whose type this is (e.g. <c>Ctl_Flags.5</c>); <c>null</c> otherwise.
/// </param>
/// <param name="IsAliasUnresolved">
/// True when the path names an alias that cannot be followed (cycle, missing target, module I/O, or deeper than
/// <see cref="AliasReadResolver.MaxDepth"/>); <see cref="Reason"/> names the chain.
/// </param>
public sealed record CipTypeResolution(
    CipTypeKind             Kind,
    string?                 TypeName,
    CipTypeSource           Source,
    IReadOnlyList<string>   MemberPaths,
    string?                 Reason = null,
    string?                 ResolvedFrom = null,
    bool                    IsAliasUnresolved = false)
{
    /// <summary>True when the type is known (atomic or structure).</summary>
    public bool IsResolved => Kind != CipTypeKind.Unresolved;

    internal static CipTypeResolution Unresolved(string reason) =>
        new(CipTypeKind.Unresolved, null, CipTypeSource.None, Array.Empty<string>(), reason);

    internal static CipTypeResolution AliasUnresolved(string reason) =>
        new(CipTypeKind.Unresolved, null, CipTypeSource.None, Array.Empty<string>(), reason, IsAliasUnresolved: true);
}

/// <summary>
/// Resolves a tag path to a Logix type name for the CIP (libplctag) backend.
/// Sources, in order:
/// <list type="number">
///   <item>The loaded project: the tag's declared <c>DataType</c>; member paths
///   <c>A.B.C</c> walk user-defined types member by member, including nested
///   UDTs and array element types (<c>A[3].B</c>).</item>
///   <item>The controller tag list (<c>@tags</c>), read once per session and
///   cached. Only a whole controller-scoped atomic tag can be answered this way.</item>
///   <item>Otherwise <see cref="CipTypeKind.Unresolved"/>. The resolver never
///   infers a type from the tag's byte size.</item>
/// </list>
/// </summary>
public sealed class CipTypeResolver
{
    private const int MaxMemberDepth = 4;

    private static readonly HashSet<string> AtomicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL",
    };

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT",
    };

    private readonly ISessionTypeSource? _types;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TagInfo>>> _listTags;
    private readonly SemaphoreSlim _tagListLock = new(1, 1);
    private Dictionary<string, string>? _tagListCache;

    /// <summary>Creates a resolver.</summary>
    /// <param name="project">The loaded project, or <c>null</c> when none is open.</param>
    /// <param name="listTags">Reads the controller tag list (<c>@tags</c>); invoked at most once per successful read.</param>
    public CipTypeResolver(
        L5X? project,
        Func<CancellationToken, Task<IReadOnlyList<TagInfo>>> listTags)
        : this(project is null ? null : new StaticTypeSource(project), listTags)
    {
    }

    private CipTypeResolver(
        ISessionTypeSource? types,
        Func<CancellationToken, Task<IReadOnlyList<TagInfo>>> listTags)
    {
        _types    = types;
        _listTags = listTags ?? throw new ArgumentNullException(nameof(listTags));
    }

    /// <summary>
    /// Creates a resolver over the session's own type lookup (the one the SDK route uses), so a tag the
    /// lookup learns about (a refresh after a publish) is typed here too, and a tag it does not yet
    /// know is refreshed once before it is called unknown.
    /// </summary>
    /// <param name="types">The session's declared-type lookup.</param>
    /// <param name="listTags">Reads the controller tag list (<c>@tags</c>).</param>
    /// <returns>The resolver.</returns>
    public static CipTypeResolver ForSession(
        ISessionTypeSource types,
        Func<CancellationToken, Task<IReadOnlyList<TagInfo>>> listTags)
    {
        ArgumentNullException.ThrowIfNull(types);
        return new CipTypeResolver(types, listTags);
    }

    /// <summary>True when <paramref name="typeName"/> is a Logix atomic type.</summary>
    public static bool IsAtomic(string typeName) => AtomicTypes.Contains(typeName);

    /// <summary>
    /// Resolves <paramref name="tagPath"/> (e.g. <c>Probe_Udt.Speed</c>,
    /// <c>Arr[3].Count</c>, <c>Program:Main.Tag</c>) to a type.
    /// Never throws for a path it cannot resolve; returns an unresolved result.
    /// </summary>
    public async Task<CipTypeResolution> ResolveAsync(
        string tagPath, CancellationToken cancellationToken = default)
    {
        if (!TryParsePath(tagPath, out var segments, out var parseError))
            return CipTypeResolution.Unresolved(parseError!);

        // (1) Project source: the session's type lookup. A root tag it does not know may have been
        // created after the project was loaded (a publish), so ask the lookup to refresh it once
        // (a partial re-export of that tag) and look again before falling through.
        if (_types is not null)
        {
            var fromProject = _types.Lookup(project => ResolveFollowingAlias(project, tagPath, segments));
            if (fromProject is null && await _types.RefreshAsync(tagPath, cancellationToken).ConfigureAwait(false))
                fromProject = _types.Lookup(project => ResolveFollowingAlias(project, tagPath, segments));
            if (fromProject is not null)
                return fromProject;
        }

        // (2) Tag list — whole controller-scoped atomic tag only.
        if (segments.Count == 1 && segments[0].IndexCount == 0 && !segments[0].IsProgramScoped)
        {
            var list = await GetTagListAsync(cancellationToken).ConfigureAwait(false);
            if (list is not null && list.TryGetValue(segments[0].Name, out var type))
                return new CipTypeResolution(
                    CipTypeKind.Atomic, type, CipTypeSource.TagList, Array.Empty<string>());
        }

        // (3) Unresolved — never size inference.
        return CipTypeResolution.Unresolved(
            $"Type of '{tagPath}' is not known: it is not declared in an open project and is not a whole " +
            "controller-scoped atomic tag in the controller tag list.");
    }

    // ── Tag list ──────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>?> GetTagListAsync(CancellationToken ct)
    {
        if (_tagListCache is not null) return _tagListCache;

        await _tagListLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tagListCache is not null) return _tagListCache;

            IReadOnlyList<TagInfo> tags;
            try
            {
                tags = await _listTags(ct).ConfigureAwait(false);
            }
            catch (TagServiceException)
            {
                return null; // not cached: a later call may retry
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tags)
                if (t.DataType is not null && IsAtomic(t.DataType))
                    map[t.Name] = t.DataType.ToUpperInvariant();

            _tagListCache = map;
            return map;
        }
        finally
        {
            _tagListLock.Release();
        }
    }

    // ── Project walk ──────────────────────────────────────────────────────────

    /// <summary>
    /// Types <paramref name="tagPath"/>; when it names an alias tag (controller or program scope, optionally with a
    /// member or bit suffix) the type is the base's and <see cref="CipTypeResolution.ResolvedFrom"/> names the base.
    /// An alias that cannot be followed is <see cref="CipTypeResolution.IsAliasUnresolved"/>, never a guess.
    /// </summary>
    private static CipTypeResolution? ResolveFollowingAlias(
        L5X project, string tagPath, IReadOnlyList<PathSegment> segments)
    {
        // Module I/O (<Module>:I / :O / :C) is typed from the module's connection structure, never from tags.
        var moduleIo = ModuleIoTypes.Resolve(project, segments);
        if (moduleIo is not null)
            return moduleIo;

        var alias = AliasReadResolver.Resolve(project, tagPath);
        if (alias is null)
            return ResolveFromProject(project, segments);

        if (!alias.IsResolved && alias.ModuleIoTarget is not null
            && TryParsePath(alias.ModuleIoTarget, out var ioSegments, out _))
        {
            // The alias ends at module I/O: type it from the module definitions.
            var io = ModuleIoTypes.Resolve(project, ioSegments);
            if (io is { IsResolved: true })
                return io with { ResolvedFrom = alias.ModuleIoTarget };
        }

        if (!alias.IsResolved)
            return CipTypeResolution.AliasUnresolved($"Alias '{tagPath}' is unresolved: {alias.Failure}.");

        if (!TryParsePath(alias.Target!, out var targetSegments, out var parseError))
            return CipTypeResolution.AliasUnresolved(
                $"Alias '{tagPath}' resolves to '{alias.Target}', which is not a valid path: {parseError}");

        var resolved = ResolveFromProject(project, targetSegments);
        if (resolved is null)
            return CipTypeResolution.AliasUnresolved(
                $"Alias '{tagPath}' resolves to '{alias.Target}', which is not declared in the open project.");
        return resolved with { ResolvedFrom = alias.Target };
    }

    /// <summary>Returns <c>null</c> when the root tag is not in the project (so the next source may answer).</summary>
    internal static CipTypeResolution? ResolveFromProject(L5X project, IReadOnlyList<PathSegment> segments)
    {
        var root = segments[0];
        var tag  = FindRootTag(project, root);
        if (tag is null) return null;

        var typeName = DeclaredMemberTypes.DeclaredTypeOf(tag);
        var rank     = DeclaredMemberTypes.RankOf(tag);
        var current  = root;

        for (var i = 0; ; i++)
        {
            // Apply the array indices of the current segment.
            if (current.IndexCount != rank)
                return CipTypeResolution.Unresolved(rank == 0
                    ? $"'{current.Name}' is not an array but is indexed."
                    : current.IndexCount == 0
                        ? $"'{current.Name}' is an array; reading or writing it as a whole is not supported. Index an element."
                        : $"'{current.Name}' has {rank} dimension(s) but {current.IndexCount} index(es) were given.");

            if (i == segments.Count - 1)
                return Classify(project, typeName);

            var next = segments[i + 1];

            if (IsAtomic(typeName))
            {
                // Bit selector on an integer: DINT_Tag.5 -> BOOL.
                if (IntegerTypes.Contains(typeName) && next.IndexCount == 0 && next.IsNumeric
                    && i + 1 == segments.Count - 1)
                    return new CipTypeResolution(
                        CipTypeKind.Atomic, "BOOL", CipTypeSource.Project, Array.Empty<string>());

                return CipTypeResolution.Unresolved(
                    $"'{next.Name}' is not a member of atomic type {typeName}.");
            }

            var dt = FindDataType(project, typeName);
            if (dt is null)
            {
                // Not a user data type: a predefined type (TIMER.ACC) or an add-on instruction
                // (parameters, local tags), typed by the same lookup the SDK route uses.
                var declared = new DeclaredMemberTypes(project).Resolve(typeName, next.Name);
                if (declared is null)
                    return CipTypeResolution.Unresolved(
                        $"Type '{typeName}' is not defined in the open project, so member '{next.Name}' cannot be typed.");

                typeName = declared;
                rank     = 0;
                current  = next;
                continue;
            }

            var member = dt.Members.FirstOrDefault(m =>
                string.Equals(m.Name, next.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null)
                return CipTypeResolution.Unresolved($"Type '{typeName}' has no member '{next.Name}'.");

            // A BOOL member of a UDT is declared as BIT (packed into a hidden SINT host).
            // The controller reads and writes it by name as a BOOL.
            typeName = string.Equals(member.DataType, "BIT", StringComparison.OrdinalIgnoreCase)
                ? "BOOL" : member.DataType;
            rank     = member.Dimension.IsEmpty ? 0 : (int)member.Dimension.Rank;
            current  = next;
        }
    }

    private static CipTypeResolution Classify(L5X project, string typeName)
    {
        if (IsAtomic(typeName))
            return new CipTypeResolution(
                CipTypeKind.Atomic, typeName.ToUpperInvariant(), CipTypeSource.Project, Array.Empty<string>());

        var members = new List<string>();
        CollectMemberPaths(project, typeName, prefix: "", depth: 0, members);
        return new CipTypeResolution(CipTypeKind.Structure, typeName, CipTypeSource.Project, members);
    }

    private static void CollectMemberPaths(
        L5X project, string typeName, string prefix, int depth, List<string> into)
    {
        if (depth >= MaxMemberDepth) return;
        var dt = FindDataType(project, typeName);
        if (dt is null) return;

        foreach (var m in dt.Members)
        {
            if (m.Hidden) continue;
            var path = prefix + m.Name;
            var arr  = !m.Dimension.IsEmpty;
            if (!arr && !IsAtomic(m.DataType) && FindDataType(project, m.DataType) is not null
                && depth + 1 < MaxMemberDepth)
                CollectMemberPaths(project, m.DataType, path + ".", depth + 1, into);
            else
                into.Add(arr ? path + "[]" : path);
        }
    }

    private static DataType? FindDataType(L5X project, string name) =>
        project.DataTypes.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Tag? FindRootTag(L5X project, PathSegment root)
    {
        if (root.IsProgramScoped)
        {
            var program = project.Programs.FirstOrDefault(p =>
                string.Equals(p.Name, root.Program, StringComparison.OrdinalIgnoreCase));
            return program?.Tags.FirstOrDefault(t =>
                string.Equals(t.Name.ToString(), root.Name, StringComparison.OrdinalIgnoreCase));
        }

        return project.Tags.FirstOrDefault(t =>
            string.Equals(t.Name.ToString(), root.Name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Path parsing ──────────────────────────────────────────────────────────

    internal sealed record PathSegment(string Name, int IndexCount, string? Program)
    {
        public bool IsProgramScoped => Program is not null;
        public bool IsNumeric => Name.Length > 0 && Name.All(char.IsDigit);
    }

    internal static bool TryParsePath(
        string path, out List<PathSegment> segments, out string? error)
    {
        segments = new List<PathSegment>();
        error    = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Tag path is empty.";
            return false;
        }

        var text    = path.Trim();
        string? program = null;
        if (text.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            var dot = text.IndexOf('.');
            if (dot <= "Program:".Length)
            {
                error = $"Tag path '{path}' has a program prefix but no tag name.";
                return false;
            }
            program = text["Program:".Length..dot];
            text    = text[(dot + 1)..];
        }

        // Split on '.' outside brackets.
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '.' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);

        if (depth != 0)
        {
            error = $"Tag path '{path}' has unbalanced brackets.";
            return false;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var br   = part.IndexOf('[');
            var name = br < 0 ? part : part[..br];
            var idx  = 0;
            if (br >= 0)
            {
                if (!part.EndsWith(']'))
                {
                    error = $"Tag path '{path}' has a malformed index in '{part}'.";
                    return false;
                }
                var inner = part[(br + 1)..^1];
                idx = inner.Length == 0 ? 0 : inner.Split(',').Length;
            }

            if (name.Length == 0)
            {
                error = $"Tag path '{path}' has an empty segment.";
                return false;
            }

            segments.Add(new PathSegment(name, idx, i == 0 ? program : null));
        }

        return true;
    }
}
