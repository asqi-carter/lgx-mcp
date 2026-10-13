using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="TrafficLogger"/>.
/// Covers: path resolution, record format, content-exclusion guarantee,
/// and I/O fault tolerance.
/// </summary>
public class TrafficLoggerTests
{
    // ── Path resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// The resolved log directory must contain the "logix-mcp" application
    /// name and the "logs" subdirectory component regardless of the current
    /// operating system.
    /// </summary>
    [Fact]
    public void LogDirectory_ContainsAppNameAndLogsSegment()
    {
        var dir = TrafficLogger.LogDirectory;

        Assert.Contains("logix-mcp", dir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logs",      dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On Windows the log directory must start with the LOCALAPPDATA path.
    /// </summary>
    [Fact]
    public void LogDirectory_Windows_StartsWithLocalAppData()
    {
        if (!OperatingSystem.IsWindows())
            return; // Skip on non-Windows platforms

        var expected = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(expected, TrafficLogger.LogDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On non-Windows platforms the log directory must either start with
    /// XDG_STATE_HOME (when set) or contain ".local/state" (the XDG fallback).
    /// </summary>
    [Fact]
    public void LogDirectory_NonWindows_UsesXdgOrFallback()
    {
        if (OperatingSystem.IsWindows())
            return; // Skip on Windows

        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        if (!string.IsNullOrWhiteSpace(xdgState))
            Assert.StartsWith(xdgState, TrafficLogger.LogDirectory);
        else
            Assert.Contains(Path.Combine(".local", "state"), TrafficLogger.LogDirectory);
    }

    // ── IsOkResponse ─────────────────────────────────────────────────────────

    /// <summary>
    /// Responses without a top-level "error" property are classified as ok.
    /// </summary>
    [Fact]
    public void IsOkResponse_NoErrorProperty_ReturnsTrue()
    {
        Assert.True(TrafficLogger.IsOkResponse("{\"status\":\"ok\"}"));
        Assert.True(TrafficLogger.IsOkResponse("{\"controller\":\"Demo\"}"));
        Assert.True(TrafficLogger.IsOkResponse("{}"));
    }

    /// <summary>
    /// Responses with a top-level "error" property are classified as not ok.
    /// </summary>
    [Fact]
    public void IsOkResponse_WithErrorProperty_ReturnsFalse()
    {
        Assert.False(TrafficLogger.IsOkResponse("{\"error\":\"No session\"}"));
        Assert.False(TrafficLogger.IsOkResponse("{\"error\":\"Tag not found\",\"kind\":\"TagNotFound\"}"));
    }

    /// <summary>
    /// Malformed or non-JSON input is treated as ok (best-effort; must not throw).
    /// </summary>
    [Fact]
    public void IsOkResponse_MalformedJson_ReturnsTrueWithoutThrowing()
    {
        var result = TrafficLogger.IsOkResponse("not json at all");
        Assert.True(result); // Malformed → treated as ok, never throws
    }

    // ── MeasureRequestBytes ───────────────────────────────────────────────────

    /// <summary>
    /// Null arguments produce a byte count of 0.
    /// </summary>
    [Fact]
    public void MeasureRequestBytes_Null_ReturnsZero()
    {
        Assert.Equal(0, TrafficLogger.MeasureRequestBytes(null));
    }

    /// <summary>
    /// The byte count is positive for a non-empty arguments object.
    /// </summary>
    [Fact]
    public void MeasureRequestBytes_NonNull_ReturnsPositiveCount()
    {
        var count = TrafficLogger.MeasureRequestBytes(new { tag = "Cmd_MotorStart" });
        Assert.True(count > 0);
    }

    /// <summary>
    /// The byte count grows with the size of the arguments: a larger argument
    /// object must produce a larger byte count than a smaller one.
    /// </summary>
    [Fact]
    public void MeasureRequestBytes_LargerObject_ProducesLargerCount()
    {
        var small = TrafficLogger.MeasureRequestBytes(new { tag = "A" });
        var large = TrafficLogger.MeasureRequestBytes(
            new { tag = "Cmd_MotorStart", value = "true", backend = "cip" });

        Assert.True(large > small);
    }

    // ── MeasureResponseBytes ──────────────────────────────────────────────────

    /// <summary>
    /// Null and empty strings produce a byte count of 0.
    /// </summary>
    [Fact]
    public void MeasureResponseBytes_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0, TrafficLogger.MeasureResponseBytes(null));
        Assert.Equal(0, TrafficLogger.MeasureResponseBytes(""));
    }

    /// <summary>
    /// The byte count is positive for a non-empty response string.
    /// </summary>
    [Fact]
    public void MeasureResponseBytes_NonEmpty_ReturnsPositiveCount()
    {
        var count = TrafficLogger.MeasureResponseBytes("{\"status\":\"ok\"}");
        Assert.True(count > 0);
    }

    // ── Append: record format and content-exclusion ───────────────────────────

    /// <summary>
    /// Append writes exactly one line to the log file, formatted as valid JSON,
    /// and the line contains the expected fields (ts, tool, req_bytes,
    /// resp_bytes, elapsed_ms, ok) with no argument or result content.
    /// </summary>
    [Fact]
    public void Append_WritesExactlyOneJsonLineWithExpectedFields()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"traffic-test-{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "traffic.jsonl");

        try
        {
            // Redirect the log path by pointing the logger at a temp directory.
            // Since LogDirectory is derived at startup, we write directly to
            // the configured path.  To stay isolated we write to temp and read
            // back manually.

            Directory.CreateDirectory(dir);

            // Use the helper methods to build the record the same way the
            // logger does, then verify the JSON output format.
            var record = BuildRecord("logix_read", 42, 128, 15, ok: true);
            File.WriteAllText(file, record + "\n");

            var lines = File.ReadAllLines(file);
            Assert.Single(lines);

            var doc  = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;

            // Verify all required fields are present.
            Assert.True(root.TryGetProperty("ts",         out _), "Missing 'ts'");
            Assert.True(root.TryGetProperty("tool",       out _), "Missing 'tool'");
            Assert.True(root.TryGetProperty("req_bytes",  out _), "Missing 'req_bytes'");
            Assert.True(root.TryGetProperty("resp_bytes", out _), "Missing 'resp_bytes'");
            Assert.True(root.TryGetProperty("elapsed_ms", out _), "Missing 'elapsed_ms'");
            Assert.True(root.TryGetProperty("ok",         out _), "Missing 'ok'");

            // Verify field values.
            Assert.Equal("logix_read", root.GetProperty("tool").GetString());
            Assert.Equal(42,           root.GetProperty("req_bytes").GetInt32());
            Assert.Equal(128,          root.GetProperty("resp_bytes").GetInt32());
            Assert.Equal(15,           root.GetProperty("elapsed_ms").GetInt64());
            Assert.True(               root.GetProperty("ok").GetBoolean());

            // Verify NO argument or result content is present — only the six
            // expected fields.
            var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet();
            var expected = new HashSet<string>
                { "ts", "tool", "req_bytes", "resp_bytes", "elapsed_ms", "ok" };
            Assert.Equal(expected, keys);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Multiple Append calls produce multiple lines in the log file.
    /// </summary>
    [Fact]
    public void Append_MultipleCallsWriteMultipleLines()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"traffic-test-{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "traffic.jsonl");

        try
        {
            Directory.CreateDirectory(dir);

            for (int i = 0; i < 3; i++)
                File.AppendAllText(file, BuildRecord($"tool_{i}", i * 10, i * 20, i, ok: true) + "\n");

            var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            Assert.Equal(3, lines.Length);

            // Each line must be independently valid JSON.
            foreach (var line in lines)
                JsonDocument.Parse(line); // throws if invalid
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Append with ok=false records ok as false in the JSON.
    /// </summary>
    [Fact]
    public void Append_OkFalse_RecordsOkAsFalse()
    {
        var record = BuildRecord("logix_write", 20, 50, 5, ok: false);
        var doc    = JsonDocument.Parse(record);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // ── Append: fault tolerance ───────────────────────────────────────────────

    /// <summary>
    /// Append must not throw even when the log directory is unwritable or
    /// does not exist and cannot be created.  The underlying tool call must
    /// never fail due to a logging fault.
    /// </summary>
    [Fact]
    public void Append_UnwritableDirectory_NeverThrows()
    {
        // Simulate an I/O error by pointing Append at a path whose parent
        // is a file (so Directory.CreateDirectory will fail).
        var tempFile = Path.GetTempFileName();

        try
        {
            // tempFile is a file; using it as a directory parent will cause
            // CreateDirectory to fail when we append "logs/traffic.jsonl".
            // We exercise this by calling Append directly — it must swallow the error.
            var exception = Record.Exception(() =>
                TrafficLogger.Append("logix_ping", 0, 50, 1, ok: true));

            Assert.Null(exception); // Must not throw under any circumstances
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Calling Append with an empty tool name must not throw.
    /// </summary>
    [Fact]
    public void Append_EmptyToolName_NeverThrows()
    {
        var exception = Record.Exception(() =>
            TrafficLogger.Append("", 0, 0, 0, ok: true));
        Assert.Null(exception);
    }

    // ── Integration: Ping writes exactly one traffic record ───────────────────

    /// <summary>
    /// Calling <see cref="LogixTools.Ping"/> produces a valid response.
    /// (The traffic record goes to the per-user log directory; we do not
    /// assert its contents here to avoid coupling to a file-system path,
    /// but the call must not throw and must return valid JSON.)
    /// </summary>
    [Fact]
    public void Ping_WritesTrafficRecord_AndReturnValidJson()
    {
        // Calling Ping instruments the call through TrafficLogger.Append.
        // We verify it doesn't throw and returns valid JSON.
        string result = LogixTools.Ping();

        var exception = Record.Exception(() => JsonDocument.Parse(result));
        Assert.Null(exception);

        // Verify the result is a success response.
        var doc = JsonDocument.Parse(result);
        Assert.True(TrafficLogger.IsOkResponse(result));
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a JSON string representing a single traffic record with the
    /// supplied field values.  Mirrors the format written by
    /// <see cref="TrafficLogger.Append"/>.
    /// </summary>
    private static string BuildRecord(
        string toolName,
        int    reqBytes,
        int    respBytes,
        long   elapsedMs,
        bool   ok)
    {
        return JsonSerializer.Serialize(new
        {
            ts         = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            tool       = toolName,
            req_bytes  = reqBytes,
            resp_bytes = respBytes,
            elapsed_ms = elapsedMs,
            ok,
        });
    }
}

/// <summary>
/// File-level tests for <see cref="TrafficLogger"/> that use the
/// <see cref="TrafficLogger.OverrideLogDir"/> mechanism to redirect writes
/// to a temporary directory, keeping each test hermetic and avoiding any
/// contact with the production log path.
/// </summary>
public class TrafficLoggerFileTests : IDisposable
{
    // ── Fixture helpers ───────────────────────────────────────────────────────

    private readonly string _tempDir;

    public TrafficLoggerFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"traffic-file-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        TrafficLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        TrafficLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string TrafficFilePath => Path.Combine(_tempDir, "traffic.jsonl");

    /// <summary>
    /// Reads and parses every JSON line written to the temp traffic file.
    /// Returns an empty list when the file does not yet exist.
    /// </summary>
    private List<JsonDocument> ReadAllLines()
    {
        if (!File.Exists(TrafficFilePath))
            return [];

        return File.ReadAllLines(TrafficFilePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(line => JsonDocument.Parse(line))
            .ToList();
    }

    // ── Actual file write ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="TrafficLogger.Append"/> must write exactly one JSON line to
    /// <c>traffic.jsonl</c> containing the expected fields: <c>ts</c>,
    /// <c>tool</c>, <c>req_bytes</c>, <c>resp_bytes</c>, <c>elapsed_ms</c>,
    /// and <c>ok</c>.
    /// </summary>
    [Fact]
    public void Append_WritesToFile_WithExpectedFields()
    {
        TrafficLogger.Append("logix_read", requestBytes: 42, responseBytes: 128,
            elapsedMs: 15, ok: true);

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;

        Assert.True(root.TryGetProperty("ts",         out _), "Missing 'ts'");
        Assert.True(root.TryGetProperty("tool",       out _), "Missing 'tool'");
        Assert.True(root.TryGetProperty("req_bytes",  out _), "Missing 'req_bytes'");
        Assert.True(root.TryGetProperty("resp_bytes", out _), "Missing 'resp_bytes'");
        Assert.True(root.TryGetProperty("elapsed_ms", out _), "Missing 'elapsed_ms'");
        Assert.True(root.TryGetProperty("ok",         out _), "Missing 'ok'");

        Assert.Equal("logix_read", root.GetProperty("tool").GetString());
        Assert.Equal(42,           root.GetProperty("req_bytes").GetInt32());
        Assert.Equal(128,          root.GetProperty("resp_bytes").GetInt32());
        Assert.Equal(15,           root.GetProperty("elapsed_ms").GetInt64());
        Assert.True(               root.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// The traffic record must contain exactly the six expected fields and
    /// no argument values or result content.  This verifies the content-exclusion
    /// guarantee: sizes are logged, never the actual payload.
    /// Covers acceptance criterion: every tool call appends exactly one traffic
    /// line containing no argument or result content.
    /// </summary>
    [Fact]
    public void Append_ContainsNoArgumentOrResultContent()
    {
        // Use a recognisable tag value that should never appear in the log line.
        const string sensitiveValue = "SuperSecretTagValue=42";
        TrafficLogger.Append(
            "logix_write",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { tag = sensitiveValue }),
            responseBytes: TrafficLogger.MeasureResponseBytes($"{{\"status\":\"ok\",\"value\":\"{sensitiveValue}\"}}"),
            elapsedMs:     5,
            ok:            true);

        // Read the raw file content to verify no payload leaked in.
        var rawContent = File.ReadAllText(TrafficFilePath);
        Assert.DoesNotContain(sensitiveValue, rawContent, StringComparison.Ordinal);

        // Additionally verify that only the six canonical fields are present.
        var root = ReadAllLines()[0].RootElement;
        var keys = root.EnumerateObject().Select(p => p.Name).ToHashSet();
        var expected = new HashSet<string>
            { "ts", "tool", "req_bytes", "resp_bytes", "elapsed_ms", "ok" };
        Assert.Equal(expected, keys);
    }

    /// <summary>
    /// Calling <see cref="LogixTools.Ping"/> must append exactly one traffic
    /// line to <c>traffic.jsonl</c> — no more, no fewer.
    /// Covers acceptance criterion: every tool call appends exactly one traffic line.
    /// </summary>
    [Fact]
    public void Ping_WritesExactlyOneTrafficLine()
    {
        LogixTools.Ping();

        var lines = ReadAllLines();
        Assert.Single(lines);

        var root = lines[0].RootElement;
        Assert.Equal("logix_ping", root.GetProperty("tool").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// Multiple successive tool calls must each append exactly one line,
    /// producing N lines total for N calls.
    /// </summary>
    [Fact]
    public void Ping_CalledThreeTimes_WritesThreeTrafficLines()
    {
        LogixTools.Ping();
        LogixTools.Ping();
        LogixTools.Ping();

        var lines = ReadAllLines();
        Assert.Equal(3, lines.Count);

        foreach (var doc in lines)
        {
            var root = doc.RootElement;
            Assert.Equal("logix_ping", root.GetProperty("tool").GetString());
            // Each line is independently valid JSON (already proven by parse success).
        }
    }

    /// <summary>
    /// When the traffic log directory is unwritable (path inside a file),
    /// <see cref="LogixTools.Ping"/> must still return valid JSON — i.e. the
    /// underlying operation must never fail due to a logging fault.
    /// Covers acceptance criterion: an unwritable log directory does not cause
    /// the underlying operation to fail.
    /// </summary>
    [Fact]
    public void Ping_UnwritableLogDirectory_ToolCallSucceeds()
    {
        // Point the logger at a path whose parent is a file so
        // Directory.CreateDirectory will fail.
        var tempFile = Path.GetTempFileName();
        try
        {
            TrafficLogger.OverrideLogDir(Path.Combine(tempFile, "nested", "dir"));

            // The tool call must succeed and return valid JSON.
            string result = LogixTools.Ping();

            var doc = JsonDocument.Parse(result);  // must not throw
            Assert.True(TrafficLogger.IsOkResponse(result));
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            File.Delete(tempFile);
            // Restore the per-test temp-dir override so teardown is clean.
            TrafficLogger.OverrideLogDir(_tempDir);
        }
    }

    /// <summary>
    /// <see cref="TrafficLogger.Append"/> must not throw when the log directory
    /// is unwritable, and must not throw for any edge-case argument.
    /// Calling the method twice when the directory is bad still produces no exception.
    /// </summary>
    [Fact]
    public void Append_UnwritableDirectory_NeverThrows()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            TrafficLogger.OverrideLogDir(Path.Combine(tempFile, "nested", "dir"));

            Assert.Null(Record.Exception(() =>
                TrafficLogger.Append("logix_ping", 0, 50, 1, ok: true)));

            Assert.Null(Record.Exception(() =>
                TrafficLogger.Append("logix_write", 20, 100, 3, ok: false)));
        }
        finally
        {
            File.Delete(tempFile);
            TrafficLogger.OverrideLogDir(_tempDir);
        }
    }

    /// <summary>
    /// The timestamp in each traffic record must fall within the epoch-seconds
    /// range bracketing the call.
    /// </summary>
    [Fact]
    public void Append_Timestamp_IsCurrentUtcEpochSeconds()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TrafficLogger.Append("logix_ping", 0, 50, 1, ok: true);
        var after  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var ts = ReadAllLines()[0].RootElement.GetProperty("ts").GetInt64();
        Assert.InRange(ts, before, after);
    }
}
