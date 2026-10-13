using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Argument-validation tests for <c>logix_import_datatype</c> member lists.
/// </summary>
public class ImportDatatypeArgumentTests
{
    private static string Run(DataTypeMemberEntry?[] members) =>
        new LogixProjectTools(new ProjectService()).GetImportDatatypeResponse(
            acdPath:      "/tmp/project.ACD",
            datatypeName: "MyType",
            l5xPath:      null,
            members:      members!,
            saveAs:       "/tmp/argtest-output.L5X");

    private static void AssertStructuredError(string json, string expectedIndexText, string field)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("InvalidInput", root.GetProperty("kind").GetString());
        var error = root.GetProperty("error").GetString()!;
        Assert.Contains(expectedIndexText, error);
        Assert.Contains($"'{field}'", error);
        Assert.DoesNotContain("An error occurred invoking", json);
    }

    [Fact]
    public void PartialMember_StructuredError()
    {
        // Binder leaves absent JSON properties null: second member has no name, third no dataType.
        var members = JsonSerializer.Deserialize<DataTypeMemberEntry?[]>(
            """
            [
              {"name":"Enable","dataType":"BOOL","dimension":"0","radix":"NullType","hidden":false,"externalAccess":"Read/Write"},
              {"dataType":"DINT"},
              {"name":"Speed"}
            ]
            """)!;

        AssertStructuredError(Run(members), "members[1]", "name");

        members[1] = members[1]! with { Name = "Mid" };
        AssertStructuredError(Run(members), "members[2]", "dataType");
    }

    [Fact]
    public void NullMemberEntry_StructuredError()
    {
        var json = Run(new DataTypeMemberEntry?[] { null });
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("InvalidInput", root.GetProperty("kind").GetString());
        Assert.Contains("members[0]", root.GetProperty("error").GetString());
    }

    [Fact]
    public void MemberMissingOnlyOptionalFields_Succeeds()
    {
        var members = JsonSerializer.Deserialize<DataTypeMemberEntry?[]>(
            """[{"name":"Enable","dataType":"BOOL"}]""")!;
        var root = JsonDocument.Parse(Run(members)).RootElement;
        Assert.True(root.TryGetProperty("l5xFragment", out _));
    }

    private static DataTypeMemberEntry?[] Parse(string json) =>
        JsonSerializer.Deserialize<DataTypeMemberEntry?[]>(json)!;

    [Fact]
    public void NumericDimensionWhereStringExpected_StructuredError()
    {
        var members = Parse(
            """
            [
              {"name":"Enable","dataType":"BOOL"},
              {"name":"Speed","dataType":"DINT","dimension":0}
            ]
            """);
        AssertStructuredError(Run(members), "members[1]", "dimension");
    }

    [Fact]
    public void ObjectWhereNumberExpected_StructuredError()
    {
        var members = Parse(
            """[{"name":"Flag","dataType":"BOOL","target":"ZZ","bitNumber":{"bit":3}}]""");
        AssertStructuredError(Run(members), "members[0]", "bitNumber");
    }

    [Fact]
    public void NonObjectMember_StructuredError()
    {
        var json = Run(Parse("""[{"name":"A","dataType":"DINT"}, 5]"""));
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("InvalidInput", root.GetProperty("kind").GetString());
        Assert.Contains("members[1]", root.GetProperty("error").GetString());
        Assert.DoesNotContain("An error occurred invoking", json);
    }

    [Fact]
    public void MemberEntry_RoundTripsThroughConverter()
    {
        var entry = new DataTypeMemberEntry("A", "BOOL", "0", "Decimal", true, "Read/Write", "ZZ", 3);
        var back = JsonSerializer.Deserialize<DataTypeMemberEntry>(JsonSerializer.Serialize(entry));
        Assert.Equal(entry, back);
    }
}
