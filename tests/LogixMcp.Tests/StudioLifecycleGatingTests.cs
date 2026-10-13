#if LOGIX_SDK
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Accept/test/untest/assemble need <c>LGX_ENABLE_LIVE_WRITES</c> in addition to
/// <c>LGX_ENABLE_STUDIO_AUTOMATION</c>; cancel keeps needing only Studio automation.
/// </summary>
public class StudioLifecycleGatingTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";

    private static LogixSdkTools Tools(bool liveWrites, bool studio) =>
        new(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: liveWrites, StudioAutomationEnabled: studio));

    [Fact]
    public async Task AcceptTestUntestAssemble_RefuseWithoutLiveWrites()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lifecycle-gating-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var tools = Tools(liveWrites: false, studio: true);
            var runnerCalls = 0;
            Func<string, CancellationToken, Task<Session1ScriptResult>> runner = (v, c) =>
            {
                runnerCalls++;
                return Task.FromResult(new Session1ScriptResult(true, "x", Array.Empty<string>()));
            };
            var studioProbeCalls = 0;
            Func<bool> studioProbe = () => { studioProbeCalls++; return true; };

            var cases = new (string Tool, Func<Task<string>> Call)[]
            {
                ("logix_accept_pending_edits", () => tools.GetAcceptPendingEditsResponseAsync(
                    Program, Routine, true, null, runner, studioProbe)),
                ("logix_test_pending_edits", () => tools.GetTestPendingEditsResponseAsync(
                    Program, Routine, true, null, runner, studioProbe)),
                ("logix_untest_pending_edits", () => tools.GetUntestPendingEditsResponseAsync(
                    Program, Routine, true, null, runner, studioProbe)),
                ("logix_assemble_pending_edits", () => tools.GetAssemblePendingEditsResponseAsync(
                    Program, Routine, true, null, runner, studioProbe)),
            };

            foreach (var (tool, call) in cases)
            {
                var root = JsonDocument.Parse(await call()).RootElement;
                Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
                Assert.Contains("LGX_ENABLE_LIVE_WRITES", root.GetProperty("error").GetString());
                Assert.Contains("LGX_ENABLE_LIVE_WRITES", root.GetProperty("hint").GetString());
                Assert.Contains(tool, root.GetProperty("error").GetString());
            }

            Assert.Equal(0, runnerCalls);
            Assert.Equal(0, studioProbeCalls);

            var refusals = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "capability_refusal")
                .ToList();
            foreach (var (tool, _) in cases)
                Assert.Contains(refusals, e =>
                    e.GetProperty("operation").GetString() == tool &&
                    e.GetProperty("env_var").GetString() == Capabilities.EnvVar);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Cancel_StillNeedsOnlyStudioAutomation()
    {
        var tools = Tools(liveWrites: false, studio: true);

        var json = await tools.GetCancelPendingEditsResponseAsync(Program, Routine, confirm: true);

        // Passes the capability gate; stops later at "no active session", not CapabilityDisabled.
        var root = JsonDocument.Parse(json).RootElement;
        Assert.NotEqual("CapabilityDisabled", root.GetProperty("kind").GetString());
        Assert.Contains("No active SDK session", root.GetProperty("error").GetString());
    }
}
#endif
