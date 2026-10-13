using System.Xml.Linq;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Verifies <c>fixtures/DemoRecipe.L5X</c>: it parses, declares the
/// RecipeStep / Recipe / STRING_20 types and the <c>Recipe1</c> tag with its non-default
/// initial values, and its envelope is derived from <c>DemoInterlock.L5X</c>.
/// </summary>
public class DemoRecipeFixtureTests
{
    private static string Path_(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static readonly string FixturePath = Path_("DemoRecipe.L5X");

    /// <summary>Loads the fixture and checks the types, tag and initial values it declares.</summary>
    [Fact]
    public void Fixture_ParsesAndDeclaresRecipe1()
    {
        var content = L5X.Load(FixturePath);
        Assert.Equal("RecipeCtrl", content.Controller.Name);

        var doc = XDocument.Load(FixturePath);
        var ctrl = doc.Root!.Element("Controller")!;

        // Types
        XElement Type(string n) => ctrl.Element("DataTypes")!.Elements("DataType").Single(d => (string?)d.Attribute("Name") == n);
        string[] Members(XElement t) => t.Element("Members")!.Elements("Member").Select(m => (string)m.Attribute("Name")!).ToArray();
        string MemberType(XElement t, string m) => (string)t.Element("Members")!.Elements("Member").Single(x => (string)x.Attribute("Name")! == m).Attribute("DataType")!;

        var s20 = Type("STRING_20");
        Assert.Equal("StringFamily", (string?)s20.Attribute("Family"));
        Assert.Equal("DINT", MemberType(s20, "LEN"));
        Assert.Equal("SINT", MemberType(s20, "DATA"));
        Assert.Equal("20", (string?)s20.Element("Members")!.Elements("Member").Single(m => (string?)m.Attribute("Name") == "DATA")!.Attribute("Dimension"));

        var step = Type("RecipeStep");
        Assert.Equal("STRING", MemberType(step, "Name"));
        Assert.Equal("DINT", MemberType(step, "PRE"));
        Assert.Equal("BOOL", MemberType(step, "Enable"));
        Assert.Equal("STRING_20", MemberType(step, "Note"));
        Assert.Contains("Name", Members(step));

        var recipe = Type("Recipe");
        Assert.Equal("RecipeStep", MemberType(recipe, "Step"));
        Assert.Equal("INT", MemberType(recipe, "Count"));

        // Tag
        Assert.Contains(content.Tags, t => t.Name == "Recipe1" && t.DataType == "Recipe");
        var tag = ctrl.Element("Tags")!.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Recipe1");
        Assert.Equal("Recipe", (string?)tag.Attribute("DataType"));

        var s = tag.Descendants("StructureMember").Single(m => (string?)m.Attribute("Name") == "Step");
        string Val(XElement parent, string name) =>
            (string)parent.Elements("DataValueMember").Single(v => (string?)v.Attribute("Name") == name).Attribute("Value")!;
        string Text(XElement strMember) =>
            strMember.Elements("DataValueMember").Single(v => (string?)v.Attribute("Name") == "DATA").Value;

        var name = s.Elements("StructureMember").Single(m => (string?)m.Attribute("Name") == "Name");
        var note = s.Elements("StructureMember").Single(m => (string?)m.Attribute("Name") == "Note");
        Assert.Equal("'Mix'", Text(name));
        Assert.Equal("3", Val(name, "LEN"));
        Assert.Equal("'hold 5s'", Text(note));
        Assert.Equal("7", Val(note, "LEN"));
        Assert.Equal("1500", Val(s, "PRE"));
        Assert.Equal("1", Val(s, "Enable"));
        Assert.Equal("3", Val(tag.Descendants("Structure").Single(), "Count"));
    }

    /// <summary>The envelope attributes must match DemoInterlock.L5X (derived, not hand-built).</summary>
    [Fact]
    public void Fixture_EnvelopeMatchesDemoInterlock()
    {
        var recipe = XDocument.Load(FixturePath).Root!;
        var interlock = XDocument.Load(Path_("DemoInterlock.L5X")).Root!;

        foreach (var attr in new[] { "SchemaRevision", "SoftwareRevision", "TargetType", "ContainsContext" })
            Assert.Equal((string?)interlock.Attribute(attr), (string?)recipe.Attribute(attr));

        var rc = recipe.Element("Controller")!;
        var ic = interlock.Element("Controller")!;
        foreach (var attr in new[] { "MajorRev", "MinorRev", "ProcessorType" })
            Assert.Equal((string?)ic.Attribute(attr), (string?)rc.Attribute(attr));
    }
}
