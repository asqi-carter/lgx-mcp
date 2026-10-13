using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Phase timings of the SDK upload-connect (upload, saveL5x, parse, gate, identity) appear in the connect
/// result, in the audit <c>connect</c> event and in <c>logix_doctor</c>.
/// </summary>
[Collection("AuditLoggerDir")]
public class ConnectPhaseTimingsTests : IDisposable
{
    private readonly string _tempDir;

    public ConnectPhaseTimingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"phase-timings-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly string[] TimingFields = ["upload", "saveL5x", "parse", "gate"];

    // ── Recorder (no SDK needed) ───────────────────────────────────────────────

    /// <summary>The recorder measures each phase from its report to the next, forwards reports, and sums to the total.</summary>
    [Fact]
    public async Task PhaseRecorder_MeasuresEachPhase_ForwardsReports_AndSumsToTotal()
    {
        var forwarded = new List<string>();
        var recorder  = new ConnectPhaseRecorder(new Collect(forwarded));

        recorder.Report(ConnectPhases.For(ConnectPhases.Upload));
        await Task.Delay(40);
        recorder.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
        await Task.Delay(30);
        recorder.Report(ConnectPhases.For(ConnectPhases.Parse));
        recorder.Report(ConnectPhases.For(ConnectPhases.GateDerivation));
        await Task.Delay(50);
        var t = recorder.Snapshot(identityMs: 30);

        Assert.Equal(ConnectPhases.All, forwarded);
        Assert.InRange(t.UploadMs, 25, 5000);
        Assert.InRange(t.SaveL5xMs, 15, 5000);
        Assert.InRange(t.ParseMs, 0, 5000);
        Assert.Equal(30, t.IdentityMs);
        Assert.InRange(t.GateMs, 10, 5000);   // ~50 ms of gate phase less 30 ms of identity
        Assert.Equal(t.UploadMs + t.SaveL5xMs + t.ParseMs + t.GateMs + t.IdentityMs, t.TotalMs);
    }

    /// <summary>A phase that never started reads 0 and a recorder without a receiver is fine.</summary>
    [Fact]
    public void PhaseRecorder_UnreportedPhases_AreZero()
    {
        var t = new ConnectPhaseRecorder().Snapshot();
        Assert.Equal(new ConnectPhaseTimings(0, 0, 0, 0, 0, 0), t);
    }

    // ── logix_doctor (no SDK needed) ───────────────────────────────────────────

    /// <summary>logix_doctor shows the last connect's phase timings.</summary>
    [Fact]
    public async Task Doctor_ShowsLastConnectPhaseTimings()
    {
        var timings = new ConnectPhaseTimings(1200, 3400, 560, 7, 89, 5256);
        var env = new DoctorHandlerTests.FakeEnv
        {
            LastConnect = new ConnectTimingsSnapshot(DateTimeOffset.UtcNow, "sdk", "online-controller", timings),
        };

        var result = await DoctorHandler.ExecuteAsync(
            null, null, env, new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false));

        Assert.Equal(timings, result.LastConnect!.PhaseTimings);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var pt = json.RootElement.GetProperty("lastConnect").GetProperty("phaseTimings");
        Assert.Equal(1200, pt.GetProperty("upload").GetInt64());
        Assert.Equal(3400, pt.GetProperty("saveL5x").GetInt64());
        Assert.Equal(560, pt.GetProperty("parse").GetInt64());
        Assert.Equal(7, pt.GetProperty("gate").GetInt64());
    }

    /// <summary>Before any timed connect the doctor omits lastConnect.</summary>
    [Fact]
    public async Task Doctor_NoConnectYet_OmitsLastConnect()
    {
        var result = await DoctorHandler.ExecuteAsync(
            null, null, new DoctorHandlerTests.FakeEnv(),
            new Capabilities(LiveWritesEnabled: false, StudioAutomationEnabled: false));

        Assert.Null(result.LastConnect);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.TryGetProperty("lastConnect", out _));
    }

    private sealed class Collect(List<string> list) : IProgress<ConnectProgress>
    {
        public void Report(ConnectProgress value) => list.Add(value.Phase);
    }

#if LOGIX_SDK
    private const string CommPath = "EmulateEthernet\\192.0.2.10";

    private const string MinimalL5Xml = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1"
            TargetName="TimingsController" TargetType="Controller" ContainsContext="false">
          <Controller Name="TimingsController" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags />
            <Programs />
            <Tasks />
            <ParameterConnections />
            <Trends />
            <QuickWatchLists />
          </Controller>
        </RSLogix5000Content>
        """;

    /// <summary>Reports the upload, save-L5X and parse boundaries with a delay in each, like the SDK adapter.</summary>
    private sealed class TimedUploadFactory : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => CreateFromControllerAsync(commPath, progress: null, ct);

        public async Task<SdkSessionResult> CreateFromControllerAsync(
            string commPath, IProgress<ConnectProgress>? progress, CancellationToken ct)
        {
            progress?.Report(ConnectPhases.For(ConnectPhases.Upload));
            await Task.Delay(40, ct);
            progress?.Report(ConnectPhases.For(ConnectPhases.SaveL5x));
            await Task.Delay(40, ct);
            progress?.Report(ConnectPhases.For(ConnectPhases.Parse));
            await Task.Delay(40, ct);
            return new SdkSessionResult(
                Service:           new VirtualTagService(),
                DownloadPerformed: false,
                ModeTransitions:   [],
                CorrelationPath:   "upload-to-new-project",
                TypeProject:       L5X.Parse(MinimalL5Xml),
                AcdPath:           Path.Combine(Path.GetTempPath(), "timings-test-uploaded.ACD"));
        }
    }

    private static Task<object> Connect() =>
        ConnectHandler.ExecuteAsync(
            "", "", false, new ProjectService(), new TagSessionService(), "sdk",
            sdkFactory: new TimedUploadFactory(), sdkMode: "online", sdkCommPath: CommPath);

    /// <summary>The connect result carries phaseTimings with upload, saveL5x, parse and gate in ms.</summary>
    [Fact]
    public async Task ConnectResult_ContainsPhaseTimings()
    {
        var result = Assert.IsType<ConnectResult>(await Connect());

        Assert.NotNull(result.PhaseTimings);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var pt = json.RootElement.GetProperty("phaseTimings");
        foreach (var f in TimingFields) Assert.True(pt.TryGetProperty(f, out _), $"phaseTimings.{f} missing");
        Assert.True(pt.GetProperty("upload").GetInt64() >= 20);
        Assert.True(pt.GetProperty("saveL5x").GetInt64() >= 20);
        Assert.True(pt.GetProperty("parse").GetInt64() >= 20);
        Assert.True(pt.GetProperty("gate").GetInt64() >= 0);
    }

    /// <summary>The audit 'connect' event carries the same phaseTimings the result does.</summary>
    [Fact]
    public async Task AuditConnectEvent_ContainsPhaseTimings()
    {
        var result = Assert.IsType<ConnectResult>(await Connect());

        var line = File.ReadAllLines(Path.Combine(_tempDir, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("event").GetString() == "connect");
        var pt = line.GetProperty("phaseTimings");
        foreach (var f in TimingFields) Assert.True(pt.TryGetProperty(f, out _), $"phaseTimings.{f} missing");
        Assert.Equal(result.PhaseTimings!.UploadMs, pt.GetProperty("upload").GetInt64());
        Assert.Equal(result.PhaseTimings.SaveL5xMs, pt.GetProperty("saveL5x").GetInt64());
        Assert.Equal(result.PhaseTimings.ParseMs, pt.GetProperty("parse").GetInt64());
        Assert.Equal(result.PhaseTimings.GateMs, pt.GetProperty("gate").GetInt64());
    }

    /// <summary>After a connect the real doctor environment reports that connect's timings.</summary>
    [Fact]
    public async Task Doctor_AfterConnect_ReportsThatConnectsTimings()
    {
        var result = Assert.IsType<ConnectResult>(await Connect());

        Assert.Equal(result.PhaseTimings, AuditLogger.LastConnect!.PhaseTimings);
    }
#endif
}
