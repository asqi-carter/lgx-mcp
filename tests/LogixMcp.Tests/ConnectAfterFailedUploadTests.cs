#if LOGIX_SDK
using System.Text.Json;
using System.Text.Json.Nodes;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Replays the C2 order. An upload connect registers a project and its ACD; a later connect that
/// disposes the upload session must not leave that registration dangling, a connect that finds the registered file
/// gone returns the typed <c>ProjectFileMissing</c>, and the status poll says which backend the attached session is.
/// </summary>
[Collection("StaticHandlerHooks")]
public class ConnectAfterFailedUploadTests
{
    private const string CommPath = "EmulateEthernet\\192.0.2.50";
    private const string OtherCommPath = "EmulateEthernet\\192.0.2.99";

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>Upload factory: succeeds with the Demo project, or throws a pending-edits failure.</summary>
    private sealed class UploadFactory(bool fail = false) : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => fail
                ? throw new TagServiceException(new TagServiceError(
                    TagServiceErrorKind.PendingEditsPresent, $"The controller at '{commPath}' has uncommitted edits."))
                : Task.FromResult(new SdkSessionResult(
                    Service:           new VirtualTagService(),
                    DownloadPerformed: false,
                    ModeTransitions:   [],
                    CorrelationPath:   "upload-to-new-project",
                    TypeProject:       L5Sharp.Core.L5X.Load(FixturePath),
                    AcdPath:           "uploaded.ACD"));
    }

    private static async Task WithSdkSeams(Func<Func<int>, Task> body, Func<TagSessionService, bool>? hasSession = null)
    {
        var savedInspector = CloseProjectHandler.SdkSessionInspector;
        var savedDisposer  = CloseProjectHandler.SdkSessionDisposer;
        try
        {
            var disposed = 0;
            CloseProjectHandler.SdkSessionInspector = s =>
                (hasSession?.Invoke(s) ?? s.Service is not null) && disposed == 0
                    ? new SdkSessionCloseInfo(0, false) : null;
            CloseProjectHandler.SdkSessionDisposer = _ => disposed++;
            await body(() => disposed);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = savedInspector;
            CloseProjectHandler.SdkSessionDisposer  = savedDisposer;
        }
    }

    [Fact]
    public async Task UploadConnect_ThenFailedSecondConnect_LeavesProjectServiceUnloaded_AndDisposesOutgoingSession()
    {
        await WithSdkSeams(async disposed =>
        {
            var projects = new ProjectService();
            var tags     = new TagSessionService();

            Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(),
                sdkMode: "online", sdkCommPath: CommPath));
            Assert.True(projects.IsLoaded);
            Assert.False(projects.OpenedExplicitly);

            var second = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(fail: true),
                sdkMode: "online", sdkCommPath: OtherCommPath);

            Assert.IsNotType<ConnectResult>(second);
            Assert.Equal(1, disposed());
            Assert.False(projects.IsLoaded);
            Assert.Null(projects.FilePath);
        });
    }

    [Fact]
    public async Task ExplicitlyOpenedProject_SurvivesDisposalOfTheOutgoingSession()
    {
        await WithSdkSeams(async disposed =>
        {
            var projects = new ProjectService();
            projects.Load(FixturePath);
            var tags = new TagSessionService();
            tags.Attach(new VirtualTagService(), isVirtual: true);

            var second = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(fail: true),
                sdkMode: "online", sdkCommPath: OtherCommPath);

            Assert.IsNotType<ConnectResult>(second);
            Assert.Equal(1, disposed());
            Assert.True(projects.IsLoaded);
            Assert.True(projects.OpenedExplicitly);
        });
    }

    /// <summary>
    /// The C2 replay (C2 order): upload connect, then a cip connect that disposes the upload session (clearing the
    /// registration, 0185), then an offline SDK connect. With no project left, the offline step returns the typed
    /// <c>NoProjectLoaded</c> with a hint naming the next steps, not <c>ProjectFileMissing</c>.
    /// </summary>
    [Fact]
    public async Task C2Replay_UploadThenCip_ThenOfflineSdkConnect_ReturnsNoProjectLoaded_WithHint()
    {
        await WithSdkSeams(async disposed =>
        {
            var projects = new ProjectService();
            var tags     = new TagSessionService();

            Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(),
                sdkMode: "online", sdkCommPath: CommPath));
            Assert.True(projects.IsLoaded);

            // The cip step disposes the upload session; its registration goes with it.
            tags.Attach(new LibPlcTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
            ConnectHandler.DisposeOutgoingSdkSession(tags, projects);
            Assert.Equal(1, disposed());
            Assert.False(projects.IsLoaded);

            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(),
                sdkMode: "offline");

            var error = Assert.IsType<ConnectError>(result);
            Assert.Equal("NoProjectLoaded", error.Kind);
            Assert.StartsWith("No project loaded.", error.Error, StringComparison.Ordinal);
            Assert.Contains("logix_connect", error.Hint, StringComparison.Ordinal);
            Assert.Contains("sdkMode='online'", error.Hint, StringComparison.Ordinal);
            Assert.Contains("sdkCommPath", error.Hint, StringComparison.Ordinal);
            Assert.Contains("gateway", error.Hint, StringComparison.Ordinal);
            Assert.Contains("logix_open_project", error.Hint, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task VirtualAndL5xConnects_WithNoProject_ReturnNoProjectLoaded_WithHint()
    {
        var virt = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: true, new ProjectService(), new TagSessionService(), backend: "cip");
        var l5x = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, new ProjectService(), new TagSessionService(), backend: "l5x");

        foreach (var r in new[] { virt, l5x })
        {
            var error = Assert.IsType<ConnectError>(r);
            Assert.Equal(ErrorKinds.NoProjectLoaded, error.Kind);
            Assert.Contains("logix_open_project", error.Hint, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RegisteredProjectWhoseFileWasDeleted_OfflineSdkConnect_ReturnsProjectFileMissing_WithHint()
    {
        await WithSdkSeams(async _ =>
        {
            var gone     = Path.Combine(Path.GetTempPath(), "lgx-gone-" + Guid.NewGuid().ToString("N"), "uploaded.ACD");
            var projects = new ProjectService();
            projects.RegisterFromUpload(L5Sharp.Core.L5X.Load(FixturePath), gone, CommPath);
            var tags = new TagSessionService();
            tags.Attach(new LibPlcTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");

            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadFactory(),
                sdkMode: "offline");

            var error = Assert.IsType<ConnectProjectFileMissingError>(result);
            Assert.Equal("ProjectFileMissing", error.Kind);
            Assert.Contains(gone, error.Hint, StringComparison.Ordinal);
            Assert.Contains("upload copy was removed", error.Hint, StringComparison.Ordinal);
            Assert.Contains("logix_connect", error.Hint, StringComparison.Ordinal);
            Assert.Contains("commPath", error.Hint, StringComparison.Ordinal);
            Assert.Contains("logix_open_project", error.Hint, StringComparison.Ordinal);
        }, hasSession: _ => false);
    }

    [Fact]
    public void StatusPoll_AfterFailedConnect_WithCipSessionAttached_ReportsConnectedBackend_AndHint()
    {
        var tags = new TagSessionService();
        tags.Attach(new LibPlcTagService(), isVirtual: false, isLiveController: true, controllerHost: "192.0.2.10");
        tags.SetAuditController("192.0.2.10");

        var o = JsonNode.Parse(SessionLostSupport.AnnotateStatus(
            tags, """{"status":"failed","error":"No project loaded."}"""))!.AsObject();

        Assert.True(o["connected"]!.GetValue<bool>());
        Assert.Equal("cip", o["connectedBackend"]!.GetValue<string>());
        var hint = o["hint"]!.GetValue<string>();
        Assert.Contains("cip", hint, StringComparison.Ordinal);
        Assert.Contains("192.0.2.10", hint, StringComparison.Ordinal);
        Assert.Contains("last connect attempt", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusPoll_WithoutSession_OrWhenNotFailed_AddsNoHint()
    {
        var none = JsonNode.Parse(SessionLostSupport.AnnotateStatus(
            new TagSessionService(), """{"status":"failed"}"""))!.AsObject();
        Assert.False(none["connected"]!.GetValue<bool>());
        Assert.Null(none["connectedBackend"]);
        Assert.Null(none["hint"]);

        var tags = new TagSessionService();
        tags.Attach(new LibPlcTagService(), isVirtual: false, isLiveController: true);
        var ok = JsonNode.Parse(SessionLostSupport.AnnotateStatus(tags, """{"status":"connected"}"""))!.AsObject();
        Assert.Equal("cip", ok["connectedBackend"]!.GetValue<string>());
        Assert.Null(ok["hint"]);
    }
}
#endif
