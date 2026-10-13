using System.Text.Json;
using System.Xml.Linq;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Target mnemonic-set detection: the revision sources (Controller/@MajorRev, the
/// SoftwareRevision fallback, the online upload), the iec/legacy/unknown mapping, and the
/// <c>targetMajorRevision</c> / <c>mnemonicSet</c> fields of connect, ping and doctor.
/// </summary>
[Collection("StaticHandlerHooks")]
public class TargetRevisionTests
{
    private static readonly string FixturePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static XElement Root(string? majorRev, string? softwareRevision)
    {
        var root = new XElement("RSLogix5000Content");
        if (softwareRevision is not null) root.SetAttributeValue("SoftwareRevision", softwareRevision);
        var controller = new XElement("Controller", new XAttribute("Name", "C"));
        if (majorRev is not null) controller.SetAttributeValue("MajorRev", majorRev);
        root.Add(controller);
        return root;
    }

    private static string L5xText(string? majorRev, string? softwareRevision)
    {
        var sw = softwareRevision is null ? "" : $" SoftwareRevision=\"{softwareRevision}\"";
        var mr = majorRev is null ? "" : $" MajorRev=\"{majorRev}\"";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
               $"<RSLogix5000Content SchemaRevision=\"1.0\"{sw} TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">\n" +
               $"  <Controller Name=\"C\"{mr} ProcessorType=\"1756-L84E\"/>\n" +
               $"</RSLogix5000Content>";
    }

    private static string WriteTemp(string? majorRev, string? softwareRevision)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lgx-rev-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, L5xText(majorRev, softwareRevision));
        return path;
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(36, "iec")]
    [InlineData(37, "iec")]
    [InlineData(39, "iec")]
    [InlineData(35, "legacy")]
    [InlineData(32, "legacy")]
    [InlineData(20, "legacy")]
    public void MnemonicSetFor_Maps36AndAboveToIec_35AndBelowToLegacy(int major, string expected) =>
        Assert.Equal(expected, TargetRevision.MnemonicSetFor(major));

    [Fact]
    public void MnemonicSetFor_Null_IsUnknown() =>
        Assert.Equal("unknown", TargetRevision.MnemonicSetFor(null));

    // ── Source 1: Controller/@MajorRev ────────────────────────────────────────

    [Fact]
    public void Detect_MajorRev_IsUsed()
    {
        var info = TargetRevision.Detect(Root(majorRev: "36", softwareRevision: "36.11"));

        Assert.Equal(36, info.MajorRevision);
        Assert.Equal("iec", info.MnemonicSet);
        Assert.Equal(TargetRevisionSource.MajorRev, info.Source);
        Assert.Null(info.Warning);
    }

    [Fact]
    public void Detect_MajorRev_WinsOverSoftwareRevision()
    {
        var info = TargetRevision.Detect(Root(majorRev: "35", softwareRevision: "36.11"));

        Assert.Equal(35, info.MajorRevision);
        Assert.Equal("legacy", info.MnemonicSet);
        Assert.Equal(TargetRevisionSource.MajorRev, info.Source);
    }

    // ── Source 2: SoftwareRevision fallback ───────────────────────────────────

    [Theory]
    [InlineData("36.11", 36, "iec")]
    [InlineData("35.00", 35, "legacy")]
    [InlineData("37", 37, "iec")]
    public void Detect_SoftwareRevision_IntegerPartWhenMajorRevAbsent(string revision, int major, string set)
    {
        var info = TargetRevision.Detect(Root(majorRev: null, softwareRevision: revision));

        Assert.Equal(major, info.MajorRevision);
        Assert.Equal(set, info.MnemonicSet);
        Assert.Equal(TargetRevisionSource.SoftwareRevision, info.Source);
        Assert.Null(info.Warning);
    }

    [Fact]
    public void Detect_PartialExportWithoutController_UsesSoftwareRevision()
    {
        var root = new XElement("RSLogix5000Content",
            new XAttribute("SoftwareRevision", "36.2"), new XAttribute("TargetType", "Routine"));

        var info = TargetRevision.Detect(root);

        Assert.Equal(36, info.MajorRevision);
        Assert.Equal(TargetRevisionSource.SoftwareRevision, info.Source);
    }

    [Fact]
    public void Detect_BlankMajorRev_FallsBackToSoftwareRevision()
    {
        var info = TargetRevision.Detect(Root(majorRev: " ", softwareRevision: "34.01"));

        Assert.Equal(34, info.MajorRevision);
        Assert.Equal("legacy", info.MnemonicSet);
        Assert.Equal(TargetRevisionSource.SoftwareRevision, info.Source);
    }

    // ── Source 3: the online upload ───────────────────────────────────────────

    [Fact]
    public void Detect_OnlineUpload_UsesUploadedProjectsValue()
    {
        var uploaded = L5X.Parse(L5xText(majorRev: "37", softwareRevision: "37.0"));

        var info = TargetRevision.Detect(uploaded, uploaded: true);

        Assert.Equal(37, info.MajorRevision);
        Assert.Equal("iec", info.MnemonicSet);
        Assert.Equal(TargetRevisionSource.UploadedPrefix + TargetRevisionSource.MajorRev, info.Source);
    }

    [Fact]
    public void RegisterFromUpload_RecordsUploadedRevisionOnProjectService()
    {
        var svc = new ProjectService();

        svc.RegisterFromUpload(L5X.Parse(L5xText("34", "34.01")), "/tmp/up.ACD", "AB_ETHIP-1\\10.0.0.5");

        Assert.Equal(34, svc.TargetRevision!.MajorRevision);
        Assert.Equal("legacy", svc.TargetRevision.MnemonicSet);
        Assert.StartsWith(TargetRevisionSource.UploadedPrefix, svc.TargetRevision.Source);
    }

    // ── Unknown ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("abc", "x.y")]
    [InlineData("0", "0.0")]
    public void Detect_NeitherAttribute_IsUnknownWithWarning(string? majorRev, string? softwareRevision)
    {
        var info = TargetRevision.Detect(Root(majorRev, softwareRevision));

        Assert.Null(info.MajorRevision);
        Assert.Equal("unknown", info.MnemonicSet);
        Assert.Null(info.Set);
        Assert.False(string.IsNullOrWhiteSpace(info.Warning));
    }

    [Fact]
    public void Detect_NullRoot_IsUnknownWithWarning()
    {
        var info = TargetRevision.Detect((XElement?)null);

        Assert.Equal("unknown", info.MnemonicSet);
        Assert.NotNull(info.Warning);
    }

    [Fact]
    public void Set_MapsToSharedEnum()
    {
        Assert.Equal(MnemonicSet.Iec, TargetRevision.Detect(Root("36", null)).Set);
        Assert.Equal(MnemonicSet.Legacy, TargetRevision.Detect(Root("35", null)).Set);
    }

    // ── ProjectService ────────────────────────────────────────────────────────

    [Fact]
    public void ProjectService_Load_RecordsRevision_ClearResets()
    {
        var svc = new ProjectService();
        Assert.Null(svc.TargetRevision);

        svc.Load(FixturePath);   // Demo.L5X: MajorRev="36"
        Assert.Equal(36, svc.TargetRevision!.MajorRevision);
        Assert.Equal(TargetRevisionSource.MajorRev, svc.TargetRevision.Source);

        svc.Clear();
        Assert.Null(svc.TargetRevision);
    }

    [Fact]
    public void ProjectService_Load_SoftwareRevisionFallback()
    {
        var path = WriteTemp(majorRev: null, softwareRevision: "35.01");
        try
        {
            var svc = new ProjectService();
            svc.Load(path);

            Assert.Equal(35, svc.TargetRevision!.MajorRevision);
            Assert.Equal("legacy", svc.TargetRevision.MnemonicSet);
            Assert.Equal(TargetRevisionSource.SoftwareRevision, svc.TargetRevision.Source);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Current_PrefersSession_ThenProject_ThenNoTarget()
    {
        var project  = new ProjectService();
        var sessions = new TagSessionService();
        Assert.Same(TargetRevision.NoTarget, TargetRevision.Current(sessions, project));

        project.Load(FixturePath);
        Assert.Equal(36, TargetRevision.Current(sessions, project).MajorRevision);

        sessions.SetTargetRevision(TargetRevision.Detect(Root("35", null)));
        Assert.Equal(35, TargetRevision.Current(sessions, project).MajorRevision);
    }

    // ── logix_connect ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_Virtual_ReportsMajorRevAndMnemonicSet_AndRecordsOnSession()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var sessions = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "198.51.100.1", "1,0", useVirtual: true, project, sessions);

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal(36, ok.TargetMajorRevision);
        Assert.Equal("iec", ok.MnemonicSet);
        Assert.Equal(36, sessions.TargetRevision!.MajorRevision);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok));
        Assert.Equal(36, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("iec", doc.RootElement.GetProperty("mnemonicSet").GetString());
    }

    [Fact]
    public async Task Connect_L5x_SoftwareRevisionFallback_Legacy()
    {
        var path = WriteTemp(majorRev: null, softwareRevision: "35.00");
        try
        {
            var project = new ProjectService();
            project.Load(path);
            var sessions = new TagSessionService();

            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, project, sessions, backend: "l5x");

            var ok = Assert.IsType<ConnectResult>(result);
            Assert.Equal(35, ok.TargetMajorRevision);
            Assert.Equal("legacy", ok.MnemonicSet);
            Assert.True(ok.Warnings is null || ok.Warnings.Length == 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Connect_UnknownRevision_ReportsUnknownWithWarning()
    {
        var path = WriteTemp(majorRev: null, softwareRevision: null);
        try
        {
            var project = new ProjectService();
            project.Load(path);
            var sessions = new TagSessionService();

            var result = await ConnectHandler.ExecuteAsync(
                "", "", useVirtual: false, project, sessions, backend: "l5x");

            var ok = Assert.IsType<ConnectResult>(result);
            Assert.Null(ok.TargetMajorRevision);
            Assert.Equal("unknown", ok.MnemonicSet);
            Assert.NotNull(ok.Warnings);
            Assert.Contains(ok.Warnings!, w => w.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        }
        finally { File.Delete(path); }
    }

#if LOGIX_SDK
    /// <summary>Upload factory returning a canned uploaded project.</summary>
    private sealed class UploadFactory(L5X uploaded) : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(string projectFilePath, L5X typeProject,
            string sdkMode = "offline", bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default) =>
            Task.FromResult(new SdkSessionResult(
                Service: new VirtualTagService(), DownloadPerformed: false, ModeTransitions: [],
                CorrelationPath: "upload-to-new-project", TypeProject: uploaded, AcdPath: "/tmp/uploaded.ACD"));
    }

    /// <summary>Online connect uses the uploaded project's revision, even with another project open.</summary>
    [Fact]
    public async Task Connect_SdkOnlineUpload_UsesUploadedProjectsRevision()
    {
        var project = new ProjectService();
        project.Load(FixturePath);   // MajorRev 36 — must not be the source
        var sessions = new TagSessionService();

        var result = await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, project, sessions, backend: "sdk",
            sdkFactory: new UploadFactory(L5X.Parse(L5xText("34", "34.01"))),
            sdkMode: "online", sdkCommPath: "EmulateEthernet\\192.0.2.10");

        var ok = Assert.IsType<ConnectResult>(result);
        Assert.Equal(34, ok.TargetMajorRevision);
        Assert.Equal("legacy", ok.MnemonicSet);
        Assert.Equal(34, sessions.TargetRevision!.MajorRevision);
    }
#endif

    // ── logix_ping ────────────────────────────────────────────────────────────

    [Fact]
    public void Ping_ReportsTargetRevisionAndMnemonicSet()
    {
        var json = LogixTools.GetPingResponse(new Capabilities(LiveWritesEnabled: false),
            TargetRevision.Detect(Root("36", null)));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(36, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("iec", doc.RootElement.GetProperty("mnemonicSet").GetString());
        Assert.False(doc.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void Ping_NoTarget_IsUnknownWithoutWarning()
    {
        using var doc = JsonDocument.Parse(PingJson(null));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("targetMajorRevision").ValueKind);
        Assert.Equal("unknown", doc.RootElement.GetProperty("mnemonicSet").GetString());
        Assert.False(doc.RootElement.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void Ping_UnknownRevisionOfOpenTarget_CarriesWarning()
    {
        using var doc = JsonDocument.Parse(PingJson(TargetRevision.Detect(Root(null, null))));

        Assert.Equal("unknown", doc.RootElement.GetProperty("mnemonicSet").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("warnings").GetArrayLength());
    }

    private static string PingJson(TargetRevisionInfo? target) =>
        LogixTools.GetPingResponse(null, target);

    [Fact]
    public void Ping_Tool_UsesCurrentTargetProvider()
    {
        var previous = LogixTools.CurrentTarget;
        try
        {
            LogixTools.CurrentTarget = () => TargetRevision.Detect(Root("35", null));
            using var doc = JsonDocument.Parse(LogixTools.Ping());
            Assert.Equal(35, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
            Assert.Equal("legacy", doc.RootElement.GetProperty("mnemonicSet").GetString());
        }
        finally { LogixTools.CurrentTarget = previous; }
    }

    // ── logix_doctor ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_ReportsTargetRevisionFromOpenProject()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var tools = new LogixDoctorTools(new TagSessionService(), new Capabilities(false), project);

        using var doc = JsonDocument.Parse(await tools.GetDoctorResponseAsync());

        Assert.Equal(36, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("iec", doc.RootElement.GetProperty("mnemonicSet").GetString());
    }

    [Fact]
    public async Task Doctor_SessionRevisionWinsOverProject()
    {
        var project = new ProjectService();
        project.Load(FixturePath);
        var sessions = new TagSessionService();
        sessions.SetTargetRevision(TargetRevision.Detect(Root("35", null), uploaded: true));
        var tools = new LogixDoctorTools(sessions, new Capabilities(false), project);

        using var doc = JsonDocument.Parse(await tools.GetDoctorResponseAsync());

        Assert.Equal(35, doc.RootElement.GetProperty("targetMajorRevision").GetInt32());
        Assert.Equal("legacy", doc.RootElement.GetProperty("mnemonicSet").GetString());
    }

    [Fact]
    public async Task Doctor_NoTarget_IsUnknown()
    {
        var tools = new LogixDoctorTools(new TagSessionService(), new Capabilities(false), new ProjectService());

        using var doc = JsonDocument.Parse(await tools.GetDoctorResponseAsync());

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("targetMajorRevision").ValueKind);
        Assert.Equal("unknown", doc.RootElement.GetProperty("mnemonicSet").GetString());
    }
}
