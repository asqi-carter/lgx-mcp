using System.Xml.Linq;

namespace LogixMcp.Tests;

/// <summary>
/// Round trip from <c>logix_list_datatypes</c> to <c>logix_import_datatype</c>: the rendered
/// fragment must use L5X spellings and L5X BOOL structure, and a fragment that breaks the
/// rules is refused before anything is written.
/// </summary>
public class ImportDatatypeRoundTripTests
{
    private static string RealShapePath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "DataTypeBool.L5X");

    private static string TempL5x() =>
        Path.Combine(Path.GetTempPath(), $"logixmcp-roundtrip-{Guid.NewGuid():N}.L5X");

    private static ImportDatatypeResult DryRun(
        IReadOnlyList<DataTypeMemberEntry> members, string name = "Pump_Cfg")
    {
        var result = ImportDatatypeHandler.Execute(
            acdPath:      "/tmp/project.ACD",
            datatypeName: name,
            l5xPath:      null,
            members:      members,
            saveAs:       TempL5x(),
            dryRun:       true);
        return Assert.IsType<ImportDatatypeResult>(result);
    }

    private static List<XElement> TargetMembers(string fragment) =>
        XDocument.Parse(fragment).Descendants("DataType")
            .Single(d => d.Attribute("Use")?.Value == "Target")
            .Element("Members")!.Elements("Member").ToList();

    [Fact]
    public void ListThenImport_ProducesL5xSpellings()
    {
        var service = new ProjectService();
        service.Load(RealShapePath);

        var listed = Assert.IsType<ListDatatypesResult>(ListDatatypesHandler.Execute(service));
        var pump   = Assert.Single(listed.DataTypes, d => d.Name == "Pump_Cfg");
        Assert.NotNull(pump.Members);

        // Feed the listed members straight back into the import.
        var fragment = DryRun(pump.Members!).L5XFragment;
        var members  = TargetMembers(fragment);

        var allowedAccess = new[] { "Read/Write", "Read Only", "None" };
        var allowedRadix  = new[] { "Binary", "Octal", "Decimal", "Hex", "Exponential", "Float", "ASCII" };
        foreach (var m in members)
        {
            Assert.Contains(m.Attribute("ExternalAccess")!.Value, allowedAccess);
            var radix = m.Attribute("Radix")?.Value;
            if (radix is not null) Assert.Contains(radix, allowedRadix);
        }

        Assert.Equal("Read Only", members.Single(m => (string?)m.Attribute("Name") == "Setpoint")
                                         .Attribute("ExternalAccess")!.Value);
        Assert.Equal("Float", members.Single(m => (string?)m.Attribute("Name") == "Setpoint")
                                     .Attribute("Radix")!.Value);
        Assert.Equal("ASCII", members.Single(m => (string?)m.Attribute("Name") == "Codes")
                                     .Attribute("Radix")!.Value);
        Assert.Equal("None", members.Single(m => (string?)m.Attribute("Name") == "Codes")
                                    .Attribute("ExternalAccess")!.Value);

        // BOOLs: one hidden SINT host, then BOOL members pointing at it with bit numbers.
        var host = Assert.Single(members, m => (string?)m.Attribute("Hidden") == "true");
        Assert.Equal("SINT", host.Attribute("DataType")!.Value);
        var hostName = host.Attribute("Name")!.Value;
        var bools = members.Where(m => (string?)m.Attribute("DataType") == "BOOL").ToList();
        Assert.Equal(2, bools.Count);
        Assert.All(bools, b => Assert.Equal(hostName, b.Attribute("Target")!.Value));
        Assert.Equal(new[] { "0", "1" }, bools.Select(b => b.Attribute("BitNumber")!.Value).ToArray());
        Assert.True(members.IndexOf(host) < members.IndexOf(bools[0]));
    }

    [Fact]
    public void ListStyleAndL5xStyleSpellings_BothAccepted()
    {
        var fragment = DryRun(new DataTypeMemberEntry[]
        {
            new("A", "REAL", "0", "Float", false, "ReadOnly"),
            new("B", "SINT", "4", "Ascii", false, "Read/Write"),
            new("C", "DINT", "0", "Hex",   false, "Read Only"),
            new("D", "DINT", "0", "DateTime", false, "ReadWrite"),
        }).L5XFragment;

        var m = TargetMembers(fragment);
        Assert.Equal(new[] { "Read Only", "Read/Write", "Read Only", "Read/Write" },
            m.Select(x => x.Attribute("ExternalAccess")!.Value).ToArray());
        Assert.Equal(new[] { "Float", "ASCII", "Hex", "Date/Time" },
            m.Select(x => x.Attribute("Radix")!.Value).ToArray());
    }

    [Fact]
    public void EmptyRadixOmitted()
    {
        var fragment = DryRun(new DataTypeMemberEntry[]
        {
            new("Label", "STRING", "0", "",         false, "Read/Write"),
            new("Other", "TIMER",  "0", "NullType", false, "Read/Write"),
            new("Count", "DINT",   "0", "Decimal",  false, "Read/Write"),
        }).L5XFragment;

        var m = TargetMembers(fragment);
        Assert.Null(m[0].Attribute("Radix"));
        Assert.Null(m[1].Attribute("Radix"));
        Assert.Equal("Decimal", m[2].Attribute("Radix")!.Value);
        Assert.DoesNotContain("Radix=\"\"", fragment);
    }

    [Fact]
    public void BoolMembersWithoutHost_GetPackedIntoSintHosts()
    {
        var members = Enumerable.Range(0, 10)
            .Select(i => new DataTypeMemberEntry($"F{i}", "BOOL", "0", "", false, "Read/Write"))
            .ToArray();

        var m = TargetMembers(DryRun(members, "Flags").L5XFragment);

        var hosts = m.Where(x => (string?)x.Attribute("Hidden") == "true").ToList();
        Assert.Equal(2, hosts.Count);
        Assert.All(hosts, h => Assert.Equal("SINT", h.Attribute("DataType")!.Value));
        Assert.Equal(12, m.Count);
        Assert.Equal("7", m.Single(x => (string?)x.Attribute("Name") == "F7").Attribute("BitNumber")!.Value);
        Assert.Equal("0", m.Single(x => (string?)x.Attribute("Name") == "F8").Attribute("BitNumber")!.Value);
    }

    [Theory]
    [InlineData("ExternalAccess")]
    [InlineData("Radix")]
    public void UnknownSpelling_RefusedBeforeWrite(string field)
    {
        var bad = field == "Radix"
            ? new DataTypeMemberEntry("A", "DINT", "0", "Sparkly", false, "Read/Write")
            : new DataTypeMemberEntry("A", "DINT", "0", "Decimal", false, "Sometimes");
        var saveAs = TempL5x();

        var result = ImportDatatypeHandler.Execute(
            acdPath: "/tmp/project.ACD", datatypeName: "Pump_Cfg", l5xPath: null,
            members: new[] { bad }, saveAs: saveAs, dryRun: false, confirm: true);

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal(ImportDatatypeHandler.InvalidFragmentKind, err.Kind);
        Assert.Contains("A", err.Error);
        Assert.False(File.Exists(saveAs));
    }

    [Fact]
    public void BoolWithMissingHost_RefusedBeforeWrite()
    {
        var saveAs = TempL5x();
        var members = new[]
        {
            new DataTypeMemberEntry("Run", "BOOL", "0", "", false, "Read/Write",
                Target: "NoSuchHost", BitNumber: 0),
        };

        var result = ImportDatatypeHandler.Execute(
            acdPath: "/tmp/project.ACD", datatypeName: "Pump_Cfg", l5xPath: null,
            members: members, saveAs: saveAs, dryRun: false, confirm: true);

        var err = Assert.IsType<ImportDatatypeError>(result);
        Assert.Equal(ImportDatatypeHandler.InvalidFragmentKind, err.Kind);
        Assert.False(File.Exists(saveAs));
    }
}
