namespace LogixMcp.Tests;

/// <summary>
/// <c>logix_find_tags</c> on a tag declared without a <c>&lt;Data&gt;</c> element: the declared
/// <c>DataType</c> attribute is reported, never L5Sharp's "NULL" placeholder.
/// </summary>
public class FindTagsDeclaredTypeTests
{
    private const string L5x = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="NoDataCtrl" TargetType="Controller" ContainsContext="false">
          <Controller Name="NoDataCtrl" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
            <DataTypes />
            <Modules />
            <AddOnInstructionDefinitions />
            <Tags>
              <Tag Name="NoDataDint" TagType="Base" DataType="DINT" Radix="Decimal" ExternalAccess="Read/Write" />
              <Tag Name="NoDataTimer" TagType="Base" DataType="TIMER" ExternalAccess="Read/Write" />
              <Tag Name="Arr" TagType="Base" DataType="REAL" Dimensions="5" Radix="Decimal" ExternalAccess="Read/Write" />
              <Tag Name="Grid" TagType="Base" DataType="REAL" Dimensions="2 3" Radix="Decimal" ExternalAccess="Read/Write" />
            </Tags>
            <Programs>
              <Program Name="P1" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="Main">
                <Tags>
                  <Tag Name="ProgNoData" TagType="Base" DataType="BOOL" Radix="Decimal" ExternalAccess="Read/Write" />
                </Tags>
                <Routines>
                  <Routine Name="Main" Type="RLL"><RLLContent /></Routine>
                </Routines>
              </Program>
            </Programs>
            <Tasks />
          </Controller>
        </RSLogix5000Content>
        """;

    private static ProjectService Load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nodata-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, L5x);
        try
        {
            var svc = new ProjectService();
            svc.Load(path);
            return svc;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FindTags_TagWithoutData_ReportsDeclaredDataType()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load()));

        Assert.Equal("DINT",  result.Tags.Single(t => t.Name == "NoDataDint").Type);
        Assert.Equal("TIMER", result.Tags.Single(t => t.Name == "NoDataTimer").Type);
        Assert.Equal("BOOL",  result.Tags.Single(t => t.Name == "ProgNoData").Type);
        Assert.DoesNotContain(result.Tags, t => t.Type == "NULL");
    }

    [Fact]
    public void FindTags_TagWithoutData_MatchesDataTypeFilter()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load(), null, "dint"));

        Assert.Equal("NoDataDint", Assert.Single(result.Tags).Name);
    }

    [Fact]
    public void FindTags_ArrayTag_CarriesDimensions_AndScalarsOmitThem()
    {
        var result = Assert.IsType<FindTagsResult>(FindTagsHandler.Execute(Load()));

        Assert.Equal([5],    result.Tags.Single(t => t.Name == "Arr").Dimensions);
        Assert.Equal([2, 3], result.Tags.Single(t => t.Name == "Grid").Dimensions);
        Assert.Null(result.Tags.Single(t => t.Name == "NoDataDint").Dimensions);

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var tags = doc.RootElement.GetProperty("tags").EnumerateArray().ToList();
        Assert.Equal(5, tags.Single(t => t.GetProperty("name").GetString() == "Arr").GetProperty("dimensions")[0].GetInt32());
        Assert.False(tags.Single(t => t.GetProperty("name").GetString() == "NoDataDint").TryGetProperty("dimensions", out _));
    }
}
