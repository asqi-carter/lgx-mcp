using System.Security.Cryptography;
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the controller-download gate on <c>logix_connect</c>:
/// <c>allowControllerDownload=true</c> requires <c>LGX_ENABLE_CONTROLLER_DOWNLOAD=1</c>
/// (else <c>CapabilityDisabled</c>) and <c>confirmedByUser=true</c> (else
/// <c>ConfirmationRequired</c>), and a performed download records the SHA-256 of the
/// downloaded project file in the audit connect record.
/// </summary>
[Collection("ProcessEnvironment")]
public class ControllerDownloadGatingTests : IDisposable
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private readonly string _tempDir;

    public ControllerDownloadGatingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dl-gating-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Factory double that records calls and reports a performed download.</summary>
    private sealed class DownloadingFactory : ISdkTagServiceFactory
    {
        public int CreateCalls { get; private set; }

        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject,
            string sdkMode = "offline", bool allowControllerDownload = false,
            CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new SdkSessionResult(
                Service:           new VirtualTagService(new Dictionary<string, (object, string?)>()),
                DownloadPerformed: allowControllerDownload,
                ModeTransitions:   allowControllerDownload ? ["Program", "Run"] : [],
                CorrelationPath:   allowControllerDownload ? "download" : "already-correlated"));
        }

        public Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private static ProjectService LoadDemoProject()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        return svc;
    }

    private static Task<object> ConnectWithDownload(
        DownloadingFactory factory, Capabilities capabilities, bool confirmedByUser) =>
        ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, LoadDemoProject(), new TagSessionService(),
            backend: "sdk", sdkFactory: factory, sdkMode: "online",
            allowControllerDownload: true, capabilities: capabilities,
            confirmedByUser: confirmedByUser);

    private List<JsonDocument> ReadAudit()
    {
        var file = Path.Combine(_tempDir, "audit.jsonl");
        return File.Exists(file)
            ? File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l))
                  .Select(l => JsonDocument.Parse(l)).ToList()
            : [];
    }

#if LOGIX_SDK
    [Fact]
    public async Task RequiresFlagAndConfirmedByUser()
    {
        var factory = new DownloadingFactory();

        // Missing LGX_ENABLE_CONTROLLER_DOWNLOAD (even with confirmation) → CapabilityDisabled.
        var noFlag = await ConnectWithDownload(
            factory, new Capabilities(LiveWritesEnabled: true), confirmedByUser: true);
        var disabled = Assert.IsType<CapabilityDisabledError>(noFlag);
        Assert.Equal("CapabilityDisabled", disabled.Kind);
        Assert.Contains("LGX_ENABLE_CONTROLLER_DOWNLOAD", disabled.Error);
        Assert.Contains("LGX_ENABLE_CONTROLLER_DOWNLOAD", disabled.Hint);

        // Flag set but no confirmedByUser → ConfirmationRequired.
        var caps = new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true);
        var noConfirm = await ConnectWithDownload(factory, caps, confirmedByUser: false);
        var confirm = Assert.IsType<ConfirmationRequiredError>(noConfirm);
        Assert.Equal("ConfirmationRequired", confirm.Kind);

        // Neither refusal reached the SDK factory.
        Assert.Equal(0, factory.CreateCalls);

        // Both present → proceeds to the download.
        var ok = await ConnectWithDownload(factory, caps, confirmedByUser: true);
        var connected = Assert.IsType<ConnectResult>(ok);
        Assert.True(connected.DownloadPerformed);
        Assert.Equal(1, factory.CreateCalls);
    }
#endif

    [Fact]
    public async Task FlagWithoutLiveWrites_IsStillCapabilityDisabled()
    {
        var factory = new DownloadingFactory();
        var result = await ConnectWithDownload(
            factory, new Capabilities(LiveWritesEnabled: false, ControllerDownloadEnabled: true),
            confirmedByUser: true);

        Assert.IsType<CapabilityDisabledError>(result);
        Assert.Equal(0, factory.CreateCalls);
    }

    [Fact]
    public void FromEnvironment_ReadsControllerDownloadFlag()
    {
        var prior = Environment.GetEnvironmentVariable(Capabilities.EnvVarControllerDownload);
        try
        {
            Environment.SetEnvironmentVariable(Capabilities.EnvVarControllerDownload, null);
            Assert.False(Capabilities.FromEnvironment().ControllerDownloadEnabled);
            Environment.SetEnvironmentVariable(Capabilities.EnvVarControllerDownload, "1");
            Assert.True(Capabilities.FromEnvironment().ControllerDownloadEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Capabilities.EnvVarControllerDownload, prior);
        }
    }

#if LOGIX_SDK
    [Fact]
    public void ConfirmedByUser_IsAValidConnectParameter()
    {
        Assert.Null(ConnectHandler.CheckForUnknownParameters(["allowControllerDownload", "confirmedByUser"]));
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task AuditRecordsFileSha256()
    {
        var caps = new Capabilities(LiveWritesEnabled: true, ControllerDownloadEnabled: true);
        var result = await ConnectWithDownload(new DownloadingFactory(), caps, confirmedByUser: true);
        Assert.IsType<ConnectResult>(result);

        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath)))
            .ToLowerInvariant();

        var connect = ReadAudit()
            .Select(d => d.RootElement)
            .Single(r => r.GetProperty("event").GetString() == "connect");
        Assert.True(connect.GetProperty("download_performed").GetBoolean());
        Assert.Equal(expected, connect.GetProperty("downloaded_file_sha256").GetString());
    }
#endif

#if LOGIX_SDK
    [Fact]
    public async Task AuditOmitsFileSha256_WhenNoDownloadPerformed()
    {
        // Online connect that correlates without a download (allowControllerDownload=false is
        // refused up-front on the file-based route, so use the offline SDK path).
        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, LoadDemoProject(), new TagSessionService(),
            backend: "sdk", sdkFactory: new DownloadingFactory(), sdkMode: "offline");
        Assert.IsType<ConnectResult>(result);

        var connect = ReadAudit()
            .Select(d => d.RootElement)
            .Single(r => r.GetProperty("event").GetString() == "connect");
        Assert.False(connect.TryGetProperty("downloaded_file_sha256", out _));
    }
#endif
}
