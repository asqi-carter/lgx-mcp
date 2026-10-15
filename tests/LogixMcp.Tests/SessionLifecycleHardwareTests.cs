// ── SessionLifecycleHardwareTests — process-level SDK session close ─────
//
// Spawns the real published LogixMcp.exe over stdio and checks that it closes its SDK project on
// client exit and on reconnect, against a live FactoryTalk Logix Echo controller. A project the
// server abandoned was reaped by LdSdkServer's keep-alive thread, whose unguarded temp-.acd delete
// crashed the service. The unit tests (SdkServerFaultedTests) cover the code paths; these cover the
// real process + real SDK end to end.
//
// Opt-in, bench only: skipped unless LGX_HW_SESSION=1 on Windows, so `make test` is unchanged.
//   LGX_HW_SESSION_EXE       published server (default C:\LogixMcp-next\LogixMcp.exe)
//   LGX_HW_SESSION_COMMPATH  SDK comm path (default EmulateEthernet\127.0.0.1)
//   LGX_HW_SESSION_AUDIT     audit log (default %LOCALAPPDATA%\logix-mcp\logs\audit.jsonl)
//   LGX_HW_SESSION_SOAK=N    run the soak test with N connect → reconnect → exit cycles
// Run on the bench (Echo controller up, nothing else using the SDK):
//   set LGX_HW_SESSION=1 && dotnet test --filter FullyQualifiedName~SessionLifecycle
//
// Limits: the crash itself needs a slow next upload, so it cannot be forced on demand. These tests
// check the mechanism (sessions are closed, not abandoned), not crash absence; the A/B (16 → 0
// crashes) covers that. The Kill test is the control: it proves the others do not pass for free.
// ──────────────────────────────────────────────────────────────────────────────────────────────
using System.Diagnostics;
using System.Text.Json.Nodes;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Process-level checks that the published server closes its SDK session on exit and on reconnect
/// Bench only: every test skips unless <c>LGX_HW_SESSION=1</c> on Windows.
/// </summary>
[Collection("LogixAcd")]
[Trait("Category", "Integration")]
public class SessionLifecycleHardwareTests
{
    private static readonly string Exe =
        Environment.GetEnvironmentVariable("LGX_HW_SESSION_EXE") ?? @"C:\LogixMcp-next\LogixMcp.exe";
    private static readonly string CommPath =
        Environment.GetEnvironmentVariable("LGX_HW_SESSION_COMMPATH") ?? @"EmulateEthernet\127.0.0.1";
    private static readonly string AuditPath =
        Environment.GetEnvironmentVariable("LGX_HW_SESSION_AUDIT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "logix-mcp", "logs", "audit.jsonl");

    /// <summary>The SDK's per-project engine process: one per open SDK project.</summary>
    private const string EngineProcess = "RSLogix5000Services";

    private static void SkipUnlessBench()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("LGX_HW_SESSION") == "1",
            "opt-in: set LGX_HW_SESSION=1 on a Windows machine with an Echo controller running");
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows bench only");
        Skip.IfNot(File.Exists(Exe), $"published server not found: {Exe} (set LGX_HW_SESSION_EXE)");
    }

    // ── 1. Exit closes ───────────────────────────────────────────────────────

    /// <summary>Closing stdin after an online connect makes the server close its SDK project and exit.</summary>
    [SkippableFact]
    public async Task ExitClosesSdkSession()
    {
        SkipUnlessBench();
        var baseline = await WaitForEngineBaselineAsync();
        var t0 = DateTime.Now;
        var audit = AuditCursor.AtEnd();

        await using var server = await McpServer.StartAsync();
        await server.ConnectOnlineAsync();
        Assert.Equal(1, audit.Read().Count(e => e.Event == "connect"));

        var exited = await server.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(exited, "server did not exit within 10 s of stdin EOF");

        Assert.Contains(audit.Read(), e => e.Event == "disconnect");
        Assert.True(await WaitForEngineCountAsync(baseline, TimeSpan.FromSeconds(45)),
            $"{EngineProcess} count did not return to {baseline} within 45 s (now {EngineCount()})");
        Assert.Equal(0, KeepAliveCrashEventsSince(t0));
    }

    // ── 2. Reconnect closes the outgoing session ────────────────────────────

    /// <summary>A second connect in the same process closes the first SDK project before uploading.</summary>
    [SkippableFact]
    public async Task ReconnectClosesOutgoingSession()
    {
        SkipUnlessBench();
        var baseline = await WaitForEngineBaselineAsync();
        var t0 = DateTime.Now;
        var audit = AuditCursor.AtEnd();

        await using var server = await McpServer.StartAsync();
        await server.ConnectOnlineAsync();
        await server.ConnectOnlineAsync();

        // connect, disconnect (the outgoing session), connect: the close comes before the new session.
        var seq = audit.Read().Select(e => e.Event).Where(e => e is "connect" or "disconnect").ToList();
        Assert.Equal(new[] { "connect", "disconnect", "connect" }, seq);

        // One live SDK project: one engine over the baseline once the closed one's engine has exited.
        // This is the check that catches the pre-fix build (c27e659): it logged the disconnect above too
        // (session replace), but left the outgoing project open, so two engines stayed up.
        Assert.True(await WaitForEngineCountAsync(baseline + 1, TimeSpan.FromSeconds(45)),
            $"expected {baseline + 1} {EngineProcess} after reconnect, now {EngineCount()}");

        Assert.True(await server.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(10)), "server did not exit");
        Assert.True(await WaitForEngineCountAsync(baseline, TimeSpan.FromSeconds(45)), "engines not released after exit");
        Assert.Equal(0, KeepAliveCrashEventsSince(t0));
    }

    // ── 3. Kill does not (control) ──────────────────────────────────────────

    /// <summary>
    /// Control: a hard-killed server cannot close its session, so no disconnect is logged. Proves the
    /// disconnect in the other tests comes from the close path and is not logged for free. The
    /// abandoned project is left to LdSdkServer's keep-alive reaper; the test waits for it.
    /// </summary>
    [SkippableFact]
    public async Task KillLeavesSessionToReaper()
    {
        SkipUnlessBench();
        var baseline = await WaitForEngineBaselineAsync();
        var audit = AuditCursor.AtEnd();

        await using var server = await McpServer.StartAsync();
        await server.ConnectOnlineAsync();
        server.Kill();

        Assert.DoesNotContain(audit.Read(), e => e.Event == "disconnect");
        // Leave the bench clean for the next test: the reaper releases the engine (~25 s on the bench).
        await WaitForEngineCountAsync(baseline, TimeSpan.FromSeconds(90));
    }

    // ── 4. Soak (optional) ──────────────────────────────────────────────────

    /// <summary>N cycles of connect → reconnect → exit: every connect closed, no crash, no leaked temp ACDs.</summary>
    [SkippableFact]
    public async Task SoakEveryConnectIsClosed()
    {
        SkipUnlessBench();
        Skip.IfNot(int.TryParse(Environment.GetEnvironmentVariable("LGX_HW_SESSION_SOAK"), out var n) && n > 0,
            "set LGX_HW_SESSION_SOAK=N to run N soak cycles");
        var baseline = await WaitForEngineBaselineAsync();
        var t0 = DateTime.Now;
        var audit = AuditCursor.AtEnd();
        var tmpBefore = LeakedTempAcdCount();

        for (var i = 0; i < n; i++)
        {
            await using var server = await McpServer.StartAsync();
            await server.ConnectOnlineAsync();
            await server.ConnectOnlineAsync();
            Assert.True(await server.CloseStdinAndWaitAsync(TimeSpan.FromSeconds(10)), $"cycle {i + 1}: server did not exit");
        }
        await WaitForEngineCountAsync(baseline, TimeSpan.FromSeconds(45));

        var events = audit.Read();
        Assert.Equal(events.Count(e => e.Event == "connect"), events.Count(e => e.Event == "disconnect"));
        Assert.Equal(2 * n, events.Count(e => e.Event == "connect"));
        Assert.Equal(0, KeepAliveCrashEventsSince(t0));
        Assert.True(LeakedTempAcdCount() <= tmpBefore, $"tmp*.acd grew from {tmpBefore} to {LeakedTempAcdCount()}");
    }

    // ── Bench probes ────────────────────────────────────────────────────────

    private static int EngineCount() => Process.GetProcessesByName(EngineProcess).Length;

    private static async Task<bool> WaitForEngineCountAsync(int expected, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (EngineCount() == expected) return true;
            await Task.Delay(1000);
        }
        return EngineCount() == expected;
    }

    /// <summary>Waits (up to 90 s) for engines left by an earlier run to be released; returns the settled count.</summary>
    private static async Task<int> WaitForEngineBaselineAsync()
    {
        await WaitForEngineCountAsync(0, TimeSpan.FromSeconds(90));
        return EngineCount();
    }

    /// <summary>LdSdkServer crash events (Application 1026 with the keep-alive reaper in the stack) since <paramref name="since"/>.</summary>
    private static int KeepAliveCrashEventsSince(DateTime since)
    {
        var script = "@(Get-WinEvent -FilterHashtable @{LogName='Application';Id=1026;StartTime=[datetime]'"
                   + since.ToString("yyyy-MM-ddTHH:mm:ss") + "'} -ErrorAction SilentlyContinue | "
                   + "Where-Object { $_.Message -match 'KeepAliveCleanup' }).Count";
        var psi = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.TryParse(output.Trim(), out var n) ? n : throw new InvalidOperationException($"event query failed: '{output}'");
    }

    /// <summary>Temp ACDs the SDK service leaves behind when its reaper fails to delete them.</summary>
    private static int LeakedTempAcdCount()
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            "ServiceProfiles", "LocalService", "AppData", "Local", "Temp");
        try { return Directory.GetFiles(dir, "tmp*.acd").Length; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return 0; }
    }

    // ── Audit log ───────────────────────────────────────────────────────────

    private sealed record AuditEvent(string Event, string Ts);

    /// <summary>Reads audit events appended after the cursor was taken.</summary>
    private sealed class AuditCursor
    {
        private readonly long _offset;
        private AuditCursor(long offset) => _offset = offset;

        public static AuditCursor AtEnd() => new(File.Exists(AuditPath) ? new FileInfo(AuditPath).Length : 0);

        public List<AuditEvent> Read()
        {
            if (!File.Exists(AuditPath)) return [];
            using var fs = new FileStream(AuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) return []; // rotated under us
            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var events = new List<AuditEvent>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var node = JsonNode.Parse(line);
                events.Add(new AuditEvent(node?["event"]?.GetValue<string>() ?? "", node?["ts"]?.ToString() ?? ""));
            }
            return events;
        }
    }

    // ── Minimal MCP stdio client ────────────────────────────────────────────

    /// <summary>
    /// Just enough MCP over stdio (newline-delimited JSON-RPC) to initialize, call logix_connect and
    /// poll logix_connect_status. Kept hand-rolled so the test drives exactly what a client sends.
    /// </summary>
    private sealed class McpServer : IAsyncDisposable
    {
        private readonly Process _p;
        private int _nextId;

        private McpServer(Process p) => _p = p;

        public static async Task<McpServer> StartAsync()
        {
            var psi = new ProcessStartInfo(Exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.Environment.Remove("LGX_ENABLE_LIVE_WRITES"); // read-only session: this test never writes
            // dotnet test hands its children DOTNET_ROOT (x64) and MSBuild/VSTest variables. The SDK's
            // components are 32-bit and load the wrong hostfxr under an x64 DOTNET_ROOT, so the upload
            // times out. Start the server with the environment a real MCP client would give it.
            foreach (var key in psi.Environment.Keys.Where(k =>
                         k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith("VSTEST_", StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase) ||
                         k.StartsWith("_MSBUILD", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(key);
            var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {Exe}");
            p.ErrorDataReceived += (_, _) => { }; // drain stderr so the server never blocks on it
            p.BeginErrorReadLine();
            var s = new McpServer(p);
            await s.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "session-lifecycle-test", ["version"] = "1" },
            });
            await s.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            return s;
        }

        /// <summary>Connects online by upload and waits until the background connect reports connected.</summary>
        public async Task ConnectOnlineAsync()
        {
            var r = await CallToolAsync("logix_connect", new JsonObject
            {
                ["backend"] = "sdk", ["sdkMode"] = "online", ["sdkCommPath"] = CommPath,
            });
            var sw = Stopwatch.StartNew();
            while (r["status"]?.GetValue<string>() == "connecting")
            {
                if (sw.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("connect still running after 3 min");
                await Task.Delay(2000);
                r = await CallToolAsync("logix_connect_status", new JsonObject());
            }
            Assert.True(r["status"]?.GetValue<string>() == "connected", $"connect did not succeed: {r.ToJsonString()}");
        }

        public async Task<bool> CloseStdinAndWaitAsync(TimeSpan timeout)
        {
            _p.StandardInput.Close();
            using var cts = new CancellationTokenSource(timeout);
            try { await _p.WaitForExitAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return false; }
        }

        public void Kill()
        {
            _p.Kill(entireProcessTree: true);
            _p.WaitForExit(10_000);
        }

        private async Task<JsonObject> CallToolAsync(string name, JsonObject args)
        {
            var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args });
            var text = result["content"]?[0]?["text"]?.GetValue<string>() ?? "{}";
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["raw"] = text };
        }

        private async Task<JsonNode> RequestAsync(string method, JsonObject @params)
        {
            var id = Interlocked.Increment(ref _nextId);
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                var line = await _p.StandardOutput.ReadLineAsync(cts.Token)
                    ?? throw new InvalidOperationException($"server closed stdout during {method}");
                if (string.IsNullOrWhiteSpace(line)) continue;
                var msg = JsonNode.Parse(line);
                if (msg?["id"] is null || msg["id"]!.GetValue<int>() != id) continue; // notifications, logs
                if (msg["error"] is { } err) throw new InvalidOperationException($"{method}: {err.ToJsonString()}");
                return msg["result"] ?? new JsonObject();
            }
        }

        private async Task SendAsync(JsonObject msg)
        {
            await _p.StandardInput.WriteLineAsync(msg.ToJsonString());
            await _p.StandardInput.FlushAsync();
        }

        public ValueTask DisposeAsync()
        {
            if (!_p.HasExited)
            {
                try { _p.StandardInput.Close(); } catch (IOException) { }
                if (!_p.WaitForExit(15_000)) _p.Kill(entireProcessTree: true);
            }
            _p.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
