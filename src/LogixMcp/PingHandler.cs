using System.Reflection;
using System.Text.Json.Serialization;

/// <summary>
/// Single source of truth for the MCP server's advertised identity. Used by
/// both <see cref="PingHandler"/> and the <c>ServerInfo</c> in Program.cs.
/// </summary>
public static class ServerIdentity
{
    /// <summary>The server name advertised over MCP and in <c>logix_ping</c>.</summary>
    public const string Name = "lgx-mcp";

    /// <summary>
    /// The server version, read from <see cref="AssemblyInformationalVersionAttribute"/>
    /// (set once via <c>Directory.Build.props</c>) with any <c>+sha</c> build-metadata
    /// suffix stripped.
    /// </summary>
    public static string Version { get; } = ReadVersion(typeof(ServerIdentity).Assembly);

    /// <summary>
    /// Reads the informational version from <paramref name="assembly"/>, stripping any
    /// <c>+metadata</c> suffix.
    /// </summary>
    /// <param name="assembly">Assembly to inspect.</param>
    /// <returns>The bare version string, or <c>"0.0.0"</c> when the attribute is absent.</returns>
    public static string ReadVersion(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return "0.0.0";
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}

/// <summary>
/// Capability flags embedded in the <c>logix_ping</c> response so callers
/// can introspect the server's active opt-in gates without reading environment
/// variables directly.
/// </summary>
/// <param name="LiveWrites">
/// <c>true</c> when <c>LGX_ENABLE_LIVE_WRITES</c> is set to a truthy value,
/// meaning live controller mutations are permitted.
/// </param>
/// <param name="StudioAutomation">
/// <c>true</c> when <c>LGX_ENABLE_STUDIO_AUTOMATION</c> is set to a truthy
/// value, meaning Studio GUI-automation tools are permitted.
/// </param>
/// <param name="PublishMode">
/// The active publish-mode gate: <c>"confirm"</c> (default) or <c>"auto"</c>.
/// Reflects the value of <c>LGX_PUBLISH_MODE</c>.
/// </param>
/// <param name="RungEditLifecycle">
/// One-line description of the full rung-edit cycle: stage -> publish -> accept or
/// cancel. Names <c>logix_accept_pending_edits</c> and <c>logix_cancel_pending_edits</c>
/// when <c>LGX_ENABLE_STUDIO_AUTOMATION</c> is on, otherwise points at Logix Designer
/// (Logic -> Online Edits) and the flag that enables the tools.
/// </param>
/// <param name="WriteAllowlistActive">
/// <c>true</c> when <c>LGX_WRITE_ALLOWLIST</c> names an allowlist file, so the next
/// connect narrows the write gate to the listed tags.
/// </param>
/// <param name="WriteAllowlistPath">The allowlist file path, or <c>null</c> (omitted) when none is configured.</param>
public record CapabilitiesPayload(
    [property: JsonPropertyName("liveWrites")]       bool   LiveWrites,
    [property: JsonPropertyName("studioAutomation")] bool   StudioAutomation,
    [property: JsonPropertyName("publishMode")]      string PublishMode = "confirm",
    [property: JsonPropertyName("rungEditLifecycle")] string RungEditLifecycle = "",
    [property: JsonPropertyName("liveFinalize")]     bool   LiveFinalize = false,
    [property: JsonPropertyName("liveRungEditPath")] string LiveRungEditPath = "",
    [property: JsonPropertyName("diagnostics")]      string Diagnostics = PingHandler.DiagnosticsHint,
    [property: JsonPropertyName("writeAllowlistActive")] bool WriteAllowlistActive = false,
    [property: JsonPropertyName("writeAllowlistPath")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? WriteAllowlistPath = null);

/// <summary>
/// Strongly-typed response returned by <see cref="PingHandler.Execute"/>.
/// </summary>
/// <param name="Status">Lifecycle status of the server (always "ok" when running).</param>
/// <param name="Server">Server identity name.</param>
/// <param name="Version">Server version string.</param>
/// <param name="Capabilities">
/// Active capability flags, or <c>null</c> when no <see cref="Capabilities"/>
/// instance was supplied to <see cref="PingHandler.Execute"/>.
/// Serialized as the <c>"capabilities"</c> JSON object; omitted entirely when
/// <c>null</c> so existing call sites that do not pass capabilities remain
/// backward-compatible.
/// </param>
/// <param name="AuditMaxMb">Active <c>LGX_AUDIT_MAX_MB</c> rotation threshold (0 = never rotate).</param>
/// <param name="AuditKeep">Active <c>LGX_AUDIT_KEEP</c> count of rotated audit files retained.</param>
/// <param name="TargetMajorRevision">Major revision of the current target (session, else open project); <c>null</c> when unknown.</param>
/// <param name="MnemonicSet"><c>iec</c> (36 and above), <c>legacy</c> (35 and below) or <c>unknown</c>.</param>
public record PingResponse(
    [property: JsonPropertyName("status")]  string Status,
    [property: JsonPropertyName("server")]  string Server,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("capabilities")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    CapabilitiesPayload? Capabilities = null,
    [property: JsonPropertyName("auditMaxMb")] double AuditMaxMb = AuditLogger.DefaultMaxMb,
    [property: JsonPropertyName("auditKeep")]  int    AuditKeep  = AuditLogger.DefaultKeep,
    [property: JsonPropertyName("targetMajorRevision")] int? TargetMajorRevision = null,
    [property: JsonPropertyName("mnemonicSet")] string MnemonicSet = MnemonicSetNames.Unknown)
{
    /// <summary>Warnings about the target (an unknown revision applies no mnemonic check); omitted when none.</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Warnings { get; init; }
}

/// <summary>
/// Pure handler for the <c>logix_ping</c> MCP tool.
/// Kept free of MCP-specific attributes so it can be unit-tested directly.
/// </summary>
public static class PingHandler
{
    /// <summary>
    /// Pointer to the read-only setup and health check, surfaced as
    /// <c>capabilities.diagnostics</c> in the <c>logix_ping</c> payload.
    /// </summary>
    public const string DiagnosticsHint =
        "logix_doctor: read-only setup and health check; run it first when a connect or action fails";

    /// <summary>
    /// Builds the ping response payload as a strongly-typed object.
    /// </summary>
    /// <param name="capabilities">
    /// Optional capability flags to embed in the response.  When <c>null</c>
    /// the <c>"capabilities"</c> field is omitted from the serialized JSON
    /// (backward-compatible path used by existing unit tests).
    /// </param>
    /// <param name="target">
    /// Revision of the current target (session, else open project). <c>null</c> reports no target:
    /// <c>mnemonicSet</c> is <c>unknown</c> and no warning is added.
    /// </param>
    /// <returns>
    /// A <see cref="PingResponse"/> with status, server name, version, and
    /// — when <paramref name="capabilities"/> is non-null — a
    /// <see cref="CapabilitiesPayload"/> reflecting the active gates.
    /// </returns>
    public static PingResponse Execute(Capabilities? capabilities = null, TargetRevisionInfo? target = null) =>
        new(
            Status:  "ok",
            Server:  ServerIdentity.Name,
            Version: ServerIdentity.Version,
            AuditMaxMb: AuditLogger.MaxMb,
            AuditKeep:  AuditLogger.Keep,
            TargetMajorRevision: target?.MajorRevision,
            MnemonicSet: target?.MnemonicSet ?? MnemonicSetNames.Unknown,
            Capabilities: capabilities is null
                ? null
                : new CapabilitiesPayload(
                    LiveWrites:       capabilities.LiveWritesEnabled,
                    StudioAutomation: capabilities.StudioAutomationEnabled,
                    PublishMode:      capabilities.PublishMode == PublishMode.Auto ? "auto" : "confirm",
                    RungEditLifecycle: DescribeRungEditLifecycle(capabilities.StudioAutomationEnabled),
                    LiveFinalize:     capabilities.LiveFinalizeEnabled,
                    LiveRungEditPath: DescribeLiveRungEditPath(capabilities.LiveFinalizeEnabled),
                    WriteAllowlistActive: capabilities.WriteAllowlistPath is not null,
                    WriteAllowlistPath:   capabilities.WriteAllowlistPath))
        {
            Warnings = target?.Warning is { } warning ? [warning] : null,
        };

    /// <summary>
    /// Describes the full rung-edit cycle (stage -> publish -> accept or cancel) for the
    /// <c>logix_ping</c> capabilities payload.
    /// </summary>
    /// <param name="studioAutomationEnabled">
    /// Whether <c>LGX_ENABLE_STUDIO_AUTOMATION</c> is on.
    /// </param>
    /// <returns>A single-sentence lifecycle description.</returns>
#if LOGIX_SDK
    public static string DescribeRungEditLifecycle(bool studioAutomationEnabled) =>
        studioAutomationEnabled
            ? "ONLY when the engineer asks for a pending edit to review in Studio (to try a change and back it out use target=live then undo); " +
              "a pending zone blocks every further import into that program until resolved: " +
              "stage (logix_propose_rung_edit target=workstation) -> publish (target=controller, " +
              "rungs pending, not executing) -> accept (logix_accept_pending_edits) or cancel " +
              "(logix_cancel_pending_edits), each with confirm=true and the engineer's go-ahead; " +
              "optionally try it first: logix_test_pending_edits (the new rung executes), verify " +
              "with logix_pending_edits (zoneState 'tested') and a read, then " +
              "logix_assemble_pending_edits (irreversible) or logix_untest_pending_edits then " +
              "logix_cancel_pending_edits; verify with logix_pending_edits"
            : "ONLY when the engineer asks for a pending edit to review in Studio (to try a change and back it out use target=live then undo); " +
              "a pending zone blocks every further import into that program until resolved: " +
              "stage (logix_propose_rung_edit target=workstation) -> publish (target=controller, " +
              "rungs pending, not executing) -> accept or cancel in Logix Designer (Logic -> " +
              "Online Edits), or set LGX_ENABLE_STUDIO_AUTOMATION=1 to use " +
              "logix_accept_pending_edits / logix_cancel_pending_edits / logix_test_pending_edits / " +
              "logix_untest_pending_edits / logix_assemble_pending_edits";
#else
    public static string DescribeRungEditLifecycle(bool studioAutomationEnabled) =>
        "not available in this build: it has no rung-editing or pending-edit tools; " +
        "make structural changes in Logix Designer";
#endif

    /// <summary>
    /// Describes the one-call live path for the <c>logix_ping</c> capabilities payload.
    /// </summary>
    /// <param name="liveFinalizeEnabled">Whether <c>LGX_ENABLE_LIVE_FINALIZE</c> is on.</param>
    /// <returns>A single-sentence description of the live path and its availability.</returns>
#if LOGIX_SDK
    public static string DescribeLiveRungEditPath(bool liveFinalizeEnabled) =>
        (liveFinalizeEnabled
            ? "available (LGX_ENABLE_LIVE_FINALIZE=1): "
            : "disabled (set LGX_ENABLE_LIVE_FINALIZE=1 to enable): ") +
        "logix_propose_rung_edit target=live (then undo, SDK only) is the way to try a change and back it out; it publishes and finalizes in one call when nothing " +
        "is pending, so the rung executes immediately; requires LGX_ENABLE_LIVE_WRITES=1, " +
        "confirmedByUser=true even in auto publish mode, an empty staged-rung buffer, and " +
        "expectedRungText when replacing; use only with the engineer's explicit go-ahead, then " +
        "verify with logix_pending_edits and a read";
#else
    public static string DescribeLiveRungEditPath(bool liveFinalizeEnabled) =>
        "not available in this build: it has no rung-editing tools";
#endif
}
