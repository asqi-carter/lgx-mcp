#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The <see cref="ZoneLedger"/> records the pending zones this server published, and
/// accept / assemble / test refuse a live zone the ledger does not know (<c>ForeignPendingZone</c>) unless
/// <c>acceptForeign</c> is passed. Cancel is never refused on this ground and reports <c>foreign: true</c>.
/// </summary>
[Collection("StaticHandlerHooks")]
public class ForeignPendingZoneTests
{
    private const string Program = "MainProgram";
    private const string Routine = "MainRoutine";
    private const string Sibling = "SiblingRoutine";

    private readonly ZoneLedger _ledger = new();

    /// <summary>Each test starts with an empty ledger, isolated from other tests' connects.</summary>
    public ForeignPendingZoneTests() => ZoneLedger.TestOverride = _ledger;

    private static RoutinePendingEditState State(string routine, string state, string zoneState = "untested") =>
        new(Program, routine, 2,
            state == "none"
                ? Array.Empty<PendingEditRungEntry>()
                : new[] { new PendingEditRungEntry(0, "I", "OTE(Motor_Run);") },
            state)
        { ZoneState = state == "none" ? "none" : zoneState, ProgramTestedEdits = zoneState == "tested" };

    /// <summary>A reader: <paramref name="before"/> (every routine it lists) on the first call, <paramref name="after"/> after.</summary>
    private static Func<string, string, CancellationToken, Task<object>> Reader(
        RoutinePendingEditState[] before, RoutinePendingEditState[]? after = null)
    {
        // The "after" set starts with the second routine-scoped read (the handler's after-read); program-scoped reads
        // made before then (the foreign gate's whole-zone read) still see the zone as it was.
        var routineReads = 0;
        return (p, r, ct) =>
        {
            if (!string.IsNullOrEmpty(r)) routineReads++;
            var set = routineReads <= 1 || after is null ? before : after;
            // A routine-scoped read returns only that routine; a program-scoped read (blank routine) returns all.
            var routines = string.IsNullOrEmpty(r)
                ? set
                : set.Where(x => string.Equals(x.Routine, r, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Task.FromResult<object>(new PendingEditsResult(
                true, "Run", null, routines,
                new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));
        };
    }

    private static Func<string, CancellationToken, Task<Session1ScriptResult>> Runner(List<string> verbs) =>
        (verb, ct) =>
        {
            verbs.Add(verb);
            return Task.FromResult(new Session1ScriptResult(true, "gesture_sent", new[] { "shot1.png" }));
        };

    private static Task<object> Run(
        string verb, Func<string, string, CancellationToken, Task<object>> reader, List<string> verbs,
        bool acceptForeign = false) =>
        verb switch
        {
            "accept" => AcceptPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, reader, Runner(verbs), () => true, default, null, null, acceptForeign),
            "assemble" => AssemblePendingEditsHandler.ExecuteAsync(
                Program, Routine, true, reader, Runner(verbs), () => true, default, null, null, acceptForeign),
            "test" => TestPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, reader, Runner(verbs), () => true, default, null, acceptForeign),
            _ => CancelPendingEditsHandler.ExecuteAsync(
                Program, Routine, true, reader, Runner(verbs), () => true, default, null),
        };

    /// <summary>The state each verb needs before it runs: assemble needs a tested zone, test an untested one.</summary>
    private static string ZoneFor(string verb) => verb == "assemble" ? "tested" : "untested";

    /// <summary>The state the zone is in afterwards: accept / assemble / cancel resolve it, test leaves it tested.</summary>
    private static RoutinePendingEditState[] AfterFor(string verb) =>
        verb == "test"
            ? new[] { State(Routine, "pending", "tested") }
            : new[] { State(Routine, "none") };

    [Fact]
    public async Task RecordedZone_LetsAcceptProceed()
    {
        _ledger.Record(Program, new[] { Routine });
        var verbs = new List<string>();

        var result = await Run("accept", Reader(new[] { State(Routine, "pending") }, AfterFor("accept")), verbs);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("accepted", ok.Status);
        Assert.False(ok.Foreign);
        Assert.DoesNotContain("foreign", JsonSerializer.Serialize(ok), StringComparison.Ordinal);
        Assert.Equal(new[] { "accept" }, verbs);
        Assert.Null(_ledger.Get(Program));   // accept resolved the zone: the program's entry is gone
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("assemble")]
    [InlineData("test")]
    public async Task UnknownZone_IsRefusedAsForeignPendingZone_AndNothingIsFinalized(string verb)
    {
        var verbs = new List<string>();

        var result = await Run(verb, Reader(new[] { State(Routine, "pending", ZoneFor(verb)) }, AfterFor(verb)), verbs);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("ForeignPendingZone", err.Kind);
        Assert.Equal(Program, err.Program);
        Assert.Equal(new[] { Routine }, err.Routines);
        Assert.False(err.Launched);
        Assert.Empty(verbs);   // no Studio command was sent

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(err));
        var root = doc.RootElement;
        Assert.Equal("ForeignPendingZone", root.GetProperty("kind").GetString());
        Assert.Equal(Program, root.GetProperty("program").GetString());
        Assert.Equal(Routine, root.GetProperty("routines")[0].GetString());
        Assert.False(root.GetProperty("publishedByThisServer").GetBoolean());
        Assert.Equal(
            "another session or Studio published these edits; review them in logix_pending_edits and pass " +
            "acceptForeign: true to finalize them anyway, or cancel them",
            root.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData("accept", "accepted")]
    [InlineData("assemble", "assembled")]
    [InlineData("test", "test_confirmed")]
    public async Task AcceptForeignTrue_Proceeds_AndTheResultCarriesForeignTrue(string verb, string status)
    {
        var verbs = new List<string>();

        var result = await Run(
            verb, Reader(new[] { State(Routine, "pending", ZoneFor(verb)) }, AfterFor(verb)), verbs, acceptForeign: true);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal(status, ok.Status);
        Assert.True(ok.Foreign);
        Assert.Equal(new[] { verb }, verbs);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok));
        Assert.True(doc.RootElement.GetProperty("foreign").GetBoolean());
    }

    [Fact]
    public async Task SiblingRoutineOfTheZone_NotInTheLedger_IsRefused_EvenWhenTheTargetRoutineIsKnown()
    {
        _ledger.Record(Program, new[] { Routine });
        var verbs = new List<string>();
        var zone = new[] { State(Routine, "pending"), State(Sibling, "pending") };

        var result = await Run("accept", Reader(zone, AfterFor("accept")), verbs);

        var err = Assert.IsType<CancelPendingEditsError>(result);
        Assert.Equal("ForeignPendingZone", err.Kind);
        Assert.Equal(new[] { Sibling }, err.Routines);
        Assert.Empty(verbs);
    }

    [Fact]
    public async Task Cancel_OfAnUnknownZone_Proceeds_AndReportsForeign()
    {
        var verbs = new List<string>();

        var result = await Run("cancel", Reader(new[] { State(Routine, "pending") }, AfterFor("cancel")), verbs);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Equal("cancelled", ok.Status);
        Assert.True(ok.Foreign);
        Assert.Equal(new[] { "cancel" }, verbs);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok));
        Assert.True(doc.RootElement.GetProperty("foreign").GetBoolean());
    }

    [Fact]
    public async Task Cancel_OfARecordedZone_IsNotForeign_AndClearsTheEntry()
    {
        _ledger.Record(Program, new[] { Routine });
        var verbs = new List<string>();

        var result = await Run("cancel", Reader(new[] { State(Routine, "pending") }, AfterFor("cancel")), verbs);

        var ok = Assert.IsType<CancelPendingEditsResult>(result);
        Assert.False(ok.Foreign);
        Assert.DoesNotContain("foreign", JsonSerializer.Serialize(ok), StringComparison.Ordinal);
        Assert.Null(_ledger.Get(Program));
    }

    [Fact]
    public async Task Test_DoesNotClearTheEntry_SoAssembleAfterTestStillProceeds()
    {
        _ledger.Record(Program, new[] { Routine });
        var verbs = new List<string>();

        var tested = await Run("test", Reader(new[] { State(Routine, "pending") }, AfterFor("test")), verbs);
        Assert.IsType<CancelPendingEditsResult>(tested);
        Assert.NotNull(_ledger.Get(Program));

        var assembled = await Run(
            "assemble", Reader(new[] { State(Routine, "pending", "tested") }, AfterFor("assemble")), verbs);
        var ok = Assert.IsType<CancelPendingEditsResult>(assembled);
        Assert.False(ok.Foreign);
        Assert.Null(_ledger.Get(Program));
    }

    [Fact]
    public void Ledger_RecordMergesRoutines_IsCaseInsensitive_AndClearProgramForgetsOnlyThatProgram()
    {
        _ledger.Record("A", new[] { "R1" });
        _ledger.Record("a", new[] { "R2" });
        _ledger.Record("B", new[] { "R1" });

        Assert.Empty(_ledger.Unknown("A", new[] { "r1", "R2" }));
        Assert.Equal(new[] { "R3" }, _ledger.Unknown("A", new[] { "R1", "R3" }));
        Assert.Equal(2, _ledger.Count);

        _ledger.ClearProgram("A");
        Assert.Equal(new[] { "R1" }, _ledger.Unknown("A", new[] { "R1" }));
        Assert.Empty(_ledger.Unknown("B", new[] { "R1" }));
    }

    [Fact]
    public async Task Connect_ClearsTheLedger()
    {
        ZoneLedger.TestOverride = null;   // use the process-wide ledgers a real connect clears
        var session = new object();
        ZoneLedger.For(session).Record(Program, new[] { Routine });
        ZoneLedger.For(null).Record(Program, new[] { Routine });

        // A connect that fails fast (unknown backend) still starts from an empty ledger.
        await ConnectHandler.ExecuteAsync("", "", false, new ProjectService(), new TagSessionService(), backend: "bogus");

        Assert.Equal(0, ZoneLedger.For(session).Count);
        Assert.Equal(0, ZoneLedger.For(null).Count);
    }

    // ── The publish path records the zone it left ───────────────────────────────

    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static string Fragment(string rungText) =>
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{rungText}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string Export(string type, string text) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"{type}\"><Text><![CDATA[{text}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenSessionAsync()
    {
        var fake = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_path, _ct) => Task.FromResult<RockwellAutomation.LogixDesigner.ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    [Fact]
    public async Task ControllerPublish_RecordsItsZoneInTheLedger_SoAcceptingItIsNotForeign()
    {
        var (session, fake) = await OpenSessionAsync();
        fake.ExportContent = Export("I", "XIC(A)OTE(B);");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        Assert.IsType<ProposeRungEditResult>(result);
        var entry = _ledger.Get(Program);
        Assert.NotNull(entry);
        Assert.Contains(Routine, entry!.Routines);
        Assert.Empty(_ledger.Unknown(Program, new[] { Routine }));
    }

    [Fact]
    public async Task WorkstationStage_RecordsNothing()
    {
        var (session, _) = await OpenSessionAsync();

        await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "workstation");

        Assert.Equal(0, _ledger.Count);
    }
}
#endif
