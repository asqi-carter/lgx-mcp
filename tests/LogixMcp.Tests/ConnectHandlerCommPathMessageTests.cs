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

#if LOGIX_SDK
    private static Task<object> ConnectViaCommPath(
        TagSessionService sessionService, SucceedingUploadFromControllerFactory factory,
        string path = "", ConnectState? connectState = null)
        => ConnectHandler.ExecuteAsync(
            gateway:        "",
            path:           path,
            useVirtual:     false,
            projectService: new ProjectService(),
            sessionService: sessionService,
            backend:        "sdk",
            sdkFactory:     factory,
            sdkMode:        "online",
            sdkCommPath:    "EmulateEthernet\\192.0.2.10",
            connectState:   connectState);

    // ── Wording ─────────────────────────────────

    /// <summary>The CommPathInvalid hint says what to check and that the SDK could not route; it never says to use sdkCommPath.</summary>
    [Fact]
    public void CommPathInvalidHint_IsTheAgreedText_AndDoesNotPointAtSdkCommPath()
    {
        Assert.Equal(
            "check the comm path (slot and port) and that the controller is reachable; the SDK could not route it",
            ConnectHandler.CommPathInvalidHint);
        Assert.DoesNotContain("sdkCommPath", ConnectHandler.CommPathInvalidHint, StringComparison.Ordinal);
    }

    /// <summary>A <c>path</c> passed with backend=sdk is reported as not used, and the warning names sdkCommPath.</summary>
    [Fact]
    public async Task ConnectHandler_SdkBackend_WithPath_WarnsPathIgnored_NamingSdkCommPath()
    {
        var result = await ConnectViaCommPath(new TagSessionService(), new SucceedingUploadFromControllerFactory(), path: "1,0");

        var ok = Assert.IsType<ConnectResult>(result);
        var warning = Assert.Single(ok.Warnings!, w => w.Contains("not used by the sdk backend", StringComparison.Ordinal));
        Assert.Contains("sdkCommPath", warning, StringComparison.Ordinal);
        Assert.Equal(ConnectHandler.PathUnusedBySdkWarning, warning);
    }

    [Fact]
    public async Task ConnectHandler_SdkBackend_WithoutPath_HasNoPathIgnoredWarning()
    {
        var result = await ConnectViaCommPath(new TagSessionService(), new SucceedingUploadFromControllerFactory());

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.DoesNotContain(ok.Warnings ?? [], w => w.Contains("not used by the sdk backend", StringComparison.Ordinal));
    }

    // ── replacedSession ─────────────────────────────────────────────────

    /// <summary>A first connect has nothing to replace; a second over the live session names the first.</summary>
    [Fact]
    public async Task ConnectHandler_ConnectOverLiveSession_ReportsReplacedSession_InResultAndMessage()
    {
        var sessionService = new TagSessionService();
        var state          = new ConnectState();
        var factory        = new SucceedingUploadFromControllerFactory();

        var first = Assert.IsType<ConnectResult>(await ConnectViaCommPath(sessionService, factory, connectState: state));
        Assert.Null(first.ReplacedSession);
        Assert.DoesNotContain("replaced the live session", first.Message, StringComparison.Ordinal);
        var stamp = state.LiveSession;
        Assert.NotNull(stamp);

        var second = Assert.IsType<ConnectResult>(await ConnectViaCommPath(sessionService, factory, connectState: state));

        Assert.NotNull(second.ReplacedSession);
        Assert.Equal(stamp!.ConnectId, second.ReplacedSession!.ConnectId);
        Assert.Equal(stamp.ConnectedAt, second.ReplacedSession.ConnectedAt);
        Assert.Contains("replaced the live session", second.Message, StringComparison.Ordinal);
        Assert.Contains(stamp.ConnectId, second.Message, StringComparison.Ordinal);

        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(second))!.AsObject();
        Assert.Equal(stamp.ConnectId, json["replacedSession"]!["connectId"]!.GetValue<string>());
        Assert.NotNull(json["replacedSession"]!["connectedAt"]);
    }

    /// <summary>Once the session is closed, the next connect replaces nothing.</summary>
    [Fact]
    public async Task ConnectHandler_ConnectAfterSessionClosed_ReportsNoReplacedSession()
    {
        var sessionService = new TagSessionService();
        var state          = new ConnectState();
        var factory        = new SucceedingUploadFromControllerFactory();
        Assert.IsType<ConnectResult>(await ConnectViaCommPath(sessionService, factory, connectState: state));
        state.MarkSessionClosed("closed by logix_close_project");
        sessionService.Detach();

        var next = Assert.IsType<ConnectResult>(await ConnectViaCommPath(sessionService, factory, connectState: state));

        Assert.Null(next.ReplacedSession);
    }

    // ── Upload-slot sweep ───────────────────────────────────────────────

    /// <summary>The connect result carries the count the registered slot cleaner returned.</summary>
    [Fact]
    public async Task ConnectHandler_ReportsStaleUploadSlotsRemoved_FromTheCleaner()
    {
        var previous = ConnectHandler.StaleUploadSlotCleaner;
        ConnectHandler.StaleUploadSlotCleaner = () => 3;
        try
        {
            var ok = Assert.IsType<ConnectResult>(
                await ConnectViaCommPath(new TagSessionService(), new SucceedingUploadFromControllerFactory()));

            Assert.Equal(3, ok.StaleUploadSlotsRemoved);
        }
        finally { ConnectHandler.StaleUploadSlotCleaner = previous; }
    }

    /// <summary>A cleaner that throws never fails the connect.</summary>
    [Fact]
    public async Task ConnectHandler_ThrowingSlotCleaner_DoesNotFailTheConnect()
    {
        var previous = ConnectHandler.StaleUploadSlotCleaner;
        ConnectHandler.StaleUploadSlotCleaner = () => throw new IOException("denied");
        try
        {
            var ok = Assert.IsType<ConnectResult>(
                await ConnectViaCommPath(new TagSessionService(), new SucceedingUploadFromControllerFactory()));

            Assert.Null(ok.StaleUploadSlotsRemoved);
        }
        finally { ConnectHandler.StaleUploadSlotCleaner = previous; }
    }

    /// <summary>A ctl-* slot whose owner pid is dead is removed; one whose owner is live, or with no owner file, is kept.</summary>
    [Fact]
    public void CleanDeadOwnerUploadSlots_RemovesDeadOwnerSlots_KeepsLiveAndUnownedAndOtherFolders()
    {
        var work = Path.Combine(Path.GetTempPath(), "lgx-slots-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Slot(string name, string? owner)
            {
                var dir = Path.Combine(work, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "uploaded.ACD"), "acd");
                if (owner is not null) File.WriteAllText(Path.Combine(dir, SdkAdapterInitializer.SlotOwnerFileName), owner);
                return dir;
            }
            var dead      = Slot("ctl-1-0",      "4242");
            var live      = Slot("ctl-2-0",      "4343");
            var unowned   = Slot("ctl-3-0",      null);
            var garbage   = Slot("ctl-4-0",      "not-a-pid");
            var otherKind = Slot("build-verify", "4242");

            var removed = SdkAdapterInitializer.CleanDeadOwnerUploadSlots(work, pid => pid == 4343);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(dead));
            Assert.True(Directory.Exists(live));
            Assert.True(Directory.Exists(unowned));
            Assert.True(Directory.Exists(garbage));
            Assert.True(Directory.Exists(otherKind));
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void CleanDeadOwnerUploadSlots_MissingWorkBase_ReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "lgx-slots-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, SdkAdapterInitializer.CleanDeadOwnerUploadSlots(missing, _ => false));
    }
#endif
}
