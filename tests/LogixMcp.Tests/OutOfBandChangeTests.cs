using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The session image notices changes made to the controller outside this session: a fingerprint probe
/// (routine names, rung counts, TestEdits, controller identity) compared with the image marks the changed programs stale so
/// the existing refresh runs before the reader answers; a different controller or a re-download makes image readers refuse
/// with <c>ControllerChanged</c> until <c>logix_connect</c> runs again.
/// </summary>
public class OutOfBandChangeTests
{
    private const string Reason = "controller changed outside this session";

    private const string Tag = "<Tag Name=\"{0}\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
                               "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    /// <summary>A controller image with the programs given as (name, routines of rung count).</summary>
    private static L5X Image(string controller, string serial, params (string Program, (string Routine, int Rungs)[] Routines)[] programs)
    {
        var tags = string.Concat(new[] { "A", "B" }.Select(t => string.Format(Tag, t)));
        var progXml = string.Concat(programs.Select(p =>
            $"<Program Name=\"{p.Program}\" Type=\"Normal\" TestEdits=\"false\" Disabled=\"false\" UseAsFolder=\"false\" MainRoutineName=\"Main\">" +
            "<Tags /><Routines>" +
            string.Concat(p.Routines.Select(r =>
                $"<Routine Name=\"{r.Routine}\" Type=\"RLL\"><RLLContent>" +
                string.Concat(Enumerable.Range(0, r.Rungs).Select(i =>
                    $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[XIC(A)OTE(B);]]></Text></Rung>")) +
                "</RLLContent></Routine>")) +
            "</Routines></Program>"));
        return L5X.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            $"<Controller Name=\"{controller}\" ProjectSN=\"{serial}\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions />" +
            $"<Tags>{tags}</Tags><Programs>{progXml}</Programs></Controller></RSLogix5000Content>");
    }

    private static L5X Standard(int mainRungs = 1, bool extraRoutine = false) =>
        Image("C", "16#0000_00AA",
            ("MainProgram", extraRoutine ? [("Main", mainRungs), ("Added", 1)] : [("Main", mainRungs)]),
            ("Other", [("Main", 1)]));

    /// <summary>The controller as the fake fingerprint source and the program export see it.</summary>
    private sealed class FakeController(L5X controller) : IControllerImageSource
    {
        public L5X Controller { get; set; } = controller;
        public int Probes { get; private set; }
        public int Exports { get; private set; }
        public bool FailProbe { get; set; }

        public Task<ControllerFingerprint?> Fingerprint(CancellationToken ct)
        {
            Probes++;
            if (FailProbe) throw new InvalidOperationException("probe failed");
            return Task.FromResult<ControllerFingerprint?>(ControllerFingerprint.FromL5x(Controller.ToString()));
        }

        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default)
        {
            Exports++;
            return Task.FromResult(Controller.ToString());
        }

        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default)
        {
            Exports++;
            return Task.FromResult(Controller.ToString());
        }
    }

    private static (LogixProjectTools Tools, TagSessionService Session, FakeController Controller) Connect(L5X connected, L5X? controller = null)
    {
        var fake    = new FakeController(controller ?? connected);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(connected, DateTimeOffset.UtcNow);
        session.ImageSourceOverride        = fake;
        session.ControllerFingerprintSource = fake.Fingerprint;
        var service = new ProjectService();
        service.RegisterFromUpload(connected, "C.ACD");
        return (new LogixProjectTools(service, session), session, fake);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ChangedRungCount_RefreshesThatProgramAndStampsTheReason()
    {
        var (tools, session, controller) = Connect(Standard(mainRungs: 1));
        controller.Controller = Standard(mainRungs: 2);   // another party added a rung to MainProgram/Main

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(2, routine.GetProperty("rungs").GetArrayLength());
        Assert.Equal(Reason, routine.GetProperty("imageRefreshedBecause").GetString());
        Assert.Equal(1, controller.Exports);              // only MainProgram, not Other
        Assert.False(session.IsImageStale);
        Assert.Null(session.ControllerChange);

        // Only the first read after the refresh reports it.
        var again = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));
        Assert.False(again.TryGetProperty("imageRefreshedBecause", out _));
    }

    [Fact]
    public async Task UnchangedController_NoRefreshAndNoReason()
    {
        var (tools, _, controller) = Connect(Standard());

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.False(routine.TryGetProperty("imageRefreshedBecause", out _));
        Assert.Equal(1, controller.Probes);
        Assert.Equal(0, controller.Exports);
    }

    [Fact]
    public async Task RoutineAddedOutOfBand_IsFoundByGetRoutineAfterTheRefresh()
    {
        var (tools, _, controller) = Connect(Standard());
        controller.Controller = Standard(extraRoutine: true);

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Added"));

        Assert.False(routine.TryGetProperty("error", out var error), $"unexpected error {error}");
        Assert.Equal(1, routine.GetProperty("rungs").GetArrayLength());
        Assert.Equal(Reason, routine.GetProperty("imageRefreshedBecause").GetString());
    }

    [Fact]
    public async Task ChangedControllerIdentity_ImageReadersReturnControllerChangedUntilReconnect()
    {
        var (tools, session, controller) = Connect(Standard());
        controller.Controller = Image("Other", "16#0000_00BB", ("MainProgram", [("Main", 1)]), ("Other", [("Main", 1)]));

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(ErrorKinds.ControllerChanged, routine.GetProperty("kind").GetString());
        Assert.Contains("'C'", routine.GetProperty("before").GetString());
        Assert.Contains("'Other'", routine.GetProperty("after").GetString());
        Assert.Contains("logix_connect", routine.GetProperty("hint").GetString());
        Assert.Contains("discarded", routine.GetProperty("hint").GetString());

        // Every image reader refuses, the diff included.
        Assert.Equal(ErrorKinds.ControllerChanged, Parse(await tools.GetLintResponseAsync()).GetProperty("kind").GetString());
        Assert.Equal(ErrorKinds.ControllerChanged,
            Parse(await tools.GetDiffAgainstControllerResponseAsync("ignored.L5X")).GetProperty("kind").GetString());

        // Live tag access and the session itself are untouched.
        Assert.True(session.IsConnected);
        Assert.NotNull(session.Service);

        // logix_connect (Attach + a new image) clears it.
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(controller.Controller, DateTimeOffset.UtcNow);
        Assert.Null(session.ControllerChange);
        Assert.False(Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main")).TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task Redownload_RoutinesLost_ReturnsControllerChangedWithoutNames()
    {
        var (tools, session, controller) = Connect(Standard(extraRoutine: true));
        controller.Controller = Standard();   // MainProgram/Added is gone

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(ErrorKinds.ControllerChanged, routine.GetProperty("kind").GetString());
        Assert.Contains("project re-downloaded", routine.GetProperty("error").GetString());
        Assert.False(routine.TryGetProperty("before", out _));
        Assert.NotNull(session.ControllerChange);
    }

    [Fact]
    public async Task TwoReadsWithinTtl_CostOneProbe_AndDiffAlwaysProbes()
    {
        var (tools, session, controller) = Connect(Standard());
        session.ImageFingerprintTtl = TimeSpan.FromSeconds(5);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Standard().ToString());

            await tools.GetGetRoutineResponseAsync("MainProgram", "Main");
            await tools.GetGetRoutineResponseAsync("MainProgram", "Main");
            Assert.Equal(1, controller.Probes);

            await tools.GetDiffAgainstControllerResponseAsync(path);
            Assert.Equal(2, controller.Probes);
            await tools.GetDiffAgainstControllerResponseAsync(path);
            Assert.Equal(3, controller.Probes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReadAfterTtl_ProbesAgain()
    {
        var (tools, session, controller) = Connect(Standard());
        session.ImageFingerprintTtl = TimeSpan.Zero;

        await tools.GetGetRoutineResponseAsync("MainProgram", "Main");
        await tools.GetGetRoutineResponseAsync("MainProgram", "Main");

        Assert.Equal(2, controller.Probes);
        Assert.NotNull(session.FingerprintAgeMs);
    }

    [Fact]
    public async Task DiffAgainstController_RefreshesFirstAndCarriesTheReason()
    {
        var (_, session, controller) = Connect(Standard(mainRungs: 1));
        controller.Controller = Standard(mainRungs: 3);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Standard(mainRungs: 1).ToString());

            var result = Assert.IsType<DiffAgainstControllerResult>(
                await DiffAgainstControllerHandler.ExecuteAsync(path, session));

            Assert.Equal(Reason, result.ImageRefreshedBecause);
            Assert.Contains(result.Routines, r => r.Name == "MainProgram/Main");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailingProbe_LeavesTheReadUnaffected()
    {
        var (tools, session, controller) = Connect(Standard());
        controller.FailProbe = true;

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.Equal(1, routine.GetProperty("rungs").GetArrayLength());
        Assert.Null(session.FingerprintAgeMs);   // a failed probe records nothing
        Assert.Null(session.ControllerChange);
    }

    [Fact]
    public async Task ProgramWaitingForSessionRefresh_IsNotMarkedOutOfBand()
    {
        var (tools, session, controller) = Connect(Standard(mainRungs: 1));
        controller.Controller = Standard(mainRungs: 2);
        session.RecordLiveEdit("MainProgram/Main");   // this session's own change; the refresh covers it

        var routine = Parse(await tools.GetGetRoutineResponseAsync("MainProgram", "Main"));

        Assert.False(routine.TryGetProperty("imageRefreshedBecause", out _));
        Assert.Equal(2, routine.GetProperty("rungs").GetArrayLength());
    }

    [Fact]
    public void ErrorKinds_ContainControllerChangedAndForeignPendingZone_WithErrorsMdRows()
    {
        Assert.Equal("ControllerChanged", ErrorKinds.ControllerChanged);
        Assert.Equal("ForeignPendingZone", ErrorKinds.ForeignPendingZone);

        var errors = File.ReadAllText(Path.Combine(PublicGuard.DocsRoot(), "docs", "errors.md"));
        Assert.Contains("| `ControllerChanged` |", errors);
        Assert.Contains("| `ForeignPendingZone` |", errors);
        Assert.Contains("a pending zone this server did not publish", errors);
        Assert.Contains("project re-downloaded", errors);
    }
}
