using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Tests that <see cref="DiffProjectsHandler.Compare"/> ignores export-format noise
/// (ProductDefined / IO data types) and still compares user-defined types.
/// </summary>
public class DiffNormalizationTests
{
    private static string Member(string type) =>
        $"<Members><Member Name=\"A\" DataType=\"{type}\" Dimension=\"0\" Radix=\"NullType\" ExternalAccess=\"Read/Write\" Hidden=\"false\" /></Members>";

    private static L5X Project(string dataTypes)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            $"<DataTypes>{dataTypes}</DataTypes>" +
            "<Modules /><AddOnInstructionDefinitions /><Tags /><Programs /><Tasks /><ParameterConnections /><Trends /><QuickWatchLists />" +
            "</Controller></RSLogix5000Content>";
        var path = Path.Combine(Path.GetTempPath(), $"diffnorm-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, xml);
        try { return L5X.Load(path); }
        finally { File.Delete(path); }
    }

    private static string Type(string name, string? cls, string memberType = "BOOL") =>
        $"<DataType Name=\"{name}\" Family=\"NoFamily\"{(cls is null ? "" : $" Class=\"{cls}\"")}>{Member(memberType)}</DataType>";

    [Fact]
    public void ProductDefinedAndIoTypes_Ignored()
    {
        var a = Project(Type("U", "User"));
        var b = Project(
            Type("U", "User") +
            Type("PD_Added", "ProductDefined") +
            Type("IO_Added", "IO"));

        var forward = DiffProjectsHandler.Compare(a, b);
        var reverse = DiffProjectsHandler.Compare(b, a);

        Assert.Empty(forward.DataTypes);
        Assert.Empty(reverse.DataTypes);
    }

    [Fact]
    public void UserAndClasslessTypes_StillCompared()
    {
        var a = Project(Type("U", "User") + Type("N", null) + Type("Gone", "User"));
        var b = Project(
            Type("U", "User", "DINT") + Type("N", null, "DINT") + Type("New", null) +
            Type("PD", "ProductDefined"));

        var result = DiffProjectsHandler.Compare(a, b);

        Assert.Contains(result.DataTypes, e => e.Name == "U" && e.Change == "changed");
        Assert.Contains(result.DataTypes, e => e.Name == "N" && e.Change == "changed");
        Assert.Contains(result.DataTypes, e => e.Name == "Gone" && e.Change == "removed");
        Assert.Contains(result.DataTypes, e => e.Name == "New" && e.Change == "added");
        Assert.DoesNotContain(result.DataTypes, e => e.Name == "PD");
    }

    private static L5X ProjectWithTags(string tags)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions />" +
            $"<Tags>{tags}</Tags>" +
            "<Programs /><Tasks /><ParameterConnections /><Trends /><QuickWatchLists />" +
            "</Controller></RSLogix5000Content>";
        var path = Path.Combine(Path.GetTempPath(), $"diffnorm-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, xml);
        try { return L5X.Load(path); }
        finally { File.Delete(path); }
    }

    // Cmd_MotorStart as it appears in the file export (fixtures/DemoInterlock.L5X).
    private const string CmdFile =
        "<Tag Name=\"Cmd_MotorStart\" TagType=\"Base\" ExternalAccess=\"Read/Write\" Constant=\"false\" DataType=\"BOOL\" Radix=\"Decimal\">" +
        "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    // Cmd_MotorStart as it appears in the SDK detailed export: OpcUaAccess plus an extra L5K <Data>.
    private const string CmdController =
        "<Tag Name=\"Cmd_MotorStart\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\" OpcUaAccess=\"None\">" +
        "<Data Format=\"L5K\"><![CDATA[0]]></Data>" +
        "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    [Fact]
    public void TagOpcUaAccessAndL5KData_NotAChange()
    {
        var a = ProjectWithTags(CmdFile);
        var b = ProjectWithTags(CmdController);

        Assert.Empty(DiffProjectsHandler.Compare(a, b).Tags);
        Assert.Empty(DiffProjectsHandler.Compare(b, a).Tags);
    }

    [Fact]
    public void TagExternalAccessChange_IsAChange()
    {
        var a = ProjectWithTags(CmdFile);
        var b = ProjectWithTags(CmdController.Replace("ExternalAccess=\"Read/Write\"", "ExternalAccess=\"Read Only\""));

        var result = DiffProjectsHandler.Compare(a, b);

        Assert.Contains(result.Tags, e => e.Name == "Cmd_MotorStart" && e.Change == "changed");
    }

    private static L5X ProjectWithRoutine(string routine)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions /><Tags />" +
            "<Programs><Program Name=\"MainProgram\" TestEdits=\"false\" Disabled=\"false\" UseAsFolder=\"false\">" +
            $"<Tags /><Routines>{routine}</Routines></Program></Programs>" +
            "<Tasks /><ParameterConnections /><Trends /><QuickWatchLists />" +
            "</Controller></RSLogix5000Content>";
        var path = Path.Combine(Path.GetTempPath(), $"diffnorm-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, xml);
        try { return L5X.Load(path); }
        finally { File.Delete(path); }
    }

    // AlarmRoutine as it appears in the file export (fixtures/DemoInterlock.L5X): XML comment, inline CDATA.
    private const string AlarmFile =
        "<Routine Name=\"AlarmRoutine\" Type=\"RLL\"><RLLContent>" +
        "<!-- Rung 0: Alarm_Light written; Safety_OK present as XIO (alarm when safety is NOT OK) -->" +
        "<Rung Number=\"0\" Type=\"N\">" +
        "<Comment><![CDATA[Alarm_Light fires when Safety_OK is de-energized (XIO)]]></Comment>" +
        "<Text><![CDATA[XIO(Safety_OK)OTE(Alarm_Light);]]></Text>" +
        "</Rung></RLLContent></Routine>";

    // AlarmRoutine as it appears in the SDK detailed export: no XML comment, CDATA on its own line.
    private const string AlarmController =
        "<Routine Name=\"AlarmRoutine\" Type=\"RLL\">\n  <RLLContent>\n" +
        "    <Rung Number=\"0\" Type=\"N\">\n" +
        "      <Comment>\n        <![CDATA[Alarm_Light fires when Safety_OK is de-energized (XIO)]]>\n      </Comment>\n" +
        "      <Text>\n        <![CDATA[XIO(Safety_OK)OTE(Alarm_Light);]]>\n      </Text>\n" +
        "    </Rung>\n  </RLLContent>\n</Routine>";

    [Fact]
    public void RungCommentWhitespaceAndXmlComment_NotAChange()
    {
        var a = ProjectWithRoutine(AlarmFile);
        var b = ProjectWithRoutine(AlarmController);

        Assert.Empty(DiffProjectsHandler.Compare(a, b).Routines);
        Assert.Empty(DiffProjectsHandler.Compare(b, a).Routines);
    }

    [Fact]
    public void RungTextChange_IsAChange()
    {
        var a = ProjectWithRoutine(AlarmFile);
        var b = ProjectWithRoutine(AlarmController.Replace("OTE(Alarm_Light)", "OTL(Alarm_Light)"));

        var result = DiffProjectsHandler.Compare(a, b);

        Assert.Contains(result.Routines, e => e.Name == "MainProgram/AlarmRoutine" && e.Change == "changed");
    }

    [Fact]
    public void RungAdded_IsAChange()
    {
        var a = ProjectWithRoutine(AlarmFile);
        var b = ProjectWithRoutine(AlarmFile.Replace("</RLLContent>",
            "<Rung Number=\"1\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent>"));

        Assert.Contains(DiffProjectsHandler.Compare(a, b).Routines, e => e.Change == "changed");
    }

    private static readonly string[] ExpectedNormalized =
        { "productDefinedTypes", "tagData", "opcUaAccess", "whitespace", "xmlComments" };

    [Fact]
    public void NormalizedListReported()
    {
        var a = Project(Type("U", "User"));
        var result = DiffProjectsHandler.Compare(a, a);

        Assert.Equal(ExpectedNormalized, result.Normalized);
        Assert.Contains("\"normalized\":[\"productDefinedTypes\",\"tagData\",\"opcUaAccess\",\"whitespace\",\"xmlComments\"]",
            System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public void NormalizeFalse_ComparesRawAndReportsNothingNormalized()
    {
        var a = Project(Type("U", "User"));
        var b = Project(Type("U", "User") + Type("PD_Added", "ProductDefined"));

        var raw = DiffProjectsHandler.Compare(a, b, normalize: false);

        Assert.Empty(raw.Normalized);
        Assert.Contains(raw.DataTypes, e => e.Name == "PD_Added" && e.Change == "added");
        Assert.Empty(DiffProjectsHandler.Compare(a, b).DataTypes);
    }
}
