using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using L5Sharp.Core;

// ── Configuration ────────────────────────────────────────────────────────────

/// <summary>
/// Configuration for the write-gate engine.  Determines which tag-name prefixes
/// identify command (writable) tags and which prefixes identify safety tags.
/// </summary>
/// <param name="CommandPrefixes">
/// Tag-name prefixes (case-insensitive) that designate writable command tags.
/// Defaults to <c>["Cmd_", "Sim_"]</c>.
/// </param>
/// <param name="SafetyPrefixes">
/// Tag-name prefixes (case-insensitive) that designate safety-interlock tags.
/// Tags whose names start with a safety prefix are denied write access.
/// Defaults to <c>["Safety_"]</c>.
/// </param>
public record GateConfig(
    [property: JsonPropertyName("commandPrefixes")] IReadOnlyList<string> CommandPrefixes,
    [property: JsonPropertyName("safetyPrefixes")]  IReadOnlyList<string> SafetyPrefixes)
{
    /// <summary>
    /// Default configuration: command prefixes <c>Cmd_</c> and <c>Sim_</c>;
    /// safety prefix <c>Safety_</c>.
    /// </summary>
    public static GateConfig Default { get; } = new(
        CommandPrefixes: new[] { "Cmd_", "Sim_" },
        SafetyPrefixes:  new[] { "Safety_" });

    /// <summary>
    /// Where the command prefixes came from: <c>"default"</c> or
    /// <c>"LGX_GATE_COMMAND_PREFIXES"</c> (see <see cref="GateConfigResolver"/>).
    /// </summary>
    [JsonPropertyName("commandPrefixesSource")]
    public string CommandPrefixesSource { get; init; } = GateConfigResolver.SourceDefault;

    /// <summary>
    /// <c>true</c> when <c>*</c> designates every tag name (relaxes the naming rule only;
    /// every other deny still applies). Internally the prefix list is a single empty prefix.
    /// </summary>
    [JsonIgnore]
    public bool AllNamesDesignated { get; init; }

    /// <summary>Warnings produced while resolving the configuration (shown in the connect result).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The effective command prefixes for display: the list, or <c>["*"]</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveCommandPrefixes =>
        AllNamesDesignated ? new[] { GateConfigResolver.Wildcard } : CommandPrefixes;

    /// <summary>
    /// Wire value of <c>commandPrefixes</c>: the effective list, or the string <c>"*"</c>
    /// when every tag name is designated.
    /// </summary>
    [JsonIgnore]
    public object CommandPrefixesWire =>
        AllNamesDesignated ? GateConfigResolver.Wildcard : CommandPrefixes;

    /// <summary>The effective command prefixes as one string: <c>"Cmd_,Sim_"</c> or <c>"*"</c>.</summary>
    [JsonIgnore]
    public string CommandPrefixesText => string.Join(",", EffectiveCommandPrefixes);
}

// ── WriteGatePolicy constituent types ────────────────────────────────────────

/// <summary>
/// Reason why a tag was denied write access by the gate engine.
/// </summary>
public enum DenyReason
{
    /// <summary>
    /// The tag is written by program logic (e.g. an OTE coil or MOV destination),
    /// meaning it is an output driven by the PLC program rather than an operator
    /// or external-system setpoint.
    /// </summary>
    ProgramWritten,

    /// <summary>
    /// The tag name matches one of the configured <see cref="GateConfig.SafetyPrefixes"/>,
    /// indicating it is a safety-interlock signal that must not be overridden.
    /// </summary>
    SafetyTag,

    /// <summary>
    /// The tag name does not match any <see cref="GateConfig.CommandPrefixes"/> entry,
    /// is not written by program logic, and is not a safety-prefixed tag.
    /// </summary>
    NotDesignated,

    /// <summary>
    /// The tag matches a command prefix, but it also appears as an operand of an
    /// instruction the classifier cannot interpret, so the engine cannot prove the
    /// tag is not driven by program logic.
    ///
    /// <para>
    /// This is the fail-closed direction. Classifying an unknown instruction as
    /// "not a write" would silently grant write access to a tag the program may be
    /// driving — exactly the claim the gate exists to make. Resolve by teaching
    /// <see cref="WriteClassifier"/> the instruction, or by confirming the operand
    /// is a read and overriding deliberately.
    /// </para>
    /// </summary>
    UnknownWriteContext,

    /// <summary>
    /// The tag was created by the current server process during the active session
    /// (e.g. via a controller-path rung edit that included a tag declaration).
    ///
    /// <para>
    /// Writes are denied for the lifetime of the session in which the tag was
    /// created. After a new <c>logix_connect</c> clears the session record, the
    /// tag follows ordinary gate policy.  This is defence in depth: tags created
    /// through lgx-mcp are External Access Read Only, so a later session is denied
    /// as <see cref="ExternalAccessNotReadWrite"/> until an engineer sets them to
    /// Read/Write in Logix Designer.
    /// </para>
    /// </summary>
    CreatedByThisSession,

    /// <summary>
    /// The tag's External Access is not <c>Read/Write</c> (it is <c>Read Only</c>
    /// or <c>None</c>). External Access is the engineer-owned write grant: an
    /// engineer permits external writes by setting it to Read/Write in Logix
    /// Designer, and nothing the gate derives from names can override that.
    /// A missing <c>ExternalAccess</c> attribute is treated as Read/Write.
    /// </summary>
    ExternalAccessNotReadWrite,

    /// <summary>
    /// The write gate would grant the tag, but an allowlist file
    /// (<c>LGX_WRITE_ALLOWLIST</c>) is active and no entry matches it.
    /// The allowlist is an extra deny rule checked after every other rule: it
    /// narrows what the gate grants and can never grant a tag the gate denies.
    /// </summary>
    NotOnAllowlist,
}

/// <summary>
/// Per-member bounds parsed from a UDT member description (e.g. "range 0..100").
/// </summary>
/// <param name="Member">Member name within the UDT.</param>
/// <param name="Min">Lower bound extracted from the description.</param>
/// <param name="Max">Upper bound extracted from the description.</param>
public record MemberBounds(
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("min")]    double Min,
    [property: JsonPropertyName("max")]    double Max);

/// <summary>
/// Numeric bounds parsed from a scalar tag description (e.g. "range 0..1000").
/// Applies to REAL/INT-family scalar tags when a <c>range N..M</c> annotation is
/// present in the tag's Description field.
/// </summary>
/// <param name="Min">Lower bound extracted from the description.</param>
/// <param name="Max">Upper bound extracted from the description.</param>
public record ScalarBounds(
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max);

/// <summary>
/// One entry in the grant list of a <see cref="WriteGatePolicy"/>.
/// Represents a tag that the gate engine allows to be written.
/// </summary>
/// <param name="Tag">Tag name.</param>
/// <param name="TagBounds">
/// Numeric bounds parsed from the tag's own Description field, populated only for
/// REAL/INT-family scalar tags that carry a <c>range N..M</c> annotation.
/// <c>null</c> when the tag is a UDT type, a non-numeric scalar, or has no range
/// annotation in its description.
/// </param>
/// <param name="Bounds">
/// Per-member numeric bounds derived from UDT member descriptions, or <c>null</c>
/// if the tag is a scalar type or has no annotated member bounds.
/// </param>
/// <param name="Provenance">
/// Human-readable explanation of why this tag was granted, including which L5X
/// element(s) justified the decision.
/// </param>
/// <param name="ExternalAccess">
/// The tag's External Access value (<c>Read/Write</c> for every grant; a missing
/// attribute is reported as <c>Read/Write</c>, the L5X and Studio default).
/// </param>
public record GrantEntry(
    [property: JsonPropertyName("tag")]            string Tag,
    [property: JsonPropertyName("tagBounds")]      ScalarBounds? TagBounds,
    [property: JsonPropertyName("bounds")]         IReadOnlyList<MemberBounds>? Bounds,
    [property: JsonPropertyName("provenance")]     string Provenance,
    [property: JsonPropertyName("externalAccess")] string ExternalAccess = "Read/Write")
{
    /// <summary>
    /// Whether the active write allowlist lists this tag; <c>null</c> (omitted) when no allowlist is active.
    /// </summary>
    [JsonPropertyName("onAllowlist")]
    public bool? OnAllowlist { get; init; }

    /// <summary>
    /// Owning program name for a program-scoped tag; <c>null</c> (omitted) for a controller
    /// tag. <see cref="Tag"/> stays the bare name; enforcement keys on <see cref="ScopedName"/>.
    /// </summary>
    [JsonPropertyName("program")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; init; }

    /// <summary>
    /// Scope-qualified key: the bare name for a controller tag, <c>Program:&lt;P&gt;.&lt;Tag&gt;</c>
    /// for a program tag.
    /// </summary>
    [JsonIgnore]
    public string ScopedName => Program is null ? Tag : GateEngine.ProgramTagName(Program, Tag);
}

/// <summary>
/// One entry in the deny list of a <see cref="WriteGatePolicy"/>.
/// Represents a tag that the gate engine prohibits from being written.
/// </summary>
/// <param name="Tag">Tag name.</param>
/// <param name="Reason">Enumerated reason for the denial.</param>
/// <param name="Provenance">
/// Human-readable explanation of why this tag was denied, including the specific
/// L5X element(s) that triggered the denial.
/// </param>
/// <param name="ExternalAccess">
/// The tag's External Access value (a missing attribute is reported as
/// <c>Read/Write</c>, the L5X and Studio default).
/// </param>
public record DenyEntry(
    [property: JsonPropertyName("tag")]            string     Tag,
    [property: JsonPropertyName("reason")]         DenyReason Reason,
    [property: JsonPropertyName("provenance")]     string     Provenance,
    [property: JsonPropertyName("externalAccess")] string     ExternalAccess = "Read/Write")
{
    /// <summary>
    /// Whether the active write allowlist lists this tag; <c>null</c> (omitted) when no allowlist is active.
    /// </summary>
    [JsonPropertyName("onAllowlist")]
    public bool? OnAllowlist { get; init; }

    /// <summary>
    /// Owning program name for a program-scoped tag; <c>null</c> (omitted) for a controller
    /// tag. <see cref="Tag"/> stays the bare name; enforcement keys on <see cref="ScopedName"/>.
    /// </summary>
    [JsonPropertyName("program")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; init; }

    /// <summary>
    /// Scope-qualified key: the bare name for a controller tag, <c>Program:&lt;P&gt;.&lt;Tag&gt;</c>
    /// for a program tag.
    /// </summary>
    [JsonIgnore]
    public string ScopedName => Program is null ? Tag : GateEngine.ProgramTagName(Program, Tag);
}

// ── UnparsedContent ──────────────────────────────────────────────────────────

/// <summary>
/// One piece of project content the gate could not parse (a rung whose text the tokenizer
/// rejects, an element L5Sharp cannot deserialise, ...). Recorded in the policy so the engineer
/// sees it; every tag whose write context depends on it fails closed
/// (<see cref="DenyReason.UnknownWriteContext"/>) and is never granted.
/// </summary>
/// <param name="Location">Where the content sits, e.g. <c>Program/Routine[Rung]</c>.</param>
/// <param name="Text">The first <see cref="MaxTextLength"/> characters of the content.</param>
/// <param name="Reason">Why it could not be parsed.</param>
/// <param name="Rung">
/// The structured program / routine / rung / error when the content is a ladder rung, otherwise <c>null</c>.
/// Reported as <c>unparsedRungs</c> by <c>logix_gate_report</c> and <c>logix_connect</c>.
/// </param>
public record UnparsedContent(
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("text")]     string Text,
    [property: JsonPropertyName("reason")]   string Reason,
    [property: JsonIgnore] UnparsedRungEntry? Rung = null)
{
    /// <summary>Maximum number of content characters kept in <see cref="Text"/>.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Maximum number of entries a report lists (the count is always complete).</summary>
    public const int MaxReported = 10;

    /// <summary>Creates an entry, truncating <paramref name="text"/> to <see cref="MaxTextLength"/> characters.</summary>
    /// <param name="location">Where the content sits.</param>
    /// <param name="text">The content (may be <c>null</c> when it could not even be read).</param>
    /// <param name="reason">Why it could not be parsed.</param>
    /// <param name="rung">The rung, when the content is a ladder rung.</param>
    public static UnparsedContent Create(string location, string? text, string reason, UnparsedRungEntry? rung = null)
    {
        text ??= string.Empty;
        return new UnparsedContent(location,
            text.Length > MaxTextLength ? text[..MaxTextLength] : text, reason, rung);
    }

    /// <summary>The rung entries among <paramref name="unparsed"/>, capped at <see cref="MaxReported"/>; <c>null</c> when there are none.</summary>
    /// <param name="unparsed">Unparsed content of a policy.</param>
    /// <returns>The first <see cref="MaxReported"/> unparsed rungs, or <c>null</c>.</returns>
    public static IReadOnlyList<UnparsedRungEntry>? RungsOf(IEnumerable<UnparsedContent> unparsed)
    {
        var rungs = unparsed.Where(u => u.Rung is not null).Select(u => u.Rung!).Take(MaxReported).ToList();
        return rungs.Count == 0 ? null : rungs;
    }

    /// <summary>Number of rung entries among <paramref name="unparsed"/>.</summary>
    /// <param name="unparsed">Unparsed content of a policy.</param>
    /// <returns>The count of ladder rungs that could not be parsed.</returns>
    public static int RungCount(IEnumerable<UnparsedContent> unparsed) => unparsed.Count(u => u.Rung is not null);
}

// ── WriteGatePolicy ──────────────────────────────────────────────────────────

/// <summary>
/// Serializable write-gate policy produced by <see cref="GateEngine.DerivePolicy"/>.
/// Contains a grant list (writable tags with optional bounds) and a deny list
/// (tags excluded for one of the three <see cref="DenyReason"/> values).
/// </summary>
/// <param name="Grants">Tags that may be written, with optional bounds and provenance.</param>
/// <param name="Denies">Tags that must not be written, with denial reason and provenance.</param>
public record WriteGatePolicy(
    [property: JsonPropertyName("grants")] IReadOnlyList<GrantEntry> Grants,
    [property: JsonPropertyName("denies")] IReadOnlyList<DenyEntry>  Denies)
{
    /// <summary>
    /// Where this policy was derived from and whether a live session enforces it.
    /// <c>null</c> when the origin was not recorded. Surfaced by
    /// <c>logix_gate_report</c> as <c>policySource</c>, <c>sourcePath</c> and
    /// <c>enforcedBySession</c>.
    /// </summary>
    [JsonIgnore]
    public GatePolicyOrigin? Origin { get; init; }

    /// <summary>
    /// Content the gate could not parse while deriving this policy (empty when everything
    /// parsed). Tags whose write context depends on it are denied as
    /// <see cref="DenyReason.UnknownWriteContext"/>. Reported by <c>logix_connect</c> and
    /// <c>logix_gate_report</c> as <c>unparsedContentCount</c> plus the first
    /// <see cref="UnparsedContent.MaxReported"/> entries.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<UnparsedContent> Unparsed { get; init; } = Array.Empty<UnparsedContent>();

    /// <summary>
    /// The write allowlist (<c>LGX_WRITE_ALLOWLIST</c>) active for the session,
    /// or <c>null</c>. Used only for reporting: <see cref="ToJson(int, string, string?, IReadOnlyList{DivergenceEntry}?)"/>
    /// adds the <c>allowlist</c> object and a per-tag <c>onAllowlist</c> flag. It never
    /// changes the grant or deny lists themselves.
    /// </summary>
    [JsonIgnore]
    public WriteAllowlist? Allowlist { get; init; }

    /// <summary>
    /// Wire value of <c>commandPrefixes</c> the policy was derived with (list, or <c>"*"</c>);
    /// <c>null</c> when not recorded (partial export, which has its own meaning).
    /// </summary>
    [JsonIgnore]
    public object? CommandPrefixesWire { get; init; }

    /// <summary><c>commandPrefixesSource</c> the policy was derived with; <c>null</c> when not recorded.</summary>
    [JsonIgnore]
    public string? CommandPrefixesSource { get; init; }

    /// <summary>
    /// Free-text note surfaced by <c>logix_gate_report</c> as <c>note</c>; <c>null</c> (omitted)
    /// for an ordinary policy. Set for the partial-export plus allowlist report.
    /// </summary>
    [JsonIgnore]
    public string? Note { get; init; }

    /// <summary>
    /// Default cap on the number of deny entries serialized by <see cref="ToJson"/>.
    /// A 1,008-tag project produced a 185 KB response with every deny expanded,
    /// which is unusable as a single MCP tool result; a 10,000-tag project would
    /// produce roughly 1.8 MB. Truncation is always reported explicitly — see
    /// <see cref="ToJson(int)"/>.
    /// </summary>
    public const int DefaultMaxDenies = 100;

    // Shared serializer options: no indentation, null properties omitted, enums as
    // their names. Without the enum converter DenyReason serializes as an integer
    // ("reason": 2) while the gate's own rejection messages use the name, and the
    // documented tool output in docs/DEMO.md does not match what ships.
    // Declared static readonly so options are built once and reused on every call.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
        Converters             = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serializes this policy to a stable, deterministic JSON string using the
    /// <c>"enforced"</c> policy label and no disclaimer.
    /// Grant and deny entries are sorted alphabetically by tag name (ordinal),
    /// ensuring byte-for-byte identical output for the same policy content on
    /// every invocation regardless of the order tags were discovered in the L5X.
    /// </summary>
    /// <returns>
    /// A compact JSON string whose <c>grants</c> and <c>denies</c> arrays are
    /// sorted ascending by tag name using <see cref="StringComparer.Ordinal"/>.
    /// </returns>
    public string ToJson() => ToJson(DefaultMaxDenies);

    /// <summary>
    /// Serializes this policy, emitting at most <paramref name="maxDenies"/> deny
    /// entries, using the <c>"enforced"</c> policy label and no disclaimer.
    /// Grants are always emitted in full — they are the security-relevant
    /// half and are few even on large projects.
    ///
    /// <para>
    /// <c>denyCounts</c> (a per-reason tally over <em>all</em> denies) is always
    /// present, so a truncated report still states the complete picture.  When
    /// entries are dropped, <c>deniesTruncated</c> names how many of how many are
    /// shown; truncation is never silent.  Pass a negative
    /// <paramref name="maxDenies"/> to disable the cap.
    /// </para>
    /// </summary>
    public string ToJson(int maxDenies) => ToJson(maxDenies, "enforced", null);

    /// <summary>
    /// Serializes this policy, emitting at most <paramref name="maxDenies"/> deny
    /// entries with an explicit <paramref name="policyLabel"/> and optional
    /// <paramref name="disclaimer"/>.
    ///
    /// <para>
    /// Use <paramref name="policyLabel"/> <c>"enforced"</c> for reports derived from
    /// <see cref="GateConfigResolver.Current"/> (the same configuration that
    /// <c>logix_write</c> applies), and <c>"hypothetical"</c> for what-if reports
    /// produced from caller-supplied prefix lists.  When <paramref name="disclaimer"/>
    /// is non-null it appears in the JSON output as the <c>disclaimer</c> field —
    /// required on all <c>"hypothetical"</c> payloads so consumers cannot mistake
    /// the result for an authoritative classification.
    /// </para>
    /// </summary>
    /// <param name="maxDenies">
    /// Maximum number of deny entries to include; pass a negative value to disable
    /// the cap.  The <c>denyCounts</c> tally covers all denies regardless of this cap.
    /// </param>
    /// <param name="policyLabel">
    /// Value written to the <c>policy</c> JSON field — either <c>"enforced"</c>
    /// or <c>"hypothetical"</c>.
    /// </param>
    /// <param name="disclaimer">
    /// Human-readable (and machine-readable) statement written to the
    /// <c>disclaimer</c> JSON field, or <c>null</c> to omit the field.
    /// Must be non-null for <c>"hypothetical"</c> payloads.
    /// </param>
    public string ToJson(int maxDenies, string policyLabel, string? disclaimer) =>
        ToJson(maxDenies, policyLabel, disclaimer, null);

    /// <summary>
    /// Serializes this policy, emitting at most <paramref name="maxDenies"/> deny
    /// entries with an explicit <paramref name="policyLabel"/>, optional
    /// <paramref name="disclaimer"/>, and an optional <paramref name="divergence"/> list.
    ///
    /// <para>
    /// The <paramref name="divergence"/> list is present only on
    /// <c>"hypothetical"</c> payloads and lists every tag whose classification
    /// differs between the enforced policy (<see cref="GateConfigResolver.Current"/>) and the
    /// hypothetical policy, with both dispositions shown.  It is <c>null</c> (omitted
    /// from JSON) for <c>"enforced"</c> payloads.
    /// </para>
    /// </summary>
    /// <param name="maxDenies">
    /// Maximum number of deny entries to include; pass a negative value to disable
    /// the cap.
    /// </param>
    /// <param name="policyLabel">
    /// Value written to the <c>policy</c> JSON field — either <c>"enforced"</c>
    /// or <c>"hypothetical"</c>.
    /// </param>
    /// <param name="disclaimer">
    /// Human-readable disclaimer written to the <c>disclaimer</c> JSON field, or
    /// <c>null</c> to omit the field.
    /// </param>
    /// <param name="divergence">
    /// Tags whose classification differs between the enforced and hypothetical
    /// policies, or <c>null</c> for enforced payloads.
    /// </param>
    public string ToJson(
        int maxDenies,
        string policyLabel,
        string? disclaimer,
        IReadOnlyList<DivergenceEntry>? divergence)
    {
        var grants = Grants.OrderBy(g => g.Tag, StringComparer.Ordinal).ToList();
        var denies = Denies.OrderBy(d => d.Tag, StringComparer.Ordinal).ToList();

        var allowlist = Allowlist;
        if (allowlist is not null)
        {
            // A grant the allowlist excludes is not writable: report it under denies
            // (NotOnAllowlist) so the report matches what logix_write does.
            var removed = grants
                .Where(g => !allowlist.Permits(g.ScopedName))
                .Select(g => new DenyEntry(
                    g.Tag,
                    DenyReason.NotOnAllowlist,
                    $"granted by {g.Provenance}, removed by allowlist {allowlist.Path}",
                    g.ExternalAccess)
                { OnAllowlist = false, Program = g.Program })
                .ToList();
            grants = grants
                .Where(g => allowlist.Permits(g.ScopedName))
                .Select(g => g with { OnAllowlist = true })
                .ToList();
            denies = denies
                .Select(d => d with { OnAllowlist = allowlist.Permits(d.ScopedName) })
                .Concat(removed)
                .OrderBy(d => d.Tag, StringComparer.Ordinal)
                .ToList();
        }

        var counts = denies
            .GroupBy(d => d.Reason)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);

        var shown = maxDenies >= 0 && denies.Count > maxDenies
            ? denies.Take(maxDenies).ToList()
            : denies;

        var truncation = shown.Count < denies.Count
            ? new DenyTruncation(shown.Count, denies.Count)
            : null;

        return JsonSerializer.Serialize(
            new GateReportPayload(policyLabel, grants, shown, counts, truncation, disclaimer, divergence)
            {
                CommandPrefixes       = CommandPrefixesWire,
                CommandPrefixesSource = CommandPrefixesSource,
                PolicySource      = Origin?.Source,
                SourcePath        = Origin?.Path,
                EnforcedBySession = Origin?.EnforcedBySession,
                Note              = Note,
                UnparsedContentCount = Unparsed.Count,
                UnparsedContent   = Unparsed.Count == 0
                    ? null
                    : Unparsed.Take(UnparsedContent.MaxReported).ToList(),
                UnparsedRungsCount = UnparsedContent.RungCount(Unparsed),
                UnparsedRungs     = UnparsedContent.RungsOf(Unparsed),
                Allowlist         = allowlist is null
                    ? null
                    : new AllowlistStatus(allowlist.Path, allowlist.EntryCount, allowlist.Sha256),
            },
            _jsonOptions);
    }
}

/// <summary>
/// One entry in the divergence list of a hypothetical gate-report run.
/// Represents a tag whose classification differs between the enforced policy
/// (<see cref="GateConfigResolver.Current"/>) and the hypothetical policy derived from
/// caller-supplied prefix lists.
/// </summary>
/// <param name="Tag">Tag name.</param>
/// <param name="Enforced">
/// Disposition under the enforced policy: <c>"granted"</c> or
/// <c>"denied:&lt;Reason&gt;"</c> (e.g. <c>"denied:NotDesignated"</c>).
/// </param>
/// <param name="Hypothetical">
/// Disposition under the hypothetical policy: <c>"granted"</c> or
/// <c>"denied:&lt;Reason&gt;"</c>.
/// </param>
public record DivergenceEntry(
    [property: JsonPropertyName("tag")]          string Tag,
    [property: JsonPropertyName("enforced")]     string Enforced,
    [property: JsonPropertyName("hypothetical")] string Hypothetical);

/// <summary>
/// Record of deny entries omitted from a serialized <see cref="WriteGatePolicy"/>.
/// </summary>
/// <param name="Shown">Number of deny entries present in the <c>denies</c> array.</param>
/// <param name="Total">Total number of deny entries in the policy.</param>
public record DenyTruncation(
    [property: JsonPropertyName("shown")] int Shown,
    [property: JsonPropertyName("total")] int Total)
{
    /// <summary>Human-readable note explaining how to retrieve the omitted entries.</summary>
    [JsonPropertyName("note")]
    public string Note =>
        $"{Total - Shown} deny entr{(Total - Shown == 1 ? "y" : "ies")} omitted. " +
        $"denyCounts covers all {Total}. Narrow the report with logix_find_tags " +
        $"or raise the cap to see the rest.";
}

/// <summary>
/// Wire shape of a serialized write-gate policy.  Distinct from
/// <see cref="WriteGatePolicy"/> because the wire form carries the per-reason
/// tally, truncation record, <c>policy</c> label, and optional
/// <c>disclaimer</c>, which are presentation concerns rather than policy content.
///
/// <para>
/// <c>policy</c> is <c>"enforced"</c> when <see cref="GateReportHandler"/>
/// uses <see cref="GateConfigResolver.Current"/> (the same configuration that
/// <c>logix_write</c> applies), and <c>"hypothetical"</c> when caller-supplied
/// prefix lists produce a what-if classification.
/// </para>
///
/// <para>
/// <c>disclaimer</c> is present on <c>"hypothetical"</c> payloads only and
/// carries both a machine-readable label and a human-readable statement
/// explaining that the results do NOT represent the policy enforced by the
/// server.  It is <c>null</c> (omitted from JSON) for <c>"enforced"</c> payloads.
/// </para>
///
/// <para>
/// <c>source</c> is always <c>"project-file"</c>: the policy is derived solely
/// from the opened or uploaded project file on disk, not from the live
/// controller and not from any pending (unsaved) edit. For the live routine
/// content including pending edits, call <c>logix_export_rungs</c> instead.
/// </para>
/// </summary>
public record GateReportPayload(
    [property: JsonPropertyName("policy")]           string Policy,
    [property: JsonPropertyName("grants")]           IReadOnlyList<GrantEntry> Grants,
    [property: JsonPropertyName("denies")]           IReadOnlyList<DenyEntry>  Denies,
    [property: JsonPropertyName("denyCounts")]       IReadOnlyDictionary<string, int> DenyCounts,
    [property: JsonPropertyName("deniesTruncated")]  DenyTruncation? DeniesTruncated,
    [property: JsonPropertyName("disclaimer")]       string? Disclaimer,
    [property: JsonPropertyName("divergence")]       IReadOnlyList<DivergenceEntry>? Divergence,
    [property: JsonPropertyName("source")]           string Source = "project-file")
{
    /// <summary>
    /// The command prefixes the reported policy was derived with: the effective list, or
    /// <c>"*"</c> when every tag name is designated (<c>LGX_GATE_COMMAND_PREFIXES</c>).
    /// </summary>
    [JsonPropertyName("commandPrefixes")]
    public object? CommandPrefixes { get; init; }

    /// <summary>
    /// <c>"default"</c> or <c>"LGX_GATE_COMMAND_PREFIXES"</c>: where <see cref="CommandPrefixes"/> came from.
    /// <c>"request"</c> on a hypothetical report whose caller supplied <c>commandPrefixes</c>.
    /// </summary>
    [JsonPropertyName("commandPrefixesSource")]
    public string? CommandPrefixesSource { get; init; }

    /// <summary>
    /// <c>"project-file"</c>, <c>"acd-derived"</c> or <c>"controller-upload"</c>:
    /// what the reported policy was derived from. Omitted when unknown.
    /// </summary>
    [JsonPropertyName("policySource")]
    public string? PolicySource { get; init; }

    /// <summary>Path of the file the policy was derived from, when known.</summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    /// <summary>
    /// <c>true</c> when this is the policy instance the active session's
    /// <see cref="GatedTagService"/> enforces; <c>false</c> when it is only the
    /// open file's policy (no gated session) or a hypothetical run.
    /// </summary>
    [JsonPropertyName("enforcedBySession")]
    public bool? EnforcedBySession { get; init; }

    /// <summary>Explanatory note (partial export plus allowlist); omitted when none.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }

    /// <summary>
    /// Number of rungs or elements the gate could not parse (always present; <c>0</c> when all parsed).
    /// Tags depending on them are denied as <c>UnknownWriteContext</c>.
    /// </summary>
    [JsonPropertyName("unparsedContentCount")]
    public int UnparsedContentCount { get; init; }

    /// <summary>The first 10 unparsed entries (location, first 200 characters, reason); omitted when none.</summary>
    [JsonPropertyName("unparsedContent")]
    public IReadOnlyList<UnparsedContent>? UnparsedContent { get; init; }

    /// <summary>Number of ladder rungs among the unparsed content (always present; <c>0</c> when all parsed).</summary>
    [JsonPropertyName("unparsedRungsCount")]
    public int UnparsedRungsCount { get; init; }

    /// <summary>
    /// The first 10 rungs the gate could not parse, as program, routine, rung and error; omitted when none.
    /// Tags depending on them are denied as <c>UnknownWriteContext</c>; every other tag is classified as usual.
    /// </summary>
    [JsonPropertyName("unparsedRungs")]
    public IReadOnlyList<UnparsedRungEntry>? UnparsedRungs { get; init; }

    /// <summary>
    /// The active write allowlist (path, entry count, sha256); omitted when none is active.
    /// </summary>
    [JsonPropertyName("allowlist")]
    public AllowlistStatus? Allowlist { get; init; }
}

/// <summary>Allowlist summary embedded in the <c>logix_gate_report</c> payload.</summary>
/// <param name="Path">Path of the allowlist file.</param>
/// <param name="Entries">Number of entries (exact names plus prefix patterns).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file bytes.</param>
public record AllowlistStatus(
    [property: JsonPropertyName("path")]    string Path,
    [property: JsonPropertyName("entries")] int    Entries,
    [property: JsonPropertyName("sha256")]  string Sha256);

/// <summary>
/// Provenance of a <see cref="WriteGatePolicy"/>.
/// </summary>
/// <param name="Source"><c>"project-file"</c>, <c>"acd-derived"</c> or <c>"controller-upload"</c>.</param>
/// <param name="Path">Path of the source file (L5X or ACD), when known.</param>
/// <param name="EnforcedBySession">Whether an active session enforces this policy.</param>
public record GatePolicyOrigin(string Source, string? Path, bool EnforcedBySession)
{
    /// <summary>Source label for a policy derived from an opened L5X file.</summary>
    public const string ProjectFile = "project-file";

    /// <summary>Source label for a policy derived from an opened ACD file.</summary>
    public const string AcdDerived = "acd-derived";

    /// <summary>Source label for a policy derived from a project uploaded from the controller.</summary>
    public const string ControllerUpload = "controller-upload";

    /// <summary>
    /// Origin for a session policy derived from the open project file:
    /// <c>"acd-derived"</c> when the path is an ACD, else <c>"project-file"</c>.
    /// </summary>
    public static GatePolicyOrigin ForSessionFromFile(string? path) =>
        new(path is not null && path.EndsWith(".acd", StringComparison.OrdinalIgnoreCase)
                ? AcdDerived : ProjectFile,
            path, EnforcedBySession: true);
}

// ── GateEngine ───────────────────────────────────────────────────────────────

/// <summary>
/// Derives a <see cref="WriteGatePolicy"/> from a parsed L5X project and a
/// <see cref="GateConfig"/> by applying four derivation rules over every
/// controller-scoped and program-scoped tag.
///
/// <para>Rules (applied in priority order; the External Access rule sits after
/// program_written and safety_tag and before the command-prefix grant):</para>
/// <list type="number">
///   <item><description>
///     <b>program_written</b> — deny any tag whose base name appears as a write
///     operand in any RLL rung (detected via <see cref="WriteClassifier"/>).
///     This overrides rule 1 when both conditions match.
///   </description></item>
///   <item><description>
///     <b>safety_tag</b> — deny any tag whose name matches a
///     <see cref="GateConfig.SafetyPrefixes"/> entry.
///   </description></item>
///   <item><description>
///     <b>grant</b> — grant any remaining tag whose name matches a
///     <see cref="GateConfig.CommandPrefixes"/> entry, attaching per-member
///     numeric bounds from UDT member descriptions where available.
///   </description></item>
///   <item><description>
///     <b>not_designated</b> — deny everything else.
///   </description></item>
/// </list>
/// </summary>
public static class GateEngine
{
    private static readonly Regex RangePattern =
        new(@"range\s+(-?\d+(?:\.\d+)?)\s*\.\.\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Derives a <see cref="WriteGatePolicy"/> from the supplied L5X project
    /// and gate configuration by applying the four derivation rules to every
    /// controller-scoped and program-scoped tag.
    /// </summary>
    /// <param name="project">Parsed L5X project.</param>
    /// <param name="config">Gate configuration controlling prefix matching.</param>
    /// <returns>
    /// A <see cref="WriteGatePolicy"/> containing grant and deny entries with
    /// full provenance strings.
    /// </returns>
    public static WriteGatePolicy DerivePolicy(L5X project, GateConfig config)
    {
        // Collect all tags: controller scope then every program scope.
        // L5Sharp hands out fresh Tag wrappers on every enumeration, so tags are
        // identified by scope + name, never by reference.
        var allTags  = new List<Tag>();
        var tagKeys  = new List<string>();
        var programOf = new List<string?>();
        var tagIndex = new Dictionary<string, Tag>(StringComparer.Ordinal);
        var programOfKey = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var t in project.Tags)
        {
            allTags.Add(t);
            tagKeys.Add(TagKey("", t.Name));
            programOf.Add(null);
            tagIndex[tagKeys[^1]] = t;
            programOfKey[tagKeys[^1]] = null;
        }
        foreach (var program in project.Programs)
            foreach (var t in program.Tags)
            {
                allTags.Add(t);
                tagKeys.Add(TagKey(program.Name, t.Name));
                programOf.Add(program.Name);
                tagIndex[tagKeys[^1]] = t;
                programOfKey[tagKeys[^1]] = program.Name;
            }

        // Build UDT bounds lookup for grant provenance.
        var udtBounds = BuildUdtBoundsMap(project, out var udtUnappliable);

        // Rule 2 / 5 pre-computation: one pass over every ladder rung yields both
        // proven writes and operands of instructions the classifier cannot read.
        var scan           = WriteClassifier.ScanRungs(project, config.CommandPrefixes);
        var programWritten = scan.ProgramWritten;
        var unknownContext = scan.UnknownContext;

        // ── Alias resolution ──────────────────────────────────────────────────
        // An alias shares storage with its base tag, so a write through the alias
        // is a write to the base. Resolve every alias to its non-alias base and let
        // program-written / unknown-context status flow alias → base, so the base's
        // decision (and therefore every alias of it) accounts for rungs that write
        // through the alias.
        var resolved = new Dictionary<string, (Tag Alias, AliasResolution Res)>(StringComparer.Ordinal);
        for (var i = 0; i < allTags.Count; i++)
        {
            if (IsAlias(allTags[i]))
                resolved[tagKeys[i]] = (allTags[i], ResolveAlias(allTags[i], tagKeys[i], tagIndex));
        }

        programWritten = new Dictionary<string, string>(programWritten, StringComparer.OrdinalIgnoreCase);
        unknownContext = new Dictionary<string, string>(unknownContext, StringComparer.OrdinalIgnoreCase);
        PropagateAliasStatus(resolved, programOfKey, programWritten, unknownContext);

        // Non-alias tags first: aliases are judged against their base's decision.
        var decisions = new Dictionary<string, TagDecision>(StringComparer.Ordinal);
        for (var i = 0; i < allTags.Count; i++)
        {
            if (!resolved.ContainsKey(tagKeys[i]))
                decisions[tagKeys[i]] =
                    EvaluateTag(allTags[i], config, programWritten, unknownContext, udtBounds,
                        udtUnappliable, null, programOf[i]);
        }

        foreach (var (key, (alias, res)) in resolved)
        {
            var decision = EvaluateAlias(alias, res, config, programWritten, unknownContext,
                udtBounds, udtUnappliable, decisions, programOfKey[key]);
            decisions[key] = decision;
            if (decision.Deny is { } refused)
                AuditLogger.LogGateDecision(alias.Name, "denied", refused.Reason.ToString(),
                    refused.Provenance);
        }

        var grants = new List<GrantEntry>();
        var denies = new List<DenyEntry>();

        // Program parameters are tags: a real export declares them as <Tag Usage="..."> in the
        // program's <Tags>, so they are in the program's tag collection and evaluated below like
        // any other tag. This loop covers program-scoped keys with no declared tag in that
        // collection (e.g. a \Prog.Tag reference or a parameter connection naming a tag the
        // program does not declare): report each as a program-scoped deny so the policy names
        // it. The gate refuses it anyway (it is never granted); this makes the reason visible.
        var declared = new HashSet<string>(
            Enumerable.Range(0, allTags.Count).Where(i => programOf[i] is not null)
                .Select(i => ProgramTagName(programOf[i]!, allTags[i].Name)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (scoped, location) in programWritten)
        {
            if (!scoped.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) ||
                declared.Contains(scoped)) continue;
            var dot = scoped.IndexOf('.');
            if (dot < 0) continue;
            denies.Add(new DenyEntry(scoped[(dot + 1)..], DenyReason.ProgramWritten,
                $"Parameter '{scoped}' is written by program logic: {location}.")
            {
                Program = scoped["Program:".Length..dot],
            });
        }

        for (var i = 0; i < allTags.Count; i++)
        {
            var d = decisions[tagKeys[i]];
            // Entries carry their owning program so the policy is keyed by scope:
            // a controller tag by bare name, a program tag as 'Program:<P>.<Tag>'.
            // A program-local tag can never grant, deny or widen a controller tag
            // of the same name.
            var owner = programOf[i];
            if (d.Grant is not null) grants.Add(d.Grant with { Program = owner });
            if (d.Deny  is not null) denies.Add(d.Deny  with { Program = owner });
        }

        // The partial-export config (a single empty prefix that is not '*') has its own meaning
        // and is not reported as the command prefixes.
        var partialMeaning = !config.AllNamesDesignated && config.CommandPrefixes.Any(p => p.Length == 0);
        return new WriteGatePolicy(grants, denies)
        {
            CommandPrefixesWire   = partialMeaning ? null : config.CommandPrefixesWire,
            CommandPrefixesSource = partialMeaning ? null : config.CommandPrefixesSource,
            Unparsed              = scan.Unparsed,
        };
    }

    // ── Scoped alias propagation ──────────────────────────────────────────────

    /// <summary>
    /// The policy keys a tag answers to: its bare name and, for a program-scoped tag, its
    /// <c>Program:&lt;P&gt;.&lt;Name&gt;</c> key. Rung scans record writes under either form.
    /// </summary>
    private static string[] PolicyKeys(string? program, string name) =>
        program is null ? [name] : [name, ProgramTagName(program, name)];

    /// <summary>
    /// Passes program-written and unknown-write-context status from every alias to its base.
    /// An alias is looked up under both its bare and scoped keys; the base (resolved in the
    /// alias's scope first, then controller scope) is marked under both of its keys. An alias
    /// whose base cannot be resolved passes its status to the literal <c>AliasFor</c> target
    /// (<c>Tag</c>, <c>\Prog.Tag</c> or <c>Program:Prog.Tag</c>) and is itself marked unknown.
    /// Repeats until stable so a literal target that is itself an alias is covered.
    /// </summary>
    private static void PropagateAliasStatus(
        Dictionary<string, (Tag Alias, AliasResolution Res)> resolved,
        Dictionary<string, string?> programOfKey,
        Dictionary<string, string> programWritten,
        Dictionary<string, string> unknownContext)
    {
        static bool Find(Dictionary<string, string> map, string[] keys, out string location)
        {
            foreach (var k in keys)
                if (map.TryGetValue(k, out location!)) return true;
            location = string.Empty;
            return false;
        }

        static bool Mark(Dictionary<string, string> map, IEnumerable<string> keys, string location)
        {
            var changed = false;
            foreach (var k in keys) changed |= map.TryAdd(k, location);
            return changed;
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var (aliasKey, (alias, res)) in resolved)
            {
                var aliasProgram = programOfKey[aliasKey];
                var aliasKeys = PolicyKeys(aliasProgram, alias.Name);

                string[] targetKeys;
                string via;
                if (res.Base is not null)
                {
                    targetKeys = PolicyKeys(programOfKey[res.BaseKey!], res.Base.Name);
                    via = $"(through alias '{alias.Name}' of '{res.Base.Name}')";
                }
                else
                {
                    var literal = WriteClassifier.ExtractScopedName(res.Target);
                    if (string.IsNullOrEmpty(literal)) literal = null;
                    targetKeys = literal is null ? []
                        : literal.StartsWith("Program:", StringComparison.OrdinalIgnoreCase) || aliasProgram is null
                            ? [literal]
                            : [literal, ProgramTagName(aliasProgram, literal)];
                    via = $"(through unresolvable alias '{alias.Name}' of '{res.Target}')";
                }

                if (Find(programWritten, aliasKeys, out var wLoc))
                    changed |= Mark(programWritten, targetKeys, $"{wLoc} {via}");
                if (Find(unknownContext, aliasKeys, out var uLoc))
                    changed |= Mark(unknownContext, targetKeys, $"{uLoc} {via}");
            }
        } while (changed);

        // An alias whose base cannot be resolved cannot be proven safe: it is itself unknown.
        // Marked after propagation so this self-mark is not passed on to the literal target.
        foreach (var (aliasKey, (alias, res)) in resolved)
        {
            if (res.Base is not null) continue;
            Mark(unknownContext, PolicyKeys(programOfKey[aliasKey], alias.Name),
                $"alias '{alias.Name}' of '{res.Target}' cannot be resolved to a base tag: {res.Failure}");
        }
    }

    // ── Per-tag evaluation ────────────────────────────────────────────────────

    /// <summary>Outcome for one tag: exactly one of <see cref="Grant"/> / <see cref="Deny"/> is set.</summary>
    private sealed record TagDecision(GrantEntry? Grant, DenyEntry? Deny);

    /// <summary>Bounds taken from an alias's base tag in place of the alias's own declaration.</summary>
    private sealed record BoundsOverride(
        ScalarBounds? TagBounds, IReadOnlyList<MemberBounds>? Bounds, string Note);

    /// <summary>
    /// Opening clause of a grant or deny explanation. An empty command prefix only occurs when a
    /// partial export is gated by <c>LGX_WRITE_ALLOWLIST</c> (every tag is a candidate and the
    /// allowlist, not a name prefix, grants writes), so the text names the allowlist instead of
    /// quoting an empty prefix.
    /// </summary>
    private static string CandidateClause(string tagName, string matchedPrefix, GateConfig config) =>
        config.AllNamesDesignated
            ? $"Tag '{tagName}' is designated by {GateConfigResolver.EnvVar}=* (every tag name; the naming rule only)"
            : matchedPrefix.Length == 0
            ? $"Tag '{tagName}' is a write candidate in a partial export (the write allowlist, " +
              "not a name prefix, grants writes)"
            : $"Tag '{tagName}' matches command prefix '{matchedPrefix}'";

    /// <summary>
    /// Applies the derivation rules (program-written, safety, External Access,
    /// command prefix, unknown context, not-designated) to one tag.
    /// </summary>
    private static TagDecision EvaluateTag(
        Tag tag,
        GateConfig config,
        IReadOnlyDictionary<string, string> programWritten,
        IReadOnlyDictionary<string, string> unknownContext,
        Dictionary<string, List<MemberBounds>> udtBounds,
        HashSet<string> udtUnappliable,
        BoundsOverride? boundsOverride,
        string? program = null)
    {
        // External Access is reported on every grant and deny entry.
        var externalAccess = GetExternalAccess(tag);

        static TagDecision Deny(Tag t, DenyReason reason, string provenance, string ea) =>
            new(null, new DenyEntry(t.Name, reason, provenance, ea));

        // ── Rule 2: program_written (highest priority, overrides any grant) ─
        // A program-scoped tag is also judged by its scoped key ('Program:P.Tag'), which is
        // how cross-program references and program parameters record their writes.
        var scopedKey = program is null ? null : ProgramTagName(program, tag.Name);
        string? writeLocation = null;
        if (programWritten.TryGetValue(tag.Name, out writeLocation) ||
            (scopedKey is not null && programWritten.TryGetValue(scopedKey, out writeLocation)))
            return Deny(tag, DenyReason.ProgramWritten,
                $"Tag '{tag.Name}' is written by program logic at {writeLocation}.",
                externalAccess);

        // ── Rule 3: safety_tag ────────────────────────────────────────────
        var matchedSafetyPrefix = config.SafetyPrefixes
            .FirstOrDefault(p => tag.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        if (matchedSafetyPrefix is not null)
            return Deny(tag, DenyReason.SafetyTag,
                $"Tag '{tag.Name}' matches safety prefix '{matchedSafetyPrefix}'.",
                externalAccess);

        // A tag declared Class="Safety" is a safety tag whatever it is named.
        if (IsSafetyClass(tag))
            return Deny(tag, DenyReason.SafetyTag,
                $"Tag '{tag.Name}' is declared Class=\"Safety\".",
                externalAccess);

        // ── Consumed tag: the value comes from another controller ─────────
        // A consumed tag is overwritten by the producing controller, so a write
        // here is not a value this controller holds. Denied whatever its name.
        if (IsConsumed(tag))
            return Deny(tag, DenyReason.UnknownWriteContext,
                $"Tag '{tag.Name}' is a consumed tag: value is produced by another controller.",
                externalAccess);

        // ── Rule 6: external_access_not_read_write ────────────────────────
        // External Access is the engineer-owned write grant. Applies to every
        // backend (cip, sdk, virtual, l5x) because the policy is derived here.
        // A missing attribute is Read/Write: that is the L5X schema default
        // and the Logix Designer default for a new tag.
        if (!externalAccess.Equals(ReadWriteAccess, StringComparison.OrdinalIgnoreCase))
            return Deny(tag, DenyReason.ExternalAccessNotReadWrite,
                $"Tag '{tag.Name}' has External Access '{externalAccess}'; " +
                "an engineer grants writes by setting it to Read/Write in Logix Designer",
                externalAccess);

        // ── Rule 1: grant CommandPrefix ───────────────────────────────────
        var matchedCommandPrefix = config.CommandPrefixes
            .FirstOrDefault(p => tag.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (matchedCommandPrefix is null)
            // ── Rule 4: not_designated ────────────────────────────────────
            return Deny(tag, DenyReason.NotDesignated,
                $"Tag '{tag.Name}' does not match any command prefix " +
                $"({string.Join(", ", config.CommandPrefixes.Select(p => $"'{p}'"))}" +
                (config.CommandPrefixesSource == GateConfigResolver.SourceDefault
                    ? "" : $"; set by {GateConfigResolver.EnvVar}") + ") " +
                $"and is not safety-tagged or program-written.",
                externalAccess);

        // ── Rule 5a: firmware-written types ────────────────────────────────
        // The controller firmware owns the members of these types (axis status, phase state,
        // SFC step flags, MSG status, ...). A command prefix does not make a write to them
        // meaningful or safe, so a Cmd_/Sim_ tag of such a type is never granted.
        // The DataType attribute is read from the element: Tag.DataType deserialises the value
        // and throws for structure types it has no atomic reader for.
        var declaredType = tag.Serialize().Attribute("DataType")?.Value;
        if (IsFirmwareWrittenType(declaredType))
            return Deny(tag, DenyReason.UnknownWriteContext,
                $"{CandidateClause(tag.Name, matchedCommandPrefix, config)} but its " +
                $"data type '{declaredType}': type is written by controller firmware.",
                externalAccess);

        // ── Rule 5: fail closed on unprovable write context ────────────────
        // The tag is designated a command tag by name, but an instruction
        // we cannot classify also touches it. Grant only what the project
        // provably designates — a name convention is not a proof.
        string? unknownLocation = null;
        if (unknownContext.TryGetValue(tag.Name, out unknownLocation) ||
            (scopedKey is not null && unknownContext.TryGetValue(scopedKey, out unknownLocation)))
            return Deny(tag, DenyReason.UnknownWriteContext,
                $"{CandidateClause(tag.Name, matchedCommandPrefix, config)} but also appears at " +
                $"{unknownLocation}, whose write operands cannot be " +
                $"determined. Denied because the engine cannot prove " +
                $"the tag is not driven by program logic.",
                externalAccess);

        ScalarBounds? tagBounds = null;
        IReadOnlyList<MemberBounds>? bounds = null;
        string boundsNote = string.Empty;

        if (boundsOverride is not null)
        {
            tagBounds  = boundsOverride.TagBounds;
            bounds     = boundsOverride.Bounds;
            boundsNote = boundsOverride.Note;
        }
        else
        {
            // The data type comes from the tag's DataType attribute (declaredType above), not
            // from the decorated data: a tag carrying only L5K data has no decorated value to
            // read it from, and its range annotation must not be dropped for that reason.
            // Descriptions are read from the tag's own XML (GateDescriptions), never through
            // Tag.Description, which resolves definition comments via L5Sharp's whole-project
            // index and throws on content that index cannot tokenise. Scalar types that carry a
            // tag-level range also accept a whole-tag comment; other types use the Description element only.
            var scalarNumeric = declaredType is not null && IsScalarNumericType(declaredType);
            var description = scalarNumeric
                ? GateDescriptions.TagDescription(tag)
                : tag.Serialize().Element("Description")?.Value;
            var rangeState = ParseRange(description, out var tMin, out var tMax);

            // A range annotation that cannot be applied is never granted unbounded.
            if (rangeState == RangeState.Invalid ||
                (rangeState == RangeState.Valid && !scalarNumeric) ||
                (declaredType is not null && udtUnappliable.Contains(declaredType)))
                return Deny(tag, DenyReason.UnknownWriteContext,
                    $"{CandidateClause(tag.Name, matchedCommandPrefix, config)} but its " +
                    $"range annotation could not be applied (data type '{declaredType}'). " +
                    "Denied because a grant must not silently drop a declared range.",
                    externalAccess);

            if (rangeState == RangeState.Valid)
            {
                tagBounds  = new ScalarBounds(tMin, tMax);
                boundsNote = $" Scalar bounds from tag description: [{tMin}..{tMax}].";
            }

            // UDT-typed: collect per-member bounds from UDT definition.
            if (declaredType is not null &&
                udtBounds.TryGetValue(declaredType, out var memberBoundsList)
                && memberBoundsList.Count > 0)
            {
                bounds     = memberBoundsList;
                boundsNote = $" Bounds from UDT '{declaredType}': " +
                    string.Join(", ",
                        memberBoundsList.Select(b => $"{b.Member} [{b.Min}..{b.Max}]")) + ".";
            }
        }

        return new TagDecision(new GrantEntry(
            Tag:        tag.Name,
            TagBounds:  tagBounds,
            Bounds:     bounds,
            Provenance: $"{CandidateClause(tag.Name, matchedCommandPrefix, config)}.{boundsNote}",
            ExternalAccess: externalAccess), null);
    }

    // ── Alias handling ────────────────────────────────────────────────────────

    /// <summary>Result of following an alias chain.</summary>
    /// <param name="Base">The non-alias base tag, or <c>null</c> when it cannot be resolved.</param>
    /// <param name="Path">Parsed remainder of the final <c>AliasFor</c> (members, indices, bit).</param>
    /// <param name="Target">The <c>AliasFor</c> text of the alias itself, for provenance.</param>
    /// <param name="Failure">Why resolution failed (module I/O, missing tag, cycle); <c>null</c> on success.</param>
    private sealed record AliasResolution(
        Tag? Base, string? BaseKey, AliasPath Path, string Target, string? Failure);

    /// <summary>
    /// The part of an alias target after the base tag name.
    /// </summary>
    /// <param name="BaseIndexed">The base tag name itself carries an array index.</param>
    /// <param name="Names">Member names, indices and bit selector removed.</param>
    /// <param name="LastIndexed">The last member name carries an array index.</param>
    /// <param name="Bit">A trailing bit selector is present.</param>
    private sealed record AliasPath(
        bool BaseIndexed, IReadOnlyList<string> Names, bool LastIndexed, bool Bit)
    {
        public static readonly AliasPath Empty = new(false, [], false, false);
        public bool IsWhole => !BaseIndexed && Names.Count == 0 && !LastIndexed && !Bit;
    }

    private static bool IsAlias(Tag tag) =>
        string.Equals(tag.Serialize().Attribute("TagType")?.Value, "Alias",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The policy key of a program-scoped tag: <c>Program:&lt;P&gt;.&lt;Tag&gt;</c>, the
    /// form a write to that tag uses. Controller tags are keyed by their bare name.
    /// </summary>
    /// <param name="program">Owning program name.</param>
    /// <param name="tag">Tag name within the program.</param>
    /// <returns>The scope-qualified policy key.</returns>
    public static string ProgramTagName(string program, string tag) => $"Program:{program}.{tag}";

    /// <summary>Identity of a tag: owning program name (empty for controller scope) plus name.</summary>
    private static string TagKey(string scope, string name) =>
        $"{scope.ToLowerInvariant()}\u0001{name.ToLowerInvariant()}";

    /// <summary>
    /// Follows <c>AliasFor</c> from <paramref name="alias"/> through any number of
    /// intermediate aliases to the non-alias base tag. Names resolve in the alias's
    /// own program scope first, then controller scope. Module I/O targets
    /// (<c>Local:1:O.Data</c>), missing tags and cycles yield an unresolved result.
    /// </summary>
    private static AliasResolution ResolveAlias(
        Tag alias, string aliasKey, Dictionary<string, Tag> tagIndex)
    {
        var target = (alias.Serialize().Attribute("AliasFor")?.Value ?? string.Empty).Trim();
        var seen   = new HashSet<string>(StringComparer.Ordinal) { aliasKey };
        var currentKey    = aliasKey;
        var currentTarget = target;
        var path  = AliasPath.Empty;
        var first = true;

        AliasResolution Fail(string why) => new(null, null, AliasPath.Empty, target, why);

        while (true)
        {
            if (!TryParseAliasTarget(currentTarget, out var baseName, out var parsed))
                return Fail($"AliasFor '{currentTarget}' cannot be parsed");

            if (baseName.Contains(':'))
                return Fail($"'{currentTarget}' is module I/O, whose write context cannot be determined");

            // Program scope first (the current tag's own program), then controller scope.
            var scope = currentKey[..currentKey.IndexOf('\u0001')];
            string nextKey = TagKey(scope, baseName);
            if (scope.Length == 0 || !tagIndex.ContainsKey(nextKey))
                nextKey = TagKey("", baseName);
            if (!tagIndex.TryGetValue(nextKey, out var next))
                return Fail($"base tag '{baseName}' (from AliasFor '{currentTarget}') does not exist");

            // Compose paths: the alias's own suffix applies beneath the intermediate's.
            path = first ? parsed : Combine(parsed, path);
            first = false;

            if (!seen.Add(nextKey))
                return Fail("the alias chain is circular");

            if (!IsAlias(next))
                return new AliasResolution(next, nextKey, path, target, null);

            currentKey    = nextKey;
            currentTarget = (next.Serialize().Attribute("AliasFor")?.Value ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// Combines the path of an intermediate alias's own target (<paramref name="inner"/>)
    /// with the path already accumulated beneath it (<paramref name="outer"/>).
    /// </summary>
    private static AliasPath Combine(AliasPath inner, AliasPath outer)
    {
        if (outer.IsWhole) return inner;
        var names = inner.Names.Concat(outer.Names).ToList();
        return new AliasPath(
            inner.BaseIndexed || (inner.Names.Count == 0 && outer.BaseIndexed),
            names,
            outer.Names.Count > 0 ? outer.LastIndexed : (inner.LastIndexed || outer.BaseIndexed),
            outer.Bit || inner.Bit);
    }

    /// <summary>
    /// Splits an <c>AliasFor</c> value such as <c>Cmd_Motor.Speeds[2].31</c> into the
    /// base tag name and the member path beneath it. Returns <c>false</c> when malformed.
    /// </summary>
    private static bool TryParseAliasTarget(string text, out string baseName, out AliasPath path)
    {
        baseName = string.Empty;
        path     = AliasPath.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var segments = new List<(string Name, bool Indexed)>();
        var pos = 0;
        while (true)
        {
            var start = pos;
            while (pos < text.Length && text[pos] != '.' && text[pos] != '[') pos++;
            var name = text[start..pos];
            if (name.Length == 0) return false;

            var indexed = false;
            while (pos < text.Length && text[pos] == '[')
            {
                var depth = 0;
                for (; pos < text.Length; pos++)
                {
                    if (text[pos] == '[') depth++;
                    else if (text[pos] == ']' && --depth == 0) break;
                }
                if (pos >= text.Length) return false;
                pos++;
                indexed = true;
            }
            segments.Add((name, indexed));
            if (pos >= text.Length) break;
            if (text[pos] != '.') return false;
            pos++;
        }

        baseName = segments[0].Name;
        var rest = segments.Skip(1).ToList();
        var bit  = rest.Count > 0 && !rest[^1].Indexed && rest[^1].Name.All(char.IsAsciiDigit);
        if (bit) rest.RemoveAt(rest.Count - 1);
        // A numeric segment anywhere but last is malformed.
        if (rest.Any(r => r.Name.All(char.IsAsciiDigit))) return false;

        path = new AliasPath(
            segments[0].Indexed,
            rest.Select(r => r.Name).ToList(),
            rest.Count > 0 && rest[^1].Indexed,
            bit);
        return true;
    }

    private static int AccessRank(string access) =>
        access.Equals(ReadWriteAccess, StringComparison.OrdinalIgnoreCase) ? 0
        : access.Equals("Read Only", StringComparison.OrdinalIgnoreCase)  ? 1
        : 2;

    /// <summary>Returns the more restrictive of two External Access values.</summary>
    private static string StricterAccess(string a, string b) => AccessRank(b) > AccessRank(a) ? b : a;

    /// <summary>
    /// Decides an alias tag: denied when its base cannot be resolved or is itself
    /// denied (for any reason); otherwise judged by the ordinary rules on its own
    /// name and External Access, with bounds and the External Access ceiling taken
    /// from the base.
    /// </summary>
    private static TagDecision EvaluateAlias(
        Tag alias,
        AliasResolution res,
        GateConfig config,
        IReadOnlyDictionary<string, string> programWritten,
        IReadOnlyDictionary<string, string> unknownContext,
        Dictionary<string, List<MemberBounds>> udtBounds,
        HashSet<string> udtUnappliable,
        Dictionary<string, TagDecision> decisions,
        string? program = null)
    {
        var aliasAccess = GetExternalAccess(alias);

        if (res.Base is null)
            return new TagDecision(null, new DenyEntry(alias.Name, DenyReason.UnknownWriteContext,
                $"Tag '{alias.Name}' is an alias of '{res.Target}', which cannot be resolved to a " +
                $"base tag: {res.Failure}. Denied because the engine cannot prove the tag is not " +
                "driven by program logic.", aliasAccess));

        var baseTag      = res.Base;
        var baseDecision = decisions[res.BaseKey!];
        var baseAccess   = GetExternalAccess(baseTag);
        var access       = StricterAccess(aliasAccess, baseAccess);

        if (baseDecision.Deny is { } baseDeny)
            return new TagDecision(null, new DenyEntry(alias.Name, baseDeny.Reason,
                $"Tag '{alias.Name}' is an alias of '{res.Target}' (base tag '{baseTag.Name}'). " +
                $"Base denied: {baseDeny.Provenance}", access));

        var baseGrant = baseDecision.Grant!;
        var derived   = DeriveAliasBounds(res.Path, baseGrant, out var refusal);
        if (refusal is not null)
            return new TagDecision(null, new DenyEntry(alias.Name, DenyReason.UnknownWriteContext,
                $"Tag '{alias.Name}' is an alias of '{res.Target}' (base tag '{baseTag.Name}'). " +
                refusal, access));

        var own = EvaluateTag(alias, config, programWritten, unknownContext, udtBounds,
            udtUnappliable, derived, program);
        if (own.Deny is { } ownDeny)
            return new TagDecision(null, ownDeny with { ExternalAccess = access });

        return new TagDecision(
            own.Grant! with
            {
                Provenance     = own.Grant.Provenance +
                                 $" Alias of '{res.Target}' (base tag '{baseTag.Name}', granted).",
                ExternalAccess = access,
            }, null);
    }

    /// <summary>
    /// Works out the bounds an alias inherits from its base grant. A bit or element
    /// alias beneath a range-checked value is refused (<paramref name="refusal"/>),
    /// because such writes change the value without the range being compared.
    /// </summary>
    private static BoundsOverride DeriveAliasBounds(
        AliasPath path, GrantEntry baseGrant, out string? refusal)
    {
        refusal = null;
        const string Why = "Bit and element writes to a range-checked value are refused, so an alias of one is denied; alias the whole value.";

        if (path.IsWhole)
            return new BoundsOverride(baseGrant.TagBounds, baseGrant.Bounds,
                BoundsNote(baseGrant.TagBounds, baseGrant.Bounds));

        if (baseGrant.TagBounds is { } scalar)
        {
            refusal = $"Base is range-checked [{scalar.Min}..{scalar.Max}]. {Why}";
            return new BoundsOverride(null, null, string.Empty);
        }

        var members = baseGrant.Bounds ?? [];
        var normalized = string.Join('.', path.Names);

        if (path.Names.Count == 0)
        {
            // Element or bit of the base itself.
            if (path.Bit && members.Count > 0)
            {
                refusal = "Base has range-checked members; a bit alias of it cannot be bounded.";
                return new BoundsOverride(null, null, string.Empty);
            }
            return new BoundsOverride(null, members.Count > 0 ? members : null,
                BoundsNote(null, members));
        }

        var exact = members.FirstOrDefault(b =>
            string.Equals(b.Member, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (path.Bit || path.LastIndexed)
            {
                refusal = $"Member '{exact.Member}' is range-checked [{exact.Min}..{exact.Max}]. {Why}";
                return new BoundsOverride(null, null, string.Empty);
            }
            var sb = new ScalarBounds(exact.Min, exact.Max);
            return new BoundsOverride(sb, null,
                $" Scalar bounds from base member '{exact.Member}': [{exact.Min}..{exact.Max}].");
        }

        var ancestor = members.FirstOrDefault(b =>
            normalized.StartsWith(b.Member + ".", StringComparison.OrdinalIgnoreCase));
        if (ancestor is not null)
        {
            refusal = $"Member '{ancestor.Member}' is range-checked [{ancestor.Min}..{ancestor.Max}]. {Why}";
            return new BoundsOverride(null, null, string.Empty);
        }

        var prefix = normalized + ".";
        var below = members
            .Where(b => b.Member.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(b => new MemberBounds(b.Member[prefix.Length..], b.Min, b.Max))
            .ToList();
        return new BoundsOverride(null, below.Count > 0 ? below : null, BoundsNote(null, below));
    }

    private static string BoundsNote(ScalarBounds? scalar, IReadOnlyList<MemberBounds>? members)
    {
        if (scalar is not null)
            return $" Scalar bounds from base tag: [{scalar.Min}..{scalar.Max}].";
        return members is { Count: > 0 }
            ? " Bounds from base tag: " +
              string.Join(", ", members.Select(b => $"{b.Member} [{b.Min}..{b.Max}]")) + "."
            : string.Empty;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string ReadWriteAccess = "Read/Write";

    /// <summary>Built-in data types whose members the controller firmware writes.</summary>
    private static readonly HashSet<string> FirmwareWrittenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PHASE", "MOTION_GROUP", "SFC_STEP", "SFC_ACTION", "MESSAGE", "COORDINATE_SYSTEM",
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="dataType"/> is a built-in type whose members the
    /// controller firmware writes: <c>PHASE</c>, any <c>AXIS_*</c>, <c>MOTION_GROUP</c>,
    /// <c>SFC_STEP</c>, <c>SFC_ACTION</c>, <c>MESSAGE</c> or <c>COORDINATE_SYSTEM</c>.
    /// </summary>
    /// <param name="dataType">Tag data type name (may be <c>null</c> or empty).</param>
    internal static bool IsFirmwareWrittenType(string? dataType)
    {
        var t = dataType?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        return FirmwareWrittenTypes.Contains(t) ||
               t.StartsWith("AXIS_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> when the tag is consumed from another controller:
    /// <c>TagType="Consumed"</c> or a <c>ConsumeInfo</c> element.
    /// </summary>
    private static bool IsConsumed(Tag tag)
    {
        var xml = tag.Serialize();
        return string.Equals(xml.Attribute("TagType")?.Value?.Trim(), "Consumed",
                   StringComparison.OrdinalIgnoreCase)
            || xml.Element("ConsumeInfo") is not null;
    }

    /// <summary>
    /// Returns <c>true</c> when the tag's <c>Class</c> attribute is <c>Safety</c>
    /// (case-insensitive). A missing attribute means <c>Standard</c>.
    /// </summary>
    private static bool IsSafetyClass(Tag tag) =>
        string.Equals(tag.Serialize().Attribute("Class")?.Value?.Trim(), "Safety",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the tag's declared <c>ExternalAccess</c> attribute value, or
    /// <c>"Read/Write"</c> when the attribute is absent (the L5X and Logix
    /// Designer default).
    /// </summary>
    private static string GetExternalAccess(Tag tag)
    {
        var declared = tag.Serialize().Attribute("ExternalAccess")?.Value;
        return string.IsNullOrWhiteSpace(declared) ? ReadWriteAccess : declared;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="dataType"/> is a scalar numeric
    /// type in the REAL/INT family (SINT, INT, DINT, LINT, REAL, LREAL).
    /// These are the only atomic types that can carry a meaningful <c>range</c>
    /// annotation in their tag description.
    /// </summary>
    private static bool IsScalarNumericType(string dataType) =>
        dataType.Equals("SINT",  StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("INT",   StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("DINT",  StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("LINT",  StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("REAL",  StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("LREAL", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a map of UDT-name to per-member bounds by scanning all user-defined
    /// data types in the project for member descriptions that contain a
    /// <c>range N..M</c> annotation.
    /// </summary>
    private static Dictionary<string, List<MemberBounds>> BuildUdtBoundsMap(
        L5X project, out HashSet<string> unappliable)
    {
        var result = new Dictionary<string, List<MemberBounds>>(StringComparer.OrdinalIgnoreCase);
        unappliable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dt in project.DataTypes)
        {
            var memberBounds = new List<MemberBounds>();
            var bad = false;
            CollectMemberBounds(project, dt, prefix: "", memberBounds,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dt.Name }, ref bad);

            if (memberBounds.Count > 0)
                result[dt.Name] = memberBounds;
            if (bad)
                unappliable.Add(dt.Name);
        }

        return result;
    }

    private enum RangeState { None, Valid, Invalid }

    /// <summary>Loose test for a description that intends a range: the word "range" then a number.</summary>
    private static readonly Regex RangeIntentPattern =
        new(@"\brange\b\s*[:=]?\s*[-+]?\.?\d", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies a description's range annotation. <see cref="RangeState.Invalid"/> means the
    /// description declares a range (the word "range" followed by a number) that is not a
    /// well-formed <c>range N..M</c> with N &lt;= M, so it cannot be enforced.
    /// </summary>
    private static RangeState ParseRange(string? description, out double min, out double max)
    {
        min = max = 0;
        if (string.IsNullOrEmpty(description) || !RangeIntentPattern.IsMatch(description))
            return RangeState.None;

        var m = RangePattern.Match(description);
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out min) &&
            double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out max) &&
            min <= max)
            return RangeState.Valid;

        return RangeState.Invalid;
    }

    /// <summary>
    /// Collects <c>range N..M</c> bounds for the members of <paramref name="dt"/>, descending
    /// into members whose type is another project data type. Nested members are recorded under
    /// their dotted path (<c>Sub.Speed</c>). <paramref name="visiting"/> guards against cyclic
    /// type definitions.
    /// </summary>
    private static void CollectMemberBounds(L5X project, DataType dt, string prefix,
        List<MemberBounds> into, HashSet<string> visiting, ref bool unappliable)
    {
        foreach (var member in dt.Serialize().Element("Members")?.Elements("Member") ?? Enumerable.Empty<XElement>())
        {
            var memberName = member.Attribute("Name")?.Value ?? string.Empty;
            var path = prefix.Length == 0 ? memberName : $"{prefix}.{memberName}";

            switch (ParseRange(GateDescriptions.MemberDescription(member), out var min, out var max))
            {
                case RangeState.Valid:
                    into.Add(new MemberBounds(path, min, max));
                    break;
                case RangeState.Invalid:
                    unappliable = true;
                    break;
            }

            string memberType = member.Attribute("DataType")?.Value ?? string.Empty;
            var nested = project.DataTypes.FirstOrDefault(d =>
                string.Equals(d.Name, memberType, StringComparison.OrdinalIgnoreCase));
            if (nested is not null && visiting.Add(nested.Name))
            {
                CollectMemberBounds(project, nested, path, into, visiting, ref unappliable);
                visiting.Remove(nested.Name);
            }
        }
    }
}
