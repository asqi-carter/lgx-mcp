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
                Tag:   ex.Error.TagName);
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
