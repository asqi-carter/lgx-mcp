using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP JSON-RPC transport. Host.CreateApplicationBuilder wires the
// default console logger to stdout, which interleaves "info: ..." lines with
// protocol frames and breaks every MCP client that does not tolerate non-JSON
// lines. Every log record must go to stderr instead.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<TagSessionService>();
builder.Services.AddSingleton<ControllerModeService>();
builder.Services.AddSingleton(_ => new ConnectState(new ConnectRegistry()));
// Read LGX_ENABLE_LIVE_WRITES once at startup. Falsy/unset = writes to live
// controllers are refused. Set LGX_ENABLE_LIVE_WRITES=1 to opt in.
builder.Services.AddSingleton(Capabilities.FromEnvironment());

var mcpServerBuilder = builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = ServerIdentity.Name,
            Version = ServerIdentity.Version
        };

        // §3 orientation text (≤25 lines, always-resident — keep to orientation only).
        // The string is defined as LogixTools.CanonicalInstructions so that
        // InstructionsToolNameStalenessTests can assert every tool name it mentions
        // still exists in the registered tool set.
        options.ServerInstructions = LogixTools.CanonicalInstructions;
    })
    .WithStdioServerTransport()
    .WithLogixTasks()
    .WithToolErrorFilter()
    .WithToolsFromAssembly(typeof(LogixTools).Assembly)
    .WithResourcesFromAssembly(typeof(LogixTools).Assembly);

// Attempt to locate and load the SDK adapter assembly at runtime.
// SdkAdapterLoader probes for RockwellAutomation.* DLLs in: LOGIX_SDK_PATH env var →
// sdk/vendor beside the binary → Studio 5000 install path → AppContext.BaseDirectory.
// When found, a custom SdkAssemblyLoadContext resolves Rockwell dependencies from the
// probed directory and loads LogixMcp.SdkAdapter.dll; TryLoad then explicitly invokes
// SdkAdapterInitializer.Initialize to register SdkConnectFactory as
// ConnectHandler.DefaultSdkFactory before the first tool call is dispatched.
// The [ModuleInitializer] attribute on Initialize is kept as a secondary guarantee for
// hosts that execute adapter code directly, but explicit invocation is the primary contract.
// If the adapter DLL or Rockwell SDK is absent, TryLoad returns null and the sdk
// backend remains unavailable (ConnectHandler surfaces ConnectSdkNotAvailableError).
var adapterAssembly = SdkAdapterLoader.TryLoad();
if (adapterAssembly is not null)
{
    // WithToolsFromAssembly calls assembly.GetTypes() internally, which forces
    // dependency resolution for every type in the adapter.  Even though TryLoad
    // succeeded (the adapter DLL is present and the probe found a qualifying SDK
    // directory), GetTypes() can still throw when a transitive dependency is missing
    // at scan time.  Guard against the whole class of such failures so startup is
    // never fatal regardless of the SDK installation state.
    //
    // Caught exceptions:
    //   ReflectionTypeLoadException — one or more types could not be loaded
    //     (most common when a transitive dependency DLL is absent at scan time).
    //   FileNotFoundException — a dependency assembly could not be located at all.
    //
    // On any caught exception the failure reason is recorded into
    // SdkAdapterLoader.LoadFailureReason so ConnectHandler can surface it as part
    // of ConnectSdkNotAvailableError when the caller requests backend="sdk".
    try
    {
        mcpServerBuilder.WithToolsFromAssembly(adapterAssembly);
    }
    catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException)
    {
        // IL3000: Assembly.Location is "" for assemblies loaded in a single-file bundle. The adapter is
        // loaded from disk next to the server (never bundled), and the value is only used in a diagnostic
        // message, so an empty string in a bundled app is harmless.
#pragma warning disable IL3000
        SdkAdapterLoader.RecordTypeScanFailure(adapterAssembly.Location, ex);
#pragma warning restore IL3000
    }
}

var host = builder.Build();

// logix_ping is a static tool; give it the target (session, else open project) so it can report the
// target's major revision and mnemonic set.
{
    var pingSessions = host.Services.GetRequiredService<TagSessionService>();
    var pingProject  = host.Services.GetRequiredService<ProjectService>();
    LogixTools.CurrentTarget = () => TargetRevision.Current(pingSessions, pingProject);
}

await host.RunAsync();

/// <summary>
/// MCP tools for Allen-Bradley/Logix PLC integration.
/// </summary>
[McpServerToolType]
public static class LogixTools
{
    /// <summary>
    /// The canonical orientation text set as the MCP server's <c>instructions</c>
    /// field (§3 of the MCP Development Foundations).
    ///
    /// <para>
    /// Exposed as a public constant so that
    /// <c>InstructionsToolNameStalenessTests</c> can extract every
    /// <c>logix_*</c> tool name and assert each one is still registered —
    /// the only automated guard against this always-resident text going stale.
    /// </para>
    ///
    /// <para>
    /// Keep this text to ≤25 lines and orientation-only; do not expand it
    /// with per-tool documentation.
    /// </para>
    /// </summary>
#if LOGIX_SDK
    public const string CanonicalInstructions =
        """
        lgx-mcp drives Allen-Bradley/Logix controllers under write-gates derived
        from the open project file, not from configuration.

        Canonical sequence:  logix_open_project -> logix_connect -> read/write
        Online SDK work:     call logix_connect (backend="sdk", sdkMode="online",
                             sdkCommPath=<path>) BEFORE any logix_open_project; the
                             server uploads and correlates the project automatically;
                             reserve logix_open_project for offline/file work only.
        Orientation:         logix_project_summary (what is in the project)
                             logix_gate_report     (what this session may write)
        Backends: cip (live, licence-free, default) | sdk (structural edits, needs Studio 5000) | l5x (file) | useVirtual (in-memory).
        Rung edits:          describe each change as typed rungs=[{text}] and tags=[{name,dataType}] (the server builds the L5X; raw l5xFragment from an exported routine is the advanced alternative, never combined with rungs/tags).
                             To try a change and back it out: target="live" (needs LGX_ENABLE_LIVE_FINALIZE=1) then undo, SDK only, no zone left behind. target="controller" is ONLY for when the engineer asks for a pending edit to review and accept in Studio: the zone it creates blocks EVERY further import into that program until Studio resolves it, there is no override parameter, and a successful result carries a zoneNote saying so.
                             Pending-edit path (only on request): stage each edit with target="workstation",
                             then publish the whole batch exactly ONCE with
                             target="controller". Full cycle: stage -> publish -> accept
                             or cancel. Publish leaves the rung pending on the controller (Type="I", not executing); with LGX_ENABLE_STUDIO_AUTOMATION=1 and the engineer's go-ahead, the SDK's accept-pending-edits or cancel-pending-edits tool resolves it (confirm=true; verify with the SDK's pending-edits tool and a read); otherwise the engineer accepts or cancels in Studio 5000 (Logic -> Online Edits). Only then the next publish (one publish per accept/cancel cycle). The SDK's finalize-edits tool cannot assemble an existing zone (the SDK has no call for it).
                             Tags declared by a publish are created on the controller immediately and permanently, even though the rungs stay pending, and cancelling the rungs does not remove them. Created tags are External Access Read Only: readable, not writable through lgx-mcp or any CIP client until an engineer sets them to Read/Write in Logix Designer. External Access is the engineer's write grant; never try to change it (ExternalAccessChangeRefused). This session also may not write them (CreatedByThisSession).
                             The SDK's pending-edits tool reads edit-zone state at any point.
                             Try it, then decide: only with LGX_ENABLE_STUDIO_AUTOMATION=1 and the engineer's go-ahead: publish (target="controller") -> the SDK's test-pending-edits tool (the new rung EXECUTES; the original is kept) -> verify with logix_read on a tag the rung drives -> the SDK's assemble-pending-edits tool (irreversible) or its untest-pending-edits then cancel-pending-edits tools (all confirm=true). The routine export marks a tested and an untested zone alike (both Type="I"), so read the zone state from the SDK's pending-edits tool (zoneState, from the program export's TestEdits), and confirm with logix_read on a tag the rung drives.
                             Live path: with LGX_ENABLE_LIVE_FINALIZE=1 (plus LGX_ENABLE_LIVE_WRITES=1) and the engineer's explicit go-ahead, target="live" publishes AND finalizes in one call when nothing is pending: the rung executes immediately. It needs confirmedByUser=true even in auto publish mode, an empty staged-rung buffer, and expectedRungText (from the export) when replacing; verify with the SDK's pending-edits tool and a read. Use it, with undo, to try a change; use target="controller" only when the engineer asks for a pending edit.
        Skills: before proposing rung edits or when a write is denied, call
                logix_list_skills then logix_get_skill to load the matching playbook.
        Publish mode:        before publishing, check logix_ping -> capabilities.publishMode;
                             confirm (default): the server refuses a controller publish unless the agent passes confirmedByUser=true, which it may only do after the user explicitly approves; auto proceeds.
        Writes are gate-enforced and every denial cites the L5X element behind it.
        Live controller writes, mode changes, publishes, and downloads are disabled
        unless LGX_ENABLE_LIVE_WRITES=1 is set in the server environment.
        """;
#else
    public const string CanonicalInstructions =
        """
        lgx-mcp drives Allen-Bradley/Logix controllers under write-gates derived
        from the open project file, not from configuration.

        Canonical sequence:  logix_open_project -> logix_connect -> read/write
        Orientation:         logix_project_summary (what is in the project)
                             logix_gate_report     (what this session may write)
        Backends: cip (live, licence-free, default) | l5x (file) | useVirtual (in-memory).
        This build reads, writes and analyses; it has no rung-editing, pending-edit or
        Studio-automation tools. Make structural changes in Logix Designer.
        Skills: before analysing a project or when a write is denied, call
                logix_list_skills then logix_get_skill to load the matching playbook.
        Setup or connect trouble: run logix_doctor first (read-only health check).
        Writes are gate-enforced and every denial cites the L5X element behind it.
        Live controller writes and mode changes are disabled
        unless LGX_ENABLE_LIVE_WRITES=1 is set in the server environment.
        """;
#endif

    /// <summary>
    /// Builds the ping response payload as a JSON string, including the
    /// active capability flags read from the process environment.
    /// Delegates to <see cref="GetPingResponse(Capabilities?)"/> with
    /// <see cref="Capabilities.FromEnvironment()"/>.
    /// </summary>
    /// <returns>
    /// JSON string with status, server name, version, and a
    /// <c>capabilities</c> object reflecting <c>LGX_ENABLE_LIVE_WRITES</c>
    /// and <c>LGX_ENABLE_STUDIO_AUTOMATION</c>.
    /// </returns>
    public static string GetPingResponse() =>
        GetPingResponse(Capabilities.FromEnvironment(), CurrentTarget?.Invoke());

    /// <summary>
    /// Supplies the current target's revision to <c>logix_ping</c>; set at startup to read the session
    /// registry and project service. <c>null</c> (tests, before startup) reports no target.
    /// </summary>
    public static Func<TargetRevisionInfo>? CurrentTarget { get; set; }

    /// <summary>
    /// Builds the ping response payload as a JSON string with the supplied
    /// capability flags.  Intended for unit tests that need to inject specific
    /// capability values without mutating the process environment.
    /// </summary>
    /// <param name="capabilities">
    /// Capability flags to embed in the response.  Pass <c>null</c> to omit
    /// the <c>"capabilities"</c> field entirely (backward-compatible path).
    /// </param>
    /// <returns>JSON string with status, server name, version, and
    /// — when <paramref name="capabilities"/> is non-null — a
    /// <c>capabilities</c> object.</returns>
    public static string GetPingResponse(Capabilities? capabilities) =>
        GetPingResponse(capabilities, null);

    /// <summary>
    /// Builds the ping response payload with the supplied capability flags and target revision.
    /// </summary>
    /// <param name="capabilities">Capability flags, or <c>null</c> to omit <c>capabilities</c>.</param>
    /// <param name="target">The target's revision; <c>null</c> reports no target (<c>unknown</c>, no warning).</param>
    /// <returns>JSON string including <c>targetMajorRevision</c> and <c>mnemonicSet</c>.</returns>
    public static string GetPingResponse(Capabilities? capabilities, TargetRevisionInfo? target) =>
        JsonSerializer.Serialize(PingHandler.Execute(capabilities, target));

    /// <summary>
    /// Health check tool. Delegates to <see cref="GetPingResponse"/>.
    /// Instruments the call with <see cref="TrafficLogger"/>.
    /// </summary>
    [McpServerTool(Name = "logix_ping")]
    [Description("Health check. Returns server identity and version.")]
    public static string Ping()
    {
        var sw     = Stopwatch.StartNew();
        var result = GetPingResponse();
        sw.Stop();
        TrafficLogger.Append(
            "logix_ping",
            requestBytes:  0,
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }
}
