#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change requirement 4 (task logix-mcp-64f5.4): an empty controller publish is refused as
/// <c>NothingStaged</c> without any import call, a connect that replaces a session holding staged rungs
/// warns that they were discarded, and the reconnect message names <c>logix_open_project</c> only when
/// that tool opened the project.
/// </summary>
[Collection("StaticHandlerHooks")]
public class StagedBufferHonestOutcomesTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string EmptyL5X =
        "<RSLogix5000Content><Controller Name=\"C\"/></RSLogix5000Content>";

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_, _) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    [Fact]
    public async Task EmptyControllerPublish_ReturnsNothingStaged_AndMakesNoImportCall()
    {
        var (session, fake) = await OpenSessionAsync();
        var auditDir = Path.Combine(Path.GetTempPath(), "lgx-nothing-staged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(auditDir);
        AuditLogger.OverrideLogDir(auditDir);
        try
        {
            var result = await ProposeRungEditHandler.ExecuteAsync(
                ValidXPath, 0, 0, "", session, target: "controller");

            var error = Assert.IsType<ProposeRungEditError>(result);
            Assert.Equal("NothingStaged", error.Kind);
            Assert.Empty(fake.CapturedImportOptions);
            Assert.Empty(fake.CapturedXPaths.Where(x => x.Contains("Import", StringComparison.OrdinalIgnoreCase)));

            // Exactly one rung_edit audit line, outcome "nothing-staged" (never "proposed").
            var edits = File.ReadAllLines(Path.Combine(auditDir, "audit.jsonl"))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l))
                .Where(d => d.RootElement.GetProperty("event").GetString() == "rung_edit")
                .ToList();
            var line = Assert.Single(edits);
            Assert.Equal("nothing-staged", line.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            AuditLogger.OverrideLogDir(null);
            try { Directory.Delete(auditDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task EmptyControllerPublish_WithStagedTag_IsNotNothingStaged()
    {
        var (session, fake) = await OpenSessionAsync();
        session.StagedTagBuffer["BufferedTag1"] = new System.Xml.Linq.XElement(
            "Tag", new System.Xml.Linq.XAttribute("Name", "BufferedTag1"),
            new System.Xml.Linq.XAttribute("DataType", "BOOL"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, "", session, target: "controller");

        Assert.IsNotType<ProposeRungEditError>(result);
        Assert.NotEmpty(fake.CapturedImportOptions);
    }

    [Fact]
    public async Task SessionReplace_DiscardingNonEmptyBuffer_ReturnsDiscardedBufferWarning()
    {
        var (session, _) = await OpenSessionAsync();
        session.StagedRungBuffer[(ValidXPath, 0u)] = ["XIC(A)OTE(B);", "XIC(C)OTE(D);"];
        session.StagedRungCount = 2;
        session.HasStagedRungEdit = true;

        var tags = new TagSessionService();
        tags.Attach(new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X), LogixProject.OperationMode.Online),
            isVirtual: false, isLiveController: true);

        var saved = CloseProjectHandler.SdkSessionInspector;
        try
        {
            CloseProjectHandler.SdkSessionInspector = CloseProjectSdkBridge.Inspect;
            var projects = new ProjectService();
            projects.Load(FixturePath);

            var connect = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, tags);

            var ok = Assert.IsType<ConnectResult>(connect);
            var warning = Assert.Single(ok.Warnings!);
            Assert.Equal(
                "2 staged rungs in MainProgram/MainRoutine were discarded by the new session; re-stage them",
                warning);
            Assert.Contains(warning, ok.Message, StringComparison.Ordinal);
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = saved;
        }
    }

    [Fact]
    public void DiscardedStagedBufferWarning_SingularRung_UsesSingularWording()
    {
        var info = new SdkSessionCloseInfo(0, true, StagedRungCount: 1, StagedRoutines: ["MainProgram/MainRoutine"]);

        var warning = ConnectHandler.DiscardedStagedBufferWarning(_ => info, new TagSessionService());

        Assert.Equal(
            "1 staged rung in MainProgram/MainRoutine was discarded by the new session; re-stage them",
            warning);
    }

    [Fact]
    public async Task SessionReplace_WithEmptyBuffer_AddsNoDiscardedBufferWarning()
    {
        var (session, _) = await OpenSessionAsync();
        var tags = new TagSessionService();
        tags.Attach(new TypedSdkTagService(session, L5Sharp.Core.L5X.Parse(EmptyL5X), LogixProject.OperationMode.Online),
            isVirtual: false, isLiveController: true);

        var saved = CloseProjectHandler.SdkSessionInspector;
        try
        {
            CloseProjectHandler.SdkSessionInspector = CloseProjectSdkBridge.Inspect;
            var projects = new ProjectService();
            projects.Load(FixturePath);

            var connect = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: true, projects, tags);

            var ok = Assert.IsType<ConnectResult>(connect);
            Assert.True(ok.Warnings is null || !ok.Warnings.Any(w => w.Contains("discarded")));
        }
        finally
        {
            CloseProjectHandler.SdkSessionInspector = saved;
        }
    }

    /// <summary>Upload factory that hands back the Demo project so the connect registers it from the upload.</summary>
    private sealed class UploadingFactory : ISdkTagServiceFactory
    {
        public Task<SdkSessionResult> CreateAsync(
            string projectFilePath, L5Sharp.Core.L5X typeProject, string sdkMode = "offline",
            bool allowControllerDownload = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SdkSessionResult> CreateFromControllerAsync(string commPath, CancellationToken ct = default)
            => Task.FromResult(new SdkSessionResult(
                Service:           new VirtualTagService(),
                DownloadPerformed: false,
                ModeTransitions:   [],
                CorrelationPath:   "upload-to-new-project",
                TypeProject:       L5Sharp.Core.L5X.Load(FixturePath),
                AcdPath:           "uploaded.ACD"));
    }

    [Fact]
    public async Task ReconnectMessage_DoesNotMentionOpenProject_UnlessThatToolWasCalled()
    {
        const string commPath = "EmulateEthernet\\192.0.2.50";
        var projects = new ProjectService();
        var tags     = new TagSessionService();

        // First connect uploads the controller project; no logix_open_project call anywhere.
        var first = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadingFactory(),
            sdkMode: "online", sdkCommPath: commPath));
        Assert.DoesNotContain("logix_open_project", first.Message, StringComparison.Ordinal);

        // Reconnect: the project is still loaded, but from the upload, not from logix_open_project.
        var second = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, projects, tags, backend: "sdk", sdkFactory: new UploadingFactory(),
            sdkMode: "online", sdkCommPath: commPath));
        Assert.DoesNotContain("logix_open_project", second.Message, StringComparison.Ordinal);
        Assert.Contains("uploaded by an earlier connect", second.Message, StringComparison.Ordinal);

        // When logix_open_project did open the project, the message may (and does) name it.
        var opened = new ProjectService();
        opened.Load(FixturePath);
        var third = Assert.IsType<ConnectResult>(await ConnectHandler.ExecuteAsync(
            "", "", useVirtual: false, opened, new TagSessionService(), backend: "sdk",
            sdkFactory: new UploadingFactory(), sdkMode: "online", sdkCommPath: commPath));
        Assert.Contains("logix_open_project", third.Message, StringComparison.Ordinal);
    }
}
#endif
