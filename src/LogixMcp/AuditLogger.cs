using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// In-memory copy of the last <see cref="AuditLogger.LogSdkServiceDiagnosis"/> call.
/// </summary>
/// <param name="At">UTC time the diagnosis was made.</param>
/// <param name="SdkCode">Raw SDK error text the diagnosis was based on.</param>
/// <param name="Probe"><c>reachable</c>, <c>unreachable</c> or <c>not-probed</c>.</param>
/// <param name="Kind">Resulting error kind, e.g. <c>SdkServiceDegraded</c>.</param>
public sealed record SdkServiceDiagnosisSnapshot(DateTimeOffset At, string SdkCode, string Probe, string Kind);

/// <summary>
/// A <c>rung_edit</c> <c>"sending"</c> intent line with no completing outcome line.
/// </summary>
/// <param name="Correlation">The intent line's correlation id.</param>
/// <param name="Routine">Routine XPath the import targeted.</param>
/// <param name="InsertPosition">Insert position of the import.</param>
/// <param name="Destination">Destination (<c>controller</c> or <c>live</c>), when recorded.</param>
/// <param name="Timestamp">When the intent line was written, when recorded.</param>
/// <param name="RungTexts">
/// Normalised texts of the rungs the import sent; <c>null</c> or empty for a line written
/// before they were recorded.
/// </param>
public sealed record UnfinishedRungEdit(
    string Correlation, string Routine, uint InsertPosition, string? Destination, string? Timestamp,
    IReadOnlyList<string>? RungTexts = null);

/// <summary>
/// In-memory copy of the phase timings of the last timed <see cref="AuditLogger.LogConnect"/> call.
/// </summary>
/// <param name="At">UTC time the connect finished.</param>
/// <param name="Backend">Backend of the connect.</param>
/// <param name="Target">Target of the connect.</param>
/// <param name="PhaseTimings">Milliseconds spent in each phase.</param>
public sealed record ConnectTimingsSnapshot(
    [property: JsonPropertyName("at")]           DateTimeOffset At,
    [property: JsonPropertyName("backend")]      string Backend,
    [property: JsonPropertyName("target")]       string Target,
    [property: JsonPropertyName("phaseTimings")] ConnectPhaseTimings PhaseTimings);

/// <summary>
/// Gate analysis annotation for a single write destination found in a proposed
/// rung fragment, as recorded in the audit log.
///
/// <para>
/// Mirrors <c>GateAnnotationEntry</c> in the SDK adapter but is defined here in
/// the core assembly so <see cref="AuditLogger"/> can accept and serialise
/// annotations without taking a compile-time dependency on the SDK adapter.
/// </para>
/// </summary>
/// <param name="Tag">
/// Base tag name (stripped of member-access suffixes) found as a proven write
/// operand in the fragment.
/// </param>
/// <param name="Classification">
/// Gate classification derived from the write-gate policy:
/// <c>"granted"</c> when the policy grants this tag for writing;
/// a <c>DenyReason</c> name (e.g. <c>"ProgramWritten"</c>, <c>"SafetyTag"</c>)
/// when the policy denies it;
/// or <c>"not-in-policy"</c> when the tag is absent from the policy.
/// </param>
/// <param name="Provenance">
/// Human-readable location string identifying the L5X element in the fragment
/// where the write was found, e.g. <c>"fragment[5] via OTE"</c>.
/// </param>
public record AuditGateAnnotation(
    [property: JsonPropertyName("tag")]            string Tag,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("provenance")]     string Provenance);

/// <summary>
/// Append-only event logger for controller mutations and gate decisions.
/// Writes exactly one JSON object per line to <c>audit.jsonl</c> in the same
/// per-user state directory as <c>traffic.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Path resolution:
/// <list type="bullet">
///   <item>Windows — <c>%LOCALAPPDATA%\logix-mcp\logs\</c></item>
///   <item>Linux/Mac — <c>$XDG_STATE_HOME/logix-mcp/logs/</c> when
///     <c>XDG_STATE_HOME</c> is set; otherwise
///     <c>~/.local/state/logix-mcp/logs/</c>.</item>
/// </list>
/// </para>
/// <para>
/// Nine event categories are recorded:
/// <list type="bullet">
///   <item><c>write</c> — tag, value written, value observed, verified flag.</item>
///   <item><c>gate_decision</c> — outcome (granted/denied), reason string, and
///     provenance naming the L5X element that determined the decision.</item>
///   <item><c>connect</c> / <c>disconnect</c> — backend, target, correlation rung
///     taken, and whether a controller download occurred.</item>
///   <item><c>rung_edit</c> — routine XPath, insert position, outcome,
///     structural-verify result, and optional gate annotations listing every
///     proven write destination with its classification and provenance.</item>
///   <item><c>tags_staged</c> — one record per tag declaration stripped from a
///     workstation-path fragment and buffered in the session's staged-tag store;
///     carries proposal_id for cross-call correlation.</item>
///   <item><c>tags_created</c> — one record per tag declaration committed to the
///     controller during a controller-path publish; carries proposal_id and
///     granted_namespace to identify immediately writable command tags.</item>
///   <item><c>sdk_service_diagnosis</c> — one record per comm-layer or
///     dead-session classification; carries the raw
///     SDK error code, the TCP-probe result, and the error kind chosen.</item>
///   <item><c>controller_mode</c> — requested mode, virtual flag, and outcome of
///     each non-refused <c>logix_controller_mode</c> write attempt.</item>
///   <item><c>edits_finalized</c> — program, routine, outcome, the routine's
///     pending-edit state observed before and after a <c>logix_finalize_edits</c>
///     call, and optional gate annotations for the pending rung text that was
///     assembled.</item>
/// </list>
/// </para>
/// <para>
/// No secret values (passwords, auth tokens, private keys) pass through these
/// parameters.  Written and observed tag values are included because they are
/// required for the audit record's integrity guarantee.
/// </para>
/// <para>
/// All I/O errors are silently discarded so a full disk, a locked file, or any
/// other fault never propagates to the caller.
/// </para>
/// </remarks>
public static class AuditLogger
{
    // Platform-default log directory, shared with TrafficLogger.  Computed once
    // at class initialisation and never mutated at runtime.
    private static readonly string _defaultLogDir = TrafficLogger.DefaultLogDirectory;

    // Per-execution-context override used by test fixtures so parallel xUnit test
    // classes each redirect writes to their own isolated temp directory without
    // interfering with one another.  AsyncLocal<T> propagates through async/await
    // continuations (unlike [ThreadStatic]), so tests that call async handlers and
    // need to capture the resulting audit writes can set the override before the
    // await and the handler's async continuations will see the same value.
    // null means "use _defaultLogDir".
    private static readonly AsyncLocal<string?> _logDirOverride = new();

    // Synchronise concurrent writes so JSON lines never interleave.
    private static readonly object _lock = new();

    // Options for all audit records: compact output, null properties omitted.
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
        // A refused NaN/Infinity write must still be recorded, not dropped by the serializer.
        NumberHandling         = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // ── Test support ──────────────────────────────────────────────────────────

    /// <summary>
    /// Overrides the log directory used for all subsequent writes in the
    /// <em>current execution context</em> (and any async continuations that
    /// inherit from it).  Because the backing field is <c>AsyncLocal&lt;string?&gt;</c>,
    /// each xUnit test class gets its own isolated temp directory even under the
    /// default parallel runner, and async tests that <c>await</c> the handler
    /// under test will see the override in the handler's async continuations.
    /// Pass <c>null</c> to clear the override and fall back to the
    /// platform-default directory.
    /// Intended for test use only.
    /// </summary>
    internal static void OverrideLogDir(string? dir) =>
        _logDirOverride.Value = dir;

    /// <summary>
    /// The log directory currently in use in the calling execution context.
    /// Returns the per-context override when one has been set via
    /// <see cref="OverrideLogDir"/>; otherwise returns the platform-default
    /// path shared with <see cref="TrafficLogger"/>.
    /// Exposed as <c>internal</c> so tests can inspect platform-appropriate
    /// path construction without writing to production locations.
    /// </summary>
    internal static string LogDirectory => _logDirOverride.Value ?? _defaultLogDir;

    // ── Rotation ───────────────────────────────────────────────────

    /// <summary>Default <c>LGX_AUDIT_MAX_MB</c>: rotate before audit.jsonl exceeds 50 MB.</summary>
    internal const double DefaultMaxMb = 50;

    /// <summary>Default <c>LGX_AUDIT_KEEP</c>: number of rotated files retained.</summary>
    internal const int DefaultKeep = 5;

    // Test-only overrides (per execution context, like _logDirOverride).
    private static readonly AsyncLocal<(double MaxMb, int Keep)?> _rotationOverride = new();
    private static readonly AsyncLocal<Action<string, string>?> _moveOverride = new();

    // Ensures a rotation failure is reported to stderr only once per process.
    private static int _rotationFailureLogged;

    /// <summary>
    /// Overrides the rotation settings for the current execution context.
    /// Pass <c>null</c> to fall back to the environment. Intended for tests.
    /// </summary>
    internal static void OverrideRotation(double? maxMb, int keep = DefaultKeep) =>
        _rotationOverride.Value = maxMb is null ? null : (maxMb.Value, keep);

    /// <summary>
    /// Replaces the file-rename operation (source, destination) for the current
    /// execution context so tests can simulate rename failures. Pass <c>null</c>
    /// to restore <see cref="File.Move(string, string)"/>.
    /// </summary>
    internal static void OverrideMove(Action<string, string>? move) =>
        _moveOverride.Value = move;

    /// <summary>
    /// Maximum audit.jsonl size in megabytes before rotation
    /// (<c>LGX_AUDIT_MAX_MB</c>, default 50; <c>0</c> disables rotation).
    /// Unset, unparsable or negative values fall back to the default.
    /// </summary>
    public static double MaxMb
    {
        get
        {
            if (_rotationOverride.Value is { } o) return o.MaxMb;
            var raw = Environment.GetEnvironmentVariable("LGX_AUDIT_MAX_MB");
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var v)
                   && v >= 0 && double.IsFinite(v)
                ? v : DefaultMaxMb;
        }
    }

    /// <summary>
    /// Number of rotated files retained (<c>LGX_AUDIT_KEEP</c>, default 5,
    /// minimum 1). Unset or unparsable values fall back to the default; values
    /// below 1 are raised to 1 so rotation never discards the just-filled file.
    /// </summary>
    public static int Keep
    {
        get
        {
            if (_rotationOverride.Value is { } o) return Math.Max(1, o.Keep);
            var raw = Environment.GetEnvironmentVariable("LGX_AUDIT_KEEP");
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? Math.Max(1, v) : DefaultKeep;
        }
    }

    /// <summary>
    /// Rotates audit.jsonl when appending <paramref name="incomingBytes"/> would
    /// take it past <see cref="MaxMb"/>: audit.(N-1).jsonl → audit.N.jsonl … audit.jsonl →
    /// audit.1.jsonl, discarding audit.<see cref="Keep"/>.jsonl. Must be called
    /// inside <c>_lock</c>. Never throws: on any failure it reports once to stderr
    /// and returns so the caller keeps appending to the current file.
    /// </summary>
    private static void TryRotate(string logDir, string path, long incomingBytes)
    {
        try
        {
            var maxMb = MaxMb;
            if (maxMb <= 0) return;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return;
            if (info.Length + incomingBytes <= (long)(maxMb * 1024 * 1024)) return;

            var keep = Keep;
            var move = _moveOverride.Value ?? ((src, dst) => File.Move(src, dst));
            string Rotated(int n) => Path.Combine(logDir, $"audit.{n}.jsonl");

            if (File.Exists(Rotated(keep))) File.Delete(Rotated(keep));
            for (var i = keep - 1; i >= 1; i--)
                if (File.Exists(Rotated(i))) move(Rotated(i), Rotated(i + 1));
            move(path, Rotated(1));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _rotationFailureLogged, 1) == 0)
            {
                try { Console.Error.WriteLine($"lgx-mcp: audit log rotation failed ({ex.GetType().Name}: {ex.Message}); continuing to append to the current file."); }
                catch { /* stderr unavailable */ }
            }
        }
    }

    // ── Core append ───────────────────────────────────────────────────────────

    /// <summary>
    /// Current UTC time as ISO 8601 with millisecond precision
    /// (<c>2026-08-04T12:34:56.789Z</c>), the format of every record's <c>ts</c>.
    /// </summary>
    internal static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

    // ── Append failures and fail-closed mode ───────────────────────

    /// <summary>Environment variable that turns audit failures into refused writes.</summary>
    public const string FailClosedEnvVar = "LGX_AUDIT_FAIL_CLOSED";

    // Process-wide count of audit appends that failed. A holder object so tests
    // can swap in an isolated counter for their execution context.
    private sealed class FailureCounter
    {
        public int Count;
        public string? Last;
    }

    private static readonly FailureCounter _globalFailures = new();
    private static readonly AsyncLocal<FailureCounter?> _failuresOverride = new();
    private static readonly AsyncLocal<bool?> _failClosedOverride = new();

    private static FailureCounter Failures => _failuresOverride.Value ?? _globalFailures;

    /// <summary>
    /// Number of audit appends that failed since the process started
    /// (reported by <c>logix_doctor</c> as <c>auditWriteFailures</c>).
    /// </summary>
    public static int WriteFailures => Volatile.Read(ref Failures.Count);

    /// <summary>Reason text of the most recent failed append, or <c>null</c> when none failed.</summary>
    public static string? LastWriteFailure => Volatile.Read(ref Failures.Last);

    /// <summary>
    /// Isolates the failure counter for the current execution context so a test
    /// that provokes append failures does not change the process-wide count.
    /// Intended for tests.
    /// </summary>
    internal static void OverrideFailureCounter(bool isolate) =>
        _failuresOverride.Value = isolate ? new FailureCounter() : null;

    /// <summary>Overrides <see cref="FailClosed"/> for the current execution context. Intended for tests.</summary>
    internal static void OverrideFailClosed(bool? value) => _failClosedOverride.Value = value;

    /// <summary>
    /// <c>true</c> when <c>LGX_AUDIT_FAIL_CLOSED</c> is <c>1</c>, <c>true</c> or <c>yes</c>: a write is refused when the
    /// pre-write check finds the audit log cannot be opened for append, with <see cref="ErrorKinds.AuditUnavailable"/>.
    /// Default off.
    /// </summary>
    public static bool FailClosed =>
        _failClosedOverride.Value
        ?? Capabilities.IsTruthy(Environment.GetEnvironmentVariable(FailClosedEnvVar)?.Trim());

    /// <summary>
    /// Checks that an audit record could be appended right now, without writing one:
    /// creates the log directory when needed and opens <c>audit.jsonl</c> for append.
    /// Used by fail-closed mode before a write reaches the controller.
    /// </summary>
    /// <param name="reason">Why the log is unavailable; <c>null</c> when it is available.</param>
    /// <returns><c>true</c> when the audit log accepts writes.</returns>
    public static bool IsAvailable(out string? reason)
    {
        try
        {
            var logDir = LogDirectory;
            lock (_lock)
            {
                Directory.CreateDirectory(logDir);
                using var fs = new FileStream(Path.Combine(logDir, "audit.jsonl"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            }
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void RecordFailure(Exception ex)
    {
        var reason = $"{ex.GetType().Name}: {ex.Message}";
        var f = Failures;
        Interlocked.Increment(ref f.Count);
        Volatile.Write(ref f.Last, reason);
        try { Console.Error.WriteLine($"audit write failed: {reason}"); }
        catch { /* stderr unavailable */ }
    }

    // ── Core append ───────────────────────────────────────────────────────────

    private static void Append(object evt)
    {
        try
        {
            // Capture LogDirectory into a local once so both reads (path build
            // and Directory.CreateDirectory) use the same value even if another
            // thread calls OverrideLogDir concurrently.
            var logDir = LogDirectory;
            var line   = JsonSerializer.Serialize(evt, _opts);
            var path   = Path.Combine(logDir, "audit.jsonl");

            lock (_lock)
            {
                Directory.CreateDirectory(logDir);
                TryRotate(logDir, path, System.Text.Encoding.UTF8.GetByteCount(line) + 1);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception ex)
        {
            // I/O errors never surface to the caller: report on stderr and count
            // them so logix_doctor shows the audit trail has gaps.
            RecordFailure(ex);
        }
    }

    // ── Public logging API ────────────────────────────────────────────────────

    /// <summary>
    /// Logs a tag-write outcome (success or failure).
    /// </summary>
    /// <param name="tag">Tag name that was written.</param>
    /// <param name="written">Parsed value sent to the controller.</param>
    /// <param name="observed">
    /// Value read back after the write, or <c>null</c> when readback was
    /// not attempted or failed.
    /// </param>
    /// <param name="verified">
    /// <c>true</c> when <paramref name="written"/> and <paramref name="observed"/>
    /// agree within type-appropriate tolerance; <c>false</c> when they differ;
    /// <c>null</c> when readback was not performed.
    /// </param>
    /// <param name="error">
    /// Human-readable error message when the write failed (including gate
    /// denials), or <c>null</c> on success.
    /// </param>
    /// <param name="kind">
    /// Machine-readable error category (e.g. <c>"WriteDenied"</c>) on failure,
    /// or <c>null</c> on success.
    /// </param>
    /// <param name="dataType">
    /// The tag's data type as resolved by the session (e.g. <c>"REAL"</c>), or
    /// <c>null</c> when it is unavailable (refusals, failed readback).
    /// </param>
    /// <param name="controller">
    /// The controller the session targets, as <c>gateway/path</c>, or
    /// <c>null</c> (omitted) when the session has no controller address (virtual, L5X).
    /// </param>
    /// <param name="route">
    /// The tag route the write took (<c>"cip"</c> or <c>"sdk"</c>, from
    /// <see cref="RoutedTagService.ActiveRoute"/>), or <c>null</c> (omitted) when the session is not routed
    /// or no route had been chosen when the record was written.
    /// </param>
    /// <param name="routeNote">
    /// Note recorded when CIP was lost and the write was retried over the SDK; <c>null</c> (omitted) otherwise.
    /// </param>
    public static void LogWrite(
        string  tag,
        object  written,
        object? observed,
        bool?   verified,
        string? error    = null,
        string? kind     = null,
        string? dataType = null,
        string? controller = null,
        string? route      = null,
        string? routeNote  = null)
    {
        Append(new
        {
            ts       = Timestamp(),
            @event   = "write",
            controller,
            route,
            routeNote,
            tag,
            written,
            observed,
            verified,
            error,
            kind,
            dataType,
        });
    }

    /// <summary>
    /// Logs a refused tag operation (for example a read of a tag whose type is
    /// unknown, or of a whole structure) as a <c>refusal</c> event.
    /// </summary>
    /// <param name="operation">Tool operation that was refused (e.g. <c>"logix_read"</c>).</param>
    /// <param name="tag">Tag name the operation targeted.</param>
    /// <param name="kind">
    /// Machine-readable refusal category (<c>"TypeUnknown"</c>,
    /// <c>"StructureReadUnsupported"</c>, <c>"TypeMismatch"</c>).
    /// </param>
    /// <param name="error">Human-readable refusal message.</param>
    public static void LogRefusal(string operation, string tag, string kind, string error)
    {
        Append(new
        {
            ts     = Timestamp(),
            @event = "refusal",
            operation,
            tag,
            kind,
            error,
        });
    }

    /// <summary>
    /// Logs a tool call that was refused before it did anything, as one <c>tool_refused</c> event with
    /// <c>outcome="refused"</c> and the refusal <c>kind</c> (for example <c>NoActiveSession</c>). Used by the
    /// state-changing tools that have no event of their own for a refusal made before their handler runs.
    /// </summary>
    /// <param name="operation">Tool that was refused (e.g. <c>"logix_finalize_edits"</c>).</param>
    /// <param name="kind">Machine-readable refusal kind (see <see cref="ErrorKinds"/>).</param>
    /// <param name="tag">Tag the call named, if any.</param>
    /// <param name="program">Program the call named, if any.</param>
    /// <param name="routine">Routine the call named, if any.</param>
    public static void LogToolRefused(
        string  operation,
        string  kind,
        string? tag     = null,
        string? program = null,
        string? routine = null)
    {
        Append(new
        {
            ts      = Timestamp(),
            @event  = "tool_refused",
            operation,
            outcome = "refused",
            kind,
            tag,
            program,
            routine,
        });
    }

    /// <summary>
    /// Logs a write-gate decision (granted or denied) for a single tag.
    /// </summary>
    /// <param name="tag">Tag name that was evaluated by the gate engine.</param>
    /// <param name="outcome"><c>"granted"</c> or <c>"denied"</c>.</param>
    /// <param name="reason">
    /// Machine-readable denial reason (e.g. <c>"SafetyTag"</c>,
    /// <c>"ProgramWritten"</c>, <c>"NotDesignated"</c>,
    /// <c>"UnknownWriteContext"</c>, <c>"BoundsViolation"</c>), or
    /// <c>null</c> for grants.
    /// </param>
    /// <param name="provenance">
    /// Human-readable explanation naming the L5X element(s) that justified
    /// the decision.
    /// </param>
    public static void LogGateDecision(
        string  tag,
        string  outcome,
        string? reason,
        string  provenance)
    {
        Append(new
        {
            ts         = Timestamp(),
            @event     = "gate_decision",
            tag,
            outcome,
            reason,
            provenance,
        });
    }

    /// <summary>
    /// Logs a successful session connection.
    /// </summary>
    /// <param name="backend">
    /// Backend used: <c>"cip"</c>, <c>"sdk"</c>, <c>"l5x"</c>, or
    /// <c>"virtual"</c>.
    /// </param>
    /// <param name="target">
    /// <c>"online-controller"</c> when writes reach a physical PLC;
    /// <c>"offline-project"</c> otherwise.
    /// </param>
    /// <param name="correlation">
    /// Correlation rung taken during SDK online session establishment
    /// (<c>"already-correlated"</c>, <c>"upload"</c>, <c>"download"</c>,
    /// <c>"upload-to-new-project"</c>), or <c>null</c> for non-SDK backends.
    /// </param>
    /// <param name="downloadPerformed">
    /// <c>true</c> when the PROGRAM→download→go_online→RUN sequence was
    /// executed during session establishment; <c>null</c> for non-SDK backends.
    /// </param>
    /// <param name="gateSource">
    /// Names the origin of this session's write-gate policy when it was derived
    /// from something other than an already-open <c>logix_open_project</c>
    /// project — currently only <c>"uploaded-controller-project"</c>, set when
    /// the comm-path-wins upload-from-controller branch establishes the online
    /// session's gate from a server-managed ACD uploaded from the controller.
    /// <c>null</c> for every other backend/branch, including the
    /// common case where the gate (if any) comes from an already-open project.
    /// </param>
    /// <param name="allowlistSha256">
    /// Lower-case hex SHA-256 of the write allowlist file (<c>LGX_WRITE_ALLOWLIST</c>) active
    /// for this session, or <c>null</c> (omitted) when no allowlist is configured.
    /// </param>
    /// <param name="gateway">Gateway (or SDK comm path) the session targets; omitted when none.</param>
    /// <param name="path">CIP route path; omitted when none.</param>
    /// <param name="gated">Whether a write gate (project-derived policy) is enforced for the session.</param>
    /// <param name="gateSourceSha256">
    /// Lower-case hex SHA-256 of the project file named in <paramref name="gateSource"/>
    /// (<c>project:&lt;path&gt;</c>); omitted for other sources.
    /// </param>
    /// <param name="downloadedFileSha256">
    /// Lower-case hex SHA-256 of the project file that was downloaded to the controller
    /// (PROGRAM→download→go_online→RUN sequence); omitted when no download was attempted.
    /// </param>
    public static void LogConnect(
        string  backend,
        string  target,
        string? correlation,
        bool?   downloadPerformed,
        string? gateSource = null,
        string? allowlistSha256 = null,
        string? gateway = null,
        string? path = null,
        bool?   gated = null,
        string? gateSourceSha256 = null,
        string? downloadedFileSha256 = null,
        ConnectPhaseTimings? phaseTimings = null)
    {
        if (phaseTimings is not null)
            LastConnect = new ConnectTimingsSnapshot(DateTimeOffset.UtcNow, backend, target, phaseTimings);
        // The effective command prefixes (LGX_GATE_COMMAND_PREFIXES) in force for this session.
        var gateConfig = GateConfigResolver.FromEnvironment() as GateConfig;
        Append(new
        {
            ts                 = Timestamp(),
            @event             = "connect",
            command_prefixes        = gateConfig?.CommandPrefixesWire,
            command_prefixes_source = gateConfig?.CommandPrefixesSource,
            backend,
            target,
            gateway,
            path,
            gated,
            correlation,
            download_performed = downloadPerformed,
            gate_source        = gateSource,
            gate_source_sha256 = gateSourceSha256,
            allowlist_sha256   = allowlistSha256,
            downloaded_file_sha256 = downloadedFileSha256,
            phaseTimings,
        });
    }

    /// <summary>Audit event name of an SDK server restart that happened inside a connect (G46).</summary>
    public const string ConnectSdkRestartEvent = "connect_sdk_restart";

    /// <summary>
    /// Logs an <c>LdSdkServer</c> restart that a <c>logix_connect</c> lived through, whether or not the connect then
    /// succeeded. <c>logix_doctor</c> counts these over the last 24 h, by phase.
    /// </summary>
    /// <param name="restart">The restart: phase it hit, pids before and after, when it was seen.</param>
    /// <param name="target">Comm path of the connect, when known.</param>
    public static void LogConnectSdkRestart(SdkRestartRecord restart, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(restart);
        Append(new
        {
            ts     = Timestamp(),
            @event = ConnectSdkRestartEvent,
            target,
            phase  = restart.Phase,
            old_pid = restart.OldPid,
            new_pid = restart.NewPid,
            detected_at = restart.DetectedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Reads the <see cref="ConnectSdkRestartEvent"/> lines of the audit log (the current file and the rotated ones)
    /// written since <paramref name="since"/>, and returns the phase of each, oldest first. Never throws; an
    /// unreadable file or line is skipped.
    /// </summary>
    /// <param name="since">Earliest time to include (UTC).</param>
    /// <returns>One phase name per restart; empty when there are none or the log cannot be read.</returns>
    public static IReadOnlyList<string> ReadConnectSdkRestartPhases(DateTimeOffset since)
    {
        var found = new List<(DateTimeOffset At, string Phase)>();
        try
        {
            var dir = LogDirectory;
            var files = new List<string> { Path.Combine(dir, "audit.jsonl") };
            for (var i = 1; i <= Keep; i++) files.Add(Path.Combine(dir, $"audit.{i}.jsonl"));
            foreach (var path in files)
            {
                string text;
                try
                {
                    lock (_lock)
                    {
                        if (!File.Exists(path)) continue;
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                        text = reader.ReadToEnd();
                    }
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains(ConnectSdkRestartEvent, StringComparison.Ordinal)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.GetProperty("event").GetString() != ConnectSdkRestartEvent) continue;
                        if (!DateTimeOffset.TryParse(root.GetProperty("ts").GetString(),
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var at) || at < since)
                            continue;
                        var phase = root.TryGetProperty("phase", out var p) ? p.GetString() : null;
                        found.Add((at, string.IsNullOrEmpty(phase) ? "unknown" : phase));
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
        return found.OrderBy(f => f.At).Select(f => f.Phase).ToList();
    }

    /// <summary>
    /// The phase timings of the most recent timed connect in this process, or <c>null</c> when none has run.
    /// Read by <c>logix_doctor</c>; never persisted.
    /// </summary>
    public static ConnectTimingsSnapshot? LastConnect { get; private set; }

    /// <summary>
    /// Logs a session disconnection — either explicit (via
    /// <see cref="TagSessionService.Detach"/>) or implicit (when a new session
    /// replaces an existing one via <see cref="TagSessionService.Attach"/>).
    /// </summary>
    public static void LogDisconnect()
    {
        Append(new
        {
            ts     = Timestamp(),
            @event = "disconnect",
        });
    }

    /// <summary>
    /// Logs a project close (<c>logix_close_project</c>) — always a successful
    /// close; refusals (pending staged batch, no project open) are not logged
    /// here since no state changed.
    /// </summary>
    /// <param name="path">File-system path of the project that was closed.</param>
    /// <param name="sdkSessionDisposed">
    /// <c>true</c> when a file-backed SDK session (<see cref="LogixProjectSession"/>)
    /// was disposed as part of the close; <c>false</c> when no SDK session was
    /// active (core-only build, or the active tag session was not SDK-backed).
    /// </param>
    public static void LogProjectClosed(string path, bool sdkSessionDisposed)
    {
        Append(new
        {
            ts                   = Timestamp(),
            @event               = "project_closed",
            path,
            sdk_session_disposed = sdkSessionDisposed,
        });
    }

    /// <summary>
    /// Appends a <c>datatype_import</c> audit event for one real (non-dry)
    /// <c>logix_import_datatype</c> call.
    /// </summary>
    /// <param name="acdPath">Input project path the datatype was imported from.</param>
    /// <param name="saveAs">Destination path requested by the caller.</param>
    /// <param name="datatypeName">Name of the datatype targeted by the import.</param>
    /// <param name="collision">Collision option in effect (e.g. <c>"Overwrite"</c>).</param>
    /// <param name="outcome">
    /// Machine-readable outcome, e.g. <c>"success"</c>, <c>"refused_no_confirm"</c>, or
    /// <c>"error:&lt;Kind&gt;"</c>.
    /// </param>
    public static void LogDatatypeImport(
        string acdPath,
        string saveAs,
        string datatypeName,
        string collision,
        string outcome)
    {
        Append(new
        {
            ts = Timestamp(),
            @event = "datatype_import",
            acdPath,
            saveAs,
            datatypeName,
            collision,
            outcome,
        });
    }

    /// <summary>
    /// Logs one tag declaration being staged (stripped from a workstation-path
    /// fragment and held in the session's staged-tag buffer) per call.
    /// One event per tag is written so individual declarations are independently
    /// queryable in <c>audit.jsonl</c>.
    /// </summary>
    /// <param name="proposalId">
    /// Opaque identifier generated once per <c>logix_propose_rung_edit</c>
    /// invocation.  Links multiple <c>tags_staged</c> lines from the same call
    /// and correlates them with the eventual <c>tags_created</c> lines written
    /// when those declarations are published to the controller.
    /// </param>
    /// <param name="tagNames">
    /// Names of the tag declarations stripped from the fragment and buffered in
    /// this call.  An event is written for each name.
    /// </param>
    public static void LogTagsStaged(string proposalId, IEnumerable<string> tagNames)
    {
        foreach (var tag in tagNames)
        {
            Append(new
            {
                ts          = Timestamp(),
                @event      = "tags_staged",
                proposal_id = proposalId,
                tag,
            });
        }
    }

    /// <summary>
    /// Logs one tag declaration being permanently created on the controller
    /// (applied during a controller-path publish operation) per call.
    /// One event per tag is written so individual declarations are independently
    /// queryable in <c>audit.jsonl</c>.
    /// </summary>
    /// <param name="proposalId">
    /// Opaque identifier generated once per <c>logix_propose_rung_edit</c>
    /// invocation.  Links multiple <c>tags_created</c> lines from the same call
    /// and correlates them with the earlier <c>tags_staged</c> lines where those
    /// declarations first appeared.
    /// </param>
    /// <param name="tags">
    /// Pairs of (tag name, granted-namespace flag).  <c>grantedNamespace</c> is
    /// <c>true</c> when the tag name falls in a write-gate command namespace
    /// (<c>Cmd_</c> or <c>Sim_</c>) and is therefore immediately writable by the
    /// <c>logix_write</c> tool under the active policy.  An event is written for
    /// each pair.
    /// </param>
    public static void LogTagsCreated(
        string proposalId,
        IEnumerable<(string tagName, bool grantedNamespace)> tags)
    {
        foreach (var (tagName, grantedNamespace) in tags)
        {
            Append(new
            {
                ts                = Timestamp(),
                @event            = "tags_created",
                proposal_id       = proposalId,
                tag               = tagName,
                granted_namespace = grantedNamespace,
            });
        }
    }

    /// <summary>
    /// The most recent SDK service diagnosis logged by this process, or
    /// <c>null</c> when none has been logged. Read by <c>logix_doctor</c>; never persisted.
    /// </summary>
    public static SdkServiceDiagnosisSnapshot? LastSdkServiceDiagnosis { get; private set; }

    /// <summary>
    /// Logs one SDK comm-layer or dead-session diagnosis decision, emitted after
    /// every classification that involves the comm-layer discriminator.
    /// </summary>
    /// <param name="sdkCode">
    /// The raw SDK error code string extracted from the exception message
    /// (e.g. <c>"RxE_COMM_DTLERROR - ..."</c>).  Included verbatim so it can
    /// be searched in the audit log.
    /// </param>
    /// <param name="probe">
    /// Result of the TCP reachability probe on port 44818:
    /// <list type="bullet">
    ///   <item><c>"reachable"</c> — controller answered; fault is in the SDK service.</item>
    ///   <item><c>"unreachable"</c> — controller did not answer; PLC is likely offline.</item>
    ///   <item><c>"not-probed"</c> — no probe was attempted (IP not derivable, or
    ///     in-session path where the controller was already known to be reachable).</item>
    /// </list>
    /// </param>
    /// <param name="kind">
    /// The <see cref="TagServiceErrorKind"/> name chosen for the resulting
    /// <see cref="TagServiceException"/> (e.g. <c>"SdkServiceDegraded"</c>,
    /// <c>"ConnectionFailed"</c>).
    /// </param>
    public static void LogSdkServiceDiagnosis(
        string sdkCode,
        string probe,
        string kind)
    {
        LastSdkServiceDiagnosis = new SdkServiceDiagnosisSnapshot(DateTimeOffset.UtcNow, sdkCode, probe, kind);
        Append(new
        {
            ts       = Timestamp(),
            @event   = "sdk_service_diagnosis",
            sdk_code = sdkCode,
            probe,
            kind,
        });
    }

    /// <summary>
    /// Logs a proposed rung edit, whether online (pending in Logix Designer) or
    /// offline (saved directly to the L5X file).
    /// </summary>
    /// <param name="routine">
    /// XPath string identifying the target routine, e.g.
    /// <c>Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']</c>.
    /// </param>
    /// <param name="insertPosition">Zero-based rung position at which the edit was applied.</param>
    /// <param name="outcome">
    /// <c>"proposed"</c> — edit left pending in Logix Designer (online path);
    /// <c>"saved-to-file"</c> — edit written directly to the L5X file (offline path);
    /// <c>"denied"</c> — gate policy rejected a write operand in the fragment;
    /// <c>"error"</c> — any other failure (SDK error, I/O error, etc.).
    /// </param>
    /// <param name="structuralVerify">
    /// Post-import connected-state string (e.g. <c>"online"</c>) when the SDK
    /// state check was performed and confirmed on a controller or live publish;
    /// the failing state on a post-import state error; <c>null</c> when not
    /// applicable (offline path, workstation staging, gate denial, or error
    /// before the SDK call). Workstation staging never writes <c>"online"</c> here:
    /// Logix does not check a rung staged in the session image, so it reports
    /// <paramref name="stagedIn"/> and <paramref name="verified"/> instead.
    /// </param>
    /// <param name="destination">
    /// The resolved dispatch destination — where the edit actually landed or was
    /// attempted.  One of:
    /// <list type="bullet">
    ///   <item><c>"controller"</c> — edit sent to the physical controller via
    ///     <c>PartialImportWithTargetFromXmlFileAsync</c> with
    ///     <c>AcceptEdits</c>.</item>
    ///   <item><c>"workstation"</c> — edit applied to the server's in-memory
    ///     project image only via <c>PartialImportRungsFromXmlFileAsync</c> with
    ///     <c>LeaveEdits</c>.</item>
    ///   <item><c>"file"</c> — edit written directly to the L5X file on disk
    ///     (offline path, no SDK session involved).</item>
    /// </list>
    /// This field is populated from the resolved dispatch path (what actually
    /// happened), not from the raw user-supplied <c>target</c> parameter value.
    /// <c>null</c> when the destination could not be determined (should not
    /// occur in practice).
    /// </param>
    /// <param name="gateAnnotations">
    /// Gate analysis for each proven write destination found in the proposed
    /// fragment: tag name, classification (e.g. <c>"granted"</c>,
    /// <c>"ProgramWritten"</c>, <c>"SafetyTag"</c>), and provenance string
    /// identifying the L5X element.
    /// Populated for <c>"proposed"</c> and <c>"saved-to-file"</c> outcomes when
    /// a write-gate policy was available at the time of the proposal.
    /// <c>null</c> when no policy was provided, when the fragment contained no
    /// proven write operands, or for error and denial outcomes.
    /// </param>
    /// <param name="buildVerify">
    /// Result of the SDK build check: <c>"passed"</c>, <c>"failed"</c>, <c>"unavailable"</c>
    /// (Logix Designer before v37 cannot run it) or <c>"skipped"</c> (the edit failed
    /// before the build check was reached); <c>null</c> when the record predates it.
    /// </param>
    /// <param name="designerVersion">Logix Designer <c>major.minor</c> when known, else <c>null</c>.</param>
    /// <param name="confirmedByUser">
    /// The value of the <c>confirmedByUser</c> parameter supplied by the caller
    /// on this controller-target publish attempt.  <c>true</c> when the agent
    /// presented the staged change set to the user and received explicit
    /// confirmation before calling; <c>false</c> when the agent did not request
    /// confirmation (Auto mode, or a failed Confirm-mode gate attempt).
    /// <c>null</c> for workstation-target and offline-path edits where the
    /// concept does not apply.
    /// </param>
    /// <param name="stagedIn">
    /// Where a workstation-target stage landed: <c>"session-image"</c> (the server's in-memory
    /// project image). <c>null</c> for every other target and outcome.
    /// </param>
    /// <param name="verified">
    /// Whether the staged rung was checked: <c>false</c> by default for workstation staging
    /// (Logix does not check an online-staged rung), <c>true</c> only when
    /// <c>verifyOnStage</c> replayed it offline and verify passed; <c>null</c> for other targets.
    /// A controller publish whose post-publish census could not be read back records <c>false</c>.
    /// </param>
    /// <param name="kind">
    /// The error kind for a <c>"refused"</c> outcome (e.g. <c>WrongMnemonicSet</c>,
    /// <c>UnsupportedOnRevision</c>, <c>RoutineNotFound</c>); <c>null</c> otherwise.
    /// </param>
    /// <param name="correlation">
    /// The correlation id of the <c>"sending"</c> intent line this line completes (see
    /// <see cref="LogRungEditSending"/>); <c>null</c> (omitted) when no intent line was written for the call.
    /// </param>
    /// <param name="rungTexts">
    /// Normalised texts of the rungs a <c>"sending"</c> line is about to import; <c>null</c> (omitted) otherwise.
    /// </param>
    public static void LogRungEdit(
        string                              routine,
        uint                                insertPosition,
        string                              outcome,
        string?                             structuralVerify,
        string?                             destination       = null,
        IReadOnlyList<AuditGateAnnotation>? gateAnnotations   = null,
        bool?                               confirmedByUser   = null,
        string?                             buildVerify       = null,
        string?                             designerVersion   = null,
        string?                             stagedIn          = null,
        bool?                               verified          = null,
        string?                             kind              = null,
        string?                             correlation       = null,
        IReadOnlyList<string>?              rungTexts         = null)
    {
        Append(new
        {
            ts                  = Timestamp(),
            @event              = "rung_edit",
            routine,
            insert_position     = insertPosition,
            outcome,
            kind,
            correlation,
            rung_texts          = rungTexts is { Count: > 0 } ? rungTexts : null,
            structural_verify   = structuralVerify,
            staged_in           = stagedIn,
            verified,
            destination,
            gate_annotations    = gateAnnotations,
            confirmed_by_user   = confirmedByUser,
            build_verify        = buildVerify,
            designer_version    = designerVersion,
        });

        // A complete publish closes every earlier intent line of the same routine.
        if (outcome == RungEditProposedOutcome && destination is "controller" or "live")
            AbandonSupersededRungEdits(routine, correlation);
    }

    /// <summary>The <c>rung_edit</c> outcome of a complete, uncontradicted publish.</summary>
    public const string RungEditProposedOutcome = "proposed";

    /// <summary>
    /// The <c>rung_edit</c> outcome written for an intent line that was superseded or whose rungs are not in the
    /// routine's pending zone: it is closed without a warning.
    /// </summary>
    public const string RungEditAbandonedOutcome = "abandoned";

    private static void AbandonSupersededRungEdits(string routine, string? completedCorrelation)
    {
        try
        {
            foreach (var intent in FindUnfinishedRungEdits())
                if (string.Equals(intent.Routine, routine, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(intent.Correlation, completedCorrelation, StringComparison.Ordinal))
                    LogRungEditAbandoned(intent);
        }
        catch
        {
            // best effort: closing stale intent lines must not fail the publish's own audit line
        }
    }

    /// <summary>The <c>rung_edit</c> outcome of an intent line written before a controller-changing import.</summary>
    public const string RungEditSendingOutcome = "sending";

    /// <summary>The <c>rung_edit</c> outcome written on the next connect for an intent line that never completed.</summary>
    public const string RungEditOrphanedOutcome = "orphaned";

    /// <summary>Creates a new correlation id tying a <c>"sending"</c> intent line to its final outcome line.</summary>
    /// <returns>A new opaque id.</returns>
    public static string NewRungEditCorrelation() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Writes the intent line of a controller-changing rung import (<c>rung_edit</c>, outcome <c>"sending"</c>)
    /// BEFORE the import is sent, so a server killed mid-import still leaves a record. The call's final outcome
    /// line carries the same <paramref name="correlation"/> and completes it; a <c>"sending"</c> line with no
    /// completing line is reported as orphaned on the next connect.
    /// </summary>
    /// <param name="routine">Routine XPath the import targets.</param>
    /// <param name="insertPosition">Insert position of the import.</param>
    /// <param name="destination">Resolved destination: <c>controller</c> or <c>live</c>.</param>
    /// <param name="correlation">Correlation id from <see cref="NewRungEditCorrelation"/>.</param>
    /// <param name="rungTexts">
    /// Normalised texts of the rungs the import sends. The next connect reports the line as orphaned only when one
    /// of them is in the routine's pending zone. <c>null</c> or empty records none.
    /// </param>
    public static void LogRungEditSending(
        string routine, uint insertPosition, string destination, string correlation,
        IReadOnlyList<string>? rungTexts = null) =>
        LogRungEdit(routine, insertPosition, RungEditSendingOutcome, null,
            destination: destination, correlation: correlation, rungTexts: rungTexts);

    /// <summary>
    /// Writes the <c>"abandoned"</c> line that closes an intent line without a warning: a later complete publish
    /// superseded it, or its rungs are not in the routine's pending zone.
    /// </summary>
    /// <param name="intent">The unfinished intent line.</param>
    public static void LogRungEditAbandoned(UnfinishedRungEdit intent) =>
        LogRungEdit(intent.Routine, intent.InsertPosition, RungEditAbandonedOutcome, null,
            destination: intent.Destination, correlation: intent.Correlation);

    /// <summary>
    /// Writes the <c>"orphaned"</c> line that completes an intent line the server never finished (it was killed or
    /// crashed between the <c>"sending"</c> line and the outcome line).
    /// </summary>
    /// <param name="intent">The unfinished intent line.</param>
    public static void LogRungEditOrphaned(UnfinishedRungEdit intent) =>
        LogRungEdit(intent.Routine, intent.InsertPosition, RungEditOrphanedOutcome, null,
            destination: intent.Destination, correlation: intent.Correlation);

    /// <summary>
    /// Reads the current audit log and returns every <c>rung_edit</c> <c>"sending"</c> line that no later line with
    /// the same correlation id completed, oldest first. Never throws (an unreadable log yields an empty list).
    /// </summary>
    /// <returns>The unfinished intent lines.</returns>
    public static IReadOnlyList<UnfinishedRungEdit> FindUnfinishedRungEdits()
    {
        var open = new Dictionary<string, UnfinishedRungEdit>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(LogDirectory, "audit.jsonl");
            string[] lines;
            lock (_lock)
            {
                if (!File.Exists(path)) return [];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
                lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "rung_edit") continue;
                    if (!root.TryGetProperty("correlation", out var corr) || corr.GetString() is not { Length: > 0 } id) continue;
                    var outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() : null;
                    if (outcome == RungEditSendingOutcome)
                    {
                        open[id] = new UnfinishedRungEdit(
                            id,
                            root.TryGetProperty("routine", out var r) ? r.GetString() ?? string.Empty : string.Empty,
                            root.TryGetProperty("insert_position", out var ip) && ip.TryGetUInt32(out var pos) ? pos : 0,
                            root.TryGetProperty("destination", out var d) ? d.GetString() : null,
                            root.TryGetProperty("ts", out var t) ? t.GetString() : null,
                            root.TryGetProperty("rung_texts", out var rt) && rt.ValueKind == JsonValueKind.Array
                                ? [.. rt.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
                                : null);
                    }
                    else
                    {
                        open.Remove(id);
                    }
                }
                catch (JsonException) { /* a torn or foreign line is skipped */ }
            }
        }
        catch
        {
            return [];
        }
        return [.. open.Values];
    }

    /// <summary>
    /// Logs the outcome of a <c>logix_finalize_edits</c> call — the gated tool that
    /// assembles a routine's pending controller edits into the running program via
    /// a zero-rung import.
    /// </summary>
    /// <param name="program">Program name the finalize call targeted.</param>
    /// <param name="routine">Routine name the finalize call targeted.</param>
    /// <param name="outcome">
    /// Machine-readable outcome, e.g. <c>"finalized"</c>, <c>"refused"</c>, or
    /// <c>"error"</c>.
    /// </param>
    /// <param name="beforeState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> value observed
    /// immediately before the finalize attempt (<c>"none"</c>, <c>"pending"</c>,
    /// <c>"tested"</c>, or <c>"accepted"</c>), or <c>null</c> when it could not be
    /// determined.
    /// </param>
    /// <param name="afterState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> value observed
    /// immediately after the finalize attempt, or <c>null</c> when the finalize
    /// call did not reach the point of re-reading state (e.g. refused before the
    /// SDK was ever called).
    /// </param>
    /// <param name="gateAnnotations">
    /// Gate analysis for each proven write destination found in the pending rung
    /// text that was evaluated as if it were being written now, or <c>null</c>
    /// when no policy was available or no proven write operands were found.
    /// </param>
    /// <param name="operation">
    /// Optional operation tag: <c>"delete"</c> for a live rung delete.
    /// <c>null</c> (field omitted) for every other finalize.
    /// </param>
    public static void LogFinalizeEdits(
        string                              program,
        string                              routine,
        string                              outcome,
        string?                             beforeState,
        string?                             afterState,
        IReadOnlyList<AuditGateAnnotation>? gateAnnotations = null,
        string?                             operation       = null)
    {
        if (operation is not null)
        {
            // A live rung delete is audited with operation "delete" so it can be
            // told apart from an insert/replace finalize. The field is omitted otherwise,
            // keeping the existing edits_finalized shape unchanged.
            Append(new
            {
                ts               = Timestamp(),
                @event           = "edits_finalized",
                program,
                routine,
                outcome,
                operation,
                before_state     = beforeState,
                after_state      = afterState,
                gate_annotations = gateAnnotations,
            });
            return;
        }

        Append(new
        {
            ts               = Timestamp(),
            @event           = "edits_finalized",
            program,
            routine,
            outcome,
            before_state     = beforeState,
            after_state      = afterState,
            gate_annotations = gateAnnotations,
        });
    }

    /// <summary>
    /// Logs the creation of an empty ladder routine by <c>logix_create_routine</c>
    /// (<c>routine_created</c>). Written once the import has been attempted and re-read:
    /// <c>created</c> when the re-export shows the routine, <c>not_confirmed</c> when the import
    /// returned but the re-export does not.
    /// </summary>
    /// <param name="program">Program the routine was created in.</param>
    /// <param name="routine">Name of the new routine.</param>
    /// <param name="outcome">Machine-readable outcome: <c>"created"</c> or <c>"not_confirmed"</c>.</param>
    /// <param name="createdInController">
    /// <c>true</c> when the session was online (the routine is in the controller's project);
    /// <c>false</c> for an offline session (it is only in the session's project image).
    /// </param>
    /// <param name="confirmedByUser">The <c>confirmedByUser</c> flag the call carried.</param>
    public static void LogRoutineCreated(
        string program,
        string routine,
        string outcome,
        bool   createdInController,
        bool   confirmedByUser)
    {
        Append(new
        {
            ts                    = Timestamp(),
            @event                = "routine_created",
            program,
            routine,
            outcome,
            created_in_controller = createdInController,
            confirmed_by_user     = confirmedByUser,
        });
    }

    /// <summary>
    /// Logs a failed cross-instance connect registry operation. The registry is advisory, so the failure is
    /// recorded here and otherwise ignored.
    /// </summary>
    /// <param name="operation">What failed (<c>write</c>, <c>prune</c>, <c>read</c>, <c>find</c>).</param>
    /// <param name="error">The error text.</param>
    public static void LogConnectRegistryFailure(string operation, string error)
    {
        Append(new
        {
            ts     = Timestamp(),
            @event = "connect_registry_failure",
            operation,
            error,
        });
    }

    /// <summary>
    /// Logs a capability-disabled refusal — emitted when a
    /// <see cref="CapabilityDisabledError"/> is returned before any network or
    /// SDK call because the required opt-in environment variable is not set.
    /// </summary>
    /// <param name="operation">
    /// MCP tool operation name that was refused (e.g. <c>"logix_write"</c>,
    /// <c>"logix_controller_mode"</c>, <c>"logix_connect"</c>,
    /// <c>"logix_propose_rung_edit"</c>, <c>"logix_cancel_pending_edits"</c>).
    /// </param>
    /// <param name="envVar">
    /// The environment variable name whose absence caused the refusal
    /// (e.g. <see cref="Capabilities.EnvVar"/> or
    /// <see cref="Capabilities.EnvVarStudio"/>).
    /// </param>
    public static void LogCapabilityRefusal(string operation, string envVar)
    {
        Append(new
        {
            ts        = Timestamp(),
            @event    = "capability_refusal",
            operation,
            env_var   = envVar,
        });
    }

    /// <summary>
    /// Logs a non-refused <c>logix_controller_mode</c> write attempt.
    /// Refusals (capability gate, missing confirm) are not recorded here.
    /// </summary>
    /// <param name="requestedMode">The mode the caller asked for (as supplied).</param>
    /// <param name="isVirtual">
    /// <c>true</c> when the session is virtual (in-memory tracking only).
    /// </param>
    /// <param name="outcome">
    /// <c>"recorded_virtual"</c> when the request was stored in the virtual
    /// session's in-memory state, or <c>"ModeChangeNotApplied"</c> when the
    /// session is live and no change was sent to the controller.
    /// </param>
    public static void LogControllerMode(string requestedMode, bool isVirtual, string outcome)
    {
        Append(new
        {
            ts             = Timestamp(),
            @event         = "controller_mode",
            requested_mode = requestedMode,
            @virtual       = isVirtual,
            outcome,
        });
    }

    /// <summary>
    /// Appends exactly one <c>test_run</c> audit event for a <c>logix_run_test</c> call that
    /// executed its cases. Carries case names and verdicts, not tag values.
    /// </summary>
    /// <param name="overall"><c>pass</c>, <c>fail</c> or <c>restore_failed</c>.</param>
    /// <param name="cases">Each executed case's name and verdict (<c>pass</c>/<c>fail</c>), in run order.</param>
    /// <param name="durationMs">Total elapsed milliseconds.</param>
    public static void LogTestRun(
        string                                       overall,
        IEnumerable<(string Name, string Verdict)>   cases,
        long                                         durationMs)
    {
        Append(new
        {
            ts          = Timestamp(),
            @event      = "test_run",
            overall,
            cases       = cases.Select(c => new { name = c.Name, verdict = c.Verdict }).ToArray(),
            duration_ms = durationMs,
        });
    }

    /// <summary>
    /// Appends an <c>edits_cancelled</c> audit event for one
    /// <c>logix_cancel_pending_edits</c> call — the Studio-GUI-driven
    /// cancellation of a routine's pending edit zone.
    /// </summary>
    /// <param name="program">Program name the cancel call targeted.</param>
    /// <param name="routine">Routine name the cancel call targeted.</param>
    /// <param name="outcome">
    /// Machine-readable outcome, e.g. <c>"cancelled"</c>, <c>"refused"</c>, or
    /// <c>"error"</c>.
    /// </param>
    /// <param name="beforeState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> value observed
    /// immediately before the cancel attempt (<c>"none"</c>, <c>"pending"</c>,
    /// <c>"tested"</c>, or <c>"accepted"</c>), or <c>null</c> when it could not be
    /// determined.
    /// </param>
    /// <param name="afterState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> value observed
    /// immediately after the cancel attempt, or <c>null</c> when the cancel call
    /// did not reach the point of re-reading state (e.g. refused before the
    /// session-1 script was ever invoked).
    /// </param>
    public static void LogCancelPendingEdits(
        string  program,
        string  routine,
        string  outcome,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = "edits_cancelled",
            program,
            routine,
            outcome,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// Appends a <c>staged_cleared</c> audit event for one <c>logix_clear_staged</c> call: the session's staged
    /// rung and tag buffers were emptied for one routine or for all. Nothing was sent to the controller.
    /// </summary>
    /// <param name="scope"><c>"all"</c> or the routine the caller named.</param>
    /// <param name="routines">XPath of each routine whose staged rungs were cleared.</param>
    /// <param name="rungCount">Number of staged rungs cleared.</param>
    /// <param name="tagNames">Names of the staged tag declarations cleared.</param>
    /// <param name="tagsRetained">Number of staged tag declarations left in place because other routines still hold staged rungs.</param>
    /// <param name="imageRestored">
    /// <c>true</c> when the session image was rebuilt and the export of every cleared routine matched
    /// the controller image; <c>false</c> when it did not (<c>ClearIncomplete</c>); <c>null</c> when no image work was needed.
    /// </param>
    /// <param name="replayedBuckets">Number of staged buckets of other routines replayed into the rebuilt image.</param>
    public static void LogStagedCleared(
        string                scope,
        IEnumerable<string>   routines,
        int                   rungCount,
        IEnumerable<string>   tagNames,
        int                   tagsRetained,
        bool?                 imageRestored   = null,
        int                   replayedBuckets = 0)
    {
        Append(new
        {
            ts            = Timestamp(),
            @event        = "staged_cleared",
            scope,
            routines      = routines.ToArray(),
            rung_count    = rungCount,
            tag_names     = tagNames.ToArray(),
            tags_retained = tagsRetained,
            controller_touched = false,
            imageRestored,
            replayedBuckets,
        });
    }

    /// <summary>
    /// Appends an <c>edits_accepted</c> audit event for one
    /// <c>logix_accept_pending_edits</c> call — the Studio-GUI-driven
    /// assembly of a routine's pending edit zone, which makes the rungs execute
    /// on the controller. Written only when the after-state was
    /// confirmed <c>"none"</c>.
    /// </summary>
    /// <param name="program">Program name the accept call targeted.</param>
    /// <param name="routine">Routine name the accept call targeted.</param>
    /// <param name="outcome">Machine-readable outcome, e.g. <c>"accepted"</c>.</param>
    /// <param name="beforeState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> observed before the gesture,
    /// or <c>null</c> when unknown.
    /// </param>
    /// <param name="afterState">
    /// The routine's <c>logix_pending_edits</c> <c>state</c> observed after the gesture,
    /// or <c>null</c> when unknown.
    /// </param>
    public static void LogAcceptPendingEdits(
        string  program,
        string  routine,
        string  outcome,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = "edits_accepted",
            program,
            routine,
            outcome,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// Appends an <c>edits_test_confirmed</c> audit event for one <c>logix_test_pending_edits</c> call (posts Studio's Test command; the new logic executes on the controller).
    /// Written only after the gesture reported success and the after-state matched the verb's contract.
    /// </summary>
    /// <param name="program">Program name the call targeted.</param>
    /// <param name="routine">Routine name the call targeted.</param>
    /// <param name="outcome">Machine-readable outcome, e.g. <c>"test_confirmed"</c>.</param>
    /// <param name="beforeState">The routine's <c>logix_pending_edits</c> state before the gesture, or <c>null</c>.</param>
    /// <param name="afterState">The routine's <c>logix_pending_edits</c> state after the gesture, or <c>null</c>.</param>
    public static void LogTestPendingEdits(
        string  program,
        string  routine,
        string  outcome,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = "edits_test_confirmed",
            program,
            routine,
            outcome,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// Appends an <c>edits_untest_confirmed</c> audit event for one <c>logix_untest_pending_edits</c> call (posts Studio's Untest command; the original logic is restored).
    /// Written only after the gesture reported success and the after-state matched the verb's contract.
    /// </summary>
    /// <param name="program">Program name the call targeted.</param>
    /// <param name="routine">Routine name the call targeted.</param>
    /// <param name="outcome">Machine-readable outcome, e.g. <c>"untest_confirmed"</c>.</param>
    /// <param name="beforeState">The routine's <c>logix_pending_edits</c> state before the gesture, or <c>null</c>.</param>
    /// <param name="afterState">The routine's <c>logix_pending_edits</c> state after the gesture, or <c>null</c>.</param>
    public static void LogUntestPendingEdits(
        string  program,
        string  routine,
        string  outcome,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = "edits_untest_confirmed",
            program,
            routine,
            outcome,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// Appends an <c>edits_assembled</c> audit event for one <c>logix_assemble_pending_edits</c> call (posts Studio's Assemble command; irreversible).
    /// Written only after the gesture reported success and the after-state matched the verb's contract.
    /// </summary>
    /// <param name="program">Program name the call targeted.</param>
    /// <param name="routine">Routine name the call targeted.</param>
    /// <param name="outcome">Machine-readable outcome, e.g. <c>"assembled"</c>.</param>
    /// <param name="beforeState">The routine's <c>logix_pending_edits</c> state before the gesture, or <c>null</c>.</param>
    /// <param name="afterState">The routine's <c>logix_pending_edits</c> state after the gesture, or <c>null</c>.</param>
    public static void LogAssemblePendingEdits(
        string  program,
        string  routine,
        string  outcome,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = "edits_assembled",
            program,
            routine,
            outcome,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// The audit <c>event</c> name each Studio-lifecycle verb writes (<c>cancel</c> → <c>edits_cancelled</c>, ...),
    /// or <c>null</c> for a verb with no event of its own.
    /// </summary>
    /// <param name="verb">The lifecycle verb: <c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c> or <c>assemble</c>.</param>
    /// <returns>The event name used by both the success and the failure line of that verb.</returns>
    internal static string? LifecycleEventOf(string verb) => verb switch
    {
        "cancel"   => "edits_cancelled",
        "accept"   => "edits_accepted",
        "test"     => "edits_test_confirmed",
        "untest"   => "edits_untest_confirmed",
        "assemble" => "edits_assembled",
        _          => null,
    };

    /// <summary>
    /// Appends the failure line of one Studio-lifecycle call that got past the pre-launch checks and failed:
    /// the verb's own event (see <see cref="LifecycleEventOf"/>) with <c>outcome="error"</c>, the response
    /// <c>kind</c> and the driver <c>reason</c>. A failure line never carries a success outcome
    /// (<c>accepted</c>, <c>cancelled</c>, ...). Pre-launch refusals are logged with <see cref="LogToolRefused"/> instead.
    /// </summary>
    /// <param name="verb">The lifecycle verb (<c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>).</param>
    /// <param name="program">Program the call targeted.</param>
    /// <param name="routine">Routine the call targeted.</param>
    /// <param name="kind">Machine-readable failure kind (<c>command_not_applicable</c>-style kinds, <c>StillPending</c>, ...).</param>
    /// <param name="reason">The driver's reason, or <c>null</c> when the failure did not come from the driver.</param>
    /// <param name="beforeState">The routine's pending-edit state before the gesture, or <c>null</c>.</param>
    /// <param name="afterState">The routine's pending-edit state after the gesture, or <c>null</c> when it was not re-read.</param>
    public static void LogLifecycleFailure(
        string  verb,
        string  program,
        string  routine,
        string  kind,
        string? reason,
        string? beforeState,
        string? afterState)
    {
        Append(new
        {
            ts           = Timestamp(),
            @event       = LifecycleEventOf(verb) ?? "edits_" + verb,
            program,
            routine,
            outcome      = "error",
            kind,
            reason,
            before_state = beforeState,
            after_state  = afterState,
        });
    }

    /// <summary>
    /// Appends the audit line of a Studio-lifecycle call whose driver toggled the Errors pane: the same event and
    /// fields as <see cref="LogLifecycleFailure"/> (when <paramref name="outcome"/> is <c>"error"</c>) or as the verb's
    /// success line, plus <c>errors_pane_toggled:true</c>. Calls that did not toggle the pane never use this method, so
    /// the field is omitted when false.
    /// </summary>
    /// <param name="verb">The lifecycle verb (<c>cancel</c>, <c>accept</c>, <c>test</c>, <c>untest</c>, <c>assemble</c>).</param>
    /// <param name="program">Program the call targeted.</param>
    /// <param name="routine">Routine the call targeted.</param>
    /// <param name="outcome"><c>"error"</c> or the verb's success status.</param>
    /// <param name="kind">Failure kind; written only when <paramref name="outcome"/> is <c>"error"</c>.</param>
    /// <param name="reason">Driver reason; written only when <paramref name="outcome"/> is <c>"error"</c>.</param>
    /// <param name="beforeState">The routine's pending-edit state before the gesture, or <c>null</c>.</param>
    /// <param name="afterState">The routine's pending-edit state after the gesture, or <c>null</c>.</param>
    /// <param name="errorsPaneLayout">The Errors pane layout the driver reported (<c>errors_pane_layout</c>); omitted from the line when <c>null</c>.</param>
    public static void LogLifecycleWithPaneToggle(
        string  verb,
        string  program,
        string  routine,
        string  outcome,
        string? kind,
        string? reason,
        string? beforeState,
        string? afterState,
        string? errorsPaneLayout = null)
    {
        var failed = string.Equals(outcome, "error", StringComparison.Ordinal);
        var evt = new Dictionary<string, object?>
        {
            ["ts"]      = Timestamp(),
            ["event"]   = LifecycleEventOf(verb) ?? "edits_" + verb,
            ["program"] = program,
            ["routine"] = routine,
            ["outcome"] = outcome,
        };
        if (failed)
        {
            evt["kind"]   = kind;
            evt["reason"] = reason;
        }
        evt["before_state"]        = beforeState;
        evt["after_state"]         = afterState;
        evt["errors_pane_toggled"] = true;
        if (errorsPaneLayout is not null) evt["errors_pane_layout"] = errorsPaneLayout;
        Append(evt);
    }

    /// <summary>
    /// Appends an <c>image_reconciled</c> audit event: <c>logix_pending_edits</c> found that the pending zone this session
    /// published to <paramref name="program"/> was resolved outside lgx-mcp (in Logix Designer) and brought the session image
    /// in line. One line per program reconciled.
    /// </summary>
    /// <param name="program">Program whose recorded zone was found gone.</param>
    /// <param name="routine">The routine(s) this session had published to, comma-separated when there are several.</param>
    /// <param name="outcome"><c>accepted</c> (records applied), <c>cancelled</c> (image restored) or <c>stale</c> (image flagged).</param>
    public static void LogImageReconciled(string program, string routine, string outcome)
    {
        Append(new
        {
            ts      = Timestamp(),
            @event  = "image_reconciled",
            program,
            routine,
            outcome,
        });
    }
}
