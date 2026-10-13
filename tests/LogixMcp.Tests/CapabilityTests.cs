using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Acceptance tests for the <c>LGX_ENABLE_LIVE_WRITES</c> opt-in gate.
///
/// <para>
/// Coverage:
/// <list type="bullet">
///   <item>Environment-variable parsing (<see cref="Capabilities.IsTruthy"/>)</item>
///   <item><c>logix_write</c> refused on live (CIP/SDK online) sessions when opt-in is off</item>
///   <item><c>logix_write</c> allowed on virtual sessions without opt-in</item>
///   <item><c>logix_write</c> with opt-in still honours the project write-gate</item>
///   <item><c>logix_controller_mode</c> reads always allowed; mode changes on live refused without opt-in</item>
///   <item><c>logix_connect allowControllerDownload=true</c> refused without opt-in</item>
///   <item>Refusal hint names both <c>LGX_ENABLE_LIVE_WRITES</c> and the client command</item>
/// </list>
/// </para>
///
/// <para>
/// The <c>logix_propose_rung_edit</c> capability tests (which require SDK types)
/// are inside a <c>#if LOGIX_SDK</c> block at the bottom of this file.
/// </para>
/// </summary>
public class CapabilityTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a session attached to a <see cref="VirtualTagService"/> with
    /// <c>isVirtual = true</c> and <c>isLiveController = false</c>.
    /// Seeded with a minimal tag dictionary.
    /// </summary>
    private static TagSessionService BuildVirtualSession()
    {
        var svc     = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var session = new TagSessionService();
        session.Attach(svc, isVirtual: true, isLiveController: false);
        return session;
    }

    /// <summary>
    /// Builds a session that simulates a live controller (CIP or SDK online):
    /// <c>isVirtual = false</c>, <c>isLiveController = true</c>.
    /// The underlying service is a <see cref="VirtualTagService"/> stand-in;
    /// only the session metadata matters for capability-gate tests.
    /// </summary>
    private static TagSessionService BuildCipSession()
    {
        var svc     = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var session = new TagSessionService();
        // isLiveController=true simulates a CIP or SDK online session.
        session.Attach(svc, isVirtual: false, isLiveController: true);
        return session;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // EnvParsing — Capabilities.IsTruthy / FromEnvironment
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that "1", "true", and "yes" (all case variants) are truthy,
    /// and that unset (null) or any other string is falsy.
    /// </summary>
    [Fact]
    public void EnvParsing_TruthyValues_AndUnsetIsOff()
    {
        // Truthy values
        Assert.True(Capabilities.IsTruthy("1"));
        Assert.True(Capabilities.IsTruthy("true"));
        Assert.True(Capabilities.IsTruthy("True"));
        Assert.True(Capabilities.IsTruthy("TRUE"));
        Assert.True(Capabilities.IsTruthy("yes"));
        Assert.True(Capabilities.IsTruthy("Yes"));
        Assert.True(Capabilities.IsTruthy("YES"));

        // Falsy values
        Assert.False(Capabilities.IsTruthy(null));
        Assert.False(Capabilities.IsTruthy(""));
        Assert.False(Capabilities.IsTruthy("0"));
        Assert.False(Capabilities.IsTruthy("false"));
        Assert.False(Capabilities.IsTruthy("no"));
        Assert.False(Capabilities.IsTruthy("off"));
        Assert.False(Capabilities.IsTruthy("enabled"));

        // FromEnvironment with unset var always returns false.
        // (We do not mutate the process env; FromEnvironment is tested via IsTruthy above.)
        var capsOn  = new Capabilities(true);
        var capsOff = new Capabilities(false);
        Assert.True(capsOn.LiveWritesEnabled);
        Assert.False(capsOff.LiveWritesEnabled);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Write — CIP session without opt-in
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>logix_ping</c> capabilities payload points at <c>logix_doctor</c>.</summary>
    [Fact]
    public void Ping_Capabilities_MentionLogixDoctor()
    {
        var json = PingHandler_GetJson();
        using var doc = JsonDocument.Parse(json);
        var diagnostics = doc.RootElement.GetProperty("capabilities").GetProperty("diagnostics").GetString();
        Assert.Contains("logix_doctor", diagnostics);
    }

    private static string PingHandler_GetJson() =>
        JsonSerializer.Serialize(PingHandler.Execute(new Capabilities(LiveWritesEnabled: false)));

    /// <summary>
    /// When <c>LGX_ENABLE_LIVE_WRITES</c> is not set, a write to a CIP (live-
    /// controller) session must be refused with <c>kind="CapabilityDisabled"</c>.
    /// </summary>
    [Fact]
    public async Task Write_OnCipSession_WithoutOptIn_IsRefused_CapabilityDisabled()
    {
        // Arrange
        var session      = BuildCipSession();
        var capabilities = new Capabilities(LiveWritesEnabled: false);

        // Act
        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "true", session,
            capabilities: capabilities);

        // Assert
        var error = Assert.IsType<CapabilityDisabledError>(result);
        Assert.Equal("CapabilityDisabled", error.Kind);
        Assert.NotNull(error.Error);
        Assert.NotNull(error.Hint);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Write — virtual session without opt-in (allowed)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes to virtual sessions must proceed regardless of the
    /// <c>LGX_ENABLE_LIVE_WRITES</c> setting.  A virtual session cannot
    /// affect a physical controller, so the gate does not apply.
    /// </summary>
    [Fact]
    public async Task Write_OnVirtualSession_WithoutOptIn_IsAllowed()
    {
        // Arrange
        var session      = BuildVirtualSession();
        var capabilities = new Capabilities(LiveWritesEnabled: false);

        // Act — capability gate must NOT fire; the write succeeds (virtual store
        // accepts any value) or fails for a non-capability reason.
        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "true", session,
            capabilities: capabilities);

        // Assert: not a CapabilityDisabledError
        Assert.IsNotType<CapabilityDisabledError>(result);

        // And specifically, the write should succeed on the virtual service.
        Assert.IsType<WriteTagResult>(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Write — with opt-in, still honours write gate
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When the opt-in is on, the existing project write-gate policy is still
    /// enforced.  A write to a gate-denied tag (e.g. <c>Safety_OK</c> =
    /// SafetyTag) must still be refused with <c>Kind="WriteDenied"</c>.
    /// </summary>
    [Fact]
    public async Task Write_WithOptIn_StillHonoursWriteGate()
    {
        // Arrange: a virtual session that wraps a GatedTagService with the
        // Demo.L5X policy (Safety_OK is classified SafetyTag → denied).
        var innerSvc = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Safety_OK"]      = (true,  "BOOL"),
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });

        var project = L5Sharp.Core.L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);
        var gated   = new GatedTagService(innerSvc, policy);

        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true, isLiveController: false);

        var capabilities = new Capabilities(LiveWritesEnabled: true);

        // Act: write to a safety-tagged, gate-denied tag.
        var result = await WriteTagHandler.ExecuteAsync(
            "Safety_OK", "false", session,
            capabilities: capabilities);

        // Assert: gate denial — NOT a capability disabled error.
        Assert.IsNotType<CapabilityDisabledError>(result);
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), error.Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerMode — read allowed without opt-in
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Controller mode reads (<c>mode=null</c>) are always allowed, even on
    /// live sessions with <c>LGX_ENABLE_LIVE_WRITES</c> unset.
    /// </summary>
    [Fact]
    public async Task ControllerMode_Read_WithoutOptIn_IsAllowed()
    {
        // Arrange: a live session (isLiveController=true)
        var session      = BuildCipSession();
        var modeService  = new ControllerModeService();
        modeService.Reset("Run");
        var capabilities = new Capabilities(LiveWritesEnabled: false);

        // Act: mode=null → read path
        var result = await ControllerModeHandler.ExecuteAsync(
            mode: null, confirm: false,
            sessionService: session,
            modeService:    modeService,
            capabilities:   capabilities);

        // Assert: success result, not a CapabilityDisabledError
        Assert.IsNotType<CapabilityDisabledError>(result);
        var ok = Assert.IsType<ControllerModeResult>(result);
        Assert.Equal("ok", ok.Status);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ControllerMode — change on live session refused without opt-in
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When <c>LGX_ENABLE_LIVE_WRITES</c> is not set, a mode change on a live
    /// controller session must be refused with <c>kind="CapabilityDisabled"</c>.
    /// </summary>
    [Fact]
    public async Task ControllerMode_Change_OnLiveSession_WithoutOptIn_IsRefused()
    {
        // Arrange: a live session (isLiveController=true)
        var session      = BuildCipSession();
        var modeService  = new ControllerModeService();
        modeService.Reset("Run");
        var capabilities = new Capabilities(LiveWritesEnabled: false);

        // Act: mode="Program" → write path on live session
        var result = await ControllerModeHandler.ExecuteAsync(
            mode: "Program", confirm: true,
            sessionService: session,
            modeService:    modeService,
            capabilities:   capabilities);

        // Assert: CapabilityDisabledError
        var error = Assert.IsType<CapabilityDisabledError>(result);
        Assert.Equal("CapabilityDisabled", error.Kind);
        Assert.NotNull(error.Error);
        Assert.NotNull(error.Hint);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Connect — allowControllerDownload refused without opt-in
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When <c>LGX_ENABLE_LIVE_WRITES</c> is not set, specifying
    /// <c>allowControllerDownload=true</c> on <c>logix_connect</c> must be
    /// refused with <c>kind="CapabilityDisabled"</c> before any network call.
    /// </summary>
    [Fact]
    public async Task Connect_AllowControllerDownload_WithoutOptIn_IsRefused()
    {
        // Arrange: no project loaded, no session, capabilities off.
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var capabilities   = new Capabilities(LiveWritesEnabled: false);

        // Act: allowControllerDownload=true with backend="sdk", sdkMode="online"
        var result = await ConnectHandler.ExecuteAsync(
            gateway:                 "",
            path:                    "",
            useVirtual:              false,
            projectService:          projectService,
            sessionService:          sessionService,
            backend:                 "sdk",
            sdkMode:                 "online",
            allowControllerDownload: true,
            capabilities:            capabilities);

        // Assert: CapabilityDisabledError
        var error = Assert.IsType<CapabilityDisabledError>(result);
        Assert.Equal("CapabilityDisabled", error.Kind);
        Assert.NotNull(error.Error);
        Assert.NotNull(error.Hint);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Refusal hint — names the variable and client command
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every <see cref="CapabilityDisabledError"/> hint must name both
    /// <c>LGX_ENABLE_LIVE_WRITES</c> and the full
    /// <c>claude mcp add lgx-mcp -e LGX_ENABLE_LIVE_WRITES=1 -- &lt;path&gt;\LogixMcp.exe</c>
    /// client command, so the user knows exactly how to opt in.
    /// </summary>
    [Fact]
    public async Task Refusal_HintNamesVariableAndClientCommand()
    {
        // Use the write refusal path as the representative sample.
        var session      = BuildCipSession();
        var capabilities = new Capabilities(LiveWritesEnabled: false);

        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "true", session,
            capabilities: capabilities);

        var error = Assert.IsType<CapabilityDisabledError>(result);

        Assert.NotNull(error.Hint);

        // Hint must name the environment variable.
        Assert.Contains(
            Capabilities.EnvVar,
            error.Hint,
            StringComparison.OrdinalIgnoreCase);

        // Hint must include the client command (or at least the key fragment of it).
        Assert.Contains(
            "claude mcp add",
            error.Hint,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "LGX_ENABLE_LIVE_WRITES=1",
            error.Hint,
            StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Ping — capability state is reported
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that <c>logix_ping</c> / <see cref="LogixTools.GetPingResponse(Capabilities?)"/>
    /// embeds a <c>"capabilities"</c> object whose <c>liveWrites</c> and
    /// <c>studioAutomation</c> fields faithfully reflect the injected
    /// <see cref="Capabilities"/> record.
    ///
    /// <para>
    /// The test exercises both the on/off edge: with
    /// <c>LiveWritesEnabled=true, StudioAutomationEnabled=false</c> the JSON
    /// must show <c>liveWrites=true, studioAutomation=false</c>, and with the
    /// flags reversed the JSON must show the opposite values.
    /// </para>
    /// </summary>
    [Fact]
    public void Ping_ReportsCapabilities()
    {
        // ── Case 1: liveWrites=true, studioAutomation=false ──────────────────
        var capsOn = new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: false);
        var jsonOn = LogixTools.GetPingResponse(capsOn);
        var docOn  = JsonDocument.Parse(jsonOn);

        Assert.True(
            docOn.RootElement.TryGetProperty("capabilities", out var capsElOn),
            $"Expected 'capabilities' field in ping response. Response: {jsonOn}");

        Assert.True(
            capsElOn.GetProperty("liveWrites").GetBoolean(),
            "capabilities.liveWrites should be true when LiveWritesEnabled=true");

        Assert.False(
            capsElOn.GetProperty("studioAutomation").GetBoolean(),
            "capabilities.studioAutomation should be false when StudioAutomationEnabled=false");

        // ── Case 2: liveWrites=false, studioAutomation=true ──────────────────
        var capsOff = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: true);
        var jsonOff = LogixTools.GetPingResponse(capsOff);
        var docOff  = JsonDocument.Parse(jsonOff);

        Assert.True(
            docOff.RootElement.TryGetProperty("capabilities", out var capsElOff),
            $"Expected 'capabilities' field in ping response. Response: {jsonOff}");

        Assert.False(
            capsElOff.GetProperty("liveWrites").GetBoolean(),
            "capabilities.liveWrites should be false when LiveWritesEnabled=false");

        Assert.True(
            capsElOff.GetProperty("studioAutomation").GetBoolean(),
            "capabilities.studioAutomation should be true when StudioAutomationEnabled=true");

        // ── Case 3: both false (default / no opt-in) ─────────────────────────
        var capsDefault = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var jsonDefault = LogixTools.GetPingResponse(capsDefault);
        var docDefault  = JsonDocument.Parse(jsonDefault);

        Assert.True(
            docDefault.RootElement.TryGetProperty("capabilities", out var capsElDef),
            $"Expected 'capabilities' field in ping response. Response: {jsonDefault}");

        Assert.False(capsElDef.GetProperty("liveWrites").GetBoolean());
        Assert.False(capsElDef.GetProperty("studioAutomation").GetBoolean());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishMode — LGX_PUBLISH_MODE parsing
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that <see cref="Capabilities.ParsePublishMode"/> returns
    /// <see cref="PublishMode.Confirm"/> for <c>null</c> (unset) and for any
    /// unrecognised string, and returns <see cref="PublishMode.Auto"/> for the
    /// case-insensitive string <c>"auto"</c>.
    /// </summary>
    [Fact]
    public void PublishMode_UnsetOrUnknown_IsConfirm_AutoParses()
    {
        // Unset (null) → Confirm
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode(null));

        // Unknown strings → Confirm
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode(""));
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode("confirm"));
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode("CONFIRM"));
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode("1"));
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode("yes"));
        Assert.Equal(PublishMode.Confirm, Capabilities.ParsePublishMode("manual"));

        // Case-insensitive "auto" → Auto
        Assert.Equal(PublishMode.Auto, Capabilities.ParsePublishMode("auto"));
        Assert.Equal(PublishMode.Auto, Capabilities.ParsePublishMode("Auto"));
        Assert.Equal(PublishMode.Auto, Capabilities.ParsePublishMode("AUTO"));

        // Capabilities record default is Confirm
        var capsDefault = new Capabilities(LiveWritesEnabled: false);
        Assert.Equal(PublishMode.Confirm, capsDefault.PublishMode);

        // Explicit Auto survives round-trip through the record
        var capsAuto = new Capabilities(LiveWritesEnabled: false, PublishMode: PublishMode.Auto);
        Assert.Equal(PublishMode.Auto, capsAuto.PublishMode);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Ping — publishMode is reported
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that <c>logix_ping</c> / <see cref="LogixTools.GetPingResponse(Capabilities?)"/>
    /// embeds a <c>capabilities.publishMode</c> field valued <c>"confirm"</c>
    /// or <c>"auto"</c> depending on the injected <see cref="Capabilities.PublishMode"/>.
    /// </summary>
    [Fact]
    public void Ping_ReportsPublishMode()
    {
        // ── Case 1: default (Confirm) ─────────────────────────────────────────
        var capsConfirm = new Capabilities(LiveWritesEnabled: false);
        var jsonConfirm = LogixTools.GetPingResponse(capsConfirm);
        var docConfirm  = JsonDocument.Parse(jsonConfirm);

        Assert.True(
            docConfirm.RootElement.TryGetProperty("capabilities", out var capsElConfirm),
            $"Expected 'capabilities' field in ping response. Response: {jsonConfirm}");

        Assert.Equal(
            "confirm",
            capsElConfirm.GetProperty("publishMode").GetString(),
            ignoreCase: true);

        // ── Case 2: Auto ──────────────────────────────────────────────────────
        var capsAuto = new Capabilities(LiveWritesEnabled: false, PublishMode: PublishMode.Auto);
        var jsonAuto = LogixTools.GetPingResponse(capsAuto);
        var docAuto  = JsonDocument.Parse(jsonAuto);

        Assert.True(
            docAuto.RootElement.TryGetProperty("capabilities", out var capsElAuto),
            $"Expected 'capabilities' field in ping response. Response: {jsonAuto}");

        Assert.Equal(
            "auto",
            capsElAuto.GetProperty("publishMode").GetString(),
            ignoreCase: true);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ProposeRungEdit — SDK-gated tests
    // ══════════════════════════════════════════════════════════════════════════

#if LOGIX_SDK

    // ══════════════════════════════════════════════════════════════════════════
    // Refusal_IsWrittenToAuditTrail — audit trail entry for CapabilityDisabled
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every <see cref="CapabilityDisabledError"/> refusal must append a
    /// <c>capability_refusal</c> entry to the audit trail before any network or
    /// SDK call is made.  This test exercises both gated capability variables:
    /// <list type="bullet">
    ///   <item>
    ///     <c>LGX_ENABLE_LIVE_WRITES</c> — verified via
    ///     <see cref="WriteTagHandler.ExecuteAsync"/> on a live-controller session.
    ///   </item>
    ///   <item>
    ///     <c>LGX_ENABLE_STUDIO_AUTOMATION</c> — verified via
    ///     <c>LogixSdkTools.GetCancelPendingEditsResponseAsync</c>.
    ///   </item>
    /// </list>
    /// The audit entry must include the operation name that was denied and the
    /// environment variable that gates it.
    /// </summary>
    [Fact]
    public async Task Refusal_IsWrittenToAuditTrail()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cap-refusal-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            var auditFile = Path.Combine(tempDir, "audit.jsonl");

            // ── LGX_ENABLE_LIVE_WRITES: logix_write refusal ──────────────────
            var session  = BuildCipSession();
            var capsOff  = new Capabilities(LiveWritesEnabled: false);

            await WriteTagHandler.ExecuteAsync(
                "Cmd_MotorStart", "true", session, capabilities: capsOff);

            var lines = File.ReadAllLines(auditFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(line => JsonDocument.Parse(line))
                .ToList();

            // At least one capability_refusal event must have been written.
            var liveWriteRefusal = lines.FirstOrDefault(doc =>
                doc.RootElement.TryGetProperty("event", out var ev) &&
                ev.GetString() == "capability_refusal");
            Assert.NotNull(liveWriteRefusal);

            // The entry must name the denied operation.
            Assert.Equal(
                "logix_write",
                liveWriteRefusal!.RootElement.GetProperty("operation").GetString());

            // The entry must name the governing environment variable.
            Assert.Equal(
                Capabilities.EnvVar,
                liveWriteRefusal.RootElement.GetProperty("env_var").GetString());

            // ── LGX_ENABLE_STUDIO_AUTOMATION: logix_cancel_pending_edits ─────
            // Reset the audit file so only the new refusal is captured.
            File.WriteAllText(auditFile, string.Empty);

            var projectService = new ProjectService();
            var sessionService = new TagSessionService();
            var capsNoStudio   = new Capabilities(
                LiveWritesEnabled:      false,
                StudioAutomationEnabled: false);
            var tools = new LogixSdkTools(projectService, sessionService, capsNoStudio);

            await tools.GetCancelPendingEditsResponseAsync(
                program: "MainProgram",
                routine: "MainRoutine",
                confirm: true);

            var studioLines = File.ReadAllLines(auditFile)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(line => JsonDocument.Parse(line))
                .ToList();

            var studioRefusal = studioLines.FirstOrDefault(doc =>
                doc.RootElement.TryGetProperty("event", out var ev) &&
                ev.GetString() == "capability_refusal");
            Assert.NotNull(studioRefusal);

            Assert.Equal(
                "logix_cancel_pending_edits",
                studioRefusal!.RootElement.GetProperty("operation").GetString());

            Assert.Equal(
                Capabilities.EnvVarStudio,
                studioRefusal.RootElement.GetProperty("env_var").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// When <c>LGX_ENABLE_LIVE_WRITES</c> is not set, calling
    /// <c>logix_propose_rung_edit</c> with <c>target="controller"</c> must be
    /// refused with <c>kind="CapabilityDisabled"</c> BEFORE any SDK session
    /// interaction.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_ControllerTarget_WithoutOptIn_IsRefused_BeforeSdkCall()
    {
        // Arrange: no project, no session, capabilities off.
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var capabilities   = new Capabilities(LiveWritesEnabled: false);
        var tools          = new LogixSdkTools(projectService, sessionService, capabilities);

        // Act: controller target — capability gate must fire before any session work.
        var json = await tools.GetProposeRungEditResponseAsync(
            routineXPath:   "Controller/Programs/Program[@Name='Main']/Routines/Routine[@Name='MainRoutine']",
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    null,
            session:        null,
            offline:        false,
            target:         "controller");

        // Assert: deserialise and verify kind=CapabilityDisabled
        var doc  = JsonDocument.Parse(json);
        var kind = doc.RootElement.GetProperty("kind").GetString();
        Assert.Equal("CapabilityDisabled", kind);

        // Hint must be present.
        Assert.True(
            doc.RootElement.TryGetProperty("hint", out var hintEl),
            "Expected 'hint' field in CapabilityDisabledError response.");
        var hint = hintEl.GetString();
        Assert.NotNull(hint);
        Assert.NotEmpty(hint);
    }

    /// <summary>
    /// When <c>LGX_ENABLE_LIVE_WRITES</c> is not set, calling
    /// <c>logix_propose_rung_edit</c> with <c>target="workstation"</c> must NOT
    /// be refused by the capability gate.  The call may fail for other reasons
    /// (no session, no project) but must not produce a
    /// <c>kind="CapabilityDisabled"</c> response.
    /// </summary>
    [Fact]
    public async Task ProposeRungEdit_WorkstationTarget_WithoutOptIn_IsAllowed()
    {
        // Arrange: no project, no session, capabilities off.
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var capabilities   = new Capabilities(LiveWritesEnabled: false);
        var tools          = new LogixSdkTools(projectService, sessionService, capabilities);

        // Act: workstation target — capability gate must NOT fire.
        var json = await tools.GetProposeRungEditResponseAsync(
            routineXPath:   "Controller/Programs/Program[@Name='Main']/Routines/Routine[@Name='MainRoutine']",
            insertPosition: 0,
            replaceCount:   0,
            l5xFragment:    "<rung/>",
            session:        null,
            offline:        false,
            target:         "workstation");

        // Assert: must NOT be a CapabilityDisabledError
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("kind", out var kindEl))
        {
            var kind = kindEl.GetString();
            Assert.NotEqual("CapabilityDisabled", kind);
        }
        // The response should contain an 'error' field explaining the real failure
        // (no SDK session, no project) — not a capability refusal.
        Assert.True(
            doc.RootElement.TryGetProperty("error", out _),
            "Expected an 'error' field in the response (non-capability failure).");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CancelPendingEdits — refused without LGX_ENABLE_STUDIO_AUTOMATION
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When <c>LGX_ENABLE_STUDIO_AUTOMATION</c> is not set,
    /// <c>logix_cancel_pending_edits</c> must be refused with
    /// <c>kind="CapabilityDisabled"</c> before any Studio-installed probe,
    /// pending-edit read, or session-1 script invocation.
    /// The hint must name both <c>LGX_ENABLE_STUDIO_AUTOMATION</c> and the
    /// client command to set it.
    /// </summary>
    [Fact]
    public async Task CancelPendingEdits_WithoutStudioAutomation_IsRefused()
    {
        // Arrange: no session, StudioAutomationEnabled=false.
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var capabilities   = new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false);
        var tools          = new LogixSdkTools(projectService, sessionService, capabilities);

        // Act: capability gate must fire before any Studio-installed probe or
        // pending-edit reader is consulted.
        var json = await tools.GetCancelPendingEditsResponseAsync(
            program: "MainProgram",
            routine: "MainRoutine",
            confirm: true);

        // Assert: CapabilityDisabledError with kind="CapabilityDisabled"
        var doc  = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("kind", out var kindEl),
            $"Expected 'kind' field in response. Response: {json}");
        Assert.Equal("CapabilityDisabled", kindEl.GetString());

        // Hint must be present.
        Assert.True(
            doc.RootElement.TryGetProperty("hint", out var hintEl),
            $"Expected 'hint' field in CapabilityDisabledError response. Response: {json}");
        var hint = hintEl.GetString();
        Assert.NotNull(hint);
        Assert.NotEmpty(hint);

        // Hint must name the environment variable.
        Assert.Contains(
            Capabilities.EnvVarStudio,
            hint!,
            StringComparison.OrdinalIgnoreCase);

        // Hint must include the client command fragment.
        Assert.Contains(
            "claude mcp add",
            hint,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "LGX_ENABLE_STUDIO_AUTOMATION=1",
            hint,
            StringComparison.Ordinal);
    }

#endif  // LOGIX_SDK

    /// <summary>logix_ping reports whether LGX_WRITE_ALLOWLIST is active.</summary>
    [Fact]
    public void Ping_ReportsWriteAllowlistActive()
    {
        var on  = JsonDocument.Parse(LogixTools.GetPingResponse(
            new Capabilities(false, WriteAllowlistPath: "/etc/lgx/allow.txt"))).RootElement.GetProperty("capabilities");
        Assert.True(on.GetProperty("writeAllowlistActive").GetBoolean());
        Assert.Equal("/etc/lgx/allow.txt", on.GetProperty("writeAllowlistPath").GetString());

        var off = JsonDocument.Parse(LogixTools.GetPingResponse(
            new Capabilities(false))).RootElement.GetProperty("capabilities");
        Assert.False(off.GetProperty("writeAllowlistActive").GetBoolean());
        Assert.False(off.TryGetProperty("writeAllowlistPath", out _));
    }
}
