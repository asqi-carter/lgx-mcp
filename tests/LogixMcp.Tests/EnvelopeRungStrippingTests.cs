#if LOGIX_SDK
using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// The import envelope built from a routine export strips rungs only from the
/// <c>Routine Use="Target"</c>; a context AOI definition and its Logic routine stay as exported.
/// The fixture is hand-written (no customer files).
/// </summary>
public class EnvelopeRungStrippingTests
{
    private const string ExportWithAoi =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" ContainsContext=\"true\">" +
        "<Controller Use=\"Context\" Name=\"DemoController\">" +
        "<AddOnInstructionDefinitions Use=\"Context\">" +
        "<AddOnInstructionDefinition Use=\"Context\" Name=\"Demo_Aoi\" Revision=\"1.0\">" +
        "<Parameters><Parameter Name=\"EnableIn\" TagType=\"Base\" DataType=\"BOOL\" Usage=\"Input\"/></Parameters>" +
        "<Routines><Routine Name=\"Logic\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(Sensor)OTE(Output);]]></Text></Rung>" +
        "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[XIO(Sensor)OTU(Output);]]></Text></Rung>" +
        "</RLLContent></Routine></Routines>" +
        "</AddOnInstructionDefinition></AddOnInstructionDefinitions>" +
        "<Programs Use=\"Context\"><Program Use=\"Context\" Name=\"MainProgram\"><Routines Use=\"Context\">" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(A)OTE(B);]]></Text></Rung>" +
        "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[Demo_Aoi(C);]]></Text></Rung>" +
        "</RLLContent></Routine>" +
        "</Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    [Fact]
    public void StripTargetRoutineRungs_RemovesRungsUnderUseTargetRoutine()
    {
        var stripped = XDocument.Parse(ProposeRungEditHandler.StripTargetRoutineRungs(ExportWithAoi));

        var target = stripped.Descendants("Routine")
            .Single(r => (string?)r.Attribute("Use") == "Target");
        Assert.Equal("MainRoutine", (string?)target.Attribute("Name"));
        Assert.NotNull(target.Element("RLLContent"));
        Assert.Empty(target.Element("RLLContent")!.Elements("Rung"));
    }

    [Fact]
    public void StripTargetRoutineRungs_PreservesAoiDefinitionAndLogicRoutineExactly()
    {
        var original = XDocument.Parse(ExportWithAoi);
        var stripped = XDocument.Parse(ProposeRungEditHandler.StripTargetRoutineRungs(ExportWithAoi));

        var originalAoi = original.Descendants("AddOnInstructionDefinition").Single();
        var strippedAoi = stripped.Descendants("AddOnInstructionDefinition").Single();
        Assert.True(XNode.DeepEquals(originalAoi, strippedAoi));

        var logic = strippedAoi.Descendants("Routine").Single(r => (string?)r.Attribute("Name") == "Logic");
        var texts = logic.Element("RLLContent")!.Elements("Rung").Select(r => r.Element("Text")!.Value).ToList();
        Assert.Equal(new[] { "XIC(Sensor)OTE(Output);", "XIO(Sensor)OTU(Output);" }, texts);
    }

    [Fact]
    public void StripTargetRoutineRungs_KeepsRootAttributesAndDeclaration()
    {
        var result = ProposeRungEditHandler.StripTargetRoutineRungs(ExportWithAoi);
        Assert.StartsWith("<?xml", result);

        var root = XDocument.Parse(result).Root!;
        foreach (var attr in XDocument.Parse(ExportWithAoi).Root!.Attributes())
            Assert.Equal(attr.Value, (string?)root.Attribute(attr.Name));
    }
}
#endif
