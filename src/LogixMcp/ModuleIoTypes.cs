// ── ModuleIoTypes — types module I/O tags from the L5X module connection structures ──
using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// Builds the type of module I/O tag paths (<c>VFD_MainDrive:I.Ready</c>, <c>Local:1:I.Pt00.Data</c>,
/// <c>Rack:3:O.Data[0]</c>, <c>Drive:C.Cfg</c>) from the module's
/// <c>Communications &gt; Connections &gt; Connection &gt; InputTag / OutputTag</c> and
/// <c>Communications &gt; ConfigTag</c> elements. Real Logix v38 exports give those elements no <c>Name</c>:
/// the tag name is derived (<c>&lt;Module&gt;:I|O|C</c> for a remote module, <c>&lt;Parent&gt;:&lt;Slot&gt;:I|O|C</c>
/// for a module in a backplane slot); a <c>Name</c> attribute, when present, is used as written. Those elements carry a decorated <c>&lt;Structure&gt;</c>
/// (<c>DataValueMember</c>, <c>StructureMember</c>, <c>ArrayMember</c>) when the export includes one; a
/// structure with no members is completed from a module-defined (<c>Class="IO"</c>) data type of the same
/// name in the project when there is one. A module whose export carries only a data blob has no structure:
/// the result is unresolved with a message that names the module and says
/// <c>module I/O structure not in this export</c>.
/// </summary>
internal static class ModuleIoTypes
{
    /// <summary>Phrase the unresolved reason carries for a module whose I/O structure the export lacks.</summary>
    internal const string NotInExport = "module I/O structure not in this export";

    private const int MaxMemberDepth = 4;

    /// <summary>
    /// True when <paramref name="rootName"/> has the shape of a module I/O tag: a <c>:</c>-separated name
    /// whose last part is <c>I</c>, <c>O</c> or <c>C</c> (<c>Mod:I</c>, <c>Local:1:O</c>).
    /// </summary>
    /// <param name="rootName">Tag name without member path.</param>
    /// <returns>True for module I/O shaped names.</returns>
    internal static bool IsModuleIoName(string rootName)
    {
        if (rootName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)) return false;
        var colon = rootName.LastIndexOf(':');
        if (colon <= 0 || colon != rootName.Length - 2) return false;
        return char.ToUpperInvariant(rootName[^1]) is 'I' or 'O' or 'C';
    }

    /// <summary>
    /// True when <paramref name="tagPath"/> names module I/O (<c>Mod:I</c>, <c>Local:1:O.Data</c>, ...).
    /// Module I/O is read-only through this server.
    /// </summary>
    /// <param name="tagPath">A tag path, possibly with member, index or bit parts.</param>
    /// <returns>True when the root name is module I/O shaped.</returns>
    internal static bool IsModuleIoPath(string tagPath)
    {
        if (string.IsNullOrWhiteSpace(tagPath)) return false;
        var text = tagPath.Trim();
        var end = text.IndexOfAny(['.', '[']);
        return IsModuleIoName(end < 0 ? text : text[..end]);
    }

    /// <summary>
    /// Types a module I/O path. Returns <c>null</c> when the path is not module I/O shaped.
    /// </summary>
    /// <param name="project">The loaded project.</param>
    /// <param name="segments">The parsed tag path.</param>
    /// <returns>The resolution, unresolved with a module-naming reason when no structure is available; or <c>null</c>.</returns>
    internal static CipTypeResolution? Resolve(L5X project, IReadOnlyList<CipTypeResolver.PathSegment> segments)
    {
        var root = segments[0];
        if (root.IsProgramScoped || !IsModuleIoName(root.Name)) return null;

        var (moduleName, tag) = FindConnectionTag(project, root.Name);
        moduleName ??= root.Name[..root.Name.LastIndexOf(':')];

        var structure = tag?.Descendants("Structure").FirstOrDefault();
        var node = structure is null ? null : BuildNode(project, structure, 0);
        if (node is null || !node.HasMembers(project))
            return CipTypeResolution.Unresolved(
                $"Module '{moduleName}' (tag '{root.Name}'): {NotInExport}, so '{string.Join('.', segments.Select(s => s.Name))}' " +
                "cannot be typed. The export holds only a data blob for this module.");

        return Walk(project, node, segments);
    }

    /// <summary>Outcome of <see cref="ReadValue"/>.</summary>
    /// <param name="IsModule">False when the root names no module in the project (the caller reports not found).</param>
    /// <param name="Value">The decoded value (int, long, float, bool ...); <c>null</c> when the file holds none.</param>
    /// <param name="TypeName">The type of the path when it could be typed; otherwise <c>null</c>.</param>
    /// <param name="Reason">Why there is no value, naming the module and the type; <c>null</c> when <see cref="Value"/> is set.</param>
    internal sealed record ValueReading(bool IsModule, object? Value, string? TypeName, string? Reason);

    /// <summary>
    /// Reads the value of a module I/O path from the decorated <c>Structure</c> the offline file carries for the
    /// module's connection tag (<c>DataValueMember Value="..."</c>). A module whose export holds only a data
    /// blob, a member the structure lacks or a structure-typed path has no value here.
    /// </summary>
    /// <param name="project">The loaded project.</param>
    /// <param name="segments">The parsed module I/O tag path (atomic leaf; a bit is read from its parent integer).</param>
    /// <returns>The reading; <see cref="ValueReading.Value"/> is <c>null</c> with a <see cref="ValueReading.Reason"/> when the file has none.</returns>
    internal static ValueReading ReadValue(L5X project, IReadOnlyList<CipTypeResolver.PathSegment> segments)
    {
        var root = segments[0];
        var (moduleName, tag) = FindConnectionTag(project, root.Name);
        if (moduleName is null && tag is null)
            return new ValueReading(false, null, null, null);

        var path   = string.Join('.', segments.Select(s => s.Name));
        var typed  = Resolve(project, segments);
        var type   = typed is { IsResolved: true } ? typed.TypeName : null;
        ValueReading None(string why) => new(true, null, type, why);

        if (typed is null || !typed.IsResolved)
            return None($"Module I/O '{path}' (module '{moduleName}') has no value in the offline file: {typed?.Reason ?? NotInExport}.");

        string Named() => $"Module I/O '{path}' (type {type}, module '{moduleName}')";

        var element = tag!.Elements("Data")
                          .FirstOrDefault(d => string.Equals(d.Attribute("Format")?.Value, "Decorated", StringComparison.OrdinalIgnoreCase))
                          ?.Element("Structure")
                      ?? tag.Descendants("Structure").FirstOrDefault();
        if (element is null)
            return None($"{Named()} has no value in the offline file: it holds no decorated data.");

        XElement? leaf = element;
        for (var i = 1; i < segments.Count && leaf is not null; i++)
        {
            var seg    = segments[i];
            var member = leaf.Elements().FirstOrDefault(m => Same(m.Attribute("Name")?.Value, seg.Name));
            if (member is null) { leaf = null; break; }
            if (seg.IndexCount > 0)
            {
                // The path carries no index values here, so an indexed member cannot be matched to an Element.
                leaf = null;
                break;
            }
            leaf = member;
        }

        var text = leaf is not null && leaf.Name.LocalName == "DataValueMember" ? leaf.Attribute("Value")?.Value : null;
        if (text is null)
            return None($"{Named()} has no value in the offline file.");

        if (type is null || !CipTypeResolver.IsAtomic(type))
            return None($"{Named()} is not a single value.");

        return TryParseValue(text, type, out var value)
            ? new ValueReading(true, value, type, null)
            : None($"{Named()} has an unreadable value '{text}' in the offline file.");
    }

    /// <summary>Parses a decorated value (<c>2#0101</c>, <c>16#FF</c>, <c>-3</c>, <c>1.5</c>) as the Logix type <paramref name="type"/>.</summary>
    private static bool TryParseValue(string text, string type, out object? value)
    {
        value = null;
        text = text.Trim().Replace("_", "");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var t   = type.ToUpperInvariant();

        if (t is "REAL" or "LREAL")
        {
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, inv, out var d)) return false;
            value = t == "REAL" ? (object)(float)d : d;
            return true;
        }

        var radix = 10;
        var hash  = text.IndexOf('#');
        if (hash > 0)
        {
            if (!int.TryParse(text[..hash], out radix) || radix is not (2 or 8 or 10 or 16)) return false;
            text = text[(hash + 1)..];
        }

        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        ulong bits;
        try { bits = Convert.ToUInt64(text, radix); }
        catch (Exception) { return false; }
        if (negative) bits = unchecked((ulong)(-(long)bits));

        // A binary/hex literal of a signed type holds the raw bit pattern; narrow with unchecked casts.
        value = t switch
        {
            "BOOL"  => bits != 0,
            "SINT"  => unchecked((sbyte)bits),
            "INT"   => unchecked((short)bits),
            "DINT"  => unchecked((int)bits),
            "LINT"  => unchecked((long)bits),
            "USINT" => unchecked((byte)bits),
            "UINT"  => unchecked((ushort)bits),
            "UDINT" => unchecked((uint)bits),
            "ULINT" => bits,
            _       => null,
        };
        return value is not null;
    }

    // ── Locating the connection tag ───────────────────────────────────────────

    private static (string? Module, XElement? Tag) FindConnectionTag(L5X project, string tagName)
    {
        var modules = project.Modules.Select(m => m.Serialize()).ToList();
        var byName  = modules
            .Where(m => m.Attribute("Name") is not null)
            .GroupBy(m => m.Attribute("Name")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var xml in modules)
        {
            var name  = xml.Attribute("Name")?.Value;
            var tagBase = TagBaseName(xml, byName);
            var comms = xml.Elements("Communications").ToList();

            // A tag's own Name attribute wins (hand-built or older exports); real Logix exports omit it and
            // the name is derived: <Base>:I for InputTag, <Base>:O for OutputTag, <Base>:C for ConfigTag.
            foreach (var conn in comms.Elements("Connections").Elements("Connection"))
            {
                foreach (var (element, suffix) in conn.Elements().Select(e => (e, e.Name.LocalName switch
                         { "InputTag" => ":I", "OutputTag" => ":O", _ => (string?)null })))
                {
                    if (suffix is null) continue;
                    var derived = element.Attribute("Name")?.Value ?? (tagBase is null ? null : tagBase + suffix);
                    if (Same(derived, tagName)) return (name, element);
                }
            }

            var config = comms.Elements("ConfigTag").FirstOrDefault();
            if (config is not null)
            {
                var configName = config.Attribute("Name")?.Value ?? (tagBase is null ? null : tagBase + ":C");
                if (Same(configName, tagName)) return (name, config);
            }
        }

        // Not a connection tag of any module; name the module from a module whose tag base shares the prefix.
        var prefix = tagName[..tagName.LastIndexOf(':')];
        var owner  = modules
            .Where(m => Same(m.Attribute("Name")?.Value, prefix) || Same(TagBaseName(m, byName), prefix))
            .Select(m => m.Attribute("Name")?.Value)
            .FirstOrDefault();
        return (owner, null);
    }

    /// <summary>
    /// The prefix Logix gives a module's I/O tags: <c>&lt;Parent&gt;:&lt;Slot&gt;</c> when the module sits in a
    /// backplane port (the parent's port named by <c>ParentModPortId</c> carries a <c>Bus</c>, e.g.
    /// <c>Local:1</c>, <c>Rack:3</c>), otherwise the module name (<c>VFD_MainDrive</c>).
    /// </summary>
    private static string? TagBaseName(XElement module, IReadOnlyDictionary<string, XElement> byName)
    {
        var name = module.Attribute("Name")?.Value;
        if (name is null) return null;

        var parentName = module.Attribute("ParentModule")?.Value;
        if (parentName is null || Same(parentName, name) || !byName.TryGetValue(parentName, out var parent))
            return name;

        var portId     = module.Attribute("ParentModPortId")?.Value;
        var parentPort = parent.Elements("Ports").Elements("Port").FirstOrDefault(p => p.Attribute("Id")?.Value == portId);
        var isBackplane = parentPort is not null
                          && (parentPort.Element("Bus") is not null
                              || parentPort.Attribute("Type")?.Value is "ICP" or "5069" or "PointIO");
        if (!isBackplane) return name;

        var slot = module.Elements("Ports").Elements("Port")
                         .FirstOrDefault(p => string.Equals(p.Attribute("Upstream")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                         ?.Attribute("Address")?.Value;
        return int.TryParse(slot, out _) ? $"{parentName}:{slot}" : name;
    }

    // ── Structure model ───────────────────────────────────────────────────────

    private sealed record Node(string DataType, int Rank, IReadOnlyDictionary<string, Node>? Members)
    {
        public bool HasMembers(L5X project) =>
            (Members is { Count: > 0 }) || FindDataType(project, DataType)?.Members.Any() == true;
    }

    private static Node BuildNode(L5X project, XElement element, int depth)
    {
        var dataType = element.Attribute("DataType")?.Value ?? "";
        var members  = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        if (depth < MaxMemberDepth)
        {
            foreach (var m in element.Elements())
            {
                var name = m.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                var type = m.Attribute("DataType")?.Value ?? "";

                switch (m.Name.LocalName)
                {
                    case "DataValueMember":
                        members[name] = new Node(type, 0, null);
                        break;
                    case "StructureMember":
                        members[name] = BuildNode(project, m, depth + 1);
                        break;
                    case "ArrayMember":
                    {
                        var rank = RankOf(m.Attribute("Dimensions")?.Value ?? m.Attribute("Dimension")?.Value);
                        // Structure-typed arrays carry their layout in the first element's Structure.
                        var elementStructure = m.Elements("Element").Select(e => e.Element("Structure")).FirstOrDefault(s => s is not null);
                        var inner = elementStructure is null ? null : BuildNode(project, elementStructure, depth + 1);
                        members[name] = new Node(type.Length > 0 ? type : inner?.DataType ?? "", rank, inner?.Members);
                        break;
                    }
                }
            }
        }
        return new Node(dataType, 0, members);
    }

    private static int RankOf(string? dimensions) =>
        string.IsNullOrWhiteSpace(dimensions) || dimensions.Trim() == "0" ? 0 : dimensions.Split(',').Length;

    // ── Walking a member path ─────────────────────────────────────────────────

    private static CipTypeResolution Walk(
        L5X project, Node root, IReadOnlyList<CipTypeResolver.PathSegment> segments)
    {
        var current = root;
        var rank    = 0;
        var at      = segments[0];

        if (at.IndexCount != 0)
            return CipTypeResolution.Unresolved($"'{at.Name}' is not an array but is indexed.");

        for (var i = 1; i < segments.Count; i++)
        {
            var next = segments[i];

            if (CipTypeResolver.IsAtomic(current.DataType))
            {
                if (IsInteger(current.DataType) && next.IndexCount == 0 && next.IsNumeric && i == segments.Count - 1)
                    return Atomic("BOOL");
                return CipTypeResolution.Unresolved($"'{next.Name}' is not a member of atomic type {current.DataType}.");
            }

            var member = MemberOf(project, current, next.Name);
            if (member is null)
                return CipTypeResolution.Unresolved(
                    $"Module I/O type '{current.DataType}' has no member '{next.Name}'.");

            current = member;
            rank    = member.Rank;
            if (next.IndexCount != rank)
                return CipTypeResolution.Unresolved(rank == 0
                    ? $"'{next.Name}' is not an array but is indexed."
                    : next.IndexCount == 0
                        ? $"'{next.Name}' is an array; reading or writing it as a whole is not supported. Index an element."
                        : $"'{next.Name}' has {rank} dimension(s) but {next.IndexCount} index(es) were given.");
        }

        var type = string.Equals(current.DataType, "BIT", StringComparison.OrdinalIgnoreCase) ? "BOOL" : current.DataType;
        if (CipTypeResolver.IsAtomic(type)) return Atomic(type.ToUpperInvariant());

        if (type.Length == 0)
            return CipTypeResolution.Unresolved("The module I/O member has no declared data type.");

        var paths = new List<string>();
        CollectMemberPaths(project, current, "", 0, paths);
        return new CipTypeResolution(CipTypeKind.Structure, type, CipTypeSource.Project, paths);
    }

    private static Node? MemberOf(L5X project, Node node, string name)
    {
        if (node.Members is { Count: > 0 } && node.Members.TryGetValue(name, out var found))
            return found;
        if (node.Members is { Count: > 0 }) return null;

        // No inline members: use the module-defined data type of that name, when the project has one.
        var dt = FindDataType(project, node.DataType);
        var m  = dt?.Members.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (m is null) return null;
        return new Node(m.DataType, m.Dimension.IsEmpty ? 0 : (int)m.Dimension.Rank, null);
    }

    private static void CollectMemberPaths(L5X project, Node node, string prefix, int depth, List<string> into)
    {
        if (depth >= MaxMemberDepth) return;
        var names = node.Members is { Count: > 0 }
            ? node.Members.Select(kv => (kv.Key, kv.Value)).ToList()
            : (FindDataType(project, node.DataType)?.Members
                   .Where(x => !x.Hidden)
                   .Select(x => (x.Name.ToString(), new Node(x.DataType, x.Dimension.IsEmpty ? 0 : (int)x.Dimension.Rank, null)))
                   .ToList() ?? []);

        foreach (var (name, child) in names)
        {
            var path = prefix + name;
            if (child.Rank == 0 && !CipTypeResolver.IsAtomic(child.DataType) && !string.Equals(child.DataType, "BIT", StringComparison.OrdinalIgnoreCase)
                && child.HasMembers(project) && depth + 1 < MaxMemberDepth)
                CollectMemberPaths(project, child, path + ".", depth + 1, into);
            else
                into.Add(child.Rank > 0 ? path + "[]" : path);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CipTypeResolution Atomic(string type) =>
        new(CipTypeKind.Atomic, type, CipTypeSource.Project, Array.Empty<string>());

    private static bool IsInteger(string type) =>
        type.ToUpperInvariant() is "SINT" or "INT" or "DINT" or "LINT" or "USINT" or "UINT" or "UDINT" or "ULINT";

    private static DataType? FindDataType(L5X project, string name) =>
        name.Length == 0 ? null : project.DataTypes.FirstOrDefault(d => Same(d.Name.ToString(), name));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
