using L5Sharp.Core;

// ── SessionCreatedTagEntry ────────────────────────────────────────────────────

/// <summary>
/// Records a single tag that was created by this server process during the
/// current session (e.g. via a controller-path rung-edit proposal).
/// </summary>
/// <param name="TagName">
/// The name of the tag that was created on the controller.
/// </param>
/// <param name="CreatedAt">
/// The UTC timestamp at which the tag was recorded in the session registry.
/// </param>
/// <param name="ProposalId">
/// Opaque identifier of the proposal that created this tag.  Used as provenance
/// in <c>logix_write</c> denial messages so the agent can trace a denied write
/// back to the originating rung-edit operation.
/// </param>
/// <param name="Zone">
/// The edit zone (<c>Program/Routine</c>) the creating publish targeted, or <c>null</c> when unknown.
/// Lets a cancel of that zone list only the tags its own publishes created.
/// </param>
public record SessionCreatedTagEntry(
    string         TagName,
    DateTimeOffset CreatedAt,
    string         ProposalId,
    string?        Zone = null);

// ── TagSessionService ─────────────────────────────────────────────────────────

/// <summary>
/// Singleton service that holds the active <see cref="ITagService"/> session for
/// tag read, write, and poll operations.
///
/// <para>
/// A session is established via <see cref="Attach"/> and cleared via
/// <see cref="Detach"/>.  All four MCP tag tools (<c>logix_connect</c>,
/// <c>logix_read</c>, <c>logix_write</c>, <c>logix_poll</c>) share this singleton.
/// </para>
///
/// <para>
/// The service also maintains a <em>session-created tag registry</em>: every tag
/// created on the controller during the current session (via a controller-path
/// rung-edit proposal) is recorded here.  <c>logix_write</c> denies writes to any
/// tag in this registry with <see cref="DenyReason.CreatedByThisSession"/>.  The
/// registry is cleared whenever <see cref="Attach"/> is called (i.e. after a new
/// <c>logix_connect</c>), after which the tag follows ordinary gate policy.
/// </para>
/// </summary>
public class TagSessionService : IDisposable
{
    private int _disposed;

    /// <summary>
    /// Closes the active SDK session when the server shuts down (the host disposes this
    /// singleton), through the same seam <c>logix_close_project</c> uses. Without it an exiting
    /// server leaves its project open in LdSdkServer until the keep-alive reaper disposes it,
    /// and that reaper crashes the service. Best effort; never throws.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { CloseProjectHandler.SdkSessionDisposer?.Invoke(this); }
        catch { /* best effort at shutdown */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The connect state this session service reports to: <c>logix_close_project</c> marks it closed so the
    /// id-less <c>logix_connect_status</c> stops answering <c>connected</c>. Set by the tool class that owns both;
    /// <c>null</c> when none is wired (tests, core-only use).
    /// </summary>
    public ConnectState? ConnectState { get; set; }

    private ITagService? _service;
    private bool _isVirtual;
    private bool _isLiveController;
    private string? _controllerHost;
    private L5X? _controllerImage;
    private DateTimeOffset? _controllerImageTakenAt;
    private DateTimeOffset? _lastLiveEditAt;
    private DateTimeOffset? _controllerImageRefreshedAt;
    private bool _imageStaleAll;
    private bool _imageStaleControllerTags;
    private string? _imageRefreshError;
    private readonly HashSet<string> _staleImagePrograms = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _imageRefreshGate = new(1, 1);
    private (L5X Session, L5X Accepted)? _acceptedCache;
    private readonly Dictionary<string, string> _staleBecause = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _fingerprintGate = new(1, 1);
    private DateTimeOffset? _fingerprintAt;
    private ControllerChangeInfo? _controllerChange;
    private string? _refreshedBecause;

    // Routines ("Program/Routine") and tag names changed live by this session.
    private readonly HashSet<string> _liveEditedRoutines = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveEditedTags     = new(StringComparer.OrdinalIgnoreCase);

    // Case-insensitive registry of tags created by this server process in the
    // current session.  Cleared on every Attach() call.
    private readonly Dictionary<string, SessionCreatedTagEntry> _sessionCreatedTags =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The write allowlist active for this session, or <c>null</c>. Cleared by
    /// <see cref="Attach"/>; set by <see cref="SetWriteAllowlist"/> after a successful connect.
    /// </summary>
    public WriteAllowlist? WriteAllowlist { get; private set; }

    /// <summary>
    /// Activates <paramref name="allowlist"/> for the current session: <c>logix_run_test</c> and
    /// ungated writes consult it, and a <see cref="GatedTagService"/> session enforces it in the gate.
    /// </summary>
    /// <param name="allowlist">The loaded allowlist, or <c>null</c> to clear.</param>
    public void SetWriteAllowlist(WriteAllowlist? allowlist)
    {
        WriteAllowlist = allowlist;
        if (_service is GatedTagService gated) gated.ApplyAllowlist(allowlist);
    }

    /// <summary>
    /// Gets the active <see cref="ITagService"/>, or <c>null</c> when no session
    /// has been established.
    /// </summary>
    public ITagService? Service => _service;

    /// <summary>
    /// The tag route (<c>"cip"</c> or <c>"sdk"</c>) the session's <see cref="RoutedTagService"/> has
    /// taken, or <c>null</c> when the session is not routed (virtual, L5X, CIP-only) or no read or
    /// write has chosen a route yet. Read after a call, it is the route that call used.
    /// </summary>
    public string? ActiveRoute => RoutedService?.ActiveRoute;

    /// <summary>
    /// The session's <see cref="RoutedTagService"/> (the <c>LGX_TAG_ROUTE</c> router), or <c>null</c> when
    /// the session is not routed. Read-only access for reporting; it never chooses a route.
    /// </summary>
    public RoutedTagService? RoutedService =>
        (_service is GatedTagService gated ? gated.InnerService : _service) as RoutedTagService;

    /// <summary>
    /// Returns <c>true</c> when an active tag service session exists.
    /// </summary>
    public bool IsConnected => _service is not null && _sessionLost is null;

    private SessionLostInfo? _sessionLost;

    /// <summary>
    /// Set when a call found the session's project closed (<c>"Project is not open."</c>); <c>null</c> otherwise.
    /// A lost session reports <see cref="IsConnected"/> <c>false</c> until <c>logix_connect</c> attaches a new one
    /// (or it is detached).
    /// </summary>
    public SessionLostInfo? SessionLost => _sessionLost;

    /// <summary>
    /// Marks the active session lost. Idempotent: the first cause is kept. Does nothing without a session.
    /// </summary>
    /// <param name="cause">What was observed (the SDK's message). It is kept as <see cref="SessionLostInfo.Observed"/>;
    /// the recorded cause is chosen from <see cref="LostEvidence"/>.</param>
    /// <returns>The session-lost record, or <c>null</c> when there is no session to mark.</returns>
    public SessionLostInfo? MarkSessionLost(string? cause)
    {
        if (_service is null) return _sessionLost;
        if (_sessionLost is not null) return _sessionLost;
        var (chosen, evidence) = LostEvidence.Decide(_serverPidsAtConnect);
        var restartedAt = chosen == SessionLostCauses.SdkServiceRestarted ? LostEvidence.SdkRestartedAt(_serverPidsAtConnect) : null;
        return _sessionLost = new SessionLostInfo(chosen, DateTimeOffset.UtcNow, cause, evidence, restartedAt);
    }

    /// <summary>
    /// Marks the active session lost when it depends on the SDK service and the <c>LdSdkServer</c> it was attached to is
    /// gone (G50). A reconnect that fails leaves the old session attached; if the server restarted since, that session
    /// can no longer be used, so it must report <c>connected:false</c> rather than look live.
    /// </summary>
    /// <returns>The session-lost record when the session was marked lost now; otherwise <c>null</c>.</returns>
    public SessionLostInfo? MarkLostIfSdkRestarted()
    {
        if (!WatchesSdkService || !LostEvidence.ServerRestarted(_serverPidsAtConnect)) return null;
        return MarkSessionLost(null);
    }

    private IReadOnlyCollection<int> _serverPidsAtConnect = [];

    /// <summary>The <c>LdSdkServer</c> pids recorded when the session was attached.</summary>
    public IReadOnlyCollection<int> ServerPidsAtConnect => _serverPidsAtConnect;

    /// <summary>How often a running tool call checks the SDK server pid; see <see cref="SessionLostSupport.CheckServiceRestart"/>.</summary>
    public TimeSpan RestartPollInterval { get; set; } = SessionLostSupport.DefaultRestartPollInterval;

    /// <summary>
    /// True when the live session depends on the SDK service (a live controller, or a service from the SDK adapter
    /// assembly) and has not been lost: calls on it are watched for a restart of the SDK service.
    /// </summary>
    public bool WatchesSdkService
    {
        get
        {
            if (_service is null || _isVirtual || _sessionLost is not null || _serverPidsAtConnect.Count == 0) return false;
            if (_isLiveController) return true;
            var inner = _service is GatedTagService gated ? gated.InnerService : _service;
            return inner.GetType().Assembly.GetName().Name == "LogixMcp.SdkAdapter";
        }
    }

    /// <summary>
    /// Where the cause of a lost session is chosen from. Replaceable so tests can stand in for the SDK service
    /// and for a second server process.
    /// </summary>
    public SessionLostEvidence LostEvidence { get; set; } = new();

    /// <summary>
    /// Returns <c>true</c> when the active session is backed by an in-memory
    /// <see cref="VirtualTagService"/> rather than a live PLC.
    /// </summary>
    public bool IsVirtual => _isVirtual;

    /// <summary>
    /// Returns <c>true</c> when the active session targets a live physical
    /// controller — either via a CIP EtherNet/IP connection
    /// (<c>backend="cip"</c>) or via the Logix Designer SDK in online mode
    /// (<c>backend="sdk"</c>, <c>sdkMode="online"</c>).
    /// <c>false</c> for every offline session (virtual, L5X, SDK offline).
    /// </summary>
    public bool IsLiveController => _isLiveController;

    /// <summary>
    /// Host (IP or name) of the controller the active session targets, when known; used by
    /// <c>logix_run_test</c> to read ListIdentity. <c>null</c> for offline sessions and when
    /// the connect path did not expose an address.
    /// </summary>
    public string? ControllerHost => _controllerHost;

    /// <summary>
    /// Controller the session targets as <c>gateway/path</c>, recorded in audit
    /// <c>write</c> records; <c>null</c> for sessions without a controller address
    /// (virtual, L5X). Set by <c>logix_connect</c>; cleared on attach and detach.
    /// </summary>
    public string? AuditController { get; private set; }

    /// <summary>
    /// Major revision and mnemonic set of the session's target, recorded by <c>logix_connect</c>
    /// (the uploaded project's value for an online target). <c>null</c> when no session is attached
    /// or none was recorded. Cleared by <see cref="Attach"/> and <see cref="Detach"/>.
    /// </summary>
    public TargetRevisionInfo? TargetRevision { get; private set; }

    /// <summary>Records <see cref="TargetRevision"/> for the current session.</summary>
    /// <param name="info">The detected revision, or <c>null</c> to clear.</param>
    public void SetTargetRevision(TargetRevisionInfo? info) => TargetRevision = info;

    /// <summary>Records the <see cref="AuditController"/> label for the current session.</summary>
    public void SetAuditController(string? controller) => AuditController = controller;

    /// <summary>
    /// Why the current session has no project-derived write gate even though a project is
    /// open (e.g. it is a partial export), or <c>null</c>. Cleared by <see cref="Attach"/>
    /// and <see cref="Detach"/>; set with <see cref="SetNoGateReason"/> after connect.
    /// </summary>
    public string? NoGateReason { get; private set; }

    /// <summary>Records <see cref="NoGateReason"/> for the current session.</summary>
    /// <param name="reason">The reason, or <c>null</c> to clear.</param>
    public void SetNoGateReason(string? reason) => NoGateReason = reason;

    /// <summary>
    /// <c>true</c> when the session was deliberately connected without a write gate (a
    /// partial export with no allowlist) and so refuses every write with
    /// <c>WriteGateRequired</c>, whether or not it targets a live controller. Cleared by
    /// <see cref="Attach"/> and <see cref="Detach"/>.
    /// </summary>
    public bool WritesRequireGate { get; private set; }

    /// <summary>
    /// Marks the session as connected without a write gate: its writes are refused with
    /// <c>WriteGateRequired</c> unless an allowlist is active.
    /// </summary>
    /// <param name="reason">Why there is no gate (see <see cref="NoGateReason"/>).</param>
    public void MarkNoWriteGate(string? reason)
    {
        WritesRequireGate = true;
        NoGateReason      = reason;
    }

    /// <summary>
    /// Reason shown when an ungated session outlives the file it was connected with: the
    /// project open now is not the one the session's (absent) gate came from.
    /// </summary>
    public const string StaleNoGateReason =
        "this session connected without a write gate, and the project opened since is not enforced by it";

    /// <summary>
    /// Re-derives <see cref="NoGateReason"/> from the project that is open now. Call after a
    /// project is opened or closed, so a session that connected on a partial export does not
    /// keep blaming a partial export once a full export is open (and vice versa). A session
    /// that has a write gate keeps none of this state and is left untouched.
    /// </summary>
    /// <param name="project">The project service whose current file is reported.</param>
    public void RefreshNoGateReason(ProjectService project)
    {
        if (_service is null or GatedTagService) return;
        if (NoGateReason is null && !WritesRequireGate) return;   // no project at connect: generic message stands

        NoGateReason = !project.IsLoaded ? null
            : project.PartialExport is not null ? PartialExportInfo.Reason
            : StaleNoGateReason;
    }

    /// <summary>
    /// The detailed L5X image of the controller taken by the session's upload
    /// (<c>SdkConnectFactory.DefaultSaveAs</c>, <c>detailedL5x</c>), or <c>null</c> when the
    /// session was not established by an SDK controller upload. Cleared on
    /// <see cref="Attach"/> and <see cref="Detach"/>.
    /// </summary>
    public L5X? ControllerImage => _controllerImage;

    /// <summary>UTC time the session's controller upload ran, or <c>null</c> when there is no image.</summary>
    public DateTimeOffset? ControllerImageTakenAt => _controllerImageTakenAt;

    /// <summary>
    /// UTC time of the most recent lgx-mcp live edit (rung or tag change applied to the running
    /// controller) in this session, or <c>null</c> when none happened. Used by
    /// <c>logix_diff_against_controller</c> to flag the controller image as stale.
    /// </summary>
    public DateTimeOffset? LastLiveEditAt => _lastLiveEditAt;

    /// <summary>
    /// Records the controller image taken by this session's upload. Call after
    /// <see cref="Attach"/> (which clears any previous image).
    /// </summary>
    /// <param name="image">Parsed detailed L5X of the uploaded controller.</param>
    /// <param name="takenAt">UTC time the upload ran.</param>
    public void SetControllerImage(L5X image, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(image);
        _controllerImage        = image;
        _controllerImageTakenAt = takenAt;
        ClearImageStaleness();
    }

    // ── Same-session image refresh ───────────────────

    /// <summary>
    /// Where a refresh exports from. When <c>null</c> the session's own backend is used if it implements
    /// <see cref="IControllerImageSource"/>. Cleared on <see cref="Attach"/> and <see cref="Detach"/>.
    /// </summary>
    public IControllerImageSource? ImageSourceOverride { get; set; }

    /// <summary>UTC time the controller image was last refreshed after a same-session change, or <c>null</c>.</summary>
    public DateTimeOffset? ControllerImageRefreshedAt => _controllerImageRefreshedAt;

    /// <summary>
    /// <c>true</c> when the session changed the controller (publish, stage, cancel, created tag) after the
    /// controller image was last taken or refreshed, so the image does not yet show that change.
    /// </summary>
    public bool IsImageStale => _imageStaleAll || _imageStaleControllerTags || _staleImagePrograms.Count > 0;

    /// <summary>
    /// The accepted image: the controller's logic as it stands without staged or pending-zone rungs (displaced
    /// originals are kept as ordinary rungs). This is what <c>includePending: false</c> reads. <c>null</c> when
    /// the session holds no controller image.
    /// </summary>
    public L5X? AcceptedControllerImage
    {
        get
        {
            if (_controllerImage is not { } session) return null;
            if (_acceptedCache is { } cached && ReferenceEquals(cached.Session, session)) return cached.Accepted;
            var accepted = ControllerImageRefresh.Accepted(session);
            _acceptedCache = (session, accepted);
            return accepted;
        }
    }

    private readonly Dictionary<string, bool> _zoneTested = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records whether a program's pending-edits zone is tested (<c>true</c>), untested (<c>false</c>) or
    /// unknown (<c>null</c>, which forgets the program). Stamped from the pending-edits read and the test /
    /// untest verbs; cleared on accept, cancel, assemble, <see cref="Attach"/> and <see cref="Detach"/>.
    /// </summary>
    public void SetZoneTested(string program, bool? tested)
    {
        if (string.IsNullOrWhiteSpace(program)) return;
        lock (_zoneTested)
        {
            if (tested is { } t) _zoneTested[program.Trim()] = t;
            else _zoneTested.Remove(program.Trim());
        }
    }

    /// <summary>The recorded zone-tested state of <paramref name="program"/>, or <c>null</c> when it is not known.</summary>
    public bool? ZoneTested(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        lock (_zoneTested)
            return _zoneTested.TryGetValue(program.Trim(), out var t) ? t : null;
    }

    /// <summary>
    /// Marks the controller image stale because this session changed the controller or the session image.
    /// The next analysis call refreshes it (see <see cref="RefreshControllerImageAsync"/>).
    /// </summary>
    /// <param name="program">The program whose logic or tags changed, or <c>null</c> when it is not known (all programs).</param>
    /// <param name="controllerTags"><c>true</c> when controller-scoped tags may have changed.</param>
    public void MarkImageStale(string? program = null, bool controllerTags = false)
    {
        if (_controllerImage is null) return;
        if (string.IsNullOrWhiteSpace(program)) _imageStaleAll = true;
        else _staleImagePrograms.Add(program.Trim());
        if (controllerTags) _imageStaleControllerTags = true;
    }

    /// <summary>
    /// Marks <paramref name="program"/> of the controller image stale because the controller changed outside this
    /// session (<see cref="OutOfBandReason"/>). The next successful refresh of the program records the reason, so the
    /// first read after it carries <c>imageRefreshedBecause</c> (see <see cref="TakeImageRefreshedBecause"/>).
    /// </summary>
    /// <param name="program">The program whose content differs from the image.</param>
    /// <param name="because">Why the program is stale.</param>
    public void MarkImageStale(string program, string because)
    {
        if (_controllerImage is null || string.IsNullOrWhiteSpace(program)) return;
        MarkImageStale(program);
        _staleBecause[program.Trim()] = because;
    }

    /// <summary>The reason an image refresh is forced when the fingerprint shows the controller changed under the session.</summary>
    public const string OutOfBandReason = "controller changed outside this session";

    /// <summary>
    /// Note to attach to an analysis result that ran on an image which could not be brought up to date, or
    /// <c>null</c> when the image is current (or there is no image).
    /// </summary>
    public string? ImageStaleNote =>
        !IsImageStale || _controllerImage is null ? null
        : "controller image is stale: this session changed the controller after the image was taken and the refresh " +
          (_imageRefreshError is null ? "has not run" : $"failed ({_imageRefreshError})") +
          ", so this result may miss those changes. Reconnect to take a fresh image.";

    /// <summary>
    /// Brings the controller image up to date after same-session changes: one partial export of each affected
    /// program (and of the controller tags when those changed), merged into the image. Does nothing when the image
    /// is current. On failure the image stays stale and <see cref="ImageStaleNote"/> says so.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the image is current afterwards.</returns>
    public async Task<bool> RefreshControllerImageAsync(CancellationToken ct = default)
    {
        if (!IsImageStale) return true;
        await _imageRefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsImageStale) return true;
            if (_controllerImage is not { } image) { ClearImageStaleness(); return true; }

            var source = ImageSourceOverride ?? TagRouteResolver.Backend(_service) as IControllerImageSource;
            if (source is null)
            {
                _imageRefreshError = "this session cannot export from the controller";
                SessionImageState.MarkStale(this, $"refresh of the controller image failed: {_imageRefreshError}");
                return false;
            }

            string? failedProgram = null;
            string? because       = null;
            try
            {
                // Work on a copy so a failed export leaves the image as it was.
                var next = SafeXml.ParseL5x(image.ToString());
                var programs = _imageStaleAll
                    ? next.Programs.Select(p => p.Name.ToString()).ToList()
                    : _staleImagePrograms.ToList();
                because = programs.Select(p => _staleBecause.TryGetValue(p, out var b) ? b : null).FirstOrDefault(b => b is not null);
                foreach (var program in programs)
                {
                    failedProgram = program;
                    var text = await source.ExportProgramL5xAsync(program, ct).ConfigureAwait(false);
                    if (!ControllerImageRefresh.MergeProgram(next, text, program))
                        throw new InvalidOperationException($"the export held no program '{program}'");
                    // The export can show this session's workstation-staged rungs as plain N: stamp them IR/I (and the
                    // originals they displace R/D) so the accepted view never holds a plain copy. Nothing else is replayed.
                    next = SessionImageState.ReapplyStaged(this, next, program, (source as IStagedRungSource)?.StagedBlocks(program));
                }
                failedProgram = null;
                if (_imageStaleAll || _imageStaleControllerTags)
                {
                    var text = await source.ExportControllerTagsL5xAsync(ct).ConfigureAwait(false);
                    ControllerImageRefresh.MergeControllerTags(next, text);
                }

                _controllerImage            = next;
                _controllerImageRefreshedAt = DateTimeOffset.UtcNow;
                if (because is not null) _refreshedBecause = because;
                ClearImageStaleness();
                // The stale mark is keyed by the image's take time; clear it after the new image is installed.
                SessionImageState.ClearStale(this);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _imageRefreshError = ex.Message;
                SessionImageState.MarkStale(this, failedProgram is null
                    ? $"refresh of the controller tags failed: {ex.Message}"
                    : $"refresh of program '{failedProgram}' failed: {ex.Message}");
                return false;
            }
        }
        finally
        {
            _imageRefreshGate.Release();
        }
    }

    private void ClearImageStaleness()
    {
        _imageStaleAll            = false;
        _imageStaleControllerTags = false;
        _imageRefreshError        = null;
        _staleImagePrograms.Clear();
        _staleBecause.Clear();
    }

    // ── Out-of-band controller change detection ─────────────────────

    /// <summary>
    /// Reads the controller's fingerprint (per program: routine names, rung count per routine and <c>TestEdits</c>, plus the
    /// controller's name and serial). Set by the SDK adapter; <c>null</c> when the session cannot probe, in which case no
    /// out-of-band check runs. Not cleared by <see cref="Attach"/> or <see cref="Detach"/>: it reads whatever session is active.
    /// The source returns <c>null</c> (or throws) when it cannot read; the check is then skipped, never an error.
    /// </summary>
    public Func<CancellationToken, Task<ControllerFingerprint?>>? ControllerFingerprintSource { get; set; }

    /// <summary>
    /// How long a fingerprint probe stays valid: reads within it cost no probe. From <c>LGX_IMAGE_FINGERPRINT_TTL_S</c>
    /// (seconds, default 5; 0 probes on every read).
    /// </summary>
    public TimeSpan ImageFingerprintTtl { get; set; } = DefaultFingerprintTtl();

    private static TimeSpan DefaultFingerprintTtl()
    {
        var raw = Environment.GetEnvironmentVariable("LGX_IMAGE_FINGERPRINT_TTL_S");
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(5);
    }

    /// <summary>Age in milliseconds of the last successful fingerprint probe, or <c>null</c> when none has run on this session.</summary>
    public long? FingerprintAgeMs =>
        _fingerprintAt is { } at ? Math.Max(0, (long)(DateTimeOffset.UtcNow - at).TotalMilliseconds) : null;

    /// <summary>
    /// Set when the fingerprint showed a different controller (name or serial) or a re-download; image readers then return
    /// <see cref="ErrorKinds.ControllerChanged"/> until <see cref="Attach"/> (a new <c>logix_connect</c>). <c>null</c> otherwise.
    /// </summary>
    public ControllerChangeInfo? ControllerChange => _controllerChange;

    /// <summary>
    /// Returns the reason the last image refresh was forced by an out-of-band change and forgets it, so only the first
    /// read after the refresh reports it; <c>null</c> when there is none.
    /// </summary>
    public string? TakeImageRefreshedBecause() => Interlocked.Exchange(ref _refreshedBecause, null);

    /// <summary>
    /// Compares the controller's fingerprint with the session image and marks what differs: a program whose routine set,
    /// rung counts or <c>TestEdits</c> differ is marked stale (<see cref="OutOfBandReason"/>) so the refresh that follows
    /// re-exports it; a different controller or a re-download sets <see cref="ControllerChange"/> and marks every program stale.
    /// Probes only when the last probe is older than <see cref="ImageFingerprintTtl"/> (or there was none), unless forced.
    /// A source that fails or returns nothing leaves everything as it was.
    /// </summary>
    /// <param name="force"><c>true</c> to probe regardless of the TTL.</param>
    /// <param name="ct">Cancellation token.</param>
    public async System.Threading.Tasks.Task CheckControllerFingerprintAsync(bool force = false, CancellationToken ct = default)
    {
        if (ControllerFingerprintSource is not { } source || _controllerImage is null || _controllerChange is not null) return;
        await _fingerprintGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_controllerChange is not null || _controllerImage is not { } image) return;
            if (!force && _fingerprintAt is { } at && DateTimeOffset.UtcNow - at < ImageFingerprintTtl) return;

            ControllerFingerprint? now;
            try { now = await source(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return; }
            if (now is null) return;
            _fingerprintAt = DateTimeOffset.UtcNow;

            // Programs already waiting for a refresh (this session changed them) are not compared: the refresh covers them.
            var comparison = AnalysisImage.Compare(
                ControllerFingerprint.FromL5x(image.ToString()), now, p => _imageStaleAll || _staleImagePrograms.Contains(p));
            if (comparison.Change is { } change)
            {
                _controllerChange = change;
                MarkImageStale();
                return;
            }
            foreach (var program in comparison.ChangedPrograms)
                MarkImageStale(program, OutOfBandReason);
        }
        finally
        {
            _fingerprintGate.Release();
        }
    }

    /// <summary>
    /// Records that an lgx-mcp live edit changed the running controller just now, which makes
    /// the controller image taken at connect time stale.
    /// </summary>
    public void RecordLiveEdit() => _lastLiveEditAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Records a live edit and remembers which routine and tags it changed, so
    /// <c>logix_diff_against_controller</c> can label matching diff entries
    /// <c>changedByThisSession</c>.
    /// </summary>
    /// <param name="routineKey">Edited routine as <c>ProgramName/RoutineName</c>, or <c>null</c>.</param>
    /// <param name="tagNames">Names of tags created or changed live, or <c>null</c>.</param>
    public void RecordLiveEdit(string? routineKey, IEnumerable<string>? tagNames = null)
    {
        RecordLiveEdit();
        var tagList = tagNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        // The edit changed the session image: the routine's program (all programs when unknown) is stale, and so
        // are controller tags when a tag was created.
        MarkImageStale(string.IsNullOrWhiteSpace(routineKey) ? null : routineKey.Split('/', 2)[0],
            controllerTags: tagList is { Count: > 0 });
        if (!string.IsNullOrWhiteSpace(routineKey)) _liveEditedRoutines.Add(routineKey);
        if (tagList is null) return;
        foreach (var name in tagList) _liveEditedTags.Add(name);
    }

    /// <summary>Returns <c>true</c> when this session live-edited the routine <c>ProgramName/RoutineName</c>.</summary>
    /// <param name="routineKey">Routine key as used by the diff (<c>ProgramName/RoutineName</c>).</param>
    public bool WasRoutineLiveEdited(string routineKey) => _liveEditedRoutines.Contains(routineKey);

    /// <summary>Returns <c>true</c> when this session created or changed the named tag live.</summary>
    /// <param name="tagName">Tag name as used by the diff.</param>
    public bool WasTagLiveEdited(string tagName) => _liveEditedTags.Contains(tagName);

    /// <summary>
    /// Attaches <paramref name="service"/> as the active session.
    /// Any previously attached service is replaced without calling
    /// <see cref="ITagService.DisconnectAsync"/> on the old instance.
    /// A disconnect event is emitted to the audit log when an existing session
    /// is implicitly displaced by the new one.
    ///
    /// <para>
    /// The session-created tag registry is cleared on every call so that tags
    /// introduced in the previous session are no longer blocked in the new one.
    /// </para>
    /// </summary>
    /// <param name="service">The <see cref="ITagService"/> to store.</param>
    /// <param name="isVirtual">
    /// <c>true</c> when <paramref name="service"/> is backed by
    /// <see cref="VirtualTagService"/>; <c>false</c> for a live PLC connection.
    /// </param>
    /// <param name="isLiveController">
    /// <c>true</c> when the session targets a live physical controller (CIP or
    /// SDK online). <c>false</c> for virtual, L5X, and SDK offline sessions.
    /// Defaults to <c>false</c>.
    /// </param>
    /// <param name="controllerHost">
    /// Host (IP or name) of the controller the session targets, when known.
    /// </param>
    public void Attach(ITagService service, bool isVirtual, bool isLiveController = false,
        string? controllerHost = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        // Log the implicit disconnect before replacing the existing session.
        if (_service is not null)
            AuditLogger.LogDisconnect();

        _service           = service;
        _sessionLost       = null;
        _serverPidsAtConnect = LostEvidence.SnapshotServerPids();
        _isVirtual         = isVirtual;
        _isLiveController  = isLiveController;
        _controllerHost    = controllerHost;
        _controllerImage        = null;
        _controllerImageTakenAt = null;
        _lastLiveEditAt         = null;
        _controllerImageRefreshedAt = null;
        _acceptedCache          = null;
        ImageSourceOverride     = null;
        ClearImageStaleness();
        _fingerprintAt          = null;
        _controllerChange       = null;
        _refreshedBecause       = null;
        _zoneTested.Clear();
        _liveEditedRoutines.Clear();
        _liveEditedTags.Clear();
        WriteAllowlist = null;
        AuditController = null;
        TargetRevision = null;
        NoGateReason = null;
        WritesRequireGate = false;

        // Clear the session-created tag registry so the new session starts clean.
        _sessionCreatedTags.Clear();
    }

    // ── Session-created tag registry ──────────────────────────────────────────

    /// <summary>
    /// Records <paramref name="tagNames"/> as having been created on the controller
    /// during this session by the proposal identified by <paramref name="proposalId"/>.
    ///
    /// <para>
    /// Once recorded, any attempt to write these tags via <c>logix_write</c> is
    /// denied with <see cref="DenyReason.CreatedByThisSession"/> for the lifetime
    /// of the current session.  The registry is cleared on the next
    /// <see cref="Attach"/> call (i.e. after a new <c>logix_connect</c>).
    /// </para>
    ///
    /// <para>
    /// If a tag name is already in the registry (e.g. re-published in a later
    /// proposal), the existing entry is replaced with the new proposal ID and
    /// current timestamp.
    /// </para>
    /// </summary>
    /// <param name="tagNames">
    /// The names of the tags created on the controller by this proposal.
    /// Case-insensitive.
    /// </param>
    /// <param name="proposalId">
    /// An opaque identifier for the proposal that created the tags (e.g. a short
    /// UUID).  Included in write-denial messages as provenance so the agent can
    /// trace a denied write back to the originating operation.
    /// </param>
    /// <param name="zone">
    /// Optional edit zone (<c>Program/Routine</c>) the publish targeted; recorded so
    /// <see cref="SessionCreatedTagNames(string, string)"/> can list the tags per zone.
    /// </param>
    public void RecordCreatedTags(IEnumerable<string> tagNames, string proposalId, string? zone = null)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var now = DateTimeOffset.UtcNow;
        foreach (var name in tagNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                _sessionCreatedTags[name] = new SessionCreatedTagEntry(name, now, proposalId, zone);
        }
    }

    /// <summary>
    /// Refreshes the session's declared-type lookup for tags a publish just created, so that a read of
    /// one over either route is typed, not <c>TypeUnknown</c>. The lookup is the SDK
    /// service's own: each tag is re-exported from the SDK session (a partial export) and added to it.
    /// Does nothing when the session has no such lookup. Never throws except for cancellation.
    /// </summary>
    /// <param name="tagNames">Names of the tags the publish created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The names that are now known to the lookup.</returns>
    public async Task<IReadOnlyList<string>> RefreshTypesForCreatedTagsAsync(
        IEnumerable<string> tagNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tagNames);
        if (TagRouteResolver.Backend(_service) is not ISessionTypeSource types)
            return Array.Empty<string>();
        return await types.RefreshTagsAsync(tagNames, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Names of the tags this session's publishes created on the controller, oldest first.
    /// </summary>
    /// <returns>The recorded tag names; empty when none.</returns>
    public IReadOnlyList<string> SessionCreatedTagNames() =>
        _sessionCreatedTags.Values.OrderBy(e => e.CreatedAt).Select(e => e.TagName).ToList();

    /// <summary>
    /// Names of the tags created by publishes that targeted the zone <paramref name="program"/>/<paramref name="routine"/>,
    /// oldest first. Tags created for other zones, or with no recorded zone, are not listed.
    /// </summary>
    /// <param name="program">Program of the zone (case-insensitive).</param>
    /// <param name="routine">Routine of the zone (case-insensitive).</param>
    /// <returns>The recorded tag names for that zone; empty when none.</returns>
    public IReadOnlyList<string> SessionCreatedTagNames(string program, string routine)
    {
        var zone = ZoneKey(program, routine);
        return _sessionCreatedTags.Values
            .Where(e => string.Equals(e.Zone, zone, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.TagName)
            .ToList();
    }

    /// <summary>
    /// Lists the tags created by publishes that targeted the zone (as <see cref="SessionCreatedTagNames(string, string)"/>)
    /// and then detaches them from it: a cancel discards the zone, so a later cancel of the same zone lists only the tags
    /// created after this one. The tags stay in the write-denial registry.
    /// </summary>
    /// <param name="program">Program of the zone (case-insensitive).</param>
    /// <param name="routine">Routine of the zone (case-insensitive).</param>
    /// <returns>The recorded tag names for that zone, oldest first; empty when none.</returns>
    public IReadOnlyList<string> TakeZoneCreatedTagNames(string program, string routine)
    {
        var names = SessionCreatedTagNames(program, routine);
        foreach (var name in names)
            if (_sessionCreatedTags.TryGetValue(name, out var entry))
                _sessionCreatedTags[name] = entry with { Zone = null };
        return names;
    }

    /// <summary>The registry key of an edit zone: <c>Program/Routine</c>.</summary>
    /// <param name="program">Program name.</param>
    /// <param name="routine">Routine name.</param>
    public static string ZoneKey(string program, string routine) => $"{program}/{routine}";

    /// <summary>
    /// Looks up <paramref name="tagName"/> in the session-created tag registry.
    /// </summary>
    /// <param name="tagName">
    /// Tag name to look up (case-insensitive).
    /// </param>
    /// <param name="entry">
    /// Set to the matching <see cref="SessionCreatedTagEntry"/> when found;
    /// <c>null</c> when the tag is not in the registry.
    /// </param>
    /// <returns>
    /// <c>true</c> when <paramref name="tagName"/> is in the registry;
    /// <c>false</c> otherwise.
    /// </returns>
    public bool TryGetSessionCreatedTagEntry(string tagName,
        out SessionCreatedTagEntry? entry) =>
        _sessionCreatedTags.TryGetValue(tagName, out entry);

    /// <summary>
    /// Clears the active session.  Does not call
    /// <see cref="ITagService.DisconnectAsync"/>; callers are responsible for
    /// disconnecting before detaching if needed.
    /// A disconnect event is emitted to the audit log when an active session exists.
    /// </summary>
    public void Detach()
    {
        if (_service is not null)
            AuditLogger.LogDisconnect();

        _service          = null;
        _sessionLost      = null;
        _serverPidsAtConnect = [];
        _isVirtual        = false;
        _isLiveController = false;
        _controllerHost   = null;
        AuditController   = null;
        TargetRevision    = null;
        NoGateReason      = null;
        WritesRequireGate = false;
        _controllerImage        = null;
        _controllerImageTakenAt = null;
        _lastLiveEditAt         = null;
        _controllerImageRefreshedAt = null;
        _acceptedCache          = null;
        ImageSourceOverride     = null;
        ClearImageStaleness();
        _fingerprintAt          = null;
        _controllerChange       = null;
        _refreshedBecause       = null;
        _zoneTested.Clear();
        _liveEditedRoutines.Clear();
        _liveEditedTags.Clear();
    }
}
