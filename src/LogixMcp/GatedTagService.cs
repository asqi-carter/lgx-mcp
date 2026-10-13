// ── GatedTagService ───────────────────────────────────────────────────────────

/// <summary>
/// Decorator that enforces a <see cref="WriteGatePolicy"/> on top of any
/// <see cref="ITagService"/> implementation.
///
/// <para>
/// Read operations (<see cref="ReadAsync"/>, <see cref="ListTagsAsync"/>),
/// connection management (<see cref="ConnectAsync"/>, <see cref="DisconnectAsync"/>)
/// are always forwarded to the wrapped service unchanged.
/// </para>
///
/// <para>
/// <see cref="WriteAsync"/> is gated in three stages:
/// <list type="number">
///   <item><description>
///     <b>Grant check</b> — if the tag is not in the policy's grant list, the write is
///     rejected with <see cref="TagServiceErrorKind.WriteDenied"/>.  The error message
///     includes the deny reason and provenance when a matching deny entry exists.
///   </description></item>
///   <item><description>
///     <b>Bounds check</b> — if the grant entry carries scalar
///     <see cref="GrantEntry.TagBounds"/> (or a member bound that matches the written
///     member path), numeric values outside <c>[Min..Max]</c> are rejected with
///     <see cref="TagServiceErrorKind.WriteDenied"/> citing the bounds and provenance.
///     A value that cannot be converted to a <see cref="double"/> is refused too, with
///     <see cref="TagServiceErrorKind.InvalidValue"/> (audit reason
///     <c>NonNumericValue</c>): it cannot be compared with the range, so it is never
///     forwarded. No clamping or silent modification is performed.
///   </description></item>
///   <item><description>
///     <b>Pass-through</b> — granted, in-bounds writes are forwarded to the wrapped
///     service as-is.
///   </description></item>
/// </list>
/// All rejections throw <see cref="TagServiceException"/>; they are never swallowed.
/// </para>
/// </summary>
public sealed class GatedTagService : ITagService
{
    private readonly ITagService _inner;
    private readonly WriteGatePolicy _policy;

    // Case-insensitive lookups built once at construction for O(1) per-write cost.
    private readonly Dictionary<string, GrantEntry> _grants;
    private readonly Dictionary<string, DenyEntry>  _denies;

    private readonly AliasResolver? _aliases;

    private WriteAllowlist? _allowlist;
    private bool _grantsFromAllowlist;

    /// <summary>
    /// Initialises a new <see cref="GatedTagService"/> that wraps
    /// <paramref name="inner"/> and enforces <paramref name="policy"/> on every write.
    /// </summary>
    /// <param name="inner">
    /// The backing <see cref="ITagService"/> to forward allowed writes to.
    /// Reads and connection calls are always forwarded regardless of policy.
    /// </param>
    /// <param name="policy">
    /// The write-gate policy derived from the currently open project.  Provides the
    /// grant and deny lists used to gate each <see cref="WriteAsync"/> call.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inner"/> or <paramref name="policy"/> is <c>null</c>.
    /// </exception>
    /// <param name="aliasTypes">
    /// The project declarations used to follow <c>AliasFor</c> before a write is judged: a write to an alias is
    /// gated, bounds-checked and forwarded as a write to its resolved base. Defaults to <paramref name="inner"/>
    /// when it is an <see cref="IAliasAware"/> service with a resolver (every backend) or an <see cref="ISessionTypeSource"/>; without
    /// one, alias names are judged as they stand (the policy carries an entry for each alias).
    /// </param>
    public GatedTagService(ITagService inner, WriteGatePolicy policy, ISessionTypeSource? aliasTypes = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policy);

        _inner  = inner;
        _policy = policy;
        _aliases = aliasTypes is not null ? new AliasResolver(aliasTypes)
                 : (inner as IAliasAware)?.Aliases
                   ?? (inner is ISessionTypeSource own ? new AliasResolver(own) : null);

        // Build case-insensitive dictionaries for fast per-write look-ups.
        // PLC tag names are case-insensitive in practice; OrdinalIgnoreCase matches
        // the convention used throughout VirtualTagService and GateEngine.
        _grants = new Dictionary<string, GrantEntry>(StringComparer.OrdinalIgnoreCase);
        _denies = new Dictionary<string, DenyEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in policy.Grants) _grants[g.ScopedName] = g;
        foreach (var d in policy.Denies) _denies[d.ScopedName] = d;
    }

    /// <summary>
    /// Exposes the wrapped <see cref="ITagService"/> so that callers needing the
    /// concrete service beneath the write-gate decorator can traverse the chain
    /// without reflection.  Used by <see cref="LogixSdkTools"/> to reach the
    /// underlying <see cref="TypedSdkTagService"/> and its
    /// <c>TypedSdkTagService.Session</c> property.
    /// </summary>
    public ITagService InnerService => _inner;

    /// <summary>
    /// The exact <see cref="WriteGatePolicy"/> instance this service enforces on
    /// every write. <c>logix_gate_report</c> reads it from here so the report can
    /// never diverge from what is actually enforced.
    /// </summary>
    public WriteGatePolicy Policy => _policy;

    /// <summary>
    /// The active write allowlist, or <c>null</c> when none is configured.
    /// </summary>
    public WriteAllowlist? Allowlist => _allowlist;

    /// <summary>
    /// Activates <paramref name="allowlist"/> as an extra deny rule, checked after every other
    /// gate rule: a write the gate grants is still denied with
    /// <see cref="DenyReason.NotOnAllowlist"/> unless an entry matches. It never grants a tag
    /// the gate denies. Pass <c>null</c> to clear.
    /// </summary>
    /// <param name="allowlist">The loaded allowlist, or <c>null</c>.</param>
    public void ApplyAllowlist(WriteAllowlist? allowlist) => _allowlist = allowlist;

    /// <summary>
    /// Whether grants come from the allowlist (partial-export mode): a tag the policy neither
    /// grants nor denies is granted when the allowlist lists it. The policy's denies and its
    /// grant ranges still apply.
    /// </summary>
    public bool GrantsFromAllowlist => _grantsFromAllowlist;

    /// <summary>
    /// Switches the service to partial-export mode. The policy was derived from a partial
    /// (routine/program) export, so it proves denies and ranges but cannot prove grants:
    /// the allowlist is the only source of grants. Tags the file does not mention are
    /// granted when listed; every deny and every range in the policy still applies.
    /// </summary>
    /// <param name="enabled"><c>true</c> to take grants from the allowlist.</param>
    public void GrantFromAllowlist(bool enabled = true) => _grantsFromAllowlist = enabled;

    /// <summary>
    /// In partial-export mode, a grant for a tag the policy knows nothing about but the
    /// allowlist lists. Returns <c>false</c> when the mode is off, no allowlist is active,
    /// the tag is not listed, the policy mentions it (grant or deny, so its deny or range
    /// applies through the normal path), or it carries a safety prefix.
    /// </summary>
    private bool TryAllowlistGrant(string tagName, out GrantEntry grant)
    {
        grant = null!;
        if (!_grantsFromAllowlist || _allowlist is null || !_allowlist.Permits(tagName)) return false;

        var cut      = tagName.IndexOf('[');
        var baseName = cut > 0 ? tagName[..cut] : tagName;
        if (_grants.ContainsKey(baseName) || _denies.ContainsKey(baseName)) return false;

        // A safety-prefixed controller tag is denied by name even when the partial file
        // only declares it in a program scope.
        var bare = baseName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase)
            ? baseName[(baseName.IndexOf('.') + 1)..] : baseName;
        if (GateConfigResolver.SafetyPrefixes.Any(p => bare.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        grant = new GrantEntry(baseName, null, null,
            $"Granted by write allowlist '{_allowlist.Path}'; the partial export does not declare this tag.");
        return true;
    }

    private void EnforceAllowlist(string tagName)
    {
        var message = _allowlist?.DenialMessage(tagName);
        if (message is null) return;
        AuditLogger.LogGateDecision(tagName, "denied", nameof(DenyReason.NotOnAllowlist), message);
        throw new TagServiceException(
            new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
    }

    /// <summary>
    /// Follows <c>AliasFor</c> when <paramref name="tagName"/> names an alias tag.
    /// </summary>
    /// <param name="tagName">The tag path about to be written.</param>
    /// <returns>The resolution (its <see cref="AliasReadResolver.Resolution.Target"/> is the base path), or <c>null</c> when the name is not an alias or no declarations are available.</returns>
    /// <exception cref="TagServiceException"><see cref="TagServiceErrorKind.AliasUnresolved"/> when the alias cannot be followed.</exception>
    public AliasReadResolver.Resolution? ResolveAliasForWrite(string tagName)
    {
        if (_aliases is null) return null;
        var resolution = _aliases.Walk(tagName);
        if (resolution is null) return null;
        if (resolution.ModuleIoTarget is { } ioTarget)
        {
            // The base is module I/O (<Module>:I / :O / :C), which is read-only whatever the alias is called.
            var ioMessage = $"Write denied for alias '{tagName}' (resolves to module I/O '{ioTarget}'): module I/O tags " +
                            "(<Module>:I, :O, :C) are read-only through this server; nothing was written.";
            AuditLogger.LogGateDecision(tagName, "denied", "ModuleIoReadOnly", ioMessage);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.WriteDenied, ioMessage, tagName));
        }
        if (!resolution.IsResolved)
        {
            var message = $"Cannot write '{tagName}': {resolution.Failure}. Nothing was written.";
            AuditLogger.LogGateDecision(tagName, "denied", nameof(TagServiceErrorKind.AliasUnresolved), message);
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.AliasUnresolved, message, tagName,
                Hint: "fix the alias's AliasFor in the project, or write its base tag directly"));
        }
        return resolution;
    }

    // ── Pass-through operations ───────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>Forwarded directly to the wrapped <see cref="ITagService"/>.</remarks>
    public Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
        => _inner.ConnectAsync(gateway, path, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>Forwarded directly to the wrapped <see cref="ITagService"/>.</remarks>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _inner.DisconnectAsync(cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Read always passes through — the write-gate policy imposes no restrictions on reads.
    /// </remarks>
    public Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
    {
        TagNameValidator.ThrowIfInvalid(tagName, "Read");
        return _inner.ReadAsync(tagName, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Forwarded directly to the wrapped <see cref="ITagService"/>.</remarks>
    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
        => _inner.ListTagsAsync(cancellationToken);

    // ── Gated write ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.WriteDenied"/> in two cases:
    /// <list type="bullet">
    ///   <item><description>
    ///     The tag is not present in the policy's grant list (either explicitly denied
    ///     or simply absent from the policy).
    ///   </description></item>
    ///   <item><description>
    ///     The tag is granted but carries scalar <see cref="GrantEntry.TagBounds"/> and
    ///     <paramref name="value"/> falls outside the allowed <c>[Min..Max]</c> range.
    ///   </description></item>
    ///   <item><description>
    ///     The tag name is a dotted member path (e.g. <c>Cmd_Motor.Speed</c>), the base
    ///     tag is granted, and the value falls outside the per-member
    ///     <see cref="GrantEntry.Bounds"/> for that member.
    ///   </description></item>
    /// </list>
    /// </exception>
    public Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        // ── Stage -2: normalise the name, refuse control characters ───────────
        // 'Cmd_Motor.Speed\t' must be matched against member bounds as
        // 'Cmd_Motor.Speed', and the normalised name is what is forwarded, so the
        // name the gate judged is the name that is written.
        tagName = NormalizeTagName(tagName);

        // ── Stage -3: an alias is judged, bounded and written as its resolved base ─
        // The gate decision must be about the tag that actually changes. An alias that cannot be
        // followed is refused (AliasUnresolved) rather than judged by its own, possibly permissive, name.
        var alias = ResolveAliasForWrite(tagName);
        if (alias is not null)
        {
            var baseName = alias.Target!;
            try
            {
                return WriteAsync(baseName, value, cancellationToken);
            }
            catch (TagServiceException ex) when (ex.Error.Kind == TagServiceErrorKind.WriteDenied)
            {
                var message = $"Write to alias '{tagName}' (resolves to '{baseName}') refused: {ex.Error.Message}";
                AuditLogger.LogGateDecision(tagName, "denied", "AliasBaseDenied", message);
                throw new TagServiceException(ex.Error with { Message = message, TagName = tagName });
            }
        }

        // ── Stage -2.5: module I/O (<Module>:I / :O / :C) is read-only ────────
        // Input, output and config images belong to the I/O system. A grant or allowlist entry cannot open them.
        if (ModuleIoTypes.IsModuleIoPath(tagName))
        {
            var message = $"Write denied for tag '{tagName}': module I/O tags (<Module>:I, :O, :C) are read-only " +
                          "through this server; nothing was written.";
            AuditLogger.LogGateDecision(tagName, "denied", "ModuleIoReadOnly", message);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
        }

        // ── Stage -1: non-finite values are refused, bounds or not ────────────
        // NaN fails every compare, so it would sail through the bounds check below.
        WriteTypeRule.RejectNonFinite(tagName, value, "GatedTagService");

        // ── Stage 0: member-path write (e.g., "Cmd_Motor.Speed") ─────────────
        // When the tag name contains a dot the caller is writing a single UDT
        // member rather than the whole tag.  Route through member-bounds checking
        // using the base tag's GrantEntry.Bounds list instead of TagBounds.
        // A program-scoped name 'Program:<P>.<Tag>' has a scope dot that is not a
        // member separator; the policy keys that whole string, so skip it.
        var dotIndex = FirstMemberDot(tagName);
        if (dotIndex >= 0)
            return HandleMemberWriteAsync(tagName, dotIndex, value, cancellationToken);

        // ── Stage 1: grant check ──────────────────────────────────────────────
        if (!_grants.TryGetValue(tagName, out var grant) && !TryAllowlistGrant(tagName, out grant))
        {
            // Build a message that includes denial reason and provenance when available.
            string message;
            string? reason     = null;
            string  provenance;

            if (_denies.TryGetValue(tagName, out var deny))
            {
                message    = $"Write denied for tag '{tagName}': {deny.Reason} — {deny.Provenance}";
                reason     = deny.Reason.ToString();
                provenance = deny.Provenance;
            }
            else if (_grantsFromAllowlist && _allowlist is not null)
            {
                // Partial-export mode: grants come only from the allowlist, so name it.
                message    = $"Write denied for tag '{tagName}': {DenyReason.NotOnAllowlist} — the tag is not listed " +
                             $"in the write allowlist file '{_allowlist.Path}' ({WriteAllowlist.EnvVar}), " +
                             "the only source of grants for a partial export.";
                reason     = nameof(DenyReason.NotOnAllowlist);
                provenance = message;
            }
            else
            {
                message    = $"Write denied for tag '{tagName}': tag is not in the write-gate policy grant list.";
                provenance = message;
            }

            AuditLogger.LogGateDecision(tagName, "denied", reason, provenance);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
        }

        // ── Stage 2: scalar bounds check (TagBounds only) ─────────────────────
        // Bounds enforcement applies to scalar REAL/INT-family tags that carry a
        // range annotation in their description (TagBounds populated by GateEngine).
        // UDT member bounds (Bounds) are not enforced here because a tag-level write
        // carries the entire UDT value; member-level enforcement would require
        // introspecting the value object, which is outside the gate's responsibility.
        if (grant.TagBounds is { } bounds)
        {
            if (!TryConvertToDouble(value, out var numericValue))
                RefuseNonNumeric(tagName, value, bounds.Min, bounds.Max, grant);

            if (numericValue < bounds.Min || numericValue > bounds.Max)
            {
                var message = $"Write denied for tag '{tagName}': value {numericValue} is outside " +
                              $"the allowed range [{bounds.Min}..{bounds.Max}]. {grant.Provenance}";

                AuditLogger.LogGateDecision(tagName, "denied", "BoundsViolation", message);
                throw new TagServiceException(
                    new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
            }
        }

        // ── Stage 3: allowlist (extra deny rule, after every gate rule) ───────
        EnforceAllowlist(tagName);

        // ── Stage 4: granted, in-bounds and allowlisted — forward ─────────────
        AuditLogger.LogGateDecision(tagName, "granted", null, grant.Provenance);
        return _inner.WriteAsync(tagName, value, cancellationToken);
    }

    /// <summary>
    /// Handles writes to a dotted member path such as <c>Cmd_Motor.Speed</c>.
    /// The base tag (everything before the first dot) must appear in the grant list.
    /// If the base tag's <see cref="GrantEntry.Bounds"/> contains an entry for the
    /// named member and the value can be converted to a <see cref="double"/>, the
    /// value is validated against that member's <c>[Min..Max]</c> range before the
    /// write is forwarded to the inner service.
    /// </summary>
    /// <param name="tagName">Full dotted tag name (e.g. <c>Cmd_Motor.Speed</c>).</param>
    /// <param name="dotIndex">Index of the first <c>.</c> character in <paramref name="tagName"/>.</param>
    /// <param name="value">Value to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.WriteDenied"/> when the base tag
    /// is not granted or the value violates the member's bounds.
    /// </exception>
    private Task HandleMemberWriteAsync(string tagName, int dotIndex, object value,
        CancellationToken cancellationToken)
    {
        var baseTagName = tagName[..dotIndex];
        var memberName  = tagName[(dotIndex + 1)..];

        // ── Grant check on base tag ───────────────────────────────────────────
        if (!_grants.TryGetValue(baseTagName, out var grant) && !TryAllowlistGrant(baseTagName, out grant))
        {
            string message;
            string? reason     = null;
            string  provenance;

            if (_denies.TryGetValue(baseTagName, out var deny))
            {
                message    = $"Write denied for tag '{tagName}': {deny.Reason} — {deny.Provenance}";
                reason     = deny.Reason.ToString();
                provenance = deny.Provenance;
            }
            else if (_grantsFromAllowlist && _allowlist is not null)
            {
                message    = $"Write denied for tag '{tagName}': {DenyReason.NotOnAllowlist} — base tag '{baseTagName}' " +
                             $"is not listed in the write allowlist file '{_allowlist.Path}' ({WriteAllowlist.EnvVar}), " +
                             "the only source of grants for a partial export.";
                reason     = nameof(DenyReason.NotOnAllowlist);
                provenance = message;
            }
            else
            {
                message    = $"Write denied for tag '{tagName}': base tag '{baseTagName}' is not in the write-gate policy grant list.";
                provenance = message;
            }

            AuditLogger.LogGateDecision(tagName, "denied", reason, provenance);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
        }

        // ── Path parse (member chain, array indices, trailing bit selector) ───
        // A path the gate cannot parse is refused: it cannot be matched to bounds.
        var path = ParseMemberPath(memberName);
        if (path is null)
        {
            var message = $"Write denied for '{tagName}': the path cannot be resolved against " +
                          $"the grant for '{baseTagName}'. {grant.Provenance}";
            AuditLogger.LogGateDecision(tagName, "denied", "UnresolvablePath", message);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
        }

        // ── Scalar bounds: every bit / sub-element write under it is refused ──
        if (grant.TagBounds is { } scalar)
            RefuseBoundedPathWrite(tagName, baseTagName, scalar.Min, scalar.Max, grant);

        // ── Member bounds check (normalized path, case-insensitive) ───────────
        if (grant.Bounds is not null)
        {
            var normalized = string.Join('.', path.Names);
            var memberBounds = grant.Bounds.FirstOrDefault(b =>
                string.Equals(b.Member, normalized, StringComparison.OrdinalIgnoreCase));

            if (memberBounds is not null)
            {
                // A bit or array-element write to the bounded member itself would
                // change the value without the range ever being compared.
                if (path.Bit is not null || path.LastIndexed)
                    RefuseBoundedPathWrite(tagName, $"{baseTagName}.{memberBounds.Member}",
                        memberBounds.Min, memberBounds.Max, grant);

                if (!TryConvertToDouble(value, out var numericValue))
                    RefuseNonNumeric(tagName, value, memberBounds.Min, memberBounds.Max, grant);

                if (numericValue < memberBounds.Min || numericValue > memberBounds.Max)
                {
                    var message = $"Write denied for '{tagName}': value {numericValue} is outside " +
                                  $"the allowed range [{memberBounds.Min}..{memberBounds.Max}]. " +
                                  $"{grant.Provenance}";

                    AuditLogger.LogGateDecision(tagName, "denied", "BoundsViolation", message);
                    throw new TagServiceException(
                        new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
                }
            }
            else
            {
                // Writing beneath a bounded member (e.g. Speed.Foo) is a sub-element write.
                var ancestor = grant.Bounds.FirstOrDefault(b =>
                    normalized.StartsWith(b.Member + ".", StringComparison.OrdinalIgnoreCase));
                if (ancestor is not null)
                    RefuseBoundedPathWrite(tagName, $"{baseTagName}.{ancestor.Member}",
                        ancestor.Min, ancestor.Max, grant);
            }
        }

        // ── Allowlist (extra deny rule, after every gate rule) ────────────────
        EnforceAllowlist(tagName);

        // ── Granted, in-bounds and allowlisted — forward to the inner service ─
        AuditLogger.LogGateDecision(tagName, "granted", null, grant.Provenance);
        return _inner.WriteAsync(tagName, value, cancellationToken);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Trims a write target name and removes whitespace around member and index
    /// separators (<c>.</c>, <c>[</c>, <c>]</c>), so the policy and member-bounds
    /// lookups see the same path the controller would resolve.
    /// </summary>
    /// <param name="tagName">Name as supplied by the caller.</param>
    /// <returns>The normalised name.</returns>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.InvalidTagName"/> when the name is
    /// empty or contains a control character once trimmed.
    /// </exception>
    private static string NormalizeTagName(string tagName)
    {
        var trimmed = (tagName ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
        {
            var shown = (tagName ?? string.Empty).Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
            var message = $"Write denied: tag name '{shown}' is empty or contains a control " +
                          "character and cannot be matched to the write-gate policy.";
            AuditLogger.LogGateDecision(shown, "denied", "InvalidTagName", message);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.InvalidTagName, message, tagName ?? string.Empty));
        }
        var normalized = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s*([.\[\]])\s*", "$1");
        if (!TagNameValidator.IsValid(normalized))
        {
            var message = $"Write denied: tag name '{normalized}' is not a valid tag path " +
                          "(identifier segments joined by '.', with [n] or [n,m] indices and ':' only).";
            AuditLogger.LogGateDecision(normalized, "denied", "InvalidTagName", message);
            throw new TagServiceException(
                new TagServiceError(TagServiceErrorKind.InvalidTagName, message, tagName ?? string.Empty));
        }
        return normalized;
    }

    /// <summary>
    /// Index of the dot that begins the member path, or -1 for a whole-tag name.
    /// For <c>Program:P.Tag[.Member]</c> the first dot is the scope separator and is skipped.
    /// </summary>
    private static int FirstMemberDot(string tagName)
    {
        var first = tagName.IndexOf('.');
        if (first < 0 || !tagName.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            return first;
        return tagName.IndexOf('.', first + 1);
    }

    /// <summary>
    /// A write path after the base tag: member names with array indices and the
    /// bit selector removed.
    /// </summary>
    /// <param name="Names">Member names in order, indices and bit selector stripped.</param>
    /// <param name="LastIndexed">True when the final member name carries array indices.</param>
    /// <param name="Bit">The trailing bit selector (<c>.31</c>), or <c>null</c>.</param>
    private sealed record MemberPath(IReadOnlyList<string> Names, bool LastIndexed, int? Bit);

    /// <summary>
    /// Parses the part of a write path that follows the base tag (e.g.
    /// <c>Speeds[2]</c>, <c>Sub.Speed</c>, <c>Speed.31</c>). Returns <c>null</c> for
    /// anything malformed so the caller can fail closed.
    /// </summary>
    /// <param name="rest">Path text after the first dot.</param>
    /// <returns>The parsed path, or <c>null</c> when it cannot be parsed.</returns>
    private static MemberPath? ParseMemberPath(string rest)
    {
        var names = new List<string>();
        int? bit = null;
        var lastIndexed = false;
        var pos = 0;

        while (true)
        {
            var start = pos;
            while (pos < rest.Length && rest[pos] != '.' && rest[pos] != '[') pos++;
            var name = rest[start..pos];
            if (name.Length == 0 || bit is not null) return null;

            var indexed = false;
            while (pos < rest.Length && rest[pos] == '[')
            {
                var depth = 0;
                var open  = pos;
                for (; pos < rest.Length; pos++)
                {
                    if (rest[pos] == '[') depth++;
                    else if (rest[pos] == ']' && --depth == 0) break;
                }
                if (pos >= rest.Length || pos == open + 1) return null;
                pos++;
                indexed = true;
            }

            if (name.All(char.IsAsciiDigit))
            {
                // A numeric segment is a bit selector: it must be last and unindexed.
                if (indexed) return null;
                if (!int.TryParse(name, out var b)) return null;
                bit = b;
            }
            else
            {
                names.Add(name);
                lastIndexed = indexed;
            }

            if (pos >= rest.Length) break;
            if (rest[pos] != '.') return null;
            pos++;
        }

        return new MemberPath(names, lastIndexed, bit);
    }

    /// <summary>
    /// Refuses a bit or sub-element write to a range-checked value, logging the decision.
    /// </summary>
    private static void RefuseBoundedPathWrite(string tagName, string boundedName,
        double min, double max, GrantEntry grant)
    {
        var message = $"Write denied for '{tagName}': '{boundedName}' is range-checked " +
                      $"[{min}..{max}]; bit and element writes to a range-checked value " +
                      $"are refused; write the whole value. {grant.Provenance}";
        AuditLogger.LogGateDecision(tagName, "denied", "BoundedPathWrite", message);
        throw new TagServiceException(
            new TagServiceError(TagServiceErrorKind.WriteDenied, message, tagName));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses a write whose value cannot be converted to a number for a range-checked
    /// tag or member: the range could not be compared, so the write fails closed with
    /// <see cref="TagServiceErrorKind.InvalidValue"/> and an audit entry.
    /// </summary>
    /// <param name="tagName">Full tag path of the write.</param>
    /// <param name="value">The offending value.</param>
    /// <param name="min">Inclusive lower bound.</param>
    /// <param name="max">Inclusive upper bound.</param>
    /// <param name="grant">Grant carrying the provenance text.</param>
    /// <exception cref="TagServiceException">Always.</exception>
    private static void RefuseNonNumeric(string tagName, object value, double min, double max,
        GrantEntry grant)
    {
        var message = $"Write refused for '{tagName}': value '{value}' is not numeric, but the " +
                      $"target is range-checked [{min}..{max}]; nothing was written. {grant.Provenance}";
        AuditLogger.LogGateDecision(tagName, "denied", "NonNumericValue", message);
        throw new TagServiceException(
            new TagServiceError(TagServiceErrorKind.InvalidValue, message, tagName));
    }

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to a <see cref="double"/> using
    /// <see cref="Convert.ToDouble(object, System.IFormatProvider)"/> with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <param name="value">The boxed value to convert.</param>
    /// <param name="result">
    /// Set to the converted double on success; <c>0</c> on failure.
    /// </param>
    /// <returns>
    /// <c>true</c> when conversion succeeds (numeric types such as <c>int</c>,
    /// <c>float</c>, <c>double</c>, etc.); <c>false</c> for types that cannot be
    /// meaningfully compared to a numeric range (e.g. complex UDT objects,
    /// <c>string</c>).
    /// </returns>
    private static bool TryConvertToDouble(object value, out double result)
    {
        try
        {
            result = Convert.ToDouble(value,
                System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }
}
