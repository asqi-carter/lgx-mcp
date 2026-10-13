using System.Text.Json.Serialization;

/// <summary>
/// Success response returned by <see cref="ListModulesHandler.Execute"/>.
/// </summary>
/// <param name="Source">
/// <c>"project-file"</c> for an offline project opened via <c>logix_open_project</c>;
/// <c>"session-image"</c> when an online session is attached and the tree comes from the
/// session's L5X image.
/// </param>
/// <param name="Modules">Modules ordered depth-first from <c>Local</c>.</param>
/// <param name="Note">
/// Top-level hint (online sessions without <c>probe</c>, or a probe that could not run);
/// <c>null</c> (omitted from JSON) otherwise.
/// </param>
public record ListModulesResult(
    [property: JsonPropertyName("source")]  string                    Source,
    [property: JsonPropertyName("modules")] IReadOnlyList<ModuleEntry> Modules,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null)
{
    /// <summary><c>true</c> when the session image is marked stale and this response may not show the controller's current logic; omitted otherwise.</summary>
    [JsonPropertyName("imageStale"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ImageStale { get; init; }

    /// <summary>UTC time (ISO 8601) the image was marked stale; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleSince { get; init; }

    /// <summary>What the image could not follow; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("staleReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StaleReason { get; init; }

    /// <summary>The reconnect hint; set with <see cref="ImageStale"/>.</summary>
    [JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

/// <summary>Error response returned when no project is loaded.</summary>
/// <param name="Error">Human-readable error message.</param>
public record ListModulesError(
    [property: JsonPropertyName("error")] string Error);

/// <summary>
/// Pure handler for the read-only <c>logix_list_modules</c> MCP tool. Never writes,
/// inhibits or configures modules, and makes no SDK or controller call: the tree comes
/// from the L5X already held by <see cref="ProjectService"/> (the same image
/// <c>logix_gate_report</c> reads).
/// </summary>
public static class ListModulesHandler
{
    /// <summary>Source value for offline projects.</summary>
    public const string SourceProjectFile  = ProjectSourceLabel.ProjectFile;

    /// <summary>Source value when an online session is attached.</summary>
    public const string SourceSessionImage = ProjectSourceLabel.SessionImage;

    /// <summary>
    /// Connection state reported for online sessions: no supported read of per-module CIP
    /// connection status is implemented, so the state is never guessed.
    /// </summary>
    public const string ConnectionUnknown = "unknown";

    /// <summary>
    /// Lists the I/O module tree of the open project. Never throws; returns a
    /// <see cref="ListModulesError"/> when no project is loaded.
    /// </summary>
    /// <param name="service">Project singleton holding the current L5X.</param>
    /// <param name="session">
    /// Optional tag session. When it targets a live controller the response carries
    /// <c>source: "session-image"</c> and a per-module <c>connection</c> field
    /// (<c>"unknown"</c>); offline projects omit the connection field.
    /// </param>
    /// <returns>A <see cref="ListModulesResult"/> or a <see cref="ListModulesError"/>.</returns>
    public static object Execute(ProjectService service, TagSessionService? session = null)
    {
        if (!service.IsLoaded)
            return new ListModulesError("No project loaded. Call logix_open_project first.");

        var modules = ModuleTreeBuilder.Build(service.Project!);
        var source  = ProjectSourceLabel.Resolve(session);

        var stale = ProjectFileStaleness.For(session, source, service);
        if (source == ProjectSourceLabel.ProjectFile)
            return new ListModulesResult(source, modules)
            {
                ImageStale  = stale is null ? null : true,
                StaleSince  = stale?.StaleSince,
                StaleReason = stale?.StaleReason,
                Hint        = stale?.Hint,
            };

        // Without probe no network call is made: every state stays "unknown"; modules whose
        // CIP route cannot be built also say why.
        var routes = BuildRoutes(modules);
        return new ListModulesResult(
            source,
            modules.Select(m => m with
            {
                Connection       = ConnectionUnknown,
                ConnectionDetail = routes[m.Name].Detail,
            }).ToList(),
            ProbeNote)
        {
            ImageStale  = stale is null ? null : true,
            StaleSince  = stale?.StaleSince,
            StaleReason = stale?.StaleReason,
            Hint        = stale?.Hint,
        };
    }

    /// <summary>Top-level note returned when the caller did not pass <c>probe: true</c>.</summary>
    public const string ProbeNote =
        "pass probe: true to read each module's connection state (read-only CIP Identity reads routed through the controller)";

    /// <summary>Detail for modules not reached before the whole-call time cap.</summary>
    public const string ProbeTimeLimitDetail = "probe time limit";

    /// <summary>Per-module reply timeout.</summary>
    public static readonly TimeSpan PerModuleTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Time cap for the whole probe.</summary>
    public static readonly TimeSpan ProbeTimeCap = TimeSpan.FromSeconds(30);

    /// <summary>Opens the single EtherNet/IP session used for a probe.</summary>
    /// <param name="host">Controller host from the active session.</param>
    /// <param name="ct">Cancellation token.</param>
    public delegate Task<IModuleIdentityReader> ReaderFactory(string host, CancellationToken ct);

    /// <summary>
    /// Like <see cref="Execute"/>, but with <paramref name="probe"/> true and an active online session
    /// it opens ONE session to the controller and sends one routed, read-only CIP Identity read per
    /// module, sequentially (2 s each, 30 s for the whole call). Results: <c>responding</c> (status 0,
    /// with <c>identity</c>), <c>not_responding</c> (0x01/0x0204 or socket timeout), <c>error</c> (any
    /// other status) or <c>unknown</c> (unprobed, unbuildable route, or past the time cap). Without
    /// <paramref name="probe"/> no network call is made. Never throws for transport failures.
    /// </summary>
    /// <param name="service">Project singleton holding the current L5X.</param>
    /// <param name="session">Tag session; the controller address comes from <see cref="TagSessionService.ControllerHost"/>.</param>
    /// <param name="probe">Whether to read each module's identity.</param>
    /// <param name="readerFactory">Injectable reader factory; defaults to a <see cref="RoutedIdentitySession"/>.</param>
    /// <param name="timeCap">Whole-call cap; defaults to <see cref="ProbeTimeCap"/>.</param>
    /// <param name="perModuleTimeout">Per-module reply timeout; defaults to <see cref="PerModuleTimeout"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ListModulesResult"/> or a <see cref="ListModulesError"/>.</returns>
    public static async Task<object> ExecuteAsync(
        ProjectService service, TagSessionService? session, bool probe,
        ReaderFactory? readerFactory = null, TimeSpan? timeCap = null, TimeSpan? perModuleTimeout = null,
        CancellationToken ct = default)
    {
        var baseline = Execute(service, session);
        if (!probe || baseline is not ListModulesResult listed || listed.Source != SourceSessionImage)
            return baseline;

        var host = session?.ControllerHost;
        if (string.IsNullOrWhiteSpace(host))
            return listed with { Note = "probe skipped: the active session has no controller address" };

        var cap     = timeCap ?? ProbeTimeCap;
        var each    = perModuleTimeout ?? PerModuleTimeout;
        var routes  = BuildRoutes(listed.Modules);
        var clock   = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ModuleEntry>(listed.Modules.Count);

        IModuleIdentityReader reader;
        try
        {
            reader = await (readerFactory ?? DefaultReaderFactory)(host, ct).ConfigureAwait(false);
        }
        catch (RoutedIdentityException ex)
        {
            return listed with { Note = $"probe failed: {ex.Message}" };
        }

        await using (reader.ConfigureAwait(false))
        {
            foreach (var m in listed.Modules)
            {
                var route = routes[m.Name];
                if (route.Path is null) { results.Add(m); continue; }   // unknown + why

                var remaining = cap - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    results.Add(m with { ConnectionDetail = ProbeTimeLimitDetail });
                    continue;
                }

                try
                {
                    var r = await reader.ReadAsync(route.Path, remaining < each ? remaining : each, ct).ConfigureAwait(false);
                    results.Add(Apply(m, r));
                }
                catch (RoutedIdentityException ex)
                {
                    results.Add(m with { Connection = "error", ConnectionDetail = ex.Message });
                }
            }
        }
        return listed with { Modules = results, Note = null };
    }

    private static async Task<IModuleIdentityReader> DefaultReaderFactory(string host, CancellationToken ct) =>
        await RoutedIdentitySession.OpenAsync(host, ct: ct).ConfigureAwait(false);

    private static ModuleEntry Apply(ModuleEntry m, RoutedIdentityResult r)
    {
        var withState = m with { Connection = r.Connection, ConnectionDetail = r.Detail };
        if (r.Identity is not { } id) return withState;

        var identity = new ModuleIdentity(
            id.VendorId, id.DeviceType, id.ProductCode, id.Revision, id.RevisionMajor,
            id.Status, id.Serial, id.ProductName);
        return withState with { Identity = identity, IdentityMismatch = Mismatch(m, identity) };
    }

    /// <summary>
    /// Whether the configured module disagrees with the identity it returned. Only the major revision
    /// is compared, and the product name only when the catalog number is a substring of it (in which case
    /// it agrees by construction). <c>null</c> when nothing was comparable; never guessed.
    /// </summary>
    internal static bool? Mismatch(ModuleEntry configured, ModuleIdentity identity)
    {
        bool? result = null;

        var rev = configured.Revision?.Split('.')[0];
        if (int.TryParse(rev, out var major))
            result = major != identity.RevisionMajor;

        if (result != true
            && !string.IsNullOrWhiteSpace(configured.CatalogNumber)
            && identity.ProductName.Contains(configured.CatalogNumber, StringComparison.OrdinalIgnoreCase))
            result ??= false;

        return result;
    }

    /// <summary>Detail reported for ControlNet, DeviceNet and other port types with no CIP route support.</summary>
    public const string RouteTypeNotSupported = "route type not supported";

    /// <summary>Route outcome for one module.</summary>
    /// <param name="Path">CIP route path bytes from the controller, or <c>null</c> when unbuildable.</param>
    /// <param name="Detail">Why the route could not be built; <c>null</c> when <paramref name="Path"/> is set.</param>
    public sealed record ModuleRoute(byte[]? Path, string? Detail);

    /// <summary>
    /// Builds the CIP route path from the controller to every module in the tree. <c>Local</c>
    /// gets <c>[0x01, controllerSlot]</c>. A module directly under <c>Local</c> gets its exit hop
    /// (port 1: <c>[0x01, slot]</c>; port 2 / Ethernet: <c>[0x12, len, ip, pad]</c>); a module
    /// behind a bridge gets the bridge's own route followed by that hop. Modules with a missing
    /// address, an unknown or unsupported port type (ControlNet, DeviceNet) or an unbuildable
    /// parent route get a <c>null</c> path and a detail explaining why.
    /// </summary>
    /// <param name="modules">Module tree from <see cref="ModuleTreeBuilder.Build"/>.</param>
    /// <returns>Route outcome keyed by module name (case-insensitive).</returns>
    public static IReadOnlyDictionary<string, ModuleRoute> BuildRoutes(IReadOnlyList<ModuleEntry> modules)
    {
        var byName = new Dictionary<string, ModuleEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules) byName.TryAdd(m.Name, m);

        var result = new Dictionary<string, ModuleRoute>(StringComparer.OrdinalIgnoreCase);

        ModuleRoute Resolve(ModuleEntry m, HashSet<string> chain)
        {
            if (result.TryGetValue(m.Name, out var done)) return done;
            if (!chain.Add(m.Name)) return new ModuleRoute(null, "module parent chain is cyclic");

            var route = Compute(m, chain);
            chain.Remove(m.Name);
            return result[m.Name] = route;
        }

        ModuleRoute Compute(ModuleEntry m, HashSet<string> chain)
        {
            if (m.Parent is null)
            {
                if (!string.Equals(m.Name, "Local", StringComparison.OrdinalIgnoreCase))
                    return new ModuleRoute(null, "module has no parent to route through");
                return TryBackplane(m.SlotOrAddress, "controller slot is missing", out var local, out var err)
                    ? new ModuleRoute(local, null)
                    : new ModuleRoute(null, err);
            }

            if (!byName.TryGetValue(m.Parent, out var parent))
                return new ModuleRoute(null, $"parent module '{m.Parent}' not found");

            byte[] hop;
            string? error;
            switch (ExitKind(m))
            {
                case HopKind.Backplane:
                    if (!TryBackplane(m.SlotOrAddress, "slot address is missing", out hop, out error))
                        return new ModuleRoute(null, error);
                    break;
                case HopKind.Ethernet:
                    if (string.IsNullOrWhiteSpace(m.SlotOrAddress))
                        return new ModuleRoute(null, "IP address is missing");
                    try { hop = RoutedIdentity.EthernetRoute(m.SlotOrAddress.Trim()); }
                    catch (ArgumentException) { return new ModuleRoute(null, "IP address is not a valid route address"); }
                    break;
                default:
                    return new ModuleRoute(null, RouteTypeNotSupported);
            }

            // Modules directly under the controller start from the controller itself.
            if (parent.Parent is null && string.Equals(parent.Name, "Local", StringComparison.OrdinalIgnoreCase))
                return new ModuleRoute(hop, null);

            var bridge = Resolve(parent, chain);
            return bridge.Path is null
                ? new ModuleRoute(null, $"route to bridge '{parent.Name}' unavailable: {bridge.Detail}")
                : new ModuleRoute([.. bridge.Path, .. hop], null);
        }

        foreach (var m in modules) Resolve(m, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private enum HopKind { Backplane, Ethernet, Unsupported }

    /// <summary>
    /// Classifies the hop onto <paramref name="m"/>: by the L5X type of its upstream port when
    /// known (<c>ICP</c>/chassis = backplane, <c>Ethernet</c>), else by the parent port id
    /// (1 = backplane, 2 = Ethernet). Anything else (ControlNet, DeviceNet, ...) is unsupported.
    /// </summary>
    private static HopKind ExitKind(ModuleEntry m)
    {
        if (!string.IsNullOrWhiteSpace(m.UpstreamPortType))
        {
            var t = m.UpstreamPortType.Trim();
            if (t.Equals("Ethernet", StringComparison.OrdinalIgnoreCase)) return HopKind.Ethernet;
            if (t.Equals("ICP", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Backplane", StringComparison.OrdinalIgnoreCase)) return HopKind.Backplane;
            return HopKind.Unsupported;
        }

        return m.Port switch
        {
            1 => HopKind.Backplane,
            2 => HopKind.Ethernet,
            _ => HopKind.Unsupported,
        };
    }

    private static bool TryBackplane(string? address, string missing, out byte[] route, out string? error)
    {
        route = [];
        error = null;
        if (string.IsNullOrWhiteSpace(address)) { error = missing; return false; }
        if (!byte.TryParse(address, out var slot)) { error = $"slot address '{address}' is not a slot number"; return false; }
        route = RoutedIdentity.BackplaneRoute(slot);
        return true;
    }
}
