#if LOGIX_SDK
using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// <see cref="RungFragmentBuilder"/>: the typed
/// rungs/tags are written into the SDK export envelope without disturbing it.
/// </summary>
public class RungFragmentBuilderTests
{
    private const string Envelope =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Use=\"Context\" Name=\"DemoController\">" +
        "<DataTypes Use=\"Context\"/>" +
        "<Programs Use=\"Context\"><Program Use=\"Context\" Name=\"MainProgram\"><Routines Use=\"Context\">" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent/></Routine>" +
        "</Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    private static XDocument Build(
        string envelope, uint insert, RungInput[] rungs, TagInput[]? tags = null) =>
        XDocument.Parse(RungFragmentBuilder.BuildOnline(envelope, insert, rungs, tags));

    [Fact]
    public void Typed_BuildsFromExportEnvelope_RootAttributesAndUseMarkersPreserved()
    {
        var built = RungFragmentBuilder.BuildOnline(
            Envelope, 0, new[] { new RungInput("XIC(A)OTE(B);") }, null);
        Assert.StartsWith("<?xml", built);

        var root = XDocument.Parse(built).Root!;
        var original = XDocument.Parse(Envelope).Root!;
        Assert.Equal("RSLogix5000Content", root.Name.LocalName);
        foreach (var attr in original.Attributes())
            Assert.Equal(attr.Value, (string?)root.Attribute(attr.Name));

        var controller = root.Element("Controller")!;
        Assert.Equal("Context", (string?)controller.Attribute("Use"));
        Assert.Equal("Context", (string?)controller.Element("Programs")!.Attribute("Use"));
        var routine = root.Descendants("Routine").Single();
        Assert.Equal("Target", (string?)routine.Attribute("Use"));
        Assert.Equal("MainRoutine", (string?)routine.Attribute("Name"));
        Assert.Single(routine.Element("RLLContent")!.Elements("Rung"));
    }

    // A routine export whose program uses an AOI lists the AOI definition, with its Logic
    // routine's rungs, before the target routine (bench 2026-10-06: the typed rungs went
    // into the AOI's Logic routine and the online import failed XMLSrv_E_ONLINE_OVERWRITE_UDI).
    private const string AoiEnvelope =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"38.02\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\">" +
        "<Controller Use=\"Context\" Name=\"DemoController\">" +
        "<AddOnInstructionDefinitions Use=\"Context\">" +
        "<AddOnInstructionDefinition Use=\"Context\" Name=\"Pulse_AOI\"><Routines>" +
        "<Routine Name=\"Logic\" Type=\"RLL\"><RLLContent>" +
        "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[XIC(In)OTE(Out);]]></Text></Rung>" +
        "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></AddOnInstructionDefinition>" +
        "</AddOnInstructionDefinitions>" +
        "<Programs Use=\"Context\"><Program Use=\"Context\" Name=\"MainProgram\"><Routines Use=\"Context\">" +
        "<Routine Use=\"Target\" Name=\"MainRoutine\" Type=\"RLL\"><RLLContent/></Routine>" +
        "</Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Typed_AoiInExport_RungsGoToTargetRoutine_AoiLogicUntouched(bool useMarker)
    {
        var envelope = useMarker ? AoiEnvelope : AoiEnvelope.Replace("<Routine Use=\"Target\" ", "<Routine ");
        var doc = Build(envelope, 0, new[] { new RungInput("XIC(A)OTE(B);") });

        var aoiRungs = doc.Descendants("AddOnInstructionDefinition").Single().Descendants("Rung").ToList();
        Assert.Equal(2, aoiRungs.Count);
        Assert.Equal("XIC(In)OTE(Out);", aoiRungs[0].Element("Text")!.Value);

        var target = doc.Descendants("Program").Single().Descendants("Rung").Single();
        Assert.Equal("XIC(A)OTE(B);", target.Element("Text")!.Value);
    }

    [Fact]
    public void Typed_CommentWithNonBmpCharacter_RoundTrips()
    {
        const string comment = "pump \U0001F600 start <&> \u00e9";
        var doc = Build(Envelope, 0, new[] { new RungInput("XIC(A)OTE(B);", comment) });

        var rung = doc.Descendants("RLLContent").Single().Elements("Rung").Single();
        Assert.Equal(comment, rung.Element("Comment")!.Value);
    }

    [Fact]
    public void Typed_NumbersRungsFromInsertPosition_CdataTextAndComment()
    {
        var doc = Build(Envelope, 3, new[]
        {
            new RungInput("XIC(A)OTE(B);", "first <&> comment"),
            new RungInput("NOP();"),
        });

        var rungs = doc.Descendants("RLLContent").Single().Elements("Rung").ToList();
        Assert.Equal(2, rungs.Count);
        Assert.Equal("3", (string?)rungs[0].Attribute("Number"));
        Assert.Equal("4", (string?)rungs[1].Attribute("Number"));
        Assert.All(rungs, r => Assert.Equal("N", (string?)r.Attribute("Type")));

        Assert.Equal("XIC(A)OTE(B);", rungs[0].Element("Text")!.Value);
        Assert.All(rungs[0].Element("Text")!.Nodes(), n => Assert.IsType<XCData>(n));
        Assert.Equal("first <&> comment", rungs[0].Element("Comment")!.Value);
        // The comment is handed to the XElement as a raw string (not CDATA) so every character round-trips.
        Assert.All(rungs[0].Element("Comment")!.Nodes(), n => Assert.IsNotType<XCData>(n));
        Assert.Equal("Comment", rungs[0].Elements().First().Name.LocalName);

        // No comment given: no Comment element at all.
        Assert.Null(rungs[1].Element("Comment"));
    }

    [Fact]
    public void NewTag_DeclaredExternalAccessReadOnly()
    {
        var tags = new[]
        {
            new TagInput("Lamp", "BOOL"),
            new TagInput("Tmr", "TIMER"),
            new TagInput("Counts", "DINT", Dimensions: "10"),
        };

        var doc = Build(Envelope, 0, new[] { new RungInput("NOP();") }, tags);
        var declared = doc.Descendants("Tags").Single().Elements("Tag").ToList();
        Assert.Equal(3, declared.Count);
        Assert.All(declared, t => Assert.Equal("Read Only", (string?)t.Attribute("ExternalAccess")));
    }

    [Fact]
    public void Typed_TagsGoInControllerTagsUseContext_CreatedWhenAbsent()
    {
        var tags = new[]
        {
            new TagInput("Lamp", "BOOL", Description: "The lamp"),
            new TagInput("Counts", "DINT", Dimensions: "10"),
            new TagInput("Tmr", "TIMER"),
            new TagInput("Speed", "REAL", Radix: "Decimal"),
        };

        // Absent: created after DataTypes, before Programs.
        var doc = Build(Envelope, 0, new[] { new RungInput("NOP();") }, tags);
        var controller = doc.Root!.Element("Controller")!;
        var context = Assert.Single(controller.Elements("Tags"));
        Assert.Equal("Context", (string?)context.Attribute("Use"));
        Assert.Equal(new[] { "DataTypes", "Tags", "Programs" },
            controller.Elements().Select(e => e.Name.LocalName).ToArray());

        var lamp = context.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Lamp");
        Assert.Equal(new[] { "Name", "TagType", "DataType", "Radix", "Constant", "ExternalAccess" },
            lamp.Attributes().Select(a => a.Name.LocalName).ToArray());
        Assert.Equal("Base", (string?)lamp.Attribute("TagType"));
        Assert.Equal("Decimal", (string?)lamp.Attribute("Radix"));
        Assert.Equal("false", (string?)lamp.Attribute("Constant"));
        Assert.Equal("Read Only", (string?)lamp.Attribute("ExternalAccess"));
        Assert.Equal("The lamp", lamp.Element("Description")!.Value);
        Assert.IsType<XCData>(lamp.Element("Description")!.FirstNode);

        var counts = context.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Counts");
        Assert.Equal("10", (string?)counts.Attribute("Dimensions"));
        Assert.Null(counts.Element("Description"));

        // No default radix for structured types; explicit radix wins.
        Assert.Null(context.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Tmr").Attribute("Radix"));
        Assert.Equal("Decimal", (string?)context.Elements("Tag")
            .Single(t => (string?)t.Attribute("Name") == "Speed").Attribute("Radix"));
        Assert.Equal("Float", RungFragmentBuilder.DefaultRadix("real"));

        // Present: the export's own Tags Use="Context" is reused; a tag it already declares is kept.
        var withTags = Envelope.Replace("<Programs ",
            "<Tags Use=\"Context\"><Tag Name=\"Lamp\" TagType=\"Base\" DataType=\"BOOL\"/></Tags><Programs ");
        var doc2 = Build(withTags, 0, new[] { new RungInput("NOP();") }, tags);
        var context2 = Assert.Single(doc2.Root!.Element("Controller")!.Elements("Tags"));
        Assert.Equal(4, context2.Elements("Tag").Count());
        Assert.Single(context2.Elements("Tag"), t => (string?)t.Attribute("Name") == "Lamp");
    }

    [Fact]
    public void Typed_AppendsMissingSemicolon()
    {
        var err = ProposeRungEditHandler.ValidateTypedInput(
            null, new[] { new RungInput("XIC(A)OTE(B)") }, null, out var normalized);
        Assert.Null(err);

        var doc = Build(Envelope, 0, normalized.ToArray());
        Assert.Equal("XIC(A)OTE(B);", doc.Descendants("Rung").Single().Element("Text")!.Value);
    }

    [Fact]
    public void BuildOffline_TakesEnvelopeAttributesFromProjectRoot()
    {
        var projectRoot = XElement.Parse(
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
            "TargetName=\"Demo\" TargetType=\"Controller\"><Controller Name=\"Demo\"/></RSLogix5000Content>");

        var doc = XDocument.Parse(RungFragmentBuilder.BuildOffline(
            projectRoot, "MainProgram", "MainRoutine", 2,
            new[] { new RungInput("NOP();") }, null));

        Assert.Equal("1.0", (string?)doc.Root!.Attribute("SchemaRevision"));
        Assert.Equal("Controller", (string?)doc.Root.Attribute("TargetType"));
        var rung = doc.Descendants("RLLContent").Single().Elements("Rung").Single();
        Assert.Equal("2", (string?)rung.Attribute("Number"));
    }

    // ── HMIBC: Logix rejects Read Only ("Incompatible External Access Value") ──

    [Fact]
    public void HmibcDeclaration_GetsReadWriteExternalAccess()
    {
        var doc = Build(Envelope, 0, new[] { new RungInput("NOP();") },
            new[] { new TagInput("Hmi_Bc", "HMIBC") });

        var tag = doc.Descendants("Tags").Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Hmi_Bc");
        Assert.Equal("Read/Write", (string?)tag.Attribute("ExternalAccess"));
    }

    [Fact]
    public void HmibcDeclaration_ExternalAccessForcedReportsValueAndReason()
    {
        var entries = RungFragmentBuilder.ForcedReadWriteEntries(
            new[] { new TagInput("Hmi_Bc", "hmibc"), new TagInput("Lamp", "BOOL"), new TagInput("Old_Bc", "HMIBC") },
            new[] { "Old_Bc" });

        var entry = Assert.Single(entries);
        Assert.StartsWith("Hmi_Bc: Read/Write (", entry);
        Assert.Contains("HMIBC", entry);
    }

    [Fact]
    public void NonHmibcTags_StillDefaultToReadOnly()
    {
        var doc = Build(Envelope, 0, new[] { new RungInput("NOP();") }, new[]
        {
            new TagInput("Lamp", "BOOL"),
            new TagInput("Tmr", "TIMER"),
            new TagInput("Hmi_Bc", "HMIBC"),
        });

        string? Access(string n) => (string?)doc.Descendants("Tags").Elements("Tag")
            .Single(t => (string?)t.Attribute("Name") == n).Attribute("ExternalAccess");
        Assert.Equal("Read Only", Access("Lamp"));
        Assert.Equal("Read Only", Access("Tmr"));
        Assert.Equal("Read/Write", Access("Hmi_Bc"));
        Assert.Empty(RungFragmentBuilder.ForcedReadWriteEntries(
            new[] { new TagInput("Lamp", "BOOL") }, Array.Empty<string>()));
    }
}
#endif
