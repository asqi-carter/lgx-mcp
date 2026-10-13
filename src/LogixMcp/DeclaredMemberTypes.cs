using System.Xml.Linq;
using L5Sharp.Core;

/// <summary>
/// Resolves the declared Logix type of a member path (<c>ACC</c>, <c>Inner.Speed</c>,
/// <c>Arr[2].Count</c>, <c>Word.3</c>) beneath a base type, from the project's own XML and a
/// fixed table of predefined types. It never calls L5Sharp's <c>TryGet</c>/<c>Find</c>/index,
/// which tokenise the whole project and throw when any rung is unparsable.
/// </summary>
/// <remarks>
/// Lookup order for each type: user <c>DataType</c> definitions, then
/// <c>AddOnInstructionDefinition</c> parameters and local tags (read from the raw XML), then
/// the predefined types (TIMER, COUNTER, CONTROL, STRING).
/// </remarks>
public sealed class DeclaredMemberTypes
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Predefined =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["TIMER"] = Members(("PRE", "DINT"), ("ACC", "DINT"),
                ("EN", "BOOL"), ("TT", "BOOL"), ("DN", "BOOL")),
            ["COUNTER"] = Members(("PRE", "DINT"), ("ACC", "DINT"),
                ("CU", "BOOL"), ("CD", "BOOL"), ("DN", "BOOL"), ("OV", "BOOL"), ("UN", "BOOL")),
            ["CONTROL"] = Members(("LEN", "DINT"), ("POS", "DINT"),
                ("EN", "BOOL"), ("EU", "BOOL"), ("DN", "BOOL"), ("EM", "BOOL"),
                ("ER", "BOOL"), ("UL", "BOOL"), ("IN", "BOOL"), ("FD", "BOOL")),
            ["STRING"] = Members(("LEN", "DINT"), ("DATA", "SINT")),
        };

    /// <summary>The members (name to type) of the predefined types TIMER, COUNTER, CONTROL and STRING.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PredefinedMembers => Predefined;

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT",
    };

    private readonly L5X _project;
    private Dictionary<string, IReadOnlyDictionary<string, string>>? _aoiMembers;

    /// <summary>Creates a resolver over <paramref name="project"/>'s definitions.</summary>
    /// <param name="project">The project holding DataType and AOI definitions.</param>
    public DeclaredMemberTypes(L5X project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
    }

    /// <summary>
    /// Resolves the declared type of <paramref name="memberPath"/> beneath
    /// <paramref name="baseType"/>.
    /// </summary>
    /// <param name="baseType">Declared type of the root tag (e.g. <c>TIMER</c>, <c>MyUdt</c>, an AOI name).</param>
    /// <param name="memberPath">Dot-separated path; array subscripts and bit numbers are accepted.</param>
    /// <returns>The member's type name, or <c>null</c> when any segment cannot be resolved.</returns>
    public string? Resolve(string baseType, string memberPath)
    {
        var current = baseType;
        foreach (var raw in memberPath.Split('.'))
        {
            var bracket = raw.IndexOf('[');
            var name    = bracket >= 0 ? raw[..bracket] : raw;
            if (name.Length == 0) return null;

            // Bit select on an integer (Word.3) yields a BOOL.
            if (name.All(char.IsAsciiDigit))
            {
                if (!IntegerTypes.Contains(current)) return null;
                current = "BOOL";
                continue;
            }

            var members = MembersOf(current);
            if (members is null || !members.TryGetValue(name, out var next)) return null;
            current = next;
        }

        return current;
    }

    /// <summary>
    /// The declared type of <paramref name="tag"/>, read from the tag's own <c>DataType</c> attribute
    /// (no value decoding, no project index). Same rule the SDK route applies to its tags.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The Logix type name.</returns>
    public static string DeclaredTypeOf(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Serialize().Attribute("DataType")?.Value ?? tag.DataType;
    }

    /// <summary>
    /// Number of array dimensions <paramref name="tag"/> declares (0 for a scalar), read from the tag's own
    /// <c>Dimensions</c> attribute (<c>"4"</c>, <c>"2 3"</c>, <c>"2,3,4"</c>).
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The rank.</returns>
    public static int RankOf(Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var raw = tag.Serialize().Attribute("Dimensions")?.Value;
        if (raw is null) return tag.Dimensions.IsEmpty ? 0 : (int)tag.Dimensions.Rank;
        var dims = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(d => d != "0");
        return dims.Count();
    }

    private IReadOnlyDictionary<string, string>? MembersOf(string typeName)
        => UserDataTypeMembers(typeName)
           ?? AoiMembers(typeName)
           ?? (Predefined.TryGetValue(typeName, out var predefined) ? predefined : null);

    private IReadOnlyDictionary<string, string>? UserDataTypeMembers(string typeName)
    {
        var dt = _project.DataTypes.AsEnumerable().FirstOrDefault(
            d => string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (dt is null) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in dt.Members.Where(m => !m.Hidden))
            map[m.Name] = m.DataType;
        return map;
    }

    private IReadOnlyDictionary<string, string>? AoiMembers(string typeName)
    {
        _aoiMembers ??= LoadAoiMembers();
        return _aoiMembers.TryGetValue(typeName, out var members) ? members : null;
    }

    /// <summary>Reads every AOI's parameters and local tags from the raw project XML.</summary>
    private Dictionary<string, IReadOnlyDictionary<string, string>> LoadAoiMembers()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var aoi in _project.Content.Serialize().Descendants("AddOnInstructionDefinition"))
        {
            var name = aoi.Attribute("Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in aoi.Elements("Parameters").Elements("Parameter")
                         .Concat(aoi.Elements("LocalTags").Elements("LocalTag")))
            {
                var memberName = el.Attribute("Name")?.Value;
                var dataType   = el.Attribute("DataType")?.Value;
                if (!string.IsNullOrEmpty(memberName) && !string.IsNullOrEmpty(dataType))
                    map[memberName] = dataType;
            }

            result[name] = map;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> Members(params (string Name, string Type)[] members)
        => members.ToDictionary(m => m.Name, m => m.Type, StringComparer.OrdinalIgnoreCase);
}
