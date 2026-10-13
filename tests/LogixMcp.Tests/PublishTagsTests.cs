#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// This change item 6: the publish response's tagsCreated lists only tags
/// that did not exist before; existing context declarations go to contextTags.
/// </summary>
public class PublishTagsTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string Fragment =
        "<RSLogix5000Content SchemaRevision=\"1.0\"><Controller>" +
          "<Tags Use=\"Context\">" +
            "<Tag Name=\"InitialConfig\" DataType=\"DINT\" />" +
            "<Tag Name=\"BrandNewTag\" DataType=\"DINT\" ExternalAccess=\"Read Only\" />" +
          "</Tags>" +
          "<Programs><Program Name=\"MainProgram\"><Routines><Routine Name=\"MainRoutine\">" +
            "<RLLContent><Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent>" +
          "</Routine></Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    /// <summary>Existing context tags are in contextTags, never in tagsCreated.</summary>
    [Fact]
    public async Task ExistingContextTagsNotInTagsCreated()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment, session, target: "controller",
            preExistingTagNames: new[] { "InitialConfig" });

        var ok = Assert.IsType<ProposeRungEditResult>(result);
        Assert.NotNull(ok.TagsCreated);
        Assert.Equal(new[] { "BrandNewTag" }, ok.TagsCreated!.Select(t => t.TagName));
        Assert.Equal(new[] { "InitialConfig" }, ok.ContextTags);
    }
}
#endif
