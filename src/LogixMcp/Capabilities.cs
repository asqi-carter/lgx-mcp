using System.Text.Json.Serialization;

// ── PublishMode ───────────────────────────────────────────────────────────────

/// <summary>
/// Controls how a staged-set publish reaches the controller.
/// </summary>
public enum PublishMode
{
    /// <summary>
    /// Default mode: the agent must request explicit confirmation before
    /// pushing a staged set to the controller.
    /// </summary>
    Confirm,

    /// <summary>
    /// Automatic mode: the agent may push a staged set to the controller
    /// without an additional confirmation step.
    /// </summary>
    Auto,
}

// ── Capabilities ──────────────────────────────────────────────────────────────

/// <summary>
/// Process-lifetime capability flags read once at startup from environment variables.
/// Injectable into handlers and tool classes so tests do not need to mutate the
/// process environment.
/// </summary>
/// <param name="LiveWritesEnabled">
/// <c>true</c> when <see cref="EnvVar"/> is set to a truthy value
/// (<c>"1"</c>, <c>"true"</c>, or <c>"yes"</c>, case-insensitive).
/// When <c>false</c>, live controller mutations are refused before any
/// network or SDK call.
/// </param>
/// <param name="StudioAutomationEnabled">
/// <c>true</c> when <see cref="EnvVarStudio"/> is set to a truthy value
/// (<c>"1"</c>, <c>"true"</c>, or <c>"yes"</c>, case-insensitive).
/// When <c>false</c>, Studio GUI-automation tools (e.g.
/// <c>logix_cancel_pending_edits</c>) are refused before any GUI or
/// desktop-automation interaction.
/// </param>
/// <param name="PublishMode">
/// Controls how a staged-set publish reaches the controller.
/// Parsed from <see cref="EnvVarPublishMode"/>: case-insensitive <c>"auto"</c>
/// maps to <see cref="PublishMode.Auto"/>; unset or any other value maps to
/// <see cref="PublishMode.Confirm"/> (the default).
/// </param>
/// <param name="LiveFinalizeEnabled">
/// <c>true</c> when <see cref="EnvVarLiveFinalize"/> is set to a truthy value
/// (<c>"1"</c>, <c>"true"</c>, or <c>"yes"</c>, case-insensitive).
/// When <c>false</c>, <c>logix_propose_rung_edit</c> with <c>target="live"</c>
/// (publish and finalize in one SDK call) is refused before any
/// session work or SDK import.
/// </param>
/// <param name="WriteAllowlistPath">
/// Path from <c>LGX_WRITE_ALLOWLIST</c> (see <see cref="WriteAllowlist"/>), or
/// <c>null</c> when unset — in which case connect behaviour is unchanged.
/// </param>
/// <param name="ControllerDownloadEnabled">
/// <c>true</c> when <see cref="EnvVarControllerDownload"/> is set to a truthy value.
/// <c>logix_connect</c> with <c>allowControllerDownload=true</c> (the destructive
/// PROGRAM→download→go_online→RUN sequence) additionally requires this flag, on top
/// of <see cref="LiveWritesEnabled"/>.
/// </param>
public record Capabilities(
    bool        LiveWritesEnabled,
    bool        StudioAutomationEnabled = false,
    PublishMode PublishMode             = PublishMode.Confirm,
    bool        LiveFinalizeEnabled     = false,
    string?     WriteAllowlistPath      = null,
    bool        ControllerDownloadEnabled = false)
{
    /// <summary>
    /// The environment variable name that gates live controller mutations.
    /// </summary>
    public const string EnvVar = "LGX_ENABLE_LIVE_WRITES";

    /// <summary>
    /// The environment variable name that gates Studio GUI-automation tools
    /// such as <c>logix_cancel_pending_edits</c>.
    /// </summary>
    public const string EnvVarStudio = "LGX_ENABLE_STUDIO_AUTOMATION";

    /// <summary>
    /// The environment variable name that gates <c>target="live"</c> on
    /// <c>logix_propose_rung_edit</c> (publish and finalize in one call).
    /// </summary>
    public const string EnvVarLiveFinalize = "LGX_ENABLE_LIVE_FINALIZE";

    /// <summary>
    /// The environment variable name that gates the destructive controller download
    /// (<c>logix_connect</c> with <c>allowControllerDownload=true</c>), in addition to
    /// <see cref="EnvVar"/>.
    /// </summary>
    public const string EnvVarControllerDownload = "LGX_ENABLE_CONTROLLER_DOWNLOAD";

    /// <summary>
    /// The environment variable name that governs the publish mode.
    /// Accepted values: <c>"auto"</c> (case-insensitive) → <see cref="PublishMode.Auto"/>.
    /// Unset or any other value → <see cref="PublishMode.Confirm"/>.
    /// </summary>
    public const string EnvVarPublishMode = "LGX_PUBLISH_MODE";

    /// <summary>
    /// The client command to include in refusal hints so the user knows exactly
    /// how to opt in from their MCP client.
    /// </summary>
    public const string ClientCommandHint =
        @"claude mcp add lgx-mcp -e LGX_ENABLE_LIVE_WRITES=1 -- <path>\LogixMcp.exe";

#if LOGIX_SDK
    /// <summary>
    /// The client command to include in Studio-automation refusal hints so the
    /// user knows exactly how to opt in from their MCP client.
    /// </summary>
    public const string ClientCommandHintStudio =
        @"claude mcp add lgx-mcp -e LGX_ENABLE_STUDIO_AUTOMATION=1 -- <path>\LogixMcp.exe";
#endif

    /// <summary>
    /// The client command to include in live-finalize refusal hints so the user
    /// knows exactly how to opt in from their MCP client.
    /// </summary>
    public const string ClientCommandHintLiveFinalize =
        @"claude mcp add lgx-mcp -e LGX_ENABLE_LIVE_WRITES=1 -e LGX_ENABLE_LIVE_FINALIZE=1 -- <path>\LogixMcp.exe";

    /// <summary>
    /// The client command to include in controller-download refusal hints so the user
    /// knows exactly how to opt in from their MCP client.
    /// </summary>
    public const string ClientCommandHintControllerDownload =
        @"claude mcp add lgx-mcp -e LGX_ENABLE_LIVE_WRITES=1 -e LGX_ENABLE_CONTROLLER_DOWNLOAD=1 -- <path>\LogixMcp.exe";

    /// <summary>
    /// Reads <see cref="EnvVar"/>, <see cref="EnvVarStudio"/>, and
    /// <see cref="EnvVarPublishMode"/> from the process environment and returns a
    /// <see cref="Capabilities"/> instance.  Truthy values for the boolean flags:
    /// <c>"1"</c>, <c>"true"</c>, <c>"yes"</c> (case-insensitive).
    /// Unset or any other value → <c>false</c> for the respective flag.
    /// For <see cref="PublishMode"/>: <c>"auto"</c> (case-insensitive) →
    /// <see cref="PublishMode.Auto"/>; unset or any other value →
    /// <see cref="PublishMode.Confirm"/>.
    /// </summary>
    public static Capabilities FromEnvironment() =>
        new(IsTruthy(Environment.GetEnvironmentVariable(EnvVar)),
            IsTruthy(Environment.GetEnvironmentVariable(EnvVarStudio)),
            ParsePublishMode(Environment.GetEnvironmentVariable(EnvVarPublishMode)),
            IsTruthy(Environment.GetEnvironmentVariable(EnvVarLiveFinalize)),
            WriteAllowlist.PathFromEnvironment(),
            IsTruthy(Environment.GetEnvironmentVariable(EnvVarControllerDownload)));

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is one of the accepted
    /// truthy strings: <c>"1"</c>, <c>"true"</c>, or <c>"yes"</c>
    /// (case-insensitive). Returns <c>false</c> for <c>null</c> or any other
    /// string.
    /// </summary>
    public static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1",    StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes",  StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses <paramref name="value"/> into a <see cref="PublishMode"/>.
    /// Case-insensitive <c>"auto"</c> → <see cref="PublishMode.Auto"/>;
    /// <c>null</c> or any other string → <see cref="PublishMode.Confirm"/>.
    /// </summary>
    public static PublishMode ParsePublishMode(string? value) =>
        value is not null && value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? PublishMode.Auto
            : PublishMode.Confirm;
}

// ── CapabilityDisabledError ───────────────────────────────────────────────────

/// <summary>
/// Structured error returned when a live-controller mutation is attempted but
/// <see cref="Capabilities.LiveWritesEnabled"/> is <c>false</c>.
///
/// <para>
/// The <see cref="Kind"/> field is always <c>"CapabilityDisabled"</c> so
/// callers can branch without parsing the human-readable <see cref="Error"/>
/// string.  The <see cref="Hint"/> field names both
/// <see cref="Capabilities.EnvVar"/> and the full client command so the user
/// knows exactly how to opt in.
/// </para>
/// </summary>
/// <param name="Error">Human-readable description of the refused operation.</param>
/// <param name="Kind">Always <c>"CapabilityDisabled"</c>.</param>
/// <param name="Hint">
/// Names <c>LGX_ENABLE_LIVE_WRITES</c> and the full
/// <c>claude mcp add lgx-mcp -e LGX_ENABLE_LIVE_WRITES=1 -- &lt;path&gt;\LogixMcp.exe</c>
/// client command.
/// </param>
public record CapabilityDisabledError(
    [property: JsonPropertyName("error")] string  Error,
    [property: JsonPropertyName("kind")]  string  Kind = ErrorKinds.CapabilityDisabled,
    [property: JsonPropertyName("hint")]  string? Hint = null);

// ── ConfirmationRequiredError ─────────────────────────────────────────────────

/// <summary>
/// Structured error returned when a controller-target publish is attempted in
/// <see cref="PublishMode.Confirm"/> mode without <c>confirmedByUser = true</c>.
///
/// <para>
/// The <see cref="Kind"/> field is always <c>"ConfirmationRequired"</c> so
/// callers can branch without parsing the human-readable <see cref="Error"/>
/// string.  <see cref="StagedRungCount"/> and <see cref="StagedTagNames"/>
/// echo the session's current staged state so the agent can present the pending
/// change set to the user before asking for explicit confirmation.
/// </para>
/// </summary>
/// <param name="Error">Human-readable description of the refused operation.</param>
/// <param name="Kind">Always <c>"ConfirmationRequired"</c>.</param>
/// <param name="StagedRungCount">
/// Number of rungs currently staged to the workstation target in this session.
/// Zero when nothing has been staged yet.
/// </param>
/// <param name="StagedTagNames">
/// Tag names currently held in the session's staged-tag buffer, or <c>null</c>
/// when the buffer is empty.
/// </param>
public record ConfirmationRequiredError(
    [property: JsonPropertyName("error")]           string                  Error,
    [property: JsonPropertyName("kind")]            string                  Kind           = "ConfirmationRequired",
    [property: JsonPropertyName("stagedRungCount")] int                     StagedRungCount = 0,
    [property: JsonPropertyName("stagedTagNames"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               IReadOnlyList<string>? StagedTagNames = null);
