using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// The twelve core readers answer "no project loaded" with the same typed
/// <c>{error, kind: NoProjectLoaded, hint}</c> shape. The hint is the offline text on a fresh server and the
/// closed-session text after <c>logix_close_project</c> ended a session. The id-less
/// <c>logix_connect_status</c> then answers <c>closed</c>.
/// </summary>
public class NoProjectLoadedTypingTests : IDisposable
{
    private readonly string _tempDir;

    public NoProjectLoadedTypingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"noproject-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        AuditLogger.OverrideLogDir(_tempDir);
    }

    public void Dispose()
    {
        AuditLogger.OverrideLogDir(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Every core reader, called against an unloaded service.</summary>
    public static IEnumerable<object[]> Readers() => new[]
    {
        Reader("logix_list_modules",     s => ListModulesHandler.Execute(s)),
        Reader("logix_document",         s => DocumentHandler.Execute(s)),
        Reader("logix_get_routine",      s => GetRoutineHandler.Execute(s, "Main", "Main")),
        Reader("logix_gate_report",      s => GateReportHandler.Execute(s)),
        Reader("logix_lint",             s => LintHandler.Execute(s)),
        Reader("logix_cross_reference",  s => CrossReferenceHandler.Execute(s, "Motor_Run")),
        Reader("logix_project_summary",  s => ProjectSummaryHandler.Execute(s)),
        Reader("logix_find_tags",        s => FindTagsHandler.Execute(s)),
        Reader("logix_list_datatypes",   s => ListDatatypesHandler.Execute(s)),
        Reader("logix_trace_origin",     s => TraceOriginHandler.Execute(s, "Motor_Run")),
        Reader("logix_get_rung_diagram", s => RungDiagramHandler.Execute(s, "Main", "Main", "0", "text")),
        Reader("logix_verify_interlock", s => VerifyInterlockHandler.Execute(s, "Motor_Run", "Guard_Ok")),
    };

    private static object[] Reader(string name, Func<ProjectService, object> call) => new object[] { name, call };

    private static JsonElement Json(object value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType()));
        return doc.RootElement.Clone();
    }

    private static string Fixture() => Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "RungDiagram.L5X");

    [Fact]
    public void Readers_CoverTwelveTools() => Assert.Equal(12, Readers().Count());

    [Theory]
    [MemberData(nameof(Readers))]
    public void Reader_WithNoProject_ReturnsNoProjectLoadedWithTheOfflineHint(string tool, Func<ProjectService, object> call)
    {
        var r = Json(call(new ProjectService()));

        Assert.Equal(ErrorKinds.NoProjectLoaded, r.GetProperty("kind").GetString());
        Assert.Contains("No project loaded", r.GetProperty("error").GetString());
        Assert.Contains(ProjectService.OfflineHint, r.GetProperty("hint").GetString());
        Assert.Contains("logix_open_project", r.GetProperty("hint").GetString());
        Assert.False(string.IsNullOrEmpty(tool));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Reader_AfterASessionClose_ReturnsNoProjectLoadedWithTheClosedHint(string tool, Func<ProjectService, object> call)
    {
        var service  = new ProjectService();
        var sessions = new TagSessionService();
        service.Load(Fixture());
        sessions.Attach(new SessionStubTagService(File.ReadAllText(Fixture())), isVirtual: false);

        Assert.IsType<CloseProjectResult>(CloseProjectHandler.Execute(service, sessions));

        var r = Json(call(service));

        Assert.Equal(ErrorKinds.NoProjectLoaded, r.GetProperty("kind").GetString());
        var hint = r.GetProperty("hint").GetString()!;
        Assert.Contains("closed", hint);
        Assert.Contains("logix_close_project", hint);
        Assert.Contains("logix_connect", hint);
        Assert.DoesNotContain(ProjectService.OfflineHint, hint);
        Assert.False(string.IsNullOrEmpty(tool));
    }

    [Fact]
    public void LoadingAProjectAgain_ClearsTheClosedHint()
    {
        var service  = new ProjectService();
        var sessions = new TagSessionService();
        service.Load(Fixture());
        sessions.Attach(new SessionStubTagService(File.ReadAllText(Fixture())), isVirtual: false);
        CloseProjectHandler.Execute(service, sessions);

        service.Load(Fixture());
        service.Clear();

        Assert.Equal(ProjectService.OfflineHint, service.NoProjectLoadedError("logix_lint").Hint);
    }

    [Fact]
    public async Task IdLessConnectStatus_AnswersClosed_AfterLogixCloseProject()
    {
        var service  = new ProjectService();
        var sessions = new TagSessionService();
        var state    = new ConnectState();
        sessions.ConnectState = state;
        service.Load(Fixture());
        sessions.Attach(new SessionStubTagService(File.ReadAllText(Fixture())), isVirtual: false);

        CloseProjectHandler.Execute(service, sessions);

        using var doc = JsonDocument.Parse(await state.GetStatusJsonAsync());
        Assert.Equal("closed", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("closedAt", out _));
        Assert.Contains("logix_connect", doc.RootElement.GetProperty("hint").GetString());
    }
}
