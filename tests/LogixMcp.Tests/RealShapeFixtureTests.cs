using System.Xml.Linq;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Pins the element and attribute structure of the real-shape fixtures under
/// <c>fixtures/real-shape/</c>. Each file copies the structure of a real Studio 5000 export
/// with every name invented. Tests that exercise the write gate load these files instead of
/// hand-writing XML, so a shape the exports do not have (<c>&lt;Program&gt;&lt;Parameters&gt;</c>;
/// <c>DestinationTag</c> exists only in CIP Generic messages, see <c>MsgCipGeneric.L5X</c>) cannot creep back in unnoticed.
/// </summary>
public class RealShapeFixtureTests
{
    private const string Header = "Structure copied from a real Studio 5000 export; all names invented.";

    private static string PathOf(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", name);

    private static XDocument Load(string name) =>
        XDocument.Load(PathOf(name), LoadOptions.PreserveWhitespace);

    private static IEnumerable<XElement> MessageParameters(XDocument doc) =>
        doc.Descendants("MessageParameters");

    /// <summary>All real-shape fixture file names.</summary>
    public static IEnumerable<object[]> Files() =>
        new[] { "MsgRead.L5X", "MsgWrite.L5X", "MsgCipGeneric.L5X", "UsageParameters.L5X", "EncodedRoutine.L5X" }
            .Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFixture_CarriesHeaderComment(string file)
    {
        var comments = Load(file).Nodes().OfType<XComment>().Select(c => c.Value.Trim());

        Assert.Contains(Header, comments);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFixture_ParsesAndScans_WithoutInventedShapes(string file)
    {
        var doc = Load(file);
        // Real exports carry DestinationTag only on CIP Generic messages.
        Assert.All(
            doc.Descendants("MessageParameters").Where(m => m.Attribute("DestinationTag") is not null),
            m => Assert.Equal("CIP Generic", (string?)m.Attribute("MessageType")));
        Assert.Empty(doc.Descendants("Program").Elements("Parameters"));

        var project = L5X.Parse(File.ReadAllText(PathOf(file)));
        var scan = WriteClassifier.ScanRungs(project);

        Assert.NotNull(scan);
    }

    [Fact]
    public void MsgCipGeneric_CarriesTargetObjectAndRealAttributes()
    {
        var all = MessageParameters(Load("MsgCipGeneric.L5X")).ToList();

        Assert.NotEmpty(all);
        foreach (var mp in all)
            foreach (var a in new[] { "ServiceCode", "ObjectType", "TargetObject", "AttributeNumber", "LocalIndex" })
                Assert.False(string.IsNullOrEmpty((string?)mp.Attribute(a)), $"missing {a}");
    }

    [Fact]
    public void MsgRead_HasEveryRealMessageParametersAttribute()
    {
        var mp = Assert.Single(MessageParameters(Load("MsgRead.L5X")));

        Assert.Equal("CIP Data Table Read", (string?)mp.Attribute("MessageType"));
        foreach (var a in new[]
                 {
                     "RemoteElement", "RequestedLength", "ConnectedFlag", "ConnectionPath",
                     "CommTypeCode", "LocalIndex", "LocalElement", "CacheConnections",
                 })
            Assert.False(string.IsNullOrEmpty((string?)mp.Attribute(a)), $"missing {a}");
        Assert.Equal("Message", (string?)mp.Parent!.Attribute("Format"));
        Assert.Equal("MESSAGE", (string?)mp.Parent.Parent!.Attribute("DataType"));
    }

    [Fact]
    public void MsgWrite_IsDataTableWriteWithSameAttributes()
    {
        var read = Assert.Single(MessageParameters(Load("MsgRead.L5X")));
        var write = Assert.Single(MessageParameters(Load("MsgWrite.L5X")));

        Assert.Equal("CIP Data Table Write", (string?)write.Attribute("MessageType"));
        Assert.Equal(
            read.Attributes().Select(a => a.Name.LocalName),
            write.Attributes().Select(a => a.Name.LocalName));
    }

    [Fact]
    public void UsageParameters_AreTagsInProgramTags()
    {
        var doc = Load("UsageParameters.L5X");
        var program = Assert.Single(doc.Descendants("Program"));
        var tags = program.Element("Tags")!.Elements("Tag").ToList();

        Assert.Equal(
            new[] { "Input", "Output", "InOut" },
            tags.Select(t => (string?)t.Attribute("Usage")).Where(u => u is not null).ToArray());
        Assert.Contains(tags, t => t.Attribute("Usage") is null); // an ordinary local tag
        Assert.Null(program.Element("Parameters"));
    }

    [Fact]
    public void EncodedRoutine_ReplacesTheRoutineElement()
    {
        var routines = Load("EncodedRoutine.L5X").Descendants("Routines").Single();
        var encoded = Assert.Single(routines.Elements("EncodedData"));

        Assert.Equal("Routine", (string?)encoded.Attribute("EncodedType"));
        Assert.Equal("Locked_Logic", (string?)encoded.Attribute("Name"));
        Assert.False(string.IsNullOrEmpty((string?)encoded.Attribute("EncryptionConfig")));
        Assert.DoesNotContain(routines.Elements("Routine"), r => r.Element("EncodedData") is not null);
    }

    [Fact]
    public void EncodedRoutine_FailsClosedOnControllerAndProgramCommandTags()
    {
        var project = L5X.Parse(File.ReadAllText(PathOf("EncodedRoutine.L5X")));

        var scan = WriteClassifier.ScanRungs(project);

        var provenance = scan.UnknownContext["Cmd_Go"];
        Assert.Contains("Line_A/Locked_Logic", provenance);
        Assert.Contains("EncodedType=\"Routine\"", provenance);
    }
}
