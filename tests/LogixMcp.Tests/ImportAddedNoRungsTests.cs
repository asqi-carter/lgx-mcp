#if LOGIX_SDK
using System.Xml.Linq;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Envelope builders open a self-closed
/// <c>&lt;Routine Use="Target" … Type="RLL"/&gt;</c> into a full element with an
/// <c>RLLContent</c>, and an import that should add rungs but landed none fails with
/// <c>ImportAddedNoRungs</c>. Fixtures are hand-written.
/// </summary>
public class ImportAddedNoRungsTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string SelfClosedExport =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Use=\"Context\" Name=\"DemoController\">" +
        "<Programs Use=\"Context\"><Program Use=\"Context\" Name=\"MainProgram\"><Routines Use=\"Context\">" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"/>" +
        "</Routines></Program></Programs></Controller></RSLogix5000Content>";

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

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession((_p, _ct) => Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    [Fact]
    public void StripTargetRoutineRungs_SelfClosedRoutine_GainsRllContent()
    {
        var doc = XDocument.Parse(ProposeRungEditHandler.StripTargetRoutineRungs(SelfClosedExport));

        var routine = doc.Descendants("Routine").Single(r => (string?)r.Attribute("Use") == "Target");
        Assert.NotNull(routine.Element("RLLContent"));
    }

    [Fact]
    public void BuildOnline_SelfClosedRoutine_ContainsRllContentAndRungsAreWritten()
    {
        var envelope = ProposeRungEditHandler.StripTargetRoutineRungs(SelfClosedExport);

        var built = RungFragmentBuilder.BuildOnline(
            envelope, 0, [new RungInput("XIC(A)OTE(B);", null), new RungInput("XIO(C)OTE(D);", null)], null);

        var routine = XDocument.Parse(built).Descendants("Routine")
            .Single(r => (string?)r.Attribute("Use") == "Target");
        var rll = Assert.IsType<XElement>(routine.Element("RLLContent"));
        Assert.Equal(2, rll.Elements("Rung").Count());
        Assert.Equal("XIC(A)OTE(B);", rll.Elements("Rung").First().Element("Text")!.Value);
    }

    [Fact]
    public void BuildOnline_SelfClosedRoutineWithoutPriorStrip_ContainsRllContentAndRungsAreWritten()
    {
        // The builder itself shares the handling: no pre-strip needed.
        var built = RungFragmentBuilder.BuildOnline(
            SelfClosedExport, 0, [new RungInput("XIC(A)OTE(B);", null)], null);

        var rll = XDocument.Parse(built).Descendants("Routine")
            .Single(r => (string?)r.Attribute("Use") == "Target").Element("RLLContent");
        Assert.NotNull(rll);
        Assert.Single(rll!.Elements("Rung"));
    }

    [Fact]
    public void EnsureRllContent_NonRllRoutine_ReturnsNull()
    {
        var routine = new XElement("Routine", new XAttribute("Type", "ST"));
        Assert.Null(ProposeRungEditHandler.EnsureRllContent(routine));
        Assert.Null(routine.Element("RLLContent"));
    }

    [Fact]
    public void CheckImportAddedRungs_ZeroLandedWhenRungsExpected_ReturnsImportAddedNoRungs()
    {
        var result = ProposeRungEditHandler.CheckImportAddedRungs(
            ValidXPath, ["XIC(A)OTE(B);"], landedCount: 0, foundTexts: []);

        var mismatch = Assert.IsType<ProposeRungEditMismatch>(result);
        Assert.Equal("ImportAddedNoRungs", mismatch.Kind);
    }

    [Fact]
    public void CheckImportAddedRungs_ExpectedRungsLanded_ReturnsNull()
    {
        Assert.Null(ProposeRungEditHandler.CheckImportAddedRungs(
            ValidXPath, ["XIC(A)OTE(B);"], landedCount: 1, foundTexts: ["XIC(A)OTE(B);"]));
    }

    [Fact]
    public async Task ControllerPublish_ImportAddsNoRungs_ReturnsImportAddedNoRungs()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = SelfClosedExport.Replace("Use=\"Target\" ", "");
        fake.ReflectAcceptedImportsInExport = false;

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var mismatch = Assert.IsType<ProposeRungEditMismatch>(result);
        Assert.Equal("ImportAddedNoRungs", mismatch.Kind);
    }

    [Fact]
    public async Task ControllerPublish_ImportLandsExpectedRungs_StillSucceeds()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = Export("I", "XIC(A)OTE(B);");

        var result = await ProposeRungEditHandler.ExecuteAsync(
            ValidXPath, 0, 0, Fragment("XIC(A)OTE(B);"), session, target: "controller");

        var success = Assert.IsType<ProposeRungEditResult>(result);
        Assert.Equal("verified", success.Verification);
        Assert.Equal(1, success.VerifiedPendingRungs);
    }
}
#endif
