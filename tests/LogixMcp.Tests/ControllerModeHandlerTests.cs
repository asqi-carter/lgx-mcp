using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="ControllerModeHandler"/>,
/// <see cref="ControllerModeService"/>, and the <c>logix_controller_mode</c>
/// MCP tool layer (<see cref="LogixTagTools.ControllerMode"/>).
///
/// All tests use an in-memory <see cref="VirtualTagService"/> so no real PLC
/// connection is required.
/// </summary>
public class ControllerModeHandlerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Builds a virtual <see cref="TagSessionService"/> (isVirtual=true)
    /// together with a fresh <see cref="ControllerModeService"/> already reset
    /// to "Program".
    /// </summary>
    private static (TagSessionService session, ControllerModeService modeSvc)
        BuildVirtualSession()
    {
        var inner   = new VirtualTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var modeSvc = new ControllerModeService();
        modeSvc.Reset("Program");

        return (session, modeSvc);
    }

    /// <summary>
    /// Builds a non-virtual <see cref="TagSessionService"/> (isVirtual=false)
    /// together with a fresh <see cref="ControllerModeService"/> reset to
    /// "Unknown" (as a live session would start).
    /// </summary>
    private static (TagSessionService session, ControllerModeService modeSvc)
        BuildLiveSession()
    {
        var inner   = new VirtualTagService();   // stand-in for live service
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: false);

        var modeSvc = new ControllerModeService();
        modeSvc.Reset("Unknown");

        return (session, modeSvc);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeService
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ControllerModeService_InitialState_IsUnknown()
    {
        var svc = new ControllerModeService();

        Assert.Equal("Unknown", svc.ControllerMode);
        Assert.Equal("Unknown", svc.RequestedMode);
    }

    [Fact]
    public void ControllerModeService_Reset_SetsControllerAndRequestedMode()
    {
        var svc = new ControllerModeService();

        svc.Reset("Program");

        Assert.Equal("Program", svc.ControllerMode);
        Assert.Equal("Program", svc.RequestedMode);
    }

    [Fact]
    public void ControllerModeService_SetRequestedMode_UpdatesBothProperties()
    {
        var svc = new ControllerModeService();
        svc.Reset("Program");

        svc.SetRequestedMode("Run");

        Assert.Equal("Run", svc.ControllerMode);
        Assert.Equal("Run", svc.RequestedMode);
    }

    [Theory]
    [InlineData("Program")]
    [InlineData("Run")]
    [InlineData("Test")]
    public void ControllerModeService_Reset_AcceptsAllValidModes(string mode)
    {
        var svc = new ControllerModeService();
        svc.Reset(mode);

        Assert.Equal(mode, svc.ControllerMode);
        Assert.Equal(mode, svc.RequestedMode);
    }

    [Fact]
    public void ControllerModeService_Reset_Null_ThrowsArgumentNullException()
    {
        var svc = new ControllerModeService();

        Assert.Throws<ArgumentNullException>(() => svc.Reset(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ControllerModeService_Reset_WhitespaceOrEmpty_ThrowsArgumentException(
        string badValue)
    {
        var svc = new ControllerModeService();

        Assert.Throws<ArgumentException>(() => svc.Reset(badValue));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeHandler — ValidModes set
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Program")]
    [InlineData("Run")]
    [InlineData("Test")]
    [InlineData("program")]
    [InlineData("RUN")]
    [InlineData("tEsT")]
    public void ControllerModeHandler_ValidModes_ContainsExpectedValues(string mode)
    {
        Assert.True(ControllerModeHandler.ValidModes.Contains(mode));
    }

    [Theory]
    [InlineData("Faulted")]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("program mode")]
    public void ControllerModeHandler_ValidModes_DoesNotContainInvalidValues(string mode)
    {
        Assert.False(ControllerModeHandler.ValidModes.Contains(mode));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeHandler.ExecuteAsync — read path
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_NoSession_ReturnsError()
    {
        var session = new TagSessionService();   // not connected
        var modeSvc = new ControllerModeService();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeError>(result);
        var err = (ControllerModeError) result;
        Assert.Contains("No active session", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ReadOnly_NullMode_ReturnsCurrentMode()
    {
        var (session, modeSvc) = BuildVirtualSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeResult>(result);
        var ok = (ControllerModeResult) result;
        Assert.Equal("ok", ok.Status);
        Assert.Equal("Program", ok.ControllerMode);
        Assert.Equal("Program", ok.RequestedControllerMode);
        Assert.True(ok.Virtual);
    }

    [Fact]
    public async Task Execute_ReadOnly_ReturnsVirtualFlagTrue_ForVirtualSession()
    {
        var (session, modeSvc) = BuildVirtualSession();

        var result = (ControllerModeResult) await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modeSvc);

        Assert.True(result.Virtual);
    }

    [Fact]
    public async Task Execute_ReadOnly_ReturnsVirtualFlagFalse_ForLiveSession()
    {
        var (session, modeSvc) = BuildLiveSession();

        var result = (ControllerModeResult) await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false, session, modeSvc);

        Assert.False(result.Virtual);
    }

    [Fact]
    public async Task Execute_ReadOnly_DoesNotChangeMode()
    {
        var (session, modeSvc) = BuildVirtualSession();
        Assert.Equal("Program", modeSvc.ControllerMode);

        await ControllerModeHandler.ExecuteAsync(mode: null, confirm: false, session, modeSvc);

        Assert.Equal("Program", modeSvc.ControllerMode);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeHandler.ExecuteAsync — write path, virtual session
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Program")]
    [InlineData("Run")]
    [InlineData("Test")]
    public async Task Execute_VirtualSession_SetValidMode_ReturnsOk(string mode)
    {
        var (session, modeSvc) = BuildVirtualSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode, confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeResult>(result);
        var ok = (ControllerModeResult) result;
        Assert.Equal("ok", ok.Status);
        Assert.Equal(mode, ok.ControllerMode);
        Assert.Equal(mode, ok.RequestedControllerMode);
    }

    [Fact]
    public async Task Execute_VirtualSession_SetMode_UpdatesModeService()
    {
        var (session, modeSvc) = BuildVirtualSession();

        await ControllerModeHandler.ExecuteAsync("Run", confirm: false, session, modeSvc);

        Assert.Equal("Run", modeSvc.ControllerMode);
        Assert.Equal("Run", modeSvc.RequestedMode);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("RUN")]
    [InlineData("tEsT")]
    public async Task Execute_VirtualSession_SetMode_CaseInsensitive_ReturnsOk(string mode)
    {
        var (session, modeSvc) = BuildVirtualSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode, confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeResult>(result);
    }

    [Fact]
    public async Task Execute_VirtualSession_SetMode_NoConfirmRequired()
    {
        // Virtual sessions should not require confirm=true.
        var (session, modeSvc) = BuildVirtualSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            "Program", confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeResult>(result);
        Assert.Equal("ok", ((ControllerModeResult) result).Status);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeHandler.ExecuteAsync — write path, live (non-virtual) session
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_LiveSession_SetMode_WithoutConfirm_ReturnsError()
    {
        var (session, modeSvc) = BuildLiveSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            "Program", confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeError>(result);
        var err = (ControllerModeError) result;
        Assert.Contains("physical controller", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirm=true", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_LiveSession_SetMode_WithoutConfirm_DoesNotChangeMode()
    {
        var (session, modeSvc) = BuildLiveSession();
        Assert.Equal("Unknown", modeSvc.ControllerMode);

        await ControllerModeHandler.ExecuteAsync(
            "Program", confirm: false, session, modeSvc);

        // Mode must remain unchanged after a refused request.
        Assert.Equal("Unknown", modeSvc.ControllerMode);
    }

    [Theory]
    [InlineData("Program")]
    [InlineData("Run")]
    [InlineData("Test")]
    public async Task Execute_LiveSession_SetMode_WithConfirm_ReturnsModeChangeNotApplied(string mode)
    {
        var (session, modeSvc) = BuildLiveSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode, confirm: true, session, modeSvc);

        var err = Assert.IsType<ControllerModeError>(result);
        Assert.Equal("ModeChangeNotApplied", err.Kind);
    }

    [Fact]
    public async Task Execute_LiveSession_SetMode_WithConfirm_DoesNotUpdateModeService()
    {
        var (session, modeSvc) = BuildLiveSession();

        await ControllerModeHandler.ExecuteAsync("Run", confirm: true, session, modeSvc);

        Assert.Equal("Unknown", modeSvc.ControllerMode);
        Assert.Equal("Unknown", modeSvc.RequestedMode);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerModeHandler.ExecuteAsync — invalid mode string
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Faulted")]
    [InlineData("Unknown")]
    [InlineData("HALT")]
    [InlineData("")]
    [InlineData("program mode")]
    public async Task Execute_InvalidMode_ReturnsError(string mode)
    {
        var (session, modeSvc) = BuildVirtualSession();

        var result = await ControllerModeHandler.ExecuteAsync(
            mode, confirm: false, session, modeSvc);

        Assert.IsType<ControllerModeError>(result);
        var err = (ControllerModeError) result;
        Assert.Contains("Invalid mode", err.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Program", err.Error);
        Assert.Contains("Run",     err.Error);
        Assert.Contains("Test",    err.Error);
    }

    [Fact]
    public async Task Execute_InvalidMode_DoesNotChangeMode()
    {
        var (session, modeSvc) = BuildVirtualSession();

        await ControllerModeHandler.ExecuteAsync("Faulted", confirm: false, session, modeSvc);

        Assert.Equal("Program", modeSvc.ControllerMode);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConnectHandler — resets ControllerModeService
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectHandler_VirtualMode_ResetsModeServiceToProgram()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();
        var modeSvc    = new ControllerModeService();

        // Pre-seed mode service with a non-default value.
        modeSvc.Reset("Run");

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true,
            projectSvc, sessionSvc, modeService: modeSvc);

        Assert.Equal("Program", modeSvc.ControllerMode);
        Assert.Equal("Program", modeSvc.RequestedMode);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_ResetsModeServiceToUnknown()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();
        var modeSvc    = new ControllerModeService();
        modeSvc.Reset("Run");   // pre-seed

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            projectSvc, sessionSvc, backend: "l5x", modeService: modeSvc);

        Assert.Equal("Unknown", modeSvc.ControllerMode);
    }

    [Fact]
    public async Task ConnectHandler_NullModeService_DoesNotThrow()
    {
        // Existing callers that do not supply modeService must still work.
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        Assert.IsType<ConnectResult>(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LogixTagTools — MCP-layer serialisation
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LogixTagTools_ControllerMode_Read_VirtualSession_ReturnsValidJson()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();
        var modeSvc    = new ControllerModeService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, modeSvc);

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetControllerModeResponseAsync(mode: null, confirm: false);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Program", doc.RootElement.GetProperty("controllerMode").GetString());
        Assert.True(doc.RootElement.GetProperty("virtual").GetBoolean());
    }

    [Fact]
    public async Task LogixTagTools_ControllerMode_Set_VirtualSession_ReturnsOkJson()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();
        var modeSvc    = new ControllerModeService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, modeSvc);

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetControllerModeResponseAsync("Run", confirm: false);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Run", doc.RootElement.GetProperty("controllerMode").GetString());
        Assert.Equal("Run", doc.RootElement.GetProperty("requestedControllerMode").GetString());
    }

    [Fact]
    public async Task LogixTagTools_ControllerMode_NoSession_ReturnsErrorJson()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();   // not connected
        var modeSvc    = new ControllerModeService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, modeSvc);

        var json = await tools.GetControllerModeResponseAsync(mode: null, confirm: false);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task LogixTagTools_ControllerMode_LiveSessionWithoutConfirm_ReturnsErrorJson()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        // Attach a non-virtual session manually.
        sessionSvc.Attach(new VirtualTagService(), isVirtual: false);
        var modeSvc = new ControllerModeService();
        modeSvc.Reset("Unknown");
        var tools = new LogixTagTools(projectSvc, sessionSvc, modeSvc);

        var json = await tools.GetControllerModeResponseAsync("Program", confirm: false);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task LogixTagTools_Connect_VirtualMode_ResetsModeToProgram()
    {
        var projectSvc = new ProjectService();
        projectSvc.Load(FixturePath);
        var sessionSvc = new TagSessionService();
        var modeSvc    = new ControllerModeService();
        modeSvc.Reset("Run");   // pre-seed with something other than Program

        var tools = new LogixTagTools(projectSvc, sessionSvc, modeSvc);

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        Assert.Equal("Program", modeSvc.ControllerMode);
    }
}
