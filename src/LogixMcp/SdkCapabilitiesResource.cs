using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>
/// Resolves the capability table to report right now: the active SDK session's table when there is one,
/// otherwise what the install alone shows. Needs no session.
/// </summary>
public static class SdkCapabilitiesView
{
    /// <summary>The URI of the MCP resource.</summary>
    public const string ResourceUri = "lgx://sdk/capabilities";

    /// <summary>The table for the active SDK session, or the no-session view.</summary>
    /// <param name="tags">The session registry (never modified); null means "no session".</param>
    public static SdkCapabilitiesReport Current(TagSessionService? tags)
    {
        var observations = DoctorEnvironment.SdkObservations;
        if (tags is not null && observations?.GetCapabilities(tags) is { } session) return session;
        return SdkCapabilities.NoSession(observations is not null, observations?.AssemblyVersion);
    }
}

/// <summary>MCP resource <c>lgx://sdk/capabilities</c>: what the Logix Designer SDK can do here. No session required.</summary>
[McpServerResourceType]
public static class SdkCapabilitiesResource
{
    /// <summary>
    /// Returns the capability table as JSON. With an active SDK session it is that session's table; without
    /// one it reports what the install shows (SDK present or not, package version) and marks the rest unknown.
    /// </summary>
    /// <param name="tags">The session registry, injected by the host.</param>
    /// <returns>The JSON of <see cref="SdkCapabilitiesReport"/>.</returns>
    [McpServerResource(UriTemplate = SdkCapabilitiesView.ResourceUri, Name = "sdk_capabilities", MimeType = "application/json")]
    [Description(
        "What the Logix Designer SDK can do on this machine and session: the build check, finalize edits, rung delete, " +
        "program export with TestEdits, online tag write, upload, the Designer version and the SDK package version. " +
        "Each entry is available, unavailable or unknown, with its source (version rule, install or observed). " +
        "Needs no session: without one it reports what the install shows.")]
    public static string Read(TagSessionService tags) =>
        JsonSerializer.Serialize(SdkCapabilitiesView.Current(tags));
}
