#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// <c>tags[]</c> declarations are checked against what the controller image already has.
/// A same-type tag is reused and reported in <c>reusedTags</c>; a different type is <c>TagTypeConflict</c> with nothing
/// staged; a type that does not exist is <c>UnknownDataType</c> at stage.
/// </summary>
public class TagDeclarationChecksTests
{
    private const string XPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static string Fixture() =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "real-shape", "RungDiagram.L5X");

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)> OpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static LogixSdkTools ToolsWithProject()
    {
        var project = new ProjectService();
        project.Load(Fixture());
        return new LogixSdkTools(project, new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());
    }

    private static Task<string> ProposeAsync(LogixSdkTools tools, LogixProjectSession session, params TagInput[] tags) =>
        tools.GetProposeRungEditResponseAsync(
            XPath, 0, 0, null, session: session, target: "workstation",
            rungs: new[] { new RungInput("XIC(Cmd_Start)OTE(Fill_Valve);") }, tags: tags);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── End to end through the tool ──────────────────────────────────────────

    [Fact]
    public async Task SameTypeTag_IsReused_AndListedInReusedTags()
    {
        var (session, _) = await OpenSessionAsync();

        var root = Parse(await ProposeAsync(ToolsWithProject(), session, new TagInput("Cmd_Start", "BOOL")));

        Assert.False(root.TryGetProperty("error", out var err), err.ToString());
        var reused = root.GetProperty("reusedTags").EnumerateArray().Single();
        Assert.Equal("Cmd_Start", reused.GetProperty("name").GetString());
        Assert.Equal("BOOL", reused.GetProperty("dataType").GetString());
        Assert.Contains("already exists", reused.GetProperty("note").GetString());
    }

    [Fact]
    public async Task ConflictingType_ReturnsTagTypeConflict_AndStagesNothing()
    {
        var (session, fake) = await OpenSessionAsync();

        var root = Parse(await ProposeAsync(ToolsWithProject(), session, new TagInput("Cmd_Start", "DINT")));

        Assert.Equal(ErrorKinds.TagTypeConflict, root.GetProperty("kind").GetString());
        Assert.Equal("Cmd_Start", root.GetProperty("tag").GetString());
        Assert.Equal("BOOL", root.GetProperty("existingType").GetString());
        Assert.Equal("DINT", root.GetProperty("declaredType").GetString());
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(session.StagedTagBuffer);
        Assert.False(session.HasStagedRungEdit);
    }

    [Fact]
    public async Task UnknownType_ReturnsUnknownDataType_AtStage()
    {
        var (session, fake) = await OpenSessionAsync();

        var root = Parse(await ProposeAsync(ToolsWithProject(), session, new TagInput("Brand_New", "NoSuchType")));

        Assert.Equal(ErrorKinds.UnknownDataType, root.GetProperty("kind").GetString());
        Assert.Equal("Brand_New", root.GetProperty("tag").GetString());
        Assert.Equal("NoSuchType", root.GetProperty("dataType").GetString());
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(session.StagedTagBuffer);
    }

    [Fact]
    public async Task UserDefinedTypeTheProjectDefines_IsNotUnknown()
    {
        var (session, _) = await OpenSessionAsync();

        var root = Parse(await ProposeAsync(ToolsWithProject(), session, new TagInput("Brand_New", "Seq_Type")));

        if (root.TryGetProperty("kind", out var kind))
            Assert.NotEqual(ErrorKinds.UnknownDataType, kind.GetString());
    }

    // ── The checks themselves ────────────────────────────────────────────────

    private static TagDeclarationCensus Census(params (string Name, string Type)[] tags) =>
        new(tags.ToDictionary(t => t.Name, t => t.Type, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Seq_Type" });

    [Fact]
    public async Task Compare_NameMatchIsCaseInsensitive_AndDimensionsMustMatch()
    {
        var (session, _) = await OpenSessionAsync();
        var census = Census(("Buf", "DINT[4]"));

        var (sameConflict, sameReused) = TagDeclarationChecks.Compare(
            new[] { new TagInput("buf", "DINT", Dimensions: "4") }, census, session);
        Assert.Null(sameConflict);
        Assert.Single(sameReused!);

        var (conflict, _) = TagDeclarationChecks.Compare(
            new[] { new TagInput("Buf", "DINT", Dimensions: "8") }, census, session);
        Assert.Equal(ErrorKinds.TagTypeConflict, conflict!.Kind);
    }

    [Fact]
    public async Task Compare_NoCensus_AndNewName_ReuseNothing()
    {
        var (session, _) = await OpenSessionAsync();

        var (conflict, reused) = TagDeclarationChecks.Compare(
            new[] { new TagInput("Fresh", "BOOL") }, Census(("Other", "DINT")), session);

        Assert.Null(conflict);
        Assert.Null(reused);
        Assert.Null(TagDeclarationChecks.UnknownType(new[] { new TagInput("Fresh", "Whatever") }, null));
    }

    [Fact]
    public void UnknownType_AcceptsAtomicAndDefinedTypes_RefusesTheRest()
    {
        var census = Census();

        Assert.Null(TagDeclarationChecks.UnknownType(
            new[] { new TagInput("A", "DINT"), new TagInput("B", "TIMER"), new TagInput("C", "Seq_Type") }, census));
        var err = TagDeclarationChecks.UnknownType(
            new[] { new TagInput("A", "DINT"), new TagInput("D", "Nope[3]") }, census);
        Assert.Equal(ErrorKinds.UnknownDataType, err!.Kind);
        Assert.Equal("D", err.Tag);
    }
}
#endif
