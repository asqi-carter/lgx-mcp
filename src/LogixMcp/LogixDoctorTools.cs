using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>
/// MCP tool type for <c>logix_doctor</c>. Lives in the core assembly so the
/// tool exists in every install state: adapter missing, adapter failed to load, or a no-SDK build.
/// The SDK adapter does not register its own <c>logix_doctor</c>; exactly one is ever registered.
/// </summary>
[McpServerToolType]
public class LogixDoctorTools
{
    private readonly TagSessionService _tagSessionService;
    private readonly Capabilities?     _capabilities;
    private readonly ProjectService?   _projectService;

    /// <summary>Creates the tool type with the injected session registry and capability flags.</summary>
    /// <param name="tagSessionService">Session registry to inspect (never modified).</param>
    /// <param name="capabilities">Process-lifetime capability flags; null falls back to the environment.</param>
    /// <param name="projectService">Project service; its open project supplies the target revision when no session does.</param>
    public LogixDoctorTools(TagSessionService tagSessionService, Capabilities? capabilities = null,
        ProjectService? projectService = null)
    {
        ArgumentNullException.ThrowIfNull(tagSessionService);
        _tagSessionService = tagSessionService;
        _capabilities      = capabilities;
        _projectService    = projectService;
    }

    /// <summary>
    /// Builds the <c>logix_doctor</c> response: runs <see cref="DoctorHandler"/> against the
    /// production <see cref="DoctorEnvironment"/>. Read-only: never connects, changes mode, launches or writes.
    /// </summary>
    /// <param name="commPath">Optional CIP/SDK comm path the caller intends to use.</param>
    /// <param name="controllerIp">Optional controller IP; overrides the IP parsed from <paramref name="commPath"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string with <c>overall</c> and the ordered <c>checks</c>.</returns>
    public async Task<string> GetDoctorResponseAsync(
        string?           commPath = null,
        string?           controllerIp = null,
        CancellationToken ct = default)
    {
        var result = await DoctorHandler.ExecuteAsync(
            commPath, controllerIp,
            new DoctorEnvironment(_tagSessionService, project: _projectService),
            _capabilities ?? Capabilities.FromEnvironment(),
            ct);
        return JsonSerializer.Serialize(result);
    }

    /// <summary>Read-only setup and health check with a named fix for every non-ok check.</summary>
    [McpServerTool(Name = "logix_doctor")]
    [Description(
        "Read-only setup and health check: call this first when a connect or action fails, or when asked to " +
        "'check the setup'. Never connects a session, changes a mode, launches a process or writes a file; it " +
        "only advises. Returns {overall: ok|warn|fail, targetMajorRevision, mnemonicSet: iec|legacy|unknown, checks: [{id, status: ok|warn|fail|skipped|info, observed, fix?}]} " +
        "with the checks in this order: server, audit, sdkInstalled, sdkService, cipRuntime, echoService, " +
        "controllerReachable, controllerFault, session, pendingZone, studio. Every warn and fail carries a " +
        "one-sentence fix naming the exact next step; a check that does not apply is skipped with the reason, and an SDK or Studio check in the no-SDK build is info (\"not used by this build\"). " +
        "It may open and close a TCP connection to <ip>:44818 to test reachability. Available in every install " +
        "state: a check for a component that is not installed says what is missing and which paths were searched.")]
    public Task<string> Doctor(
        [Description(
            "Optional comm path you intend to use (for example 'AB_ETHIP-1\\10.0.0.5'); the controller IP is " +
            "parsed from it and the Echo checks apply when it starts with EmulateEthernet.")]
        string? commPath = null,
        [Description(
            "Optional controller IP to test for reachability on port 44818; overrides the IP parsed from commPath.")]
        string? controllerIp = null,
        CancellationToken ct = default) =>
        GetDoctorResponseAsync(commPath, controllerIp, ct);
}
