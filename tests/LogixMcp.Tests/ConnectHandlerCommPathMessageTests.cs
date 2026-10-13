using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Regression tests for the comm-path-wins success message in
/// <see cref="ConnectHandler.ExecuteAsync"/> (upload-from-controller
/// branch).
///
/// <para>
/// The success <c>Message</c> previously interpolated the <c>gateway</c>
/// parameter directly (<c>"using controller at '{gateway}'"</c>). On the
/// comm-path-wins branch, callers may supply only <c>sdkCommPath</c> (Logix
/// Designer format, e.g. <c>"EmulateEthernet\192.0.2.10"</c>) and leave
/// <c>gateway</c> empty — in that case the message rendered as
/// <c>"using controller at ''"</c>, an empty quoted string. The message must
/// instead report the actual comm path used to connect: <c>sdkCommPath</c>
/// when supplied, otherwise the <c>gateway</c>/<c>path</c> pair.
/// </para>
/// </summary>
public class ConnectHandlerCommPathMessageTests
{
    /// <summary>
    /// Minimal <see cref="ISdkTagServiceFactory"/> stub whose
    /// <see cref="CreateFromControllerAsync"/> succeeds with a canned
    /// <see cref="SdkSessionResult"/> backed by an in-memory
    /// <see cref="VirtualTagService"/>. <c>TypeProject</c> and
    /// <c>AcdPath</c> are left <c>null</c> so no gate-policy derivation or
    /// project registration is attempted — this test targets only the
    /// success message text.
    /// </summary>
    private sealed class SucceedingUploadFromControllerFactory : ISdkTagServiceFactory
    {
        public string? LastCommPath { get; private set; }

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline",
            bool allowControllerDownload = false,
            CancellationToken ct = default)
            => throw new NotImplementedException(
                "SucceedingUploadFromControllerFactory is only for the no-project-file path.");

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath,
            CancellationToken ct = default)
        {
            LastCommPath = commPath;
            return Task.FromResult(new SdkSessionResult(
                Service:           new VirtualTagService(),
                DownloadPerformed: false,
                ModeTransitions:   [],
                CorrelationPath:   "upload-to-new-project",
                TypeProject:       null,
                AcdPath:           null));
        }
    }

#if LOGIX_SDK
    /// <summary>
    /// When <c>sdkCommPath</c> is supplied and <c>gateway</c> is empty (a
    /// realistic caller shape for the comm-path-wins branch), the success
    /// message must report the resolved <c>sdkCommPath</c> value — never an
    /// empty quoted string.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_CommPathWins_EmptyGateway_MessageUsesResolvedCommPath()
    {
        const string commPath = "EmulateEthernet\\192.0.2.10";

        var projectService = new ProjectService();   // no project loaded
        var sessionService = new TagSessionService();
        var fakeFactory    = new SucceedingUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     fakeFactory,
            sdkMode:        "online",
            sdkCommPath:    commPath);

        var ok = Assert.IsType<ConnectResult>(result);

        Assert.NotNull(ok.Message);
        Assert.NotEmpty(ok.Message);
        Assert.DoesNotContain("''", ok.Message, StringComparison.Ordinal);
        Assert.Contains(commPath, ok.Message, StringComparison.Ordinal);
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// Same branch, but with a project already open via
    /// <c>logix_open_project</c> (the <c>projectAlreadyLoaded</c> ternary's
    /// other arm at ConnectHandler.cs line ~1082). Both message variants must
    /// use the resolved comm path, not the empty <c>gateway</c>.
    /// </summary>
    [Fact]
    public async Task ConnectHandler_CommPathWins_ProjectAlreadyLoaded_MessageUsesResolvedCommPath()
    {
        const string commPath = "EmulateEthernet\\192.0.2.40";
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

        var projectService = new ProjectService();
        projectService.Load(fixturePath);
        var sessionService = new TagSessionService();
        var fakeFactory    = new SucceedingUploadFromControllerFactory();

        var result = await ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           "",
            useVirtual:     false,
            projectService: projectService,
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     fakeFactory,
            sdkMode:        "online",
            sdkCommPath:    commPath);

        var ok = Assert.IsType<ConnectResult>(result);

        Assert.NotNull(ok.Message);
        Assert.NotEmpty(ok.Message);
        Assert.DoesNotContain("''", ok.Message, StringComparison.Ordinal);
        Assert.Contains(commPath, ok.Message, StringComparison.Ordinal);
    }
#endif
}
