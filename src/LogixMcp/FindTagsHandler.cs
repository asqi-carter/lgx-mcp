using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using L5Sharp.Core;

/// <summary>A single tag entry returned by <see cref="FindTagsHandler.Execute"/>.</summary>
/// <param name="Name">Tag name.</param>
/// <param name="Type">Data type string (e.g. BOOL, REAL, MotorCmd).</param>
/// <param name="Scope">
/// <c>"Controller"</c> for controller-scoped tags,
/// or the program name for program-scoped tags.
/// </param>
/// <param name="Description">Tag description, or empty string if none.</param>
/// <param name="Dimensions">
/// The array dimensions of an array tag (<c>[5]</c>, <c>[2, 3]</c>); omitted for a scalar tag. The data type stays the
/// element type, so <c>Type</c> is <c>DINT</c> for a <c>DINT[5]</c> tag.
/// </param>
public record TagEntry(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("type")]        string Type,
    [property: JsonPropertyName("scope")]       string Scope,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("dimensions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<int>? Dimensions = null);

/// <summary>
/// Strongly-typed success response returned by <see cref="FindTagsHandler.Execute"/>.
/// </summary>
/// <param name="Tags">List of matching tags.</param>
/// <param name="Source">
/// Always <c>"project-file"</c>. States that the tags above were read from the
/// opened or uploaded project file on disk, not from the live controller and not
/// from any pending (unsaved) edit. For the live routine content including
/// pending edits, call <c>logix_export_rungs</c> instead.
/// </param>
public record FindTagsResult(
    [property: JsonPropertyName("tags")]   IReadOnlyList<TagEntry> Tags,
    [property: JsonPropertyName("source")] string                 Source = "project-file")
{
    /// <summary><c>true</c> when the session image is marked stale and this response may not show the controller's current logic; omitted otherwise.</summary>
    [JsonPropertyName("imageStale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ImageStale { get; init; }

    /// <summary>UTC time (ISO 8601) the image was marked stale; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleSince { get; init; }

    /// <summary>What the image could not follow; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleReason { get; init; }

    /// <summary>The reconnect hint; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    /// <summary>
    /// Rungs in <em>other</em> programs that reference a matched program-scoped tag by
    /// <c>\Program.Tag</c>, each with <c>crossProgram: true</c> and the referenced <c>target</c>.
    /// Only searched when a name pattern is given; <c>null</c> when there are none.
    /// </summary>
    [JsonPropertyName("crossProgramReferences"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CrossRefEntry>? CrossProgramReferences { get; init; }

    /// <summary>Rungs the cross-program search skipped because they could not be parsed; <c>null</c> when none.</summary>
    [JsonPropertyName("unparsedRungs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// Set when a matched tag has no parsed ladder reference but is named in rungs that could not be
    /// parsed, so it may be used there; <c>null</c> otherwise.
    /// </summary>
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>Total number of tags matching the filters, across all pages.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary><c>true</c> when more matching tags exist beyond this page; pass <see cref="NextCursor"/> to read them.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>Opaque cursor for the next page; <c>null</c> when this is the last page.</summary>
    [JsonPropertyName("nextCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Strongly-typed error response returned when no project is loaded.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record FindTagsError(
    [property: JsonPropertyName("error")] string Error)
{
    /// <summary>Machine-readable error kind (for example <c>NoProjectLoaded</c>); omitted for untyped errors.</summary>
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    /// <summary>Next step (for example how to recover from <c>NoProjectLoaded</c>); omitted when none.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

/// <summary>
/// Pure handler for the <c>logix_find_tags</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class FindTagsHandler
{
    /// <summary>Default page size when no limit is given.</summary>
    public const int DefaultLimit = 200;

    /// <summary>
    /// Searches controller-scoped and program-scoped tags, optionally filtering
    /// by a glob-style name pattern and/or data type.
    /// Never throws; returns <see cref="FindTagsError"/> if no project is loaded.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="namePattern">
    /// Optional glob-style pattern (e.g. <c>Cmd_*</c>, <c>?im_*</c>). A program qualifier
    /// (<c>P2.Src</c>, <c>\P2.Src</c>) restricts the search to that program's tags. When a pattern is
    /// given, rungs in other programs that reference a matched program tag as <c>\Program.Tag</c> are
    /// returned as <see cref="FindTagsResult.CrossProgramReferences"/>.
    /// <c>null</c> or empty string means "match all names".
    /// </param>
    /// <param name="dataType">
    /// Optional data type filter (case-insensitive, e.g. <c>BOOL</c>, <c>REAL</c>).
    /// <c>null</c> or empty string means "match all types".
    /// </param>
    /// <param name="limit">Maximum tags per page (default 200). Values below 1 fall back to the default.</param>
    /// <param name="cursor">Cursor from a previous page's <c>nextCursor</c>; <c>null</c> starts at the first tag.</param>
    /// <param name="session">Optional tag session; a stale session image is flagged on the response.</param>
    /// <returns>
    /// A <see cref="FindTagsResult"/> with matching tags on success,
    /// or a <see cref="FindTagsError"/> when no project is open.
    /// </returns>
    public static object Execute(
        ProjectService service,
        string? namePattern = null,
        string? dataType    = null,
        int?    limit       = null,
        string? cursor      = null,
        TagSessionService? session = null)
    {
        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_find_tags");
            return new FindTagsError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        var pageSize = limit is > 0 ? limit.Value : DefaultLimit;
        var offset   = 0;
        if (!string.IsNullOrEmpty(cursor)
            && (!int.TryParse(cursor, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out offset)))
            return new FindTagsError($"Invalid cursor '{cursor}'. Pass the nextCursor value from a previous logix_find_tags response.")
            { Kind = ErrorKinds.InvalidArguments };

        var project        = service.Project!;
        var entries        = new List<TagEntry>();
        var patternIsEmpty = string.IsNullOrEmpty(namePattern);
        var typeIsEmpty    = string.IsNullOrEmpty(dataType);
        var descriptions   = new SafeTagDescriptions();

        // "P2.Src" / "\P2.Src": search only P2, for Src.
        string? scopeProgram = null;
        if (!patternIsEmpty)
            (scopeProgram, namePattern) = CrossReferenceHandler.ResolveTarget(project, namePattern!);

        // Controller-scoped tags
        if (scopeProgram is null)
        {
            foreach (var tag in project.Tags)
            {
                if (TagMatches(tag, namePattern, patternIsEmpty, dataType, typeIsEmpty))
                    entries.Add(ToEntry(tag, "Controller", descriptions));
            }
        }

        // Program-scoped tags
        var matchedProgramTags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in project.Programs)
        {
            if (scopeProgram is not null
                && !string.Equals(program.Name, scopeProgram, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tag in program.Tags)
            {
                if (!TagMatches(tag, namePattern, patternIsEmpty, dataType, typeIsEmpty)) continue;
                entries.Add(ToEntry(tag, program.Name, descriptions));
                if (!matchedProgramTags.TryGetValue(program.Name, out var names))
                    matchedProgramTags[program.Name] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(tag.Name);
            }
        }

        // Rungs in other programs that reach a matched program tag with \Program.Tag. The same pass notes which
        // tag names any parsed rung uses, so a tag named only by unparsed rungs can be flagged.
        IReadOnlyList<CrossRefEntry>? crossRefs = null;
        IReadOnlyList<UnparsedRungEntry>? unparsedRungs = null;
        string? note = null;
        if (!patternIsEmpty && entries.Count > 0)
        {
            var unparsed = new List<UnparsedRungEntry>();
            var scopes = new TagScopes(project);
            // Tags used by a parsed rung, keyed by owning scope so same-named tags in different programs stay apart.
            var used = new HashSet<(string Scope, string Name)>(ScopedNameComparer.Instance);
            var found = CrossReferenceHandler.Scan(
                project, WriteClassifier.BuildAoiWriteMap(project), unparsed,
                (refProgram, operand) =>
                {
                    var baseName = WriteClassifier.ExtractBaseName(operand.Tag) ?? operand.Tag;
                    used.Add((scopes.Resolve(refProgram, operand), baseName));
                    if (!operand.IsProgramQualified
                        || string.Equals(operand.Program, refProgram, StringComparison.OrdinalIgnoreCase)
                        || !matchedProgramTags.TryGetValue(operand.Program!, out var names)
                        || !names.Contains(baseName))
                        return null;
                    return (true, $"{operand.Program}.{operand.Tag}");
                });
            crossRefs     = found.Count == 0 ? null : found;
            unparsedRungs = unparsed.Count == 0 ? null : unparsed;

            var notes = entries
                .Select(e => (Scope: e.Scope == "Controller" ? TagScopes.Controller : e.Scope, e.Name))
                .Distinct(ScopedNameComparer.Instance)
                .Select(e => RungParser.OnlyInUnparsedNote(e.Name, used.Contains((e.Scope, e.Name)), unparsed))
                .Where(n => n is not null).Distinct().ToList();
            note = notes.Count == 0 ? null : string.Join(" ", notes);
        }

        var total     = entries.Count;
        var start     = Math.Min(offset, total);
        var page      = entries.Skip(start).Take(pageSize).ToList();
        var truncated = start + page.Count < total;

        var stale = ProjectFileStaleness.For(session, ProjectSourceLabel.ProjectFile, service);
        return new FindTagsResult(page)
        {
            Total                  = total,
            Truncated              = truncated,
            NextCursor             = truncated ? (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            CrossProgramReferences = crossRefs,
            UnparsedRungs          = unparsedRungs,
            Note                   = note,
            ImageStale             = stale is null ? null : true,
            StaleSince             = stale?.StaleSince,
            StaleReason            = stale?.StaleReason,
            Hint                   = stale?.Hint,
        };
    }

    private static TagEntry ToEntry(Tag tag, string scope, SafeTagDescriptions descriptions) =>
        // Description is customer-authored text from the project file; wrap it so the
        // model can distinguish project content from server-authored text. A rung L5Sharp cannot
        // parse elsewhere in the project must not stop tags being listed (SafeTagReader).
        new(Name: tag.Name, Type: DeclaredType(tag), Scope: scope,
            Description: UntrustedWrapper.WrapTagDescription(descriptions.Read(tag) ?? ""),
            Dimensions: DimensionsOf(tag));

    /// <summary>
    /// The array dimensions a tag declares, read from its own <c>Dimensions</c> attribute (<c>"5"</c>, <c>"2 3"</c>,
    /// <c>"2,3,4"</c>), or <c>null</c> for a scalar tag.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The dimension lengths in declaration order, or <c>null</c> when the tag is not an array.</returns>
    internal static IReadOnlyList<int>? DimensionsOf(Tag tag)
    {
        var raw = tag.Serialize().Attribute("Dimensions")?.Value;
        if (raw is null)
            return tag.Dimensions.IsEmpty ? null : ParseDimensions(tag.Dimensions.ToString());
        return ParseDimensions(raw);
    }

    /// <summary>Parses an L5X <c>Dimensions</c> value; <c>null</c> when it holds no non-zero length.</summary>
    /// <param name="raw">The attribute text.</param>
    internal static IReadOnlyList<int>? ParseDimensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var dims = raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();
        return dims.Count == 0 ? null : dims;
    }

    /// <summary>
    /// The tag's data type. L5Sharp reports <c>"NULL"</c> for a tag declared without a <c>&lt;Data&gt;</c>
    /// element, so fall back to the <c>DataType</c> attribute the tag was declared with.
    /// </summary>
    /// <param name="tag">The tag to read.</param>
    /// <returns>The declared data type name.</returns>
    internal static string DeclaredType(Tag tag)
    {
        var type = tag.DataType;
        if (!string.IsNullOrEmpty(type) && !string.Equals(type, "NULL", StringComparison.OrdinalIgnoreCase))
            return type;
        var declared = tag.Serialize().Attribute("DataType")?.Value;
        return string.IsNullOrEmpty(declared) ? type : declared;
    }

    private static bool TagMatches(
        Tag    tag,
        string? namePattern,
        bool    patternIsEmpty,
        string? dataType,
        bool    typeIsEmpty)
    {
        if (!patternIsEmpty && !GlobMatch(namePattern!, tag.Name))
            return false;
        if (!typeIsEmpty && !string.Equals(DeclaredType(tag), dataType, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Converts a simple glob pattern (<c>*</c> = any chars, <c>?</c> = one char)
    /// to a case-insensitive regex and tests it against <paramref name="value"/>.
    /// </summary>
    /// <param name="pattern">Glob pattern, e.g. <c>Cmd_*</c>.</param>
    /// <param name="value">Value to match against.</param>
    /// <returns><c>true</c> if the value matches the pattern.</returns>
    public static bool GlobMatch(string pattern, string value)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (char c in pattern)
        {
            sb.Append(c switch
            {
                '*'  => ".*",
                '?'  => ".",
                '.'  => "\\.",
                '('  => "\\(",
                ')'  => "\\)",
                '['  => "\\[",
                ']'  => "\\]",
                '{'  => "\\{",
                '}'  => "\\}",
                '^'  => "\\^",
                '$'  => "\\$",
                '+'  => "\\+",
                '|'  => "\\|",
                '\\' => "\\\\",
                _    => Regex.Escape(c.ToString())
            });
        }
        sb.Append('$');
        return Regex.IsMatch(value, sb.ToString(), RegexOptions.IgnoreCase);
    }

    /// <summary>Case-insensitive equality over (scope, tag name) pairs.</summary>
    private sealed class ScopedNameComparer : IEqualityComparer<(string Scope, string Name)>
    {
        public static readonly ScopedNameComparer Instance = new();

        public bool Equals((string Scope, string Name) x, (string Scope, string Name) y) =>
            string.Equals(x.Scope, y.Scope, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Scope, string Name) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Scope),
                             StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
