#if LOGIX_SDK
using System.Text.Json;
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for normalization of hand-written raw <c>l5xFragment</c> envelopes:
/// a root without <c>SchemaRevision</c> is
/// rebuilt server-side when it holds only rungs and tags, refused with
/// <c>EnvelopeIncomplete</c> when it holds anything else, and an exported envelope
/// passes through unchanged.
/// </summary>
public class ProposeRungEditNormalizationTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ExportRoot =
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">";

    private static string ExportedEnvelope(string rungs) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + ExportRoot +
        "<Controller Name=\"DemoController\"><Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>{rungs}</RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static string HandWritten(string controllerPrefix = "", string rungs =
        "<Rung Number=\"0\" Type=\"N\"><Comment><![CDATA[Starts the lamp]]></Comment>" +
        "<Text><![CDATA[XIC(Start)OTE(Lamp);]]></Text></Rung>") =>
        "<RSLogix5000Content><Controller>" + controllerPrefix +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        $"<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>{rungs}</RLLContent></Routine>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenAsync()
    {
        var fake = new ProposeRungEditRecordingFake
        {
            ExportContent = ExportedEnvelope("<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>"),
        };
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static LogixSdkTools NewTools() =>
        new(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

    [Fact]
    public async Task RawWithoutSchemaRevision_RungsAndTagsOnly_RebuiltServerSide_EnvelopeServerBuilt()
    {
        var (session, fake) = await OpenAsync();
        var fragment = HandWritten(
            "<Tags Use=\"Context\"><Tag Name=\"Lamp\" TagType=\"Base\" DataType=\"BOOL\">" +
            "<Description><![CDATA[Lamp output]]></Description></Tag></Tags>");

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 1, 0, fragment, session: session, target: "workstation");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("error", out var err), json + err);
        Assert.Equal("server-built", root.GetProperty("envelope").GetString());

        // The imported file carries the export's root attributes, not the hand-written root.
        var imported = XDocument.Parse(fake.LastCapturedXmlContent!);
        Assert.Equal("1.0",  (string?)imported.Root!.Attribute("SchemaRevision"));
        Assert.Equal("36.00", (string?)imported.Root.Attribute("SoftwareRevision"));
        Assert.Equal("MainRoutine", (string?)imported.Root.Attribute("TargetName"));

        // Rung kept its Text and Comment, renumbered from insertPosition; export rung removed.
        var rung = Assert.Single(imported.Descendants("Rung"));
        Assert.Equal("1", (string?)rung.Attribute("Number"));
        Assert.Equal("XIC(Start)OTE(Lamp);", rung.Element("Text")!.Value);
        Assert.Equal("Starts the lamp", rung.Element("Comment")!.Value);

        // Workstation: the tag was stripped from the import and held in the staging buffer
        // with its Description.
        Assert.Empty(imported.Descendants("Tag"));
        Assert.True(session.StagedTagBuffer.TryGetValue("Lamp", out var staged));
        Assert.Equal("Lamp output", staged!.Element("Description")!.Value);
    }

    [Fact]
    public async Task RawWithoutSchemaRevision_WithDataTypes_EnvelopeIncomplete_BeforeSdk()
    {
        var (session, fake) = await OpenAsync();
        var fragment = HandWritten("<DataTypes><DataType Name=\"MyUdt\" Family=\"NoFamily\" Class=\"User\"/></DataTypes>");

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, fragment, session: session, target: "workstation");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("EnvelopeIncomplete", root.GetProperty("kind").GetString());
        var message = root.GetProperty("error").GetString()!;
        Assert.Contains("SchemaRevision", message);
        Assert.Contains("rungs/tags", message);
        Assert.Contains("logix_export_rungs", message);

        // Refused before the SDK: no export, no import.
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
    }

    [Fact]
    public async Task RawWithSchemaRevision_PassedThroughUnchanged_EnvelopeCaller()
    {
        var (session, fake) = await OpenAsync();
        var fragment = ExportedEnvelope(
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Start)OTE(Lamp);]]></Text></Rung>");

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, fragment, session: session, target: "workstation");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("error", out var err), json + err);
        Assert.Equal("caller", root.GetProperty("envelope").GetString());

        // Passed through: the imported content is the caller's own.
        Assert.Single(fake.CapturedXPaths);
        Assert.Equal(
            XDocument.Parse(fragment).ToString(SaveOptions.DisableFormatting),
            XDocument.Parse(fake.LastCapturedXmlContent!).ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public async Task RawWithoutSchemaRevision_RoutineNameMismatch_Refused_BeforeSdk()
    {
        var (session, fake) = await OpenAsync();
        var fragment = HandWritten().Replace("Name=\"MainRoutine\"", "Name=\"OtherRoutine\"");

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, fragment, session: session, target: "workstation");

        Assert.Contains("does not match", JsonDocument.Parse(json).RootElement.GetProperty("error").GetString());
        Assert.Empty(fake.CapturedImportOptions);
    }
}
#endif
