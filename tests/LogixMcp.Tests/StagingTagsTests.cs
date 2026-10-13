#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change item 6: workstation staging must report and buffer only
/// declarations for tags that are NEW (absent from the pre-stage census). Context
/// declarations of existing tags, carried by the export envelope, are never "staged".
/// </summary>
public class StagingTagsTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    /// <summary>Export-shaped fragment: context declarations for existing tags plus one caller tag.</summary>
    private const string FragmentWithContextAndCallerTag =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
          "<Tags Use=\"Context\">" +
            "<Tag Name=\"InitialConfig\" DataType=\"DINT\" />" +
            "<Tag Name=\"_Options\" DataType=\"DINT\" />" +
            "<Tag Name=\"BrandNewTag\" DataType=\"DINT\" ExternalAccess=\"Read Only\" />" +
          "</Tags>" +
          "<Programs><Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\">" +
            "<RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent>" +
          "</Routine></Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    private static async Task<LogixProjectSession> OpenAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return session;
    }

    /// <summary>Context declarations of tags already in the census are not buffered or reported.</summary>
    [Fact]
    public async Task ContextEnvelopeTagsNotReportedAsStaged()
    {
        var session = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0,
            FragmentWithContextAndCallerTag.Replace("<Tag Name=\"BrandNewTag\" DataType=\"DINT\" ExternalAccess=\"Read Only\" />", ""),
            session, target: "workstation",
            preExistingTagNames: new[] { "InitialConfig", "_Options" });

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Null(ok.StagedTags);
        Assert.Empty(session.StagedTagBuffer);
    }

    /// <summary>A caller-supplied tag absent from the census is still staged, alone.</summary>
    [Fact]
    public async Task CallerTagsStillStaged()
    {
        var session = await OpenAsync();

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, FragmentWithContextAndCallerTag, session, target: "workstation",
            preExistingTagNames: new[] { "InitialConfig", "_Options" });

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(ok.StagedTags);
        Assert.Equal(new[] { "BrandNewTag" }, ok.StagedTags!);
        Assert.Single(session.StagedTagBuffer);
        Assert.True(session.StagedTagBuffer.ContainsKey("BrandNewTag"));
    }
}
#endif
