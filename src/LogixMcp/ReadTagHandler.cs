using System.Text.Json.Serialization;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Successful response from <see cref="ReadTagHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Tag">Name of the tag that was read.</param>
/// <param name="Value">Current value of the tag (boxed .NET object).</param>
/// <param name="DataType">
/// Data-type string as reported by the driver (e.g. <c>"BOOL"</c>, <c>"REAL"</c>),
/// or <c>null</c> when the driver cannot determine the type.
/// </param>
/// <param name="UntrustedContent">
/// <c>["value"]</c> when <paramref name="Value"/> is a wrapped STRING value; omitted otherwise.
/// </param>
/// <param name="Notice">Untrusted-content notice; present exactly when <paramref name="UntrustedContent"/> is.</param>
/// <param name="Route"><c>"cip"</c> or <c>"sdk"</c>: the tag route this call used (<see cref="RoutedTagService.ActiveRoute"/>); omitted when the session is not routed.</param>
/// <param name="RouteNote">Present when CIP was lost during the read and it was retried over the SDK; says so. Omitted otherwise.</param>
/// <param name="AliasFor">Present for an alias tag: its own <c>AliasFor</c> target, whose type and value the result carries. Omitted otherwise.</param>
/// <param name="ResolvedFrom">Present for an alias tag: the base path it resolves to (member path or bit included), whether the value was read by the alias's name or from that path. Omitted otherwise.</param>
public record ReadTagResult(
    [property: JsonPropertyName("tag")]      string  Tag,
    [property: JsonPropertyName("value")]    object  Value,
    [property: JsonPropertyName("dataType")] string? DataType,
    [property: JsonPropertyName("untrustedContent"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? UntrustedContent = null,
    [property: JsonPropertyName("notice"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Notice = null,
    [property: JsonPropertyName("route"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Route = null,
    [property: JsonPropertyName("routeNote"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? RouteNote = null,
    [property: JsonPropertyName("aliasFor"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? AliasFor = null,
    [property: JsonPropertyName("resolvedFrom"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? ResolvedFrom = null);

/// <summary>
/// Error response from <see cref="ReadTagHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Error">Human-readable error description.</param>
/// <param name="Kind">
/// Machine-readable error category matching a
/// <see cref="TagServiceErrorKind"/> name, or <c>null</c> for session errors.
/// </param>
/// <param name="Tag">Name of the tag involved, or <c>null</c> for non-tag errors.</param>
/// <param name="Hint">
/// Optional imperative remediation text.  Present only when an unambiguous
/// corrective action is known; <c>null</c> otherwise.
/// </param>
/// <param name="DocsAnchor">
/// Optional stable slug referencing the relevant docs/ section.
/// <c>null</c> when no targeted documentation exists for this error.
/// </param>
public record ReadTagError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string? Kind       = null,
    [property: JsonPropertyName("tag")]         string? Tag        = null,
    [property: JsonPropertyName("hint")]        string? Hint       = null,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null);

/// <summary>
/// Unified not-found response from <see cref="ReadTagHandler.ExecuteAsync"/>.
/// Returned for all tag-not-found and member-not-found outcomes so callers
/// receive a single, consistent shape rather than raw SDK error strings.
/// </summary>
/// <param name="Status">Always <c>"not_found"</c>.</param>
/// <param name="What">
/// <c>"tag"</c> when the top-level tag was missing;
/// <c>"member"</c> when the parent tag was found but the requested member
/// path could not be resolved.
/// </param>
/// <param name="Path">
/// The full originally-requested tag path, for both <c>what:"tag"</c> and
/// <c>what:"member"</c> cases.  For example, when the caller requests
/// <c>"Recipe_Working.NoSuchMember"</c> and the member is not found, this
/// field is <c>"Recipe_Working.NoSuchMember"</c> — not the parent tag name.
/// </param>
/// <param name="SdkError">
/// The raw SDK error code (e.g. <c>"XMLSrv_E_TARGET_NOT_FOUND"</c>).  Set
/// only by the SDK lane; <c>null</c> (and omitted from the JSON) for CIP and
/// offline not-found results, which carry no SDK label.
/// </param>
public record ReadTagNotFound(
    [property: JsonPropertyName("status")]   string Status,
    [property: JsonPropertyName("what")]     string What,
    [property: JsonPropertyName("path")]     string Path,
    [property: JsonPropertyName("sdkError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SdkError = null);

// ── ReadTagHandler ────────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_read</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// Reads a single named tag from the active <see cref="TagSessionService"/>
/// session.  Returns a structured error object — never throws — for all
/// failure conditions including no active session and tag-not-found.
/// </para>
/// </summary>
public static class ReadTagHandler
{
    /// <summary>
    /// Reads the current value of <paramref name="tagName"/> from the active session.
    /// </summary>
    /// <param name="tagName">
    /// Controller-scoped tag name (e.g. <c>"Cmd_MotorStart"</c>).
    /// Member-access notation (e.g. <c>"Cmd_Motor.Speed"</c>) is supported when
    /// the underlying driver allows it.
    /// </param>
    /// <param name="session">The active tag session singleton.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ReadTagResult"/> on success, or a <see cref="ReadTagError"/>
    /// on failure.  Never throws.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string            tagName,
        TagSessionService session,
        CancellationToken ct = default)
    {
        if (!session.IsConnected)
            return new ReadTagError(
                "No active session. Call logix_connect first.",
                Kind: ErrorKinds.NoActiveSession,
                Tag:  null);

        using var routeNote = RouteNoteCapture.Begin();
        try
        {
            TagNameValidator.ThrowIfInvalid(tagName, "Read");
            if (TagPathProbe.ForRead(session.Service, tagName) is { } refused)
            {
                AuditLogger.LogRefusal("logix_read", tagName, refused.Kind, refused.Message);
                return new ReadTagError(refused.Message, Kind: refused.Kind, Tag: tagName, Hint: refused.Hint);
            }
            var result = await session.Service!.ReadAsync(tagName, ct);
            if (result.Value is string str && UntrustedWrapper.IsStringFamily(result.DataType))
                return new ReadTagResult(
                    result.TagName,
                    UntrustedWrapper.WrapTagValue(str),
                    result.DataType,
                    UntrustedWrapper.ValueKinds,
                    UntrustedWrapper.TagValueNotice,
                    session.ActiveRoute,
                    routeNote.Note,
                    result.AliasFor,
                    result.ResolvedFrom);

            return new ReadTagResult(result.TagName, result.Value, result.DataType, Route: session.ActiveRoute, RouteNote: routeNote.Note, AliasFor: result.AliasFor, ResolvedFrom: result.ResolvedFrom);
        }
        catch (TagServiceException ex)
            when (ex.Error.Kind == TagServiceErrorKind.TagNotFound)
        {
            // Return the unified not-found shape so callers always receive
            // status/what/path (+ sdkError on the SDK lane) — never just a raw SDK error string.
            return new ReadTagNotFound(
                Status:   "not_found",
                What:     ex.Error.NotFoundWhat ?? "tag",
                Path:     tagName,
                SdkError: ex.Error.SdkCode);
        }
        catch (TagServiceException ex)
        {
            if (ex.Error.Kind is TagServiceErrorKind.TypeUnknown
                              or TagServiceErrorKind.StructureReadUnsupported
                              or TagServiceErrorKind.TypeMismatch
                              or TagServiceErrorKind.BitOutOfRange)
                AuditLogger.LogRefusal("logix_read", ex.Error.TagName ?? tagName,
                    ex.Error.Kind.ToString(), ex.Error.Message);

            return new ReadTagError(
                Error: ex.Error.Message,
                Kind:  ex.Error.Kind.ToString(),
                Tag:   ex.Error.TagName,
                Hint:  ex.Error.Hint);
        }
        catch (Exception ex)
        {
            return new ReadTagError(
                Error: ex.Message,
                Kind:  TagServiceErrorKind.Unknown.ToString(),
                Tag:   tagName);
        }
    }
}

// ── TagPathProbe ──────────────────────────────────────────────────────────────

/// <summary>A refusal the declared types can name before any controller call.</summary>
/// <param name="Kind">The error kind (an <see cref="ErrorKinds"/> value).</param>
/// <param name="Message">What is wrong, naming the tag and the dimension or members involved.</param>
/// <param name="Hint">The corrective action, with the element or member form to use.</param>
internal sealed record TagPathFinding(string Kind, string Message, string Hint);

/// <summary>
/// Checks a tag path against the project's declared types (the session's type lookup) before a read or write goes to
/// the controller, so the shapes the data path cannot carry are named by kind rather than as a protocol or type
/// failure: a whole array, an index past a dimension, a member the type does not have, a whole structure written.
/// Returns <c>null</c> whenever the declarations cannot answer (no lookup, tag not declared, alias, Add-On
/// Instruction type, a non-numeric index), so the existing path keeps its behaviour.
/// </summary>
internal static class TagPathProbe
{
    private sealed record Segment(string Name, IReadOnlyList<int?> Indices);

    private sealed record Member(string Type, IReadOnlyList<int> Dimensions);

    /// <summary>The refusal for reading <paramref name="path"/>, or <c>null</c>.</summary>
    /// <param name="service">The session's service; its declared-type lookup is used when it has one.</param>
    /// <param name="path">The tag path.</param>
    internal static TagPathFinding? ForRead(ITagService? service, string path) => Probe(service, path, forWrite: false);

    /// <summary>The refusal for writing <paramref name="path"/>, or <c>null</c>.</summary>
    /// <param name="service">The session's service; its declared-type lookup is used when it has one.</param>
    /// <param name="path">The tag path.</param>
    internal static TagPathFinding? ForWrite(ITagService? service, string path) => Probe(service, path, forWrite: true);

    private static TagPathFinding? Probe(ITagService? service, string path, bool forWrite)
    {
        if (string.IsNullOrWhiteSpace(path) || TagRouteResolver.Backend(service) is not ISessionTypeSource types)
            return null;
        try
        {
            return types.Lookup(project => Walk(project, path.Trim(), forWrite));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // the declarations could not be read: leave it to the data path
        }
    }

    private static bool TryParse(string path, out string? program, out List<Segment> segments)
    {
        program  = null;
        segments = [];
        var text = path;
        if (text.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            var dot = text.IndexOf('.');
            if (dot <= "Program:".Length) return false;
            program = text["Program:".Length..dot];
            text    = text[(dot + 1)..];
        }

        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == '.' && depth == 0) { parts.Add(text[start..i]); start = i + 1; }
        }
        parts.Add(text[start..]);
        if (depth != 0) return false;

        foreach (var part in parts)
        {
            var br = part.IndexOf('[');
            var name = br < 0 ? part : part[..br];
            if (name.Length == 0) return false;
            var indices = new List<int?>();
            if (br >= 0)
            {
                if (!part.EndsWith(']')) return false;
                foreach (var raw in part[(br + 1)..^1].Split(','))
                    indices.Add(int.TryParse(raw.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null);
            }
            segments.Add(new Segment(name, indices));
        }
        return true;
    }

    private static TagPathFinding? Walk(L5Sharp.Core.L5X project, string path, bool forWrite)
    {
        if (!TryParse(path, out var program, out var segments))
            return null;

        var root = segments[0];
        var tag = program is null
            ? project.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), root.Name, StringComparison.OrdinalIgnoreCase))
            : project.Programs.FirstOrDefault(p => string.Equals(p.Name, program, StringComparison.OrdinalIgnoreCase))
                ?.Tags.FirstOrDefault(t => string.Equals(t.Name.ToString(), root.Name, StringComparison.OrdinalIgnoreCase));
        if (tag is null || tag.Serialize().Attribute("AliasFor") is not null)
            return null;

        var typeName = DeclaredMemberTypes.DeclaredTypeOf(tag);
        IReadOnlyList<int> dims = FindTagsHandler.DimensionsOf(tag) ?? (IReadOnlyList<int>)[];
        var walked = new System.Text.StringBuilder();
        if (program is not null) walked.Append("Program:").Append(program).Append('.');
        string verb = forWrite ? "write" : "read";

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (i > 0) walked.Append('.');
            walked.Append(seg.Name);
            bool last = i == segments.Count - 1;

            if (seg.Indices.Count > 0 && dims.Count > 0)
            {
                if (seg.Indices.Count != dims.Count)
                    return null;
                for (var k = 0; k < dims.Count; k++)
                {
                    if (seg.Indices[k] is { } idx && (idx < 0 || idx >= dims[k]))
                        return new TagPathFinding(ErrorKinds.IndexOutOfRange,
                            $"Cannot {verb} '{path}': index {idx} is out of range for '{walked}', whose " +
                            (dims.Count == 1 ? "dimension is " : $"dimension {k + 1} is ") + $"{dims[k]} " +
                            $"(valid indices 0 to {dims[k] - 1}).",
                            $"Use an index from 0 to {dims[k] - 1}; the declared dimension of '{walked}' is [{string.Join(",", dims)}].");
                }
                walked.Append('[').Append(string.Join(",", seg.Indices.Select(n => n?.ToString() ?? "?"))).Append(']');
                dims = [];
            }
            else if (seg.Indices.Count == 0 && dims.Count > 0)
            {
                if (!last) return null;
                var element = $"{walked}[{string.Join(",", dims.Select(_ => "0"))}]";
                var kind = forWrite ? ErrorKinds.StructureWriteUnsupported : TagServiceErrorKind.StructureReadUnsupported.ToString();
                return new TagPathFinding(kind,
                    $"Cannot {verb} '{path}' as a whole: it is an array {typeName}[{string.Join(",", dims)}]. " +
                    $"A whole array is not carried by {(forWrite ? "writes" : "reads")}.",
                    $"{(forWrite ? "Write" : "Read")} one element at a time, for example {element} " +
                    $"(valid indices 0 to {dims[0] - 1}).");
            }
            else if (seg.Indices.Count > 0)
            {
                return null; // indexed but not an array: the resolver names that
            }

            if (last) break;

            var next = segments[i + 1];
            if (CipTypeResolver.IsAtomic(typeName))
                return null; // a bit selector or a path the resolver names

            var members = MembersOf(project, typeName);
            if (members is null)
                return null; // an Add-On Instruction or an unknown type: not judged here

            if (!members.TryGetValue(next.Name, out var member))
            {
                var list = string.Join(", ", members.Keys);
                return new TagPathFinding(ErrorKinds.MemberNotFound,
                    $"Cannot {verb} '{path}': type {typeName} of '{walked}' has no member '{next.Name}'. Members: {list}.",
                    $"Use one of the members of {typeName}: {list}.");
            }

            typeName = member.Type;
            dims     = member.Dimensions;
        }

        if (forWrite && !CipTypeResolver.IsAtomic(typeName) && MembersOf(project, typeName) is { } own)
        {
            var list = string.Join(", ", own.Keys);
            return new TagPathFinding(ErrorKinds.StructureWriteUnsupported,
                $"Cannot write '{path}' as a whole: it is a structure or STRING (type {typeName}). Members: {list}.",
                $"Write one member at a time, for example {path}.{own.Keys.FirstOrDefault()}. Members: {list}.");
        }

        return null;
    }

    private static Dictionary<string, Member>? MembersOf(L5Sharp.Core.L5X project, string typeName)
    {
        var dt = project.DataTypes.FirstOrDefault(d => string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (dt is not null)
        {
            var map = new Dictionary<string, Member>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in dt.Members.Where(m => !m.Hidden))
            {
                var type = string.Equals(m.DataType, "BIT", StringComparison.OrdinalIgnoreCase) ? "BOOL" : m.DataType;
                IReadOnlyList<int> dims = m.Dimension.IsEmpty
                    ? []
                    : new[] { (int)m.Dimension.X, (int)m.Dimension.Y, (int)m.Dimension.Z }
                        .Take((int)m.Dimension.Rank).ToList();
                map[m.Name] = new Member(type, dims);
            }
            return map;
        }

        if (DeclaredMemberTypes.PredefinedMembers.TryGetValue(typeName, out var predefined))
            return predefined.ToDictionary(kv => kv.Key, kv => new Member(kv.Value, []), StringComparer.OrdinalIgnoreCase);

        return null;
    }
}
