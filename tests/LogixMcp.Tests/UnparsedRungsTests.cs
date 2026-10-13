using System.Buffers.Binary;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Per-rung failure containment (<c>unparsedRungs</c>) across every tool that parses rungs, against
/// <c>fixtures/DemoCrossProgram.L5X</c>: P1 holds <c>MOVE(\P2.Src,Dst)</c>, <c>XIC(\P2.Bit)OTE(Local)</c>
/// and one deliberately unparseable rung (3); P2 is clean.
/// </summary>
public class UnparsedRungsTests
{
    private static ProjectService Load()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossProgram.L5X"));
        return svc;
    }

    private static JsonElement Json(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, result.GetType())).RootElement;

    private static void AssertBadRung(JsonElement unparsedRungs)
    {
        var entry = Assert.Single(unparsedRungs.EnumerateArray());
        Assert.Equal("P1", entry.GetProperty("program").GetString());
        Assert.Equal("Main", entry.GetProperty("routine").GetString());
        Assert.Equal(3, entry.GetProperty("rung").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("error").GetString()));
    }

    [Fact]
    public void Lint_UnparseableRung_IsReported_AndOtherResultsAreUnchanged()
    {
        var result = Assert.IsType<LintResult>(LintHandler.Execute(Load()));

        var entry = Assert.Single(result.UnparsedRungs!);
        Assert.Equal(("P1", "Main", 3), (entry.Program, entry.Routine, entry.Rung));
        Assert.False(string.IsNullOrWhiteSpace(entry.Error));
        AssertBadRung(Json(result).GetProperty("unparsedRungs"));
        // Everything else is unchanged: rules still find the duplicate OTE on the parseable rungs around the
        // bad one, the bad rung adds no finding of its own, and P2's findings equal those of a P2-scoped lint.
        Assert.Equal([1, 2], result.Findings.Where(f => f.Rule == "DuplicateDestructiveOutput")
            .Select(f => f.Rung).OrderBy(n => n).ToArray());
        Assert.DoesNotContain(result.Findings, f => f.Program == "P1" && f.Rung == 3);
        var clean = Assert.IsType<LintResult>(LintHandler.Execute(Load(), program: "P2"));
        Assert.True(clean.Findings.SequenceEqual(result.Findings.Where(f => f.Program == "P2")));
    }

    [Fact]
    public void Lint_FilteredToProgramWithoutTheBadRung_SucceedsAndReportsNone()
    {
        var result = Assert.IsType<LintResult>(LintHandler.Execute(Load(), program: "P2"));

        Assert.Null(result.UnparsedRungs);
        Assert.False(Json(result).TryGetProperty("unparsedRungs", out _));
    }

    [Fact]
    public void Lint_FilteredToProgramWithTheBadRung_ReportsIt()
    {
        var result = Assert.IsType<LintResult>(LintHandler.Execute(Load(), program: "P1", routine: "Main"));
        Assert.Single(result.UnparsedRungs!);
    }

    [Fact]
    public void VerifyInterlock_CrossProgramContact_Succeeds_AndReportsTheBadRung()
    {
        // Local is written on rungs 1 and 2; only rung 1 carries \P2.Bit. The unparseable rung 3 also names Local.
        var result = Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(Load(), "Local", "Run"));

        Assert.Equal([1, 2], result.Rungs.Select(r => r.RungNumber).ToArray());
        Assert.False(result.Verified);
        AssertBadRung(Json(result).GetProperty("unparsedRungs"));
    }

    [Fact]
    public void VerifyInterlock_UnparsedRungNamingTheOutput_BlocksVerification()
    {
        // Every parsed rung that writes Local has Run or \P2.Bit; asking for a condition both have is impossible,
        // so use Dst: written only by rung 0, which has no XIC at all.
        var dst = Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(Load(), "Dst", "Run"));
        // P1 rung 0 and P2 rung 1 (which writes P1's Dst as \P1.Dst) both write it, neither with an XIC.
        Assert.Equal(2, dst.Rungs.Count);
        // The bad rung does not mention Dst, so it does not change the proof.
        Assert.False(dst.Verified);
        Assert.Contains("unparsedRungs", Json(dst).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void CrossReference_CrossProgramOperands_Succeeds_AndReportsTheBadRung()
    {
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(Load(), "Local"));

        Assert.Equal([1, 2], result.References.Select(r => r.RungNumber).ToArray());
        AssertBadRung(Json(result).GetProperty("unparsedRungs"));
    }

    [Fact]
    public void CrossReference_CleanProject_OmitsUnparsedRungs()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossRef.L5X"));
        var result = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "Motor_Run"));
        Assert.Null(result.UnparsedRungs);
    }

    [Fact]
    public void FindTags_Succeeds_OnTheFixture()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load()));
        Assert.Contains(result.Tags, t => t.Name == "Src" && t.Scope == "P2");
    }

    [Fact]
    public void GateReport_ReportsTheBadRung_AndStillClassifiesEveryTag()
    {
        var result = GateReportHandler.Execute(Load());
        var doc = JsonDocument.Parse(GateReportHandler.Serialize(result)).RootElement;

        AssertBadRung(doc.GetProperty("unparsedRungs"));
        Assert.Equal(1, doc.GetProperty("unparsedRungsCount").GetInt32());
        Assert.Equal(1, doc.GetProperty("unparsedContentCount").GetInt32());
    }

    [Fact]
    public void Document_ReportsTheBadRung_AndStillRendersTheProject()
    {
        var result = Assert.IsType<DocumentResult>(DocumentHandler.Execute(Load()));

        AssertBadRung(Json(result).GetProperty("unparsedRungs"));
        Assert.Contains("Program P2", result.Markdown);
    }

    [Fact]
    public void Document_ProgramWithoutTheBadRung_ReportsNone()
    {
        var result = Assert.IsType<DocumentResult>(DocumentHandler.Execute(Load(), program: "P2"));
        Assert.Null(result.UnparsedRungs);
    }

    [Fact]
    public async Task Diagnose_ArraySubscriptFault_ReportsTheBadRung()
    {
        var fault = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(fault.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(fault.AsSpan(12), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(fault.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fault.AsSpan(22), 20);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(), isVirtual: false, isLiveController: true, controllerHost: "10.0.0.7");

        var result = await DiagnoseHandler.ExecuteAsync(
            null, session, new ControllerModeService(),
            (_, _) => Task.FromResult(new EmulatorIdentityInfo("Emulate 5580 Controller", 38, 11, 1, 14, 0x55, 1, 0x3450)),
            (_, _) => Task.FromResult(fault),
            Load());

        var r = Assert.IsType<DiagnoseResult>(result);
        Assert.True(r.Faulted);
        AssertBadRung(Json(r).GetProperty("unparsedRungs"));
    }

    [Fact]
    public void RungParser_ParseContained_NeverThrows_AndRecordsTheRung()
    {
        var project = L5Sharp.Core.L5X.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoCrossProgram.L5X"));
        var rungs = project.Programs.Single(p => p.Name == "P1").Routines.Single().Rungs.ToList();
        var sink = new List<UnparsedRungEntry>();

        Assert.Single(RungParser.ParseContained(rungs[0], "P1", "Main", sink));
        Assert.Empty(sink);
        Assert.Empty(RungParser.ParseContained(rungs[3], "P1", "Main", sink));
        Assert.Equal(("P1", "Main", 3), (sink[0].Program, sink[0].Routine, sink[0].Rung));
    }

    private static ProjectService LoadUnparsed()
    {
        var svc = new ProjectService();
        svc.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoUnparsedRungs.L5X"));
        return svc;
    }

    [Fact]
    public void UnparsedRungs_UnbalancedParen_AndSpacedProgramQualifier_AreBothReported()
    {
        var project = L5Sharp.Core.L5X.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoUnparsedRungs.L5X"));
        var rungs = project.Programs.Single(p => p.Name == "P1").Routines.Single().Rungs.ToList();
        var sink = new List<UnparsedRungEntry>();

        Assert.Equal(2, RungParser.ParseContained(rungs[0], "P1", "Main", sink).Count);
        Assert.Empty(sink);
        Assert.Empty(RungParser.ParseContained(rungs[1], "P1", "Main", sink));   // unbalanced paren
        Assert.Empty(RungParser.ParseContained(rungs[2], "P1", "Main", sink));   // \P2 .Flag
        Assert.Equal([1, 2], sink.Select(e => e.Rung).ToArray());

        // And the tools surface them.
        var xref = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(LoadUnparsed(), "Run"));
        Assert.Equal([1, 2], xref.UnparsedRungs!.Select(e => e.Rung).ToArray());
        var lint = Assert.IsType<LintResult>(LintHandler.Execute(LoadUnparsed()));
        Assert.Equal([1, 2], lint.UnparsedRungs!.Select(e => e.Rung).ToArray());
    }

    [Fact]
    public void VerifyInterlock_WriterRungUnparsed_IsNamedInsteadOfNotWrittenByAnyRung()
    {
        var result = Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(LoadUnparsed(), "Lonely", "Run"));

        Assert.Contains("writer rung unparsed: P1/Main/1", result.Error);
        Assert.DoesNotContain("not written by any rung", result.Error);
        Assert.Equal([1, 2], result.UnparsedRungs!.Select(e => e.Rung).ToArray());
    }

    [Fact]
    public void VerifyInterlock_TagNoRungMentions_StillSaysNotWrittenByAnyRung()
    {
        var result = Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(LoadUnparsed(), "Nowhere", "Run"));
        Assert.Contains("not written by any rung", result.Error);
        Assert.DoesNotContain("writer rung unparsed", result.Error);
    }

    [Fact]
    public void CrossReference_TagOnlyInUnparsedRungs_AddsANote()
    {
        var only = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(LoadUnparsed(), "Lonely"));
        Assert.Empty(only.References);
        Assert.Contains("only in rungs that could not be parsed", only.Note);
        Assert.Contains("P1/Main/1", only.Note);
        Assert.Equal("note", Json(only).EnumerateObject().Single(p => p.Name == "note").Name);

        // A tag with parsed references gets no note.
        var parsed = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(LoadUnparsed(), "Cmd"));
        Assert.Null(parsed.Note);
    }

    [Fact]
    public void FindTags_TagOnlyInUnparsedRungs_AddsANote()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(LoadUnparsed(), "Lonely"));
        Assert.Contains(result.Tags, t => t.Name == "Lonely");
        Assert.Contains("Lonely", result.Note);
        Assert.Contains("P1/Main/1", result.Note);
        Assert.NotEmpty(result.UnparsedRungs!);

        var clean = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(LoadUnparsed(), "Cmd"));
        Assert.Null(clean.Note);
    }
}
