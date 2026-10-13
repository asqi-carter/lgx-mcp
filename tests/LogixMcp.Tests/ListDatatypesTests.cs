using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="ListDatatypesHandler"/> and the
/// <c>logix_list_datatypes</c> MCP tool wired in <see cref="LogixProjectTools"/>.
/// </summary>
public class ListDatatypesTests
{
    // Demo.L5X — standard fixture used across most tests (has MotorCmd User type only)
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // DemoDataTypes.L5X — extended fixture with User, Predefined, and IO class types
    private static readonly string MultiClassFixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoDataTypes.L5X");

    // ------------------------------------------------------------------ //
    //  Error path                                                          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsErrorWhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = ListDatatypesHandler.Execute(svc);

        Assert.IsType<ListDatatypesError>(result);
        var err = (ListDatatypesError)result;
        Assert.Contains("logix_open_project", err.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_ErrorSerializesToJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = ListDatatypesHandler.Execute(svc);
        var json   = JsonSerializer.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    // ------------------------------------------------------------------ //
    //  Basic success path                                                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsResultWhenProjectLoaded()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = ListDatatypesHandler.Execute(svc);

        Assert.IsType<ListDatatypesResult>(result);
    }

    [Fact]
    public void Execute_ReturnsUserTypesFromDemo()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);

        // Demo.L5X has exactly one user-defined DataType: MotorCmd
        Assert.Single(result.DataTypes);
        Assert.Equal("MotorCmd", result.DataTypes[0].Name);
    }

    [Fact]
    public void Execute_ResultHasSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);

        Assert.Equal("project-file", result.Source);
    }

    [Fact]
    public void Execute_SerializesWithSourceProjectFile()
    {
        var svc  = new ProjectService();
        svc.Load(FixturePath);
        var json = JsonSerializer.Serialize(ListDatatypesHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("project-file", doc.RootElement.GetProperty("source").GetString());
    }

    // ------------------------------------------------------------------ //
    //  Contractual: UserTypes_ReturnOrderedMembersWithDimensionAndRadix    //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Contractual acceptance test: user-defined types must include an ordered member
    /// list with name, dataType, dimension, radix, hidden, and externalAccess fields.
    /// </summary>
    [Fact]
    public void UserTypes_ReturnOrderedMembersWithDimensionAndRadix()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);

        // MotorCmd is a User type and must carry its member list
        var motorCmd = result.DataTypes.Single(dt => dt.Name == "MotorCmd");

        Assert.NotNull(motorCmd.Members);
        Assert.Equal(2, motorCmd.Members!.Count);

        // First member: Enable (BOOL)
        var enable = motorCmd.Members[0];
        Assert.Equal("Enable",    enable.Name);
        Assert.Equal("BOOL",      enable.DataType);
        Assert.NotNull(enable.Dimension);      // dimension field present
        Assert.NotNull(enable.Radix);          // radix field present
        Assert.NotNull(enable.ExternalAccess); // externalAccess field present

        // Second member: Speed (REAL)
        var speed = motorCmd.Members[1];
        Assert.Equal("Speed", speed.Name);
        Assert.Equal("REAL",  speed.DataType);
        Assert.NotNull(speed.Dimension);
        Assert.NotNull(speed.Radix);
        Assert.NotNull(speed.ExternalAccess);
    }

    [Fact]
    public void UserTypes_MemberCountMatchesMembersListLength()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);

        foreach (var dt in result.DataTypes)
        {
            if (dt.Members is not null)
                Assert.Equal(dt.MemberCount, dt.Members.Count);
        }
    }

    [Fact]
    public void UserTypes_MembersAreInDocumentOrder()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result  = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);
        var members = result.DataTypes.Single(dt => dt.Name == "MotorCmd").Members!;

        // Demo.L5X declares Enable before Speed
        Assert.Equal("Enable", members[0].Name);
        Assert.Equal("Speed",  members[1].Name);
    }

    [Fact]
    public void UserTypes_MemberJsonHasAllRequiredKeys()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json  = JsonSerializer.Serialize(ListDatatypesHandler.Execute(svc));
        var doc   = JsonDocument.Parse(json);
        var types = doc.RootElement.GetProperty("dataTypes");
        Assert.True(types.GetArrayLength() > 0);

        var members = types[0].GetProperty("members");
        Assert.True(members.GetArrayLength() > 0);
        var m = members[0];

        Assert.True(m.TryGetProperty("name",           out _), "member must have 'name'");
        Assert.True(m.TryGetProperty("dataType",       out _), "member must have 'dataType'");
        Assert.True(m.TryGetProperty("dimension",      out _), "member must have 'dimension'");
        Assert.True(m.TryGetProperty("radix",          out _), "member must have 'radix'");
        Assert.True(m.TryGetProperty("hidden",         out _), "member must have 'hidden'");
        Assert.True(m.TryGetProperty("externalAccess", out _), "member must have 'externalAccess'");
    }

    // ------------------------------------------------------------------ //
    //  Contractual: NameContains_FiltersCaseInsensitively                  //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Contractual acceptance test: nameContains filter must be case-insensitive.
    /// </summary>
    [Fact]
    public void NameContains_FiltersCaseInsensitively()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        // Lower-case search for "motorcmd"
        var resultLower = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: "motorcmd");
        Assert.Single(resultLower.DataTypes);
        Assert.Equal("MotorCmd", resultLower.DataTypes[0].Name);

        // Upper-case search for "MOTORCMD"
        var resultUpper = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: "MOTORCMD");
        Assert.Single(resultUpper.DataTypes);
        Assert.Equal("MotorCmd", resultUpper.DataTypes[0].Name);

        // Mixed-case partial search for "motor"
        var resultMixed = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: "Motor");
        Assert.Single(resultMixed.DataTypes);
        Assert.Equal("MotorCmd", resultMixed.DataTypes[0].Name);
    }

    [Fact]
    public void NameContains_ReturnsEmptyWhenNoMatch()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: "NoSuchType");

        Assert.Empty(result.DataTypes);
    }

    [Fact]
    public void NameContains_NullOrEmptyReturnsAll()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var withNull  = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: null);
        var withEmpty = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, nameContains: "");

        Assert.Equal(withNull.DataTypes.Count, withEmpty.DataTypes.Count);
        Assert.Single(withNull.DataTypes);
    }

    // ------------------------------------------------------------------ //
    //  userOnly filter                                                     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void UserOnly_True_ReturnsOnlyUserClassTypes()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, userOnly: true);

        // DemoDataTypes.L5X has 2 User types, 1 Predefined, 1 IO
        Assert.Equal(2, result.DataTypes.Count);
        Assert.All(result.DataTypes, dt => Assert.Equal("User", dt.Class));
    }

    [Fact]
    public void UserOnly_True_ExcludesPredefinedAndIOClassTypes()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, userOnly: true);

        var names = result.DataTypes.Select(dt => dt.Name).ToHashSet();
        Assert.DoesNotContain("SomePredefined", names);
        Assert.DoesNotContain("SomeIO",         names);
    }

    [Fact]
    public void UserOnly_False_ReturnsAllClassTypes()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        var result = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, userOnly: false);

        // DemoDataTypes.L5X has 4 total types
        Assert.Equal(4, result.DataTypes.Count);
    }

    [Fact]
    public void UserOnly_Default_IsTrue()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        // Default call (userOnly omitted) must behave like userOnly=true
        var resultDefault  = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);
        var resultExplicit = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, userOnly: true);

        Assert.Equal(resultDefault.DataTypes.Count, resultExplicit.DataTypes.Count);
    }

    // ------------------------------------------------------------------ //
    //  Non-User type members                                               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void NonUserTypes_HaveNullMembersList()
    {
        var svc = new ProjectService();
        svc.Load(MultiClassFixturePath);

        var result      = (ListDatatypesResult)ListDatatypesHandler.Execute(svc, userOnly: false);
        var predefined  = result.DataTypes.Single(dt => dt.Name == "SomePredefined");
        var io          = result.DataTypes.Single(dt => dt.Name == "SomeIO");

        Assert.Null(predefined.Members);
        Assert.Null(io.Members);
    }

    // ------------------------------------------------------------------ //
    //  DataType fields                                                     //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DataTypeEntry_HasNameClassFamilyMemberCount()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result   = (ListDatatypesResult)ListDatatypesHandler.Execute(svc);
        var motorCmd = result.DataTypes.Single(dt => dt.Name == "MotorCmd");

        Assert.Equal("MotorCmd", motorCmd.Name);
        Assert.Equal("User",     motorCmd.Class);
        Assert.NotEmpty(motorCmd.Family);
        Assert.Equal(2, motorCmd.MemberCount);
    }

    [Fact]
    public void DataTypeEntry_JsonHasAllRequiredKeys()
    {
        var svc  = new ProjectService();
        svc.Load(FixturePath);
        var json = JsonSerializer.Serialize(ListDatatypesHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);
        var dt   = doc.RootElement.GetProperty("dataTypes")[0];

        Assert.True(dt.TryGetProperty("name",        out _), "dataType must have 'name'");
        Assert.True(dt.TryGetProperty("class",       out _), "dataType must have 'class'");
        Assert.True(dt.TryGetProperty("family",      out _), "dataType must have 'family'");
        Assert.True(dt.TryGetProperty("memberCount", out _), "dataType must have 'memberCount'");
        Assert.True(dt.TryGetProperty("members",     out _), "dataType must have 'members'");
    }

    // ------------------------------------------------------------------ //
    //  Operates without Studio open (offline / project-file only)          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ToolOperatesWithoutStudioOpen_ReadsFromProjectFileOnly()
    {
        // This test verifies that the tool reads entirely from the already-loaded L5X
        // (ProjectService) and does not attempt any controller or Studio connection.
        // If it depended on Studio, it would throw or return a connection error here.
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var result = ListDatatypesHandler.Execute(svc);

        // Must return a result, not an error, purely from in-memory L5X data
        Assert.IsType<ListDatatypesResult>(result);
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools integration                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_ListDatatypes_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetListDatatypesResponse();

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_ListDatatypes_ReturnsDataTypesAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetListDatatypesResponse();
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("dataTypes").ValueKind);
        Assert.Equal(1, doc.RootElement.GetProperty("dataTypes").GetArrayLength());
    }

    [Fact]
    public void LogixProjectTools_ListDatatypes_WithNameContainsFilter()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetListDatatypesResponse(nameContains: "motor");
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("dataTypes").GetArrayLength());
        Assert.Equal("MotorCmd",
            doc.RootElement.GetProperty("dataTypes")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void LogixProjectTools_ListDatatypes_WithUserOnlyFalse()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(MultiClassFixturePath);

        var json = tools.GetListDatatypesResponse(userOnly: false);
        var doc  = JsonDocument.Parse(json);

        // 4 types total in DemoDataTypes.L5X when userOnly=false
        Assert.Equal(4, doc.RootElement.GetProperty("dataTypes").GetArrayLength());
    }
}
