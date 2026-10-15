using System.Text.Json.Serialization;

// ── Result / error types ──────────────────────────────────────────────────────

/// <summary>
/// Successful response from <see cref="WriteTagHandler.ExecuteAsync"/>.
/// </summary>
/// <param name="Status">Always <c>"ok"</c> on success.</param>
/// <param name="Tag">Name of the tag that was written.</param>
/// <param name="Written">The parsed input value that was sent to the controller.</param>
/// <param name="Observed">
/// The value read back from the controller immediately after the write, or
/// <c>null</c> when readback was not performed.
/// </param>
/// <param name="Verified">
/// <c>true</c> when <see cref="Observed"/> matches <see cref="Written"/> within
/// type-appropriate tolerance; <c>false</c> when they disagree (e.g. the running
/// ladder program overwrote the tag between the write and the readback);
/// <c>null</c> when readback was not performed.
/// </param>
/// <param name="VerificationError">
/// Human-readable explanation of why <see cref="Verified"/> is <c>null</c> (e.g.
/// the readback itself threw), or <c>null</c> when verification succeeded or was
/// not attempted.
/// </param>
/// <param name="DataType">
/// The tag's data type as reported by the controller (e.g. <c>"BOOL"</c>,
/// <c>"REAL"</c>), or <c>null</c> when unavailable.
/// </param>
/// <param name="Route"><c>"cip"</c> or <c>"sdk"</c>: the tag route this write and its readback used (<see cref="RoutedTagService.ActiveRoute"/>); omitted when the session is not routed.</param>
/// <param name="RouteNote">Present when CIP was lost during the write and it was retried over the SDK; says so. Omitted otherwise.</param>
/// <param name="ResolvedFrom">Present when <paramref name="Tag"/> is an alias: the base path the gate judged and the write went to. <paramref name="Tag"/> stays the alias, so the result shows both names. Omitted otherwise.</param>
public record WriteTagResult(
    [property: JsonPropertyName("status")]            string  Status,
    [property: JsonPropertyName("tag")]               string  Tag,
    [property: JsonPropertyName("written")]           object  Written,
    [property: JsonPropertyName("observed")]          object? Observed,
    [property: JsonPropertyName("verified")]          bool?   Verified,
    [property: JsonPropertyName("verificationError")] string? VerificationError,
    [property: JsonPropertyName("dataType")]          string? DataType,
    [property: JsonPropertyName("route"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? Route = null,
    [property: JsonPropertyName("routeNote"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? RouteNote = null,
    [property: JsonPropertyName("resolvedFrom"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? ResolvedFrom = null)
{
    /// <summary>
    /// Backward-compatible alias for <see cref="Observed"/>.
    /// Kept so existing callers that reference the <c>"value"</c> JSON field
    /// continue to work without modification.
    /// </summary>
    [JsonPropertyName("value")]
    public object? Value => Observed;
}

/// <summary>
/// Structured error response from <see cref="WriteTagHandler.ExecuteAsync"/>.
/// Returned for all failure conditions including gate rejections.
/// </summary>
/// <param name="Error">Human-readable description of the failure.</param>
/// <param name="Kind">
/// Machine-readable error category matching a
/// <see cref="TagServiceErrorKind"/> name, or <c>null</c> for session errors.
/// </param>
/// <param name="Tag">Name of the tag involved, or <c>null</c> for non-tag errors.</param>
/// <param name="Hint">
/// Optional imperative remediation text (e.g. "Check that the tag exists in the
/// controller project and that the session is active.").  Present only when an
/// unambiguous corrective action is known; <c>null</c> otherwise.
/// </param>
/// <param name="DocsAnchor">
/// Optional stable slug referencing the relevant docs/ section
/// (e.g. <c>"write-tag-errors"</c>).  <c>null</c> when no targeted
/// documentation exists for this error.
/// </param>
public record WriteTagError(
    [property: JsonPropertyName("error")]       string  Error,
    [property: JsonPropertyName("kind")]        string? Kind       = null,
    [property: JsonPropertyName("tag")]         string? Tag        = null,
    [property: JsonPropertyName("hint")]        string? Hint       = null,
    [property: JsonPropertyName("docs_anchor")] string? DocsAnchor = null);

// ── WriteTagHandler ───────────────────────────────────────────────────────────

/// <summary>
/// Pure handler for the <c>logix_write</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// Writes a value to the active <see cref="TagSessionService"/> session.  When
/// the session was established with a project-derived <see cref="GatedTagService"/>
/// wrapper, every write is gate-checked before reaching the controller or virtual
/// store.  Gate rejections are surfaced as <see cref="WriteTagError"/> with
/// <c>Kind = "WriteDenied"</c> rather than exceptions.
/// </para>
/// </summary>
public static class WriteTagHandler
{
    /// <summary>Gate-decision reason recorded when a live session has no write gate.</summary>
    private const string NoWriteGateReason = "NoWriteGate";

    private const string WriteGateRequiredMessage =
        "No write gate for this session: no project was open when you connected over CIP, " +
        "and no site allowlist is set.";

    private static readonly string WriteGateRequiredHint =
        "Open the engineer's project (logix_open_project), then call logix_connect again so " +
        "writes are checked against it; or set " + WriteAllowlist.EnvVar +
        " to the tags this site permits.";

    /// <summary>
    /// Converts a JSON <c>value</c> argument to the canonical text the string form of
    /// <c>logix_write</c> uses: booleans become <c>true</c>/<c>false</c>, numbers their invariant
    /// decimal text, strings are unchanged.
    /// </summary>
    /// <param name="value">The JSON value bound from the tool call.</param>
    /// <param name="text">The canonical text when the conversion succeeds.</param>
    /// <returns><c>false</c> when the value is null, an object, an array or undefined.</returns>
    public static bool TryCanonicalValueText(System.Text.Json.JsonElement value, out string text)
    {
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                text = value.GetString() ?? "";
                return true;
            case System.Text.Json.JsonValueKind.True:
                text = "true";
                return true;
            case System.Text.Json.JsonValueKind.False:
                text = "false";
                return true;
            case System.Text.Json.JsonValueKind.Number:
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (value.TryGetInt64(out var l))
                    text = l.ToString(inv);
                else if (value.TryGetDouble(out var d) && double.IsFinite(d))
                    text = d.ToString("R", inv);
                else
                    text = value.GetRawText();
                return true;
            default:
                text = "";
                return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="rawValue"/> to the tag named <paramref name="tagName"/>
    /// in the active session.
    ///
    /// <para>
    /// <paramref name="rawValue"/> is first parsed to the most specific .NET type
    /// that can represent it:
    /// <list type="bullet">
    ///   <item><c>"true"</c> / <c>"false"</c> (case-insensitive) → <see cref="bool"/></item>
    ///   <item>An integer string (e.g. <c>"42"</c>) → <see cref="int"/></item>
    ///   <item>A decimal string (e.g. <c>"3.14"</c>) → <see cref="double"/></item>
    ///   <item>Anything else → kept as <see cref="string"/></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="tagName">
    /// Controller-scoped tag name to write (e.g. <c>"Cmd_SpeedSetpoint"</c>).
    /// </param>
    /// <param name="rawValue">
    /// String representation of the value to write.  Parsed to a typed value
    /// before being forwarded to the underlying driver.
    /// </param>
    /// <param name="session">The active tag session singleton.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="WriteTagResult"/> on success, or a <see cref="WriteTagError"/>
    /// on failure (including gate rejections and tag-not-found).  Never throws.
    /// </returns>
    public static async Task<object> ExecuteAsync(
        string            tagName,
        string            rawValue,
        TagSessionService session,
        CancellationToken ct           = default,
        Capabilities?     capabilities = null)
    {
        // ── Normalise the tag name once ───────────────────────────────────
        // Every later step (gate, write, read-back, audit, response) uses this
        // trimmed name; none sees the raw padded input.
        tagName = tagName?.Trim() ?? string.Empty;

        if (!session.IsConnected)
        {
            AuditLogger.LogToolRefused("logix_write", ErrorKinds.NoActiveSession, tag: tagName);
            return new WriteTagError(
                "No active session. Call logix_connect first.",
                Kind: ErrorKinds.NoActiveSession,
                Tag:  null);
        }

        // Collects the note left when a CIP write is retried over the SDK (never silent).
        using var routeNote = RouteNoteCapture.Begin();

        // ── Live-write capability gate ──────────────────────────────────────────
        // When LGX_ENABLE_LIVE_WRITES is not set, writes to live controllers
        // (CIP or SDK online sessions) are refused before any network call.
        // Writes to virtual, L5X, and SDK offline sessions are always allowed.
        if (capabilities is not { LiveWritesEnabled: true } && session.IsLiveController)
        {
            AuditLogger.LogCapabilityRefusal("logix_write", Capabilities.EnvVar);
            return new CapabilityDisabledError(
                Error:
                    $"Write to tag '{tagName}' refused: LGX_ENABLE_LIVE_WRITES is not set. " +
                    "Writes to live controllers require the opt-in capability flag.",
                Hint:
                    $"Set the {Capabilities.EnvVar} environment variable to 1 to enable " +
                    $"live controller writes. " +
                    $"Client command: {Capabilities.ClientCommandHint}");
        }

        var parsedValue = ParseValue(rawValue);

        // ── Non-finite values (NaN, ±Infinity) ─────────────────────────────────
        // Refused before the gate and the backend, whether or not the tag has bounds.
        if (WriteTypeRule.IsNonFinite(parsedValue))
        {
            var nonFiniteMsg = WriteTypeRule.NonFiniteMessage(tagName, parsedValue);
            AuditLogger.LogGateDecision(tagName, "denied", WriteTypeRule.NonFiniteReason,
                "WriteTagHandler: " + nonFiniteMsg);
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    nonFiniteMsg,
                kind:     ErrorKinds.InvalidValue);
            return new WriteTagError(
                Error: nonFiniteMsg,
                Kind:  ErrorKinds.InvalidValue,
                Tag:   tagName,
                Hint:  "Supply a finite number. NaN and ±Infinity are never written to a tag.");
        }

        // ── Session-created tag guard ──────────────────────────────────────────
        // Tags created by this server process during the current session must not
        // be written back in the same session.  This closes the in-session
        // self-grant hole: a tag just introduced by a rung-edit proposal cannot be
        // written in the same session; after a new logix_connect the registry is
        // cleared and the tag follows ordinary gate policy.
        //
        // For dotted member paths (e.g. "Cmd_SessionTag.Speed"), check the base
        // tag name (everything before the first dot), which is the tag that was
        // created.
        var baseTagName = tagName;
        var dotIndex    = tagName.IndexOf('.');
        if (dotIndex >= 0)
            baseTagName = tagName[..dotIndex];

        if (session.TryGetSessionCreatedTagEntry(baseTagName, out var createdEntry))
        {
            var denyMsg =
                $"Write denied for tag '{tagName}': {DenyReason.CreatedByThisSession} — " +
                $"Tag '{baseTagName}' was created by this session via proposal " +
                $"'{createdEntry!.ProposalId}' at {createdEntry.CreatedAt:O}. " +
                "This is a defence-in-depth guard: a tag this session created cannot " +
                "be written by the same session, whatever its External Access. " +
                "Call logix_connect to start a new session, after which the tag is " +
                "writable only if an engineer has set its External Access to Read/Write " +
                "in Logix Designer and ordinary gate policy grants it.";

            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    denyMsg,
                kind:     TagServiceErrorKind.WriteDenied.ToString());

            return new WriteTagError(
                Error: denyMsg,
                Kind:  TagServiceErrorKind.WriteDenied.ToString(),
                Tag:   tagName,
                Hint:  "This tag was created during the current session. " +
                       "Call logix_connect to establish a new session; the tag will " +
                       "then be subject to ordinary gate policy.");
        }

        // ── Write gate required for live CIP sessions ──────────────────────────
        // A live controller session with no project-derived gate (no project was open at
        // connect time) and no site allowlist has nothing checking the write. Refuse before
        // any network call. So is any session connected on a partial export without an
        // allowlist, whatever its backend (virtual, l5x, cip). Other offline and gated
        // sessions are not affected.
        if ((session.IsLiveController || session.WritesRequireGate) &&
            session.Service is not GatedTagService &&
            session.WriteAllowlist is null)
        {
            var gateMessage = session.NoGateReason is { } noGate
                ? "No write gate for this session: " + noGate + "."
                : WriteGateRequiredMessage;
            var gateHint = session.NoGateReason is null
                ? WriteGateRequiredHint
                : "Open a full controller export (logix_open_project), then call logix_connect again so " +
                  "writes are checked against it; or set " + WriteAllowlist.EnvVar +
                  " to the tags this site permits.";
            AuditLogger.LogGateDecision(tagName, "denied", NoWriteGateReason, gateMessage);
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    gateMessage,
                kind:     ErrorKinds.WriteGateRequired);
            return new WriteTagError(
                Error: gateMessage,
                Kind:  ErrorKinds.WriteGateRequired,
                Tag:   tagName,
                Hint:  gateHint);
        }

        // ── Allowlist for sessions without a write gate ────────────────────────
        // A GatedTagService enforces the allowlist itself, after its own rules. An ungated
        // session (no project) has no gate, but an active allowlist still narrows writes.
        if (session.Service is not GatedTagService && session.WriteAllowlist is { } allowlist &&
            allowlist.DenialMessage(tagName, gated: false) is { } allowlistMsg)
        {
            AuditLogger.LogGateDecision(tagName, "denied", nameof(DenyReason.NotOnAllowlist), allowlistMsg);
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    allowlistMsg,
                kind:     TagServiceErrorKind.WriteDenied.ToString());
            return new WriteTagError(
                Error: allowlistMsg,
                Kind:  TagServiceErrorKind.WriteDenied.ToString(),
                Tag:   tagName,
                Hint:  $"Add the tag to '{allowlist.Path}' and reconnect, or unset {WriteAllowlist.EnvVar}.");
        }

        // ── Fail-closed audit ──────────────────────────────────────────────────
        // With LGX_AUDIT_FAIL_CLOSED=1 a write is refused if the audit log cannot be opened for append.
        if (AuditLogger.FailClosed && !AuditLogger.IsAvailable(out var auditReason))
        {
            var auditMsg = $"Write to tag '{tagName}' refused: the audit log cannot be opened for append " +
                           $"({auditReason}) and {AuditLogger.FailClosedEnvVar}=1. Nothing was written.";
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    auditMsg,
                kind:     ErrorKinds.AuditUnavailable);
            return new WriteTagError(
                Error: auditMsg,
                Kind:  ErrorKinds.AuditUnavailable,
                Tag:   tagName,
                Hint:  $"Make the audit directory writable, or unset {AuditLogger.FailClosedEnvVar}.");
        }

        // ── Whole structures, whole arrays, bad indices, missing members ─────────
        // The declared types name these before any controller call, so a refused shape never reaches the gate or the wire.
        if (TagPathProbe.ForWrite(session.Service, tagName) is { } pathRefused)
        {
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    pathRefused.Message,
                kind:     pathRefused.Kind);
            return new WriteTagError(pathRefused.Message, Kind: pathRefused.Kind, Tag: tagName, Hint: pathRefused.Hint);
        }

        string? resolvedFrom = null;
        try
        {
            // An alias is gated and written as its resolved base; the result keeps both names.
            resolvedFrom = (session.Service as GatedTagService)?.ResolveAliasForWrite(tagName)?.ResolvedFrom;
            await session.Service!.WriteAsync(tagName, parsedValue, ct);
        }
        catch (TagServiceException ex)
        {
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      ex.Error.TagName ?? tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    ex.Error.Message,
                kind:     ex.Error.Kind.ToString());

            // For WriteDenied, surface an actionable hint naming the write-gate
            // designation and the L5X provenance already embedded in the message.
            string? hint = ex.Error.Kind == TagServiceErrorKind.WriteDenied
                ? ex.Error.Message.Contains(nameof(DenyReason.NotOnAllowlist), StringComparison.Ordinal)
                  ? $"The tag is not listed in the write allowlist file ({WriteAllowlist.EnvVar}). " +
                    (session.Service is GatedTagService { GrantsFromAllowlist: true }
                        ? "For a partial export the allowlist is the only source of grants; add the tag to the file and reconnect, "
                        : "The allowlist only narrows the gate; add the tag to the file and reconnect, ") +
                    $"or unset {WriteAllowlist.EnvVar}."
                  : session.Service is GatedTagService { GrantsFromAllowlist: true } && session.WriteAllowlist is { } al
                  ? $"This session is a partial export gated by the write allowlist file '{al.Path}' ({WriteAllowlist.EnvVar}): " +
                    "only tags the allowlist lists can be written, and the partial file's denies and ranges still apply. " +
                    "The deny reason and provenance are in the error message. " +
                    "Call logix_gate_report to list the allowlisted tags and the file's denies and ranges."
                  : $"Only write-designated tags (name starts with {GateConfigResolver.TryDescribePrefixes()}) whose External " +
                  "Access is Read/Write are permitted by " +
                  "the project write-gate policy.  The deny reason and the L5X element " +
                  "(provenance) that declared this restriction are in the error message. " +
                  "Call logix_gate_report to list all write-granted tags."
                : null;

            return new WriteTagError(
                Error: ex.Error.Message,
                Kind:  ex.Error.Kind.ToString(),
                Tag:   ex.Error.TagName,
                Hint:  hint);
        }
        catch (Exception ex)
        {
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    ex.Message,
                kind:     TagServiceErrorKind.Unknown.ToString());
            return new WriteTagError(
                Error: ex.Message,
                Kind:  TagServiceErrorKind.Unknown.ToString(),
                Tag:   tagName);
        }

        // Write succeeded — perform readback to observe the controller's actual value.
        try
        {
            var readback = await session.Service!.ReadAsync(tagName, ct);
            var verified = ValuesMatch(parsedValue, readback.Value, readback.DataType);
            var verificationNote = verified
                ? null
                : "The observed value does not match the written value. " +
                  "The value may have been overwritten by the running controller " +
                  "program between the write and the readback.";
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                routeNote: routeNote.Note,
                tag:      tagName,
                written:  parsedValue,
                observed: readback.Value,
                verified: verified,
                dataType: readback.DataType);
            return new WriteTagResult(
                Status:            "ok",
                Tag:               tagName,
                Written:           parsedValue,
                Observed:          readback.Value,
                Verified:          verified,
                VerificationError: verificationNote,
                DataType:          readback.DataType,
                Route:             session.ActiveRoute,
                RouteNote:         routeNote.Note,
                ResolvedFrom:      resolvedFrom);
        }
        catch (Exception ex)
        {
            // Readback failed; the write itself succeeded, so surface a partial result.
            AuditLogger.LogWrite(
                controller: session.AuditController,
                route:    session.ActiveRoute,
                routeNote: routeNote.Note,
                tag:      tagName,
                written:  parsedValue,
                observed: null,
                verified: null,
                error:    ex.Message,
                kind:     "ReadbackFailed");
            return new WriteTagResult(
                Status:            "ok",
                Tag:               tagName,
                Written:           parsedValue,
                Observed:          null,
                Verified:          null,
                VerificationError: ex.Message,
                DataType:          null,
                Route:             session.ActiveRoute,
                RouteNote:         routeNote.Note,
                ResolvedFrom:      resolvedFrom);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Relative-epsilon tolerance used when comparing REAL tag values.
    ///
    /// <para>
    /// A Logix REAL is a 32-bit (single-precision) IEEE 754 float.  When a
    /// .NET <see cref="double"/> is written to a REAL tag and subsequently read
    /// back, the controller stores only ~7 significant decimal digits.  The
    /// maximum relative rounding error of a double-to-float32-to-double
    /// conversion is approximately 5.96 × 10⁻⁸ (half the float32 machine
    /// epsilon of 1.19 × 10⁻⁷).  The chosen value 1 × 10⁻⁶ provides ~16×
    /// headroom above that rounding bound, ensuring that an in-tolerance
    /// round-trip always produces <c>verified: true</c> while remaining tight
    /// enough to catch genuine controller-side value changes.
    /// </para>
    /// </summary>
    public const double RealEpsilon = 1e-6;

    /// <summary>
    /// Compares <paramref name="written"/> and <paramref name="observed"/> using
    /// the type-appropriate tolerance for the given Logix <paramref name="dataType"/>.
    ///
    /// <para>
    /// Dispatch rules:
    /// <list type="bullet">
    ///   <item>
    ///     <term>BOOL, DINT, INT, SINT</term>
    ///     <description>
    ///     Exact equality via <see cref="object.Equals(object?, object?)"/>.
    ///     Integer types round-trip perfectly; no tolerance is needed or desirable.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>REAL</term>
    ///     <description>
    ///     Relative-epsilon comparison using <see cref="RealEpsilon"/>.
    ///     The Logix REAL type is a 32-bit float; a caller-supplied
    ///     <see cref="double"/> is truncated to float32 on the wire, so the
    ///     readback value will differ from the original double by up to ~5.96 × 10⁻⁸
    ///     even on a perfectly successful write.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>All other / unknown types</term>
    ///     <description>Exact equality via <see cref="object.Equals(object?, object?)"/>.</description>
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="written">The value that was sent to the controller.</param>
    /// <param name="observed">The value read back from the controller.</param>
    /// <param name="dataType">
    /// The tag's Logix data-type string (e.g. <c>"REAL"</c>, <c>"DINT"</c>),
    /// or <c>null</c> when the type is unknown.  Comparison is case-insensitive.
    /// </param>
    /// <returns>
    /// <c>true</c> when the values agree within the type-appropriate tolerance;
    /// <c>false</c> when they disagree.
    /// </returns>
    public static bool ValuesMatch(object written, object observed, string? dataType)
    {
        if (dataType is not null &&
            dataType.Equals("REAL", StringComparison.OrdinalIgnoreCase))
        {
            return RelativelyEqual(written, observed, RealEpsilon);
        }

        // Integer-typed values compare by numeric value, not .NET type: a written
        // int 5 matches an observed sbyte 5 read back from a SINT tag.
        if (IsIntegral(written) && IsIntegral(observed))
            return ToBigInteger(written) == ToBigInteger(observed);

        // Integer written to an LREAL (or float vs double): compare typed numerics exactly.
        if (IsNumeric(written) && IsNumeric(observed))
            return Convert.ToDouble(written) == Convert.ToDouble(observed);

        // BOOL and all other types: exact equality.
        return Equals(written, observed);
    }

    private static bool IsIntegral(object v) =>
        v is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static bool IsNumeric(object v) => IsIntegral(v) || v is float or double;

    private static System.Numerics.BigInteger ToBigInteger(object v) =>
        v is ulong u ? u : Convert.ToInt64(v);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="a"/> and <paramref name="b"/> are
    /// within <paramref name="epsilon"/> relative to the larger magnitude.  Both
    /// operands are coerced to <see cref="double"/>; non-numeric types fall back
    /// to exact equality.
    /// </summary>
    private static bool RelativelyEqual(object a, object b, double epsilon)
    {
        double da, db;
        try
        {
            da = Convert.ToDouble(a);
            db = Convert.ToDouble(b);
        }
        catch
        {
            return Equals(a, b);
        }

        if (da == db) return true;

        var magnitude = Math.Max(Math.Abs(da), Math.Abs(db));
        return magnitude > 0.0 && Math.Abs(da - db) / magnitude <= epsilon;
    }

    /// <summary>
    /// Parses <paramref name="raw"/> to the most specific .NET value type.
    /// <list type="bullet">
    ///   <item><c>"true"</c> / <c>"false"</c> → <see cref="bool"/></item>
    ///   <item>Integer string → <see cref="int"/>, else <see cref="long"/>, else <see cref="ulong"/> (exact)</item>
    ///   <item>Decimal string → <see cref="double"/> (using invariant culture)</item>
    ///   <item>Any other string → returned as-is</item>
    /// </list>
    /// </summary>
    public static object ParseValue(string raw)
    {
        if (raw.Equals("true",  StringComparison.OrdinalIgnoreCase)) return true;
        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        if (int.TryParse(raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var intVal))
            return intVal;

        // Integer-looking strings stay exact: never round-trip through double,
        // which would corrupt LINT/ULINT values beyond 2^53.
        if (long.TryParse(raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var longVal))
            return longVal;

        if (ulong.TryParse(raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var ulongVal))
            return ulongVal;

        // NaN / ±Infinity spellings ("nan", "-NaN", "Infinity", "∞", ...) parse to a
        // non-finite double so ExecuteAsync can refuse them with InvalidValue.
        if (WriteTypeRule.TryParseNonFiniteToken(raw, out var nonFinite))
            return nonFinite;

        if (double.TryParse(raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var dblVal))
            return dblVal;

        return raw;
    }
}
