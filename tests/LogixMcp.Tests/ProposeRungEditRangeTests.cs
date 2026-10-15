#if LOGIX_SDK
using System.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>insertPosition</c> and <c>replaceCount</c> are checked against the routine's visible
/// rung count before any import. <c>insertPosition == N</c> is the legal append; the SDK import would otherwise clamp
/// an out-of-range value silently while the response echoed the requested numbers.
/// </summary>
[Collection("AuditLoggerDir")]
public class ProposeRungEditRangeTests
{
    private const string Routine = "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='SafeRoutine']";

    private static ProposeRungEditError? Check(int n, uint insert, uint replace) =>
        ProposeRungEditHandler.CheckRungRange(Routine, n, insert, replace);

    [Fact]
    public void CheckRungRange_OneRung_InsertPastEnd_IsInsertPositionOutOfRange()
    {
        var error = Check(1, 5, 0);
        Assert.NotNull(error);
        Assert.Equal("InsertPositionOutOfRange", error!.Kind);
    }

    [Fact]
    public void CheckRungRange_OneRung_ReplaceTooMany_IsReplaceCountOutOfRange()
    {
        Assert.Equal("ReplaceCountOutOfRange", Check(1, 0, 3)?.Kind);
    }

    [Fact]
    public void CheckRungRange_OneRung_AppendPlusReplace_IsReplaceCountOutOfRange()
    {
        Assert.Equal("ReplaceCountOutOfRange", Check(1, 1, 1)?.Kind);
    }

    [Fact]
    public void CheckRungRange_OneRung_Append_IsLegal()
    {
        Assert.Null(Check(1, 1, 0));
    }

    [Fact]
    public void CheckRungRange_OneRung_ReplaceTheOnlyRung_IsLegal()
    {
        Assert.Null(Check(1, 0, 1));
    }

    [Fact]
    public void CheckRungRange_EmptyRoutine_OnlyAppendOfNothingIsLegal()
    {
        Assert.Null(Check(0, 0, 0));
        Assert.Equal("ReplaceCountOutOfRange", Check(0, 0, 1)?.Kind);
        Assert.Equal("InsertPositionOutOfRange", Check(0, 1, 0)?.Kind);
    }

    [Fact]
    public void CheckRungRange_TwoRungs_ReplaceTheLastRung_IsLegal()
    {
        Assert.Null(Check(2, 1, 1));
    }

    [Fact]
    public void CheckRungRange_MaxValues_DoNotOverflow()
    {
        Assert.Equal("InsertPositionOutOfRange", Check(1, uint.MaxValue, 0)?.Kind);
        Assert.Equal("InsertPositionOutOfRange", Check(1, uint.MaxValue, uint.MaxValue)?.Kind);
        Assert.Equal("ReplaceCountOutOfRange", Check(1, 0, uint.MaxValue)?.Kind);
        Assert.Equal("ReplaceCountOutOfRange", Check(int.MaxValue, 1, uint.MaxValue)?.Kind);
        Assert.Null(Check(int.MaxValue, 0, (uint)int.MaxValue));
    }

    [Fact]
    public void CheckRungRange_Messages_NameRoutineCountAndRequestedValues()
    {
        var insert = Check(3, 7, 2)!;
        Assert.Equal("InsertPositionOutOfRange", insert.Kind);
        Assert.Contains(Routine, insert.Error);
        Assert.Contains("3 rungs, positions 0..2; 3 appends", insert.Error);
        Assert.Contains("insertPosition 7", insert.Error);
        Assert.Contains("replaceCount 2", insert.Error);
        Assert.Contains("logix_get_routine", insert.Hint);
        Assert.Contains("insertPosition + replaceCount", insert.Hint);

        var replace = Check(3, 2, 4)!;
        Assert.Equal("ReplaceCountOutOfRange", replace.Kind);
        Assert.Contains(Routine, replace.Error);
        Assert.Contains("3 rungs, positions 0..2; 3 appends", replace.Error);
        Assert.Contains("insertPosition 2", replace.Error);
        Assert.Contains("replaceCount 4", replace.Error);
        Assert.Contains("logix_get_routine", replace.Hint);
        Assert.Contains("at most N", replace.Hint);
    }

    // ── ExecuteAsync level (driven through the recording fake) ────────────────

    private static string Fragment(params string[] texts) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"SafeRoutine\"><RLLContent>" +
        string.Concat(texts.Select((t, i) => $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string Export(params string[] texts) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"SafeRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Use=\"Target\" Name=\"SafeRoutine\" Type=\"RLL\"><RLLContent>" +
        string.Concat(texts.Select((t, i) => $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        fake.ExportContent   = Export("XIC(A)OTE(B);");
        return (session, fake);
    }

    /// <summary>The l5xFragment path: an out-of-range request is refused with nothing imported or staged.</summary>
    [Theory]
    [InlineData(5u, 0u, "InsertPositionOutOfRange")]
    [InlineData(0u, 3u, "ReplaceCountOutOfRange")]
    [InlineData(1u, 1u, "ReplaceCountOutOfRange")]
    public async Task Fragment_Workstation_OutOfRange_IsRefusedBeforeAnyImport(uint insert, uint replace, string kind)
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, insert, replace, Fragment("XIC(C)OTE(D);"), session, target: "workstation");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal(kind, error.Kind);
        Assert.Empty(fake.CapturedInsertPositions);
        Assert.False(session.HasStagedRungEdit);
    }

    /// <summary>An append (insertPosition == N) still stages.</summary>
    [Fact]
    public async Task Fragment_Workstation_Append_IsStillAccepted()
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, 1, 0, Fragment("XIC(C)OTE(D);"), session, target: "workstation");

        Assert.IsType<ProposeRungEditResult>(result);
        Assert.Single(fake.CapturedInsertPositions);
    }

    /// <summary>Controller with a fragment: refused before the pre-import export or any import.</summary>
    [Theory]
    [InlineData(5u, 0u, "InsertPositionOutOfRange")]
    [InlineData(1u, 1u, "ReplaceCountOutOfRange")]
    public async Task Fragment_ControllerWithFragment_OutOfRange_IsRefusedBeforeAnyImport(uint insert, uint replace, string kind)
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, insert, replace, Fragment("XIC(C)OTE(D);"), session, target: "controller");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal(kind, error.Kind);
        Assert.Empty(fake.CapturedInsertPositions);
        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>Live target, replaceCount 0: an insertPosition past the end is refused before the import.</summary>
    [Fact]
    public async Task Live_InsertPastEnd_IsRefusedBeforeAnyImport()
    {
        var (session, fake) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, 5, 0, Fragment("XIC(C)OTE(D);"), session, target: "live");

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Equal("InsertPositionOutOfRange", error.Kind);
        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>
    /// Live replace/delete shape (replaceCount &gt; 0): the live-target stale-target gate (gate 5) runs before the range
    /// check and already refuses a range with no rung at that index, so the refusal kind there is StaleTarget. Either
    /// way nothing is imported.
    /// </summary>
    [Theory]
    [InlineData(0u, 3u)]
    [InlineData(1u, 1u)]
    [InlineData(5u, 1u)]
    public async Task LiveDelete_OutOfRange_IsRefusedBeforeAnyImport(uint insert, uint replace)
    {
        var (session, fake) = await OpenAsync();
        var expected = Enumerable.Repeat("XIC(A)OTE(B);", (int)replace).ToList();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, insert, replace, null, session, target: "live",
            expectedRungText: expected, rungs: System.Array.Empty<RungInput>());

        var error = Assert.IsType<ProposeRungEditError>(result);
        Assert.Contains(error.Kind, new[] { "StaleTarget", "ReplaceCountOutOfRange", "InsertPositionOutOfRange" });
        Assert.Empty(fake.CapturedImportOptions);
    }

    /// <summary>An empty-fragment controller publish is exempt: its position is server-chosen.</summary>
    [Fact]
    public async Task EmptyFragmentControllerPublish_IsNotRangeChecked()
    {
        var (session, _) = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            Routine, 9, 9, "", session, target: "controller");

        if (result is ProposeRungEditError error)
        {
            Assert.NotEqual("InsertPositionOutOfRange", error.Kind);
            Assert.NotEqual("ReplaceCountOutOfRange", error.Kind);
        }
    }
}
#endif
