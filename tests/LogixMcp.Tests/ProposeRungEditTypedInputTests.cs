#if LOGIX_SDK
using System.Text.Json;
using RockwellAutomation.LogixDesigner;

using PartialImportOption = RockwellAutomation.LogixDesigner.LogixProject.PartialImportOption;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the typed <c>rungs</c>/<c>tags</c> input of
/// <c>logix_propose_rung_edit</c>:
/// mutual exclusion with <c>l5xFragment</c>, rung-text validation, tag validation
/// and the tag-only publish rule. All refusals must happen before any SDK call.
/// </summary>
public class ProposeRungEditTypedInputTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static async Task<(LogixProjectSession Session, ProposeRungEditRecordingFake Fake)>
        BuildOpenSessionAsync()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        return (session, fake);
    }

    private static LogixSdkTools NewTools() =>
        new(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto), new StudioConfirmWait());

    private static void AssertNoSdkIo(ProposeRungEditRecordingFake fake)
    {
        Assert.Empty(fake.CapturedImportOptions);
        Assert.Empty(fake.CapturedXPaths);
    }

    private static string Kind(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString()!;

    private static string Error(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task BothTypedAndRaw_AmbiguousInput_NoIo()
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, "<RSLogix5000Content/>",
            session: session, target: "workstation",
            rungs: new[] { new RungInput("XIC(A)OTE(B);") });

        Assert.Equal("AmbiguousInput", Kind(json));
        AssertNoSdkIo(fake);

        // tags + raw fragment is ambiguous too, on any target (including offline).
        json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, "<RSLogix5000Content/>",
            session: session, target: "controller", confirmedByUser: true,
            tags: new[] { new TagInput("Lamp", "BOOL") });
        Assert.Equal("AmbiguousInput", Kind(json));

        json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, "<RSLogix5000Content/>",
            offline: true, rungs: new[] { new RungInput("XIC(A)OTE(B);") });
        Assert.Equal("AmbiguousInput", Kind(json));
        AssertNoSdkIo(fake);
    }

    [Theory]
    [InlineData("<![CDATA[XIC(A)OTE(B);]]>")]
    [InlineData("XIC(A)OTE(B);]]>")]
    [InlineData("<Rung Number=\"0\"><Text>XIC(A);</Text></Rung>")]
    [InlineData("   ")]
    public async Task RungTextContainingXml_InvalidRungText(string text)
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "workstation",
            rungs: new[] { new RungInput("XIC(Ok)OTE(Fine);"), new RungInput(text) });

        Assert.Equal("InvalidRungText", Kind(json));
        Assert.Contains("rungs[1].text", Error(json));
        if (!string.IsNullOrWhiteSpace(text))
            Assert.Contains("not XML", Error(json));
        AssertNoSdkIo(fake);
    }

    [Theory]
    [InlineData("1Lamp", "BOOL", "tags[0].name")]
    [InlineData("My-Tag", "BOOL", "tags[0].name")]
    [InlineData("Bad__Name", "BOOL", "tags[0].name")]
    [InlineData("", "BOOL", "tags[0].name")]
    [InlineData("AAAAAAAAAABBBBBBBBBBCCCCCCCCCCDDDDDDDDDDE", "BOOL", "tags[0].name")] // 41 chars
    [InlineData("Lamp", "  ", "tags[0].dataType")]
    public async Task BadTagName_InvalidTag(string name, string dataType, string field)
    {
        var (session, fake) = await BuildOpenSessionAsync();

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "controller", confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(A)OTE(B);") },
            tags:  new[] { new TagInput(name, dataType) });

        Assert.Equal("InvalidTag", Kind(json));
        Assert.Contains(field, Error(json));
        AssertNoSdkIo(fake);
    }

    [Fact]
    public async Task Offline_TypedTagsWithRungs_RefusedTagsNotSupportedOffline()
    {
        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null, offline: true,
            rungs: new[] { new RungInput("XIC(A)OTE(Lamp);") },
            tags: new[] { new TagInput("Lamp", "BOOL") });
        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("kind", out var kind));
        Assert.Equal("TagsNotSupportedOffline", kind.GetString());
        Assert.Contains("offline", Error(json));
    }

    [Fact]
    public async Task TagsOnly_Workstation_Refused_Controller_Allowed()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var tags = new[] { new TagInput("Lamp", "BOOL") };

        // Workstation (explicit and default) and offline: refused, nothing attempted.
        foreach (var target in new string?[] { "workstation", null })
        {
            var json = await NewTools().GetProposeRungEditResponseAsync(
                ValidXPath, 0, 0, null, session: session, target: target, tags: tags);
            Assert.Contains("tags without rungs", Error(json));
        }
        var offlineJson = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null, offline: true, tags: tags);
        Assert.Contains("tags without rungs", Error(offlineJson));
        AssertNoSdkIo(fake);

        // Controller: not refused by the tag-only rule or by typed-input validation.
        var ctl = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "controller", confirmedByUser: true, tags: tags);
        var root = JsonDocument.Parse(ctl).RootElement;
        if (root.TryGetProperty("error", out var ctlError))
            Assert.DoesNotContain("tags without rungs", ctlError.GetString());
        if (root.TryGetProperty("kind", out var kind))
            Assert.DoesNotContain(kind.GetString(),
                new[] { "AmbiguousInput", "InvalidRungText", "InvalidTag" });
    }

    [Fact]
    public void ValidateTypedInput_TrimsAndAppendsMissingSemicolon()
    {
        var err = ProposeRungEditHandler.ValidateTypedInput(
            null,
            new[] { new RungInput("  XIC(A)OTE(B)  ", "c"), new RungInput("NOP();") },
            null, out var normalized);

        Assert.Null(err);
        Assert.Equal("XIC(A)OTE(B);", normalized[0].Text);
        Assert.Equal("c", normalized[0].Comment);
        Assert.Equal("NOP();", normalized[1].Text);
    }

    private static string ExportedStyleFragment(string text) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.00\" " +
        "TargetName=\"MainRoutine\" TargetType=\"Routine\" TargetSubType=\"RLL\" " +
        "ContainsContext=\"true\"><Controller Name=\"DemoController\">" +
        "<Programs><Program Name=\"MainProgram\"><Routines>" +
        "<Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
        $"<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[{text}]]></Text></Rung>" +
        "</RLLContent></Routine></Routines></Program></Programs>" +
        "</Controller></RSLogix5000Content>";

    private static IReadOnlyList<(string? Number, string? Type, string Text)> ImportedRungs(string? xml) =>
        System.Xml.Linq.XDocument.Parse(xml!).Descendants("Rung")
            .Select(r => ((string?)r.Attribute("Number"), (string?)r.Attribute("Type"),
                          r.Element("Text")!.Value))
            .ToList();

    [Fact]
    public async Task Typed_Workstation_ImportsSameRungsAsEquivalentExportedFragment()
    {
        var (typedSession, typedFake) = await BuildOpenSessionAsync();
        var typedJson = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null, session: typedSession, target: "workstation",
            rungs: new[] { new RungInput("XIC(Start)OTE(Lamp)") });

        var (rawSession, rawFake) = await BuildOpenSessionAsync();
        var rawJson = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, ExportedStyleFragment("XIC(Start)OTE(Lamp);"),
            session: rawSession, target: "workstation");

        Assert.False(JsonDocument.Parse(typedJson).RootElement.TryGetProperty("error", out _), typedJson);
        Assert.False(JsonDocument.Parse(rawJson).RootElement.TryGetProperty("error", out _), rawJson);

        Assert.Equal(rawFake.CapturedImportOptions, typedFake.CapturedImportOptions);
        Assert.Equal(rawFake.CapturedInsertPositions, typedFake.CapturedInsertPositions);
        Assert.Equal(rawFake.CapturedReplaceCounts, typedFake.CapturedReplaceCounts);
        Assert.Equal(
            ImportedRungs(rawFake.LastCapturedXmlContent),
            ImportedRungs(typedFake.LastCapturedXmlContent));
        Assert.Equal("XIC(Start)OTE(Lamp);", ImportedRungs(typedFake.LastCapturedXmlContent).Single().Text);
    }

    /// <summary>
    /// Direct publish (target="controller", no staged buffer) with typed rungs and tags: the
    /// fragment sent to the SDK carries every passed tag in Controller &gt; Tags Use="Context"
    /// next to the rungs.
    /// </summary>
    [Fact]
    public async Task DirectPublish_RungsAndTags_FragmentHoldsEveryTagInControllerTagsContext()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        var passed = new[]
        {
            new TagInput("Lamp", "BOOL"),
            new TagInput("Speed", "DINT"),
            new TagInput("Level", "REAL"),
        };

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "controller", confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(Start)OTE(Lamp);") },
            tags:  passed);

        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _), json);
        Assert.NotNull(fake.LastCapturedXmlContent);
        var doc = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!);

        var context = Assert.Single(
            doc.Root!.Element("Controller")!.Elements("Tags"),
            t => (string?)t.Attribute("Use") == "Context");
        var names = context.Elements("Tag").Select(t => (string?)t.Attribute("Name")).ToList();
        foreach (var tag in passed)
            Assert.Contains(tag.Name, names);
        Assert.Equal("XIC(Start)OTE(Lamp);", doc.Descendants("Rung").Single().Element("Text")!.Value);
    }

    /// <summary>
    /// An import failure on the controller target lists the tags in the fragment that was sent
    /// as <c>tagsInFragment</c>.
    /// </summary>
    [Fact]
    public async Task DirectPublish_ImportFails_ErrorListsTagsInFragment()
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ThrowOnImport = new InvalidOperationException("import exploded");

        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: session, target: "controller", confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(Start)OTE(Lamp);") },
            tags:  new[] { new TagInput("Lamp", "BOOL"), new TagInput("Speed", "DINT") });

        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("error", out _), json);
        var listed = root.GetProperty("tagsInFragment").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new string?[] { "Lamp", "Speed" }, listed);
    }

    [Fact]
    public async Task Typed_Live_ReplaceWithoutComment_PreservesExistingComment()
    {
        var fake    = new ProposeRungEditRecordingFake();
        var session = new LogixProjectSession(
            (_path, _ct) => System.Threading.Tasks.Task.FromResult<ILogixProject>(fake));
        await session.OpenAsync("dummy.ACD");
        session.IsOnline     = true;
        session.IsCorrelated = true;
        fake.ExportContent = ExportedStyleFragment("XIC(Now)OTE(Out);").Replace(
            "<Text>", "<Comment><![CDATA[Keep me]]></Comment><Text>");

        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto,
                             LiveFinalizeEnabled: true),
            new StudioConfirmWait());
        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 1, null, session: session, target: "live", confirmedByUser: true,
            expectedRungText: new[] { "XIC(Now)OTE(Out);" },
            rungs: new[] { new RungInput("XIC(New)OTE(Out)") });

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("error", out var err), err.ToString());
        Assert.Equal(new[] { 0 },
            root.GetProperty("commentsPreserved").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        Assert.Equal(PartialImportOption.FinalizeEdits, Assert.Single(fake.CapturedImportOptions));
        var rung = System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!)
            .Descendants("Rung").Single();
        Assert.Equal("Keep me", rung.Element("Comment")?.Value);
        Assert.Equal("XIC(New)OTE(Out);", rung.Element("Text")?.Value);
    }

    // ── Comment preservation on every replace path (logix-mcp-qpqy.1) ─────────

    private static async Task<(ProposeRungEditRecordingFake Fake, string Json)> ReplaceOnCommentedRungAsync(
        string target, string? comment)
    {
        var (session, fake) = await BuildOpenSessionAsync();
        fake.ExportContent = ExportedStyleFragment("XIC(Now)OTE(Out);").Replace(
            "<Text>", "<Comment><![CDATA[Keep me]]></Comment><Text>");
        var json = await NewTools().GetProposeRungEditResponseAsync(
            ValidXPath, 0, 1, null, session: session, target: target, confirmedByUser: true,
            rungs: new[] { new RungInput("XIC(New)OTE(Out)", comment) });
        return (fake, json);
    }

    private static System.Xml.Linq.XElement ImportedRung(ProposeRungEditRecordingFake fake) =>
        System.Xml.Linq.XDocument.Parse(fake.LastCapturedXmlContent!).Descendants("Rung").Single();

    private static int[] Preserved(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("commentsPreserved")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();

    [Fact]
    public async Task Controller_Replace_WithoutComment_PreservesExistingComment()
    {
        var (fake, json) = await ReplaceOnCommentedRungAsync("controller", null);

        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("error", out var err), err.ToString());
        Assert.Equal(new[] { 0 }, Preserved(json));
        Assert.Equal(PartialImportOption.AcceptEdits, Assert.Single(fake.CapturedImportOptions));
        Assert.Equal("Keep me", ImportedRung(fake).Element("Comment")?.Value);
    }

    [Fact]
    public async Task Workstation_Replace_WithoutComment_PreservesExistingComment()
    {
        var (fake, json) = await ReplaceOnCommentedRungAsync("workstation", null);

        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("error", out var err), err.ToString());
        Assert.Equal(new[] { 0 }, Preserved(json));
        Assert.Equal(PartialImportOption.LeaveEdits, Assert.Single(fake.CapturedImportOptions));
        Assert.Equal("Keep me", ImportedRung(fake).Element("Comment")?.Value);
    }

    [Theory]
    [InlineData("workstation")]
    public async Task Replace_EmptyComment_ClearsComment(string target)
    {
        var (fake, json) = await ReplaceOnCommentedRungAsync(target, "");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("error", out var err), err.ToString());
        Assert.False(root.TryGetProperty("commentsPreserved", out _));
        Assert.Null(ImportedRung(fake).Element("Comment"));
    }

    [Theory]
    [InlineData("workstation")]
    public async Task Replace_ExplicitComment_Wins(string target)
    {
        var (fake, json) = await ReplaceOnCommentedRungAsync(target, "New one");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("error", out var err), err.ToString());
        Assert.False(root.TryGetProperty("commentsPreserved", out _));
        Assert.Equal("New one", ImportedRung(fake).Element("Comment")?.Value);
    }

    // An accepted controller edit cannot be cancelled with the original comment written back,
    // so an explicit comment that changes the replaced rung's comment is refused on the controller path.
    [Theory]
    [InlineData("")]
    [InlineData("New one")]
    public async Task Controller_Replace_ExplicitComment_IsRefusedAsCommentNotReversible(string comment)
    {
        var (fake, json) = await ReplaceOnCommentedRungAsync("controller", comment);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CommentNotReversible", root.GetProperty("kind").GetString());
        Assert.Equal("omit comment to keep the original, or set it after the edit is finalized",
            root.GetProperty("hint").GetString());
        Assert.Empty(fake.CapturedImportOptions);
    }
}
#endif
