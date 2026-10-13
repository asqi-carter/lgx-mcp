using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>
/// A single component-level difference entry returned by <see cref="DiffProjectsHandler.Execute"/>.
/// </summary>
/// <param name="Name">
/// Component name. For routines this is formatted as <c>ProgramName/RoutineName</c>.
/// </param>
/// <param name="Change">
/// Classification of the change: <c>"added"</c> (present in B but not A),
/// <c>"removed"</c> (present in A but not B), or <c>"changed"</c> (same name, different definition).
/// </param>
/// <param name="ChangedByThisSession">
/// <c>true</c> only in <c>logix_diff_against_controller</c> output, for entries this session
/// changed live; <c>null</c> (omitted from JSON) otherwise.
/// </param>
/// <param name="Rungs">
/// Present only in <c>logix_diff_against_controller</c> output, on <c>"changed"</c> ladder routines:
/// the rungs that differ. Omitted from JSON otherwise.
/// </param>
public record DiffEntry(
    [property: JsonPropertyName("name")]   string Name,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("changedByThisSession"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ChangedByThisSession = null,
    [property: JsonPropertyName("rungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RungDiff>? Rungs = null);

/// <summary>
/// One differing rung of a changed routine. <paramref name="Before"/> and <paramref name="After"/>
/// are customer-authored rung text wrapped with <see cref="UntrustedWrapper.WrapRungText"/>.
/// </summary>
/// <param name="Number">Zero-based rung number.</param>
/// <param name="Change">
/// <c>"added"</c> (rung only in the "after"), <c>"removed"</c> (only in the "before") or
/// <c>"changed"</c> (same number, different text).
/// </param>
/// <param name="Before">Wrapped rung text from the "before" side; <c>null</c> when <c>added</c>.</param>
/// <param name="After">Wrapped rung text from the "after" side; <c>null</c> when <c>removed</c>.</param>
public record RungDiff(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("before"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Before,
    [property: JsonPropertyName("after"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? After);

/// <summary>
/// Strongly-typed success response returned by <see cref="DiffProjectsHandler.Execute"/>.
/// </summary>
/// <param name="Tags">Controller-scoped tag differences.</param>
/// <param name="DataTypes">User-defined data type differences.</param>
/// <param name="Routines">Routine differences, keyed as <c>ProgramName/RoutineName</c>.</param>
/// <param name="Normalized">
/// What was normalized away and therefore not compared (see <see cref="DiffProjectsHandler.NormalizedList"/>);
/// empty when normalization was switched off.
/// </param>
public record DiffProjectsResult(
    [property: JsonPropertyName("tags")]      IReadOnlyList<DiffEntry> Tags,
    [property: JsonPropertyName("dataTypes")] IReadOnlyList<DiffEntry> DataTypes,
    [property: JsonPropertyName("routines")]  IReadOnlyList<DiffEntry> Routines,
    [property: JsonPropertyName("normalized")] IReadOnlyList<string> Normalized);

/// <summary>
/// Strongly-typed error response returned by <see cref="DiffProjectsHandler.Execute"/>
/// when a path is empty, a file is missing, or an L5X parse error occurs.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record DiffProjectsError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null);

/// <summary>
/// Pure handler for the <c>logix_diff_projects</c> MCP tool.
/// Loads both L5X files independently — the <see cref="ProjectService"/> singleton
/// is never touched — and reports component-level differences grouped by kind.
/// </summary>
public static class DiffProjectsHandler
{
    /// <summary>
    /// Loads the L5X files at <paramref name="pathA"/> and <paramref name="pathB"/>
    /// independently and returns a diff grouped into <c>tags</c>, <c>dataTypes</c>,
    /// and <c>routines</c>.  Each entry is classified as <c>"added"</c>,
    /// <c>"removed"</c>, or <c>"changed"</c>.
    /// Never throws; returns <see cref="DiffProjectsError"/> on failure.
    /// </summary>
    /// <param name="pathA">Absolute or relative path to the first .L5X file (the "before").</param>
    /// <param name="pathB">Absolute or relative path to the second .L5X file (the "after").</param>
    /// <param name="normalize">
    /// When <c>true</c> (default) export-format noise is ignored (see <see cref="NormalizedList"/>);
    /// when <c>false</c> the raw serialized XML of every component is compared.
    /// </param>
    /// <returns>
    /// A <see cref="DiffProjectsResult"/> on success,
    /// or a <see cref="DiffProjectsError"/> on validation or parse failure.
    /// </returns>
    public static object Execute(string pathA, string pathB, bool normalize = true)
    {
        if (string.IsNullOrWhiteSpace(pathA))
            return new DiffProjectsError("pathA must not be empty.");
        if (string.IsNullOrWhiteSpace(pathB))
            return new DiffProjectsError("pathB must not be empty.");

        try
        {
            return Compare(SafeXml.LoadL5x(pathA), SafeXml.LoadL5x(pathB), normalize);
        }
        catch (SafeXmlException ex)
        {
            return new DiffProjectsError(ex.Message, ex.Kind);
        }
        catch (FileNotFoundException ex)
        {
            return new DiffProjectsError(ex.Message);
        }
        catch (Exception ex)
        {
            return new DiffProjectsError(ex.Message);
        }
    }

    // ------------------------------------------------------------------ //
    //  Helpers                                                             //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Diffs two already-parsed projects. Shared by <c>logix_diff_projects</c> (two files) and
    /// <c>logix_diff_against_controller</c> (a file against the session's controller image).
    /// Neither project is modified.
    /// </summary>
    /// <param name="projectA">The "before" project.</param>
    /// <param name="projectB">The "after" project.</param>
    /// <param name="normalize">
    /// <c>true</c> (default) ignores export-format noise; <c>false</c> compares raw serialized XML.
    /// </param>
    /// <returns>Component-level differences grouped into tags, data types and routines.</returns>
    public static DiffProjectsResult Compare(L5X projectA, L5X projectB, bool normalize = true)
    {
        if (!normalize)
            return new DiffProjectsResult(
                Tags:       Diff(BuildRawTagMap(projectA), BuildRawTagMap(projectB)),
                DataTypes:  Diff(BuildRawTypeMap(projectA), BuildRawTypeMap(projectB)),
                Routines:   Diff(BuildRawRoutineMap(projectA), BuildRawRoutineMap(projectB)),
                Normalized: Array.Empty<string>());

        // Controller-scoped tags
        var tagsA = projectA.Tags.ToDictionary(t => t.Name.ToString(), t => TagDeclaration(t.Serialize()));
        var tagsB = projectB.Tags.ToDictionary(t => t.Name.ToString(), t => TagDeclaration(t.Serialize()));

        // User-defined data types
        // ProductDefined / IO types are generated by the controller and differ between export
        // formats, so only User (or Class-less) types take part in the comparison.
        var typesA = BuildUserTypeMap(projectA);
        var typesB = BuildUserTypeMap(projectB);

        // Routines keyed as "ProgramName/RoutineName"
        return new DiffProjectsResult(
            Tags:      Diff(tagsA, tagsB),
            DataTypes: Diff(typesA, typesB),
            Routines:  Diff(BuildRoutineMap(projectA), BuildRoutineMap(projectB)),
            Normalized: NormalizedList);
    }

    /// <summary>
    /// The normalizations applied when <c>normalize</c> is on, reported as <c>normalized</c> in the result
    /// so a reader knows what was not compared.
    /// </summary>
    public static readonly IReadOnlyList<string> NormalizedList = new[]
    {
        "productDefinedTypes", "tagData", "opcUaAccess", "whitespace", "xmlComments"
    };

    private static Dictionary<string, string> BuildRawTagMap(L5X project) =>
        project.Tags.ToDictionary(t => t.Name.ToString(), t => Normalize(t.Serialize().ToString()));

    private static Dictionary<string, string> BuildRawTypeMap(L5X project) =>
        project.DataTypes.ToDictionary(d => d.Name, d => Normalize(d.Serialize().ToString()));

    private static Dictionary<string, string> BuildRawRoutineMap(L5X project)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in project.Programs)
            foreach (var routine in program.Routines)
                map[$"{program.Name}/{routine.Name}"] = Normalize(routine.Serialize().ToString());
        return map;
    }

    /// <summary>Tag attributes that make up a declaration, in fixed comparison order.</summary>
    private static readonly string[] TagDeclarationAttributes =
    {
        "Name", "TagType", "DataType", "Dimensions", "Radix", "Constant",
        "ExternalAccess", "AliasFor", "Usage"
    };

    /// <summary>
    /// Reduces a serialized <c>&lt;Tag&gt;</c> element to its declaration: name, TagType, DataType,
    /// Dimensions, Radix, Constant, ExternalAccess, AliasFor, Usage and the trimmed description text.
    /// <c>OpcUaAccess</c>, every <c>&lt;Data&gt;</c> element (Decorated and L5K) and attribute order
    /// are deliberately excluded, since values are not logic and those parts differ between export formats.
    /// </summary>
    /// <param name="tag">The serialized tag element.</param>
    /// <returns>A canonical string; equal strings mean equal declarations.</returns>
    internal static string TagDeclaration(System.Xml.Linq.XElement tag)
    {
        var parts = TagDeclarationAttributes
            .Select(a => $"{a}={(string?)tag.Attribute(a) ?? ""}")
            .ToList();
        var description = (string?)tag.Element("Description") ?? "";
        parts.Add($"Description={description.Trim()}");
        return string.Join("|", parts);
    }

    /// <summary>
    /// Builds a name→serialized-XML map of the user-defined data types in the project,
    /// dropping any type whose <c>Class</c> attribute is present and is not <c>User</c>
    /// (e.g. <c>ProductDefined</c> and <c>IO</c>).
    /// </summary>
    private static Dictionary<string, string> BuildUserTypeMap(L5X project)
    {
        var map = new Dictionary<string, string>();
        foreach (var dt in project.DataTypes)
        {
            var xml = dt.Serialize();
            var cls = (string?)xml.Attribute("Class");
            if (!string.IsNullOrEmpty(cls) && !string.Equals(cls, "User", StringComparison.OrdinalIgnoreCase))
                continue;
            map[dt.Name] = Normalize(xml.ToString());
        }
        return map;
    }

    /// <summary>
    /// Reduces a serialized <c>&lt;Routine&gt;</c> to its comparable content. For ladder routines this is
    /// each rung's <c>Number</c>, <c>Type</c>, <c>Text</c> and <c>Comment</c>, with whitespace collapsed and
    /// trimmed; XML comments are ignored, so a routine differs only when a rung differs or is added/removed.
    /// Routines without rungs (ST, FBD, SFC) fall back to their XML with XML comments dropped and
    /// whitespace normalized.
    /// </summary>
    /// <param name="routine">The serialized routine element.</param>
    /// <returns>A canonical string; equal strings mean equal routine content.</returns>
    internal static string RoutineContent(System.Xml.Linq.XElement routine)
    {
        var rungs = routine.Descendants("Rung").ToList();
        if (rungs.Count == 0)
        {
            var copy = new System.Xml.Linq.XElement(routine);
            copy.DescendantNodes().OfType<System.Xml.Linq.XComment>().ToList().ForEach(c => c.Remove());
            return CollapseWhitespace(Normalize(copy.ToString()));
        }

        return string.Join("\n", rungs.Select(r =>
            $"Number={(string?)r.Attribute("Number") ?? ""}" +
            $"|Type={(string?)r.Attribute("Type") ?? ""}" +
            $"|Text={CollapseWhitespace(TextOf(r, "Text"))}" +
            $"|Comment={CollapseWhitespace(TextOf(r, "Comment"))}"));
    }

    private static string TextOf(System.Xml.Linq.XElement rung, string child) =>
        string.Concat(rung.Element(child)?.Nodes().Where(n => n is not System.Xml.Linq.XComment)
            .Select(n => n is System.Xml.Linq.XElement e ? e.Value : ((System.Xml.Linq.XText)n).Value)
            ?? Enumerable.Empty<string>());

    /// <summary>Collapses every whitespace run to one space and trims.</summary>
    private static string CollapseWhitespace(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>
    /// Builds a name→serialized-XML map for every routine in the project,
    /// keying each entry as <c>ProgramName/RoutineName</c>.
    /// </summary>
    private static Dictionary<string, string> BuildRoutineMap(L5X project)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in project.Programs)
        {
            foreach (var routine in program.Routines)
            {
                var key = $"{program.Name}/{routine.Name}";
                map[key] = RoutineContent(routine.Serialize());
            }
        }
        return map;
    }

    /// <summary>
    /// Compares two name→content dictionaries and returns a sorted list of
    /// <see cref="DiffEntry"/> records classified as <c>added</c>, <c>removed</c>,
    /// or <c>changed</c>.
    /// </summary>
    private static IReadOnlyList<DiffEntry> Diff(
        Dictionary<string, string> a,
        Dictionary<string, string> b)
    {
        var results = new List<DiffEntry>();

        foreach (var (name, contentA) in a)
        {
            if (!b.TryGetValue(name, out var contentB))
                results.Add(new DiffEntry(name, "removed"));
            else if (contentA != contentB)
                results.Add(new DiffEntry(name, "changed"));
        }

        foreach (var name in b.Keys)
        {
            if (!a.ContainsKey(name))
                results.Add(new DiffEntry(name, "added"));
        }

        return results.OrderBy(e => e.Name).ToList();
    }

    /// <summary>
    /// Collapses inter-element whitespace so that cosmetic formatting
    /// differences between files do not produce false positives.
    /// CDATA content is preserved verbatim because the regex only targets
    /// whitespace between <c>&gt;</c> and <c>&lt;</c> boundaries.
    /// </summary>
    private static string Normalize(string xml) =>
        System.Text.RegularExpressions.Regex
            .Replace(xml.Trim(), @">\s+<", "><");
}
