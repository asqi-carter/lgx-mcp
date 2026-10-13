using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Append-only per-call instrumentation logger.
/// Writes exactly one JSON line per MCP tool call to <c>traffic.jsonl</c> in
/// the same per-user state directory as <c>audit.jsonl</c>.
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
/// Each record contains: UTC timestamp (epoch seconds), tool name, request
/// size in bytes, response size in bytes, elapsed wall-clock time in
/// milliseconds, and an <c>ok</c> boolean.  Argument content and result
/// content are <b>never</b> recorded — only sizes.
/// </para>
/// <para>
/// All I/O errors are silently discarded so a full disk, a locked file, or
/// any other fault never propagates to the caller.
/// </para>
/// </remarks>
public static class TrafficLogger
{
    // Platform-default log directory, resolved once at startup.
    private static readonly string _defaultLogDir = ResolveLogDir();

    // Per-execution-context override used by test fixtures so parallel xUnit test
    // classes each redirect writes to their own isolated temp directory without
    // interfering with one another; null means "use _defaultLogDir".  AsyncLocal,
    // like AuditLogger: a [ThreadStatic] value set in an async test class's
    // constructor stayed on that pool thread when Dispose ran on another one, and
    // later tests on the thread saw the stale directory (TrafficLoggerTests flake).
    private static readonly AsyncLocal<string?> _logDirOverride = new();

    // ── Path resolution ───────────────────────────────────────────────────────

    private static string ResolveLogDir()
    {
        // Windows: %LOCALAPPDATA%\logix-mcp\logs\
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "logix-mcp", "logs");
        }

        // Linux/Mac: $XDG_STATE_HOME/logix-mcp/logs/
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgState))
            return Path.Combine(xdgState, "logix-mcp", "logs");

        // Fallback: ~/.local/state/logix-mcp/logs/
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "logix-mcp", "logs");
    }

    // ── Test support ──────────────────────────────────────────────────────────

    /// <summary>
    /// Overrides the log directory used for all subsequent writes in the
    /// <em>calling execution context</em>.  Because the backing field is
    /// <c>AsyncLocal</c>, each xUnit test class gets its own isolated
    /// temp directory even under the default parallel runner.
    /// Pass <c>null</c> to clear the per-context override and fall back to the
    /// platform-default directory.
    /// Intended for test use only.
    /// </summary>
    internal static void OverrideLogDir(string? dir) =>
        _logDirOverride.Value = dir;

    /// <summary>
    /// The log directory currently in use in the calling execution context.
    /// Returns the per-context override when one has been set via
    /// <see cref="OverrideLogDir"/>; otherwise returns the platform-default path.
    /// Exposed as <c>internal</c> so tests can verify platform-appropriate
    /// path construction without writing to production locations.
    /// </summary>
    internal static string LogDirectory => _logDirOverride.Value ?? _defaultLogDir;

    /// <summary>
    /// The platform-default log directory, ignoring any per-context override set
    /// via <see cref="OverrideLogDir"/>.  Use this for process-wide defaults
    /// (e.g. <see cref="AuditLogger"/>) that must not capture a test thread's
    /// temporary directory.
    /// </summary>
    internal static string DefaultLogDirectory => _defaultLogDir;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Appends one traffic record to <c>traffic.jsonl</c>.
    /// All I/O errors are silently discarded.
    /// </summary>
    /// <param name="toolName">
    /// The MCP tool name (e.g. <c>"logix_read"</c>).
    /// </param>
    /// <param name="requestBytes">
    /// Byte size of the serialised request arguments.  Must not include
    /// argument values — only the size of their JSON representation.
    /// </param>
    /// <param name="responseBytes">
    /// Byte size of the serialised response string in UTF-8.
    /// </param>
    /// <param name="elapsedMs">
    /// Wall-clock elapsed time in milliseconds from the start of the tool
    /// dispatch to the return of the response string.
    /// </param>
    /// <param name="ok">
    /// <c>true</c> when the response does not carry a top-level
    /// <c>"error"</c> property; <c>false</c> when it does.
    /// </param>
    /// <param name="dataType">
    /// The resolver-reported data type from a typed read or write result, or
    /// <c>null</c> (omitted from the record) when the response carries none.
    /// </param>
    public static void Append(
        string toolName,
        int    requestBytes,
        int    responseBytes,
        long   elapsedMs,
        bool   ok,
        string? dataType = null)
    {
        try
        {
            var record = new TrafficRecord(
                Ts:        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Tool:      toolName,
                ReqBytes:  requestBytes,
                RespBytes: responseBytes,
                ElapsedMs: elapsedMs,
                Ok:        ok,
                DataType:  dataType);

            var line   = JsonSerializer.Serialize(record) + "\n";
            var logDir = LogDirectory;

            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "traffic.jsonl"), line);
        }
        catch
        {
            // Best-effort: full disk, locked file, missing directory, or any
            // other I/O fault must never surface to the caller.
        }
    }

    // ── Measurement helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <paramref name="responseJson"/> does not
    /// contain a top-level <c>"error"</c> property, indicating success.
    /// Returns <c>true</c> on any parse error (non-JSON responses are
    /// treated as non-error).
    /// </summary>
    /// <param name="responseJson">JSON string returned by a tool handler.</param>
    public static bool IsOkResponse(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return !doc.RootElement.TryGetProperty("error", out _);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Returns the top-level <c>"dataType"</c> string of a tool response, or
    /// <c>null</c> when absent, not a string, or the response is not JSON.
    /// </summary>
    /// <param name="responseJson">JSON string returned by a tool handler.</param>
    public static string? ExtractDataType(string? responseJson)
    {
        if (string.IsNullOrEmpty(responseJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("dataType", out var dt) &&
                   dt.ValueKind == JsonValueKind.String
                ? dt.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the UTF-8 byte count of the JSON serialisation of
    /// <paramref name="arguments"/>, without retaining any argument values.
    /// Returns <c>0</c> when <paramref name="arguments"/> is <c>null</c>.
    /// </summary>
    /// <param name="arguments">
    /// An anonymous object whose fields mirror the tool's parameter list.
    /// Only the serialised <em>size</em> is used — values are discarded.
    /// </param>
    public static int MeasureRequestBytes(object? arguments)
    {
        if (arguments is null)
            return 0;
        try
        {
            return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(arguments));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns the UTF-8 byte count of <paramref name="responseJson"/>.
    /// Returns <c>0</c> for null or empty strings.
    /// </summary>
    /// <param name="responseJson">The response JSON string to measure.</param>
    public static int MeasureResponseBytes(string? responseJson) =>
        string.IsNullOrEmpty(responseJson)
            ? 0
            : Encoding.UTF8.GetByteCount(responseJson);
}

// ── Serialisation support types ───────────────────────────────────────────────

/// <summary>
/// Immutable value type representing one traffic log record.
/// Serialised as a compact JSONL line with snake_case field names.
/// </summary>
internal readonly record struct TrafficRecord(
    [property: JsonPropertyName("ts")]         long   Ts,
    [property: JsonPropertyName("tool")]       string Tool,
    [property: JsonPropertyName("req_bytes")]  int    ReqBytes,
    [property: JsonPropertyName("resp_bytes")] int    RespBytes,
    [property: JsonPropertyName("elapsed_ms")] long   ElapsedMs,
    [property: JsonPropertyName("ok")]         bool   Ok,
    [property: JsonPropertyName("data_type"),
               JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
               string? DataType = null);
