using System.Reflection;
using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The read tools (<c>logix_get_routine</c>, <c>logix_get_rung_diagram</c>, <c>logix_document</c>,
/// <c>logix_find_tags</c>, <c>logix_diff_against_controller</c>) refresh a stale session image before they answer, as
/// <c>logix_lint</c> and <c>logix_cross_reference</c> do; a failed refresh is not an error and the result carries the
/// stale note.
/// </summary>
public class ReadToolsRefreshFirstTests
{
    private const string Tag = "<Tag Name=\"{0}\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
                               "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    /// <summary>Builds a one-program L5X; each routine is (name, rungs) and each rung is (text, Type code).</summary>
    private static L5X Image(string[] tags, params (string Name, (string Text, string Type)[] Rungs)[] routines)
    {
        var tagXml = string.Concat(tags.Select(t => string.Format(Tag, t)));
        var rtnXml = string.Concat(routines.Select(r =>
            $"<Routine Name=\"{r.Name}\" Type=\"RLL\"><RLLContent>" +
            string.Concat(r.Rungs.Select((g, i) =>
                $"<Rung Number=\"{i}\" Type=\"{g.Type}\"><Text><![CDATA[{g.Text}]]></Text></Rung>")) +
            "</RLLContent></Routine>"));
        return L5X.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions />" +
            $"<Tags>{tagXml}</Tags>" +
            "<Programs><Program Name=\"MainProgram\" Type=\"Normal\" TestEdits=\"false\" Disabled=\"false\" UseAsFolder=\"false\" MainRoutineName=\"Main\">" +
            $"<Tags /><Routines>{rtnXml}</Routines></Program></Programs></Controller></RSLogix5000Content>");
    }

    private static (string, (string, string)[]) MainR(params (string, string)[] rungs) => ("Main", rungs);

    /// <summary>A controller that exports whatever image it currently holds.</summary>
    private sealed class ScriptedSource(L5X controller) : IControllerImageSource
    {
        public L5X Controller { get; set; } = controller;
        public int Exports { get; private set; }
        public Exception? Fail { get; set; }

        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default)
        {
            Exports++;
            return Fail is null ? Task.FromResult(Controller.ToString()) : Task.FromException<string>(Fail);
        }

        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default)
        {
            Exports++;
            return Fail is null ? Task.FromResult(Controller.ToString()) : Task.FromException<string>(Fail);
        }
    }

    /// <summary>An online session whose registered upload is <paramref name="connected"/>.</summary>
    private static (LogixProjectTools Tools, TagSessionService Session, ScriptedSource Source) Connect(L5X connected, L5X controller)
    {
        var source  = new ScriptedSource(controller);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(connected, DateTimeOffset.UtcNow);
        session.ImageSourceOverride = source;
        var service = new ProjectService();
        service.RegisterFromUpload(connected, "C.ACD");
        return (new LogixProjectTools(service, session), session, source);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string[] RungTexts(JsonElement routine) =>
        routine.GetProperty("rungs").EnumerateArray().Select(r => r.GetProperty("text").GetString()!).ToArray();

    [Fact]
    public async Task GetRoutine_StaleSessionWithScriptedSource_ReturnsRefreshedRungs()
    {
        var connected = Image(["A", "B", "C"], MainR(("XIC(A)OTE(B);", "N")));
        var published = Image(["A", "B", "C"],
            MainR(("XIC(A)OTE(B);", "N"), ("XIC(C)OTE(B);", "N")));
        var (tools, session, source) = Connect(connected, published);

        // A read before the edit starts the applied-edit baseline, as in a real session.
        Assert.Single(RungTexts(Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"))));

        session.RecordLiveEdit("MainProgram/Main");
        Assert.True(session.IsImageStale);

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(2, RungTexts(routine).Length);
        Assert.Equal("session-image", routine.GetProperty("source").GetString());
        Assert.False(routine.TryGetProperty("imageStale", out _));
        Assert.False(session.IsImageStale);
        Assert.Equal(1, source.Exports);   // one program export, no controller tags
    }

    [Fact]
    public async Task GetRoutine_RoutineCreatedAfterConnect_IsListedAfterRefresh()
    {
        var connected = Image(["A", "B"], MainR(("XIC(A)OTE(B);", "N")));
        var published = Image(["A", "B"],
            MainR(("XIC(A)OTE(B);", "N")),
            ("Created", [("XIC(B)OTE(A);", "N")]));
        var (tools, session, _) = Connect(connected, published);
        _ = await tools.GetGetRoutineResponseAsync("MainProgram", "Main");

        session.RecordLiveEdit("MainProgram/Created");
        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Created"));

        Assert.Equal(["XIC(B)OTE(A);"], RungTexts(routine));
        Assert.Equal("session-image", routine.GetProperty("source").GetString());
    }

    [Fact]
    public async Task GetRoutine_AcceptedZone_ShowsAcceptedRungs()
    {
        var connected = Image(["A", "B", "C"], MainR(("XIC(A)OTE(B);", "N")));
        // The controller holds a pending insert (e) and a displaced original (rN is the accepted rung kept as is).
        var staged = Image(["A", "B", "C"],
            MainR(("XIC(A)OTE(B);", "N"), ("XIC(C)OTE(B);", "e")));
        var (tools, session, _) = Connect(connected, staged);
        _ = await tools.GetGetRoutineResponseAsync("MainProgram", "Main");

        session.RecordLiveEdit("MainProgram/Main");
        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        // The pending rung is never shown; the accepted rungs are.
        Assert.Equal(["XIC(A)OTE(B);"], RungTexts(routine));
    }

    [Fact]
    public async Task GetRoutine_FailingSource_ReturnsOldRungsWithStaleNote()
    {
        var connected = Image(["A", "B", "C"], MainR(("XIC(A)OTE(B);", "N")));
        var published = Image(["A", "B", "C"],
            MainR(("XIC(A)OTE(B);", "N"), ("XIC(C)OTE(B);", "N")));
        var (tools, session, source) = Connect(connected, published);
        _ = await tools.GetGetRoutineResponseAsync("MainProgram", "Main");

        source.Fail = new InvalidOperationException("export refused");
        session.RecordLiveEdit("MainProgram/Main");
        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(["XIC(A)OTE(B);"], RungTexts(routine));
        Assert.True(routine.GetProperty("imageStale").GetBoolean());
        Assert.Contains("export refused", routine.GetProperty("staleReason").GetString());
        Assert.True(session.IsImageStale);
        Assert.Contains("controller image is stale", session.ImageStaleNote);
    }

    [Fact]
    public async Task EveryReadToolWrapper_RefreshesBeforeTheHandler()
    {
        var connected = Image(["A", "B"], MainR(("XIC(A)OTE(B);", "N")));
        var path = Path.Combine(Path.GetTempPath(), $"lgx-refresh-{Guid.NewGuid():N}.L5X");
        connected.Save(path);
        try
        {
            var calls = new Dictionary<string, Func<LogixProjectTools, Task<string>>>
            {
                ["get_routine"]   = t => t.GetGetRoutineResponseAsync("MainProgram", "Main"),
                ["find_tags"]     = t => t.GetFindTagsResponseAsync("*", null),
                ["document"]      = t => t.GetDocumentResponseAsync(),
                ["diff"]          = t => t.GetDiffAgainstControllerResponseAsync(path),
                ["rung_diagram"]  = t => t.GetGetRungDiagramResponseAsync("MainProgram", "Main", "0"),
            };
            foreach (var (name, call) in calls)
            {
                var (tools, session, source) = Connect(connected, connected);
                session.RecordLiveEdit("MainProgram/Main");
                Assert.True(session.IsImageStale, name);

                _ = await call(tools);

                Assert.False(session.IsImageStale, name);
                Assert.True(source.Exports > 0, name);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadToolEntryPoints_AreAsync()
    {
        foreach (var name in new[] { "GetRoutine", "GetRungDiagram", "Document", "FindTags", "DiffAgainstController" })
        {
            var method = typeof(LogixProjectTools).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<string>), method!.ReturnType);
        }
    }

    [Fact]
    public async Task GetRoutine_SessionWithoutFingerprintSource_NeverProbesAndNeverStampsAReason()
    {
        var connected = Image(["A", "B", "C"], MainR(("XIC(A)OTE(B);", "N")));
        var (tools, session, source) = Connect(connected, connected);
        Assert.Null(session.ControllerFingerprintSource);

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.False(routine.TryGetProperty("imageRefreshedBecause", out _));
        Assert.Null(session.FingerprintAgeMs);
        Assert.Equal(0, source.Exports);
    }
}
