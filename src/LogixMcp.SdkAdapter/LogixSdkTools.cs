#if LOGIX_SDK
// ── LogixSdkTools — MCP tool type for Logix Designer SDK operations ──────────
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>
/// MCP tool type exposing Logix Designer SDK operations that require an active
/// <see cref="LogixProjectSession"/> gRPC connection.
///
/// <para>
/// All tools in this class are SDK-only and are compiled only when the
/// <c>LOGIX_SDK</c> compilation symbol is defined (i.e. when the project is
/// built with <c>-p:EnableLogixSdk=true</c> and the
/// <c>RockwellAutomation.LogixDesigner.CSClient</c> package is available).
/// The core-only build excludes this file entirely via the
/// <c>&lt;Compile Remove&gt;</c> directive in the project file.
/// </para>
/// </summary>
[McpServerToolType]
public class LogixSdkTools
{
    /// <summary>
    /// Hint attached to the "No active SDK session" error of
    /// logix_propose_rung_edit, logix_pending_edits and logix_export_rungs.
    /// </summary>
    internal const string NoActiveSessionHint =
        "SDK sessions live in this server process; if your MCP client restarted the " +
        "server (new conversation turn or client restart), call logix_connect again. " +
        "Staged edits from the earlier process are gone.";

    private readonly ProjectService    _projectService;
    private readonly TagSessionService _tagSessionService;
    private readonly Capabilities?     _capabilities;
    private readonly StudioConfirmWait _confirmWait;

    /// <summary>
    /// Initialises a new <see cref="LogixSdkTools"/> with the injected services.
    /// </summary>
    /// <param name="projectService">
    /// Singleton project service holding the loaded project file path.
    /// </param>
    /// <param name="tagSessionService">
    /// Singleton tag-session service holding the active <see cref="ITagService"/>.
    /// Used by <see cref="TryGetActiveSession"/> to reuse a live
    /// <see cref="LogixProjectSession"/> when one is already open.
    /// </param>
    /// <param name="capabilities">
    /// Process-lifetime capability flags. When <c>null</c>, a default instance
    /// with <c>LiveWritesEnabled = false</c> is used so the gate is safe by default.
    /// </param>
    /// <param name="confirmWait">
    /// Publish/lifecycle call tracker for the Studio confirmation wait. When <c>null</c>,
    /// <see cref="StudioConfirmWait.Shared"/> is used; tests pass their own instance.
    /// </param>
    public LogixSdkTools(
        ProjectService    projectService,
        TagSessionService tagSessionService,
        Capabilities?     capabilities = null,
        StudioConfirmWait? confirmWait = null)
    {
        _confirmWait       = confirmWait ?? StudioConfirmWait.Shared;
        ArgumentNullException.ThrowIfNull(projectService);
        ArgumentNullException.ThrowIfNull(tagSessionService);
        _projectService    = projectService;
        _tagSessionService = tagSessionService;
        _capabilities      = capabilities;    // null = no capability gate applied

        // Out-of-band change detection: image reads probe the active SDK session's fingerprint through this.
        // It reads whichever session is active, so a reconnect needs no re-wiring.
        _tagSessionService.ControllerFingerprintSource ??= async ct =>
            GetActiveSession(_tagSessionService) is { } active
                ? await PendingEditsHandler.ReadFingerprintAsync(active, ct).ConfigureAwait(false)
                : null;
    }

    /// <summary>
    /// Returns the <see cref="LogixProjectSession"/> from the active
    /// <see cref="TagSessionService"/> session when the active service is backed
    /// by a <see cref="TypedSdkTagService"/>; returns <c>null</c> when no SDK
    /// session is active.
    ///
    /// <para>
    /// Traversal order:
    /// <see cref="TagSessionService.Service"/> →
    /// unwrap <see cref="GatedTagService.InnerService"/> decorator if present →
    /// cast to <see cref="TypedSdkTagService"/> →
    /// return <see cref="TypedSdkTagService.Session"/>.
    /// </para>
    /// </summary>
    internal LogixProjectSession? TryGetActiveSession() => GetActiveSession(_tagSessionService);

    /// <summary>
    /// The Studio window the lifecycle tools must act on for <paramref name="session"/>: its
    /// controller name and project file name, passed to <c>bench/studio-edits.ps1</c> as script
    /// parameters so the driver selects only the matching Logix Designer window.
    /// </summary>
    internal static StudioWindowTarget StudioWindowTargetOf(LogixProjectSession session) =>
        new(session.StudioControllerName, session.StudioProjectFileName);

    /// <summary>
    /// Static form of <see cref="TryGetActiveSession"/> that does not require an
    /// <see cref="LogixSdkTools"/> instance — used by seam bridges (e.g.
    /// <c>CloseProjectHandler.SdkSessionInspector</c>/<c>SdkSessionDisposer</c>)
    /// that only have a <see cref="TagSessionService"/> reference.
    /// </summary>
    /// <param name="tagSessionService">The tag-session singleton to inspect.</param>
    /// <returns>
    /// The active <see cref="LogixProjectSession"/> when the active service is
    /// backed by a <see cref="TypedSdkTagService"/> (optionally wrapped in a
    /// <see cref="GatedTagService"/>); <c>null</c> otherwise.
    /// </returns>
    internal static LogixProjectSession? GetActiveSession(TagSessionService tagSessionService)
    {
        // Unwrap the write-gate decorator that the SDK path always adds via GatedTagService,
        // and the tag-route decorator beneath it (LGX_TAG_ROUTE).
        var svc = TagRouteResolver.Backend(tagSessionService.Service);
        return (svc as TypedSdkTagService)?.Session;
    }

    // ── Internal helpers (testable without MCP layer) ─────────────────────────

    /// <summary>
    /// Retrieves the active <see cref="LogixProjectSession"/>, delegates to
    /// <see cref="ExportRungsHandler.ExecuteAsync"/>, then serializes the result.
    ///
    /// <para>
    /// Session selection order:
    /// <list type="number">
    ///   <item>Caller-supplied <paramref name="session"/> (unit tests) — used as-is.</item>
    ///   <item>Active <see cref="LogixProjectSession"/> from
    ///     <see cref="TagSessionService"/> when an SDK session is already established
    ///     via <c>logix_connect</c> — reused so the export operates on the live
    ///     connected project view without opening a redundant gRPC handle.</item>
    ///   <item>No session available — returns a structured error directing the caller
    ///     to call <c>logix_connect</c> first.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="xPath">Routine XPath to export.</param>
    /// <param name="session">
    /// Optional pre-opened <see cref="LogixProjectSession"/> for testing.
    /// When non-<c>null</c>, the session must already be open — this method
    /// does not open or dispose it.
    /// When <c>null</c> (the default), the active session is retrieved via
    /// <see cref="TryGetActiveSession"/>; an error is returned if none is registered.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetExportRungsResponseAsync(
        string xPath,
        LogixProjectSession? session = null,
        CancellationToken ct = default)
    {
        // logix_export_rungs operates on the live SDK session only — it does not
        // read or write the project file on disk.  No FilePath or IsLoaded guard is
        // needed here: the upload-path scenario establishes a session without ever
        // calling logix_open_project, and that is perfectly valid for export.

        // Determine which session to use (see summary for priority order):
        //   (1) Caller-supplied session (e.g. unit tests) — used as-is, no ownership.
        //   (2) Active LogixProjectSession from TagSessionService, established by
        //       logix_connect — reused so the export operates on the live connected
        //       project view without opening a redundant gRPC handle.
        //   (3) No session registered — fail fast with a clear error so the caller
        //       knows logix_connect must be called before logix_export_rungs.
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
            return JsonSerializer.Serialize(
                new ExportRungsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_export_rungs.",
                    NoActiveSessionHint,
                    ErrorKinds.NoActiveSession));

        var result = await ExportRungsHandler.ExecuteAsync(xPath, sdkSession, ct);
        return JsonSerializer.Serialize(result);
    }

    // ── MCP Tool endpoints ────────────────────────────────────────────────────

    /// <summary>
    /// Exports the current rungs at <paramref name="xPath"/> from the open
    /// project as an L5X fragment by calling
    /// <see cref="ILogixProject.PartialExportToXmlFileAsync"/> via the Logix
    /// Designer SDK.
    /// Returns a JSON object with <c>status</c>, <c>xPath</c>, and <c>l5x</c>
    /// on success, or a structured error object on failure.
    /// </summary>
    [McpServerTool(Name = "logix_export_rungs")]
    [Description(
        "Exports the current rungs at the specified SDK XPath from the open " +
        "Logix project as an L5X XML fragment, using the Logix Designer SDK " +
        "(PartialExportToXmlFileAsync). " +
        "This captures a 'before' snapshot of the rung logic at the given routine " +
        "path, which is useful for diffing or as the basis for a proposed rung edit. " +
        "The xPath parameter must be a Logix SDK XPath string identifying the routine, " +
        "e.g. Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']. " +
        "Returns a JSON object with status='ok', xPath, and l5x (the XML content) on success. " +
        "The l5x field in the returned JSON is the canonical input for logix_propose_rung_edit — " +
        "it can be used directly as the l5xFragment parameter without modification, because the " +
        "SDK export already produces the full RSLogix5000Content > Controller > Programs > Program > " +
        "Routines > Routine > RLLContent envelope that logix_propose_rung_edit requires. " +
        "Canonical workflow: (1) call logix_export_rungs to obtain a valid l5x fragment, " +
        "(2) edit the exported fragment as needed, " +
        "(3) call logix_propose_rung_edit with the (modified) l5x text as l5xFragment. " +
        "When the export contains project-authored text, the result also carries " +
        "untrustedContent (the kinds present, e.g. rung-comment, tag-description) and a notice. " +
        "Both are omitted when no authored text is present; the l5x itself is never wrapped or altered. " +
        "Notice: " + ExportRungsHandler.UntrustedNotice + " " +
        "Returns a structured error object if no project is loaded, the SDK session " +
        "cannot be opened, or the export operation fails. " +
        "Requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed " +
        "Studio 5000 or FactoryTalk Logix Echo installation on the host. " +
        "Call logix_open_project first. " +
        "Before your first edit, load the playbook: logix_get_skill(\"lgx-propose-rung-change\").")]
    public async Task<string> ExportRungs(
        [Description(
            "Logix Designer SDK XPath identifying the routine or rung range to export. " +
            "Example: Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']. " +
            "The XPath must resolve to a valid element in the open project.")]
        string xPath)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetExportRungsResponseAsync(xPath);
        sw.Stop();
        TrafficLogger.Append(
            "logix_export_rungs",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { xPath }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    // ── logix_propose_rung_edit ───────────────────────────────────────────────

    /// <summary>
    /// Maps every controller- and program-scoped tag name in the loaded project to its
    /// <c>ExternalAccess</c> (a missing attribute is Read/Write, the L5X default).
    /// </summary>
    private static Dictionary<string, string> BuildTagAccessCensus(L5Sharp.Core.L5X project)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in project.Tags.Concat(project.Programs.SelectMany(p => p.Tags)))
            map[tag.Name] = tag.Serialize().Attribute("ExternalAccess")?.Value ?? "Read/Write";
        return map;
    }

    /// <summary>
    /// Data type per tag name (controller- and program-scoped) and the names of every data type and add-on
    /// instruction the loaded project defines, for the <c>tags[]</c> checks of <c>logix_propose_rung_edit</c>.
    /// A name declared with different types in different scopes is left out (the check cannot tell which one is meant).
    /// </summary>
    private static TagDeclarationCensus BuildTagDeclarationCensus(L5Sharp.Core.L5X project, L5Sharp.Core.L5X? sessionImage = null)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in project.Tags.Concat(project.Programs.SelectMany(p => p.Tags)))
        {
            var el   = tag.Serialize();
            var type = el.Attribute("DataType")?.Value;
            if (string.IsNullOrWhiteSpace(type)) continue;
            var dims = el.Attribute("Dimensions")?.Value;
            var full = string.IsNullOrWhiteSpace(dims) || dims == "0" ? type : $"{type}[{dims}]";
            if (types.TryGetValue(tag.Name, out var seen) && !string.Equals(seen, full, StringComparison.OrdinalIgnoreCase))
                ambiguous.Add(tag.Name);
            types[tag.Name] = full;
        }
        foreach (var name in ambiguous) types.Remove(name);

        var defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dt in project.DataTypes) defined.Add(dt.Name);
        foreach (var aoi in project.AddOnInstructions) defined.Add(aoi.Name);
        // A type created in this session (logix_import_datatype) is in the controller image before the project file.
        if (sessionImage is not null)
        {
            foreach (var dt in sessionImage.DataTypes) defined.Add(dt.Name);
            foreach (var aoi in sessionImage.AddOnInstructions) defined.Add(aoi.Name);
        }
        return new TagDeclarationCensus(types, defined);
    }

    /// <summary>The Add-On Instruction names declared in the loaded project (empty when none is loaded).</summary>
    private static IReadOnlySet<string> AoiNames(ProjectService projectService)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (projectService.Project is { } project)
                foreach (var aoi in project.AddOnInstructions)
                    names.Add(aoi.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return names;
    }

    /// <summary>
    /// Builds the offline import fragment for typed <c>rungs</c>/<c>tags</c> from the open
    /// L5X file's root attributes.
    /// </summary>
    private static (string? Fragment, ProposeRungEditError? Error) BuildOfflineTypedFragment(
        string routineXPath, uint insertPosition,
        IReadOnlyList<RungInput>? rungs, IReadOnlyList<TagInput>? tags, string projectFilePath)
    {
        // Validation already passed in the caller; this only normalizes the rung text.
        ProposeRungEditHandler.ValidateTypedInput(null, rungs, tags, out var normalized);

        var programMatch = ProposeRungEditHandler.ProgramNamePattern.Match(routineXPath ?? string.Empty);
        var routineMatch = ProposeRungEditHandler.RoutineNamePattern.Match(routineXPath ?? string.Empty);
        if (!programMatch.Success || !routineMatch.Success)
            return (null, new ProposeRungEditError(
                $"Could not parse program or routine name from routineXPath '{routineXPath}'. " +
                "Expected format: Controller/Programs/Program[@Name='...']/Routines/Routine[@Name='...']."));

        try
        {
            var root = SafeXml.LoadDocument(projectFilePath).Root
                ?? throw new InvalidOperationException("the project file has no root element");
            return (RungFragmentBuilder.BuildOffline(
                root, programMatch.Groups[1].Value, routineMatch.Groups[1].Value,
                insertPosition, normalized, tags), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, new ProposeRungEditError(
                $"Could not build the offline import fragment from '{projectFilePath}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Manages a <see cref="LogixProjectSession"/> lifecycle and delegates to
    /// <see cref="ProposeRungEditHandler.ExecuteAsync"/> (online path) or
    /// <see cref="ProposeRungEditHandler.ExecuteOfflineAsync"/> (offline path),
    /// then serializes the result.
    ///
    /// <para>
    /// When <paramref name="offline"/> is <c>false</c> (default):
    /// if <paramref name="session"/> is <c>null</c> a new session is allocated and
    /// opened; when non-<c>null</c> the caller-supplied session is used as-is.
    /// The session must be both online and correlated — the handler enforces this.
    /// </para>
    ///
    /// <para>
    /// When <paramref name="offline"/> is <c>true</c>:
    /// no session is created or used.  The handler applies the rung edit directly
    /// to the L5X project file on disk and returns a
    /// <see cref="ProposeRungEditOfflineResult"/> with <c>status="saved-to-file"</c>.
    /// Offline mode must be requested explicitly; it is never activated by fallback.
    /// </para>
    /// </summary>
    /// <param name="routineXPath">Routine XPath identifying the target routine.</param>
    /// <param name="insertPosition">Zero-based rung position for the import.</param>
    /// <param name="replaceCount">Number of existing rungs to replace (0 = pure insert).</param>
    /// <param name="l5xFragment">
    /// Inline L5X XML containing the rungs to import. May be <c>null</c>, empty, or
    /// whitespace-only when <paramref name="target"/> is <c>"controller"</c> and
    /// <paramref name="offline"/> is <c>false</c> — see
    /// <see cref="ProposeRungEditHandler.ExecuteAsync"/> for the empty-fragment
    /// controller-publish behaviour. Required (non-empty)
    /// for every other combination.
    /// </param>
    /// <param name="session">
    /// Optional pre-opened <see cref="LogixProjectSession"/> for unit testing.
    /// Ignored when <paramref name="offline"/> is <c>true</c>.
    /// Production callers should omit this argument.
    /// </param>
    /// <param name="offline">
    /// When <c>true</c>, applies the rung edit directly to the project file on disk
    /// without opening a session or contacting a controller.
    /// When <c>false</c> (default), requires an online correlated session.
    /// Offline mode is never activated by fallback — callers must pass <c>true</c> explicitly.
    /// </param>
    /// <param name="target">
    /// Controls where the online rung edit lands after import.  Accepted values:
    /// <c>null</c> or <c>"workstation"</c> (default — rungs and tag declarations
    /// are held locally in the server's in-memory image and staging buffer; neither
    /// can be seen by any other session or the controller) or
    /// <c>"controller"</c> (rungs and buffered tag declarations are sent to the
    /// physical controller and are visible to any Logix Designer session online
    /// with the controller). Tag creation is LIVE: any newly-declared tag is created
    /// on the controller immediately at publish time and is OUTSIDE the pending-edit
    /// zone — only the rungs are pending; cancelling the pending rung edit in Studio
    /// does not remove the tags. See <see cref="ProposeRungEditResult.TagsCreated"/>.
    /// Ignored when <paramref name="offline"/> is <c>true</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="verifyOnStage">
    /// Opt-in (default <c>false</c>), <c>target="workstation"</c> only; see
    /// <see cref="ProposeRungEditHandler.ExecuteAsync"/>. Costs an extra project save-as, offline open, import and
    /// build per stage call.
    /// </param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetProposeRungEditResponseAsync(
        string               routineXPath,
        uint                 insertPosition,
        uint                 replaceCount,
        string?              l5xFragment,
        LogixProjectSession? session          = null,
        bool                 offline          = false,
        string?              target           = null,
        bool                 confirmedByUser  = false,
        CancellationToken    ct               = default,
        IReadOnlyList<string>? expectedRungText = null,
        IReadOnlyList<RungInput>? rungs         = null,
        IReadOnlyList<TagInput>?  tags          = null,
        bool                 verifyOnStage    = false)
    {
        // One rung_edit audit line per call. A refusal made here, before the handler
        // runs, writes it as outcome "refused" with the error kind.
        string RefuseRungEdit(ProposeRungEditError refusal)
        {
            AuditLogger.LogRungEdit(routineXPath, insertPosition, "refused", null,
                destination: offline ? "file"
                    : string.Equals(target, "live", StringComparison.OrdinalIgnoreCase) ? "live"
                    : string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ? "controller"
                    : "workstation",
                kind: refusal.Kind);
            return JsonSerializer.Serialize(refusal);
        }

        // Capability-flag and confirm-mode gate refusals also count as the call's single rung_edit line.
        void LogGateRefusal(string kind) =>
            AuditLogger.LogRungEdit(routineXPath, insertPosition, "refused", null,
                destination: string.Equals(target, "live", StringComparison.OrdinalIgnoreCase) ? "live" : "controller",
                kind: kind);

        // ── Typed rungs/tags validation ──────────────────────
        // Runs before any I/O, capability gate, session work or SDK call.
        bool hasTypedInput = rungs is { Count: > 0 } || tags is { Count: > 0 };
        if (ProposeRungEditHandler.ValidateTypedInput(l5xFragment, rungs, tags, out var checkedRungs)
                is { } typedInputError)
            return RefuseRungEdit(typedInputError);

        // ── Mnemonic-set check ───────────────────────────────
        // Every target: a rung using the other revision's instruction names, or something the
        // revision dropped, is refused before any capability gate, session work, import or staging.
        // Nothing is rewritten; the refusal carries suggestedText for the caller to resend.
        var mnemonicTarget = offline
            ? _projectService.TargetRevision ?? TargetRevision.Current(_tagSessionService, _projectService)
            : TargetRevision.Current(_tagSessionService, _projectService);
        var mnemonicCheck = MnemonicSetChecker.Check(
            checkedRungs.Count > 0
                ? checkedRungs.Select(r => r.Text).ToList()
                : MnemonicSetChecker.RungTextsFromFragment(l5xFragment, routineXPath),
            mnemonicTarget,
            () => AoiNames(_projectService));
        if (mnemonicCheck.Refused)
            return RefuseRungEdit(
                new ProposeRungEditError(
                    mnemonicCheck.Message!,
                    Hint: "Use the instruction names of the target's mnemonic set (logix_ping and logix_connect " +
                          "report mnemonicSet and targetMajorRevision).",
                    Kind: mnemonicCheck.Kind,
                    Rungs: mnemonicCheck.Findings));
        IReadOnlyList<string>? mnemonicWarnings =
            mnemonicCheck.Warning is { } mnemonicWarning ? [mnemonicWarning] : null;

        // On target="live", an explicit empty `rungs: []` with replaceCount > 0 is
        // a rung DELETE, not a tag-only publish (a null/omitted `rungs` is still not a delete).
        // A delete carries no tags: tags next to it are refused rather than published.
        bool liveDeleteRequested =
            !offline &&
            string.Equals(target, "live", StringComparison.OrdinalIgnoreCase) &&
            rungs is { Count: 0 } && replaceCount > 0;
        if (liveDeleteRequested && tags is { Count: > 0 })
            return RefuseRungEdit(
                new ProposeRungEditError(
                    "A live rung delete (rungs: [] with replaceCount > 0) cannot be combined " +
                    "with tags. Send the tags in a separate call. No import was attempted.",
                    Kind: "DeleteWithTags"));

        // An empty rungs list with replaceCount > 0 is a delete; only target="live" supports
        // it. On controller/workstation it would create a zone, so refuse it up front.
        if (!offline && rungs is { Count: 0 } && replaceCount > 0 &&
            !string.Equals(target, "live", StringComparison.OrdinalIgnoreCase))
            return RefuseRungEdit(
                new ProposeRungEditError(
                    "An empty rungs list (rungs: []) with replaceCount > 0 is a rung delete, " +
                    "which is only supported with target=\"live\". On target=\"controller\" or " +
                    "\"workstation\" it would create a pending-edit zone instead of deleting. " +
                    "No import was attempted.",
                    Kind: "DeleteNeedsLive"));

        // Tags without rungs is a tag-only publish: allowed on controller/live,
        // refused everywhere else exactly as an empty fragment is today.
        if (hasTypedInput && rungs is not { Count: > 0 } && !liveDeleteRequested)
        {
            bool tagOnlyPublishTarget =
                !offline &&
                (string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(target, "live",       StringComparison.OrdinalIgnoreCase));
            if (!tagOnlyPublishTarget)
                return RefuseRungEdit(
                    new ProposeRungEditError(
                        "tags without rungs is only accepted when target=\"controller\" (or " +
                        "\"live\"): a tag-only publish sends the declared tags with the staged " +
                        "buffer. Provide at least one rung for target=\"workstation\" or " +
                        "offline=true. No import was attempted."));
        }

        // ── Offline path: write directly to the L5X file, no session needed ───
        // The offline branch must write to the project file on disk, so it
        // genuinely requires a loaded project with a valid FilePath. The
        // empty-fragment publish is a controller-target,
        // online-only concept — offline mode always requires a non-empty fragment,
        // so a null l5xFragment is coalesced to empty here and rejected by
        // ExecuteOfflineAsync's own validation exactly as before.
        if (offline)
        {
            // The offline writer only writes rungs, so typed tags would be dropped
            // silently. Refuse instead (QA note).
            if (hasTypedInput && tags is { Count: > 0 } && rungs is { Count: > 0 })
                return RefuseRungEdit(
                    new ProposeRungEditError(
                        "tags are not supported with offline=true: the offline path writes " +
                        "rungs only, so tag declarations would be dropped. Declare the tags in " +
                        "the project file first, or use an online target.",
                        Kind: "TagsNotSupportedOffline"));
            if (!_projectService.IsLoaded || _projectService.FilePath is null)
                return RefuseRungEdit(
                    new ProposeRungEditError(
                        "Offline rung edit requires a project file on disk. " +
                        "Call logix_open_project to load a project file, then retry."));

            // Typed rungs: build the minimal shape the offline path parses, taking the
            // envelope attributes from the open L5X file's root. (Tags are carried in the
            // fragment but, as with a raw fragment, the offline path only writes rungs.)
            if (hasTypedInput)
            {
                var offlineBuilt = BuildOfflineTypedFragment(
                    routineXPath, insertPosition, rungs, tags, _projectService.FilePath);
                if (offlineBuilt.Error is not null)
                    return RefuseRungEdit(offlineBuilt.Error);
                l5xFragment = offlineBuilt.Fragment;
            }

            var policy = _projectService.Project is { } proj
                ? GateEngine.DerivePolicy(proj, GateConfigResolver.Current)
                : (WriteGatePolicy?)null;

            var offlineResult = await ProposeRungEditHandler.ExecuteOfflineAsync(
                routineXPath, insertPosition, replaceCount, l5xFragment ?? string.Empty,
                _projectService.FilePath, policy, ct);

            // The file on disk changed: refresh the in-memory model so logix_get_routine,
            // logix_find_tags and the other read tools do not serve the pre-edit image.
            // A reload failure must not turn a successful save into an error; surface a warning.
            if (offlineResult is ProposeRungEditOfflineResult)
            {
                try
                {
                    _projectService.Reload();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    mnemonicWarnings = (mnemonicWarnings ?? [])
                        .Append("The file was saved, but the in-memory project could not be reloaded " +
                                $"({ex.Message}); read tools may show stale content until logix_open_project is called again.")
                        .ToList();
                }
            }

            // ── Stamp publishMode on every ProposeRungEditOfflineResult ────────
            var offlinePublishModeStr = _capabilities?.PublishMode == PublishMode.Auto ? "auto" : "confirm";
            if (offlineResult is ProposeRungEditOfflineResult offlinePropResult)
                offlineResult = offlinePropResult with
                {
                    PublishMode = offlinePublishModeStr,
                    // Typed rungs/tags: the server built the envelope.
                    Envelope    = hasTypedInput ? "server-built" : offlinePropResult.Envelope,
                    Warnings    = mnemonicWarnings,
                };

            return JsonSerializer.Serialize(offlineResult);
        }

        // ── Live-write capability gate ─────────────────────────────────────────
        // When LGX_ENABLE_LIVE_WRITES is not set, controller-target publishes are
        // refused here, before any session interaction or SDK call.
        // Workstation-target staging is always allowed (does not reach the
        // physical controller).
        {
            // This change gate 1: target="live" additionally requires LGX_ENABLE_LIVE_FINALIZE.
            // Checked first so a disabled flag is always reported before anything else,
            // and before any session work or SDK import.
            if (string.Equals(target, "live", StringComparison.OrdinalIgnoreCase) &&
                _capabilities is not { LiveFinalizeEnabled: true })
            {
                AuditLogger.LogCapabilityRefusal(
                    "logix_propose_rung_edit", Capabilities.EnvVarLiveFinalize);
                LogGateRefusal("CapabilityDisabled");
                return JsonSerializer.Serialize(
                    new CapabilityDisabledError(
                        Error:
                            "logix_propose_rung_edit with target='live' refused: " +
                            "LGX_ENABLE_LIVE_FINALIZE is not set. " +
                            "Publishing and finalizing an edit in one call (the edit executes " +
                            "on the controller immediately) requires the opt-in capability flag. " +
                            "target='controller' is the pending alternative: it delivers the edit " +
                            "as a pending online edit for the engineer to accept or cancel " +
                            "(requires LGX_ENABLE_LIVE_WRITES=1). No import was attempted.",
                        Hint:
                            $"Set the {Capabilities.EnvVarLiveFinalize} environment variable to 1 " +
                            $"(together with {Capabilities.EnvVar}=1) to enable target='live', or " +
                            $"use target='controller' for a pending edit. " +
                            $"Client command: {Capabilities.ClientCommandHintLiveFinalize}"));
            }

            // "live" reaches the physical controller too, so it is gated here.
            bool isControllerTarget =
                string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, "live",       StringComparison.OrdinalIgnoreCase);
            if (_capabilities is not { LiveWritesEnabled: true } && isControllerTarget)
            {
                AuditLogger.LogCapabilityRefusal("logix_propose_rung_edit", Capabilities.EnvVar);
                LogGateRefusal("CapabilityDisabled");
                return JsonSerializer.Serialize(
                    new CapabilityDisabledError(
                        Error:
                            "logix_propose_rung_edit with target='controller' refused: " +
                            "LGX_ENABLE_LIVE_WRITES is not set. " +
                            "Publishing edits to the physical controller requires the " +
                            "opt-in capability flag. " +
                            "Workstation-target staging (target='workstation') is still " +
                            "allowed without the opt-in; use target='controller' with " +
                            "LGX_ENABLE_LIVE_WRITES=1 to publish the staged changes.",
                        Hint:
                            $"Set the {Capabilities.EnvVar} environment variable to 1 to " +
                            $"enable live controller mutations. " +
                            $"Client command: {Capabilities.ClientCommandHint}"));
            }
        }

        // ── Confirm mode gate ────────────────────────────────
        // In Confirm mode (the default), a controller-target publish requires
        // confirmedByUser = true.  Without it the call is refused before any SDK
        // session creation or SDK call.  The refusal echoes the session's staged
        // state (rung count, tag names) so the agent can present the pending
        // change set to the user before asking for explicit confirmation.
        // Workstation-target staging is never gated by this check.
        {
            // target="live" makes logic execute, so confirmedByUser is required
            // even when LGX_PUBLISH_MODE=auto.
            bool isLiveTarget =
                string.Equals(target, "live", StringComparison.OrdinalIgnoreCase);
            bool isControllerTarget =
                string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ||
                isLiveTarget;
            if ((isLiveTarget || _capabilities is not { PublishMode: PublishMode.Auto })
                && isControllerTarget && !confirmedByUser)
            {
                // Peek at staged state from the session without making any SDK call.
                // GetNewStagedTagNames filters StagedTagBuffer against PreStageTagCensus
                // so only tags a publish would actually create are echoed — pre-existing
                // context-declaration tags are excluded. Returns null when empty (omitted
                // from JSON via WhenWritingNull on ConfirmationRequiredError.StagedTagNames).
                var peekSession                    = session ?? TryGetActiveSession();
                int stagedRungCount                = peekSession?.StagedRungCount ?? 0;
                IReadOnlyList<string>? stagedTagNames = peekSession?.GetNewStagedTagNames();

                AuditLogger.LogCapabilityRefusal(
                    "logix_propose_rung_edit", Capabilities.EnvVarPublishMode);
                LogGateRefusal("ConfirmationRequired");
                return JsonSerializer.Serialize(
                    new ConfirmationRequiredError(
                        Error:
                            $"logix_propose_rung_edit with target='{(isLiveTarget ? "live" : "controller")}' refused" +
                            (isLiveTarget ? " (target='live' always requires confirmation, even in Auto mode)"
                                          : " in Confirm mode") +
                            ": a controller publish requires the user's explicit " +
                            "approval of this exact staged change set. Present the staged " +
                            "change set (rung count, tag names) to the user, wait for their " +
                            "explicit approval in the conversation, and only then publish. " +
                            "The server cannot verify that approval; never set confirmedByUser " +
                            "on your own judgement or to retry this refusal.",
                        StagedRungCount: stagedRungCount,
                        StagedTagNames:  stagedTagNames));
            }
        }

        // ── Online path: use an SDK session (requires online correlated session) ─
        // When no session is injected:
        //   (1) Reuse the live LogixProjectSession from TagSessionService when an SDK
        //       session is already active — avoids opening a redundant gRPC handle.
        //       The active session is already open and must not be disposed here.
        //   (2) Fall back to creating a new owned session when no active SDK session
        //       is found (e.g. backend="cip" or no logix_connect has been called).
        bool ownSession;
        LogixProjectSession sdkSession;

        if (session is not null)
        {
            // Caller-supplied session (e.g. unit tests): use it without ownership.
            ownSession = false;
            sdkSession = session;
        }
        else if (TryGetActiveSession() is { } activeSession)
        {
            // Active SDK session exists — reuse it. ConnectHandler owns its lifetime.
            ownSession = false;
            sdkSession = activeSession;
        }
        else
        {
            // No active SDK session: create and own a new one for this call.
            // Opening a new session requires a project file path from logix_open_project.
            if (!_projectService.IsLoaded || _projectService.FilePath is null)
                return RefuseRungEdit(
                    new ProposeRungEditError(
                        "No active SDK session and no project file loaded. " +
                        "Call logix_connect to establish an online session, or call " +
                        "logix_open_project to load a project file that can be opened as a new session.",
                        Kind: ErrorKinds.NoActiveSession));

            ownSession = true;
            sdkSession = new LogixProjectSession();
        }

        try
        {
            if (ownSession)
            {
                try
                {
                    await sdkSession.OpenAsync(_projectService.FilePath!, ct);
                }
                catch (TagServiceException ex)
                {
                    return JsonSerializer.Serialize(
                        new ProposeRungEditError(
                            $"SDK session open failed: {ex.Error.Message}"));
                }
            }

            // Derive the write-gate policy from the loaded project so the handler can
            // evaluate every write operand in the proposed fragment before any SDK call.
            var onlinePolicy = _projectService.Project is { } l5xProject
                ? GateEngine.DerivePolicy(l5xProject, GateConfigResolver.Current)
                : (WriteGatePolicy?)null;

            // Census of tags the controller already had before this publish:
            // controller-scoped plus every program-scoped tag name from the server's
            // already-uploaded project state, mirroring logix_find_tags' enumeration.
            // Used by the handler to compute the created-tag delta so a context
            // declaration of a pre-existing tag is never marked CreatedByThisSession.
            IReadOnlyCollection<string>? preExistingTagNames = _projectService.Project is { } censusProject
                ? censusProject.Tags.Select(t => t.Name)
                    .Concat(censusProject.Programs.SelectMany(p => p.Tags.Select(t => t.Name)))
                    .ToList()
                : null;

            // ExternalAccess per census tag, from the same already-loaded export (no SDK call).
            IReadOnlyDictionary<string, string>? preExistingTagAccess = _projectService.Project is { } accessProject
                ? BuildTagAccessCensus(accessProject)
                : null;

            var result = await ProposeRungEditHandler.ExecuteAsync(
                routineXPath, insertPosition, replaceCount, l5xFragment, sdkSession, onlinePolicy, target,
                preExistingTagNames, ct, confirmedByUser, expectedRungText,
                rungs, tags, preExistingTagAccess,
                presetReader: TagRouteResolver.Backend(_tagSessionService.Service),
                verifyOnStage: verifyOnStage,
                tagCensus: _projectService.Project is { } declProject ? BuildTagDeclarationCensus(declProject, _tagSessionService.ControllerImage) : null);

            // ── Record session-created tags ────────────────────────────────────
            // When the controller path creates tags, record them in the session
            // registry so that logix_write denies writes to them for the lifetime
            // of this session (DenyReason.CreatedByThisSession).  A short proposal
            // ID ties each batch of tags back to the propose call that created them.
            if (result is ProposeRungEditResult { TagsCreated: { Count: > 0 } tagsCreated })
            {
                var proposalId = Guid.NewGuid().ToString("N")[..12];
                _tagSessionService.RecordCreatedTags(
                    tagsCreated.Select(e => e.TagName),
                    proposalId,
                    zone: DiffAgainstControllerHandler.RoutineKeyFromXPath(routineXPath));

                // Refresh the session's type lookup from a partial re-export of the created tags, so a
                // read of one (over CIP or the SDK) is typed at once rather than TypeUnknown.
                await _tagSessionService.RefreshTypesForCreatedTagsAsync(
                    tagsCreated.Select(e => e.TagName), ct);
            }

            // ── Any rung proposal (stage, replace, publish) changes the session image ──
            // The affected program is re-exported at once, so includePending sees the staged or published rung
            // without a reconnect.
            if (result is ProposeRungEditResult)
            {
                _tagSessionService.MarkImageStale(
                    DiffAgainstControllerHandler.RoutineKeyFromXPath(routineXPath)?.Split('/', 2)[0],
                    controllerTags: result is ProposeRungEditResult { TagsCreated: { Count: > 0 } });
                // The export is the only writer of rungs into the image, so refresh before the response returns
                // and the next read of any kind sees the zone exactly as the SDK exports it. A failed refresh leaves the
                // image marked stale with the export error (ImageStaleNote); the publish itself is not failed.
                await _tagSessionService.RefreshControllerImageAsync(ct);
            }

            // ── Mark the session's controller image stale ───────────
            // A live rung edit (Finalized) or a tag created on the controller changes the
            // running controller after the image taken at logix_connect time.
            if (result is ProposeRungEditResult liveResult &&
                (liveResult.Finalized == true || liveResult.TagsCreated is { Count: > 0 }))
                _tagSessionService.RecordLiveEdit(
                    liveResult.Finalized == true
                        ? DiffAgainstControllerHandler.RoutineKeyFromXPath(routineXPath)
                        : null,
                    liveResult.TagsCreated?.Select(e => e.TagName));

            // ── Remember the controller publish (StudioConfirmWait) ─────────────
            // A lifecycle verb on this routine within 30 s runs while Studio is still digesting
            // the publish, so the driver is told to wait longer for its confirmation dialog.
            if (result is ProposeRungEditResult { Status: "proposed" or "live" } &&
                DiffAgainstControllerHandler.RoutineKeyFromXPath(routineXPath) is { } publishedKey &&
                publishedKey.Split('/', 2) is { Length: 2 } publishedParts)
                _confirmWait.Record(publishedParts[0], publishedParts[1]);

            // ── Stamp publishMode on every ProposeRungEditResult ───────────────
            // Every publish response includes the server's current publish mode so
            // clients can know whether a Confirm or Auto policy was in effect for
            // this call without querying capabilities separately.
            var publishModeStr = _capabilities?.PublishMode == PublishMode.Auto ? "auto" : "confirm";
            if (result is ProposeRungEditResult propResult)
                result = propResult with
                {
                    PublishMode = publishModeStr,
                    Warnings    = (mnemonicWarnings ?? []).Concat(propResult.Warnings ?? []).ToList() is { Count: > 0 } merged
                        ? merged : null,
                };

            return JsonSerializer.Serialize(result);
        }
        finally
        {
            if (ownSession)
                sdkSession.Dispose();
        }
    }

    /// <summary>
    /// Proposes rung edits to the currently open Logix Designer project by
    /// importing the supplied L5X fragment at the specified position in the
    /// target routine.
    ///
    /// <para>
    /// <b>Online mode</b> (default, <c>offline=false</c>):
    /// Uses <c>PartialImportRungsFromXmlFileAsync</c> with <c>PartialImportOption.LeaveEdits</c>.
    /// The imported rungs are placed in the pending-edits state for operator review in
    /// Studio 5000 Logix Designer before they go live.  Requires an online correlated
    /// session — the session must already be connected to a live controller.
    /// </para>
    ///
    /// <para>
    /// <b>Offline mode</b> (<c>offline=true</c>):
    /// Applies the rung edit directly to the L5X project file on disk — no controller is
    /// contacted and no Logix Designer session is required.  Returns
    /// <c>status="saved-to-file"</c> and includes the path of the modified file in the
    /// response.  Offline mode is never activated by fallback — callers must request it
    /// explicitly by passing <c>offline=true</c>.
    /// </para>
    ///
    /// <para>
    /// The write-gate policy is enforced in both modes: fragments that write denied tags
    /// are rejected before any file I/O or SDK call.
    /// </para>
    /// </summary>
    [McpServerTool(Name = "logix_propose_rung_edit")]
    [Description(
        "ADVISORY PROPOSAL: every edit submitted by this tool is advisory. " +
        "Nothing executes on the controller until the operator explicitly accepts " +
        "the proposal in Logix Designer — this tool does not make that decision. " +
        "Lifecycle: stage (target='workstation') -> publish (target='controller'; the rung sits " +
        "pending on the controller — logix_pending_edits reports Type=\"I\", not executing) -> " +
        "accept or cancel. With LGX_ENABLE_STUDIO_AUTOMATION=1 and the engineer's go-ahead, call " +
        "logix_accept_pending_edits or logix_cancel_pending_edits (confirm=true) and verify with " +
        "logix_pending_edits and a read; otherwise the engineer accepts or cancels in Logix " +
        "Designer (Logic -> Online Edits). Only then can the next target='controller' " +
        "publish happen. The Logix Designer SDK itself has NO accept, assemble, or cancel call " +
        "for an existing pending zone: logix_finalize_edits can never assemble one — it always " +
        "refuses with kind 'PendingZoneStudioOnly' while a zone exists. " +
        "One publish per accept/cancel cycle: the SDK refuses every further " +
        "import into the program until the zone is resolved; stage further edits with " +
        "target='workstation' meanwhile. Tags declared by a publish are created on the " +
        "controller immediately and permanently, even though the rungs stay pending, and " +
        "cancelling the rungs does not remove them. Created tags are External Access Read Only: " +
        "readable, and not writable through lgx-mcp or any CIP client until an engineer sets " +
        "them to Read/Write in Logix Designer (External Access is the engineer's write grant; " +
        "this tool never changes an existing tag's External Access and refuses a fragment that " +
        "tries, kind 'ExternalAccessChangeRefused'; a caller-fragment tag declaration for a new " +
        "tag is forced to Read Only and listed in 'externalAccessForced'; the exception is a " +
        "data type Logix rejects as Read Only, HMIBC, which is created Read/Write and listed " +
        "in 'externalAccessForced' with the reason). " +
        "This session also may not write them (CreatedByThisSession). " +
        "Call logix_pending_edits at any point to read the current " +
        "edit-zone state. " +
        "The gate analysis attached to each response exists to inform the operator's decision, " +
        "not to substitute for it. The agent's judgment is advisory; a human operator reviews " +
        "every proposed edit before it takes effect on the running system. " +
        "Applies rung edits to the open Logix Designer project by importing an L5X " +
        "fragment at the specified rung position in the target routine. " +
        "Two modes are available, selected by the offline parameter: " +
        "offline=false (default, online mode): calls PartialImportRungsFromXmlFileAsync; " +
        "imported rungs are left awaiting operator review and are never assembled, so the " +
        "controller does not begin executing them; requires an online correlated session " +
        "connected to a live controller. Where the edit lands is chosen by the target " +
        "parameter, described below; this tool never finalises or assembles edits. " +
        "offline=true (offline mode): writes the rung edit directly to the L5X project file on " +
        "disk without contacting a controller or opening a Logix Designer session; the change is " +
        "immediately durable on disk; returns status='saved-to-file' with the modified file path; " +
        "never uses the word proposed and does not suggest checking Studio for pending edits; " +
        "offline mode is never activated by fallback and must be requested explicitly. " +
        "In online mode, the target parameter controls where the edit lands: " +
        "TO TRY A CHANGE AND BACK IT OUT, use target='live' (needs LGX_ENABLE_LIVE_FINALIZE) and then undo — " +
        "that is SDK-only and leaves no zone behind. " +
        "target='controller' is ONLY for when the engineer asks for a pending edit to review and accept in Studio; " +
        "do not use it to try a change. A pending zone blocks EVERY further import into that program until " +
        "Studio resolves it (accept or cancel). There is no override parameter. " +
        "A successful target='controller' result carries a zoneNote saying so. " +
        "With target='controller' the edit is placed as accepted-but-not-assembled on the physical controller and is immediately " +
        "visible to any Logix Designer session that is online with the controller; " +
        "the operator must still accept the assembled program in Studio 5000 before the new logic " +
        "executes — no code change takes effect without explicit operator action. " +
        "TAG CREATION IS LIVE: any tag declared by the published fragment (directly, or merged in " +
        "from the workstation staging buffer) that does not already exist on the controller is " +
        "created there IMMEDIATELY at publish time — this happens as soon as the target='controller' " +
        "call returns, not when the operator later accepts the pending rungs in Studio 5000. Created " +
        "tags are OUTSIDE the pending-edit zone: unlike the rungs, they are not part of what the " +
        "operator accepts or cancels, and cancelling the pending rung edit in Studio does NOT remove " +
        "them. The response's tagsCreated field discloses exactly which tags were created this way. " +
        "target='workstation' (or omit, default): STAGING. The edit is held in the server's " +
        "in-memory project image and reaches no controller and no other Logix Designer session. " +
        "Staged edits ACCUMULATE — call this repeatedly to build up a multi-rung change set, " +
        "revising as you go, with nothing visible to the operator while you work. " +
        "To build a change set and then publish it in one reviewable batch: call with " +
        "target='workstation' for each edit, then ONCE with target='controller' — that final call " +
        "sends every staged edit to the controller together, not just its own. " +
        "Prefer this over several separate target='controller' calls: the controller accepts only " +
        "one outstanding batch, so a second target='controller' call is rejected until the operator " +
        "accepts or cancels the first. Staging also lets the operator review one coherent change " +
        "rather than rungs arriving piecemeal. " +
        "FINAL PUBLISH NEEDS NO FRAGMENT: once staged with target='workstation', publish ONCE with " +
        "target='controller' and OMIT l5xFragment (or pass an empty string) — this sends the staged " +
        "buffer only (the accumulated staged rungs plus any buffered tags), never re-sending or " +
        "duplicating rungs already staged. Omitting l5xFragment is accepted ONLY for target='controller'; " +
        "target='workstation' (and offline=true) still require a non-empty l5xFragment. Supplying a " +
        "non-empty l5xFragment on a target='controller' publish is still supported — it is merged in " +
        "alongside the staged buffer as before. " +
        "The write-gate policy is enforced in both modes: fragments that write denied tags are " +
        "rejected before any file I/O or SDK call. " +
        "MNEMONIC SET: rung text must use the instruction names of the target's mnemonic set " +
        "(Logix Designer v36 and later use the IEC names such as LT, MOVE, SQRT; v35 and earlier use LES, MOV, SQR; " +
        "logix_ping and logix_connect report mnemonicSet and targetMajorRevision). " +
        "Before anything is imported or staged, on every target, the server refuses rung text that uses the other set " +
        "(kind 'WrongMnemonicSet') and returns, per offending rung, found, expected and suggestedText " +
        "(the rung with only those names replaced); resend the suggestedText in the same call. " +
        "Nothing is imported, staged or rewritten by the server. It likewise refuses, as 'UnsupportedOnRevision', " +
        "the ASCII serial instructions on v37 and later. " +
        "When the target's set is unknown the check is skipped and the result carries a warning. " +
        "See lgx-instruction-reference, section \"Versions\". " +
        "Canonical workflow (export → edit → import): " +
        "call logix_export_rungs to obtain a valid l5x fragment for the target routine, " +
        "then edit the exported l5x text as needed, " +
        "then pass the (modified) l5x text as l5xFragment to this tool. " +
        "The l5x field returned by logix_export_rungs is directly usable as l5xFragment " +
        "without modification — it already has the required RSLogix5000Content envelope. " +
        "routineXPath must be the Logix SDK XPath string that identifies the target routine, e.g. " +
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']. " +
        "insertPosition is the zero-based rung index at which the import is inserted. " +
        "In the result of a controller publish, insertPosition is the first index the call's routine's rungs landed at; " +
        "when MORE THAN ONE routine is published the top-level insertPosition is null and each routine's position is " +
        "reported in publishedRoutines[i].insertPosition and publishedRoutines[i].landedIndices (a single-routine " +
        "publish is unchanged). " +
        "replaceCount is the number of existing rungs to replace starting at insertPosition (0 = pure insert). " +
        "Returns a descriptive error object when no project is loaded, the routine XPath is blank, " +
        "l5xFragment is blank on any target other than 'controller', the SDK session cannot be " +
        "opened, or the import call fails. " +
        "Online mode requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed " +
        "Studio 5000 or FactoryTalk Logix Echo installation on the host. " +
        "Call logix_connect first to establish a session, then call logix_open_project. " +
        "Before your first edit, load the playbook: logix_get_skill(\"lgx-propose-rung-change\").")]
    public async Task<string> ProposeRungEdit(
        [Description(
            "Logix Designer SDK XPath identifying the target routine, e.g. " +
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']. " +
            "Must not be empty.")]
        string routineXPath,
        [Description(
            "Integer (JSON number, 0 or more; a string or null is refused with kind 'InvalidArguments' naming the parameter). " +
            "Zero-based rung position at which the imported rungs are inserted. " +
            "A value of 0 inserts before the first rung.")]
        object? insertPosition,
        [Description(
            "Integer (JSON number, 0 or more; a string or null is refused with kind 'InvalidArguments' naming the parameter). " +
            "Number of existing rungs to replace starting at insertPosition. " +
            "Pass 0 to insert without replacing any rungs.")]
        object? replaceCount,
        [Description(
            "ADVANCED path — prefer the typed parameters, e.g. " +
            "rungs=[{\"text\":\"XIC(Start)OTE(Lamp);\",\"comment\":\"…\"}], " +
            "tags=[{\"name\":\"Lamp\",\"dataType\":\"BOOL\"}] (the server builds the import envelope). " +
            "Use l5xFragment only to pass the l5x returned by logix_export_rungs with ONLY the " +
            "RLLContent rungs and Tags edited; leave the RSLogix5000Content root and its attributes as exported. " +
            "A hand-written envelope is rebuilt by the server when it can be, or refused. " +
            "Required nesting: RSLogix5000Content > Controller > Programs > Program > " +
            "Routines > Routine > RLLContent > Rung > Text > CDATA. " +
            "The Program Name and Routine Name attributes in the fragment must match the " +
            "target routine identified by routineXPath. " +
            "Must contain at least one Rung element inside RLLContent for target='workstation' " +
            "(or offline=true) — empty is rejected there. " +
            "May be omitted, null, or empty ONLY for target='controller' or 'live': this publishes the " +
            "staged buffer only (previously staged rungs plus any buffered tags) without merging " +
            "in any new rungs — the recommended way to send the final publish of a staged change " +
            "set. An export envelope with an empty RLLContent is accepted the same way.")]
        string? l5xFragment = null,
        [Description(
            "When true, applies the rung edit directly to the L5X project file on disk " +
            "without contacting a controller or opening a Logix Designer session. " +
            "Returns status='saved-to-file' with the modified file path. " +
            "Offline mode must be requested explicitly — it is never activated by fallback. " +
            "When false (default), requires an online correlated session connected to a live controller.")]
        bool offline = false,
        [Description(
            "Controls where the online rung edit lands after import. " +
            "Use 'live' to publish AND finalize in one SDK call: the edit executes " +
            "on the controller immediately and leaves nothing pending. It uses the same " +
            "fragment, tag-merge and tagsCreated handling as 'controller', requires " +
            "LGX_ENABLE_LIVE_WRITES and confirmedByUser=true (even when LGX_PUBLISH_MODE=auto), " +
            "and is only valid on a program with nothing pending. It also requires LGX_ENABLE_LIVE_FINALIZE=1, " +
            "an empty staged-rung buffer (publish staged rungs first, or discard them with logix_clear_staged, which also removes them from the session image; staged tags are merged), " +
            "and expectedRungText when replaceCount > 0. " +
            "LIVE RUNG DELETE: target='live' with an explicit empty rungs list (rungs: [] — " +
            "omitting rungs is NOT a delete) and replaceCount > 0 deletes replaceCount rungs at " +
            "insertPosition via an empty-RLLContent FinalizeEdits import. expectedRungText is " +
            "required with one entry per deleted rung (StaleTarget on mismatch). The result has " +
            "status='live', deletedRungs [{number,text,comment}] and afterRungs; the server " +
            "verifies the routine has before-replaceCount rungs, all normal, with the following " +
            "rungs shifted up unchanged (else LiveFinalizeNotConfirmed). Tags the change created " +
            "stay on the controller. " +
            "UNDO (every successful live result): the result carries an `undo` block " +
            "{routineXPath, insertPosition, replaceCount, rungs:[{text,comment}], expectedRungText}. " +
            "Undo is one more live call: pass the block back to logix_propose_rung_edit with target='live', confirmedByUser=true and " +
            "those same values (rungs = the pre-image, an empty list deletes the inserted rungs; " +
            "comment '' means the rung had no comment). It is an ordinary live call guarded by the " +
            "stale check (StaleTarget if the routine changed since) and returns its own undo block " +
            "(redo). The block lives in the response only; the server stores nothing. " +
            "Tags the change created (see tagsCreated) STAY on the controller after an undo: that is " +
            "normal, because the SDK cannot delete tags online; only the rungs are reverted. " +
            "Refusals happen before any import: " +
            "kinds CapabilityDisabled, ConfirmationRequired, StagedRungsPresent, PendingZoneExists " +
            "(resolve with logix_accept_pending_edits / logix_cancel_pending_edits), StaleTarget. " +
            "To try a change and back it out, use 'live' (with LGX_ENABLE_LIVE_FINALIZE) plus undo — " +
            "SDK only. Use 'controller' ONLY when the engineer asks for a pending edit to review " +
            "and accept in Studio; a pending zone blocks every further import into that program " +
            "until Studio resolves it (there is no override parameter), and a successful " +
            "'controller' result carries a zoneNote saying so. With 'controller' the edit is sent as accepted-but-not-assembled to the physical " +
            "controller and is immediately visible to any Logix Designer session that is online " +
            "with the controller. Declared tags are created on the controller immediately at " +
            "publish time (not when the operator later accepts the rungs) and are outside the " +
            "pending-edit zone — only the rungs are pending; created tags are permanent even if " +
            "the pending rung edit is later cancelled. See tagsCreated in the response. " +
            "Use 'workstation' (or omit, default) for a local-only proposal: rungs are held in " +
            "the server's in-memory project image and tag declarations are held in the session's " +
            "staging buffer — neither reaches the controller or any other session. " +
            "Omitting this parameter (or passing null) is identical to 'workstation'. " +
            "Ignored when offline=true.")]
        string? target = null,
        [Description(
            "Required for target='controller' when LGX_PUBLISH_MODE is 'confirm' (the default). " +
            "Records that the user explicitly approved this exact staged change set in the " +
            "conversation. The server cannot verify that approval: the agent must set this " +
            "to true only after the user has explicitly approved the staged change set, and " +
            "must never set it on its own judgement or to retry a refusal. " +
            "When false (or omitted) in Confirm mode the call is refused with " +
            "kind='ConfirmationRequired' and the response echoes the staged rung count and " +
            "tag names so the agent can present the pending change set to the user, wait for " +
            "explicit approval, and only then publish. " +
            "Has no effect for target='workstation' or when LGX_PUBLISH_MODE='auto' — EXCEPT " +
            "target='live', which always requires confirmedByUser=true, even when " +
            "LGX_PUBLISH_MODE='auto'. " +
            "Ignored when offline=true.")]
        bool confirmedByUser = false,
        [Description(
            "target='live' only; required when replaceCount > 0, optional for pure inserts. " +
            "One entry per replaced rung (rungs insertPosition..insertPosition+replaceCount-1, " +
            "in order): the rung text you last read from logix_export_rungs. The server " +
            "re-exports those rungs, normalizes both sides identically, and refuses the call " +
            "(kind='StaleTarget', both texts returned) when they differ, so a live replace " +
            "can never overwrite a rung that changed since you read it. Ignored for other " +
            "targets and when offline=true.")]
        string[]? expectedRungText = null,
        [Description(
            "Typed rungs to import: [{\"text\": \"XIC(Start)OTE(Lamp);\", \"comment\": \"optional\"}]. " +
            "With target='live' and replaceCount > 0, an explicit empty list (rungs: []) deletes " +
            "replaceCount rungs at insertPosition (needs expectedRungText); an omitted rungs is not a delete. " +
            "text is rung text only, never XML (CDATA or <Rung markup is refused with " +
            "kind='InvalidRungText'); a missing trailing ';' is appended. Mutually exclusive with " +
            "l5xFragment (both given: kind='AmbiguousInput', nothing attempted).")]
        RungInput[]? rungs = null,
        [Description(
            "Typed tag declarations: [{\"name\": \"Lamp\", \"dataType\": \"BOOL\", " +
            "\"radix\": optional, \"description\": optional, \"dimensions\": optional}]. " +
            "name must match ^[A-Za-z_][A-Za-z0-9_]{0,39}$ and not contain '__'; dataType must be " +
            "non-empty (otherwise kind='InvalidTag' naming the field). Mutually exclusive with " +
            "l5xFragment. Tags without rungs is a tag-only publish: accepted for " +
            "target='controller' or 'live', refused for target='workstation' and offline=true. With offline=true, tags are refused even alongside rungs (kind='TagsNotSupportedOffline'): the offline path writes rungs only.")]
        TagInput[]? tags = null,
        [Description(
            "Opt-in, target='workstation' only; default false. Staging in the session image is NOT checked by Logix, " +
            "so a stage result always carries stagedIn='session-image' and verified=false (the rung is checked when " +
            "it is published to the controller). With verifyOnStage=true the server first replays the fragment into " +
            "an offline copy of the project and verifies it: verified=true on success, otherwise verified=false with " +
            "kind='StageVerifyFailed' and the rung is NOT staged (the error carries verifyRejectReasons: " +
            "[{mnemonic, hex, text, rungIndex, hint}]); when the replay itself was unavailable the rung is staged " +
            "with verified=false and the reason in verifiedNote. COST: a project save-as, an offline open, an " +
            "import and a build on every stage call, about +12 to 18 s per stage on the bench " +
            "(figure from the task brief / an earlier change, not independently reproduced here; more on a large project, bounded at 60 s). Leave it off for routine staging; use it before a " +
            "publish you cannot afford to have rejected. With target='controller' it is ignored (verifiedNote: " +
            "'ignored: controller publishes are always checked by Logix'); also ignored when offline=true.")]
        bool verifyOnStage = false)
    {
        // The two positions arrive as raw values so a string ("1") or a null is refused by name instead of being
        // coerced or failing the SDK's own binding without saying which argument was wrong.
        if (BindPosition(nameof(insertPosition), insertPosition, out var insertAt) is { } badInsert)
            return badInsert;
        if (BindPosition(nameof(replaceCount), replaceCount, out var replaceN) is { } badReplace)
            return badReplace;

        // Typed-input validation precedes the no-session check so a malformed
        // request is reported as such rather than masked by a session error.
        if (ProposeRungEditHandler.ValidateTypedInput(l5xFragment, rungs, tags, out _)
                is { } earlyTypedInputError)
            return JsonSerializer.Serialize(earlyTypedInputError);

        // ── Online path: retrieve and validate the active SDK session early ────
        // Return a distinct error before reaching the handler's own 'session is
        // not online' guard when no session has been established via logix_connect.
        // Offline mode does not require a session and skips this check entirely.
        //
        // Note: the Confirm mode gate in GetProposeRungEditResponseAsync fires
        // BEFORE any SDK session work, so a ConfirmationRequired refusal is
        // returned even when no session is active, consistent with the LiveWrites
        // gate behaviour.  The early session-null check here would prevent the
        // Confirm gate from firing in the no-session case; we therefore only
        // short-circuit on null session when the Confirm gate would NOT fire.
        LogixProjectSession? liveSession = null;
        if (!offline)
        {
            liveSession = TryGetActiveSession();
            bool isLiveTarget =
                string.Equals(target, "live", StringComparison.OrdinalIgnoreCase);
            bool isControllerTarget =
                string.Equals(target, "controller", StringComparison.OrdinalIgnoreCase) ||
                isLiveTarget;
            // target="live" is gated by the flag and by confirmedByUser regardless of
            // LGX_PUBLISH_MODE, and those refusals must not be masked by the
            // no-session error.
            bool confirmGateWouldFire =
                (isLiveTarget && (_capabilities is not { LiveFinalizeEnabled: true } ||
                                  _capabilities is not { LiveWritesEnabled: true } ||
                                  !confirmedByUser)) ||
                (_capabilities is not { PublishMode: PublishMode.Auto } &&
                 isControllerTarget &&
                 !confirmedByUser);
            if (liveSession is null && !confirmGateWouldFire)
            {
                AuditLogger.LogRungEdit(routineXPath, insertAt, "refused", null,
                    destination: isControllerTarget ? (isLiveTarget ? "live" : "controller") : "workstation",
                    kind: ErrorKinds.NoActiveSession);
                return JsonSerializer.Serialize(
                    new ProposeRungEditError(
                        "No active SDK session — call logix_connect first to establish " +
                        "an online correlated session before proposing rung edits.",
                        Hint: NoActiveSessionHint,
                        Kind: ErrorKinds.NoActiveSession));
            }
        }

        var sw     = Stopwatch.StartNew();
        var result = await GetProposeRungEditResponseAsync(
            routineXPath, insertAt, replaceN, l5xFragment,
            session: liveSession, offline: offline, target: target,
            confirmedByUser: confirmedByUser,
            expectedRungText: expectedRungText,
            rungs: rungs, tags: tags,
            verifyOnStage: verifyOnStage);
        sw.Stop();
        TrafficLogger.Append(
            "logix_propose_rung_edit",
            requestBytes:  TrafficLogger.MeasureRequestBytes(
                new { routineXPath, insertAt, replaceN, l5xFragment, offline, target, confirmedByUser, expectedRungText, rungs, tags, verifyOnStage }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    /// <summary>
    /// Binds <c>insertPosition</c> / <c>replaceCount</c> of <c>logix_propose_rung_edit</c>: only a whole JSON number from
    /// 0 to <see cref="uint.MaxValue"/> is accepted (the SDK would coerce the string <c>"1"</c> to 1). Anything else,
    /// including null, is refused as <c>InvalidArguments</c> naming the parameter.
    /// </summary>
    /// <param name="name">The parameter name (<c>insertPosition</c> or <c>replaceCount</c>).</param>
    /// <param name="raw">The bound value: a <see cref="JsonElement"/> from the MCP pipeline, or a boxed integer.</param>
    /// <param name="value">The position when the value is valid; otherwise 0.</param>
    /// <returns>The serialized error to return, or <c>null</c> when the value is valid.</returns>
    internal static string? BindPosition(string name, object? raw, out uint value)
    {
        value = 0;
        string? problem = null;
        switch (raw)
        {
            case null:
                problem = "is null; it must be a whole number from 0 upward";
                break;
            case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                problem = "is null; it must be a whole number from 0 upward";
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } n:
                if (n.TryGetUInt32(out var u)) value = u;
                else problem = $"is {n.GetRawText()}; it must be a whole number from 0 to {uint.MaxValue}";
                break;
            case JsonElement { ValueKind: JsonValueKind.String } str:
                problem = $"is the string \"{str.GetString()}\"; it must be a JSON number, not a string";
                break;
            case JsonElement other:
                problem = $"is a JSON {other.ValueKind.ToString().ToLowerInvariant()}; it must be a whole number from 0 upward";
                break;
            case string str:
                problem = $"is the string \"{str}\"; it must be a JSON number, not a string";
                break;
            case byte or ushort or uint or sbyte or short or int or long or ulong:
                var asLong = Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (asLong is >= 0 and <= uint.MaxValue) value = (uint)asLong;
                else problem = $"is {asLong}; it must be a whole number from 0 to {uint.MaxValue}";
                break;
            default:
                problem = $"is a {raw.GetType().Name}; it must be a whole number from 0 upward";
                break;
        }
        if (problem is null) return null;

        return JsonSerializer.Serialize(new
        {
            error = $"logix_propose_rung_edit: argument '{name}' {problem}.",
            kind = ErrorKinds.InvalidArguments,
            tool = "logix_propose_rung_edit",
            parameter = name,
            hint = $"'{name}' must be a whole number from 0 upward (a JSON number, not a string). Fix it and call logix_propose_rung_edit again.",
        });
    }

    // ── logix_pending_edits ───────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the active <see cref="LogixProjectSession"/>, delegates to
    /// <see cref="PendingEditsHandler.ExecuteAsync"/>, then serializes the result.
    /// Uses the same session-selection order as
    /// <see cref="GetExportRungsResponseAsync"/>.
    /// </summary>
    /// <param name="program">Optional program name to scope the read to.</param>
    /// <param name="routine">
    /// Optional routine name to scope the read to. Requires <paramref name="program"/>.
    /// </param>
    /// <param name="session">
    /// Optional pre-opened <see cref="LogixProjectSession"/> for testing.
    /// When <c>null</c> (the default), the active session is retrieved via
    /// <see cref="TryGetActiveSession"/>; an error is returned if none is registered.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetPendingEditsResponseAsync(
        string?              program = null,
        string?              routine = null,
        LogixProjectSession? session = null,
        CancellationToken    ct = default)
    {
        // logix_pending_edits operates on the live SDK session only, exactly like
        // logix_export_rungs — no project file needs to be open (upload route).
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
            return JsonSerializer.Serialize(
                new PendingEditsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_pending_edits.",
                    NoActiveSessionHint,
                    ErrorKinds.NoActiveSession));

        var result = await PendingEditsHandler.ExecuteAsync(program, routine, sdkSession, ct);
        if (result is PendingEditsResult ok)
        {
            var target = TargetRevision.Current(_tagSessionService, _projectService);
            // A live session always has a target: a missing revision is unknown (with a warning), not absent.
            if (ReferenceEquals(target, TargetRevision.NoTarget)) target = TargetRevision.UnknownTarget();
            result = ok with
            {
                FingerprintAgeMs    = _tagSessionService.FingerprintAgeMs,
                TargetMajorRevision = target.MajorRevision,
                MnemonicSet         = target.MnemonicSet,
                Warnings            = target.Warning is { } warning ? [warning] : null,
            };
            if (ok.Connected && ReconcileImage(ok) is { } reconciled)
                result = ((PendingEditsResult)result) with { Reconciled = reconciled };
        }
        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// This change requirement 3, as changed by an earlier change: reconcile on observation. For each program read whose export shows
    /// a zone the session image does not hold (a program this session did not publish to, or a zone another process
    /// changed), marks the image stale with the existing reason, marks the program for refresh (the next successful
    /// refresh clears the stale mark) and appends one <c>image_reconciled</c> audit line. Never throws.
    /// </summary>
    /// <returns>The distinct outcomes joined by a comma, or <c>null</c> when nothing was reconciled.</returns>
    private string? ReconcileImage(PendingEditsResult read)
    {
        var outcomes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var group in read.Routines.GroupBy(r => r.Program, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var exported = group
                    .Where(r => r.ExportedRungs is not null)
                    .Select(r => new SessionImageState.ZoneRoutine(r.Routine, r.ExportedRungs!))
                    .ToList();
                if (exported.Count != group.Count()) continue;
                if (SessionImageState.Reconcile(_tagSessionService, group.Key, exported) is not { } done) continue;

                _tagSessionService.MarkImageStale(group.Key);
                AuditLogger.LogImageReconciled(done.Program, string.Join(",", done.Routines), done.Outcome);
                outcomes.Add(done.Outcome);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A reconcile failure must not fail the read: the next lint/xref refresh still sees the program as stale.
                SessionImageState.MarkStale(_tagSessionService,
                    $"pending edits of {group.Key} were resolved outside lgx-mcp");
            }
        }
        return outcomes.Count == 0 ? null : string.Join(",", outcomes);
    }

    /// <summary>
    /// Reads the pending-edit (edit-zone) state of one routine, one program, or
    /// every program's ladder routines by exporting them and classifying every rung's L5X
    /// <c>Type</c> attribute. Strictly read-only — never calls any
    /// <c>PartialImport*</c> SDK method.
    /// </summary>
    [McpServerTool(Name = "logix_pending_edits")]
    [Description(
        "Reads the pending-edit state of a routine, a program, or every program's ladder routines " +
        "on the active online SDK session, by exporting them (PartialExportToXmlFileAsync) " +
        "and classifying every rung's L5X Type attribute. Strictly read-only: it never " +
        "calls any PartialImport* SDK method and never touches the controller's program " +
        "or edit state. The session image may be updated: after the read, for each program whose " +
        "recorded zone is gone from the controller, the routine is compared with the record — equal to " +
        "the recorded rungs means the edit is applied to the session image (accepted), equal to the " +
        "pre-edit content means it is restored (cancelled), neither means the image is marked stale " +
        "with a reason. The response carries 'reconciled' and one image_reconciled audit line is written. " +
        "No project needs to be open — it works over the upload route " +
        "the same way logix_export_rungs does; call logix_connect first. " +
        "Rung Type code table (from the L5X export schema): " +
        "'N' = Normal, no pending edit (also the default when Type is absent). " +
        "'I'/'D'/'R'/'IR' = Insert/Delete/Replace/InsertReplace — pending, untested " +
        "(offline or LeaveEdits-image edit zone). " +
        "'e'/'er'/'dN' = PendingInsertRung/PendingReplaceRung/PendingDeleteRung — tested: " +
        "an edit currently live in the online session (bench-confirmed on this controller). " +
        "'rR'/'rI'/'rN' = PendingReplace/PendingReplaceInsert/PendingReplaceNormal — accepted: " +
        "an edit accepted into the assembly-pending track (via AcceptEdits), not yet finalized. " +
        "Any Type code not in this table is conservatively reported as 'pending', never " +
        "silently dropped as 'none'. " +
        "KNOWN LIMITATION (bench-confirmed): a rung published with PartialImportOption." +
        "AcceptEdits has been observed to export as plain 'N' with no distinguishing marker " +
        "at all — this tool cannot always tell an accepted-but-unassembled edit apart from a " +
        "rung that was never edited, and will report state='none' in that case. A 'none' " +
        "result is therefore not proof the controller has no accepted edits, only that none " +
        "are visible in this export. " +
        "Returns a JSON object with connected (bool, true only when ReadConnectedStateAsync " +
        "reports Online), controllerMode (string: Run/Program/Test/Faulted, from " +
        "ReadControllerModeAsync), routines (array of {program, routine, rungs, controllerRungs, " +
        "stagedRungs, pendingRungs: [{number, type, text}], state: 'none'|'pending'|'accepted'|" +
        "'tested'}; controllerRungs is the number of rungs the controller holds and rungs is the " +
        "same number; stagedRungs counts rungs this session staged to the workstation target, " +
        "which the export shows as normal rungs and the server subtracts by matching rung text — " +
        "the staged count lives only on the server, not on the controller), staged " +
        "({rungCount, tagNames}: this session's local staging-buffer snapshot — the number " +
        "of rungs staged to the workstation target and not yet published, and the names of " +
        "tag declarations currently held in the session's staging buffer — so a caller can " +
        "inspect what the next target=\"controller\" publish will send before committing), " +
        "and observedAt (UTC timestamp of the snapshot). It also returns targetMajorRevision " +
        "(the target's major revision, or null when unknown) and mnemonicSet ('iec' for 36 and above, " +
        "'legacy' for 35 and below, 'unknown' with a warning when the revision is not known). " +
        "Omitting program reads every program's ladder routines; " +
        "supply program alone to scope to one program; supply both to scope to one routine. " +
        "Requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed " +
        "Studio 5000 or FactoryTalk Logix Echo installation on the host. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public async Task<string> PendingEdits(
        [Description(
            "Optional program name to scope the read to. Omit (or pass null) to read " +
            "every program on the controller.")]
        string? program = null,
        [Description(
            "Optional routine name to scope the read to. Requires program to also be " +
            "supplied. Omit (or pass null) to read every routine in the scoped program(s).")]
        string? routine = null)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetPendingEditsResponseAsync(program, routine);
        StampZoneTestedFrom(result);
        sw.Stop();
        TrafficLogger.Append(
            "logix_pending_edits",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, routine }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    // ── logix_clear_staged ────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the active <see cref="LogixProjectSession"/>, delegates to
    /// <see cref="ClearStagedHandler.ExecuteAsync"/>, then serializes the result. No auto-session creation.
    /// </summary>
    /// <param name="routine">Optional routine name or routine XPath; omit to clear every routine.</param>
    /// <param name="program">Optional program name narrowing a bare routine name.</param>
    /// <param name="session">Optional pre-opened session for testing; defaults to the active session.</param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetClearStagedResponseAsync(
        string?              routine = null,
        string?              program = null,
        LogixProjectSession? session = null)
    {
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused("logix_clear_staged", ErrorKinds.NoActiveSession, program: program, routine: routine);
            return JsonSerializer.Serialize(
                new ClearStagedError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_clear_staged.",
                    NoActiveSessionHint,
                    ErrorKinds.NoActiveSession));
        }

        var result = await ClearStagedHandler.ExecuteAsync(sdkSession, routine, program).ConfigureAwait(false);
        return result is ClearStagedError err
            ? JsonSerializer.Serialize(err)
            : JsonSerializer.Serialize((ClearStagedResult)result);
    }

    /// <summary>
    /// Discards the session's staged rungs and staged tag declarations for one routine, or for all routines.
    /// Also returns the session image to its pre-stage state and verifies it; the controller is never written.
    /// </summary>
    [McpServerTool(Name = "logix_clear_staged")]
    [Description(
        "Clears this session's staged rungs and staged tag declarations (what logix_propose_rung_edit with " +
        "target=\"workstation\" accumulated and a target=\"controller\" publish has not yet sent) for one routine, " +
        "or for every routine when routine is omitted. Use it to start over after a StagedPositionConflict, " +
        "a refused publish (\"the staged buffer holds conflicting buckets\"), or a StagedRungsPresent refusal of " +
        "target=\"live\". The staged rungs also sit in the SDK session image (imported with LeaveEdits), so on an online " +
        "session the clear rebuilds that image from the controller (the upload logix_connect performs), replays the " +
        "staged rungs of any routine it keeps, then re-exports each cleared routine and compares it rung by rung " +
        "(count, types, text) with the routine as exported just before its first stage (a routine with no such record " +
        "only gets a check that no staged rung is left, and the note says so): a match returns imageRestored=true. Anything else returns kind ClearIncomplete with leftoverRungs and the " +
        "hint to reconnect with logix_connect; until a clear verifies the image, stage and publish into that program " +
        "are refused. It sends nothing to the controller and changes nothing on it (controllerTouched is always " +
        "false). A session that is not online cannot stage, so no staged rung is ever written into the project file you " +
        "opened. Staged tags are not recorded per " +
        "routine, so a single-routine clear removes them only when no other routine still holds staged rungs; " +
        "otherwise they are kept and reported as tagsRetained. Returns status ('cleared' or 'nothing_staged'), scope, " +
        "clearedRoutines ([{routineXPath, buckets, rungs}]), clearedRungCount, clearedTagNames, tagsRetained, " +
        "controllerTouched, imageRestored, replayedBuckets and a note that says what was removed from where, and " +
        "writes a staged_cleared audit line (with imageRestored and replayedBuckets). Check logix_pending_edits afterwards. " +
        "Requires an open session (logix_connect first) and the Logix Designer SDK build (EnableLogixSdk=true).")]
    public async Task<string> ClearStaged(
        [Description(
            "Optional routine to clear: a routine name (e.g. MainRoutine) or a full routine XPath. " +
            "Omit to clear the staged rungs and tags of every routine.")]
        string? routine = null,
        [Description(
            "Optional program name that narrows a bare routine name when several programs have a routine " +
            "of that name. Ignored when routine is an XPath.")]
        string? program = null)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetClearStagedResponseAsync(routine, program);
        _tagSessionService.MarkImageStale(program); // clearing staged rungs changes the session image
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            "logix_clear_staged",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { routine, program }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    // ── logix_finalize_edits ──────────────────────────────────────────────────

    /// <summary>
    /// Retrieves the active <see cref="LogixProjectSession"/>, delegates to
    /// <see cref="FinalizeEditsHandler.ExecuteAsync"/>, then serializes the
    /// result. Uses the same session-selection order as
    /// <see cref="GetExportRungsResponseAsync"/> and <see cref="GetPendingEditsResponseAsync"/>
    /// — no auto-session creation.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to finalize. Required.</param>
    /// <param name="confirm">
    /// Must be <c>true</c> to proceed. Defaults to <c>false</c>, which always refuses.
    /// </param>
    /// <param name="session">
    /// Optional pre-opened <see cref="LogixProjectSession"/> for testing.
    /// When <c>null</c> (the default), the active session is retrieved via
    /// <see cref="TryGetActiveSession"/>; an error is returned if none is registered.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetFinalizeEditsResponseAsync(
        string               program,
        string               routine,
        bool                 confirm = false,
        LogixProjectSession? session = null,
        CancellationToken    ct = default)
    {
        // logix_finalize_edits operates on the live SDK session only, exactly like
        // logix_pending_edits and logix_export_rungs — no project file needs to be
        // open (upload route).
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused("logix_finalize_edits", ErrorKinds.NoActiveSession, program: program, routine: routine);
            return JsonSerializer.Serialize(
                new FinalizeEditsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_finalize_edits.",
                    Kind: ErrorKinds.NoActiveSession));
        }

        var result = await FinalizeEditsHandler.ExecuteAsync(
            program, routine, confirm, sdkSession, ct,
            studioAutomationEnabled: _capabilities is { StudioAutomationEnabled: true });
        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Attempts to finalize a single routine's pending edit zone on the active
    /// online SDK session. As of an earlier change this always refuses when
    /// there is an existing pending edit zone to finalize: the Logix Designer SDK
    /// has no accept, assemble, or cancel call for one, so this tool never calls
    /// <c>PartialImportOption.FinalizeEdits</c> against an existing zone. Gated:
    /// refuses unless <c>confirm=true</c>, refuses when <c>logix_pending_edits</c>
    /// reports <c>state='none'</c> for the target routine ("nothing pending"), and
    /// otherwise refuses with kind <c>PendingZoneStudioOnly</c>.
    ///
    /// <para>
    /// This tool is not the only <c>PartialImportOption.FinalizeEdits</c> path in
    /// this server: <c>logix_propose_rung_edit</c> with <c>target="live"</c> also
    /// dispatches <c>FinalizeEdits</c>, publishing and finalizing in one
    /// call, but never against an existing pending zone. <c>logix_connect</c> and
    /// the other <c>logix_propose_rung_edit</c> targets must never do so.
    /// This tool itself no longer reaches that call for any input.
    /// </para>
    /// </summary>
    [McpServerTool(Name = "logix_finalize_edits")]
    [Description(
        "Finalizing an EXISTING pending edit zone is not possible through this tool: the SDK " +
        "cannot assemble an existing zone — the Logix Designer SDK " +
        "has NO accept, assemble, or cancel call for a zone already on the controller. " +
        "PartialImportOption.FinalizeEdits is an import option that finalizes " +
        "the rungs of the import that carries it — it cannot touch a zone created by a " +
        "prior import. This tool NEVER calls the SDK against an existing pending edit zone; " +
        "it always returns a structured refusal instead. GATING: (1) refuses unless " +
        "confirm=true is passed. (2) Calls logix_pending_edits for the target routine and " +
        "refuses when its state is 'none' (\"nothing pending\" — either genuinely nothing " +
        "pending, or a known SDK limitation where an accepted-but-unassembled edit " +
        "exports as plain 'N'). (3) For every other state, refuses with kind " +
        "'PendingZoneStudioOnly', naming the routine, every pending rung (number, type, " +
        "text), and the valid transitions: with LGX_ENABLE_STUDIO_AUTOMATION=1, call " +
        "logix_accept_pending_edits or logix_cancel_pending_edits; otherwise accept or cancel " +
        "the zone in Logix Designer (Logic -> Online Edits). Every refusal ends with: to go " +
        "live in one step when nothing is pending, use `logix_propose_rung_edit target=\"live\"`. " +
        "While a routine's pending edit zone exists, the SDK refuses every further import " +
        "into that program — one publish per Studio accept/cancel cycle. Returns a JSON " +
        "error object with error, before (pending-edit snapshot), and kind. No project needs " +
        "to be open — it works over the upload route the same way logix_pending_edits does; " +
        "call logix_connect first. Requires the Logix Designer SDK build " +
        "(EnableLogixSdk=true) and a licensed Studio 5000 or FactoryTalk Logix Echo " +
        "installation on the host. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public async Task<string> FinalizeEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to finalize. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses. Note: even " +
            "with confirm=true, this tool refuses whenever the routine has an existing " +
            "pending edit zone (state != 'none') — it never assembles one; resolve it with " +
            "logix_accept_pending_edits / logix_cancel_pending_edits (needs " +
            "LGX_ENABLE_STUDIO_AUTOMATION=1) or in Logix Designer (Logic -> Online Edits).")]
        bool confirm = false)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetFinalizeEditsResponseAsync(program, routine, confirm);
        _tagSessionService.MarkImageStale(program); // finalize changes the controller
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            "logix_finalize_edits",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, routine, confirm }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    // ── logix_cancel_pending_edits ────────────────────────────────────────────

    /// <summary>
    /// Retrieves the active <see cref="LogixProjectSession"/>, delegates to
    /// <see cref="CancelPendingEditsHandler.ExecuteAsync"/> — wiring its
    /// <c>pendingEditsReader</c> to <see cref="PendingEditsHandler.ExecuteAsync"/>
    /// against that session and its <c>session1Runner</c> to
    /// <see cref="Session1ScriptRunner.RunAsync"/> — then serializes the result.
    /// Uses the same session-selection order as
    /// <see cref="GetExportRungsResponseAsync"/>, <see cref="GetPendingEditsResponseAsync"/>,
    /// and <see cref="GetFinalizeEditsResponseAsync"/> — no auto-session creation.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to cancel edits for. Required.</param>
    /// <param name="confirm">
    /// Must be <c>true</c> to proceed. Defaults to <c>false</c>, which always refuses.
    /// </param>
    /// <param name="session">
    /// Optional pre-opened <see cref="LogixProjectSession"/> for testing.
    /// When <c>null</c> (the default), the active session is retrieved via
    /// <see cref="TryGetActiveSession"/>; an error is returned if none is registered.
    /// </param>
    /// <param name="session1Runner">
    /// Optional override for the session-1 script runner, keyed by verb. Defaults to
    /// <see cref="Session1ScriptRunner.RunAsync"/> (bound to verb <c>"cancel"</c>)
    /// when <c>null</c>. Tests supply a fake runner.
    /// </param>
    /// <param name="isStudioInstalled">
    /// Optional override for the Studio-installed probe. Defaults to
    /// <see cref="CancelPendingEditsHandler.DefaultIsStudioInstalled"/> when <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to all async operations.</param>
    /// <param name="probe">
    /// Optional environment probe used to report <c>launchMode</c>/<c>runLevel</c> on
    /// refusals that occur before the runner is invoked (and by the default runner).
    /// Defaults to the real <see cref="EnvironmentProbe"/>.
    /// </param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetCancelPendingEditsResponseAsync(
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm = false,
        LogixProjectSession?                                         session = null,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner = null,
        Func<bool>?                                                  isStudioInstalled = null,
        CancellationToken                                            ct = default,
        IEnvironmentProbe?                                           probe = null)
    {
        // ── LGX_ENABLE_STUDIO_AUTOMATION gate ─────────────────────────────────
        // logix_cancel_pending_edits drives the Studio 5000 GUI in the
        // interactive desktop session — desktop automation that posts Studio's own
        // menu command IDs (no keystrokes, no mouse clicks, no screen coordinates).
        // Refused when the opt-in is not set, before any
        // Studio-installed probe, pending-edit read, or session-1 script call.
        if (_capabilities is not { StudioAutomationEnabled: true })
        {
            AuditLogger.LogCapabilityRefusal("logix_cancel_pending_edits", Capabilities.EnvVarStudio);
            return JsonSerializer.Serialize(
                new CapabilityDisabledError(
                    Error:
                        "logix_cancel_pending_edits refused: LGX_ENABLE_STUDIO_AUTOMATION " +
                        "is not set. This tool drives the Studio 5000 GUI in the interactive " +
                        "desktop session via desktop automation: it posts Studio's own " +
                        "Logic -> Online Edits menu command to the Studio window and answers " +
                        "the confirmation dialog, with no keystrokes, mouse clicks or screen " +
                        "coordinates, so it does not depend on screen resolution or layout. " +
                        "It still acts on the engineer's open Studio session. " +
                        "Set LGX_ENABLE_STUDIO_AUTOMATION=1 to opt in.",
                    Hint:
                        $"Set the {Capabilities.EnvVarStudio} environment variable to 1 to " +
                        "enable Studio GUI-automation tools. " +
                        $"Client command: {Capabilities.ClientCommandHintStudio}"));
        }

        // logix_cancel_pending_edits operates on the live SDK session only,
        // exactly like logix_pending_edits and logix_finalize_edits — no project
        // file needs to be open (upload route).
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused("logix_cancel_pending_edits", ErrorKinds.NoActiveSession, program: program, routine: routine);
            // The runner is never invoked here; still report how it would launch.
            var (planMode, planLevel) = Session1ScriptRunner.PlanLaunch(probe);
            return JsonSerializer.Serialize(
                new CancelPendingEditsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_cancel_pending_edits.",
                    LaunchMode: planMode,
                    RunLevel:   planLevel,
                    Launched:   false,
                    Kind:       ErrorKinds.NoActiveSession));
        }

        Task<object> PendingEditsReader(string p, string r, CancellationToken token) =>
            PendingEditsHandler.ExecuteAsync(p, r, sdkSession, token);

        var runner = session1Runner ??
            ((verb, token) => RunLifecycleDriverAsync(verb, program, routine, sdkSession, probe, token));

        var result = await CancelPendingEditsHandler.ExecuteAsync(
            program, routine, confirm, PendingEditsReader, runner, isStudioInstalled, ct,
            planLaunch: () => Session1ScriptRunner.PlanLaunch(probe),
            leftoverTagsProvider: () => _tagSessionService.TakeZoneCreatedTagNames(program, routine),
            imageSession: _tagSessionService,
            ledgerSession: sdkSession);

        // A successful cancel discards the program's pending edits,
        // which resolves any recorded PublishMismatch for it.
        if (result is CancelPendingEditsResult)
            sdkSession.ClearPublishMismatchesForProgram(program);

        // The cancel discarded the zone, so write back the original rung comments that an
        // explicit-comment workstation stage overwrote before it was published.
        if (result is CancelPendingEditsResult cancelled)
            result = await RestoreZoneCommentsAsync(sdkSession, program, cancelled, ct);

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// After a successful cancel of a program's pending-edit zone, writes back the original comments of the rungs that
    /// an explicit-comment workstation replace overwrote before the publish.
    /// </summary>
    /// <param name="session">The session that staged and published.</param>
    /// <param name="program">The program whose zone was cancelled (Studio's cancel is program-wide).</param>
    /// <param name="cancelled">The successful cancel result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result, carrying <c>commentsRestored</c> or <c>commentRestoreFailure</c> when there was anything to restore.</returns>
    internal static async Task<CancelPendingEditsResult> RestoreZoneCommentsAsync(
        LogixProjectSession session, string program, CancelPendingEditsResult cancelled, CancellationToken ct)
    {
        var (restored, failure) = await RestoreZoneCommentUndosAsync(session, program, ct);
        if (restored.Count == 0 && failure is null) return cancelled;
        if (failure is not null)
            return cancelled with
            {
                Message = cancelled.Message + " The zone was cancelled, but an original rung comment that an explicit-comment " +
                          $"replace had overwritten could not be written back: {failure}",
                CommentsRestored = restored.Count > 0 ? restored : null,
                CommentRestoreFailure = failure,
            };
        return cancelled with
        {
            Message = cancelled.Message + $" {restored.Count} original rung comment(s) that a published replace had overwritten were written back.",
            CommentsRestored = restored,
        };
    }

    /// <summary>
    /// Writes back the original rung comments held on the cancelled zone of <paramref name="program"/> and forgets those records.
    /// </summary>
    /// <param name="session">The session that staged and published.</param>
    /// <param name="program">The program whose zone was cancelled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <c>routine#rung</c> keys written back (empty when none were held) and the failure text or <c>null</c>.</returns>
    internal static async Task<(List<string> Restored, string? Failure)> RestoreZoneCommentUndosAsync(
        LogixProjectSession session, string program, CancellationToken ct)
    {
        bool InProgram(string xpath)
        {
            var m = ProposeRungEditHandler.ProgramNamePattern.Match(xpath);
            return m.Success && string.Equals(m.Groups[1].Value, program.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var undos = session.ZoneCommentUndos.Where(u => InProgram(u.RoutineXPath)).ToList();
        if (undos.Count == 0) return (new List<string>(), null);
        session.ForgetZoneCommentUndos(InProgram);
        return await ClearStagedHandler.RestoreCommentsAsync(session, undos, ct);
    }

    /// <summary>
    /// Cancels all pending online edits for a single routine by driving the
    /// Studio 5000 GUI (<c>Logic → Online Edits → Cancel Accepted Program Edits</c>)
    /// in the interactive desktop session through desktop automation — this tool
    /// has no SDK equivalent (verified against the Logix Designer SDK 2.2.1109
    /// nupkg XML docs).
    /// Gated: refuses when Studio 5000 is not installed on this host, refuses
    /// unless <c>confirm=true</c>, and refuses when <c>logix_pending_edits</c>
    /// reports <c>state='none'</c> for the target routine.
    /// </summary>
    [McpServerTool(Name = "logix_cancel_pending_edits")]
    [Description(
        "PRECONDITION: the zone must NOT be tested (zoneState 'untested' from logix_pending_edits) — Studio disables Cancel Accepted " +
        "while a zone is tested, so a tested zone is refused with kind ZoneTested before any Studio command: call " +
        "logix_untest_pending_edits first, then this tool. " +
        "EXPERIMENTAL — DRIVES THE STUDIO 5000 GUI IN THE INTERACTIVE SESSION — this is " +
        "desktop automation, not an SDK call. It does not click at screen positions: the " +
        "gesture is Studio's own menu command, posted by command ID to the Studio main window " +
        "(found by process ID), then the confirmation dialog's text is verified and its Yes " +
        "button is pressed by a posted button message. There is no keyboard fallback and no " +
        "screen-coordinate fallback (a hotkey tier and a coordinate-click tier existed earlier " +
        "and were removed); if the menu command does nothing (no dialog appears) the " +
        "result is command_not_applicable and nothing else is tried. No keystrokes, mouse " +
        "clicks, cursor movement or focus changes are made by the driver (reported as cursorMovedByDriver=false; " +
        "cursorMovedExternally and the \"either\" cursorMoved only show someone else moving the cursor), so the gesture does not depend on " +
        "screen resolution, window position or layout. What can still depend on the screen: " +
        "the screenshots the script saves (full virtual screen, for human review only) and the " +
        "read-only observation of Studio's window title and edits indicator; neither is used to " +
        "aim a gesture. It still acts on the engineer's open Studio session. " +
        "The Logix Designer SDK has no API to cancel pending or accepted edits (verified " +
        "against the SDK 2.2.1109 nupkg XML docs); only Studio 5000's " +
        "Logic -> Online Edits -> Cancel Accepted Program Edits menu gesture can. This tool " +
        "reaches that gesture by running bench/studio-edits.ps1 -Verb cancel on the " +
        "interactive desktop (session 1+): directly as a child powershell.exe when this server is already " +
        "in the user's interactive session (the normal case for a stdio server), or, only " +
        "when the server is in session 0 (SSH or a service), through a scheduled task with an " +
        "Interactive principal named logix-mcp-s1-edits. It parses the RESULT_JSON output, " +
        "and every payload reports launchMode (direct or scheduled_task). The driver must " +
        "not be less privileged than Studio: refuses with studio_not_in_this_session or " +
        "studio_elevated_driver_not when Studio is in another session or elevated. " +
        "OPT-IN REQUIRED: LGX_ENABLE_STUDIO_AUTOMATION must be set to 1 to use this tool. " +
        "CAPABILITY HONESTY: this tool only works on a host with Studio 5000 Logix Designer " +
        "installed — it refuses immediately when that install is not found, before touching " +
        "confirm, pending-edit state, or the session-1 script. " +
        "GATING: (0) refuses when LGX_ENABLE_STUDIO_AUTOMATION is not set. (1) refuses when " +
        "Studio 5000 is not installed on this host. (2) refuses unless confirm=true is " +
        "passed — cancelling discards the controller's pending rung edits via desktop " +
        "automation, not a reviewable SDK call. (3) calls logix_pending_edits for the target " +
        "routine first and refuses when its state is 'none' — there is nothing to cancel. " +
        "(4) runs bench/studio-edits.ps1 -Verb cancel on the interactive desktop (directly, or " +
        "via the logix-mcp-s1-edits task from session 0) and refuses when it reports failure. " +
        "(5) re-reads logix_pending_edits afterward and " +
        "returns both snapshots plus every screenshot path the script collected. Cancel is never refused as a " +
        "ForeignPendingZone: when this server did not publish the zone (another session or Studio did) it proceeds " +
        "and the result carries foreign: true. Returns a " +
        "JSON object with status, program, routine, message, before (pending-edit snapshot " +
        "before cancelling), after (snapshot afterward), and screenshots (array of file paths " +
        "written by the session-1 script). " +
        "No project needs to be open — it works over the upload route the same way " +
        "logix_pending_edits does; call logix_connect first. " +
        "Requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed " +
        "Studio 5000 Logix Designer installation on the host running this server. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public async Task<string> CancelPendingEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to cancel pending edits for. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses: cancelling " +
            "discards the controller's pending rung edits by driving the Studio 5000 GUI. " +
            "Pass true to acknowledge this risk and proceed.")]
        bool confirm = false)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetCancelPendingEditsResponseAsync(program, routine, confirm);
        if (TrafficLogger.IsOkResponse(result)) _tagSessionService.SetZoneTested(program, null);
        _tagSessionService.MarkImageStale(program); // cancel changes the session image
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            "logix_cancel_pending_edits",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, routine, confirm }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    // ── logix_accept_pending_edits ────────────────────────────────────────────

    /// <summary>
    /// Tool-layer wiring for <c>logix_accept_pending_edits</c>: applies the
    /// <c>LGX_ENABLE_STUDIO_AUTOMATION</c> gate (<c>CapabilityDisabled</c>), resolves the
    /// active SDK session, then delegates to <see cref="AcceptPendingEditsHandler.ExecuteAsync"/>
    /// with the pending-edits reader bound to <see cref="PendingEditsHandler.ExecuteAsync"/>
    /// and the runner bound to <see cref="Session1ScriptRunner.RunAsync"/> (verb
    /// <c>accept</c>). Mirrors <see cref="GetCancelPendingEditsResponseAsync"/>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to accept edits for. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="session">Optional pre-opened session for testing.</param>
    /// <param name="session1Runner">Optional runner override (tests), keyed by verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="probe">Optional environment probe for launch-mode reporting.</param>
    /// <param name="acceptForeign">
    /// <c>true</c> accepts a pending zone this server did not publish (see <see cref="ZoneLedger"/>); the result then
    /// carries <c>foreign: true</c>. <c>false</c> refuses it with <c>ForeignPendingZone</c>.
    /// </param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public async Task<string> GetAcceptPendingEditsResponseAsync(
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm = false,
        LogixProjectSession?                                         session = null,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner = null,
        Func<bool>?                                                  isStudioInstalled = null,
        CancellationToken                                            ct = default,
        IEnvironmentProbe?                                           probe = null,
        bool                                                         acceptForeign = false)
    {
        // Gate 0: refused before any Studio probe, pending-edit read, or GUI action.
        if (_capabilities is not { StudioAutomationEnabled: true })
        {
            AuditLogger.LogCapabilityRefusal("logix_accept_pending_edits", Capabilities.EnvVarStudio);
            return JsonSerializer.Serialize(
                new CapabilityDisabledError(
                    Error:
                        "logix_accept_pending_edits refused: LGX_ENABLE_STUDIO_AUTOMATION " +
                        "is not set. This tool drives the Studio 5000 GUI in the interactive " +
                        "desktop session via desktop automation: it posts Studio's own " +
                        "Logic -> Online Edits menu command to the Studio window and answers " +
                        "the confirmation dialog, with no keystrokes, mouse clicks or screen " +
                        "coordinates, so it does not depend on screen resolution or layout. " +
                        "Accepting makes the pending rungs execute on a running controller. " +
                        "Set LGX_ENABLE_STUDIO_AUTOMATION=1 to opt in.",
                    Hint:
                        $"Set the {Capabilities.EnvVarStudio} environment variable to 1 to " +
                        "enable Studio GUI-automation tools. " +
                        $"Client command: {Capabilities.ClientCommandHintStudio}"));
        }

        // Gate 0b: accept makes pending rungs execute on a running controller, so it also
        // needs LGX_ENABLE_LIVE_WRITES. Refused before any probe, read or script call.
        if (RefuseWithoutLiveWrites("logix_accept_pending_edits") is { } liveRefusal)
            return liveRefusal;

        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused("logix_accept_pending_edits", ErrorKinds.NoActiveSession, program: program, routine: routine);
            var (planMode, planLevel) = Session1ScriptRunner.PlanLaunch(probe);
            return JsonSerializer.Serialize(
                new CancelPendingEditsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling logix_accept_pending_edits.",
                    LaunchMode: planMode,
                    RunLevel:   planLevel,
                    Launched:   false,
                    Kind:       ErrorKinds.NoActiveSession));
        }

        Task<object> PendingEditsReader(string p, string r, CancellationToken token) =>
            PendingEditsHandler.ExecuteAsync(p, r, sdkSession, token);

        var runner = session1Runner ??
            ((verb, token) => RunLifecycleDriverAsync(verb, program, routine, sdkSession, probe, token));

        var result = await AcceptPendingEditsHandler.ExecuteAsync(
            program, routine, confirm, PendingEditsReader, runner, isStudioInstalled, ct,
            () => Session1ScriptRunner.PlanLaunch(probe), _tagSessionService,
            acceptForeign, sdkSession);
        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Accepts (assembles) all pending online edits for a single routine by driving the
    /// Studio 5000 GUI (<c>Logic → Online Edits → Assemble Accepted Program Edits</c>).
    /// Gated like <see cref="CancelPendingEdits"/>.
    /// </summary>
    [McpServerTool(Name = "logix_accept_pending_edits")]
    [Description(
        "EXPERIMENTAL — DRIVES THE STUDIO 5000 GUI IN THE INTERACTIVE SESSION — this is " +
        "desktop automation, not an SDK call. It does not click at screen positions: the " +
        "gesture is Studio's own menu command, posted by command ID to the Studio main window " +
        "(found by process ID), then the confirmation dialog's text is verified and its Yes " +
        "button is pressed by a posted button message. There is no keyboard fallback and no " +
        "screen-coordinate fallback (a hotkey tier and a coordinate-click tier existed earlier " +
        "and were removed); if the menu command does nothing (no dialog appears) the " +
        "result is command_not_applicable and nothing else is tried. No keystrokes, mouse " +
        "clicks, cursor movement or focus changes are made by the driver (reported as cursorMovedByDriver=false; " +
        "cursorMovedExternally and the \"either\" cursorMoved only show someone else moving the cursor), so the gesture does not depend on " +
        "screen resolution, window position or layout. What can still depend on the screen: " +
        "the screenshots the script saves (full virtual screen, for human review only) and the " +
        "read-only observation of Studio's window title and edits indicator; neither is used to " +
        "aim a gesture. It still acts on the engineer's open Studio session. " +
        "ACCEPTING MAKES THE PENDING RUNGS EXECUTE ON A RUNNING CONTROLLER — that is why this " +
        "tool needs LGX_ENABLE_STUDIO_AUTOMATION=1 and confirm=true. Only call it with the " +
        "engineer's go-ahead. The Logix Designer SDK cannot assemble an existing pending edit " +
        "zone; this tool reaches Studio 5000's Logic -> Online Edits -> Assemble Accepted " +
        "Program Edits gesture by running bench/studio-edits.ps1 -Verb accept on the " +
        "interactive desktop (session 1+): directly as a child powershell.exe when this server " +
        "is in the user's interactive session, or, only when the server is in session 0, " +
        "through a scheduled task with an Interactive principal named logix-mcp-s1-edits. " +
        "Every payload reports launched, launchMode (direct or scheduled_task) and runLevel. " +
        "Refuses with studio_not_in_this_session or studio_elevated_driver_not when Studio is " +
        "in another session or elevated. " +
        "GATING: (0) refuses (CapabilityDisabled) when LGX_ENABLE_STUDIO_AUTOMATION is not " +
        "set. (1) refuses when Studio 5000 is not installed on this host. (2) refuses unless " +
        "confirm=true. (3) calls logix_pending_edits for the target routine first and refuses " +
        "when its state is 'none' — nothing to accept. (3b) refuses with kind ForeignPendingZone " +
        "(program, routines, publishedByThisServer: false) when the live zone holds a routine this server did " +
        "not publish (another session or Studio did): review it in logix_pending_edits and pass " +
        "acceptForeign=true to finalize it anyway (the result then carries foreign: true), or cancel it. " +
        "(4) runs the driver with -Verb accept. " +
        "(5) re-reads logix_pending_edits and returns ok only when the after-state is 'none' " +
        "(the before-rungs are now 'N'); if the gesture reports success but the routine is " +
        "still pending, returns an error naming both states — success is never claimed on the " +
        "gesture alone. Returns status, program, routine, message, before, after and " +
        "screenshots. No project needs to be open; call logix_connect first. Requires the " +
        "Logix Designer SDK build (EnableLogixSdk=true) and a licensed Studio 5000 Logix " +
        "Designer installation on the host running this server. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public async Task<string> AcceptPendingEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to accept pending edits for. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses: accepting makes " +
            "the pending rungs execute on a running controller, by driving the Studio 5000 GUI. " +
            "Pass true to acknowledge this and proceed.")]
        bool confirm = false,
        [Description(
            "Defaults to false: a pending zone this server did not publish (another session or Studio did) is " +
            "refused with kind ForeignPendingZone. Pass true, after reviewing the edits in logix_pending_edits, to " +
            "finalize such a zone anyway; the result then carries foreign: true.")]
        bool acceptForeign = false)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetAcceptPendingEditsResponseAsync(program, routine, confirm, acceptForeign: acceptForeign);
        if (TrafficLogger.IsOkResponse(result)) _tagSessionService.SetZoneTested(program, null);
        _tagSessionService.MarkImageStale(program); // accept changes the controller
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            "logix_accept_pending_edits",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, routine, confirm, acceptForeign }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    /// <summary>Shared description clause for the tools: payload fields and gates 0-3.</summary>
    private const string StudioLifecycleGatingText =
        "Every payload reports launched, launchMode (direct or scheduled_task) and runLevel, and passes through gestureTier, dialogText and dialogWarnings. Refuses with studio_not_in_this_session or studio_elevated_driver_not when Studio is in another session or elevated. GATING: (0) refuses (CapabilityDisabled) when LGX_ENABLE_STUDIO_AUTOMATION is not set. (1) refuses when Studio 5000 is not installed on this host. (2) refuses unless confirm=true. (3) calls logix_pending_edits for the target routine first and refuses when its state is 'none'. ";

    /// <summary>Description clause of the foreign-zone gate for test and assemble.</summary>
    private const string ForeignZoneGateText =
        "FOREIGN ZONE: refuses with kind ForeignPendingZone (program, routines, publishedByThisServer: false), before any " +
        "Studio command, when the live zone holds a routine this server did not publish (another session or Studio " +
        "did); review it in logix_pending_edits and pass acceptForeign=true to proceed anyway (the result then carries " +
        "foreign: true), or cancel it. ";

    // ── logix_test_pending_edits / logix_untest_pending_edits / logix_assemble_pending_edits ──
    // Thin callers of StudioLifecycleHandler (via the *PendingEditsHandler bindings).

    /// <summary>
    /// Returns a serialized <see cref="CapabilityDisabledError"/> (and audits the refusal) when
    /// <c>LGX_ENABLE_LIVE_WRITES</c> is not set; otherwise <c>null</c>. Used by the Studio
    /// lifecycle tools that mutate the live controller (accept/test/untest/assemble).
    /// </summary>
    private string? RefuseWithoutLiveWrites(string toolName)
    {
        if (_capabilities is { LiveWritesEnabled: true })
            return null;

        AuditLogger.LogCapabilityRefusal(toolName, Capabilities.EnvVar);
        return JsonSerializer.Serialize(
            new CapabilityDisabledError(
                Error:
                    toolName + " refused: LGX_ENABLE_LIVE_WRITES is not set. This tool " +
                    "changes the state of the live controller's program, so it needs " +
                    "LGX_ENABLE_LIVE_WRITES=1 in addition to LGX_ENABLE_STUDIO_AUTOMATION=1.",
                Hint:
                    $"Set the {Capabilities.EnvVar} environment variable to 1 to enable live " +
                    $"controller mutations. Client command: {Capabilities.ClientCommandHint}"));
    }

    /// <summary>
    /// Shared tool-layer wiring for the verbs: applies the
    /// <c>LGX_ENABLE_STUDIO_AUTOMATION</c> gate (<c>CapabilityDisabled</c>), resolves the active
    /// SDK session, then calls <paramref name="handler"/> with the pending-edits reader bound to
    /// <see cref="PendingEditsHandler.ExecuteAsync"/> and the runner bound to
    /// <see cref="Session1ScriptRunner.RunAsync"/>. Same gates and order as
    /// <see cref="GetAcceptPendingEditsResponseAsync"/>.
    /// </summary>
    private async Task<string> GetStudioLifecycleResponseAsync(
        string                                                       toolName,
        string                                                       effect,
        Func<string, string, bool,
             Func<string, string, CancellationToken, Task<object>>,
             Func<string, CancellationToken, Task<Session1ScriptResult>>,
             Func<bool>?, CancellationToken,
             Func<(string? LaunchMode, string? RunLevel)>?,
             bool, object?,
             Task<object>>                                           handler,
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm,
        LogixProjectSession?                                         session,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner,
        Func<bool>?                                                  isStudioInstalled,
        CancellationToken                                            ct,
        IEnvironmentProbe?                                           probe,
        bool                                                         acceptForeign = false)
    {
        // Gate 0: refused before any Studio probe, pending-edit read, or GUI action.
        if (_capabilities is not { StudioAutomationEnabled: true })
        {
            AuditLogger.LogCapabilityRefusal(toolName, Capabilities.EnvVarStudio);
            return JsonSerializer.Serialize(
                new CapabilityDisabledError(
                    Error:
                        toolName + " refused: LGX_ENABLE_STUDIO_AUTOMATION " +
                        "is not set. This tool drives the Studio 5000 GUI in the interactive " +
                        "desktop session with posted commands, and " + effect + " " +
                        "Set LGX_ENABLE_STUDIO_AUTOMATION=1 to opt in.",
                    Hint:
                        $"Set the {Capabilities.EnvVarStudio} environment variable to 1 to " +
                        "enable Studio GUI-automation tools. " +
                        $"Client command: {Capabilities.ClientCommandHintStudio}"));
        }

        // Gate 0b: test/untest/assemble mutate the live controller, so they also need
        // LGX_ENABLE_LIVE_WRITES. Refused before any probe, read or script call.
        if (RefuseWithoutLiveWrites(toolName) is { } liveRefusal)
            return liveRefusal;

        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused(toolName, ErrorKinds.NoActiveSession, program: program, routine: routine);
            var (planMode, planLevel) = Session1ScriptRunner.PlanLaunch(probe);
            return JsonSerializer.Serialize(
                new CancelPendingEditsError(
                    "No active SDK session. Call logix_connect to establish a session " +
                    "before calling " + toolName + ".",
                    LaunchMode: planMode,
                    RunLevel:   planLevel,
                    Launched:   false,
                    Kind:       ErrorKinds.NoActiveSession));
        }

        // Never test or assemble a zone that a failed publish census
        // flagged as not matching what was staged. Refused before any read or Studio command.
        if (toolName is "logix_test_pending_edits" or "logix_assemble_pending_edits"
            && PendingEditsHandler.RefuseOnUnresolvedPublishMismatch(
                   sdkSession, toolName, program, Session1ScriptRunner.PlanLaunch(probe)) is { } mismatchRefusal)
        {
            return JsonSerializer.Serialize(mismatchRefusal);
        }

        Task<object> PendingEditsReader(string p, string r, CancellationToken token) =>
            PendingEditsHandler.ExecuteAsync(p, r, sdkSession, token);

        var runner = session1Runner ??
            ((verb, token) => RunLifecycleDriverAsync(verb, program, routine, sdkSession, probe, token));

        var result = await handler(
            program, routine, confirm, PendingEditsReader, runner, isStudioInstalled, ct,
            () => Session1ScriptRunner.PlanLaunch(probe), acceptForeign, sdkSession);

        // An assembled (or accepted) zone can no longer be cancelled: its comment undos are void.
        if (result is CancelPendingEditsResult { Status: "assembled" or "accepted" })
            sdkSession.ForgetZoneCommentUndos(x =>
            {
                var m = ProposeRungEditHandler.ProgramNamePattern.Match(x);
                return m.Success && string.Equals(m.Groups[1].Value, program.Trim(), StringComparison.OrdinalIgnoreCase);
            });
        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Runs the Studio driver for a lifecycle verb with the confirmation wait chosen by
    /// <see cref="StudioConfirmWait"/>: 15 s when the call follows the server's own publish or
    /// lifecycle call on the same routine within 30 s, else the driver's 10 s default. The call is
    /// recorded afterwards so a following verb on the routine gets the longer wait.
    /// </summary>
    private async Task<Session1ScriptResult> RunLifecycleDriverAsync(
        string verb, string program, string routine, LogixProjectSession sdkSession,
        IEnvironmentProbe? probe, CancellationToken token)
    {
        var confirmSeconds = _confirmWait.SecondsFor(program, routine);
        try
        {
            return await Session1ScriptRunner.RunAsync(
                verb, ct: token, probe: probe, taskName: Session1ScriptRunner.EditsTaskName,
                program: program, target: StudioWindowTargetOf(sdkSession),
                confirmTimeoutSeconds: confirmSeconds).ConfigureAwait(false);
        }
        finally
        {
            _confirmWait.Record(program, routine);
        }
    }

    /// <summary>
    /// Stamps each program's tested state (<c>programTestedEdits</c>, read SDK-side from the program export's
    /// <c>TestEdits</c>) from a pending-edits response onto the session, so analysis readers can word a pending
    /// rung as tested or untested. A <c>null</c> reading forgets the program (state unknown).
    /// </summary>
    private void StampZoneTestedFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement);
        }
        catch (JsonException) { /* not a pending-edits payload */ }

        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("program", out var prog) && prog.ValueKind == JsonValueKind.String &&
                    e.TryGetProperty("programTestedEdits", out var tested) &&
                    tested.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                {
                    _tagSessionService.SetZoneTested(
                        prog.GetString()!, tested.ValueKind == JsonValueKind.Null ? null : tested.GetBoolean());
                }
                if (e.TryGetProperty("program", out var p2) && p2.ValueKind == JsonValueKind.String &&
                    e.TryGetProperty("testedEdits", out var t2) &&
                    t2.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                {
                    _tagSessionService.SetZoneTested(
                        p2.GetString()!, t2.ValueKind == JsonValueKind.Null ? null : t2.GetBoolean());
                }
                foreach (var prop in e.EnumerateObject()) Walk(prop.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) Walk(item);
        }
    }

    private async Task<string> RunStudioLifecycleToolAsync(
        string toolName, string program, string routine, bool confirm,
        Func<Task<string>> call, bool acceptForeign = false)
    {
        var sw     = Stopwatch.StartNew();
        var result = await call();
        if (TrafficLogger.IsOkResponse(result))
        {
            if (toolName.Contains("untest", StringComparison.Ordinal)) _tagSessionService.SetZoneTested(program, false);
            else if (toolName.Contains("_test_", StringComparison.Ordinal)) _tagSessionService.SetZoneTested(program, true);
            else _tagSessionService.SetZoneTested(program, null);   // assemble: the zone is gone
        }
        _tagSessionService.MarkImageStale(program); // test / untest / assemble change the session image
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            toolName,
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, routine, confirm, acceptForeign }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    /// <summary>
    /// Tool-layer wiring for <c>logix_test_pending_edits</c>: the
    /// <c>LGX_ENABLE_STUDIO_AUTOMATION</c> gate, the active SDK session, then
    /// <see cref="TestPendingEditsHandler.ExecuteAsync"/>. Mirrors
    /// <see cref="GetAcceptPendingEditsResponseAsync"/>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to test pending edits for. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="session">Optional pre-opened session for testing.</param>
    /// <param name="session1Runner">Optional runner override (tests), keyed by verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="probe">Optional environment probe for launch-mode reporting.</param>
    /// <param name="acceptForeign">
    /// <c>true</c> tests a pending zone this server did not publish (see <see cref="ZoneLedger"/>); the result then
    /// carries <c>foreign: true</c>. <c>false</c> refuses it with <c>ForeignPendingZone</c>.
    /// </param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public Task<string> GetTestPendingEditsResponseAsync(
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm = false,
        LogixProjectSession?                                         session = null,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner = null,
        Func<bool>?                                                  isStudioInstalled = null,
        CancellationToken                                            ct = default,
        IEnvironmentProbe?                                           probe = null,
        bool                                                         acceptForeign = false) =>
        GetStudioLifecycleResponseAsync(
            "logix_test_pending_edits",
            "testing makes the new logic execute on a running controller.",
            TestPendingEditsHandler.ExecuteAsync,
            program, routine, confirm, session, session1Runner, isStudioInstalled, ct, probe, acceptForeign);

    /// <summary>
    /// Tests (runs the new logic of) the pending online edits of a single routine by posting a Studio 5000 command through the
    /// session-1 driver. Gated like <see cref="AcceptPendingEdits"/>.
    /// </summary>
    [McpServerTool(Name = "logix_test_pending_edits")]
    [Description(
        "PRECONDITION: the zone must be pending and untested (zoneState 'untested'); a zone that is already tested is refused with kind AlreadyTested before any Studio command. After an unresolved PublishMismatch it is refused with kind PublishMismatchUnresolved until logix_cancel_pending_edits succeeds or logix_pending_edits shows the zone matches what was staged. " +
        "EXPERIMENTAL — DRIVES THE STUDIO 5000 GUI IN THE INTERACTIVE SESSION with a posted command — this is " +
        "desktop automation, not an SDK call. TEST MAKES THE NEW LOGIC EXECUTE ON THE RUNNING CONTROLLER " +
        "(the original logic is kept); logix_untest_pending_edits reverts it. Only call it with the engineer's " +
        "go-ahead, on a zone already accepted (e.g. published with logix_propose_rung_edit target=\"controller\"). " +
        "The Logix Designer SDK has no test call; this tool posts Studio's Test Accepted Program Edits command " +
        "(WM_COMMAND 32061) by running bench/studio-edits.ps1 -Verb test on the interactive desktop (directly, or " +
        "via the logix-mcp-s1-edits scheduled task from session 0), with no driver cursor movement (cursorMovedByDriver is false) and no focus change. "
        + StudioLifecycleGatingText + ForeignZoneGateText +
        "(4) runs the driver with -Verb test; no confirmation dialog means command_not_applicable. (5) re-reads " +
        "logix_pending_edits and returns ok only when the program export afterward shows zoneState 'tested' (program TestEdits=true; " +
        "result says 'verified: program TestEdits=true'), else error kind TestStateNotConfirmed with the before and " +
        "after states. Refuses with kind AlreadyTested, before any Studio command, when the zone is already tested. " +
        "Check behaviour with logix_read. " +
        "dialogWarnings carries Studio's own warnings verbatim (outputs in 'D' and 'R' zones keep their last " +
        "state in the Original View; SFC routines are reset) — read them. Returns status, program, routine, " +
        "message, before, after, screenshots, dialogText and dialogWarnings. No project needs to be open; call " +
        "logix_connect first. Requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed Studio " +
        "5000 Logix Designer installation on the host. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public Task<string> TestPendingEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to test pending edits for. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses: testing makes the new logic execute on a running controller, by driving the Studio 5000 GUI with a posted command. Pass true to acknowledge this and proceed.")]
        bool confirm = false,
        [Description(
            "Defaults to false: a pending zone this server did not publish (another session or Studio did) is refused with kind ForeignPendingZone. Pass true, after reviewing the edits in logix_pending_edits, to test such a zone anyway; the result then carries foreign: true.")]
        bool acceptForeign = false) =>
        RunStudioLifecycleToolAsync(
            "logix_test_pending_edits", program, routine, confirm,
            () => GetTestPendingEditsResponseAsync(program, routine, confirm, acceptForeign: acceptForeign),
            acceptForeign);

    /// <summary>
    /// Tool-layer wiring for <c>logix_untest_pending_edits</c>: the
    /// <c>LGX_ENABLE_STUDIO_AUTOMATION</c> gate, the active SDK session, then
    /// <see cref="UntestPendingEditsHandler.ExecuteAsync"/>. Mirrors
    /// <see cref="GetAcceptPendingEditsResponseAsync"/>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to untest pending edits for. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="session">Optional pre-opened session for testing.</param>
    /// <param name="session1Runner">Optional runner override (tests), keyed by verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="probe">Optional environment probe for launch-mode reporting.</param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public Task<string> GetUntestPendingEditsResponseAsync(
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm = false,
        LogixProjectSession?                                         session = null,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner = null,
        Func<bool>?                                                  isStudioInstalled = null,
        CancellationToken                                            ct = default,
        IEnvironmentProbe?                                           probe = null) =>
        GetStudioLifecycleResponseAsync(
            "logix_untest_pending_edits",
            "untesting reverts the tested logic on a running controller.",
            (p, r, c, reader, runner, installed, token, plan, _, _) =>
                UntestPendingEditsHandler.ExecuteAsync(p, r, c, reader, runner, installed, token, plan),
            program, routine, confirm, session, session1Runner, isStudioInstalled, ct, probe);

    /// <summary>
    /// Untests (reverts to the original logic) the pending online edits of a single routine by posting a Studio 5000 command through the
    /// session-1 driver. Gated like <see cref="AcceptPendingEdits"/>.
    /// </summary>
    [McpServerTool(Name = "logix_untest_pending_edits")]
    [Description(
        "PRECONDITION: the zone must be tested (zoneState 'tested'); an untested zone is refused with kind NotTested before any Studio command. " +
        "EXPERIMENTAL — DRIVES THE STUDIO 5000 GUI IN THE INTERACTIVE SESSION with a posted command — this is " +
        "desktop automation, not an SDK call. UNTEST REVERTS A TESTED ZONE: it puts the original logic back on the " +
        "running controller after logix_test_pending_edits made the new logic execute. The Logix Designer SDK has no " +
        "untest call; this tool posts Studio's Untest Accepted Program Edits command (WM_COMMAND 32062) by running " +
        "bench/studio-edits.ps1 -Verb untest on the interactive desktop (directly, or via the logix-mcp-s1-edits " +
        "scheduled task from session 0), with no driver cursor movement (cursorMovedByDriver is false) and no focus change. "
        + StudioLifecycleGatingText +
        "(4) runs the driver with -Verb untest; no confirmation dialog means command_not_applicable. (5) re-reads " +
        "logix_pending_edits and returns ok only when the program export afterward shows zoneState 'untested' (program TestEdits=false; " +
        "result says 'verified: program TestEdits=false'), else error kind UntestStateNotConfirmed with the before and " +
        "after states. Refuses with kind NotTested, before any Studio command, when the zone is not tested. " +
        "Check behaviour with logix_read. " +
        "dialogWarnings carries Studio's own warnings verbatim (outputs in 'I' zones) — read them. Untest leaves the " +
        "zone pending: follow with logix_cancel_pending_edits to discard it. Returns status, program, routine, " +
        "message, before, after, screenshots, dialogText and dialogWarnings. No project needs to be open; call " +
        "logix_connect first. Requires the Logix Designer SDK build (EnableLogixSdk=true) and a licensed Studio " +
        "5000 Logix Designer installation on the host. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public Task<string> UntestPendingEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to untest pending edits for. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses: untesting reverts the tested logic on a running controller, by driving the Studio 5000 GUI with a posted command. Pass true to acknowledge this and proceed.")]
        bool confirm = false) =>
        RunStudioLifecycleToolAsync(
            "logix_untest_pending_edits", program, routine, confirm,
            () => GetUntestPendingEditsResponseAsync(program, routine, confirm));

    /// <summary>
    /// Tool-layer wiring for <c>logix_assemble_pending_edits</c>: the
    /// <c>LGX_ENABLE_STUDIO_AUTOMATION</c> gate, the active SDK session, then
    /// <see cref="AssemblePendingEditsHandler.ExecuteAsync"/>. Mirrors
    /// <see cref="GetAcceptPendingEditsResponseAsync"/>.
    /// </summary>
    /// <param name="program">Program name containing the target routine. Required.</param>
    /// <param name="routine">Routine name to assemble pending edits for. Required.</param>
    /// <param name="confirm">Must be <c>true</c> to proceed.</param>
    /// <param name="session">Optional pre-opened session for testing.</param>
    /// <param name="session1Runner">Optional runner override (tests), keyed by verb.</param>
    /// <param name="isStudioInstalled">Optional Studio-installed probe override.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="probe">Optional environment probe for launch-mode reporting.</param>
    /// <param name="acceptForeign">
    /// <c>true</c> assembles a pending zone this server did not publish (see <see cref="ZoneLedger"/>); the result then
    /// carries <c>foreign: true</c>. <c>false</c> refuses it with <c>ForeignPendingZone</c>.
    /// </param>
    /// <returns>JSON string containing the result or a structured error.</returns>
    public Task<string> GetAssemblePendingEditsResponseAsync(
        string                                                       program,
        string                                                       routine,
        bool                                                         confirm = false,
        LogixProjectSession?                                         session = null,
        Func<string, CancellationToken, Task<Session1ScriptResult>>? session1Runner = null,
        Func<bool>?                                                  isStudioInstalled = null,
        CancellationToken                                            ct = default,
        IEnvironmentProbe?                                           probe = null,
        bool                                                         acceptForeign = false) =>
        GetStudioLifecycleResponseAsync(
            "logix_assemble_pending_edits",
            "assembling is irreversible and finalizes the pending rung edits on a running controller.",
            (p, r, c, reader, runner, installed, token, plan, foreign, ledger) =>
                AssemblePendingEditsHandler.ExecuteAsync(
                    p, r, c, reader, runner, installed, token, plan, _tagSessionService, foreign, ledger),
            program, routine, confirm, session, session1Runner, isStudioInstalled, ct, probe, acceptForeign);

    /// <summary>
    /// Assembles (irreversibly finalizes) the pending online edits of a single routine by posting a Studio 5000 command through the
    /// session-1 driver. Gated like <see cref="AcceptPendingEdits"/>.
    /// </summary>
    [McpServerTool(Name = "logix_assemble_pending_edits")]
    [Description(
        "PRECONDITION: the zone must be tested (zoneState 'tested'); an untested zone is refused with kind NotTested before any Studio command. After an unresolved PublishMismatch it is refused with kind PublishMismatchUnresolved until logix_cancel_pending_edits succeeds or logix_pending_edits shows the zone matches what was staged. " +
        "EXPERIMENTAL — DRIVES THE STUDIO 5000 GUI IN THE INTERACTIVE SESSION with a posted command — this is " +
        "desktop automation, not an SDK call. ASSEMBLE IS IRREVERSIBLE: it finalizes the tested rung edits on the " +
        "running controller and cannot be undone with untest or cancel. Only call it with the engineer's go-ahead. " +
        "Studio enables Assemble only on a TESTED zone: call logix_test_pending_edits first, or use " +
        "logix_accept_pending_edits to finalize in one step. The Logix Designer SDK has no assemble call for an " +
        "existing zone; this tool posts Studio's Assemble Accepted Program Edits command (WM_COMMAND 32063) by " +
        "running bench/studio-edits.ps1 -Verb assemble on the interactive desktop (directly, or via the " +
        "logix-mcp-s1-edits scheduled task from session 0), with no driver cursor movement (cursorMovedByDriver is false) and no focus change. "
        + StudioLifecycleGatingText + ForeignZoneGateText +
        "(4) runs the driver with -Verb assemble; when Studio shows no confirmation dialog (the zone is not tested) " +
        "it returns an error with kind 'NotTested': Assemble needs a tested zone. (5) re-reads logix_pending_edits " +
        "and returns ok only when the after-state is 'none' (the before-rungs are now 'N'); if the gesture " +
        "reports success but the routine is still pending, returns an error naming both states — success is never " +
        "claimed on the gesture alone. Returns status, program, routine, message, before, after, screenshots, " +
        "dialogText and dialogWarnings. No project needs to be open; call logix_connect first. Requires the Logix " +
        "Designer SDK build (EnableLogixSdk=true) and a licensed Studio 5000 Logix Designer installation on the " +
        "host. " +
        "Before reading or resolving pending edits, load the playbook: logix_get_skill(\"lgx-read-pending-state\").")]
    public Task<string> AssemblePendingEdits(
        [Description(
            "Program name containing the target routine. Required.")]
        string program,
        [Description(
            "Routine name to assemble pending edits for. Required.")]
        string routine,
        [Description(
            "Must be true to proceed. Defaults to false, which always refuses: assembling is irreversible and finalizes the pending rung edits on a running controller, by driving the Studio 5000 GUI with a posted command. Pass true to acknowledge this and proceed.")]
        bool confirm = false,
        [Description(
            "Defaults to false: a pending zone this server did not publish (another session or Studio did) is refused with kind ForeignPendingZone. Pass true, after reviewing the edits in logix_pending_edits, to assemble such a zone anyway; the result then carries foreign: true.")]
        bool acceptForeign = false) =>
        RunStudioLifecycleToolAsync(
            "logix_assemble_pending_edits", program, routine, confirm,
            () => GetAssemblePendingEditsResponseAsync(program, routine, confirm, acceptForeign: acceptForeign),
            acceptForeign);

    /// <summary>
    /// Builds the <c>logix_run_test</c> response: runs <see cref="RunTestHandler"/> against the
    /// active session. Every refusal (not an emulator, identity unavailable, live writes off, not confirmed,
    /// limits) happens before any write.
    /// </summary>
    /// <param name="cases">Scripted test cases (up to 20, each up to 50 steps).</param>
    /// <param name="confirmedByUser">Must be true.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional MCP progress sink: one report per case, in order.</param>
    /// <returns>JSON string with the result or a structured error.</returns>
    public async Task<string> GetRunTestResponseAsync(
        IReadOnlyList<RunTestCase>? cases,
        bool                        confirmedByUser,
        CancellationToken           ct = default,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var result = await RunTestHandler.ExecuteAsync(
            cases, confirmedByUser, _tagSessionService, _capabilities, ct: ct,
            progress: progress is null
                ? null
                : new SynchronousProgress<(int Completed, int Total, string Message)>(p =>
                    progress.Report(new ProgressNotificationValue
                    {
                        Progress = p.Completed,
                        Total    = p.Total,
                        Message  = p.Message,
                    })));
        return JsonSerializer.Serialize(result, result.GetType());
    }

    /// <summary>Emulator-only scripted logic tests.</summary>
    [McpServerTool(Name = "logix_run_test")]
    [Description(
        "Run scripted logic tests against the Echo EMULATOR ONLY. Each case is {name, setup?: [{tag, value}], " +
        "steps: [{set?: [{tag, value}], waitMs?, expect?: [{tag, equals? | min? | max?}]}]}: a step writes its " +
        "'set' tags, waits waitMs (default 200, clamped to 0..10000), then reads each 'expect' tag and gives a " +
        "pass/fail verdict. Up to 20 cases and 50 steps per case. Before anything else the server reads the " +
        "controller's EtherNet/IP ListIdentity: unless the product name starts with 'Emulate' it refuses with " +
        "kind 'EmulatorOnly' naming the observed product name, and with kind 'IdentityUnavailable' when the " +
        "lookup fails. It also needs LGX_ENABLE_LIVE_WRITES=1 and confirmedByUser=true (ask the engineer first). " +
        "On any refusal nothing is written. Test writes are looser than logix_write: Safety_-prefixed, input and " +
        "NotDesignated tags may be driven, but a tag written by program logic (ProgramWritten), a tag whose External " +
        "Access is not Read/Write (including tags lgx-mcp created) and a program-scoped tag are refused with kind " +
        "'TestWriteRefused' before anything is written. Each case lists in ordinaryWriteDenied the tags it set that " +
        "logix_write would deny. Returns {overall: pass|fail, productName, cases: [{name, result, " +
        "steps: [{set, waitMs, expect: [{tag, expected, observed, verdict}], verdict}], ordinaryWriteDenied, durationMs}], durationMs}. " +
        "Call logix_connect (online to the emulator) first. Requires the Logix Designer SDK build " +
        "(EnableLogixSdk=true).")]
    public Task<string> RunTest(
        RequestContext<CallToolRequestParams> context,
        [Description(
            "Test cases: up to 20, each {name, setup?, steps} with up to 50 steps; see the tool description.")]
        RunTestCase[] cases,
        [Description(
            "Must be true: the engineer's go-ahead to write test inputs on the emulator. Defaults to false, which always refuses.")]
        bool confirmedByUser = false) =>
        GetRunTestResponseAsync(cases, confirmedByUser, progress: TasksSupport.CreateProgress(context));

    /// <summary>
    /// Builds the <c>logix_build</c> response: runs <see cref="BuildHandler"/> on the active session (or the
    /// caller-supplied one in tests) and serializes the result.
    /// </summary>
    /// <param name="target"><c>default</c> (when null), <c>physical</c> or <c>echo</c>.</param>
    /// <param name="session">Optional pre-opened session for tests; otherwise the active SDK session is used.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string with the build result or a structured error.</returns>
    public async Task<string> GetBuildResponseAsync(
        string? target = null,
        LogixProjectSession? session = null,
        CancellationToken ct = default)
    {
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
            return JsonSerializer.Serialize(
                new BuildError(
                    "No active SDK session. Call logix_connect (or open a project) before calling logix_build.",
                    NoActiveSessionHint,
                    ErrorKinds.NoActiveSession));

        var result = await BuildHandler.ExecuteAsync(target, sdkSession, ct);
        return JsonSerializer.Serialize(result, result.GetType());
    }

    /// <summary>Runs the SDK build check on the session's project.</summary>
    [McpServerTool(Name = "logix_build")]
    [Description(
        "Build the session's project with the Logix Designer SDK (BuildAsync) and report what the build log says. " +
        "It saves a copy of the project the session has open (the .ACD or L5X from logix_open_project, or the " +
        "server-managed upload from logix_connect), opens the copy offline in a separate handle and builds that; it " +
        "never builds inside the online session. If the SDK cannot save the project with edits pending, or the copy " +
        "lacks the staged edit, it returns buildVerify \"unavailable\" with the reason and builds nothing. It never goes online, downloads or changes the controller mode, needs no " +
        "live-writes flag, and is always read-only toward the controller. Use it after staging rung edits to catch " +
        "undefined tags and other compile errors before publishing. Returns {status: passed|failed, target, " +
        "errors: [{location, program, routine, rung, line, code, message}], warnings: [same], elapsedMs, " +
        "designerVersion?, note?}. Each issue carries its location (program/routine/rung, or line) when the SDK's " +
        "log gave one. On Logix Designer before v37 the SDK cannot build: the tool refuses with kind " +
        "'SdkCapabilityUnavailable' before calling it, and Studio's Verify (Logic → Verify → Controller) is the " +
        "manual equivalent. Requires an active SDK session (kind 'NoActiveSession' otherwise) and the Logix " +
        "Designer SDK build (EnableLogixSdk=true).")]
    public Task<string> Build(
        [Description(
            "Build target: \"default\" (the SDK chooses: the previous build's target, or a physical controller for a " +
            "first build), \"physical\" or \"echo\" (the Echo emulated controller). Match the controller you will " +
            "download to, to avoid a recompile. Defaults to \"default\".")]
        string target = "default") =>
        GetBuildResponseAsync(target);

    /// <summary>
    /// Builds the <c>logix_create_routine</c> response: runs <see cref="CreateRoutineHandler"/> on the active
    /// session (or the caller-supplied one in tests) and serializes the result.
    /// </summary>
    /// <param name="program">Program that will own the new routine.</param>
    /// <param name="name">New routine name.</param>
    /// <param name="type">Routine type; only <c>RLL</c> (the default) is supported.</param>
    /// <param name="description">Optional routine description.</param>
    /// <param name="confirmedByUser">The user's go-ahead for this exact call.</param>
    /// <param name="session">Optional pre-opened session for tests; otherwise the active SDK session is used.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>JSON string with the result or a structured error.</returns>
    public async Task<string> GetCreateRoutineResponseAsync(
        string               program,
        string               name,
        string?              type = null,
        string?              description = null,
        bool                 confirmedByUser = false,
        LogixProjectSession? session = null,
        CancellationToken    ct = default)
    {
        var sdkSession = session ?? TryGetActiveSession();
        if (sdkSession is null)
        {
            AuditLogger.LogToolRefused("logix_create_routine", ErrorKinds.NoActiveSession, program: program, routine: name);
            return JsonSerializer.Serialize(
                new CreateRoutineError(
                    "No active SDK session. Call logix_connect before calling logix_create_routine.",
                    ErrorKinds.NoActiveSession,
                    Hint: NoActiveSessionHint));
        }

        var result = await CreateRoutineHandler.ExecuteAsync(
            program, name, type, description, confirmedByUser, sdkSession,
            liveWritesEnabled: _capabilities is { LiveWritesEnabled: true },
            allowlist:         _tagSessionService.WriteAllowlist,
            ct:                ct);
        return JsonSerializer.Serialize(result, result.GetType());
    }

    /// <summary>Creates an empty ladder routine in a program through the SDK's named-object import.</summary>
    [McpServerTool(Name = "logix_create_routine")]
    [Description(
        "Create an empty ladder (RLL) routine in a program, so logix_propose_rung_edit has a routine to stage " +
        "rungs into (it cannot create one: proposing into a missing routine returns RoutineNotFound). Needs an " +
        "ONLINE SDK session: an offline session is refused with kind OfflineCreateNotSupported (importAttempted " +
        "false, nothing created or saved), because the SDK session has no path that saves the routine to the " +
        "project file; create it in Studio instead. It imports an envelope with PartialImportWithTargetFromXmlFile into " +
        "<program>/Routines, always with LeaveEdits so it never finalizes other pending edits (any other import " +
        "option is refused in code). The envelope is an export of an existing RLL routine of the same program, " +
        "renamed and emptied (a self-closed export is opened into a full RLLContent first, kept under 30 kB); a " +
        "program with no RLL routine gets one built from the program export. It then re-exports the program and " +
        "returns CreateNotConfirmed if the routine is not there. GATING, all checked before any import: " +
        "LGX_ENABLE_LIVE_WRITES=1; confirmedByUser=true (the user's go-ahead for this exact call: the SDK cannot " +
        "delete a routine, so never set it on your own judgement); the program must be listed in the write " +
        "allowlist (LGX_WRITE_ALLOWLIST) when one is set; the name must follow the Logix rules (letters, digits " +
        "and single underscores, not starting with a digit, no trailing underscore, at most 40 characters) and " +
        "must not already exist in the program (RoutineAlreadyExists; names are case-insensitive); type must be " +
        "'RLL'. If the program has edits that are not yet assembled, it refuses with PendingEditsBlockImport " +
        "(read the way logix_pending_edits reads them) and attempts no import: the SDK refuses imports into such " +
        "a program, so create the routine BEFORE staging rungs. Returns {status:'created', routineXPath, " +
        "createdInController, undo, program, name, type}. createdInController is always true (online " +
        "sessions only). undo says the " +
        "routine STAYS even if later edits are cancelled and the SDK has no routine delete: remove it in Studio. " +
        "Emits a routine_created audit event. Recipe for new logic: logix_create_routine, then stage the rungs " +
        "(logix_propose_rung_edit target=workstation) plus a JSR to the new routine in the caller, then one " +
        "controller publish makes one pending zone. Requires an active SDK session (kind 'NoActiveSession' " +
        "otherwise) and the Logix Designer SDK build (EnableLogixSdk=true). Before using it, load the playbook: " +
        "logix_get_skill(\"lgx-propose-rung-change\").")]
    public async Task<string> CreateRoutine(
        [Description("Program that will own the new routine, e.g. MainProgram. Required.")]
        string program,
        [Description(
            "New routine name: starts with a letter or underscore, then letters, digits and single underscores; no " +
            "trailing underscore; at most 40 characters; not already used by a routine of the program. Required.")]
        string name,
        [Description("Routine type. Only \"RLL\" (ladder) is supported; this is the default.")]
        string type = "RLL",
        [Description("Optional description stored on the routine (at most 500 characters).")]
        string? description = null,
        [Description(
            "Must be true: the user's explicit go-ahead to create this routine. The SDK has no routine delete, so " +
            "it can only be removed in Studio. Defaults to false, which always refuses.")]
        bool confirmedByUser = false)
    {
        var sw     = Stopwatch.StartNew();
        var result = await GetCreateRoutineResponseAsync(program, name, type, description, confirmedByUser);
        _tagSessionService.MarkImageStale(program); // a new routine changes the program
        await _tagSessionService.RefreshControllerImageAsync(CancellationToken.None); // The export is the only writer of rungs into the image
        sw.Stop();
        TrafficLogger.Append(
            "logix_create_routine",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { program, name, type, description, confirmedByUser }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/>: each report is forwarded immediately.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
#endif
