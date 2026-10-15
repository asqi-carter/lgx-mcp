using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the TCP-probe discriminator that runs before a
/// route-error attribution in <see cref="ConnectHandler.ExecuteAsync"/>.
///
/// <para>
/// The Logix Designer SDK's "No module was found using the supplied encoded
/// route string." error fires identically whether the comm path uses the
/// wrong addressing scheme OR the target controller simply does not answer
/// on the network (measured on Bench 2 2026-09-06 by stopping the
/// FactoryTalk Logix Echo Message Broker). Rewriting a comm path that was
/// correct all along wastes an engineer's — or a model's — time.
/// </para>
///
/// <para>
/// These tests use a fake <see cref="ISdkTagServiceFactory"/> whose
/// <see cref="ISdkTagServiceFactory.CreateFromControllerAsync"/> raises the
/// encoded-route error, and inject the TCP probe via
/// <c>ExecuteAsync</c>'s <c>controllerReachableProbe</c> parameter so no
/// network connection is ever attempted during the test run.
/// </para>
/// </summary>
public class RouteErrorWithUnreachableControllerTests
{
    // ── Fake factory — always raises the encoded-route error ──────────────────

    /// <summary>
    /// <see cref="ISdkTagServiceFactory"/> stub whose
    /// <see cref="CreateFromControllerAsync"/> always throws the SDK's
    /// encoded-route-string <see cref="TagServiceException"/>, exactly as
    /// <c>UploadToNewProjectAsync</c> does on Bench 2 when the target does not
    /// answer or when the comm path format is genuinely wrong.
    /// </summary>
    private sealed class RouteErrorRaisingFactory : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "RouteErrorRaisingFactory is only for the no-project-file upload path.");

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw new TagServiceException(new TagServiceError(
                TagServiceErrorKind.ConnectionFailed,
                "UploadToNewProjectAsync failed: RxCL_E_CANNOT_UPLOAD_PHYS_ADDR - " +
                "No module was found using the supplied encoded route string."));
    }

    private static (ProjectService, TagSessionService) NewServices()
        => (new ProjectService(), new TagSessionService());

    // ── Branch 1: controller unreachable ───────────────────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// When an IP can be derived from the comm path and the injected TCP probe
    /// reports the controller unreachable, the response must be the
    /// <c>ConnectionFailed</c> shape naming the address and port 44818 — not the
    /// comm-path-format advice from <see cref="ConnectSdkRouteError"/>.
    /// </summary>
    [Fact]
    public async Task RouteError_ControllerUnreachable_IsConnectionFailedNamingPort44818()
    {
        const string commPath = "EmulateEthernet\\192.0.2.20";
        var (projectService, sessionService) = NewServices();

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     new RouteErrorRaisingFactory(),
            sdkMode:        "online",
            sdkCommPath:    commPath,
            controllerReachableProbe: (_, _) => Task.FromResult(false));

        var failure = Assert.IsType<ConnectSdkConnectionFailedError>(result);

        Assert.Equal("ConnectionFailed", failure.Kind);
        Assert.Contains("192.0.2.20", failure.ControllerAddress, StringComparison.Ordinal);
        Assert.Contains("44818", failure.ControllerAddress, StringComparison.Ordinal);
        Assert.Contains("44818", failure.Error, StringComparison.Ordinal);
        Assert.Contains("192.0.2.20", failure.Error, StringComparison.Ordinal);

        // Verbatim SDK code/detail is included so it can be searched.
        Assert.Contains(
            "No module was found using the supplied encoded route string.",
            failure.Error, StringComparison.Ordinal);

        // No comm-path-format advice — the probe already confirmed the format
        // was fine; rewriting the path would not fix an unreachable target.
        Assert.DoesNotContain("sdkCommPath parameter", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("CIP/libplctag", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("sdkCommPath parameter", failure.Hint, StringComparison.Ordinal);
    }
#endif

    // ── Branch 2: controller reachable ─────────────────────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// When the injected TCP probe reports the controller reachable, the
    /// original route-error message and comm-path-format advice must be
    /// returned unchanged — the path (or module route) really is wrong.
    /// </summary>
    [Fact]
    public async Task RouteError_ControllerReachable_KeepsCommPathAdvice()
    {
        const string commPath = "EmulateEthernet\\192.0.2.10";
        var (projectService, sessionService) = NewServices();

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     new RouteErrorRaisingFactory(),
            sdkMode:        "online",
            sdkCommPath:    commPath,
            controllerReachableProbe: (_, _) => Task.FromResult(true));

        var routeError = Assert.IsType<ConnectSdkRouteError>(result);

        Assert.Equal(commPath, routeError.CommPathUsed);
        Assert.Contains("route error", routeError.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "check the comm path (slot and port) and that the controller is reachable; the SDK could not route it",
            routeError.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("may also be down", routeError.Error, StringComparison.OrdinalIgnoreCase);
    }
#endif

    // ── Branch 3: IP not derivable ──────────────────────────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// When no IP can be derived from the comm path, the probe cannot run at
    /// all; the response must keep the existing route-error message plus one
    /// sentence noting the target may also be down.
    /// </summary>
    [Fact]
    public async Task RouteError_IpNotDerivable_MentionsTargetMayBeDown()
    {
        // Unrecognised protocol prefix — TryParseControllerIp returns null.
        const string commPath = "UnknownProtocol\\not-an-ip";
        var (projectService, sessionService) = NewServices();

        var probeInvoked = false;

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     new RouteErrorRaisingFactory(),
            sdkMode:        "online",
            sdkCommPath:    commPath,
            controllerReachableProbe: (_, _) =>
            {
                probeInvoked = true;
                return Task.FromResult(true);
            });

        var routeError = Assert.IsType<ConnectSdkRouteError>(result);

        Assert.False(probeInvoked, "the probe must not run when no IP can be derived");
        Assert.Equal(commPath, routeError.CommPathUsed);
        Assert.Contains("may also be down", routeError.Error, StringComparison.OrdinalIgnoreCase);
        // Existing route-error content and advice must still be present.
        Assert.Contains("route error", routeError.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "check the comm path (slot and port) and that the controller is reachable; the SDK could not route it",
            routeError.Hint, StringComparison.Ordinal);
    }
#endif

    // ── Bonus: audit record ──────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// One <c>sdk_service_diagnosis</c> audit record is written per probe on
    /// this branch, carrying the probe outcome.
    /// </summary>
    [Fact]
    public async Task RouteError_Probe_WritesSdkServiceDiagnosisRecord()
    {
        const string commPath = "EmulateEthernet\\192.0.2.20";
        var (projectService, sessionService) = NewServices();

        var tempDir = Path.Combine(Path.GetTempPath(), $"routeerror-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AuditLogger.OverrideLogDir(tempDir);
        try
        {
            _ = await ConnectHandler.ExecuteAsync(
                gateway:        "",
                path:           "",
                useVirtual:     false,
                projectService: projectService,
                sessionService: sessionService,
                backend:        "sdk",
                sdkFactory:     new RouteErrorRaisingFactory(),
                sdkMode:        "online",
                sdkCommPath:    commPath,
                controllerReachableProbe: (_, _) => Task.FromResult(false));

            var auditPath = Path.Combine(tempDir, "audit.jsonl");
            Assert.True(File.Exists(auditPath));
            var lines = File.ReadAllLines(auditPath)
                .Where(l => l.Contains("sdk_service_diagnosis", StringComparison.Ordinal))
                .ToArray();
            Assert.Single(lines);
            Assert.Contains("\"probe\":\"unreachable\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"ConnectionFailed\"", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
#endif
}
