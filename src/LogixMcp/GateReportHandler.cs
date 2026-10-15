using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Strongly-typed error response returned by <see cref="GateReportHandler.Execute"/>
/// when no project is loaded.
/// </summary>
/// <param name="Error">Human-readable error message.</param>
public record GateReportError(
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
/// Wrapper returned by <see cref="GateReportHandler.Execute"/> when
/// caller-supplied prefix lists trigger a hypothetical (what-if) run.
///
/// <para>
/// Carries the derived <see cref="WriteGatePolicy"/>, the <see cref="GateConfig"/>
/// that was used, and a <see cref="Divergence"/> list that names every tag whose
/// classification differs between the enforced policy (<see cref="GateConfigResolver.Current"/>)
/// and this hypothetical policy.  Both dispositions are shown for each divergent tag so
/// callers can evaluate what would change under the new naming convention.
/// </para>
/// </summary>
/// <param name="Policy">The gate policy derived from the custom config.</param>
/// <param name="Config">The custom gate configuration used for this run.</param>
/// <param name="Divergence">
/// Tags whose classification differs between the enforced and hypothetical policies,
/// sorted alphabetically.  An empty list means the two policies are equivalent.
/// </param>
/// <param name="Reason">
/// Set when the report is hypothetical because the open project is a partial export
/// (no write gate can be derived from it) or the active session enforces no gate; it is
/// shown ahead of the disclaimer.
/// </param>
/// <param name="CustomPrefixes">
/// <c>true</c> (default) when the caller supplied prefix lists, so the disclaimer describes
/// custom prefix rules. <c>false</c> when the report is hypothetical only because no gate is
/// enforced; the disclaimer then names the reason alone.
/// </param>
public record HypotheticalGateResult(
    WriteGatePolicy Policy,
    GateConfig Config,
    IReadOnlyList<DivergenceEntry> Divergence,
    string? Reason = null,
    bool CustomPrefixes = true);

/// <summary>
/// Pure handler for the <c>logix_gate_report</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
///
/// <para>
/// The handler derives a fresh <see cref="WriteGatePolicy"/> on every invocation
/// from the live project state — it is intentionally never cached, so results
/// always reflect the project that is currently open via
/// <c>logix_open_project</c>.
/// </para>
///
/// <para>
/// When no custom prefixes are supplied the handler uses
/// <see cref="GateConfigResolver.Current"/> — the same configuration that
/// <c>logix_write</c> enforces — and the result carries
/// <c>"policy": "enforced"</c>.
/// </para>
///
/// <para>
/// When caller-supplied <c>commandPrefixes</c> and/or <c>safetyPrefixes</c> are
/// provided, the handler runs the engine with a custom <see cref="GateConfig"/> and
/// returns a <see cref="HypotheticalGateResult"/>.  <see cref="Serialize"/> then
/// emits <c>"policy": "hypothetical"</c> together with an explicit disclaimer so
/// the result can never be mistaken for an authoritative classification.
/// </para>
/// </summary>
public static class GateReportHandler
{
    /// <summary>Reason when the active session has no write gate and no more specific cause is recorded.</summary>
    internal const string NoSessionGateReason =
        "the active session enforces no write gate; call logix_connect to derive one from the open project";

    // Shared serialiser options used for the error case: compact, null-safe.
    private static readonly JsonSerializerOptions _errorOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    /// <summary>
    /// Derives a <see cref="WriteGatePolicy"/> (enforced path) or a
    /// <see cref="HypotheticalGateResult"/> (hypothetical path) from the
    /// currently open L5X project.  Never throws; returns a
    /// <see cref="GateReportError"/> when no project is open.
    /// </summary>
    /// <param name="service">The project singleton holding the current project.</param>
    /// <param name="commandPrefixes">
    /// Optional custom command-tag name prefixes.  When non-null the run is
    /// hypothetical and the result is wrapped in a <see cref="HypotheticalGateResult"/>.
    /// </param>
    /// <param name="safetyPrefixes">
    /// Optional custom safety-tag name prefixes.  When non-null the run is
    /// hypothetical and the result is wrapped in a <see cref="HypotheticalGateResult"/>.
    /// </param>
    /// <param name="session">
    /// Optional active tag session. When its service is a <see cref="GatedTagService"/>
    /// the enforced report is taken from that service's policy instance
    /// (<c>enforcedBySession: true</c>); otherwise the open file's policy is
    /// reported with <c>policySource: "project-file"</c> and
    /// <c>enforcedBySession: false</c>.
    /// </param>
    /// <returns>
    /// A <see cref="WriteGatePolicy"/> (enforced, no custom prefixes),
    /// a <see cref="HypotheticalGateResult"/> (custom prefixes supplied),
    /// or a <see cref="GateReportError"/> when no project is open.
    /// </returns>
    public static object Execute(
        ProjectService service,
        IReadOnlyList<string>? commandPrefixes = null,
        IReadOnlyList<string>? safetyPrefixes  = null,
        TagSessionService?     session         = null)
    {
        try
        {
            return ExecuteCore(service, commandPrefixes, safetyPrefixes, session);
        }
        catch (GateConfigInvalidException ex)
        {
            return new GateReportError(ex.Error.Error + " " + ex.Error.Hint);
        }
    }

    private static object ExecuteCore(
        ProjectService service,
        IReadOnlyList<string>? commandPrefixes,
        IReadOnlyList<string>? safetyPrefixes,
        TagSessionService?     session)
    {
        bool isHypothetical = commandPrefixes is not null || safetyPrefixes is not null;
        var allowlist = session?.WriteAllowlist;

        // Enforced path with a gated session: report the very policy instance the
        // session's GatedTagService enforces — never re-derived from the open file,
        // which can differ (e.g. after reconnecting against another controller).
        if (!isHypothetical && session?.Service is GatedTagService gated)
        {
            var enforced = gated.Policy;
            if (enforced.Origin is not { EnforcedBySession: true })
                enforced = enforced with
                {
                    Origin = new GatePolicyOrigin(
                        GatePolicyOrigin.ProjectFile, service.FilePath, EnforcedBySession: true),
                };
            // Partial export plus allowlist: grants come only from the allowlist.
            if (gated.GrantsFromAllowlist && allowlist is not null)
                return BuildPartialAllowlistPolicy(gated.Policy, allowlist, service.FilePath);

            // Reporting only: the allowlist narrows writes but never alters the policy lists.
            return allowlist is null ? enforced : enforced with { Allowlist = allowlist };
        }

        if (!service.IsLoaded)
        {
            var np = service.NoProjectLoadedError("logix_gate_report");
            return new GateReportError(np.Error) { Kind = np.Kind, Hint = np.Hint };
        }

        // The open file's policy: not enforced by any session.
        var fileOrigin = new GatePolicyOrigin(
            GatePolicyOrigin.ProjectFile, service.FilePath, EnforcedBySession: false);

        // Why no gate is enforced for this file, computed from the live state on every call
        // (never cached) so it follows files opened or closed since the session connected.
        // A partial export never derives a write gate; a session that connected without one
        // keeps enforcing none whatever file is open now.
        string? noGateReason = service.PartialExport is not null
            ? PartialExportInfo.Reason
            : session?.Service is not null and not GatedTagService
                ? session.NoGateReason ?? NoSessionGateReason
                : null;

        if (!isHypothetical && noGateReason is not null)
        {
            // Labelled hypothetical, never "enforced": nothing enforces this file's gate.
            var fileConfig = GateConfigResolver.Current;
            var filePolicy = GateEngine.DerivePolicy(service.Project!, fileConfig)
                with { Origin = fileOrigin, Allowlist = allowlist };
            return new HypotheticalGateResult(
                filePolicy, fileConfig, Array.Empty<DivergenceEntry>(), noGateReason,
                CustomPrefixes: false);
        }

        if (!isHypothetical)
        {
            // No session at all: the file-derived policy, explicitly marked as
            // not enforced by a session. Result carries policy: "enforced".
            return GateEngine.DerivePolicy(service.Project!, GateConfigResolver.Current)
                with { Origin = fileOrigin, Allowlist = allowlist };
        }

        // Hypothetical path: merge caller-supplied lists with defaults for any
        // dimension the caller omitted, then run the engine with both configs and
        // compute the divergence between the two policies.
        var enforcedConfig = GateConfigResolver.Current;
        var wildcard = commandPrefixes?.Any(p => p.Trim() == GateConfigResolver.Wildcard) == true;
        var config = enforcedConfig with
        {
            CommandPrefixes       = wildcard ? new[] { string.Empty } : commandPrefixes ?? enforcedConfig.CommandPrefixes,
            SafetyPrefixes        = safetyPrefixes ?? enforcedConfig.SafetyPrefixes,
            AllNamesDesignated    = wildcard || (commandPrefixes is null && enforcedConfig.AllNamesDesignated),
            CommandPrefixesSource = commandPrefixes is null ? enforcedConfig.CommandPrefixesSource : "request",
        };

        var hypotheticalPolicy = GateEngine.DerivePolicy(service.Project!, config)
            with { Origin = fileOrigin, Allowlist = allowlist };
        var enforcedPolicy     = GateEngine.DerivePolicy(service.Project!, enforcedConfig);
        var divergence         = ComputeDivergence(enforcedPolicy, hypotheticalPolicy);

        return new HypotheticalGateResult(hypotheticalPolicy, config, divergence, noGateReason);
    }

    /// <summary>Note attached to the partial-export plus allowlist report.</summary>
    internal const string PartialAllowlistNote =
        "Partial export plus write allowlist: grants come only from the allowlist; the partial file's " +
        "denies and ranges still apply. Only tags the allowlist names are listed. A prefix entry (ending in '*') " +
        "also grants tags the file does not declare, which cannot be enumerated here.";

    /// <summary>
    /// Builds the enforced report for a partial export gated by a write allowlist. The session's
    /// policy (derived with every tag a candidate) is reduced to the tags the allowlist names:
    /// each is a grant carrying any range the file declares, or a deny carrying the file's reason.
    /// Exact allowlist entries the file does not declare are listed as allowlist grants (or as a
    /// <see cref="DenyReason.SafetyTag"/> deny when they carry a safety prefix). File tags the
    /// allowlist does not name are not listed.
    /// </summary>
    /// <param name="filePolicy">The policy instance the session enforces.</param>
    /// <param name="allowlist">The active allowlist.</param>
    /// <param name="filePath">Path of the open partial export.</param>
    /// <returns>A policy labelled <c>allowlist+partial-export-denies</c>, enforced by the session.</returns>
    internal static WriteGatePolicy BuildPartialAllowlistPolicy(
        WriteGatePolicy filePolicy, WriteAllowlist allowlist, string? filePath)
    {
        var grants = filePolicy.Grants.Where(g => allowlist.Permits(g.ScopedName)).ToList();
        var denies = filePolicy.Denies.Where(d => allowlist.Permits(d.ScopedName)).ToList();

        var known = new HashSet<string>(
            filePolicy.Grants.Select(g => g.ScopedName).Concat(filePolicy.Denies.Select(d => d.ScopedName)),
            StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in allowlist.ExactEntries)
        {
            var cut  = entry.IndexOf('[');
            var name = cut > 0 ? entry[..cut] : entry;
            if (name.Contains('.') || known.Contains(name) || !added.Add(name)) continue;

            if (GateConfigResolver.SafetyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                denies.Add(new DenyEntry(name, DenyReason.SafetyTag,
                    $"Tag '{name}' matches a safety prefix; listed in the write allowlist '{allowlist.Path}' but never granted."));
            else
                grants.Add(new GrantEntry(name, null, null,
                    $"Granted by write allowlist '{allowlist.Path}'; the partial export does not declare this tag."));
        }

        return new WriteGatePolicy(grants, denies)
        {
            Origin    = new GatePolicyOrigin(PartialAllowlistPolicySource, filePath ?? filePolicy.Origin?.Path, EnforcedBySession: true),
            Allowlist = allowlist,
            Note      = PartialAllowlistNote,
            Unparsed  = filePolicy.Unparsed,
        };
    }

    /// <summary><c>policySource</c> of the partial-export plus allowlist report (same as the connect <c>gateSource</c>).</summary>
    internal const string PartialAllowlistPolicySource = "allowlist+partial-export-denies";

    /// <summary>
    /// Serializes the result of <see cref="Execute"/> to a JSON string.
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="WriteGatePolicy"/> — uses <see cref="WriteGatePolicy.ToJson()"/>
    ///     for deterministic, null-free output with <c>policy: "enforced"</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="HypotheticalGateResult"/> — serializes with
    ///     <c>policy: "hypothetical"</c> and an explicit <c>disclaimer</c> string
    ///     stating the results do NOT represent the enforced policy.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="GateReportError"/> — serializes to a compact JSON error object.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="result">Return value of <see cref="Execute"/>.</param>
    /// <returns>Compact JSON string.</returns>
    public static string Serialize(object result)
    {
        if (result is WriteGatePolicy policy)
            return policy.ToJson();

        if (result is HypotheticalGateResult hypothetical)
            return hypothetical.Policy.ToJson(
                WriteGatePolicy.DefaultMaxDenies,
                "hypothetical",
                !hypothetical.CustomPrefixes
                    ? $"NO ENFORCED WRITE GATE — {hypothetical.Reason}. " +
                      "This report classifies the open file only and does NOT represent a policy enforced by logix_write."
                    : hypothetical.Reason is null
                        ? BuildDisclaimer(hypothetical.Config)
                        : $"NO ENFORCED WRITE GATE — {hypothetical.Reason}. " + BuildDisclaimer(hypothetical.Config),
                hypothetical.Divergence);

        return JsonSerializer.Serialize(result, _errorOptions);
    }

    /// <summary>
    /// Computes the divergence between an enforced policy and a hypothetical policy by
    /// building a per-tag disposition map for each and collecting tags where the
    /// disposition differs.
    /// </summary>
    /// <param name="enforced">Policy derived from <see cref="GateConfigResolver.Current"/>.</param>
    /// <param name="hypothetical">Policy derived from the caller-supplied config.</param>
    /// <returns>
    /// A list of <see cref="DivergenceEntry"/> records sorted alphabetically by tag
    /// name, one per tag whose classification changed between the two policies.
    /// Empty when the two policies classify every tag identically.
    /// </returns>
    private static IReadOnlyList<DivergenceEntry> ComputeDivergence(
        WriteGatePolicy enforced,
        WriteGatePolicy hypothetical)
    {
        var enforcedMap    = BuildDispositionMap(enforced);
        var hypotheticalMap = BuildDispositionMap(hypothetical);

        // Union of all tag names across both policies (handles any tag present in one
        // but not the other, though in practice both run on the same tag list).
        var allTags = enforcedMap.Keys
            .Union(hypotheticalMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal);

        var result = new List<DivergenceEntry>();
        foreach (var tag in allTags)
        {
            // Tags absent from a map are treated as "unknown" (defensive only —
            // both policies are derived from the same project tag list).
            enforcedMap.TryGetValue(tag, out var enforcedDisp);
            hypotheticalMap.TryGetValue(tag, out var hypotheticalDisp);

            if (!string.Equals(enforcedDisp, hypotheticalDisp, StringComparison.OrdinalIgnoreCase))
                result.Add(new DivergenceEntry(tag, enforcedDisp ?? "unknown", hypotheticalDisp ?? "unknown"));
        }

        return result;
    }

    /// <summary>
    /// Builds a tag-name → disposition-string map from a <see cref="WriteGatePolicy"/>.
    /// Disposition strings are <c>"granted"</c> or <c>"denied:&lt;Reason&gt;"</c>
    /// (e.g. <c>"denied:NotDesignated"</c>), matching the serialized
    /// <see cref="DenyReason"/> name.
    /// </summary>
    private static Dictionary<string, string> BuildDispositionMap(WriteGatePolicy policy)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in policy.Grants)
            map[grant.ScopedName] = "granted";
        foreach (var deny in policy.Denies)
            map[deny.ScopedName] = $"denied:{deny.Reason}";
        return map;
    }

    /// <summary>
    /// Builds a disclaimer string for a hypothetical gate-report run that names
    /// the custom prefix lists used and states clearly that the results are NOT
    /// the enforced policy.
    /// </summary>
    /// <param name="config">The custom <see cref="GateConfig"/> used for the run.</param>
    /// <returns>A combined machine-readable and human-readable disclaimer string.</returns>
    private static string BuildDisclaimer(GateConfig config)
    {
        var cmdPrefixes = string.Join(", ", config.EffectiveCommandPrefixes.Select(p => $"'{p}'"));
        var enforcedCmd = GateConfigResolver.TryDescribeEnforced();
        var safPrefixes = string.Join(", ", config.SafetyPrefixes.Select(p => $"'{p}'"));
        return
            $"HYPOTHETICAL CLASSIFICATION ONLY — " +
            $"This report describes a what-if policy using custom prefix rules " +
            $"(commandPrefixes: [{cmdPrefixes}], safetyPrefixes: [{safPrefixes}]) " +
            $"and does NOT represent the policy enforced by logix_write or the live server. " +
            $"The enforced policy uses commandPrefixes: {enforcedCmd} and " +
            $"safetyPrefixes: ['Safety_']. " +
            $"Do not treat these results as authoritative.";
    }
}
