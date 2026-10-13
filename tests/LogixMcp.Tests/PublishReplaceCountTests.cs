#if LOGIX_SDK
using System.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// logix-mcp-i3b6.4: a staged-set <c>target="controller"</c> publish reports the number of controller rungs
/// it actually replaced (top-level <c>replaceCount</c>, the message, and <c>publishedRoutines[i].replaceCount</c>
/// agree), not the caller's zero. A caller's ignored non-zero replaceCount still shows in <c>stagedReplaceCount</c>.
/// </summary>
[Collection("AuditLoggerDir")]
public class PublishReplaceCountTests
{
    private const string MainXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";
    private const string SecondXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='SecondRoutine']";

    private static string Fragment(string routine, params string[] texts) =>
        "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"{routine}\"><RLLContent>" +
        string.Concat(texts.Select((t, i) => $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{t}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string Export(string routine, params (string Type, string Text)[] rungs) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        $"TargetName=\"{routine}\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Use=\"Target\" Name=\"{routine}\" Type=\"RLL\"><RLLContent>" +
        string.Concat(rungs.Select((r, i) => $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
        "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    /// <summary>Stage replace(0,2) with two new rungs, then publish with no fragment: replaceCount is 2 everywhere.</summary>
    [Fact]
    public async Task StagedReplaceTwo_ThenPublish_ReportsReplaceCountTwo()
    {
        var (session, fake) = await OpenAsync();

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 2, Fragment("MainRoutine", "XIC(A)OTE(B);", "XIC(C)OTE(D);"),
            session, target: "workstation"));

        fake.ExportContent = Export("MainRoutine", ("I", "XIC(A)OTE(B);"), ("I", "XIC(C)OTE(D);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(2u, success.ReplaceCount);
        Assert.Null(success.StagedReplaceCount);   // the caller passed 0: nothing was ignored
        Assert.Contains("replacing 2 rungs", success.Message);
        Assert.Equal(2u, success.PublishedRoutines!.Single().ReplaceCount);
    }

    /// <summary>A caller's ignored non-zero replaceCount stays in stagedReplaceCount; replaceCount is the real count.</summary>
    [Fact]
    public async Task StagedReplaceTwo_PublishWithIgnoredCallerCount_KeepsIgnoredFieldAndReportsActual()
    {
        var (session, fake) = await OpenAsync();

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 2, Fragment("MainRoutine", "XIC(A)OTE(B);", "XIC(C)OTE(D);"),
            session, target: "workstation"));

        fake.ExportContent = Export("MainRoutine", ("I", "XIC(A)OTE(B);"), ("I", "XIC(C)OTE(D);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 5, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(2u, success.ReplaceCount);
        Assert.Equal(5u, success.StagedReplaceCount);
        Assert.Contains("replacing 2 rungs", success.Message);
    }

    /// <summary>A direct publish with no staged rungs is unchanged: the caller's replaceCount is reported.</summary>
    [Fact]
    public async Task DirectPublish_NoStagedRungs_ReportsCallerReplaceCount()
    {
        var (session, fake) = await OpenAsync();
        fake.ExportContent = Export("MainRoutine", ("I", "XIC(A)OTE(B);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 1, Fragment("MainRoutine", "XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(1u, success.ReplaceCount);
        Assert.Null(success.StagedReplaceCount);
        Assert.Contains("replacing 1 rung", success.Message);
    }

    /// <summary>A multi-routine publish reports each routine's replace count; the top level is their sum.</summary>
    [Fact]
    public async Task MultiRoutinePublish_ReportsReplaceCountPerRoutine()
    {
        var (session, fake) = await OpenAsync();
        fake.ReflectAcceptedImportsInExport = false;
        fake.ExportContentByXPath[ProposeRungEditRecordingFake.ProgramsXPath] =
            ProposeRungEditRecordingFake.ProgramsExport("MainRoutine", "SecondRoutine");

        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 2, Fragment("MainRoutine", "XIC(A)OTE(B);", "XIC(C)OTE(D);"),
            session, target: "workstation"));
        Assert.IsType<ProposeRungEditResult>(await ProposeRungEditHandler.ExecuteAsync(
            SecondXPath, 0, 1, Fragment("SecondRoutine", "XIC(E)OTE(F);"),
            session, target: "workstation"));

        fake.ExportContentByXPath[MainXPath]   = Export("MainRoutine", ("I", "XIC(A)OTE(B);"), ("I", "XIC(C)OTE(D);"));
        fake.ExportContentByXPath[SecondXPath] = Export("SecondRoutine", ("I", "XIC(E)OTE(F);"));

        var result = await ProposeRungEditHandler.ExecuteAsync(
            MainXPath, 0, 0, "", session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal(3u, success.ReplaceCount);
        Assert.Contains("replacing 3 rungs", success.Message);
        Assert.Equal(2, success.PublishedRoutines!.Count);
        Assert.Equal(2u, success.PublishedRoutines.Single(r => r.Routine == MainXPath).ReplaceCount);
        Assert.Equal(1u, success.PublishedRoutines.Single(r => r.Routine == SecondXPath).ReplaceCount);
    }
}
#endif
