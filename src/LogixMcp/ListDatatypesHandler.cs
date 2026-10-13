using System.Text.Json;
using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>A single member entry for a user-defined data type.</summary>
/// <param name="Name">Member name.</param>
/// <param name="DataType">Member data type string (e.g. BOOL, REAL).</param>
/// <param name="Dimension">Array dimension string (e.g. "0" for scalar, "5" for 1-D array).</param>
/// <param name="Radix">Radix format string (e.g. "NullType", "Decimal", "Float").</param>
/// <param name="Hidden">True if this is a hidden backing member for a boolean member.</param>
/// <param name="ExternalAccess">External access level string (e.g. "Read/Write", "Read Only", "None").</param>
/// <param name="Target">For a BOOL member packed into a hidden host member, the host member's name; otherwise <c>null</c>.</param>
/// <param name="BitNumber">For a packed BOOL member, its bit position in the host; otherwise <c>null</c>.</param>
/// <param name="BindError">
/// Set only by <see cref="DataTypeMemberEntryConverter"/> when the JSON member had a wrong-typed field
/// (e.g. a number for <c>dimension</c>); never serialized. Lets the handler return a structured
/// argument error instead of the binder throwing.
/// </param>
[JsonConverter(typeof(DataTypeMemberEntryConverter))]
public record DataTypeMemberEntry(
    [property: JsonPropertyName("name")]           string Name,
    [property: JsonPropertyName("dataType")]       string DataType,
    [property: JsonPropertyName("dimension")]      string Dimension,
    [property: JsonPropertyName("radix")]          string Radix,
    [property: JsonPropertyName("hidden")]         bool   Hidden,
    [property: JsonPropertyName("externalAccess")] string ExternalAccess,
    [property: JsonPropertyName("target")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target = null,
    [property: JsonPropertyName("bitNumber")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BitNumber = null,
    [property: JsonIgnore] string? BindError = null);

/// <summary>
/// Lenient reader/writer for <see cref="DataTypeMemberEntry"/>: wrong JSON types never throw;
/// the first mismatch is recorded in <see cref="DataTypeMemberEntry.BindError"/> so the tool can
/// report it as a structured argument error naming the member index and field.
/// </summary>
public sealed class DataTypeMemberEntryConverter : JsonConverter<DataTypeMemberEntry>
{
    private static DataTypeMemberEntry Bad(string error) =>
        new(null!, null!, null!, null!, false, null!, BindError: error);

    private static string Describe(JsonValueKind k) => k switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    /// <inheritdoc />
    public override DataTypeMemberEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Bad($"must be an object with 'name' and 'dataType', got {Describe(root.ValueKind)}");

        string? error = null;
        string? Str(string field)
        {
            if (!root.TryGetProperty(field, out var e) || e.ValueKind == JsonValueKind.Null) return null;
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            error ??= $"field '{field}' must be a string, got {Describe(e.ValueKind)}";
            return null;
        }
        var name      = Str("name");
        var dataType  = Str("dataType");
        var dimension = Str("dimension");
        var radix     = Str("radix");
        var access    = Str("externalAccess");
        var target    = Str("target");

        var hidden = false;
        if (root.TryGetProperty("hidden", out var h) && h.ValueKind != JsonValueKind.Null)
        {
            if (h.ValueKind is JsonValueKind.True or JsonValueKind.False) hidden = h.GetBoolean();
            else error ??= $"field 'hidden' must be a boolean, got {Describe(h.ValueKind)}";
        }

        int? bit = null;
        if (root.TryGetProperty("bitNumber", out var b) && b.ValueKind != JsonValueKind.Null)
        {
            if (b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n)) bit = n;
            else error ??= $"field 'bitNumber' must be an integer, got {Describe(b.ValueKind)}";
        }

        return new DataTypeMemberEntry(name!, dataType!, dimension!, radix!, hidden, access!, target, bit, error);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DataTypeMemberEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("dataType", value.DataType);
        writer.WriteString("dimension", value.Dimension);
        writer.WriteString("radix", value.Radix);
        writer.WriteBoolean("hidden", value.Hidden);
        writer.WriteString("externalAccess", value.ExternalAccess);
        if (value.Target is not null) writer.WriteString("target", value.Target);
        if (value.BitNumber is not null) writer.WriteNumber("bitNumber", value.BitNumber.Value);
        writer.WriteEndObject();
    }
}

/// <summary>A single data type entry returned by <see cref="ListDatatypesHandler.Execute"/>.</summary>
/// <param name="Name">Data type name.</param>
/// <param name="Class">Class string: "User", "IO", "Predefined", or "Unknown".</param>
/// <param name="Family">Family string: "None" for non-string types, "String" for string types.</param>
/// <param name="MemberCount">Number of members defined in the type.</param>
/// <param name="Members">
/// Ordered member list (name, dataType, dimension, radix, hidden, externalAccess).
/// Populated only for User class types; <c>null</c> for non-user types.
/// </param>
public record DataTypeEntry(
    [property: JsonPropertyName("name")]        string                          Name,
    [property: JsonPropertyName("class")]       string                          Class,
    [property: JsonPropertyName("family")]      string                          Family,
    [property: JsonPropertyName("memberCount")] int                             MemberCount,
    [property: JsonPropertyName("members")]     IReadOnlyList<DataTypeMemberEntry>? Members);

/// <summary>
/// Strongly-typed success response returned by <see cref="ListDatatypesHandler.Execute"/>.
/// </summary>
/// <param name="DataTypes">List of data type entries.</param>
/// <param name="Source">
/// Always <c>"project-file"</c>. States that the types were read from the
/// opened or uploaded project file on disk, not from a live controller.
/// </param>
public record ListDatatypesResult(
    [property: JsonPropertyName("dataTypes")] IReadOnlyList<DataTypeEntry> DataTypes,
    [property: JsonPropertyName("source")]    string                       Source = "project-file")
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
}

/// <summary>
/// Strongly-typed error response returned when no project is loaded.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record ListDatatypesError(
    [property: JsonPropertyName("error")] string Error);

/// <summary>
/// Pure handler for the <c>logix_list_datatypes</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class ListDatatypesHandler
{
    /// <summary>
    /// Returns all data types in the currently open project, optionally filtered
    /// by a case-insensitive name substring and/or restricted to user-defined types only.
    /// Never throws; returns <see cref="ListDatatypesError"/> if no project is loaded.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="nameContains">
    /// Optional case-insensitive substring filter applied to the data type name.
    /// <c>null</c> or empty string means "match all names".
    /// </param>
    /// <param name="userOnly">
    /// When <c>true</c> (the default), only User-class data types are returned;
    /// ProductDefined (Predefined) and IO class types are excluded.
    /// When <c>false</c>, all data type classes are returned.
    /// </param>
    /// <param name="session">Optional tag session; a stale session image is flagged on the response.</param>
    /// <returns>
    /// A <see cref="ListDatatypesResult"/> with matching data type entries on success,
    /// or a <see cref="ListDatatypesError"/> when no project is open.
    /// </returns>
    public static object Execute(
        ProjectService service,
        string?        nameContains = null,
        bool           userOnly     = true,
        TagSessionService? session  = null)
    {
        if (!service.IsLoaded)
            return new ListDatatypesError("No project loaded. Call logix_open_project first.");

        var project    = service.Project!;
        var datatypes  = project.DataTypes.AsEnumerable();

        // Apply userOnly filter — exclude ProductDefined (Predefined) and IO class types
        if (userOnly)
            datatypes = datatypes.Where(dt => dt.Class == DataTypeClass.User);

        // Apply nameContains filter (case-insensitive)
        if (!string.IsNullOrEmpty(nameContains))
        {
            datatypes = datatypes.Where(dt =>
                dt.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        }

        var entries = datatypes.Select(ToEntry).ToList();
        var stale = ProjectFileStaleness.For(session, ProjectSourceLabel.ProjectFile, service);
        return new ListDatatypesResult(entries)
        {
            ImageStale  = stale is null ? null : true,
            StaleSince  = stale?.StaleSince,
            StaleReason = stale?.StaleReason,
            Hint        = stale?.Hint,
        };
    }

    // ------------------------------------------------------------------ //
    //  Helpers                                                             //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Converts a <see cref="DataType"/> to a <see cref="DataTypeEntry"/>.
    /// Members are populated only for User-class types.
    /// </summary>
    private static DataTypeEntry ToEntry(DataType dt)
    {
        var members = dt.Class == DataTypeClass.User
            ? dt.Members.Select(ToMemberEntry).ToList()
            : null;

        return new DataTypeEntry(
            Name:        dt.Name,
            Class:       dt.Class.Name,
            Family:      dt.Family.Name,
            MemberCount: dt.Members.Count(),
            Members:     members);
    }

    /// <summary>
    /// Converts a <see cref="DataTypeMember"/> to a <see cref="DataTypeMemberEntry"/>.
    /// </summary>
    private static DataTypeMemberEntry ToMemberEntry(DataTypeMember m) =>
        new(
            Name:           m.Name,
            DataType:       m.DataType,
            Dimension:      m.Dimension.ToString(),
            Radix:          m.Radix?.Name          ?? string.Empty,
            Hidden:         m.Hidden,
            ExternalAccess: m.ExternalAccess?.Name ?? string.Empty,
            Target:         m.Target is { Length: > 0 } t ? t : null,
            BitNumber:      m.Target is { Length: > 0 } ? m.BitNumber : null);
}
