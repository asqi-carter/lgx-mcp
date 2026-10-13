using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Write-path tests for <c>logix_controller_mode</c>.
/// Compiled in both SDK and non-SDK builds (partial of the SDK-only
/// annotation tests in ControllerModeTests.cs).
/// </summary>
public partial class ControllerModeTests
{
    private static (TagSessionService session, ControllerModeService modeSvc) WriteSession(bool isVirtual)
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(), isVirtual: isVirtual);
        var modeSvc = new ControllerModeService();
        modeSvc.Reset(isVirtual ? "Program" : "Unknown");
        return (session, modeSvc);
    }

    /// <summary>A confirmed write on a live session must not claim success.</summary>
    [Fact]
    public async Task LiveSession_Write_ReturnsModeChangeNotApplied()
    {
        var (session, modeSvc) = WriteSession(isVirtual: false);

        var result = await ControllerModeHandler.ExecuteAsync("Run", confirm: true, session, modeSvc);

        var err = Assert.IsType<ControllerModeError>(result);
        Assert.Equal("ModeChangeNotApplied", err.Kind);
        Assert.Contains("Studio 5000", err.Error);
        Assert.Contains("keyswitch", err.Error, StringComparison.OrdinalIgnoreCase);
        // Nothing was recorded as the mode.
        Assert.Equal("Unknown", modeSvc.ControllerMode);
        Assert.Equal("Unknown", modeSvc.RequestedMode);

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("ModeChangeNotApplied", doc.RootElement.GetProperty("kind").GetString());
        Assert.False(doc.RootElement.TryGetProperty("status", out _));
    }

    /// <summary>Virtual writes stay in-memory and say so.</summary>
    [Fact]
    public async Task VirtualSession_Write_ReportsAppliedToControllerFalse()
    {
        var (session, modeSvc) = WriteSession(isVirtual: true);

        var result = await ControllerModeHandler.ExecuteAsync("Run", confirm: false, session, modeSvc);

        var ok = Assert.IsType<ControllerModeResult>(result);
        Assert.Equal("ok", ok.Status);
        Assert.Equal("Run", ok.RequestedControllerMode);
        Assert.True(ok.Virtual);
        Assert.False(ok.AppliedToController);
        Assert.Equal("Run", modeSvc.RequestedMode);

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(doc.RootElement.GetProperty("appliedToController").GetBoolean());
    }

    /// <summary>Every non-refused write attempt emits a controller_mode audit event.</summary>
    [Fact]
    public async Task Write_IsAudited()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"controller-mode-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var (vSession, vMode) = WriteSession(isVirtual: true);
            await ControllerModeHandler.ExecuteAsync("Test", confirm: false, vSession, vMode);

            var (lSession, lMode) = WriteSession(isVirtual: false);
            await ControllerModeHandler.ExecuteAsync("Program", confirm: true, lSession, lMode);

            // Refused (no confirm) must NOT be audited as controller_mode.
            await ControllerModeHandler.ExecuteAsync("Run", confirm: false, lSession, lMode);

            var events = File.ReadAllLines(Path.Combine(tempDir, "audit.jsonl"))
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "controller_mode")
                .ToList();

            Assert.Equal(2, events.Count);

            Assert.Equal("Test", events[0].GetProperty("requested_mode").GetString());
            Assert.True(events[0].GetProperty("virtual").GetBoolean());
            Assert.Equal("recorded_virtual", events[0].GetProperty("outcome").GetString());

            Assert.Equal("Program", events[1].GetProperty("requested_mode").GetString());
            Assert.False(events[1].GetProperty("virtual").GetBoolean());
            Assert.Equal("ModeChangeNotApplied", events[1].GetProperty("outcome").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
