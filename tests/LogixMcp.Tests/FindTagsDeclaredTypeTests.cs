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
}
