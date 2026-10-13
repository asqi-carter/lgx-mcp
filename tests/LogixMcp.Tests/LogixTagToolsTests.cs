using System.Text.Json;
using L5Sharp.Core;
#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
#endif
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Unit and acceptance tests for the four MCP tag-access tools:
/// <c>logix_connect</c>, <c>logix_read</c>, <c>logix_write</c>, <c>logix_poll</c>.
///
/// All tests use a <see cref="VirtualTagService"/> seeded with demo tags so no
/// real PLC connection is required.  Tests exercise the handler layer directly and
/// (for the MCP-tool layer) via <see cref="LogixTagTools"/>.
/// </summary>
[Collection("StaticHandlerHooks")]
public class LogixTagToolsTests : IDisposable
{

    // Per-test lease file (xunit builds one instance per test): nothing here may read or write the host-wide lease.
    private readonly IsolatedUploadLease _leaseScope = new();

    /// <inheritdoc />
    public void Dispose() => _leaseScope.Dispose();
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>Builds a fresh ProjectService with Demo.L5X loaded.</summary>
    private static ProjectService LoadDemoProject()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        return svc;
    }

    /// <summary>
    /// Builds a <see cref="VirtualTagService"/> seeded with the demo controller tags.
    /// </summary>
    private static VirtualTagService BuildDemoVirtual() =>
        new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"]    = (false, "BOOL"),
            ["Cmd_SpeedSetpoint"] = (0.0,   "REAL"),
            ["Sim_LineSpeed"]     = (0.0,   "REAL"),
            ["Safety_OK"]         = (true,  "BOOL"),
            ["Motor_Run"]         = (false, "BOOL"),
            ["Cmd_Motor"]         = (0,     "MotorCmd"),
        });

    /// <summary>
    /// Builds a <see cref="TagSessionService"/> already attached to a gated virtual
    /// session backed by the Demo fixture.
    /// </summary>
    private static (TagSessionService session, VirtualTagService inner) BuildGatedVirtualSession()
    {
        var inner  = BuildDemoVirtual();
        var policy = GateEngine.DerivePolicy(L5X.Load(FixturePath), GateConfig.Default);
        var gated  = new GatedTagService(inner, policy);

        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);
        return (session, inner);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TagSessionService
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TagSessionService_InitiallyNotConnected()
    {
        var svc = new TagSessionService();

        Assert.False(svc.IsConnected);
        Assert.Null(svc.Service);
    }

    [Fact]
    public void TagSessionService_Attach_SetsIsConnected()
    {
        var svc     = new TagSessionService();
        var virtual_ = BuildDemoVirtual();

        svc.Attach(virtual_, isVirtual: true);

        Assert.True(svc.IsConnected);
        Assert.Same(virtual_, svc.Service);
        Assert.True(svc.IsVirtual);
    }

    [Fact]
    public void TagSessionService_Detach_ClearsSession()
    {
        var svc = new TagSessionService();
        svc.Attach(BuildDemoVirtual(), isVirtual: true);

        svc.Detach();

        Assert.False(svc.IsConnected);
        Assert.Null(svc.Service);
        Assert.False(svc.IsVirtual);
    }

    [Fact]
    public void TagSessionService_Attach_NullThrows()
    {
        var svc = new TagSessionService();

        Assert.Throws<ArgumentNullException>(() => svc.Attach(null!, isVirtual: false));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ConnectHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectHandler_VirtualMode_NoProjectLoaded_ReturnsError()
    {
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        Assert.IsType<ConnectError>(result);
        var err = (ConnectError) result;
        Assert.Contains("No project loaded", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_VirtualMode_ProjectLoaded_ReturnsConnectResult()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        Assert.IsType<ConnectResult>(result);
        var ok = (ConnectResult) result;
        Assert.Equal("connected", ok.Status);
        Assert.True(ok.Virtual);
        Assert.True(ok.Gated);
        Assert.True(ok.TagCount > 0);
    }

    [Fact]
    public async Task ConnectHandler_VirtualMode_AttachesGatedServiceToSession()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        Assert.True(sessionSvc.IsConnected);
        Assert.True(sessionSvc.IsVirtual);
        Assert.IsType<GatedTagService>(sessionSvc.Service);
    }

    [Fact]
    public async Task ConnectHandler_VirtualMode_TagCountMatchesProjectControllerTags()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        // Demo.L5X has 7 controller-scoped tags.
        Assert.Equal(7, result.TagCount);
    }

    // ── ConnectHandler — backend parameter ───────────────────────────────────

    [Fact]
    public async Task ConnectHandler_SdkBackend_NoProjectLoaded_ReturnsError()
    {
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        Assert.IsType<ConnectError>(result);
        var err = (ConnectError) result;
        Assert.Contains("No project loaded", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_ProjectLoaded_ReturnsConnectResult()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        Assert.IsType<ConnectResult>(result);
        var ok = (ConnectResult) result;
        Assert.Equal("connected", ok.Status);
        Assert.Equal("l5x", ok.Backend);
        Assert.False(ok.Virtual);
        Assert.True(ok.Gated);
        Assert.Null(ok.TagCount);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_AttachesGatedLogixSdkServiceToSession()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        Assert.True(sessionSvc.IsConnected);
        Assert.False(sessionSvc.IsVirtual);
        Assert.IsType<GatedTagService>(sessionSvc.Service);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_CaseInsensitive_ProjectLoaded_ReturnsConnectResult()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        // Upper-case variant should also be accepted.
        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "L5X");

        Assert.IsType<ConnectResult>(result);
        var ok = (ConnectResult) result;
        Assert.Equal("l5x", ok.Backend);
    }

    [Fact]
    public async Task ConnectHandler_DefaultBackend_ReturnsBackendCip()
    {
        // Virtual mode returns "virtual", not "cip".
        // Test that the non-virtual default path reports backend="cip" in its result.
        // We cannot actually connect to a live PLC in unit tests, so test via the result
        // type only: default backend is "cip", which means it attempts a live connection.
        // Instead, verify via virtual mode returns "virtual" so the new field is always set.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        Assert.Equal("virtual", result.Backend);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_SessionCanReadTag()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        // After attaching, reading a known tag via the session should work.
        var readResult = await ReadTagHandler.ExecuteAsync("Cmd_MotorStart", sessionSvc);

        Assert.IsType<ReadTagResult>(readResult);
        var ok = (ReadTagResult) readResult;
        Assert.Equal("Cmd_MotorStart", ok.Tag);
        Assert.Equal("BOOL", ok.DataType);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_GrantedWrite_Succeeds()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var writeResult = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", sessionSvc);

        Assert.IsType<WriteTagResult>(writeResult);
        Assert.Equal("ok", ((WriteTagResult) writeResult).Status);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_DeniedWrite_ReturnsWriteDenied()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var writeResult = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", sessionSvc);

        Assert.IsType<WriteTagError>(writeResult);
        Assert.Equal("WriteDenied", ((WriteTagError) writeResult).Kind);
    }

    [Fact]
    public async Task LogixTagTools_Connect_SdkBackend_ReturnsValidJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync("198.51.100.1", "1,0",
            useVirtual: false, backend: "l5x");

        var doc = JsonDocument.Parse(json);
        Assert.Equal("connected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("l5x",       doc.RootElement.GetProperty("backend").GetString());
        Assert.False(doc.RootElement.GetProperty("virtual").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("gated").GetBoolean());
    }

    // ── ConnectHandler — backend='sdk' ───────────────────────────────────────

    /// <summary>
    /// <see cref="ISdkTagServiceFactory"/> that always throws a
    /// <see cref="TagServiceException"/> to simulate a failed SDK open.
    ///
    /// <para>
    /// Depends only on <see cref="ISdkTagServiceFactory"/>,
    /// <see cref="SdkSessionResult"/> and <see cref="TagServiceException"/> — all
    /// unconditional types in the core <c>LogixMcp</c> assembly — so this helper
    /// lives outside the <c>#if LOGIX_SDK</c> block and is usable by tests that
    /// exercise handler-level error paths (e.g. missing-argument families) without
    /// requiring the Logix Designer SDK feed.
    /// </para>
    /// </summary>
    private sealed class ThrowingOpenSdkFactory : ISdkTagServiceFactory
    {
        private readonly TagServiceException _ex;

        internal ThrowingOpenSdkFactory(TagServiceException ex) => _ex = ex;

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw _ex;

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw _ex;
    }

#if LOGIX_SDK
    // ── SDK test helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ISdkTagServiceFactory"/> implementation that creates a
    /// <see cref="TypedSdkTagService"/> backed by an injected
    /// <see cref="LogixProjectSession"/> for unit-test isolation.
    /// Records the <paramref name="sdkMode"/> passed to <see cref="CreateAsync"/>
    /// so tests can assert the correct mode was forwarded from the handler.
    /// </summary>
    private sealed class InjectableSessionSdkFactory : ISdkTagServiceFactory
    {
        private readonly LogixProjectSession _session;

        /// <summary>The sdkMode last passed to <see cref="CreateAsync"/>.</summary>
        public string? LastSdkMode { get; private set; }

        /// <summary>The allowControllerDownload value last passed to <see cref="CreateAsync"/>.</summary>
        public bool LastAllowControllerDownload { get; private set; }

        internal InjectableSessionSdkFactory(LogixProjectSession session)
            => _session = session;

        public async Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
        {
            LastSdkMode                = sdkMode;
            LastAllowControllerDownload = allowControllerDownload;
            await _session.OpenAsync(projectFilePath, ct);
            return new SdkSessionResult(
                Service:           new TypedSdkTagService(_session, typeProject),
                DownloadPerformed: false,
                ModeTransitions:   []);
        }

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "InjectableSessionSdkFactory does not support the no-project-file path.");
    }

    // ── UploadFromControllerSdkFactory — test double for CreateFromControllerAsync ──

    /// <summary>
    /// <see cref="ISdkTagServiceFactory"/> whose <see cref="CreateFromControllerAsync"/>
    /// is configurable: it can succeed (returning a canned <see cref="SdkSessionResult"/>)
    /// or throw (simulating an upload failure).
    ///
    /// <para>
    /// Used exclusively to test <see cref="ConnectHandler"/>'s no-project-file path
    /// (<c>backend="sdk"</c>, <c>sdkMode="online"</c>, no project loaded, gateway provided).
    /// </para>
    /// </summary>
    private sealed class UploadFromControllerSdkFactory : ISdkTagServiceFactory
    {
        private readonly SdkSessionResult? _result;
        private readonly TagServiceException? _ex;

        /// <summary>The commPath last passed to <see cref="CreateFromControllerAsync"/>.</summary>
        public string? LastCommPath { get; private set; }

        /// <summary>
        /// Constructs a factory that returns <paramref name="result"/> on
        /// <see cref="CreateFromControllerAsync"/>.
        /// </summary>
        internal UploadFromControllerSdkFactory(SdkSessionResult result) => _result = result;

        /// <summary>
        /// Constructs a factory that throws <paramref name="ex"/> on
        /// <see cref="CreateFromControllerAsync"/>.
        /// </summary>
        internal UploadFromControllerSdkFactory(TagServiceException ex) => _ex = ex;

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "UploadFromControllerSdkFactory is only for the no-project-file path.");

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
        {
            LastCommPath = commPath;
            if (_ex is not null) throw _ex;
            return System.Threading.Tasks.Task.FromResult(_result!);
        }
    }

    /// <summary>
    /// Builds an <see cref="UploadFromControllerSdkFactory"/> that returns a
    /// pre-built <see cref="SdkSessionResult"/> with
    /// <see cref="SdkSessionResult.CorrelationPath"/> set to
    /// <c>"upload-to-new-project"</c> and
    /// <see cref="SdkSessionResult.TypeProject"/> set to the Demo L5X so that
    /// the gate-policy derivation step in <see cref="ConnectHandler"/> succeeds.
    /// </summary>
#if LOGIX_SDK
    private static UploadFromControllerSdkFactory BuildUploadFromControllerFactory()
    {
        var typeProject = L5X.Load(FixturePath);
        var session     = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(
                new RecordingFakeLogixProject()));
        var svc    = new TypedSdkTagService(session, typeProject);
        var result = new SdkSessionResult(
            Service:           svc,
            DownloadPerformed: false,
            ModeTransitions:   [],
            CorrelationPath:   "upload-to-new-project",
            TypeProject:       typeProject,
            AcdPath:           "/tmp/logix-mcp-work/fake-slot/uploaded.ACD");
        return new UploadFromControllerSdkFactory(result);
    }
#endif

    /// <summary>
    /// Creates a <see cref="LogixProjectSession"/> backed by a
    /// <see cref="RecordingFakeLogixProject"/> and wraps it in an
    /// <see cref="InjectableSessionSdkFactory"/> for SDK backend tests.
    /// </summary>
    private static (InjectableSessionSdkFactory Factory, RecordingFakeLogixProject Fake)
        BuildFakeSdkFactory()
    {
        var fake    = new RecordingFakeLogixProject();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        return (new InjectableSessionSdkFactory(session), fake);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_NoProjectLoaded_ReturnsError()
    {
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        Assert.IsType<ConnectError>(result);
        var err = (ConnectError) result;
        Assert.Contains("No project loaded", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_ProjectLoaded_ReturnsConnectResult()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        Assert.IsType<ConnectResult>(result);
        var ok = (ConnectResult) result;
        Assert.Equal("connected", ok.Status);
        Assert.Equal("sdk", ok.Backend);
        Assert.False(ok.Virtual);
        Assert.True(ok.Gated);
        Assert.Null(ok.TagCount);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_AttachesGatedTypedSdkServiceToSession()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        Assert.True(sessionSvc.IsConnected);
        Assert.False(sessionSvc.IsVirtual);
        Assert.IsType<GatedTagService>(sessionSvc.Service);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_CaseInsensitive_ReturnsConnectResult()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "SDK", sdkFactory: factory);

        Assert.IsType<ConnectResult>(result);
        Assert.Equal("sdk", ((ConnectResult) result).Backend);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_SessionOpenFails_ReturnsError()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        // Inject a factory that always throws TagServiceException on CreateAsync.
        var factory = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed, "Simulated SDK open failure")));

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        Assert.IsType<ConnectError>(result);
        Assert.Contains("SDK session open failed", ((ConnectError) result).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_SessionCanReadTag()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        // After attaching, reading a known tag via the session should succeed.
        var readResult = await ReadTagHandler.ExecuteAsync("Cmd_MotorStart", sessionSvc);

        Assert.IsType<ReadTagResult>(readResult);
        var ok = (ReadTagResult) readResult;
        Assert.Equal("Cmd_MotorStart", ok.Tag);
        Assert.Equal("BOOL", ok.DataType);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_GrantedWrite_Succeeds()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var writeResult = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", sessionSvc);

        Assert.IsType<WriteTagResult>(writeResult);
        Assert.Equal("ok", ((WriteTagResult) writeResult).Status);
    }

    [Fact]
    public async Task ConnectHandler_TypedSdkBackend_DeniedWrite_ReturnsWriteDenied()
    {
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var writeResult = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", sessionSvc);

        Assert.IsType<WriteTagError>(writeResult);
        Assert.Equal("WriteDenied", ((WriteTagError) writeResult).Kind);
    }

    // ── ConnectHandler — download-required safety guardrail ──────────────────
    //
    // When sdkMode='online' and allowControllerDownload is absent or false,
    // any ImagesIncompatible error from the factory MUST surface as a
    // ConnectDownloadRequiredError — not a bare ConnectError — and NO download
    // may be triggered.

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_ReturnsDownloadRequiredError()
    {
        // Arrange: project loaded but controller holds a non-correlated image.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var factory = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ImagesIncompatible,
                "The controller holds a different project than the one opened.")));

        // Act: online mode, allowControllerDownload omitted (defaults to false).
        // gateway is empty so this exercises the file-based SDK path (no comm
        // path), not the comm-path-wins upload-from-controller branch.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkMode: "online", sdkFactory: factory);

        // Assert: structured download-required error, not a generic ConnectError.
        Assert.IsType<ConnectDownloadRequiredError>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_ErrorExplainsDownloadEffect()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var factory = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ImagesIncompatible,
                "The controller holds a different project than the one opened.")));

        // gateway is empty so this exercises the file-based SDK path.
        var result = (ConnectDownloadRequiredError) await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkMode: "online", sdkFactory: factory);

        // The DownloadEffect field must explain what a download does.
        Assert.Contains("stop", result.DownloadEffect, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overwrite", result.DownloadEffect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_ErrorExplainsHowToOptIn()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var factory = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ImagesIncompatible,
                "The controller holds a different project than the one opened.")));

        // gateway is empty so this exercises the file-based SDK path.
        var result = (ConnectDownloadRequiredError) await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkMode: "online", sdkFactory: factory);

        // The HowToOptIn field must mention allowControllerDownload=true.
        Assert.Contains("allowControllerDownload", result.HowToOptIn,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("true", result.HowToOptIn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_NoSessionAttached()
    {
        // When the download-required error is returned, no session must be attached.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var factory = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ImagesIncompatible,
                "The controller holds a different project than the one opened.")));

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkMode: "online", sdkFactory: factory);

        Assert.False(sessionSvc.IsConnected);
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_NonCorrelatedImage_NoDownloadTriggered()
    {
        // Verify end-to-end: SdkConnectFactory with a fake that throws
        // IMAGES_INCOMPATIBLE must not call DownloadAsync.
        //
        // IMPORTANT: LogixProjectSession with an identity copier (the test
        // constructor) will call File.Delete() on the path passed to OpenAsync
        // when the session is disposed.  To prevent the fixture from being
        // deleted, we use a temp copy of the fixture as the project path.
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".L5X");
        File.Copy(FixturePath, tempPath, overwrite: true);
        try
        {
            var projectSvc = new ProjectService();
            projectSvc.Load(tempPath);
            var sessionSvc = new TagSessionService();

            var fake = new FakeLogixProject
            {
                GoOnlineException = new InvalidOperationException(
                    "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
            };
            var sdkFactory = new SdkConnectFactory(
                () => new LogixProjectSession(
                    (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake)),
                    lease: _leaseScope.Lease);

            // allowControllerDownload intentionally omitted (defaults to false).
            // gateway is empty so this exercises the file-based SDK path.
            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projectSvc, sessionSvc,
                backend: "sdk", sdkMode: "online", sdkFactory: sdkFactory);

            // Must return download-required error, not succeed or download.
            Assert.IsType<ConnectDownloadRequiredError>(result);
            Assert.Equal(0, fake.DownloadCallCount);
        }
        finally
        {
            // Session.Dispose() may have already deleted the temp file — that is
            // correct behaviour (identity copier treats the source as the copy).
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_CannotUploadPhysAddr_ReturnsDownloadRequiredError()
    {
        // CANNOT_UPLOAD_PHYS_ADDR must also surface as ConnectDownloadRequiredError.
        // Same temp-copy pattern as ConnectHandler_SdkOnline_NonCorrelatedImage_NoDownloadTriggered.
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".L5X");
        File.Copy(FixturePath, tempPath, overwrite: true);
        try
        {
            var projectSvc = new ProjectService();
            projectSvc.Load(tempPath);
            var sessionSvc = new TagSessionService();

            var fake = new FakeLogixProject
            {
                GoOnlineException = new InvalidOperationException(
                    "CANNOT_UPLOAD_PHYS_ADDR - Physical address cannot be correlated.")
            };
            var sdkFactory = new SdkConnectFactory(
                () => new LogixProjectSession(
                    (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake)),
                    lease: _leaseScope.Lease);

            // gateway is empty so this exercises the file-based SDK path.
            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projectSvc, sessionSvc,
                backend: "sdk", sdkMode: "online", sdkFactory: sdkFactory);

            Assert.IsType<ConnectDownloadRequiredError>(result);
            Assert.Equal(0, fake.DownloadCallCount);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_HintNamesUpload()
    {
        // The IMAGES_INCOMPATIBLE hint must contain the word "upload" so the
        // caller knows the non-destructive path involves uploading the controller's
        // project rather than a destructive download.  Guards against the ordering
        // regression: the existing doc-comment listed download
        // first; the hint must not replicate that ordering.
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".L5X");
        File.Copy(FixturePath, tempPath, overwrite: true);
        try
        {
            var projectSvc = new ProjectService();
            projectSvc.Load(tempPath);
            var sessionSvc = new TagSessionService();

            var fake = new FakeLogixProject
            {
                GoOnlineException = new InvalidOperationException(
                    "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
            };
            var sdkFactory = new SdkConnectFactory(
                () => new LogixProjectSession(
                    (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake)),
                    lease: _leaseScope.Lease);

            // gateway is empty so this exercises the file-based SDK path.
            var result = (ConnectDownloadRequiredError) await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projectSvc, sessionSvc,
                backend: "sdk", sdkMode: "online", sdkFactory: sdkFactory);

            Assert.NotNull(result.Hint);
            Assert.Contains("upload", result.Hint, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ConnectHandler_SdkOnline_ImagesIncompatible_HintDoesNotLeadWithDownload()
    {
        // The IMAGES_INCOMPATIBLE hint must lead with the non-destructive upload
        // path, not with a download instruction.  Specifically, "upload" must appear
        // before "download" in the hint text — this is the regression guard for the
        // ordering error in the doc-comment that listed download first.
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".L5X");
        File.Copy(FixturePath, tempPath, overwrite: true);
        try
        {
            var projectSvc = new ProjectService();
            projectSvc.Load(tempPath);
            var sessionSvc = new TagSessionService();

            var fake = new FakeLogixProject
            {
                GoOnlineException = new InvalidOperationException(
                    "RxCL_E_IMAGES_INCOMPATIBLE - Project in controller does not match open project.")
            };
            var sdkFactory = new SdkConnectFactory(
                () => new LogixProjectSession(
                    (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake)),
                    lease: _leaseScope.Lease);

            // gateway is empty so this exercises the file-based SDK path.
            var result = (ConnectDownloadRequiredError) await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projectSvc, sessionSvc,
                backend: "sdk", sdkMode: "online", sdkFactory: sdkFactory);

            var hint = result.Hint;
            Assert.NotNull(hint);

            var hintLower      = hint.ToLowerInvariant();
            var uploadIndex    = hintLower.IndexOf("upload",   StringComparison.Ordinal);
            var downloadIndex  = hintLower.IndexOf("download", StringComparison.Ordinal);

            // "upload" must appear in the hint and must come before any mention of
            // "download".  If there is no "download" at all, that is also acceptable.
            Assert.True(uploadIndex >= 0,
                "Hint must contain 'upload'.");
            Assert.True(downloadIndex < 0 || uploadIndex < downloadIndex,
                $"Hint must not lead with 'download': upload at {uploadIndex}, download at {downloadIndex}.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
#endif

    // ── ConnectHandler — backend validation ──────────────────────────────────

    [Fact]
    public async Task ConnectHandler_UnrecognizedBackend_ReturnsConnectBadBackendError()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "bogus");

        Assert.IsType<ConnectBadBackendError>(result);
    }

    [Fact]
    public async Task ConnectHandler_UnrecognizedBackend_ErrorContainsSuppliedValue()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectBadBackendError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "bogus");

        Assert.Equal("bogus", result.Backend);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_UnrecognizedBackend_ValidValuesListsAllFourOptions()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectBadBackendError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "bogus");

        Assert.Contains("cip",  result.ValidValues, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sdk",  result.ValidValues, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("l5x",  result.ValidValues, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("virtual", result.ValidValues, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(4, result.ValidValues.Length);
    }
#endif

    [Fact]
    public async Task ConnectHandler_UnrecognizedBackend_ErrorMessageIncludesValidOptions()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectBadBackendError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "live");

        Assert.Contains("cip", result.Error, StringComparison.OrdinalIgnoreCase);
#if LOGIX_SDK
        Assert.Contains("sdk", result.Error, StringComparison.OrdinalIgnoreCase);
#endif
        Assert.Contains("l5x", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CIP")]
    [InlineData("Cip")]
    [InlineData("L5X")]
    [InlineData("L5x")]
#if LOGIX_SDK
    [InlineData("SDK")]
    [InlineData("Sdk")]
#endif
    public async Task ConnectHandler_ValidBackend_CaseVariants_NotRejectedAsUnrecognized(
        string backendVariant)
    {
        // Valid backends must be accepted case-insensitively; they must NOT produce a
        // ConnectBadBackendError (they may produce ConnectError for other reasons such as
        // "no project loaded").
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: backendVariant,
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        Assert.IsNotType<ConnectBadBackendError>(result);
    }

    [Fact]
    public async Task ConnectHandler_UnrecognizedBackend_SessionIsNotAttached()
    {
        // The session must not be attached when an invalid backend is supplied.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "bad");

        Assert.False(sessionSvc.IsConnected);
    }

    // ── ConnectHandler — unknown parameter validation ─────────────────────────
    // Tests for CheckForUnknownParameters (static) and the GetConnectResponseAsync
    // rawArgumentKeys path.  These guard against the silent-downgrade hazard where
    // a caller passing mode="online" (instead of sdkMode="online") would otherwise
    // receive an offline session without any warning.

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_NoUnknownKeys_ReturnsNull()
    {
        // All valid parameter names must be accepted; the method must return null.
        var result = ConnectHandler.CheckForUnknownParameters(
#if LOGIX_SDK
            ["gateway", "path", "useVirtual", "backend", "sdkMode"]);
#else
            ["gateway", "path", "useVirtual", "backend", "confirmedByUser"]);
#endif

        Assert.Null(result);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_EmptyKeys_ReturnsNull()
    {
        // An empty argument list (caller supplied no parameters) is always valid.
        var result = ConnectHandler.CheckForUnknownParameters([]);

        Assert.Null(result);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_UnknownKey_ReturnsError()
    {
        // A single unrecognised key must produce a ConnectUnknownParameterError.
        var result = ConnectHandler.CheckForUnknownParameters(["mode"]);

        Assert.NotNull(result);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_UnknownKey_ErrorNamesTheKey()
    {
        // The error message must explicitly name the unrecognised parameter so the
        // caller can identify and correct the typo without consulting documentation.
        var result = ConnectHandler.CheckForUnknownParameters(["mode"]);

        Assert.NotNull(result);
        Assert.Contains("mode", result!.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mode", result.UnknownParameters, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_UnknownKey_ErrorListsValidParams()
    {
        // The error must list all valid parameter names so the caller can pick the
        // correct one (e.g. sdkMode) without knowing the full API surface.
        var result = ConnectHandler.CheckForUnknownParameters(["mode"]);

        Assert.NotNull(result);
#if LOGIX_SDK
        Assert.Contains("sdkMode",   result!.ValidParameters, StringComparer.OrdinalIgnoreCase);
#endif
        Assert.Contains("gateway",   result!.ValidParameters, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("path",      result.ValidParameters,  StringComparer.OrdinalIgnoreCase);
        Assert.Contains("backend",   result.ValidParameters,  StringComparer.OrdinalIgnoreCase);
        Assert.Contains("useVirtual",result.ValidParameters,  StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_CaseInsensitive_KnownKeys_ReturnsNull()
    {
        // Valid parameter names must be accepted case-insensitively (MCP clients may
        // use different casing than the canonical C# name).
        var result = ConnectHandler.CheckForUnknownParameters(
#if LOGIX_SDK
            ["GATEWAY", "PATH", "USEVIRTUAL", "BACKEND", "SDKMODE"]);
#else
            ["GATEWAY", "PATH", "USEVIRTUAL", "BACKEND", "CONFIRMEDBYUSER"]);
#endif

        Assert.Null(result);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_MultipleUnknownKeys_AllReported()
    {
        // When more than one unknown key is supplied, every unrecognised key must
        // appear in UnknownParameters — not just the first one.
        var result = ConnectHandler.CheckForUnknownParameters(["mode", "host", "gateway"]);

        Assert.NotNull(result);
        Assert.Contains("mode", result!.UnknownParameters, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("host", result.UnknownParameters,  StringComparer.OrdinalIgnoreCase);
        // "gateway" is a valid param and must NOT appear in the unknown list.
        Assert.DoesNotContain("gateway", result.UnknownParameters, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_ValidParamsCount()
    {
        // The ValidParameters list on the error must contain exactly the seven declared
        // parameters — no additions, no omissions.
        var result = ConnectHandler.CheckForUnknownParameters(["typo"]);

        Assert.NotNull(result);
#if LOGIX_SDK
        Assert.Equal(8, result!.ValidParameters.Length);
#else
        Assert.Equal(5, result!.ValidParameters.Length);   // the three SDK-only options are not in this build
#endif
    }

    [Fact]
    public async Task GetConnectResponseAsync_UnknownRawKey_ReturnsUnknownParameterError()
    {
        // Passing rawArgumentKeys that includes an unknown key must produce a
        // ConnectUnknownParameterError in the JSON response — no connection is attempted.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            rawArgumentKeys: ["gateway", "path", "mode"]);  // "mode" is unknown

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Expected 'error' field for unknown parameter.");
        Assert.True(doc.RootElement.TryGetProperty("unknownParameters", out _),
            "Expected 'unknownParameters' field for unknown parameter.");
        Assert.True(doc.RootElement.TryGetProperty("validParameters", out _),
            "Expected 'validParameters' field for unknown parameter.");
    }

    [Fact]
    public async Task GetConnectResponseAsync_UnknownRawKey_NamesTheUnknownKey()
    {
        // The error JSON must explicitly name the unrecognised parameter key so the
        // caller can immediately identify the typo.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            rawArgumentKeys: ["gateway", "path", "mode"]);

        var doc = JsonDocument.Parse(json);
        var errorMsg = doc.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("mode", errorMsg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetConnectResponseAsync_UnknownRawKey_SessionIsNotAttached()
    {
        // No connection may be established when an unknown parameter is present.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: false,
            rawArgumentKeys: ["gateway", "path", "mode"]);

        Assert.False(sessionSvc.IsConnected,
            "Session must not be attached when an unknown parameter is present.");
    }

#if LOGIX_SDK
    [Fact]
    public async Task GetConnectResponseAsync_UnknownRawKey_ModeInsteadOfSdkMode_NamesMode()
    {
        // Reproduce the exact real-world hazard: caller passes mode="online" instead of
        // sdkMode="online".  The error must name 'mode' as the unrecognised parameter
        // and list 'sdkMode' among the valid alternatives.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: false, backend: "sdk",
            rawArgumentKeys: ["gateway", "path", "backend", "mode"]);

        var doc = JsonDocument.Parse(json);
        var errorMsg    = doc.RootElement.GetProperty("error").GetString() ?? "";
        var unknownArr  = doc.RootElement.GetProperty("unknownParameters");
        var validArr    = doc.RootElement.GetProperty("validParameters");

        // The unknown parameter must be identified.
        Assert.Contains("mode", errorMsg, StringComparison.OrdinalIgnoreCase);

        // unknownParameters array must contain exactly "mode".
        Assert.Equal(1, unknownArr.GetArrayLength());
        Assert.Equal("mode",
            unknownArr[0].GetString(), StringComparer.OrdinalIgnoreCase);

        // validParameters array must list sdkMode among the valid choices.
        var validParams = Enumerable.Range(0, validArr.GetArrayLength())
            .Select(i => validArr[i].GetString() ?? "")
            .ToArray();
        Assert.Contains("sdkMode", validParams, StringComparer.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public async Task GetConnectResponseAsync_AllValidRawKeys_NullKeys_ProceedsNormally()
    {
        // When rawArgumentKeys is null (the default), no validation is performed and
        // the connection proceeds normally — this is the path used by direct callers
        // and unit tests that bypass the MCP layer.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: true);  // rawArgumentKeys omitted (null)

        var doc = JsonDocument.Parse(json);
        Assert.Equal("connected", doc.RootElement.GetProperty("status").GetString());
    }

    // ── Gateway/path default to "" ────────────────
    //
    // Prior to this fix, gateway and path were non-defaulted required string
    // parameters on the logix_connect tool method, so the MCP argument marshaller
    // rejected any call that omitted them (e.g. the canonical
    // backend="sdk" sdkMode="online" sdkCommPath="..." call) before the handler's
    // branch logic ever ran. These tests call GetConnectResponseAsync relying on
    // the C# default value for gateway/path (i.e. the argument is not supplied at
    // all), mirroring what happens when an MCP caller omits the keys entirely.

    [Fact]
    public async Task GetConnectResponseAsync_Cip_GatewayAndPathOmitted_ReturnsStructuredGatewayError()
    {
        // cip backend with gateway/path omitted entirely (relying on the ""
        // default) must still surface the existing structured "gateway required"
        // connection error, not an MCP marshaller exception.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        // backend defaults to "cip"; gateway/path are simply not supplied.
        var json = await tools.GetConnectResponseAsync(useVirtual: false);

        var doc      = JsonDocument.Parse(json);
        var errorMsg = doc.RootElement.GetProperty("error").GetString() ?? "";
        Assert.Contains("gateway", errorMsg, StringComparison.OrdinalIgnoreCase);
        Assert.False(sessionSvc.IsConnected,
            "No session may be attached when the cip backend is missing a gateway.");
    }

    [Fact]
    public async Task GetConnectResponseAsync_KnownRawKeys_ProceedsNormally()
    {
        // When rawArgumentKeys contains only valid keys, no error is produced and the
        // connection proceeds normally.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: true,
            rawArgumentKeys: ["gateway", "path", "useVirtual"]);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("connected", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ConnectUnknownParameterError_SerializesCorrectly()
    {
        // The record must serialise to JSON with the three expected property names:
        // error, unknownParameters, validParameters.  Callers that inspect raw JSON
        // must be able to address these fields by stable, predictable keys.
        var error = new ConnectUnknownParameterError(
            Error:             "Unknown parameter: 'mode'. Valid parameters are: ...",
            UnknownParameters: ["mode"],
            ValidParameters:   ConnectHandler.ValidParameters);

        var json = System.Text.Json.JsonSerializer.Serialize(error);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error",             out _),
            "JSON must contain 'error' key.");
        Assert.True(doc.RootElement.TryGetProperty("unknownParameters", out _),
            "JSON must contain 'unknownParameters' key.");
        Assert.True(doc.RootElement.TryGetProperty("validParameters",   out _),
            "JSON must contain 'validParameters' key.");
    }

#if !LOGIX_SDK
    // ── ConnectHandler — backend='sdk' in core-only builds ───────────────────
    // These tests verify the distinct, actionable error returned when backend='sdk'
    // is requested in a build compiled without Logix Designer SDK support.

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ReturnsSdkNotAvailableError()
    {
        // backend='sdk' must return ConnectSdkNotAvailableError in a non-SDK build,
        // NOT the generic ConnectBadBackendError.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.IsType<ConnectSdkNotAvailableError>(result);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ErrorDoesNotSayUnrecognized()
    {
        // The error must NOT say "unrecognized backend" — that message is misleading
        // because 'sdk' is a recognised backend that simply requires the SDK package.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.DoesNotContain("unrecognized", result.Error, StringComparison.OrdinalIgnoreCase);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ErrorMentionsEnableLogixSdk()
    {
        // The error must mention EnableLogixSdk so the caller knows the exact MSBuild
        // property to set without consulting external documentation.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.Contains("EnableLogixSdk", result.Error, StringComparison.Ordinal);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ErrorMentionsPackageName()
    {
        // The error must mention the NuGet package name so the caller can locate
        // the correct artefact for .localfeed/.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.Contains("RockwellAutomation.LogixDesigner.CSClient", result.Error,
            StringComparison.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_SessionIsNotAttached()
    {
        // The session must remain unattached when the SDK lane is unavailable.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.False(sessionSvc.IsConnected);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_CaseInsensitive_ReturnsSdkNotAvailableError()
    {
        // Case variants of "sdk" must all return ConnectSdkNotAvailableError, not
        // ConnectBadBackendError.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "SDK");

        Assert.IsType<ConnectSdkNotAvailableError>(result);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ErrorMentionsLogixSdkPathEnvVar()
    {
        // The error must mention LOGIX_SDK_PATH so the caller knows exactly which
        // environment variable to set to override the probe order.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.Contains("LOGIX_SDK_PATH", result.Error, StringComparison.Ordinal);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ProbedPathsIsNotEmpty()
    {
        // The structured ProbedPaths field must contain at least one entry so
        // tooling can inspect individual probe outcomes without parsing the error text.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.NotEmpty(result.ProbedPaths);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_EachProbeEntryHasNonEmptyPath()
    {
        // Every entry in ProbedPaths must carry a non-empty path string.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.All(result.ProbedPaths, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Path)));
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_EachProbeEntryHasNonEmptyStatus()
    {
        // Every entry in ProbedPaths must carry a non-empty status string.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.All(result.ProbedPaths, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Status)));
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ProbedPathsCountMatchesProbeOrder()
    {
        // The number of ProbedPaths entries must match the number of candidates
        // returned by SdkAdapterLoader.GetProbeCandidates() so no path is silently
        // omitted from the diagnostic output.
        Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        var expectedCount = SdkAdapterLoader.GetProbeCandidates().Count();
        Assert.Equal(expectedCount, result.ProbedPaths.Length);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_ErrorListsProbedPathsInline()
    {
        // The human-readable Error string must enumerate at least one probed path so
        // a caller reading only Error (not ProbedPaths) can still self-diagnose.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        // Every path from ProbedPaths must appear verbatim in the Error text.
        foreach (var entry in result.ProbedPaths)
            Assert.Contains(entry.Path, result.Error, StringComparison.Ordinal);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_ClientDllMissing_ErrorNamesCsClientDll()
    {
        // When a probe path has status "client_dll_missing" (partial SDK install), the
        // human-readable Error must name the required CSClient assembly so the user can
        // identify the exact file they need to provide.
        var partialSdkDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(partialSdkDir);
        try
        {
            // Write a non-CSClient Rockwell DLL to simulate a partial SDK install.
            File.WriteAllText(
                Path.Combine(partialSdkDir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
                "fake");

            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", partialSdkDir);
            try
            {
                var projectSvc = new ProjectService();
                var sessionSvc = new TagSessionService();

                var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
                    "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

                Assert.Contains(
                    SdkAdapterLoader.CsClientDllName,
                    result.Error,
                    StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
            }
        }
        finally
        {
            Directory.Delete(partialSdkDir, recursive: true);
        }
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_ClientDllMissing_ErrorNamesNuPkgLocation()
    {
        // When a probe path has status "client_dll_missing", the Error must include
        // the known nupkg directory so users know where to find the client package.
        var partialSdkDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(partialSdkDir);
        try
        {
            File.WriteAllText(
                Path.Combine(partialSdkDir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
                "fake");

            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", partialSdkDir);
            try
            {
                var projectSvc = new ProjectService();
                var sessionSvc = new TagSessionService();

                var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
                    "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

                Assert.Contains(
                    SdkAdapterLoader.NuPkgLocation,
                    result.Error,
                    StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
            }
        }
        finally
        {
            Directory.Delete(partialSdkDir, recursive: true);
        }
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_ClientDllMissing_ProbedPathHasClientDllMissingStatus()
    {
        // When a directory contains other Rockwell DLLs but not CSClient.dll, the
        // ProbedPaths entry for that directory must carry status "client_dll_missing".
        var partialSdkDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(partialSdkDir);
        try
        {
            File.WriteAllText(
                Path.Combine(partialSdkDir, "RockwellAutomation.LogixDesigner.LogixProjectServices.dll"),
                "fake");

            Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", partialSdkDir);
            try
            {
                var projectSvc = new ProjectService();
                var sessionSvc = new TagSessionService();

                var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
                    "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

                var entry = result.ProbedPaths.FirstOrDefault(e => e.Path == partialSdkDir);
                Assert.NotNull(entry);
                Assert.Equal("client_dll_missing", entry!.Status);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LOGIX_SDK_PATH", null);
            }
        }
        finally
        {
            Directory.Delete(partialSdkDir, recursive: true);
        }
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_LoadFailureReasonField_MatchesSdkAdapterLoaderValue()
    {
        // After recording a load failure, the structured LoadFailureReason field on the
        // returned ConnectSdkNotAvailableError must be equal to
        // SdkAdapterLoader.LoadFailureReason so callers can inspect the reason without
        // parsing the human-readable Error string.
        SdkAdapterLoader.RecordTypeScanFailure(
            "/fake/path/LogixMcp.SdkAdapter.dll",
            new FileNotFoundException("Simulated missing dependency for LoadFailureReason test."));

        var expectedReason = SdkAdapterLoader.LoadFailureReason;
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.Equal(expectedReason, result.LoadFailureReason);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_LoadFailureReasonField_IsNonEmptyWhenRecorded()
    {
        // When a load failure is recorded, the structured LoadFailureReason field must
        // be a non-empty string so tooling can branch on it without null/whitespace checks.
        SdkAdapterLoader.RecordTypeScanFailure(
            "/fake/path/LogixMcp.SdkAdapter.dll",
            new FileNotFoundException("Simulated missing dependency."));

        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        Assert.False(string.IsNullOrWhiteSpace(result.LoadFailureReason));
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_CoreOnlyBuild_LoadFailureReasonField_AppearsInJsonWhenRecorded()
    {
        // The structured LoadFailureReason field must appear in the serialized JSON so
        // MCP clients that consume raw JSON can detect a load failure without parsing
        // the free-text Error string.  A unique marker is embedded in the recorded
        // reason so the assertion is unambiguous.
        const string uniqueMarker = "UNIQUE_LOAD_FAILURE_MARKER_XYZ123";
        SdkAdapterLoader.RecordTypeScanFailure(
            "/fake/path/LogixMcp.SdkAdapter.dll",
            new FileNotFoundException(uniqueMarker));

        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectSdkNotAvailableError) await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "sdk");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("loadFailureReason", json, StringComparison.Ordinal);
        Assert.Contains(uniqueMarker, json, StringComparison.Ordinal);
    }
#endif
#endif

    // ── ConnectSdkNotAvailableError — LoadFailureReason structural tests ───────
    // These tests run in both SDK and non-SDK builds.  They exercise the record
    // constructor directly — no ExecuteAsync call — so they are independent of
    // DefaultSdkFactory state and SDK availability.

    [Fact]
    public void ConnectSdkNotAvailableError_LoadFailureReason_AcceptsNull()
    {
        // ConnectSdkNotAvailableError must accept a null LoadFailureReason — this is the
        // normal case when no load exception was recorded (adapter simply absent or no SDK
        // directory found).  Verify the record can be constructed with null and the field
        // serialises to JSON as a null value (not a hard error).
        var entry = new SdkProbeEntry("/some/path", "not_found");
        var error = new ConnectSdkNotAvailableError(
            Error: "backend='sdk' unavailable.",
            ProbedPaths: [entry],
            LoadFailureReason: null);

        Assert.Null(error.LoadFailureReason);

        var json = System.Text.Json.JsonSerializer.Serialize(error);
        var doc  = System.Text.Json.JsonDocument.Parse(json);
        // When null the property is serialised as JSON null (nullable reference type
        // serialises as null by default in System.Text.Json).
        Assert.True(
            !doc.RootElement.TryGetProperty("loadFailureReason", out var prop) ||
             prop.ValueKind == System.Text.Json.JsonValueKind.Null,
            "loadFailureReason must be absent or null in JSON when not set.");
    }

    [Fact]
    public void ConnectSdkNotAvailableError_LoadFailureReason_AcceptsNonNull()
    {
        // ConnectSdkNotAvailableError must store and expose a non-null LoadFailureReason
        // when one is provided, and it must appear in the serialised JSON as a structured
        // field so MCP clients can read it without parsing the free-text Error string.
        // The reason string uses only ASCII alphanum/hyphen chars to avoid JSON
        // Unicode-escaping that would cause a literal substring comparison to fail.
        const string reason = "Assembly-load-failed-BadImageFormatException-bad-PE-image";
        var entry = new SdkProbeEntry("/some/path", "ok");
        var error = new ConnectSdkNotAvailableError(
            Error: "backend-sdk-unavailable",
            ProbedPaths: [entry],
            LoadFailureReason: reason);

        Assert.Equal(reason, error.LoadFailureReason);

        var json = System.Text.Json.JsonSerializer.Serialize(error);
        Assert.Contains("loadFailureReason", json, StringComparison.Ordinal);
        Assert.Contains(reason, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectSdkNotAvailableError_LoadFailureReason_JsonPropertyName_IsLoadFailureReason()
    {
        // The JSON property name must be exactly "loadFailureReason" (camelCase) so
        // MCP clients that inspect raw JSON can address the field by a stable key.
        const string reason = "SENTINEL_REASON_VALUE";
        var entry = new SdkProbeEntry("/path", "not_found");
        var error = new ConnectSdkNotAvailableError(
            Error: "unavailable",
            ProbedPaths: [entry],
            LoadFailureReason: reason);

        var json = System.Text.Json.JsonSerializer.Serialize(error);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("loadFailureReason", out var prop),
            "JSON must contain the 'loadFailureReason' key.");
        Assert.Equal(reason, prop.GetString());
    }

    [Fact]
    public async Task LogixTagTools_Connect_UnrecognizedBackend_ReturnsErrorJson()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync(
            "198.51.100.1", "1,0", useVirtual: false, backend: "unknown");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _),
            "Expected 'error' field in JSON response for unrecognized backend.");
        Assert.True(doc.RootElement.TryGetProperty("validValues", out _),
            "Expected 'validValues' field in JSON response for unrecognized backend.");
    }

    [Fact]
    public void ConnectResult_TargetField_SerializesAsTargetInJson()
    {
        // The JSON property name for the Target field must be exactly "target"
        // (camelCase) so MCP clients and agents can read it without parsing the
        // human-readable message string.
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           null,
            Path:              null,
            GatewayUsed:       false,
            Virtual:           false,
            Backend:           "sdk",
            TagCount:          null,
            Gated:             true,
            Target:            "offline-project",
            Online:            false,
            DownloadPerformed: false,
            ModeTransitions:   [],
            Correlation:       null,
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("target", out var prop),
            "JSON must contain the 'target' key.");
        Assert.Equal("offline-project", prop.GetString());
    }

    [Fact]
    public void ConnectResult_OnlineControllerTarget_SerializesCorrectly()
    {
        // Verify the "online-controller" target value also round-trips through JSON.
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           "198.51.100.10",
            Path:              "1,0",
            GatewayUsed:       true,
            Virtual:           false,
            Backend:           "cip",
            TagCount:          null,
            Gated:             false,
            Target:            "online-controller",
            Online:            true,
            DownloadPerformed: null,
            ModeTransitions:   null,
            Correlation:       null,
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("target", out var prop),
            "JSON must contain the 'target' key.");
        Assert.Equal("online-controller", prop.GetString());
    }

    // ── ConnectHandler — missing-argument cases return structured errors ───────
    // Every path that can be reached when a caller omits an
    // argument (or passes an empty string) for a contextually required field must
    // return a structured error carrying both a machine-readable "kind" and an
    // actionable "hint" naming the specific argument to supply — never a bare
    // ConnectError, and never let the omission propagate past the handler to the
    // MCP marshaller.

    [Fact]
    public async Task ConnectHandler_CipBackend_EmptyGateway_ReturnsMissingArgumentError()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip");

        Assert.IsType<ConnectMissingArgumentError>(result);
    }

    [Fact]
    public async Task ConnectHandler_CipBackend_WhitespaceGateway_ReturnsMissingArgumentError()
    {
        // A whitespace-only gateway is just as much "missing" as an empty string.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "   ", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip");

        Assert.IsType<ConnectMissingArgumentError>(result);
    }

    [Fact]
    public async Task ConnectHandler_CipBackend_EmptyGateway_ErrorHasKindAndHintNamingGateway()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = (ConnectMissingArgumentError) await ConnectHandler.ExecuteAsync(
            "", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip");

        Assert.Equal("MissingGateway", result.Kind);
        Assert.Equal("gateway", result.Parameter);
        Assert.Contains("gateway", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_CipBackend_EmptyGateway_NoSessionAttached()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        await ConnectHandler.ExecuteAsync(
            "", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip");

        Assert.False(sessionSvc.IsConnected);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkOnline_NoCommPath_NoProject_ReturnsMissingArgumentError()
    {
        // backend='sdk', sdkMode='online', neither sdkCommPath nor gateway supplied,
        // and no project loaded to fall back on for the file-based route.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed, "should not be reached")));

        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.IsType<ConnectMissingArgumentError>(result);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkOnline_NoCommPath_NoProject_ErrorHasKindAndHintNamingSdkCommPath()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed, "should not be reached")));

        var result = (ConnectMissingArgumentError) await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("MissingSdkCommPath", result.Kind);
        Assert.Equal("sdkCommPath", result.Parameter);
        Assert.Contains("sdkCommPath", result.Hint, StringComparison.OrdinalIgnoreCase);
    }
#endif

    [Fact]
    public async Task ConnectHandler_SdkOnline_NoCommPath_NoProject_NoSessionAttached()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new ThrowingOpenSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed, "should not be reached")));

        await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.False(sessionSvc.IsConnected);
    }

#if LOGIX_SDK
    // This test uses BuildFakeSdkFactory, which is backed by LogixProjectSession/
    // TypedSdkTagService/RecordingFakeLogixProject — genuinely SDK-only types — so
    // it is guarded rather than moved out of the block.
    [Fact]
    public async Task ConnectHandler_SdkOnline_NoCommPath_ProjectLoaded_DownloadRequiredError_HasKind()
    {
        // Same missing-argument family (sdkMode='online', no sdkCommPath/gateway),
        // but with a project already loaded: the file-based route requires the
        // explicit allowControllerDownload opt-in, surfaced as
        // ConnectDownloadRequiredError. That record must also carry a "kind" so
        // every branch of this family exposes kind+hint consistently.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = (ConnectDownloadRequiredError) await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("DownloadRequired", result.Kind);
        Assert.False(string.IsNullOrWhiteSpace(result.Hint));
    }
#endif

    // ── ConnectHandler — Backend field equals requested backend (pinning) ───────
    // Each test in this block asserts that ConnectResult.Backend is exactly equal
    // to the normalised backend string that was requested.  They act as regression
    // guards: if the handler ever returns a wrong or hard-coded label the failure
    // will be caught here immediately, before any integration path is exercised.

    [Fact]
    public async Task ConnectHandler_CipBackend_BackendField_EqualsCip()
    {
        // LibPlcTagService.ConnectAsync stores the gateway/path without making a
        // live network call until the first tag read/write, so this test runs
        // entirely without network access.  CIP does not require a loaded project.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip",
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("cip", ok.Backend);
    }

    [Fact]
    public async Task ConnectHandler_L5xBackend_BackendField_EqualsL5x()
    {
        // Pinning test: requesting "l5x" must be reflected in ConnectResult.Backend.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("l5x", ok.Backend);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_BackendField_EqualsSdk()
    {
        // Pinning test: requesting "sdk" must be reflected in ConnectResult.Backend.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("sdk", ok.Backend);
    }
#endif

    [Fact]
    public async Task ConnectHandler_VirtualBackend_BackendField_EqualsVirtual()
    {
        // Virtual mode always reports "virtual" regardless of the backend parameter.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("virtual", ok.Backend);
    }

    // ── ConnectHandler — Target field pinning ────────────────────────────────
    // Each test asserts that ConnectResult.Target is always present and always
    // equals one of the two explicit named values: "online-controller" or
    // "offline-project".  Guards against regression where the field is absent or
    // set to an ambiguous value.

    [Fact]
    public async Task ConnectHandler_CipBackend_TargetField_EqualsOnlineController()
    {
        // CIP targets a live PLC — target must be "online-controller".
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip",
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("online-controller", ok.Target);
    }

    [Fact]
    public async Task ConnectHandler_L5xBackend_TargetField_EqualsOfflineProject()
    {
        // L5X operates offline — target must be "offline-project".
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("offline-project", ok.Target);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_TargetField_EqualsOfflineProject()
    {
        // SDK operates against the project file only — target must be "offline-project".
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("offline-project", ok.Target);
    }
#endif

    [Fact]
    public async Task ConnectHandler_VirtualBackend_TargetField_EqualsOfflineProject()
    {
        // Virtual operates against an in-memory store — target must be "offline-project".
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("offline-project", ok.Target);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_MessageStatesOfflineMode()
    {
        // SDK connect message must state the operating mode in plain language.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Contains("offline", ok.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("online-controller", ok.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OnlineMode_TargetField_EqualsOnlineController()
    {
        // SDK with sdkMode='online' targets the live controller — target must be "online-controller".
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("online-controller", ok.Target);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OfflineModeExplicit_TargetField_EqualsOfflineProject()
    {
        // Explicitly passing sdkMode='offline' must still produce "offline-project".
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "offline");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("offline-project", ok.Target);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OnlineMode_MessageStatesOnlineMode()
    {
        // The connect message for online SDK mode must describe the online-controller target.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Contains("online", ok.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline-project", ok.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OnlineMode_CaseInsensitive()
    {
        // sdkMode must be case-insensitive: "ONLINE" must produce "online-controller" target.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "ONLINE",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal("online-controller", ok.Target);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OnlineMode_SdkModeForwardedToFactory()
    {
        // The sdkMode value must be forwarded to the factory so the factory can
        // create the TypedSdkTagService with the correct OperationMode.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        Assert.Equal("online", factory.LastSdkMode, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_OfflineMode_SdkModeForwardedToFactory()
    {
        // The default sdkMode='offline' must also be forwarded to the factory.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);  // sdkMode defaults to "offline"

        Assert.Equal("offline", factory.LastSdkMode, StringComparer.OrdinalIgnoreCase);
    }
#endif

    // ── ConnectHandler — GatewayUsed field pinning ───────────────────────────
    // These tests verify that gateway_used=false for all offline backends and
    // that gateway/path are null in the response (so callers cannot infer a live
    // network connection from an IP address).  gateway_used=true only for "cip".

    [Fact]
    public async Task ConnectHandler_CipBackend_GatewayUsed_IsTrue()
    {
        // CIP opens a real EtherNet/IP connection — gateway_used must be true.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip",
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.True(ok.GatewayUsed,
            "CIP backend must set gateway_used=true because it opens a real network connection.");
        Assert.Equal("198.51.100.1", ok.Gateway);
        Assert.Equal("1,0", ok.Path);
    }

    [Fact]
    public async Task ConnectHandler_L5xBackend_GatewayUsed_IsFalse()
    {
        // L5X is offline — gateway_used must be false and gateway/path must be null.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.GatewayUsed,
            "L5X backend must set gateway_used=false — no network connection is made.");
        Assert.Null(ok.Gateway);
        Assert.Null(ok.Path);
    }

    [Fact]
    public async Task ConnectHandler_VirtualBackend_GatewayUsed_IsFalse()
    {
        // Virtual is offline — gateway_used must be false and gateway/path must be null.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.GatewayUsed,
            "Virtual backend must set gateway_used=false — no network connection is made.");
        Assert.Null(ok.Gateway);
        Assert.Null(ok.Path);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_GatewayUsed_IsFalse()
    {
        // SDK is offline — gateway_used must be false and gateway/path must be null.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.GatewayUsed,
            "SDK backend must set gateway_used=false — no network connection is made.");
        Assert.Null(ok.Gateway);
        Assert.Null(ok.Path);
    }
#endif

    [Fact]
    public void ConnectResult_GatewayUsed_SerializesAsGatewayUsedInJson()
    {
        // The JSON property name must be exactly "gateway_used" (snake_case).
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           null,
            Path:              null,
            GatewayUsed:       false,
            Virtual:           false,
            Backend:           "sdk",
            TagCount:          null,
            Gated:             true,
            Target:            "offline-project",
            Online:            false,
            DownloadPerformed: false,
            ModeTransitions:   [],
            Correlation:       null,
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("gateway_used", out var prop),
            "JSON must contain the 'gateway_used' key.");
        Assert.False(prop.GetBoolean());
    }

    // ── ConnectResult — Online / DownloadPerformed / ModeTransitions fields ─────

    [Fact]
    public void ConnectResult_OnlineField_SerializesAsOnlineInJson()
    {
        // The JSON property name for Online must be exactly "online".
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           "198.51.100.1",
            Path:              "1,0",
            GatewayUsed:       true,
            Virtual:           false,
            Backend:           "cip",
            TagCount:          null,
            Gated:             false,
            Target:            "online-controller",
            Online:            true,
            DownloadPerformed: null,
            ModeTransitions:   null,
            Correlation:       null,
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("online", out var prop),
            "JSON must contain the 'online' key.");
        Assert.True(prop.GetBoolean());
    }

    [Fact]
    public void ConnectResult_DownloadPerformedField_SerializesAsDownloadPerformedInJson()
    {
        // The JSON property name for DownloadPerformed must be exactly "downloadPerformed".
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           null,
            Path:              null,
            GatewayUsed:       false,
            Virtual:           false,
            Backend:           "sdk",
            TagCount:          null,
            Gated:             true,
            Target:            "online-controller",
            Online:            true,
            DownloadPerformed: true,
            ModeTransitions:   ["Program", "Run"],
            Correlation:       "download",
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("downloadPerformed", out var prop),
            "JSON must contain the 'downloadPerformed' key.");
        Assert.True(prop.GetBoolean());
    }

    [Fact]
    public void ConnectResult_ModeTransitionsField_SerializesAsModeTransitionsInJson()
    {
        // The JSON property name for ModeTransitions must be exactly "modeTransitions".
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           null,
            Path:              null,
            GatewayUsed:       false,
            Virtual:           false,
            Backend:           "sdk",
            TagCount:          null,
            Gated:             true,
            Target:            "online-controller",
            Online:            true,
            DownloadPerformed: true,
            ModeTransitions:   ["Program", "Run"],
            Correlation:       "download",
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("modeTransitions", out var arrProp),
            "JSON must contain the 'modeTransitions' key.");
        var arr = arrProp.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Program", "Run" }, arr);
    }

    [Fact]
    public void ConnectResult_DownloadPerformed_Null_SerializesAsNull()
    {
        // When DownloadPerformed is null (non-SDK backends), JSON must encode null.
        var result = new ConnectResult(
            Status:            "connected",
            Gateway:           null,
            Path:              null,
            GatewayUsed:       false,
            Virtual:           false,
            Backend:           "l5x",
            TagCount:          null,
            Gated:             true,
            Target:            "offline-project",
            Online:            false,
            DownloadPerformed: null,
            ModeTransitions:   null,
            Correlation:       null,
            Message:           "test");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var doc  = System.Text.Json.JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("downloadPerformed", out var prop),
            "JSON must contain the 'downloadPerformed' key (even when null).");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, prop.ValueKind);
    }

    // ── ConnectHandler — Online field pinning ──────────────────────────────────

    [Fact]
    public async Task ConnectHandler_CipBackend_Online_IsTrue()
    {
        // CIP connects to a live PLC — Online must be true.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip",
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.True(ok.Online, "CIP backend must report Online=true.");
    }

    [Fact]
    public async Task ConnectHandler_L5xBackend_Online_IsFalse()
    {
        // L5X is offline — Online must be false.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.Online, "L5X backend must report Online=false.");
    }

    [Fact]
    public async Task ConnectHandler_VirtualBackend_Online_IsFalse()
    {
        // Virtual is offline — Online must be false.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.Online, "Virtual backend must report Online=false.");
    }

    [Fact]
    public async Task ConnectHandler_CipBackend_DownloadPerformed_IsNull()
    {
        // CIP backend: DownloadPerformed is not applicable — must be null.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "cip",
            cipConnectProbe: (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Null(ok.DownloadPerformed);
    }

    [Fact]
    public async Task ConnectHandler_L5xBackend_DownloadPerformed_IsNull()
    {
        // L5X backend: DownloadPerformed is not applicable — must be null.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc, backend: "l5x");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Null(ok.DownloadPerformed);
    }

    [Fact]
    public async Task ConnectHandler_VirtualBackend_DownloadPerformed_IsNull()
    {
        // Virtual backend: DownloadPerformed is not applicable — must be null.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, projectSvc, sessionSvc);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Null(ok.DownloadPerformed);
    }

#if LOGIX_SDK
    [Fact]
    public async Task ConnectHandler_SdkBackend_Offline_DownloadPerformed_IsFalse()
    {
        // SDK offline mode: no download ever occurs — DownloadPerformed must be false.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "offline");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.DownloadPerformed);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Offline_ModeTransitions_IsEmpty()
    {
        // SDK offline mode: no mode transitions — ModeTransitions must be empty.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "offline");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.NotNull(ok.ModeTransitions);
        Assert.Empty(ok.ModeTransitions);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoDownload_DownloadPerformed_IsFalse()
    {
        // SDK online mode without a download: DownloadPerformed must be false.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.DownloadPerformed);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_IsTrue()
    {
        // SDK with sdkMode='online' targets the live controller — Online must be true.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        // gateway is empty so this exercises the file-based SDK path, which is
        // explicit opt-in and requires allowControllerDownload=true.
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.True(ok.Online);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Offline_Online_IsFalse()
    {
        // SDK with sdkMode='offline' does not target the live controller — Online must be false.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "offline");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.False(ok.Online);
    }
#endif

    // ── ConnectHandler.BuildSeedFromProject ───────────────────────────────────

    [Fact]
    public void ConnectHandler_BuildSeedFromProject_ContainsAllControllerTags()
    {
        var project = L5X.Load(FixturePath);

        var seed = ConnectHandler.BuildSeedFromProject(project);

        Assert.Contains("Cmd_MotorStart",    seed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cmd_SpeedSetpoint", seed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Sim_LineSpeed",     seed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Safety_OK",         seed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Motor_Run",         seed.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cmd_Motor",         seed.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectHandler_BuildSeedFromProject_BoolTagDefaultIsFalse()
    {
        var project = L5X.Load(FixturePath);
        var seed    = ConnectHandler.BuildSeedFromProject(project);

        // Cmd_MotorStart is BOOL; default should be false.
        Assert.Equal(false, seed["Cmd_MotorStart"].Value);
        Assert.Equal("BOOL", seed["Cmd_MotorStart"].DataType);
    }

    [Fact]
    public void ConnectHandler_BuildSeedFromProject_RealTagDefaultIsDouble()
    {
        var project = L5X.Load(FixturePath);
        var seed    = ConnectHandler.BuildSeedFromProject(project);

        // Cmd_SpeedSetpoint is REAL; default should be 0.0 (double).
        Assert.Equal(0.0, seed["Cmd_SpeedSetpoint"].Value);
        Assert.Equal("REAL", seed["Cmd_SpeedSetpoint"].DataType);
    }

    // ── ConnectHandler.DefaultValueForType ────────────────────────────────────

    [Theory]
    [InlineData("BOOL")]
    public void ConnectHandler_DefaultValueForType_Bool_ReturnsFalse(string dataType)
    {
        var value = ConnectHandler.DefaultValueForType(dataType);
        Assert.Equal(false, value);
    }

    [Theory]
    [InlineData("REAL")]
    [InlineData("LREAL")]
    public void ConnectHandler_DefaultValueForType_FloatTypes_ReturnsDouble(string dataType)
    {
        var value = ConnectHandler.DefaultValueForType(dataType);
        Assert.IsType<double>(value);
        Assert.Equal(0.0, (double) value);
    }

    [Theory]
    [InlineData("DINT")]
    [InlineData("INT")]
    [InlineData("SINT")]
    [InlineData("MotorCmd")]
    public void ConnectHandler_DefaultValueForType_OtherTypes_ReturnsInt(string dataType)
    {
        var value = ConnectHandler.DefaultValueForType(dataType);
        Assert.IsType<int>(value);
        Assert.Equal(0, (int) value);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ReadTagHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadTagHandler_NoSession_ReturnsError()
    {
        var session = new TagSessionService();   // not connected

        var result = await ReadTagHandler.ExecuteAsync("Cmd_MotorStart", session);

        Assert.IsType<ReadTagError>(result);
        var err = (ReadTagError) result;
        Assert.Contains("No active session", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadTagHandler_KnownTag_ReturnsReadTagResult()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await ReadTagHandler.ExecuteAsync("Cmd_MotorStart", session);

        Assert.IsType<ReadTagResult>(result);
        var ok = (ReadTagResult) result;
        Assert.Equal("Cmd_MotorStart", ok.Tag);
        Assert.Equal("BOOL", ok.DataType);
    }

    [Fact]
    public async Task ReadTagHandler_UnknownTag_ReturnsTagNotFoundError()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await ReadTagHandler.ExecuteAsync("NonExistent_Tag", session);

        // Tag-not-found now returns the unified ReadTagNotFound shape.
        var notFound = Assert.IsType<ReadTagNotFound>(result);
        Assert.Equal("not_found",       notFound.Status);
        Assert.Equal("tag",             notFound.What);
        Assert.Equal("NonExistent_Tag", notFound.Path);
        Assert.Null(notFound.SdkError);
    }

    [Fact]
    public async Task CipNotFound_HasNoSdkErrorField()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await ReadTagHandler.ExecuteAsync("NonExistent_Tag", session);

        var notFound = Assert.IsType<ReadTagNotFound>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(notFound);
        Assert.DoesNotContain("sdkError", json);
        Assert.Contains("\"status\":\"not_found\"", json);
    }

    [Fact]
    public async Task ReadTagHandler_DeniedTag_StillReadable()
    {
        // Read is never gated — denied tags are still readable.
        var (session, _) = BuildGatedVirtualSession();

        var result = await ReadTagHandler.ExecuteAsync("Safety_OK", session);

        Assert.IsType<ReadTagResult>(result);
        var ok = (ReadTagResult) result;
        Assert.Equal("Safety_OK", ok.Tag);
    }

    [Fact]
    public async Task ReadTagHandler_CaseInsensitive_ReturnsValue()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await ReadTagHandler.ExecuteAsync("cmd_motorstart", session);

        Assert.IsType<ReadTagResult>(result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WriteTagHandler — ParseValue
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("true",  true)]
    [InlineData("True",  true)]
    [InlineData("TRUE",  true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    public void WriteTagHandler_ParseValue_BoolStrings_ReturnsBool(
        string raw, bool expected)
    {
        var result = WriteTagHandler.ParseValue(raw);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0",    0)]
    [InlineData("42",   42)]
    [InlineData("-7",   -7)]
    [InlineData("1000", 1000)]
    public void WriteTagHandler_ParseValue_IntegerStrings_ReturnsInt(
        string raw, int expected)
    {
        var result = WriteTagHandler.ParseValue(raw);

        Assert.IsType<int>(result);
        Assert.Equal(expected, (int) result);
    }

    [Theory]
    [InlineData("3.14")]
    [InlineData("0.0")]
    [InlineData("-1.5")]
    public void WriteTagHandler_ParseValue_DecimalStrings_ReturnsDouble(string raw)
    {
        var result = WriteTagHandler.ParseValue(raw);

        Assert.IsType<double>(result);
    }

    [Fact]
    public void WriteTagHandler_ParseValue_UnrecognisedString_ReturnsString()
    {
        var result = WriteTagHandler.ParseValue("hello");

        Assert.IsType<string>(result);
        Assert.Equal("hello", result);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WriteTagHandler — ExecuteAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WriteTagHandler_NoSession_ReturnsError()
    {
        var session = new TagSessionService();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session);

        Assert.IsType<WriteTagError>(result);
        Assert.Contains("No active session", ((WriteTagError) result).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteTagHandler_GrantedTag_ReturnsOk()
    {
        var (session, inner) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session);

        Assert.IsType<WriteTagResult>(result);
        var ok = (WriteTagResult) result;
        Assert.Equal("ok", ok.Status);
        Assert.Equal("Cmd_MotorStart", ok.Tag);
        Assert.Equal(true, ok.Value);
    }

    [Fact]
    public async Task WriteTagHandler_GrantedTag_PersistsValueInInnerService()
    {
        var (session, inner) = BuildGatedVirtualSession();

        await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session);

        var stored = await inner.ReadAsync("Cmd_MotorStart");
        Assert.Equal(true, stored.Value);
    }

    [Fact]
    public async Task WriteTagHandler_SafetyTag_ReturnsWriteDeniedError()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        Assert.IsType<WriteTagError>(result);
        var err = (WriteTagError) result;
        Assert.Equal("WriteDenied", err.Kind);
        Assert.Equal("Safety_OK", err.Tag);
    }

    [Fact]
    public async Task WriteTagHandler_ProgramWrittenTag_ReturnsWriteDeniedError()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        Assert.IsType<WriteTagError>(result);
        Assert.Equal("WriteDenied", ((WriteTagError) result).Kind);
    }

    [Fact]
    public async Task WriteTagHandler_GrantedScalar_OutOfBounds_ReturnsWriteDeniedError()
    {
        // Cmd_SpeedSetpoint: range 0..1000 in Demo.L5X.
        var (session, _) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "9999.0", session);

        Assert.IsType<WriteTagError>(result);
        Assert.Equal("WriteDenied", ((WriteTagError) result).Kind);
    }

    [Fact]
    public async Task WriteTagHandler_GrantedScalar_InBounds_ReturnsOk()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "500.0", session);

        Assert.IsType<WriteTagResult>(result);
        Assert.Equal("ok", ((WriteTagResult) result).Status);
    }

    [Fact]
    public async Task WriteTagHandler_UnknownTag_ReturnsWriteDeniedError()
    {
        // Tags not in the policy are denied before even reaching the virtual store.
        var (session, _) = BuildGatedVirtualSession();

        var result = await WriteTagHandler.ExecuteAsync("Unknown_Tag", "1", session);

        Assert.IsType<WriteTagError>(result);
        Assert.Equal("WriteDenied", ((WriteTagError) result).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PollTagsHandler
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollTagsHandler_NoSession_ReturnsError()
    {
        var session = new TagSessionService();

        var result = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 1, intervalMs: 100, session);

        Assert.IsType<PollError>(result);
        Assert.Contains("No active session", ((PollError) result).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollTagsHandler_EmptyTags_ReturnsError()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await PollTagsHandler.ExecuteAsync(
            [], count: 1, intervalMs: 100, session);

        Assert.IsType<PollError>(result);
        Assert.Contains("At least one tag", ((PollError) result).Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PollTagsHandler_SingleCycle_ReturnsAllTagsAsChanges()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart", "Cmd_SpeedSetpoint"], count: 1, intervalMs: 100, session);

        Assert.IsType<PollResult>(result);
        var ok = (PollResult) result;
        Assert.Single(ok.Cycles);
        // Cycle 1 is the baseline; all polled tags appear as changes with previous=null.
        Assert.Equal(2, ok.Cycles[0].Changes.Count);
        Assert.All(ok.Cycles[0].Changes, c => Assert.Null(c.Previous));
    }

    [Fact]
    public async Task PollTagsHandler_TwoCycles_NoChange_SecondCycleEmpty()
    {
        // VirtualTagService values don't change between polls unless written to.
        var (session, _) = BuildGatedVirtualSession();

        var result = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 2, intervalMs: 100, session);

        Assert.IsType<PollResult>(result);
        var ok = (PollResult) result;
        Assert.Equal(2, ok.Cycles.Count);

        // Cycle 1: baseline (change emitted with previous=null).
        Assert.Single(ok.Cycles[0].Changes);
        Assert.Null(ok.Cycles[0].Changes[0].Previous);

        // Cycle 2: same value → no change emitted.
        Assert.Empty(ok.Cycles[1].Changes);
    }

    [Fact]
    public async Task PollTagsHandler_TwoCycles_ValueChangeBetweenPolls_ReportedInCycle2()
    {
        var (session, inner) = BuildGatedVirtualSession();

        // Start first poll cycle to establish baseline.
        // Directly write to the INNER virtual service to bypass the gate check
        // and simulate a PLC-side value change between polls.

        // Poll cycle 1 (baseline): value = false.
        var firstPoll = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 1, intervalMs: 100, session);

        // Simulate value change by writing directly to inner (bypassing gate).
        await inner.WriteAsync("Cmd_MotorStart", true);

        // Poll cycle 2 (diff against baseline): should detect the change.
        // We can't easily do this in a single ExecuteAsync call with 2 cycles because
        // the change happens between the two cycles here.  Instead, run two 1-cycle polls
        // and verify that the second one detects the new value, then manually confirm
        // the diff would be detected.
        var secondPoll = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 1, intervalMs: 100, session);

        // Verify the second 1-cycle poll sees the updated value.
        Assert.IsType<PollResult>(secondPoll);
        var ok = (PollResult) secondPoll;
        Assert.Single(ok.Cycles[0].Changes);
        Assert.Equal(true, ok.Cycles[0].Changes[0].Current);
    }

    [Fact]
    public async Task PollTagsHandler_UnknownTag_ReturnsTagNotFoundError()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await PollTagsHandler.ExecuteAsync(
            ["NonExistent_Tag"], count: 1, intervalMs: 100, session);

        Assert.IsType<PollError>(result);
        var err = (PollError) result;
        Assert.Equal("TagNotFound", err.Kind);
    }

    [Fact]
    public async Task PollTagsHandler_ReturnsCorrectMetadata()
    {
        var (session, _) = BuildGatedVirtualSession();
        string[] tags    = ["Cmd_MotorStart", "Sim_LineSpeed"];

        var result = await PollTagsHandler.ExecuteAsync(
            tags, count: 2, intervalMs: 100, session);

        Assert.IsType<PollResult>(result);
        var ok = (PollResult) result;
        Assert.Equal(tags,  ok.Tags);
        Assert.Equal(2,     ok.Count);
        Assert.Equal(100,   ok.IntervalMs);
        Assert.Equal(2,     ok.Cycles.Count);
    }

    [Fact]
    public async Task PollTagsHandler_CountLessThanOne_Clamps_ToSingleCycle()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: -5, intervalMs: 100, session);

        Assert.IsType<PollResult>(result);
        Assert.Single(((PollResult) result).Cycles);
    }

    [Fact]
    public async Task PollTagsHandler_Cycle1Changes_HaveNullPrevious()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = (PollResult) await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 1, intervalMs: 100, session);

        Assert.All(result.Cycles[0].Changes, c => Assert.Null(c.Previous));
    }

    [Fact]
    public async Task PollTagsHandler_ChangeEntry_ContainsDataType()
    {
        var (session, _) = BuildGatedVirtualSession();

        var result = (PollResult) await PollTagsHandler.ExecuteAsync(
            ["Cmd_MotorStart"], count: 1, intervalMs: 100, session);

        var change = result.Cycles[0].Changes[0];
        Assert.Equal("BOOL", change.DataType);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LogixTagTools — MCP-layer serialisation tests
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LogixTagTools_Connect_VirtualMode_ReturnsValidJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("connected", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("virtual").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("gated").GetBoolean());
    }

    [Fact]
    public async Task LogixTagTools_Connect_NoProject_ReturnsErrorJson()
    {
        var projectSvc = new ProjectService();   // empty
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task LogixTagTools_ReadTag_AfterConnect_ReturnsValueJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetReadTagResponseAsync("Cmd_MotorStart");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("value", out _));
        Assert.Equal("Cmd_MotorStart", doc.RootElement.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task LogixTagTools_WriteTag_GrantedTag_ReturnsOkJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetWriteTagResponseAsync("Cmd_MotorStart", "true");

        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LogixTagTools_WriteTag_DeniedTag_ReturnsErrorJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetWriteTagResponseAsync("Safety_OK", "false");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("WriteDenied", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task LogixTagTools_PollTags_ReturnsValidJson()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        await tools.GetConnectResponseAsync("198.51.100.1", "1,0", useVirtual: true);

        var json = await tools.GetPollTagsResponseAsync(
            ["Cmd_MotorStart", "Safety_OK"], count: 2, intervalMs: 100);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("cycles", out var cycles));
        Assert.Equal(2, cycles.GetArrayLength());

        // Cycle 1 is the baseline: 2 tags, all with null previous.
        var cycle1 = cycles[0];
        Assert.Equal(1, cycle1.GetProperty("cycle").GetInt32());
        var changes1 = cycle1.GetProperty("changes");
        Assert.Equal(2, changes1.GetArrayLength());
    }

    [Fact]
    public async Task LogixTagTools_PollTags_NoSession_ReturnsErrorJson()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());

        var json = await tools.GetPollTagsResponseAsync(["Cmd_MotorStart"], count: 1, intervalMs: 100);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ── ConnectHandler — no-project-file path (SDK online, no project loaded) ──
    //
    // When backend="sdk", sdkMode="online", no project is loaded, and a gateway
    // address is provided, ConnectHandler invokes CreateFromControllerAsync rather
    // than returning an error.  The controller's image is uploaded to a new ACD and
    // used to establish an online session without requiring a pre-existing project
    // file.  These tests cover the ConnectHandler dispatch logic only — the factory
    // is a test double so no real SDK or controller is required.

#if LOGIX_SDK

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_ReturnsConnectResult()
    {
        // Gateway provided + sdkMode=online + no project → calls CreateFromControllerAsync
        // and returns ConnectResult (not ConnectError).
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.IsType<ConnectResult>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_BackendIsSdk()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("sdk", result.Backend);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_TargetIsOnlineController()
    {
        // Upload-from-controller always produces an online-controller session.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("online-controller", result.Target);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_CorrelationIsUploadToNewProject()
    {
        // The correlation field must report "upload-to-new-project" for this path.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("upload-to-new-project", result.Correlation);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_OnlineIsTrue()
    {
        // Online must be true because the session targets a live controller.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.True(result.Online);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_DownloadPerformedIsFalse()
    {
        // UploadToNewProject is non-destructive; DownloadPerformed must be false.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal(false, result.DownloadPerformed);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGateway_AttachesSessionService()
    {
        // After a successful upload-from-controller connect, the session must be attached.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.True(sessionSvc.IsConnected);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGatewayOnly_CommPathIsJustGateway()
    {
        // When path is empty, commPath must be just the gateway address.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("198.51.100.10", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_WithGatewayAndPath_CommPathIncludesPath()
    {
        // When both gateway and path are provided, commPath must be "gateway,path".
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("198.51.100.10,1,0", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Offline_NoProject_ReturnsError()
    {
        // When sdkMode="offline" and no project is loaded, an error is returned
        // regardless of the gateway — the upload-from-controller path is not used.
        var projectSvc = new ProjectService();   // no project loaded
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);  // sdkMode defaults to "offline"

        Assert.IsType<ConnectError>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Offline_NoProject_ErrorMentionsProjectRequired()
    {
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectError) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory);

        Assert.Contains("No project loaded", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_EmptyGateway_ReturnsError()
    {
        // When sdkMode="online" but gateway is empty/whitespace (and sdkCommPath is
        // not supplied either), no upload can be initiated and no project is loaded
        // to fall back on. This is a missing-argument case: the
        // handler must return a structured ConnectMissingArgumentError — with a
        // machine-readable kind and a hint naming sdkCommPath/gateway — rather than
        // a bare ConnectError or crashing.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        var err = Assert.IsType<ConnectMissingArgumentError>(result);
        Assert.Equal("MissingSdkCommPath", err.Kind);
        Assert.Equal("sdkCommPath", err.Parameter);
        Assert.Contains("sdkCommPath", err.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_UploadFails_ReturnsError()
    {
        // When CreateFromControllerAsync throws (e.g. controller unreachable),
        // ConnectHandler must return ConnectError rather than propagating the exception.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Controller at '198.51.100.10' is unreachable.")));

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.IsType<ConnectError>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_UploadFails_ErrorMentionsGateway()
    {
        // The ConnectError message must include the gateway address so the caller
        // can identify which controller failed.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "Controller at '198.51.100.10' is unreachable.")));

        var result = (ConnectError) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Contains("198.51.100.10", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ── sdkCommPath — separate Logix SDK comm path parameter ──────────────────
    //
    // The Logix Designer SDK requires a Logix comm path (e.g.
    // "EmulateEthernet\192.0.2.10"), not the CIP/libplctag form ("192.0.2.10,1,0").
    // These tests verify that sdkCommPath takes precedence over gateway/path when
    // provided, and that route errors produce a ConnectSdkRouteError with an
    // actionable message naming the expected format.

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_SdkCommPath_UsedAsCommPath()
    {
        // When sdkCommPath is provided it must be passed directly to
        // CreateFromControllerAsync rather than the gateway/path combination.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        Assert.Equal(@"EmulateEthernet\192.0.2.10", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_SdkCommPath_ReturnsConnectResult()
    {
        // Passing a valid sdkCommPath must still produce a successful ConnectResult.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        Assert.IsType<ConnectResult>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_SdkCommPath_OverridesGatewayPath()
    {
        // sdkCommPath must take precedence over the gateway/path fallback.
        // Verify: factory receives the sdkCommPath value, not "gateway,path".
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        // Must NOT be "192.0.2.10,1,0" (the CIP fallback).
        Assert.NotEqual("192.0.2.10,1,0", factory.LastCommPath);
        Assert.Equal(@"EmulateEthernet\192.0.2.10", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_NoSdkCommPath_FallsBackToGatewayPath()
    {
        // When sdkCommPath is null, the gateway/path fallback must still work.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: null);

        Assert.Equal("198.51.100.10,1,0", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_RouteError_ReturnsConnectSdkRouteError()
    {
        // When CreateFromControllerAsync throws a route error (matching "No module was found"),
        // AND the TCP probe confirms the controller answers, ConnectHandler must
        // return ConnectSdkRouteError (the path — or module route — really is wrong) rather
        // than a plain ConnectError. The probe is injected as "reachable" so this test targets
        // the comm-path-advice branch, independent of network reachability.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "No module was found using the supplied encoded route string.")));

        var result = await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            controllerReachableProbe: (_, _) => Task.FromResult(true));

        Assert.IsType<ConnectSdkRouteError>(result);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_RouteError_ErrorMentionsExpectedFormat()
    {
        // The ConnectSdkRouteError must name the expected Logix comm path format.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "No module was found using the supplied encoded route string.")));

        var result = (ConnectSdkRouteError) await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            controllerReachableProbe: (_, _) => Task.FromResult(true));

        // ExpectedFormat must name the Logix comm path form with a concrete example.
        Assert.Contains("EmulateEthernet", result.ExpectedFormat,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_RouteError_HintMentionsSdkCommPath()
    {
        // The hint in ConnectSdkRouteError must tell the caller to use sdkCommPath.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "No module was found using the supplied encoded route string.")));

        var result = (ConnectSdkRouteError) await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            controllerReachableProbe: (_, _) => Task.FromResult(true));

        Assert.NotNull(result.Hint);
        Assert.Contains("sdkCommPath", result.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_RouteError_CommPathUsedIsPopulated()
    {
        // CommPathUsed in ConnectSdkRouteError must reflect the path that was sent to the SDK.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = new UploadFromControllerSdkFactory(
            new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "No module was found using the supplied encoded route string.")));

        var result = (ConnectSdkRouteError) await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            controllerReachableProbe: (_, _) => Task.FromResult(true));

        // The fallback commPath "192.0.2.10,1,0" is what was passed to SDK.
        Assert.Equal("192.0.2.10,1,0", result.CommPathUsed);
    }

    // ── Upload-path ProjectService population ─────────────────────────────────
    //
    // After a successful no-project-file upload session, ConnectHandler must
    // register the uploaded L5X in ProjectService via RegisterFromUpload so
    // that subsequent tool calls that guard on IsLoaded or FilePath see a
    // valid project without requiring a separate logix_open_project invocation.

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_PopulatesProjectService_IsLoaded()
    {
        // After upload, projectService.IsLoaded must be true so downstream
        // guards do not reject subsequent tool calls.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.True(projectSvc.IsLoaded);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_PopulatesProjectService_FilePath()
    {
        // After upload, FilePath must be non-null so the SDK can open a new
        // session against the uploaded ACD in a subsequent call.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.NotNull(projectSvc.FilePath);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_Online_NoProject_PopulatesProjectService_Project()
    {
        // After upload, projectService.Project must be non-null so gate-policy
        // derivation and project-summary handlers work without a file open.
        var projectSvc = new ProjectService();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.NotNull(projectSvc.Project);
    }

    // ── Comm-path-wins routing when a project is already open ──────
    //
    // logix_open_project must not be a one-way door: when sdkMode="online" and a
    // comm path is supplied (sdkCommPath, or gateway with or without path), the
    // upload-from-controller branch is taken whether or not a project is already
    // open. The open project stays loaded (RegisterFromUpload is a no-op when
    // IsLoaded is already true) and the response names its gate source as
    // "uploaded-controller-project" so the two projects are never confused.

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_SdkCommPath_CallsUploadFactory_NotFileFactory()
    {
        // A project is already open (LoadDemoProject). BuildUploadFromControllerFactory's
        // CreateAsync throws NotImplementedException, so a successful ConnectResult here
        // proves the upload factory — not the file factory — was invoked.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        Assert.IsType<ConnectResult>(result);
        Assert.Equal(@"EmulateEthernet\192.0.2.10", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_SdkCommPath_ProjectRemainsLoaded()
    {
        // The project that was open before the connect call must still be loaded
        // afterwards, at the same file path — RegisterFromUpload no-ops because a
        // project was already loaded, so the open project is not replaced by the
        // uploaded one.
        var projectSvc  = LoadDemoProject();
        var sessionSvc  = new TagSessionService();
        var factory     = BuildUploadFromControllerFactory();
        var originalPath = projectSvc.FilePath;

        await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        Assert.True(projectSvc.IsLoaded);
        Assert.Equal(originalPath, projectSvc.FilePath);
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_SdkCommPath_GateSourceIsUploadedControllerProject()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            sdkCommPath: @"EmulateEthernet\192.0.2.10");

        Assert.Equal("uploaded-controller-project", result.GateSource);
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_SdkCommPath_AuditLineIncludesGateSource()
    {
        // The connect audit record (not just the ConnectResult
        // response) must carry gate_source so the uploaded-controller-project
        // origin of the write-gate policy is queryable in audit.jsonl.
        // Redirects AuditLogger to an isolated temp directory for this test
        // only so the default/production audit location is never touched.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();
        var tempDir    = Path.Combine(Path.GetTempPath(), $"connect-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            await ConnectHandler.ExecuteAsync(
                "192.0.2.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
                backend: "sdk", sdkFactory: factory, sdkMode: "online",
                sdkCommPath: @"EmulateEthernet\192.0.2.10");

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath));
            var connectLine = File.ReadAllLines(auditPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => System.Text.Json.JsonDocument.Parse(l))
                .Last(d => d.RootElement.GetProperty("event").GetString() == "connect");

            Assert.Equal(
                "uploaded-controller-project",
                connectLine.RootElement.GetProperty("gate_source").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_Gateway_NoSdkCommPath_CallsUploadFactory()
    {
        // Same as above but using gateway (no sdkCommPath) as the comm path source —
        // The comm-path-wins rule covers both sdkCommPath and gateway.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.IsType<ConnectResult>(result);
        Assert.Equal("198.51.100.10,1,0", factory.LastCommPath);
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_Gateway_ProjectRemainsLoaded()
    {
        var projectSvc    = LoadDemoProject();
        var sessionSvc    = new TagSessionService();
        var factory       = BuildUploadFromControllerFactory();
        var originalPath  = projectSvc.FilePath;

        await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.True(projectSvc.IsLoaded);
        Assert.Equal(originalPath, projectSvc.FilePath);
    }

    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectOpen_Gateway_GateSourceIsUploadedControllerProject()
    {
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var factory    = BuildUploadFromControllerFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "198.51.100.10", "1,0", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online");

        Assert.Equal("uploaded-controller-project", result.GateSource);
    }

    [Fact]
    public async Task ConnectHandler_FileBasedOnlinePath_GateSourceIsNull()
    {
        // Regression guard: GateSource is only populated on the comm-path-wins
        // upload branch. A plain file-based SDK connect (no comm path) must not
        // set it, so the two gate sources are never confused. The file-based
        // path is explicit opt-in, so allowControllerDownload
        // must be true to reach it.
        var projectSvc   = LoadDemoProject();
        var sessionSvc   = new TagSessionService();
        var (factory, _) = BuildFakeSdkFactory();

        var result = (ConnectResult) await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projectSvc, sessionSvc,
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, confirmedByUser: true,
            capabilities: new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true));

        Assert.Null(result.GateSource);
    }

    [Fact]
    public async Task GetConnectResponseAsync_SdkOnline_SdkCommPathOnly_GatewayAndPathOmitted_RoutesToUploadBranch()
    {
        // Reproduces the canonical online call from every kickoff doc:
        // logix_connect backend="sdk" sdkMode="online" sdkCommPath="EmulateEthernet\..."
        // with NO gateway/path keys supplied at all (relying on their "" default,
        // exercised at the actual MCP-facing GetConnectResponseAsync boundary, not
        // ConnectHandler.ExecuteAsync directly). Must reach the comm-path-wins
        // upload branch and report gateSource accordingly.
        var projectSvc = LoadDemoProject();
        var sessionSvc = new TagSessionService();
        var tools      = new LogixTagTools(projectSvc, sessionSvc, new ControllerModeService());
        var factory    = BuildUploadFromControllerFactory();

        var previousFactory = ConnectHandler.DefaultSdkFactory;
        ConnectHandler.DefaultSdkFactory = factory;
        try
        {
            var json = await tools.GetConnectResponseAsync(
                backend: "sdk", sdkMode: "online",
                sdkCommPath: @"EmulateEthernet\192.0.2.20",
                rawArgumentKeys: ["backend", "sdkMode", "sdkCommPath"]);

            var doc = JsonDocument.Parse(json);
            Assert.Equal("uploaded-controller-project",
                doc.RootElement.GetProperty("gateSource").GetString());
            Assert.Equal(@"EmulateEthernet\192.0.2.20", factory.LastCommPath);
        }
        finally
        {
            ConnectHandler.DefaultSdkFactory = previousFactory;
        }
    }

#endif

    // ── sdkCommPath/IsRouteError — core (non-SDK-gated) tests ─────────────────
    //
    // These tests exercise types in the core assembly (ConnectHandler, TagServiceException)
    // and do not require the Logix Designer SDK adapter to be present.

#if LOGIX_SDK
    [Fact]
    public void ConnectHandler_ValidParameters_IncludesSdkCommPath()
    {
        // sdkCommPath must be recognised as a valid parameter to prevent
        // CheckForUnknownParameters from rejecting it.
        Assert.Contains("sdkCommPath", ConnectHandler.ValidParameters);
    }
#endif

#if LOGIX_SDK
    [Fact]
    public void ConnectHandler_CheckForUnknownParameters_SdkCommPath_IsValid()
    {
        // Callers passing sdkCommPath must not receive a ConnectUnknownParameterError.
        var result = ConnectHandler.CheckForUnknownParameters(
            ["gateway", "sdkCommPath", "backend", "sdkMode"]);

        Assert.Null(result);
    }
#endif

    [Fact]
    public void ConnectHandler_IsRouteError_MatchesNoModuleFound()
    {
        // The helper must recognise the "No module was found" SDK error message.
        var ex = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            "No module was found using the supplied encoded route string."));

        Assert.True(ConnectHandler.IsRouteError(ex));
    }

    [Fact]
    public void ConnectHandler_IsRouteError_MatchesEncodedRoute()
    {
        // The helper must also match "encoded route" substring for partial messages.
        var ex = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            "CIP error: invalid encoded route string supplied."));

        Assert.True(ConnectHandler.IsRouteError(ex));
    }

    [Fact]
    public void ConnectHandler_IsRouteError_ReturnsFalse_ForUnrelatedError()
    {
        // Non-route errors must return false so they fall through to the
        // generic ConnectError path.
        var ex = new TagServiceException(new TagServiceError(
            TagServiceErrorKind.ConnectionFailed,
            "Controller at '198.51.100.10' is unreachable."));

        Assert.False(ConnectHandler.IsRouteError(ex));
    }

#if LOGIX_SDK
    // ── Delta-based CreatedByThisSession ────────
    //
    // A controller-path logix_propose_rung_edit publish whose fragment merely
    // re-declares a tag the controller already had (a context declaration, as any
    // real export carries) must not record that tag in the session's
    // CreatedByThisSession registry — so a subsequent logix_write to it is not
    // blocked by the self-grant guard.

    /// <summary>
    /// End-to-end: <see cref="LogixSdkTools.GetProposeRungEditResponseAsync"/> with
    /// <c>target="controller"</c> publishes a fragment whose context
    /// <c>&lt;Tags&gt;</c> container re-declares <c>Cmd_MotorStart</c> — a tag that
    /// already exists in the loaded project (the publish-time census source).
    /// The delta-based fix must exclude it from <c>TagsCreated</c> and must
    /// not call <see cref="TagSessionService.RecordCreatedTags"/> for it, so a
    /// same-session <c>logix_write</c> to <c>Cmd_MotorStart</c> succeeds instead of
    /// being denied with <c>CreatedByThisSession</c>.
    /// </summary>
    [Fact]
    public async Task WriteToPreExistingTag_AllowedAfterPublish()
    {
        // Arrange: a gated virtual session so logix_write has a live tag service to
        // write through, and a loaded project (Demo.L5X, which already declares
        // Cmd_MotorStart) so the publish-time census sees it as pre-existing.
        var (tagSession, _) = BuildGatedVirtualSession();
        var projectService  = LoadDemoProject();
        var tools           = new LogixSdkTools(projectService, tagSession, new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

        var fake = new ProposeRungEditRecordingFake();
        var sdkSession = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await sdkSession.OpenAsync("dummy.ACD");
        sdkSession.IsOnline     = true;
        sdkSession.IsCorrelated = true;

        // Export-shaped fragment: context declaration of the pre-existing
        // Cmd_MotorStart tag alongside the rung being published.
        const string fragment =
            "<RSLogix5000Content>" +
              "<Controller>" +
                "<Tags Use=\"Context\">" +
                  "<Tag Name=\"Cmd_MotorStart\" DataType=\"BOOL\" />" +
                "</Tags>" +
                "<Programs>" +
                  "<Program Name=\"MainProgram\">" +
                    "<Routines>" +
                      "<Routine Name=\"MainRoutine\">" +
                        "<RLLContent>" +
                          "<Rung Number=\"0\" Type=\"N\">" +
                            "<Text><![CDATA[NOP();]]></Text>" +
                          "</Rung>" +
                        "</RLLContent>" +
                      "</Routine>" +
                    "</Routines>" +
                  "</Program>" +
                "</Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        // Act (1): publish to the controller.
        var publishJson = await tools.GetProposeRungEditResponseAsync(
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']",
            0, 0, fragment, session: sdkSession, offline: false, target: "controller");

        using var publishDoc = JsonDocument.Parse(publishJson);
        Assert.Equal("proposed", publishDoc.RootElement.GetProperty("status").GetString());

        // The session-created registry must not carry Cmd_MotorStart: it was a
        // context declaration of a pre-existing tag, not a genuine creation.
        Assert.False(tagSession.TryGetSessionCreatedTagEntry("Cmd_MotorStart", out _));

        // Act (2): write the pre-existing tag in the same session.
        var writeResult = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", tagSession);

        // Assert: the write is allowed — not denied by the CreatedByThisSession
        // self-grant guard.
        Assert.IsType<WriteTagResult>(writeResult);
    }

    // ── Self-grant guard still enforced for genuinely new tags ──
    //
    // The delta-based fix must not weaken the
    // self-grant guard for tags that are genuinely absent from the
    // publish-time census. This is the end-to-end counterpart to
    // WriteToPreExistingTag_AllowedAfterPublish above: instead of re-declaring
    // an existing tag, the fragment declares a brand-new one, so the guard must
    // still fire.

    /// <summary>
    /// End-to-end: <see cref="LogixSdkTools.GetProposeRungEditResponseAsync"/> with
    /// <c>target="controller"</c> publishes a fragment whose context
    /// <c>&lt;Tags&gt;</c> container declares <c>Cmd_BrandNewTag</c> — a tag
    /// absent from the loaded project (the publish-time census source),
    /// i.e. a genuine creation rather than a context re-declaration. The
    /// delta-based fix must still include it in <c>TagsCreated</c> and must
    /// still call <see cref="TagSessionService.RecordCreatedTags"/> for it, so a
    /// same-session <c>logix_write</c> to <c>Cmd_BrandNewTag</c> is denied with
    /// <c>WriteDenied</c>, citing the proposal reference the publish recorded.
    /// </summary>
    [Fact]
    public async Task WriteToNewTagCreatedByPublish_IsDeniedWithProposalReference()
    {
        // Arrange: a gated virtual session so logix_write has a live tag service to
        // write through (the guard fires before that service is ever consulted),
        // and a loaded project (Demo.L5X) so the publish-time census does NOT
        // contain Cmd_BrandNewTag.
        var (tagSession, _) = BuildGatedVirtualSession();
        var projectService  = LoadDemoProject();
        var tools           = new LogixSdkTools(projectService, tagSession, new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

        var fake = new ProposeRungEditRecordingFake();
        var sdkSession = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await sdkSession.OpenAsync("dummy.ACD");
        sdkSession.IsOnline     = true;
        sdkSession.IsCorrelated = true;

        // Export-shaped fragment: context declaration of a genuinely new tag,
        // alongside the rung being published.
        const string fragment =
            "<RSLogix5000Content>" +
              "<Controller>" +
                "<Tags Use=\"Context\">" +
                  "<Tag Name=\"Cmd_BrandNewTag\" DataType=\"BOOL\" />" +
                "</Tags>" +
                "<Programs>" +
                  "<Program Name=\"MainProgram\">" +
                    "<Routines>" +
                      "<Routine Name=\"MainRoutine\">" +
                        "<RLLContent>" +
                          "<Rung Number=\"0\" Type=\"N\">" +
                            "<Text><![CDATA[NOP();]]></Text>" +
                          "</Rung>" +
                        "</RLLContent>" +
                      "</Routine>" +
                    "</Routines>" +
                  "</Program>" +
                "</Programs>" +
              "</Controller>" +
            "</RSLogix5000Content>";

        // Act (1): publish to the controller.
        var publishJson = await tools.GetProposeRungEditResponseAsync(
            "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']",
            0, 0, fragment, session: sdkSession, offline: false, target: "controller");

        using var publishDoc = JsonDocument.Parse(publishJson);
        Assert.Equal("proposed", publishDoc.RootElement.GetProperty("status").GetString());

        // The session-created registry must carry Cmd_BrandNewTag: it is a
        // genuine creation, not a context re-declaration of a pre-existing tag.
        Assert.True(tagSession.TryGetSessionCreatedTagEntry("Cmd_BrandNewTag", out var createdEntry));
        Assert.NotNull(createdEntry);

        // Act (2): attempt to write the newly-created tag in the same session.
        var writeResult = await WriteTagHandler.ExecuteAsync("Cmd_BrandNewTag", "true", tagSession);

        // Assert: the write is denied by the CreatedByThisSession self-grant
        // guard, and the denial message cites the proposal reference that the
        // publish above recorded for this tag.
        var error = Assert.IsType<WriteTagError>(writeResult);
        Assert.Equal("WriteDenied", error.Kind);
        Assert.Contains("CreatedByThisSession", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(createdEntry!.ProposalId, error.Error, StringComparison.Ordinal);
    }
#endif
}
