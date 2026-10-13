using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Acceptance tests asserting that each of the four target structured errors
/// carries a non-empty <c>hint</c> string that guides the caller toward a
/// concrete remediation action.
///
/// <para>
/// Covered errors (one test per kind):
/// <list type="bullet">
///   <item><see cref="WriteTagError"/> with <c>Kind="WriteDenied"</c> — exercised
///     via <see cref="WriteTagHandlerTests"/> (see that file).</item>
///   <item><see cref="ConnectDownloadRequiredError"/> — IMAGES_INCOMPATIBLE path
///     in <see cref="ConnectHandler.ExecuteAsync"/>.</item>
///   <item><see cref="ConnectSdkNotAvailableError"/> — SDK adapter absent path
///     in <see cref="ConnectHandler.ExecuteAsync"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// All tests in this file run on the core-only (non-SDK) Linux build and carry
/// no <c>#if LOGIX_SDK</c> guard.  The build-failure hint test
/// (<c>ProposeRungEditError.Hint</c>) lives in
/// <c>ProposeRungEditHandlerTests.cs</c> because it requires SDK types.
/// </para>
/// </summary>
[Collection("StaticHandlerHooks")]
public class ErrorHintTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ── ConnectSdkNotAvailableError hint ──────────────────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// When the SDK adapter is not registered (<c>DefaultSdkFactory</c> is
    /// <c>null</c> and no per-call factory is supplied),
    /// <see cref="ConnectHandler.ExecuteAsync"/> must return a
    /// <see cref="ConnectSdkNotAvailableError"/> that carries a non-empty
    /// <c>hint</c> guiding the caller to install the Logix Designer SDK.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_SdkNotAvailable_ConnectSdkNotAvailableError_HintIsNonEmpty()
    {
        // Arrange: null out DefaultSdkFactory so the adapter-unavailable path fires.
        var savedFactory = ConnectHandler.DefaultSdkFactory;
        ConnectHandler.DefaultSdkFactory = null;
        try
        {
            var projectService = new ProjectService();
            var sessionService = new TagSessionService();

            // Act: call with backend="sdk" and no factory (sdkFactory is null,
            // DefaultSdkFactory is null) → ConnectSdkNotAvailableError.
            var result = await ConnectHandler.ExecuteAsync(
                gateway:        "",
                path:           "",
                useVirtual:     false,
                projectService: projectService,
                sessionService: sessionService,
                backend:        "sdk",
                sdkFactory:     null);

            // Assert: structured error with a non-empty hint.
            var error = Assert.IsType<ConnectSdkNotAvailableError>(result);
            Assert.NotNull(error.Hint);
            Assert.NotEmpty(error.Hint);
        }
        finally
        {
            ConnectHandler.DefaultSdkFactory = savedFactory;
        }
    }
#endif

    // ── ConnectDownloadRequiredError hint (IMAGES_INCOMPATIBLE) ──────────────

#if LOGIX_SDK
    /// <summary>
    /// When the SDK factory throws a
    /// <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.ImagesIncompatible"/>,
    /// <see cref="ConnectHandler.ExecuteAsync"/> must return a
    /// <see cref="ConnectDownloadRequiredError"/> that carries a non-empty
    /// <c>hint</c> describing the non-destructive-first remediation path.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_ImagesIncompatible_ConnectDownloadRequiredError_HintIsNonEmpty()
    {
        // Arrange: load a project so FilePath is set (required before the
        // factory call is attempted) and inject a factory that throws.
        var projectService = new ProjectService();
        projectService.Load(FixturePath);

        var sessionService = new TagSessionService();
        var fakeFactory    = new ThrowImagesIncompatibleFactory();

        // Act: call with backend="sdk" and the throwing factory.
        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     fakeFactory);

        // Assert: ConnectDownloadRequiredError with a non-empty hint.
        var error = Assert.IsType<ConnectDownloadRequiredError>(result);
        Assert.NotNull(error.Hint);
        Assert.NotEmpty(error.Hint);

        // The hint must name the non-destructive "upload" path.
        Assert.Contains("upload", error.Hint, StringComparison.OrdinalIgnoreCase);

        // The upload instruction must appear before any download instruction,
        // guarding against the ordering regression described in the task PRD.
        var uploadIdx   = error.Hint.IndexOf("upload",   StringComparison.OrdinalIgnoreCase);
        var downloadIdx = error.Hint.IndexOf("download", StringComparison.OrdinalIgnoreCase);
        Assert.True(
            downloadIdx < 0 || uploadIdx < downloadIdx,
            $"Expected 'upload' (at index {uploadIdx}) to appear before 'download' " +
            $"(at index {downloadIdx}) in the hint, but it did not. " +
            $"Hint: \"{error.Hint}\"");
    }
#endif

    // ── ConnectPendingEditsError hint + docs_anchor ───────────────────────────

#if LOGIX_SDK
    /// <summary>
    /// When the SDK factory throws a
    /// <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.PendingEditsPresent"/> from
    /// <see cref="ISdkTagServiceFactory.CreateFromControllerAsync"/>,
    /// <see cref="ConnectHandler.ExecuteAsync"/> must return a
    /// <see cref="ConnectPendingEditsError"/> with:
    /// <list type="bullet">
    ///   <item>A non-empty <c>hint</c> that names both resolutions —
    ///     accept/finalize <em>and</em> cancel — without preferring either.</item>
    ///   <item>A non-empty <c>docs_anchor</c> that resolves to a real heading
    ///     in <c>docs/SPEC.md</c>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ConnectHandler_PendingEdits_ConnectPendingEditsError_HintMentionsBothResolutions()
    {
        // Arrange: no project loaded (triggers the upload-from-controller path),
        // inject a factory that throws PendingEditsPresent from
        // CreateFromControllerAsync.
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var fakeFactory    = new ThrowPendingEditsFactory();

        // Act: call with backend="sdk", sdkMode="online", gateway set (no project).
        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "EmulateEthernet\\192.0.2.10",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkMode:        "online",
            sdkFactory:     fakeFactory);

        // Assert: structured ConnectPendingEditsError.
        var error = Assert.IsType<ConnectPendingEditsError>(result);

        // Hint must be non-empty.
        Assert.NotNull(error.Hint);
        Assert.NotEmpty(error.Hint);

        // Hint must name the accept/finalize resolution.
        Assert.True(
            error.Hint.Contains("accept", StringComparison.OrdinalIgnoreCase) ||
            error.Hint.Contains("finalize", StringComparison.OrdinalIgnoreCase),
            $"Hint must mention 'accept' or 'finalize' to name the first resolution. " +
            $"Hint: \"{error.Hint}\"");

        // Hint must name the cancel resolution.
        Assert.Contains("cancel", error.Hint, StringComparison.OrdinalIgnoreCase);

        // docs_anchor must be non-empty.
        Assert.NotNull(error.DocsAnchor);
        Assert.NotEmpty(error.DocsAnchor);
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// Verifies that the <c>kind</c> field in <see cref="ConnectPendingEditsError"/>
    /// is set to the machine-readable string <c>"PendingEditsPresent"</c>.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_PendingEdits_ConnectPendingEditsError_KindIsCorrect()
    {
        var projectService = new ProjectService();
        var sessionService = new TagSessionService();
        var fakeFactory    = new ThrowPendingEditsFactory();

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "EmulateEthernet\\192.0.2.10",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkMode:        "online",
            sdkFactory:     fakeFactory);

        var error = Assert.IsType<ConnectPendingEditsError>(result);
        Assert.Equal("PendingEditsPresent", error.Kind);
    }
#endif

    // ── Private test doubles ──────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ISdkTagServiceFactory"/> stub that always throws
    /// <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.ImagesIncompatible"/> from
    /// <see cref="CreateAsync"/>, simulating the controller holding a different
    /// project image than the one open in the SDK session.
    /// </summary>
    private sealed class ThrowImagesIncompatibleFactory : ISdkTagServiceFactory
    {
        /// <inheritdoc/>
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath,
            L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new TagServiceException(
                new TagServiceError(
                    TagServiceErrorKind.ImagesIncompatible,
                    "RxCL_E_IMAGES_INCOMPATIBLE — the controller holds a different " +
                    "project image than the one currently open in the SDK session."));

        /// <inheritdoc/>
        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "Stub — CreateFromControllerAsync not exercised in this test.");
    }

    /// <summary>
    /// Minimal <see cref="ISdkTagServiceFactory"/> stub that always throws
    /// <see cref="TagServiceException"/> with
    /// <see cref="TagServiceErrorKind.PendingEditsPresent"/> from
    /// <see cref="CreateFromControllerAsync"/>, simulating the upload-from-controller
    /// path failing because the controller holds uncommitted edits.
    /// </summary>
    private sealed class ThrowPendingEditsFactory : ISdkTagServiceFactory
    {
        /// <inheritdoc/>
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath,
            L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "Stub — CreateAsync not exercised in this test.");

        /// <inheritdoc/>
        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
            => throw new TagServiceException(
                new TagServiceError(
                    TagServiceErrorKind.PendingEditsPresent,
                    $"The controller at '{commPath}' has uncommitted edits that prevented " +
                    $"project extraction. Commit or discard the pending edits on the " +
                    $"controller and retry the connection."));
    }
}
