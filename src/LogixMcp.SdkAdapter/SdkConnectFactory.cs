#if LOGIX_SDK
// ── SdkConnectFactory — adapter implementation of ISdkTagServiceFactory ───────
using System.Runtime.CompilerServices;
using L5Sharp.Core;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;

// Alias System.Threading.Tasks.Task to avoid ambiguity with L5Sharp.Core.Task.
using Task                = System.Threading.Tasks.Task;
using OperationMode       = RockwellAutomation.LogixDesigner.LogixProject.OperationMode;
using ConnectedState      = RockwellAutomation.LogixDesigner.LogixProject.ConnectedState;
using ILogixProjectHandle = RockwellAutomation.LogixDesigner.ILogixProject;

/// <summary>
/// Adapter implementation of <see cref="ISdkTagServiceFactory"/> that creates
/// a <see cref="TypedSdkTagService"/> backed by a new
/// <see cref="LogixProjectSession"/> for each connect call.
///
/// <para>
/// Registered as <see cref="ConnectHandler.DefaultSdkFactory"/> by an explicit call to
/// <see cref="SdkAdapterInitializer.Initialize"/> after the adapter assembly is loaded —
/// see <see cref="SdkAdapterLoader.TryLoad"/> for the invocation site.
/// </para>
/// </summary>
internal sealed class SdkConnectFactory : ISdkTagServiceFactory
{
    // ── Injectable factories (production vs. test) ────────────────────────────

    private readonly Func<LogixProjectSession> _sessionFactory;

    /// <summary>
    /// Delegate that performs <c>LogixProject.UploadToNewProjectAsync</c>.
    /// Injectable for unit tests so no live SDK is required.
    /// Signature: <c>(acdPath, commPath, ct) -&gt; Task&lt;ILogixProject&gt;</c>.
    /// </summary>
    private readonly Func<string, string, CancellationToken,
        System.Threading.Tasks.Task<ILogixProjectHandle>> _controllerUploadFactory;

    /// <summary>
    /// Delegate that performs <c>ILogixProject.SaveAsAsync</c> to export the
    /// uploaded project to L5X.
    /// Injectable for unit tests.
    /// Signature: <c>(project, l5xPath, ct) -&gt; Task</c>.
    /// </summary>
    private readonly Func<ILogixProjectHandle, string, CancellationToken, Task> _saveAsFactory;

    /// <summary>
    /// Delegate that parses a file as <see cref="L5X"/>.
    /// Injectable for unit tests so tests can provide a canned L5X without
    /// writing to disk.
    /// Signature: <c>(l5xPath) -&gt; L5X</c>.
    /// </summary>
    private readonly Func<string, L5X> _l5xLoader;

    /// <summary>
    /// Delegate that queries whether the <c>LdSdkService</c> Windows service is
    /// currently running.  Injectable for unit tests to simulate stopped/running
    /// service states without requiring a live Windows service.
    ///
    /// <para>
    /// Returns <c>true</c> when the service is running; <c>false</c> when it is
    /// stopped or could not be determined to be running.  On any query failure
    /// (service not installed, access denied, etc.) the default implementation
    /// returns <c>true</c> so that callers fall through to the pending-edit
    /// possibility message rather than asserting a stopped service.
    /// </para>
    /// </summary>
    private readonly Func<bool> _ldSdkServiceRunning;

    /// <summary>
    /// Delegate that checks whether at least one <c>LdSdkServer.exe</c> process
    /// is visible in the process list.  Injectable for unit tests.
    ///
    /// <para>
    /// Returns <c>true</c> when at least one <c>LdSdkServer</c> process is
    /// found; <c>false</c> when no such process is running.  On any query failure
    /// the default implementation returns <c>true</c> to avoid falsely asserting
    /// a dead service.
    /// </para>
    ///
    /// <para>
    /// Process absence is treated as equivalent to "not running" independently of
    /// the SCM state reported by <see cref="_ldSdkServiceRunning"/>.  This covers
    /// the measured race where SCM lagged behind process death by up to 5 seconds.
    /// </para>
    /// </summary>
    private readonly Func<bool> _ldSdkServerProcessRunning;

    /// <summary>
    /// Delegate used to introduce timed delays inside
    /// <see cref="BuildDeadSessionExceptionAsync"/> between successive service /
    /// process re-queries.  Injectable for unit tests so tests complete without
    /// real waits.
    ///
    /// <para>
    /// Signature: <c>(duration, ct) → Task</c>.
    /// The production constructor wires this to <c>Task.Delay</c>.
    /// </para>
    /// </summary>
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How often a connect holding the host upload lease records a heartbeat in it (G49).</summary>
    private static readonly TimeSpan LeaseHeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Forwards connect progress and records each phase in the host upload lease, so a waiting connect on this host
    /// sees the phase change and treats this connect as still making progress.
    /// </summary>
    private sealed class LeaseProgress(IProgress<ConnectProgress>? inner, IUploadLeaseHandle lease)
        : IProgress<ConnectProgress>
    {
        public void Report(ConnectProgress value)
        {
            if (value.Phase is not (ConnectPhases.LeaseWait or ConnectPhases.SdkRestart or ConnectPhases.CommsRetry))
                lease.NoteProgress(value.Phase);
            inner?.Report(value);
        }
    }

    /// <summary>
    /// Coordinator whose host-wide upload lease <see cref="CreateFromControllerAsync(string, IProgress{ConnectProgress}?, CancellationToken)"/>
    /// takes. <see cref="SdkServiceRecovery.Shared"/> (and so <see cref="HostUploadLease.Default"/>) unless a lease was injected.
    /// </summary>
    private readonly SdkServiceRecovery _recovery;

    /// <summary>
    /// Delegate that probes whether a controller is reachable on the EtherNet/IP
    /// port (44818) via a TCP connect.  Injectable for unit tests so no network
    /// connection is ever made during testing.
    ///
    /// <para>
    /// Returns <c>true</c> when the TCP handshake to <c>&lt;ip&gt;:44818</c>
    /// succeeds within the probe timeout; <c>false</c> on any failure (host
    /// unreachable, connection refused, timeout, etc.).
    /// </para>
    ///
    /// <para>
    /// Signature: <c>(ip, ct) -&gt; Task&lt;bool&gt;</c>.
    /// </para>
    /// </summary>
    private readonly Func<string, CancellationToken,
        System.Threading.Tasks.Task<bool>> _controllerReachable;

    // ── Production default implementations ────────────────────────────────────

    private static System.Threading.Tasks.Task<ILogixProjectHandle> DefaultUploadFromController(
        string acdPath, string commPath, CancellationToken ct)
        => LogixProject.UploadToNewProjectAsync(acdPath, commPath, (IOperationEvent?)null, ct)
            .ContinueWith(
                t => (ILogixProjectHandle)t.Result,
                CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);

    private static Task DefaultSaveAs(
        ILogixProjectHandle project, string l5xPath, CancellationToken ct)
        => project.SaveAsAsync(l5xPath, force: true, detailedL5x: true, ct);

    /// <summary>
    /// Production implementation of <see cref="_ldSdkServiceRunning"/>: queries
    /// the Windows service control manager for the current status of
    /// <c>LdSdkService</c>.
    ///
    /// <para>
    /// Returns <c>true</c> when the service is
    /// <see cref="System.ServiceProcess.ServiceControllerStatus.Running"/>;
    /// <c>false</c> for any other status (stopped, start-pending, paused, etc.).
    /// On any query failure (service not installed on this machine, access
    /// denied, etc.) the method returns <c>true</c> so callers retain the
    /// pending-edit possibility message rather than falsely asserting a stopped
    /// service.
    /// </para>
    ///
    /// <para>
    /// <b>This method never starts, stops, or otherwise controls the service.</b>
    /// It only reads the current status.
    /// </para>
    /// </summary>
    // CA1416 suppressed: this file is compiled only when EnableLogixSdk is true
    // (i.e. on Windows), so ServiceController is always available at runtime.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "SdkConnectFactory.cs is compiled only under EnableLogixSdk=true, " +
                        "which is a Windows-only build condition.")]
    private static bool DefaultLdSdkServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("LdSdkService");
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not installed on this machine, or the query failed for
            // any other reason — assume running to avoid falsely asserting a
            // stopped service.
            return true;
        }
    }

    /// <summary>
    /// Production implementation of <see cref="_ldSdkServerProcessRunning"/>:
    /// checks whether at least one <c>LdSdkServer.exe</c> process is currently
    /// visible in the Windows process list.
    ///
    /// <para>
    /// Returns <c>true</c> when <c>Process.GetProcessesByName("LdSdkServer")</c>
    /// returns at least one entry; <c>false</c> when the list is empty.  On any
    /// query failure the method returns <c>true</c> so callers retain the
    /// pending-edit possibility message rather than falsely asserting a dead
    /// service.
    /// </para>
    ///
    /// <para>
    /// <b>This method never starts, stops, or otherwise controls any process.</b>
    /// It only reads the current process list.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "SdkConnectFactory.cs is compiled only under EnableLogixSdk=true, " +
                        "which is a Windows-only build condition.")]
    private static bool DefaultLdSdkServerProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("LdSdkServer").Length > 0;
        }
        catch
        {
            // Cannot enumerate processes — assume running to avoid falsely
            // asserting a dead service.
            return true;
        }
    }

    /// <summary>
    /// Production implementation of <see cref="_controllerReachable"/>: opens a
    /// TCP connection to <paramref name="ip"/>:44818 (the standard EtherNet/IP
    /// port) with a 2-second timeout to determine whether the controller is
    /// reachable on the network.
    ///
    /// <para>
    /// Returns <c>true</c> when the three-way TCP handshake completes within
    /// 2 seconds; <c>false</c> on any failure (host unreachable, connection
    /// refused, timeout, cancellation, etc.).  The connection is closed
    /// immediately after a successful handshake — no EtherNet/IP traffic is
    /// sent.
    /// </para>
    /// </summary>
    /// <param name="ip">IP address of the controller to probe.</param>
    /// <param name="ct">Upstream cancellation token.</param>
    private static async System.Threading.Tasks.Task<bool> DefaultControllerReachable(
        string ip, CancellationToken ct)
    {
        using var cts =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(ip, 44818, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to extract a controller IP address from a Logix Designer SDK
    /// comm path or a bare CIP gateway string.
    ///
    /// <para>
    /// Recognised forms (case-insensitive prefix matching):
    /// <list type="bullet">
    ///   <item><c>"EmulateEthernet\&lt;ip&gt;"</c> — emulated controller.</item>
    ///   <item><c>"&lt;ip&gt;"</c> — bare IP address (real controller, direct).</item>
    ///   <item><c>"AB_ETHIP-1\&lt;ip&gt;"</c> — real controller via AB EtherNet/IP driver.</item>
    ///   <item>Any of the above with a trailing backplane route
    ///     (<c>"\Backplane\0"</c> etc.) or CIP comma route (<c>",1,0"</c> etc.).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> for any path that does not match a recognised form,
    /// including paths with an unrecognised protocol prefix.
    /// </para>
    /// </summary>
    /// <param name="commPath">
    /// The comm path string to parse (e.g.
    /// <c>"EmulateEthernet\192.0.2.10"</c>,
    /// <c>"192.0.2.10,1,0"</c>).
    /// </param>
    /// <returns>
    /// The extracted IP address string when a recognised form is matched;
    /// <c>null</c> otherwise.
    /// </returns>
    internal static string? TryParseControllerIp(string commPath)
    {
        if (string.IsNullOrWhiteSpace(commPath))
            return null;

        var path = commPath.Trim();

        // Strip trailing CIP comma route: ,<anything>
        // e.g. "198.51.100.1,1,0" → "198.51.100.1"
        var commaIdx = path.IndexOf(',');
        if (commaIdx >= 0)
            path = path[..commaIdx];

        // Strip trailing backplane route: \Backplane\<n>
        // e.g. "EmulateEthernet\198.51.100.1\Backplane\0" → "EmulateEthernet\198.51.100.1"
        var backplaneIdx = path.IndexOf(
            "\\Backplane\\", StringComparison.OrdinalIgnoreCase);
        if (backplaneIdx >= 0)
            path = path[..backplaneIdx];

        // Backslash-prefixed forms: "EmulateEthernet\<ip>" or "AB_ETHIP-1\<ip>"
        var backslashIdx = path.IndexOf('\\');
        if (backslashIdx >= 0)
        {
            var prefix = path[..backslashIdx];
            if (prefix.Equals("EmulateEthernet", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("AB_ETHIP-1", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(backslashIdx + 1)..];
            }
            else
            {
                // Unrecognised protocol prefix — cannot extract IP.
                return null;
            }
        }

        // Validate the remaining segment as an IP address.
        return System.Net.IPAddress.TryParse(path, out _) ? path : null;
    }

    /// <summary>
    /// Production constructor — creates a <see cref="LogixProjectSession"/>
    /// backed by the real Logix Designer SDK.
    /// </summary>
    /// <param name="lease">
    /// Host-wide upload lease to take for controller uploads; <c>null</c> (production) uses
    /// <see cref="HostUploadLease.Default"/>.
    /// </param>
    public SdkConnectFactory(HostUploadLease? lease = null) : this(
        static () => new LogixProjectSession(),
        DefaultUploadFromController,
        DefaultSaveAs,
        SafeXml.LoadL5x,
        DefaultLdSdkServiceRunning,
        DefaultControllerReachable,
        DefaultLdSdkServerProcessRunning,
        static (duration, ct) => Task.Delay(duration, ct),
        lease)
    { }

    /// <summary>
    /// Testing constructor — allows injection of a custom
    /// <see cref="LogixProjectSession"/> factory so unit tests can supply fake
    /// SDK handles without requiring a live Logix Designer installation.
    /// Only <see cref="CreateAsync"/> is affected; <see cref="CreateFromControllerAsync"/>
    /// uses the production upload delegates.
    /// </summary>
    /// <param name="sessionFactory">
    /// Delegate that produces a new <see cref="LogixProjectSession"/> on each
    /// <see cref="CreateAsync"/> call.  Must not be <c>null</c>.
    /// </param>
    /// <param name="lease">
    /// Host-wide upload lease to take for controller uploads; <c>null</c> uses <see cref="HostUploadLease.Default"/>.
    /// </param>
    internal SdkConnectFactory(Func<LogixProjectSession> sessionFactory, HostUploadLease? lease = null)
        : this(sessionFactory, DefaultUploadFromController, DefaultSaveAs, SafeXml.LoadL5x,
               null, null, null, null, lease)
    { }

    /// <summary>
    /// Full-control testing constructor — allows injection of all production
    /// delegates for comprehensive unit testing of both
    /// <see cref="CreateAsync"/> and <see cref="CreateFromControllerAsync"/>.
    /// </summary>
    /// <param name="sessionFactory">
    /// Factory for <see cref="CreateAsync"/> sessions.
    /// </param>
    /// <param name="controllerUploadFactory">
    /// Replaces <c>LogixProject.UploadToNewProjectAsync</c>.
    /// </param>
    /// <param name="saveAsFactory">
    /// Replaces <c>ILogixProject.SaveAsAsync</c> for L5X export.
    /// </param>
    /// <param name="l5xLoader">
    /// Replaces <c>SafeXml.LoadL5x</c> for parsing the exported L5X.
    /// </param>
    /// <param name="ldSdkServiceRunning">
    /// Replaces <see cref="DefaultLdSdkServiceRunning"/> for service-state
    /// queries.  When <c>null</c>, defaults to <c>() =&gt; true</c> (service
    /// is assumed running), which preserves the pending-edit possibility message
    /// and keeps existing tests passing without modification.
    /// </param>
    /// <param name="controllerReachable">
    /// Replaces <see cref="DefaultControllerReachable"/> for TCP reachability
    /// probes.  When <c>null</c>, defaults to a delegate that always returns
    /// <c>true</c> (controller assumed reachable), which keeps existing tests
    /// passing without modification.  Inject a custom delegate in unit tests to
    /// simulate reachable / unreachable controller states without touching the
    /// network.
    /// </param>
    /// <param name="ldSdkServerProcessRunning">
    /// Replaces <see cref="DefaultLdSdkServerProcessRunning"/> for process-presence
    /// checks.  When <c>null</c>, defaults to <c>() =&gt; true</c> (process
    /// assumed present), which keeps existing tests passing without modification.
    /// Inject a custom delegate in unit tests to simulate process-absent scenarios.
    /// </param>
    /// <param name="delay">
    /// Replaces <c>Task.Delay</c> for the timed waits between service re-queries.
    /// When <c>null</c>, defaults to <c>Task.Delay</c>.
    /// Inject <c>(_, _) =&gt; Task.CompletedTask</c> in unit tests to avoid
    /// real waits during the re-query loop.
    /// </param>
    /// <param name="lease">
    /// Host-wide upload lease taken by <see cref="CreateFromControllerAsync(string, IProgress{ConnectProgress}?, CancellationToken)"/>.
    /// When <c>null</c> (production), <see cref="HostUploadLease.Default"/> is used. Tests pass their own so they never
    /// read or write the real host-wide lease file.
    /// </param>
    internal SdkConnectFactory(
        Func<LogixProjectSession> sessionFactory,
        Func<string, string, CancellationToken,
            System.Threading.Tasks.Task<ILogixProjectHandle>> controllerUploadFactory,
        Func<ILogixProjectHandle, string, CancellationToken, Task> saveAsFactory,
        Func<string, L5X> l5xLoader,
        Func<bool>? ldSdkServiceRunning = null,
        Func<string, CancellationToken,
            System.Threading.Tasks.Task<bool>>? controllerReachable = null,
        Func<bool>? ldSdkServerProcessRunning = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        HostUploadLease? lease = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(controllerUploadFactory);
        ArgumentNullException.ThrowIfNull(saveAsFactory);
        ArgumentNullException.ThrowIfNull(l5xLoader);
        _sessionFactory          = sessionFactory;
        _controllerUploadFactory = controllerUploadFactory;
        _saveAsFactory           = saveAsFactory;
        _l5xLoader               = l5xLoader;
        // When no checker is injected (unit-test usage without service state
        // simulation), default to "service is running" so existing tests pass
        // without modification.  The production 0-arg constructor passes
        // DefaultLdSdkServiceRunning explicitly.
        _ldSdkServiceRunning     = ldSdkServiceRunning ?? (() => true);
        // When no probe is injected, default to "controller is reachable" so
        // existing tests pass without modification.  The production 0-arg
        // constructor passes DefaultControllerReachable explicitly.
        _controllerReachable     = controllerReachable
            ?? ((_, _) => System.Threading.Tasks.Task.FromResult(true));
        // When no process checker is injected, default to "process is present"
        // so existing tests pass without modification.  The production 0-arg
        // constructor passes DefaultLdSdkServerProcessRunning explicitly.
        _ldSdkServerProcessRunning = ldSdkServerProcessRunning ?? (() => true);
        // When no delay factory is injected, default to Task.Delay (real waits).
        // Unit tests should inject (_, _) => Task.CompletedTask to avoid waiting.
        _delay = delay ?? (static (duration, ct) => Task.Delay(duration, ct));
        // Production keeps the process-wide coordinator (and so HostUploadLease.Default); an injected lease gets its
        // own coordinator so its upload count and lease file are private to the caller.
        _recovery = lease is null ? SdkServiceRecovery.Shared : new SdkServiceRecovery(lease: lease);
    }

    // ── ISdkTagServiceFactory ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Creates a new <see cref="LogixProjectSession"/>, opens it against
    /// <paramref name="projectFilePath"/>, and — when
    /// <paramref name="sdkMode"/> is <c>"online"</c> — attempts correlation in
    /// ascending order of invasiveness (the three-rung correlation ladder):
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>Rung 1 — GoOnline directly:</b> calls <c>GoOnlineAsync</c>.
    ///     Succeeds immediately when the controller already holds a matching
    ///     project image (e.g. a previously uploaded ACD).  No destructive
    ///     operations are performed.
    ///   </item>
    ///   <item>
    ///     <b>Rung 2 — Upload then GoOnline:</b> when rung 1 fails, calls
    ///     <c>UploadAsync</c> to pull the controller's current image into the
    ///     server-managed private working copy (never the engineer's source file),
    ///     then retries <c>GoOnlineAsync</c>.  This is the normal uncorrelated
    ///     case — non-destructive to the controller, available regardless of
    ///     <paramref name="allowControllerDownload"/>.
    ///   </item>
    ///   <item>
    ///     <b>Rung 3 — Download to controller (last-resort, explicit opt-in only):</b>
    ///     only reached when rungs 1 and 2 both fail and
    ///     <paramref name="allowControllerDownload"/> is <c>true</c>.  Executes the
    ///     PROGRAM→download→go_online→RUN sequence.  Only necessary when the
    ///     controller is empty or holds a completely unrelated program that cannot
    ///     be correlated via upload.  Destructive — stops the running controller and
    ///     permanently overwrites its stored program image.
    ///   </item>
    /// </list>
    /// <para>
    /// When rungs 1 and 2 fail and <paramref name="allowControllerDownload"/> is
    /// <c>false</c> (the default), a <see cref="TagServiceException"/> is thrown with
    /// <see cref="TagServiceErrorKind.ImagesIncompatible"/> (when the failure was
    /// an SDK correlation error) or
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> (for all other failures).
    /// No code path silently falls back to an offline session.
    /// </para>
    /// <para>
    /// The session's lifetime is managed by the returned service — callers
    /// should dispose the service when done to release the gRPC channel.
    /// </para>
    /// </remarks>
    /// <exception cref="TagServiceException">
    /// Thrown with <see cref="TagServiceErrorKind.ConnectionFailed"/> when:
    /// <list type="bullet">
    ///   <item>The underlying <c>OpenAsync</c> call fails.</item>
    ///   <item>All three rungs fail (connection unreachable or incompatible).</item>
    ///   <item><c>ReadConnectedStateAsync</c> does not report
    ///         <c>ConnectedState.Online</c> after a successful GoOnline.</item>
    ///   <item><c>ChangeControllerModeAsync(Program)</c> throws (rung 3 only).</item>
    ///   <item><c>DownloadAsync</c> throws (rung 3 only).</item>
    ///   <item><c>ChangeControllerModeAsync(Run)</c> throws (rung 3 only).</item>
    /// </list>
    /// </exception>
    public async Task<SdkSessionResult> CreateAsync(
        string projectFilePath,
        L5X typeProject,
        string sdkMode = "offline",
        bool allowControllerDownload = false,
        CancellationToken ct = default)
    {
        var isOnline = sdkMode.Equals("online", StringComparison.OrdinalIgnoreCase);
        var mode     = isOnline ? OperationMode.Online : OperationMode.Offline;

        var session = _sessionFactory();
        await session.OpenAsync(projectFilePath, ct).ConfigureAwait(false);
        session.StudioControllerName  = typeProject.Controller?.Name;
        session.StudioProjectFileName = Path.GetFileName(projectFilePath);
        session.DesignerVersion       = DesignerVersionInfo.FromProjectRevision(ProjectRevisionOrNull(typeProject));

        // Track whether a download was performed and the ordered mode transitions.
        bool    downloadPerformed = false;
        var     modeTransitions   = new List<string>();
        string? correlationPath   = null;

        if (isOnline)
        {
            // ── Three-rung correlation ladder ─────────────────────────────────
            // Attempts to reach the ONLINE state in ascending order of
            // invasiveness.  Rungs 1 and 2 are always attempted regardless of
            // allowControllerDownload; only rung 3 is gated on that flag.

            bool       connected    = false;
            Exception? lastSdkFail  = null;   // last SDK-level failure, for final error

            // ── Rung 1: GoOnline directly (least invasive) ────────────────────
            // When the controller already holds a matching project image (e.g. a
            // previously uploaded ACD), GoOnlineAsync succeeds immediately and no
            // further operations are needed.
            try
            {
                await session.ExecuteAsync(p => p.GoOnlineAsync(ct), ct)
                    .ConfigureAwait(false);
                connected = true;
            }
            catch (TagServiceException)
            {
                // Session-level error — cannot continue; propagate.
                session.Dispose();
                throw;
            }
            catch (OperationCanceledException)
            {
                // Cancellation must propagate immediately — falling through to
                // rung 2 or 3 would send unintended operations to the controller
                // before the cancellation is detected.
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                // ── Operation-first classifier: GoOnline on a file-opened project ──
                // When GoOnlineAsync throws an RxE_COMM_* error on this path the
                // caller opened a project file (L5X or ACD) via logix_open_project.
                // A file cannot prove it matches the controller's running image; this
                // is the "uncorrelated file" trap. Surface it as
                // ProjectNotCorrelated immediately — the TCP probe is NOT invoked
                // because the error is inherent to the file-based path, not to
                // controller reachability.
                if (SdkCommErrorClassifier.IsCommLayerError(ex))
                {
                    session.Dispose();
                    throw SdkCommErrorClassifier.BuildProjectNotCorrelatedError(
                        ex, projectFilePath);
                }
                // SDK-level failure — controller image may be uncorrelated.
                // Fall through to rung 2.
                lastSdkFail = ex;
            }

            // ── Rung 2: Upload into working copy, then GoOnline ───────────────
            // Pull the controller's current image into the server-managed private
            // copy (never the engineer's source file).  This is the normal
            // uncorrelated case: the working copy does not yet match the
            // controller's program, but upload makes them consistent so that a
            // second GoOnline attempt can succeed without a destructive download.
            // Non-destructive to the controller; attempted regardless of
            // allowControllerDownload.
            //
            // NOTE — pending edits on the controller cannot produce the
            // "Project is not open." failure on this path.
            // That error is specific to CreateFromControllerAsync, where
            // UploadToNewProjectAsync returns a degraded handle when the controller
            // has pending edits and SaveAsAsync then throws.  Here, UploadAsync
            // refreshes an already-open, disk-backed session; no SaveAsAsync call
            // is ever made on this path.  Pending edits may cause UploadAsync or
            // the subsequent GoOnlineAsync to fail, but those failures surface via
            // the normal SDK exception branch below with their own error messages —
            // they are mapped to ConnectionFailed or ImagesIncompatible as
            // appropriate, not to PendingEditsPresent.
            // See BuildDeadSessionExceptionAsync for the authoritative scope note.
            if (!connected)
            {
                bool uploadOk = false;
                try
                {
                    await session.ExecuteAsync(p => p.UploadAsync(ct), ct)
                        .ConfigureAwait(false);
                    uploadOk = true;
                }
                catch (TagServiceException)
                {
                    session.Dispose();
                    throw;
                }
                catch (OperationCanceledException)
                {
                    session.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    // Upload failed (e.g. controller is empty or unrelated) —
                    // record and fall through to rung 3.
                    lastSdkFail = ex;
                }

                if (uploadOk)
                {
                    try
                    {
                        await session.ExecuteAsync(p => p.GoOnlineAsync(ct), ct)
                            .ConfigureAwait(false);
                        connected       = true;
                        correlationPath = "upload";   // rung 2 succeeded
                    }
                    catch (TagServiceException)
                    {
                        session.Dispose();
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        session.Dispose();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // GoOnline after upload still failed — controller image is
                        // too different (empty or completely unrelated program).
                        // Fall through to rung 3.
                        lastSdkFail = ex;
                    }
                }
            }

            // ── Rungs 1 or 2 succeeded: verify connected state and return ─────
            if (connected)
            {
                ConnectedState earlyState;
                try
                {
                    earlyState = await session.ExecuteAsync(
                        p => p.ReadConnectedStateAsync(ct), ct).ConfigureAwait(false);
                }
                catch (TagServiceException)
                {
                    session.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    session.Dispose();
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.ConnectionFailed,
                        $"ReadConnectedStateAsync failed for '{projectFilePath}': {ex.Message}"),
                        ex);
                }

                if (earlyState != ConnectedState.Online)
                {
                    session.Dispose();
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.ConnectionFailed,
                        $"Session for '{projectFilePath}' did not reach ONLINE state after " +
                        $"GoOnlineAsync: ReadConnectedStateAsync returned '{earlyState}'. " +
                        "sdkMode=\"online\" requires a live controller connection."));
                }

                // Session is fully online — no download or mode transitions.
                // Determine which rung succeeded: if upload was never attempted
                // (UploadCallCount == 0 is unavailable here), we rely on whether
                // correlationPath was set during rung 2's upload block below.
                // At this point correlationPath is already set correctly:
                //   • rung 1: still null → set to "already-correlated"
                //   • rung 2: set to "upload" after UploadAsync succeeded
                correlationPath ??= "already-correlated";

                // Mark the session as online and correlated so downstream
                // handlers (e.g. ProposeRungEditHandler) can verify the guard.
                session.IsOnline     = true;
                session.IsCorrelated = true;

                return new SdkSessionResult(
                    Service:           new TypedSdkTagService(session, typeProject, mode),
                    DownloadPerformed: false,
                    ModeTransitions:   [],
                    CorrelationPath:   correlationPath,
                    DesignerVersion:   session.DesignerVersion,
                    Capabilities:      session.Capabilities);
            }

            // ── Rung 3: Download to controller (most invasive) ────────────────
            // Both rung 1 (direct GoOnline) and rung 2 (upload → GoOnline)
            // failed.  A download is required — the controller holds an empty or
            // completely unrelated program.  Only proceed when the caller has
            // explicitly authorised the destructive PROGRAM→download→go_online→RUN
            // sequence.
            if (!allowControllerDownload)
            {
                session.Dispose();
                if (lastSdkFail is not null && IsIncompatibilityError(lastSdkFail))
                {
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.ImagesIncompatible,
                        $"The controller holds a different project than the one opened " +
                        $"('{projectFilePath}'). " +
                        $"To resolve this, either: " +
                        $"(1) download the open project to the controller and reconnect, or " +
                        $"(2) open the project that is already stored in the controller. " +
                        $"SDK detail: {lastSdkFail.Message}"),
                        lastSdkFail);
                }

                // RxE_COMM_* comm-layer fault: LdSdkService is running but its
                // internal communications layer has faulted.  Surface a structured
                // error that distinguishes this from a dead PLC.
                if (lastSdkFail is not null && IsCommLayerError(lastSdkFail))
                {
                    throw new TagServiceException(new TagServiceError(
                        TagServiceErrorKind.SdkCommLayerFaulted,
                        $"SDK comm-layer fault during GoOnlineAsync for '{projectFilePath}': " +
                        $"{lastSdkFail.Message}"),
                        lastSdkFail);
                }

                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"GoOnlineAsync failed for '{projectFilePath}': " +
                    $"{lastSdkFail?.Message ?? "unknown error"}"),
                    lastSdkFail);
            }

            // ── 1a. Transition to PROGRAM mode (stops program scan) ───────────
            try
            {
                await session.ExecuteAsync(
                    p => p.ChangeControllerModeAsync(
                        RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Program, ct),
                    ct).ConfigureAwait(false);
                modeTransitions.Add("Program");
            }
            catch (TagServiceException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"ChangeControllerModeAsync(Program) failed for '{projectFilePath}': " +
                    $"{ex.Message}"), ex);
            }

            // ── 1b. Download project to controller ────────────────────────────
            try
            {
                await session.ExecuteAsync(p => p.DownloadAsync(ct), ct)
                    .ConfigureAwait(false);
                downloadPerformed = true;
                correlationPath   = "download";   // rung 3 succeeded
            }
            catch (TagServiceException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"DownloadAsync failed for '{projectFilePath}': {ex.Message}"), ex);
            }

            // ── 2. Go online after download ───────────────────────────────────
            try
            {
                await session.ExecuteAsync(p => p.GoOnlineAsync(ct), ct)
                    .ConfigureAwait(false);
            }
            catch (TagServiceException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex) when (IsIncompatibilityError(ex))
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ImagesIncompatible,
                    $"The controller holds a different project than the one opened " +
                    $"('{projectFilePath}'). " +
                    $"To resolve this, either: " +
                    $"(1) download the open project to the controller and reconnect, or " +
                    $"(2) open the project that is already stored in the controller. " +
                    $"SDK detail: {ex.Message}"), ex);
            }
            catch (Exception ex) when (IsCommLayerError(ex))
            {
                // RxE_COMM_* comm-layer fault after download — SDK comm layer faulted.
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.SdkCommLayerFaulted,
                    $"SDK comm-layer fault during GoOnlineAsync for '{projectFilePath}': " +
                    $"{ex.Message}"), ex);
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"GoOnlineAsync failed for '{projectFilePath}': {ex.Message}"), ex);
            }

            // ── 3. Verify the session is actually ONLINE ──────────────────────
            ConnectedState connectedState;
            try
            {
                connectedState = await session.ExecuteAsync(
                    p => p.ReadConnectedStateAsync(ct), ct).ConfigureAwait(false);
            }
            catch (TagServiceException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"ReadConnectedStateAsync failed for '{projectFilePath}': {ex.Message}"), ex);
            }

            if (connectedState != ConnectedState.Online)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"Session for '{projectFilePath}' did not reach ONLINE state after " +
                    $"GoOnlineAsync: ReadConnectedStateAsync returned '{connectedState}'. " +
                    "sdkMode=\"online\" requires a live controller connection."));
            }

            // Mark the session as online and correlated (rung 3: download path).
            session.IsOnline     = true;
            session.IsCorrelated = true;

            // ── 4. Transition back to RUN mode ────────────────────────────────
            try
            {
                await session.ExecuteAsync(
                    p => p.ChangeControllerModeAsync(
                        RockwellAutomation.LogixDesigner.LogixProject.RequestedControllerMode.Run, ct),
                    ct).ConfigureAwait(false);
                modeTransitions.Add("Run");
            }
            catch (TagServiceException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"ChangeControllerModeAsync(Run) failed for '{projectFilePath}': " +
                    $"{ex.Message}"), ex);
            }
        }

        return new SdkSessionResult(
            Service:           new TypedSdkTagService(session, typeProject, mode),
            DownloadPerformed: downloadPerformed,
            ModeTransitions:   [.. modeTransitions],
            CorrelationPath:   correlationPath,
            DesignerVersion:   session.DesignerVersion,
            Capabilities:      session.Capabilities);
    }

    // ── CreateFromControllerAsync — no-project-file entry point ──────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Implementation strategy — three ordered steps, each guarded against
    /// cancellation and structured errors:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>Upload to new ACD</b>: calls
    ///     <c>LogixProject.UploadToNewProjectAsync(acdPath, commPath, null, ct)</c>
    ///     to create a server-managed working copy of the controller's current
    ///     program image.  The ACD is placed in a slot under
    ///     <see cref="LogixProjectSession.WorkingDirectoryBase"/> keyed by a
    ///     random GUID prefix; it is deleted when the session is disposed.
    ///   </item>
    ///   <item>
    ///     <b>Extract L5X for type resolution</b>: calls
    ///     <c>ILogixProject.SaveAsAsync(l5xPath, force: true, detailedL5x: true, ct)</c>
    ///     on the uploaded handle to produce a temporary L5X file, which is
    ///     parsed via <c>SafeXml.LoadL5x</c> and immediately deleted.  The parsed L5X
    ///     is returned in <see cref="SdkSessionResult.TypeProject"/> so the
    ///     caller can derive a write-gate policy without a separate
    ///     <c>logix_open_project</c> call.
    ///   </item>
    ///   <item>
    ///     <b>Go online</b>: wraps the uploaded handle in a
    ///     <see cref="LogixProjectSession"/> and calls <c>GoOnlineAsync</c>.
    ///     Since the ACD was just uploaded from the controller, it is
    ///     inherently correlated; <c>GoOnlineAsync</c> should succeed on the
    ///     first attempt (equivalent to rung 1 of the standard ladder).
    ///   </item>
    /// </list>
    /// </remarks>
    public Task<SdkSessionResult> CreateFromControllerAsync(
        string commPath,
        CancellationToken ct = default) =>
        CreateFromControllerAsync(commPath, progress: null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Reports <see cref="ConnectPhases.Upload"/> before the upload,
    /// <see cref="ConnectPhases.SaveL5x"/> before the detailed L5X save and
    /// <see cref="ConnectPhases.Parse"/> before the L5X is parsed.  The fourth phase,
    /// <see cref="ConnectPhases.GateDerivation"/>, belongs to <c>ConnectHandler</c>.
    /// </remarks>
    public async Task<SdkSessionResult> CreateFromControllerAsync(
        string commPath,
        IProgress<ConnectProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commPath);

        // Serialise uploads host-wide (G33): wait a bounded time for another server process's upload, else throw a
        // typed ConnectBusy with a retry-after. While the lease is held no recovery on the host may restart the
        // shared service.
        // A caller that has to wait reports the lease-wait phase (with the owner's pid) first, so the connect's
        // phase timings show the time spent waiting; the upload phase is reported once the lease is held.
        // The wait runs past the base bound while the owner records progress (G49), and the closing lease-wait report
        // says whether it ended with the owner finishing or with a timeout.
        using var uploadScope = await _recovery.AcquireUploadAsync(
            ct,
            owner => progress?.Report(ConnectPhases.LeaseWaitFor(owner?.Pid)),
            outcome => progress?.Report(ConnectPhases.LeaseWaitEnded(outcome))).ConfigureAwait(false);

        // From here this connect is the lease owner: record each phase and a heartbeat in the lease file so a
        // concurrent connect waiting on us can tell we are still making progress.
        uploadScope.StartHeartbeat(LeaseHeartbeatInterval);
        progress = new LeaseProgress(progress, uploadScope);

        // ── 1. Prepare working-directory slot ─────────────────────────────────
        // Use a random 12-char prefix (Guid) so concurrent uploads to different
        // controllers never collide in the shared WorkingDirectoryBase.
        var slot    = Guid.NewGuid().ToString("N")[..12];
        var workDir = Path.Combine(LogixProjectSession.WorkingDirectoryBase, "ctl-" + slot);
        var acdPath = Path.Combine(workDir, "uploaded.ACD");
        var l5xPath = Path.Combine(Path.GetTempPath(), $"lgx-l5x-{slot}.L5X");

        try
        {
            Directory.CreateDirectory(workDir);
        }
        catch (Exception ex)
        {
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Failed to create working directory for upload from '{commPath}': " +
                $"{ex.Message}"), ex);
        }

        // ── 2. Upload controller image to new ACD ─────────────────────────────
        // UploadToNewProjectAsync connects to the controller at commPath, pulls
        // its current program, saves it as an ACD at acdPath, and returns an
        // open ILogixProject handle.  This is non-destructive to the controller.
        //
        // NOTE — no pre-upload pending-edit check is possible.
        // The Logix Designer SDK (v2.2) exposes no API to query whether the
        // controller holds uncommitted (pending) edits before an upload is
        // attempted.  ILogixProject and ILogixSDKClientHandler offer no
        // HasPendingEdits, GetPendingEditStatus, or equivalent query.  The
        // PartialImportOption enum values (LeaveEdits, AcceptEdits, and the
        // finalize variant) are import-time directives, not interrogation
        // mechanisms, and none of them reports the controller's current edit
        // state.  The pending-edit condition therefore cannot be detected until
        // after the upload: UploadToNewProjectAsync itself succeeds (the ACD is
        // written), but the returned project handle can be left in a degraded
        // state that causes the subsequent SaveAsAsync call to throw "Project is
        // not open." (step 3 below).  That exception is caught by
        // BuildDeadSessionExceptionAsync below and surfaced as
        // TagServiceErrorKind.ConnectionFailed, not PendingEditsPresent — hardware
        // verification showed "Project is not open." is a dead-service
        // indicator, not a pending-edit indicator.  PendingEditsPresent is only
        // assigned by the evidence-based logix_pending_edits read.
        ILogixProjectHandle uploadedHandle;
        try
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.Upload));
            uploadedHandle = await _controllerUploadFactory(acdPath, commPath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cleanup of the working directory before propagating.
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw;
        }
        catch (Exception ex) when (IsDeadSessionIndicator(ex))
        {
            // "Could not reach LdSdkService" from the upload step means the gRPC
            // service is already dead before the upload even completes.  Apply the
            // same service-state diagnostic used for the SaveAs step below.
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw await BuildDeadSessionExceptionAsync(ex, commPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCommLayerError(ex))
        {
            // RxE_COMM_* comm-layer fault during upload — run the TCP probe to
            // distinguish a degraded LdSdkService (controller reachable) from an
            // unreachable controller, then surface the appropriate structured error.
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw await BuildCommLayerTriageExceptionAsync(ex, commPath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"UploadToNewProjectAsync failed for controller at '{commPath}': " +
                $"{ex.Message}"), ex);
        }

        // ── 3. Extract L5X for type resolution ───────────────────────────────
        // Save the uploaded project as a detailed L5X to a temporary file, parse
        // it for tag-type resolution, then delete the temporary L5X.  The ACD
        // itself is owned by the session (see step 4) and deleted on dispose.
        L5X typeProject;
        try
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
            await _saveAsFactory(uploadedHandle, l5xPath, ct).ConfigureAwait(false);

            try
            {
                progress?.Report(ConnectPhases.For(ConnectPhases.Parse));
                typeProject = _l5xLoader(l5xPath);
            }
            finally
            {
                // Always delete the temporary L5X, even when parsing fails.
                try { File.Delete(l5xPath); } catch { /* swallow */ }
            }
        }
        catch (TagServiceException)
        {
            uploadedHandle.Dispose();
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw;
        }
        catch (OperationCanceledException)
        {
            uploadedHandle.Dispose();
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw;
        }
        catch (Exception ex) when (IsDeadSessionIndicator(ex))
        {
            // The SDK throws "Project is not open." from SaveAsAsync when the
            // session is in a dead state.  "Could not reach LdSdkService" can
            // also appear here when the gRPC service died between the upload and
            // the SaveAs call.  Both messages are dead-session indicators.
            //
            // Rather than asserting a single root cause, query the actual service
            // state and report the observed fact:
            //   - service not running → definitive "service is down" diagnosis
            //   - service running, controller unreachable → definitive "PLC is down"
            //   - service running, controller reachable   → ambiguous two-cause message
            //     (pending edits OR a service fault that has since recovered)
            uploadedHandle.Dispose();
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw await BuildDeadSessionExceptionAsync(ex, commPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            uploadedHandle.Dispose();
            try { Directory.Delete(workDir, recursive: true); } catch { /* swallow */ }
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Failed to extract L5X from uploaded project for '{commPath}': " +
                $"{ex.Message}"), ex);
        }

        // ── 4. Wrap handle in session and go online ───────────────────────────
        // The pre-opened handle is wrapped in a LogixProjectSession using an
        // identity factory (returns the pre-created handle without calling
        // OpenLogixProjectAsync again) and an identity copier (the ACD is
        // already at acdPath — no copy step needed).  When the session is
        // eventually disposed, it will delete acdPath and, when the workDir is
        // then empty, will also remove the directory — keeping WorkingDirectoryBase clean.
        // Other files (the offline build check's saved copy) open in their own SDK handle, never this one.
        var session = new LogixProjectSession(
            (_, _) => System.Threading.Tasks.Task.FromResult(uploadedHandle),
            path  => path,  // identity copier
            LogixProjectSession.OpenWithSdkAsync);

        try
        {
            await session.OpenAsync(acdPath, ct).ConfigureAwait(false);
        }
        catch (TagServiceException)
        {
            // OpenAsync already disposed the handle on failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            session.Dispose();
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Failed to open session against uploaded ACD for '{commPath}': " +
                $"{ex.Message}"), ex);
        }

        // ── 5. Go online — uploaded ACD is inherently correlated ──────────────
        // Since the ACD was just uploaded from the controller, GoOnlineAsync
        // should succeed on the first attempt (no ladder rungs needed).
        try
        {
            await session.ExecuteAsync(p => p.GoOnlineAsync(ct), ct)
                .ConfigureAwait(false);
        }
        catch (TagServiceException)
        {
            session.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Dispose();
            throw;
        }
        catch (Exception ex) when (IsCommLayerError(ex))
        {
            // RxE_COMM_* comm-layer fault during GoOnlineAsync after upload — run
            // the TCP probe to distinguish a degraded LdSdkService from an
            // unreachable controller, then surface the appropriate structured error.
            session.Dispose();
            throw await BuildCommLayerTriageExceptionAsync(ex, commPath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.Dispose();
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"GoOnlineAsync failed after upload from controller at '{commPath}': " +
                $"{ex.Message}"), ex);
        }

        // ── 6. Verify connected state ─────────────────────────────────────────
        ConnectedState connectedState;
        try
        {
            connectedState = await session.ExecuteAsync(
                p => p.ReadConnectedStateAsync(ct), ct).ConfigureAwait(false);
        }
        catch (TagServiceException)
        {
            session.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            session.Dispose();
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"ReadConnectedStateAsync failed after upload from '{commPath}': {ex.Message}"),
                ex);
        }

        if (connectedState != ConnectedState.Online)
        {
            session.Dispose();
            throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"Session for uploaded project from '{commPath}' did not reach ONLINE state: " +
                $"ReadConnectedStateAsync returned '{connectedState}'."));
        }

        // Mark the session as online and correlated so downstream handlers
        // (e.g. ProposeRungEditHandler) can verify the guard.
        session.IsOnline     = true;
        session.IsCorrelated = true;
        // The uploaded ACD is server-managed, so its file name means nothing to Studio:
        // only the controller name is known for window targeting.
        session.StudioControllerName = typeProject.Controller?.Name;
        session.DesignerVersion      = DesignerVersionInfo.FromProjectRevision(ProjectRevisionOrNull(typeProject));

        return new SdkSessionResult(
            Service:           new TypedSdkTagService(session, typeProject, OperationMode.Online),
            DownloadPerformed: false,
            ModeTransitions:   [],
            CorrelationPath:   "upload-to-new-project",
            TypeProject:       typeProject,
            AcdPath:           acdPath,
            DesignerVersion:   session.DesignerVersion,
            Capabilities:      session.Capabilities);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// The project's <c>SoftwareRevision</c> as text (for example <c>36.11</c>), or <c>null</c> when the
    /// loaded content does not carry one. Best effort: a read failure means "version unknown".
    /// </summary>
    private static string? ProjectRevisionOrNull(L5X typeProject)
    {
        try { return typeProject.Content?.SoftwareRevision?.ToString(); }
        catch { return null; }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> represents an SDK
    /// incompatibility error that signals the controller's stored program image
    /// cannot be correlated to the open project without a download.
    ///
    /// <para>
    /// The SDK surfaces these conditions via any exception whose
    /// <see cref="Exception.Message"/> contains one of the following tokens:
    /// <list type="bullet">
    ///   <item>
    ///     <c>"IMAGES_INCOMPATIBLE"</c> (<c>RxCL_E_IMAGES_INCOMPATIBLE</c>) —
    ///     the controller holds a different project image than the one currently
    ///     open in the session.
    ///   </item>
    ///   <item>
    ///     <c>"CANNOT_UPLOAD_PHYS_ADDR"</c> — the SDK cannot correlate the
    ///     physical address information in the controller with the open project,
    ///     which also requires a fresh download to resolve.
    ///   </item>
    /// </list>
    /// Both conditions map to <see cref="TagServiceErrorKind.ImagesIncompatible"/>
    /// because both require a download to proceed to ONLINE.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when the exception message contains
    /// <c>"IMAGES_INCOMPATIBLE"</c> or <c>"CANNOT_UPLOAD_PHYS_ADDR"</c>;
    /// <c>false</c> otherwise.
    /// </returns>
    private static bool IsIncompatibilityError(Exception ex)
        => RockwellErrorHints.IsIncompatibility(ex);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="ex"/> is a dead-session
    /// indicator — a message that signals the SDK session has died, either
    /// because the <c>LdSdkService</c> gRPC service crashed
    /// (<c>"Project is not open."</c>) or because it is unreachable
    /// (<c>"Could not reach LdSdkService"</c>,
    ///  <c>"Unable to complete connection to LdSdkService"</c>).
    ///
    /// <para>
    /// Delegates to <see cref="SdkCommErrorClassifier.IsDeadSessionIndicator"/>
    /// so that both the connect path (<see cref="CreateFromControllerAsync"/>)
    /// and the in-session path (<see cref="TypedSdkTagService"/>) use one
    /// shared classifier.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when the shared classifier identifies the exception as a
    /// dead-session indicator; <c>false</c> otherwise.
    /// </returns>
    private static bool IsDeadSessionIndicator(Exception ex)
        => SdkCommErrorClassifier.IsDeadSessionIndicator(ex);

    /// <summary>
    /// Returns <c>true</c> when any exception in <paramref name="ex"/>'s
    /// inner-exception chain contains a message token beginning with
    /// <c>"RxE_COMM_"</c> — indicating a Rockwell comm-layer fault
    /// (e.g. <c>RxE_COMM_DTLERROR</c>).
    ///
    /// <para>
    /// Searches the full inner-exception chain so that the token is detected
    /// even when the SDK wraps the original Rockwell exception in a higher-level
    /// exception type.
    /// </para>
    ///
    /// <para>
    /// <c>RxCL_E_CANNOT_UPLOAD_PHYS_ADDR</c> is excluded by caller ordering:
    /// all callers invoke <see cref="IsIncompatibilityError"/> first; since
    /// <c>CANNOT_UPLOAD_PHYS_ADDR</c> does not begin with <c>"RxE_COMM_"</c>
    /// the two matchers are structurally disjoint, and the ordering ensures the
    /// correlation-failure path wins when both would theoretically fire.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception to inspect.</param>
    /// <returns>
    /// <c>true</c> when any exception in the inner-exception chain has a message
    /// containing a word-boundary occurrence of <c>"RxE_COMM_"</c>;
    /// <c>false</c> otherwise.
    /// </returns>
    private static bool IsCommLayerError(Exception ex)
        => SdkCommErrorClassifier.IsCommLayerError(ex);

    /// <summary>
    /// Builds a <see cref="TagServiceException"/> for an <c>RxE_COMM_*</c>
    /// comm-layer fault detected during the connect/upload path, applying a
    /// three-branch triage based on whether the controller is reachable on
    /// EtherNet/IP port 44818.
    ///
    /// <para>
    /// The three branches are:
    /// <list type="number">
    ///   <item>
    ///     <b>Controller reachable (IP parseable AND TCP probe succeeds):</b>
    ///     returns <see cref="TagServiceErrorKind.SdkServiceDegraded"/> — the
    ///     controller answers on port 44818 so the PLC is alive; the fault is
    ///     isolated to <c>LdSdkService</c>'s communications layer.  The raw
    ///     SDK code is included verbatim.
    ///   </item>
    ///   <item>
    ///     <b>Controller not reachable (IP parseable AND TCP probe fails):</b>
    ///     returns <see cref="TagServiceErrorKind.ConnectionFailed"/> naming
    ///     <c>&lt;ip&gt;:44818</c> and the SDK code.  No mention of
    ///     <c>LdSdkService</c> — the PLC itself is the likely cause.
    ///   </item>
    ///   <item>
    ///     <b>IP not derivable from <paramref name="commPath"/>:</b>
    ///     returns <see cref="TagServiceErrorKind.ConnectionFailed"/> with a
    ///     two-cause message — degraded-service cause listed first, unreachable
    ///     controller cause listed second.
    ///   </item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>This method never emits a service-control command.</b>  It only reads
    /// state via <see cref="_controllerReachable"/> and returns a structured error.
    /// </para>
    /// </summary>
    /// <param name="ex">
    /// The original <c>RxE_COMM_*</c> SDK exception.  Preserved as
    /// <see cref="Exception.InnerException"/> of the returned
    /// <see cref="TagServiceException"/>.
    /// </param>
    /// <param name="commPath">
    /// The controller comm path that was being contacted, used to derive the
    /// controller IP for the TCP probe and included in error messages.
    /// </param>
    /// <param name="ct">Cancellation token for the TCP probe.</param>
    private async System.Threading.Tasks.Task<TagServiceException>
        BuildCommLayerTriageExceptionAsync(Exception ex, string commPath, CancellationToken ct)
    {
        var ip = TryParseControllerIp(commPath);

        if (ip is not null)
        {
            var reachable = await _controllerReachable(ip, ct).ConfigureAwait(false);

            if (reachable)
            {
                // Branch (a): controller answers on port 44818 — PLC is alive;
                // fault is isolated to LdSdkService's communications layer.
                AuditLogger.LogSdkServiceDiagnosis(ex.Message, "reachable", "SdkServiceDegraded");
                return new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.SdkServiceDegraded,
                    $"The controller at '{ip}' answers on port 44818 (EtherNet/IP). " +
                    $"LdSdkService is running but its communications layer has faulted. " +
                    $"SDK code: \"{ex.Message}\". " +
                    $"An operator must restart the LdSdkService to recover communications.",
                    SdkCode:           ex.Message,
                    ControllerAddress: ip),
                    ex);
            }

            // Branch (b): controller does not answer on port 44818 — PLC is the
            // likely cause; no mention of LdSdkService.
            AuditLogger.LogSdkServiceDiagnosis(ex.Message, "unreachable", "ConnectionFailed");
            return new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                $"The controller at '{ip}:44818' is not reachable. " +
                $"SDK code: \"{ex.Message}\". " +
                $"Verify that the controller is powered on and connected to the network, " +
                $"then retry."),
                ex);
        }

        // Branch (c): IP not derivable from commPath — surface a two-cause message
        // with the degraded-service cause listed first.  Each cause includes plain-
        // language remediation so an operator knows what action to take.
        AuditLogger.LogSdkServiceDiagnosis(ex.Message, "not-probed", "ConnectionFailed");
        return new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            $"SDK comm-layer fault at '{commPath}': \"{ex.Message}\". " +
            $"Possible causes: " +
            $"(1) LdSdkService is running but its communications layer has faulted — " +
            $"restart LdSdkService to recover; " +
            $"(2) the controller at '{commPath}' is not reachable on port 44818 — " +
            $"verify the controller is powered on and connected to the network."),
            ex);
    }

    /// <summary>
    /// Builds a <see cref="TagServiceException"/> for a dead-session condition
    /// detected at <paramref name="commPath"/>, applying the SCM race-avoidance
    /// re-query protocol before choosing between the definitive "service is not
    /// running" error, the definitive "controller is unreachable" error, and the
    /// "service communications layer faulted" error.
    ///
    /// <para>
    /// <b>SCM race-avoidance re-query protocol.</b>  The Windows Service Control
    /// Manager has been measured to lag behind <c>LdSdkServer.exe</c> process
    /// death by up to 5 seconds.  To avoid falsely asserting "service is running"
    /// when only the SCM entry is stale, the combined service check
    /// (<see cref="_ldSdkServiceRunning"/> AND
    /// <see cref="_ldSdkServerProcessRunning"/>) is performed up to three times:
    /// <list type="number">
    ///   <item>Query 1 — immediately on entry.</item>
    ///   <item>Query 2 — after a 2-second wait (via <see cref="_delay"/>).</item>
    ///   <item>Query 3 — after a further 5-second wait.</item>
    /// </list>
    /// If any query finds the service not running (SCM status ≠ RUNNING, or the
    /// <c>LdSdkServer.exe</c> process is absent), the method immediately emits
    /// the definitive "service is down" error.  Only when <b>all three</b>
    /// queries agree the service is effectively running does the method proceed
    /// to the TCP probe.
    /// </para>
    ///
    /// <para>
    /// <b>Process presence check.</b>  The absence of an <c>LdSdkServer.exe</c>
    /// process is treated as "not running" independently of the SCM state,
    /// covering the observed race window.
    /// </para>
    ///
    /// <para>
    /// When all three checks confirm the service is running, the method probes
    /// the controller on port 44818 via TCP.  If unreachable, it emits a
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> error naming the
    /// controller.  When reachable (or the IP cannot be parsed from
    /// <paramref name="commPath"/>), it emits a
    /// <see cref="TagServiceErrorKind.ConnectionFailed"/> error stating that the
    /// service's communications layer may have faulted.
    /// </para>
    ///
    /// <para>
    /// <b>Pending-edit cause retired (requirement 3a).</b>  Hardware-verified
    /// 2026-09-06: an upload connect succeeded with three pending (I) rungs in
    /// the controller.  <c>"Project is not open."</c> is a dead-service indicator,
    /// not a pending-edit indicator.  No code path in this method emits
    /// <c>"controller holds uncommitted edits"</c> as a cause.
    /// </para>
    ///
    /// <para>
    /// <b>This method never starts, stops, or otherwise controls the service.</b>
    /// It only reads state via <see cref="_ldSdkServiceRunning"/> and
    /// <see cref="_ldSdkServerProcessRunning"/>, and performs a passive TCP probe
    /// via <see cref="_controllerReachable"/>.
    /// </para>
    /// </summary>
    /// <param name="ex">
    /// The original SDK exception whose message triggered the dead-session
    /// detection.  Preserved as the <see cref="Exception.InnerException"/> of
    /// the returned <see cref="TagServiceException"/>.
    /// </param>
    /// <param name="commPath">
    /// The controller comm path that was being contacted, included in the
    /// error message for operator diagnostics.
    /// </param>
    /// <param name="ct">Cancellation token for delays and TCP probe.</param>
    private async System.Threading.Tasks.Task<TagServiceException>
        BuildDeadSessionExceptionAsync(Exception ex, string commPath, CancellationToken ct)
    {
        // Combined check: SCM says RUNNING AND LdSdkServer.exe process is present.
        // Either condition being false is treated as "service not effectively running".
        bool IsEffectivelyRunning() => _ldSdkServiceRunning() && _ldSdkServerProcessRunning();

        // ── Query 1: immediate ────────────────────────────────────────────────
        if (!IsEffectivelyRunning())
        {
            // Observed service state: not running.  Report the definitive fact
            // rather than presenting a list of possibilities.  Do not attempt
            // to restart the service — Windows service recovery handles that.
            return ServiceDownException(ex, commPath);
        }

        // ── Query 2: after 2-second wait ──────────────────────────────────────
        // SCM has been measured to lag behind process death by up to 5 s;
        // re-query before committing to the "service is running" branch.
        await _delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (!IsEffectivelyRunning())
            return ServiceDownException(ex, commPath);

        // ── Query 3: after further 5-second wait ──────────────────────────────
        await _delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!IsEffectivelyRunning())
            return ServiceDownException(ex, commPath);

        // ── All three queries confirm the service is running ──────────────────
        // Probe the controller on port 44818 to distinguish a dead PLC (network
        // unreachable) from a faulted service.
        var ip = TryParseControllerIp(commPath);
        if (ip is not null)
        {
            var reachable = await _controllerReachable(ip, ct).ConfigureAwait(false);
            if (!reachable)
            {
                // Controller is not accepting TCP connections on port 44818 — the PLC
                // is offline or unreachable.
                return new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.ConnectionFailed,
                    $"The SDK call for controller at '{commPath}' failed: \"{ex.Message}\" " +
                    $"The controller at '{ip}' is not reachable on port 44818 (EtherNet/IP). " +
                    $"Verify that the controller is powered on and connected to the network, " +
                    $"then retry."),
                    ex);
            }
        }

        // Service running, process present, controller reachable (or IP not
        // parseable).  The pending-edit hypothesis did not reproduce on hardware
        // (HARDWARE-VERIFIED.md 2026-09-06 "NOT REPRODUCED"): an upload connect
        // succeeded with three pending (I) rungs present.  "Project is not open."
        // is a dead-service indicator, not a pending-edit indicator — requirement 3a.
        return new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            $"Could not extract the project uploaded from '{commPath}'. The SDK " +
            $"reported: \"{ex.Message}\" The Logix SDK service (LdSdkService) is " +
            $"running but its communications layer may have faulted. Check the " +
            $"Windows Application event log for LdSdkServer.exe errors and restart " +
            $"the LdSdkService service, then retry."),
            ex);
    }

    /// <summary>
    /// Builds the definitive "service is not running" exception used by
    /// <see cref="BuildDeadSessionExceptionAsync"/> when any of the three
    /// combined service/process queries confirms the service is down.
    /// </summary>
    private static TagServiceException ServiceDownException(Exception ex, string commPath)
        => new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            $"The SDK call for controller at '{commPath}' failed: \"{ex.Message}\" " +
            $"The Logix SDK service (LdSdkService) is not running; it has crashed " +
            $"or been stopped. Recovery will restart it — wait a few seconds and " +
            $"retry."),
            ex);
}

// ── SdkAdapterInitializer — module-level registration ────────────────────────

/// <summary>
/// Provides the <see cref="Initialize"/> method that the host must call after loading
/// the adapter assembly to register <see cref="SdkConnectFactory"/> as the default SDK
/// backend factory on <see cref="ConnectHandler"/>.
///
/// <para>
/// The primary contract is explicit invocation: <see cref="SdkAdapterLoader.TryLoad"/>
/// calls <see cref="Initialize"/> reflectively after loading the adapter assembly so that
/// registration is guaranteed regardless of whether the runtime fires module initializers.
/// The <see cref="ModuleInitializerAttribute"/> on <see cref="Initialize"/> is a secondary
/// guarantee for hosts that happen to execute adapter code directly, not a substitute for
/// the explicit call.
/// </para>
/// </summary>
internal static class SdkAdapterInitializer
{
    /// <summary>
    /// Registers <see cref="SdkConnectFactory"/> as
    /// <see cref="ConnectHandler.DefaultSdkFactory"/>.
    ///
    /// <para>
    /// Must be called by the host after loading the adapter assembly —
    /// <see cref="SdkAdapterLoader.TryLoad"/> performs this call reflectively.
    /// The <see cref="ModuleInitializerAttribute"/> provides a secondary guarantee for
    /// hosts that happen to execute adapter code directly, but explicit invocation is the
    /// primary contract.
    /// </para>
    /// </summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Intentional: adapter must self-register before any type is used.")]
    internal static void Initialize()
    {
        ConnectHandler.DefaultSdkFactory         = new SdkConnectFactory();
        ConnectHandler.StaleOfflineCopyCleaner   = OfflineBuildCheck.CleanStaleCopies;
        OpenProjectHandler.AcdConverter          = AcdProjectOpener.Default.OpenAsync;
        CloseProjectHandler.SdkSessionInspector  = CloseProjectSdkBridge.Inspect;
        CloseProjectHandler.SdkSessionDisposer   = CloseProjectSdkBridge.Dispose;
        SdkImportDatatypeRunner.Register();
        DoctorEnvironment.SdkObservations        = new DoctorSdkObservations();
        CleanStaleControllerWorkDirs(LogixProjectSession.WorkingDirectoryBase, StaleControllerWorkDirAge, DateTime.UtcNow);
    }

    /// <summary>How old a <c>ctl-*</c> controller-upload work directory must be before startup removes it.</summary>
    internal static readonly TimeSpan StaleControllerWorkDirAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes <c>ctl-*</c> controller-upload work directories under <paramref name="workBase"/> whose last write is
    /// older than <paramref name="maxAge"/>. Other entries (per-project slots, the offline-copy folder, files) are
    /// left alone. Best effort: a directory that cannot be removed (in use, access denied) is skipped; never throws.
    /// </summary>
    /// <param name="workBase">The working-directory base (<c>%TEMP%\logix-mcp-work</c>).</param>
    /// <param name="maxAge">Minimum age for removal.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>The number of directories removed.</returns>
    internal static int CleanStaleControllerWorkDirs(string workBase, TimeSpan maxAge, DateTime nowUtc)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(workBase)) return 0;
            foreach (var dir in Directory.EnumerateDirectories(workBase, "ctl-*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (nowUtc - Directory.GetLastWriteTimeUtc(dir) <= maxAge) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch { /* in use or denied: leave it for the next startup */ }
            }
        }
        catch { /* best effort */ }
        return removed;
    }
}
#endif
