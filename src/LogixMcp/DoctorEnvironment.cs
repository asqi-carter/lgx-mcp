// ── DoctorEnvironment — production IDoctorEnvironment ─────────────
//
// Read-only: every member observes. Nothing here connects a session, changes a mode,
// launches a process or writes a file. This type lives in the core assembly so logix_doctor
// is available whether or not the SDK adapter loaded. Observations that need Rockwell types
// (assembly version, SDK session, pending-edit zones) come from IDoctorSdkObservations, which
// the adapter registers when it loads; the Windows service query stays behind LOGIX_SDK.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

/// <summary>
/// SDK-only observations the core assembly cannot make itself (it has no compile-time
/// dependency on <c>RockwellAutomation.*</c>). Implemented by <c>LogixMcp.SdkAdapter</c> and
/// registered on <see cref="DoctorEnvironment.SdkObservations"/> when the adapter loads.
/// </summary>
public interface IDoctorSdkObservations
{
    /// <summary>Version of the loaded Rockwell SDK assembly, or null when it cannot be read.</summary>
    string? AssemblyVersion { get; }

    /// <summary>The active SDK session as the doctor reports it; null when the active session is not an SDK session.</summary>
    DoctorSessionInfo? GetSession(TagSessionService tags);

    /// <summary>Reads zone state per routine from the active online SDK session (read-only). Throws on failure.</summary>
    Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(TagSessionService? tags, CancellationToken ct);

    /// <summary>
    /// The capability table of the active SDK session (<see cref="SdkCapabilities"/>); null when the active
    /// session is not an SDK session.
    /// </summary>
    SdkCapabilitiesReport? GetCapabilities(TagSessionService tags) => null;
}

/// <summary>Production <see cref="IDoctorEnvironment"/> backed by the real process, machine and session registry.</summary>
public sealed class DoctorEnvironment : IDoctorEnvironment
{
    private const string StudioProcessName = "LogixDesigner";

    /// <summary>
    /// SDK observations registered by the adapter on load; null when the adapter is missing,
    /// failed to load, or this is a no-SDK build.
    /// </summary>
    public static IDoctorSdkObservations? SdkObservations { get; set; }

    /// <summary>
    /// An <see cref="IDoctorSdkObservations"/> that reports "no SDK adapter": null version and session,
    /// and a failing zone read. Pass it as the <c>observations</c> constructor argument to get the
    /// no-adapter behavior without writing <see cref="SdkObservations"/>.
    /// </summary>
    public static IDoctorSdkObservations NoObservations { get; } = new NoSdkObservations();

    private sealed class NoSdkObservations : IDoctorSdkObservations
    {
        public string? AssemblyVersion => null;
        public DoctorSessionInfo? GetSession(TagSessionService tags) => null;
        public Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(TagSessionService? tags, CancellationToken ct) =>
            throw new InvalidOperationException("the SDK adapter is not loaded");
    }

    private readonly TagSessionService? _tagSessions;
    private readonly ProjectService?    _project;
    private readonly IEnvironmentProbe  _probe;
    private readonly IDoctorSdkObservations? _observations;

    // The observations this instance reports: the injected one, else the adapter-registered static default
    // (null when no adapter is loaded).
    private IDoctorSdkObservations? Observations => _observations is null ? SdkObservations : (ReferenceEquals(_observations, NoObservations) ? null : _observations);

    /// <summary>Creates the environment.</summary>
    /// <param name="tagSessions">The session registry to inspect (never modified); null means "no session".</param>
    /// <param name="probe">Process/session/elevation probe; defaults to <see cref="EnvironmentProbe"/>.</param>
    /// <param name="project">The project service whose open project supplies the target revision when no session records one.</param>
    /// <param name="observations">SDK observations for this instance; null uses the adapter-registered <see cref="SdkObservations"/>, <see cref="NoObservations"/> means "no adapter".</param>
    public DoctorEnvironment(TagSessionService? tagSessions, IEnvironmentProbe? probe = null, ProjectService? project = null,
        IDoctorSdkObservations? observations = null)
    {
        _observations = observations;
        _tagSessions = tagSessions;
        _project     = project;
        _probe       = probe ?? new EnvironmentProbe();
    }

    /// <inheritdoc />
    public int CurrentSessionId => _probe.CurrentSessionId;

    /// <inheritdoc />
    public bool IsElevated => _probe.IsElevated;

    /// <inheritdoc />
    public string? GateConfigRaw() => GateConfigResolver.RawFromEnvironment();

    /// <inheritdoc />
    public string? WriteAllowlistRaw() => WriteAllowlist.PathFromEnvironment();

    /// <inheritdoc />
    public bool SdkSupportedInBuild =>
#if LOGIX_SDK
        true;
#else
        false;
#endif

    /// <inheritdoc />
    public string? SdkAssemblyVersion => Observations?.AssemblyVersion;

    /// <inheritdoc />
    /// <remarks>
    /// Names every directory <see cref="SdkAdapterLoader"/> probed and why it was rejected, followed
    /// by the recorded adapter load error when there is one, so a box without the SDK says where
    /// to put it.
    /// </remarks>
    public string? SdkLoadFailureReason
    {
        get
        {
            string probed;
            try
            {
                probed = "probed " + string.Join("; ",
                    SdkAdapterLoader.DescribeProbeResults(SdkAdapterLoader.CollectProbeResults()));
            }
            catch
            {
                probed = "the probed paths could not be listed";
            }

            var failure = SdkAdapterLoader.LoadFailureReason;
            return failure is null ? probed : $"{probed}; adapter load error: {failure}";
        }
    }

    /// <inheritdoc />
    public bool CipRuntimeLoads()
    {
        try
        {
            LibPlcTagNativeInit.EnsureInitialized();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsOwnInterfaceAddress(IPAddress address)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.Equals(address)) return true;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
        }
        return false;
    }

    /// <inheritdoc />
    public string GetServiceState(string serviceName)
    {
#if LOGIX_SDK
        if (!OperatingSystem.IsWindows()) return DoctorServiceState.Unsupported;
        return QueryService(serviceName);
#else
        return DoctorServiceState.Unsupported;
#endif
    }

    /// <inheritdoc />
    /// <remarks>
    /// Windows only. Reads the Application log through <c>EventLogReader</c> (read-only; the
    /// System.Diagnostics.EventLog assembly the host already ships) for <c>.NET Runtime</c> and
    /// <c>Application Error</c> events of the last 24 h and counts those naming <c>LdSdkServer.exe</c>
    /// (see <see cref="CountSdkServerCrashes"/>). Null when the log cannot be read.
    /// </remarks>
    public int? SdkServerCrashesLast24h()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ReadSdkServerCrashes(); }
        catch { return null; }
    }

    /// <inheritdoc />
    /// <remarks>Windows only; reads the same Application-log events as <see cref="SdkServerCrashesLast24h"/>.</remarks>
    public DateTimeOffset? NewestSdkServerCrashLast24h()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return NewestSdkServerCrash(ReadCrashEventsXml(), DateTimeOffset.UtcNow); }
        catch { return null; }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Windows only. Reads the System log (event 7031, Service Control Manager, for <c>LdSdkService</c>) and the
    /// Application log (<c>LdSdkServer.exe</c> crashes) for the last 7 days; see <see cref="CountSdkCrashes"/>.
    /// </remarks>
    public int? SdkServerCrashesLast7d()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ReadSdkCrashes(TimeSpan.FromDays(7)); }
        catch { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static int? ReadSdkCrashes(TimeSpan window) =>
        CountSdkCrashes(ReadCrashEventsXml(window), ReadServiceCrashEventsXml(window), DateTimeOffset.UtcNow, window);

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static string ReadServiceCrashEventsXml(TimeSpan window) =>
        ReadLogXml("System",
            "*[System[Provider[@Name='Service Control Manager'] and (EventID=7031) and " +
            $"TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]");

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static string ReadLogXml(string logName, string query)
    {
        var xml = new System.Text.StringBuilder();
        using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
            new System.Diagnostics.Eventing.Reader.EventLogQuery(
                logName, System.Diagnostics.Eventing.Reader.PathType.LogName, query));
        for (var ev = reader.ReadEvent(); ev is not null; ev = reader.ReadEvent())
        {
            using (ev) xml.Append(ev.ToXml());
        }
        return xml.ToString();
    }

    // NoInlining: an event-log assembly that cannot load fails when the enclosing method is JIT-compiled,
    // before any try block is entered, so the touch lives in its own method behind the IsWindows() guard.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static int? ReadSdkServerCrashes() => ReadSdkCrashes(TimeSpan.FromHours(24));

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static string ReadCrashEventsXml() => ReadCrashEventsXml(TimeSpan.FromHours(24));

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site.")]
    private static string ReadCrashEventsXml(TimeSpan window) =>
        ReadLogXml("Application",
            "*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error']) " +
            $"and TimeCreated[timediff(@SystemTime) <= {(long)window.TotalMilliseconds}]]]");

    /// <summary>
    /// Counts <c>LdSdkServer.exe</c> crashes in <paramref name="eventsXml"/>: the concatenated <c>&lt;Event&gt;</c>
    /// elements <c>wevtutil qe /f:xml</c> prints. An event counts when its provider is <c>.NET Runtime</c> or
    /// <c>Application Error</c>, its data names <c>LdSdkServer.exe</c>, and it falls in the 24 h before
    /// <paramref name="now"/>. One crash writes both a <c>.NET Runtime</c> and an <c>Application Error</c> event
    /// within moments, so events less than 30 s apart count once. Null when the text cannot be parsed.
    /// </summary>
    /// <param name="eventsXml">The wevtutil output.</param>
    /// <param name="now">The current time.</param>
    internal static int? CountSdkServerCrashes(string eventsXml, DateTimeOffset now) =>
        CountSdkCrashes(eventsXml, "", now, TimeSpan.FromHours(24));

    /// <summary>
    /// Counts SDK crashes in <paramref name="window"/> before <paramref name="now"/> from two log queries: the
    /// Application-log events (<c>LdSdkServer.exe</c>, as <see cref="CountSdkServerCrashes"/>) and the System-log
    /// <c>Service Control Manager</c> event 7031 naming <c>LdSdkService</c> (the service terminated unexpectedly).
    /// Events less than 30 s apart count once, across both logs: one crash writes several events. Null when either
    /// text cannot be parsed.
    /// </summary>
    /// <param name="applicationXml">The Application-log events as XML.</param>
    /// <param name="systemXml">The System-log events as XML.</param>
    /// <param name="now">The current time.</param>
    /// <param name="window">How far back to count.</param>
    internal static int? CountSdkCrashes(string applicationXml, string systemXml, DateTimeOffset now, TimeSpan window)
    {
        var app = SdkServerCrashTimes(applicationXml, now, window);
        var sys = SdkServerCrashTimes(systemXml, now, window);
        if (app is null || sys is null) return null;

        var times = app.Concat(sys).Order().ToList();
        var crashes = 0;
        DateTimeOffset? last = null;
        foreach (var t in times)
        {
            if (last is null || t - last.Value >= TimeSpan.FromSeconds(30)) crashes++;
            last = t;
        }
        return crashes;
    }

    /// <summary>
    /// The time of the newest <c>LdSdkServer.exe</c> crash event in <paramref name="eventsXml"/> within the 24 h before
    /// <paramref name="now"/> (same filtering as <see cref="CountSdkServerCrashes"/>). Null when there is none or the
    /// text cannot be parsed.
    /// </summary>
    /// <param name="eventsXml">The wevtutil output.</param>
    /// <param name="now">The current time.</param>
    internal static DateTimeOffset? NewestSdkServerCrash(string eventsXml, DateTimeOffset now)
    {
        var times = SdkServerCrashTimes(eventsXml, now);
        return times is { Count: > 0 } ? times[^1] : null;
    }

    /// <summary>Sorted times of the LdSdkServer.exe crash events in the window; null when the text cannot be parsed.</summary>
    private static List<DateTimeOffset>? SdkServerCrashTimes(string eventsXml, DateTimeOffset now) =>
        SdkServerCrashTimes(eventsXml, now, TimeSpan.FromHours(24));

    private static List<DateTimeOffset>? SdkServerCrashTimes(string eventsXml, DateTimeOffset now, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(eventsXml)) return new List<DateTimeOffset>();
        System.Xml.Linq.XDocument doc;
        try { doc = SafeXml.ParseDocument("<Events>" + eventsXml + "</Events>"); }
        catch (System.Xml.XmlException) { return null; }

        var times = new List<DateTimeOffset>();
        foreach (var ev in doc.Root!.Elements().Where(e => e.Name.LocalName == "Event"))
        {
            var system   = ev.Elements().FirstOrDefault(e => e.Name.LocalName == "System");
            var provider = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "Provider")?.Attribute("Name")?.Value;
            var text = ev.ToString();
            if (provider is ".NET Runtime" or "Application Error")
            {
                if (!text.Contains("LdSdkServer.exe", StringComparison.OrdinalIgnoreCase)) continue;
            }
            else if (provider == "Service Control Manager")
            {
                // Event 7031: "The <service> service terminated unexpectedly."
                var id = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value;
                if (id != "7031" || !text.Contains("LdSdkService", StringComparison.OrdinalIgnoreCase)) continue;
            }
            else continue;

            var stamp = system?.Elements().FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (!DateTimeOffset.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                continue;
            if (at < now - window || at > now + TimeSpan.FromMinutes(5)) continue;
            times.Add(at);
        }

        times.Sort();
        return times;
    }

    /// <inheritdoc />
    public SdkServiceDiagnosisSnapshot? LastSdkServiceDiagnosis => AuditLogger.LastSdkServiceDiagnosis;

    /// <inheritdoc />
    public ConnectTimingsSnapshot? LastConnect => AuditLogger.LastConnect;

    /// <inheritdoc />
    /// <remarks>Read from this server's audit log (<c>connect_sdk_restart</c> lines of the last 24 h); never null here.</remarks>
    public SdkConnectCrashes? SdkCrashesDuringConnect24h =>
        SdkConnectCrashes.From(AuditLogger.ReadConnectSdkRestartPhases(DateTimeOffset.UtcNow - TimeSpan.FromHours(24)));

    /// <inheritdoc />
    public string AuditDirectory => AuditLogger.LogDirectory;

    /// <inheritdoc />
    /// <remarks>
    /// Probes without creating or changing anything: an existing <c>audit.jsonl</c> is opened for
    /// write with <see cref="FileMode.Open"/> (no truncation, no bytes written); otherwise the
    /// nearest existing ancestor directory must not be read-only.
    /// </remarks>
    public bool IsAuditDirectoryWritable()
    {
        try
        {
            var dir  = AuditDirectory;
            var file = Path.Combine(dir, "audit.jsonl");
            if (File.Exists(file))
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }

            var probe = new DirectoryInfo(dir);
            while (probe is not null && !probe.Exists) probe = probe.Parent;
            if (probe is null) return false;
            if ((probe.Attributes & FileAttributes.ReadOnly) != 0) return false;
            if (!OperatingSystem.IsWindows())
                return (File.GetUnixFileMode(probe.FullName) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public long? AuditLogBytes()
    {
        try
        {
            var info = new FileInfo(Path.Combine(AuditDirectory, "audit.jsonl"));
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TcpConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own timeout
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<byte[]> ReadFaultRecordAsync(string host, CancellationToken ct) =>
        ControllerFaultReader.ReadAsync(host, ct: ct);

    /// <inheritdoc />
    public DoctorSessionInfo? GetActiveSession()
    {
        var tags = _tagSessions;
        if (tags is null || tags.Service is null) return null;

        if (tags.IsVirtual)
            return new DoctorSessionInfo("virtual", "offline-project", false, null, null);

        if (Observations?.GetSession(tags) is { } sdk) return sdk;
        var live = tags.IsLiveController;
        return new DoctorSessionInfo(live ? "cip" : "l5x", live ? "online-controller" : "offline-project", live, null, null);
    }

    /// <inheritdoc />
    public TargetRevisionInfo GetTargetRevision() => TargetRevision.Current(_tagSessions, _project);

    /// <inheritdoc />
    public Task<IReadOnlyList<DoctorZone>> ReadPendingZonesAsync(CancellationToken ct)
    {
        var sdk = Observations
            ?? throw new InvalidOperationException("the SDK adapter is not loaded");
        return sdk.ReadPendingZonesAsync(_tagSessions, ct);
    }

    /// <inheritdoc />
    public SdkCapabilitiesReport GetSdkCapabilities() => SdkCapabilitiesView.Current(_tagSessions);

    /// <inheritdoc />
    public IReadOnlyList<int> StudioSessionIds => _probe.LogixDesignerSessionIds;

    /// <inheritdoc />
    public bool StudioIntegrityHigherThanCurrent => _probe.StudioIntegrityHigherThanCurrent;

    /// <inheritdoc />
    /// <remarks>
    /// <c>Process.MainWindowTitle</c> is only populated for windows in this process's session,
    /// which is exactly the set the Studio check can act on.
    /// </remarks>
    public IReadOnlyList<string> StudioWindowTitlesInCurrentSession
    {
        get
        {
            var titles  = new List<string>();
            var current = _probe.CurrentSessionId;
            foreach (var p in Process.GetProcessesByName(StudioProcessName))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId == current) titles.Add(p.MainWindowTitle ?? string.Empty);
                    }
                    catch { /* process exited */ }
                }
            }
            return titles;
        }
    }

#if LOGIX_SDK
    // NoInlining: a missing ServiceController assembly fails when the enclosing method is
    // JIT-compiled, before any try block is entered, so the touch lives in its own method
    // behind the OperatingSystem.IsWindows() guard at the call site.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Guarded by OperatingSystem.IsWindows() at the call site; the block is SDK-only (Windows).")]
    private static string QueryService(string serviceName)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(serviceName);
            return sc.Status.ToString(); // read-only: never Start/Stop/Refresh-with-side-effects
        }
        catch (InvalidOperationException)
        {
            return DoctorServiceState.NotInstalled;
        }
        catch
        {
            return DoctorServiceState.Unsupported;
        }
    }
#endif
}
