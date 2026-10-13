using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// After accept, assemble or cancel the session image either follows the change this session
/// published or says it is stale (<c>imageStale</c>, <c>staleSince</c>, <c>staleReason</c>, reconnect hint), and a
/// reconnect clears the mark.
/// </summary>
[Collection("StaticHandlerHooks")]
public class SessionImageAfterAcceptTests
{
    private const string Tag = "<Tag Name=\"{0}\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
                               "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    private static readonly string[] Tags = ["A", "C", "B", "D", "E"];

    internal static L5X ImageOf(params string[] rungs) => Image(rungs);

    internal static L5X ImageWithComments(params (string Text, string? Comment)[] rungs) => BuildImage(rungs);

    private static L5X Image(params string[] rungs) => BuildImage(rungs.Select(r => (r, (string?)null)).ToArray());

    private static L5X BuildImage((string Text, string? Comment)[] rungs)
    {
        var tagXml  = string.Concat(Tags.Select(t => string.Format(Tag, t)));
        var rungXml = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\">" +
            (r.Comment is null ? "" : $"<Comment><![CDATA[{r.Comment}]]></Comment>") +
            $"<Text><![CDATA[{r.Text}]]></Text></Rung>"));
        return L5X.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions />" +
            $"<Tags>{tagXml}</Tags>" +
            "<Programs><Program Name=\"MainProgram\" Type=\"Normal\" TestEdits=\"false\" Disabled=\"false\" UseAsFolder=\"false\" MainRoutineName=\"Main\">" +
            "<Tags /><Routines><Routine Name=\"Main\" Type=\"RLL\"><RLLContent>" + rungXml +
            "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
    }

    /// <summary>The connected image: rungs 0 and 1 both drive B (a DuplicateDestructiveOutput).</summary>
    private static L5X Connected() => Image("XIC(A)OTE(B);", "XIC(C)OTE(B);", "XIC(A)OTE(D);");

    private static readonly string[] Published =
        ["XIC(A)XIC(C)OTE(B);", "XIC(D)OTE(E);", "XIC(E)OTE(C);"];   // replace(0,2) + one insert

    internal static (TagSessionService Session, ProjectService Service, L5X Image) Connect(L5X? image = null)
    {
        image ??= Connected();
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");   // as logix_connect does
        return (session, service, image);
    }

    internal static int DuplicateOutputs(TagSessionService session, ProjectService service) =>
        Assert.IsType<LintResult>(LintHandler.Execute(service, session: session))
            .Findings.Count(f => f.Rule == "DuplicateDestructiveOutput");

    private static string DocumentMarkdown(TagSessionService session, ProjectService service) =>
        Assert.IsType<DocumentResult>(DocumentHandler.Execute(service, session: session)).Markdown;

    private static JsonElement LintJson(TagSessionService session, ProjectService service) =>
        JsonDocument.Parse(LintHandler.Serialize(LintHandler.Execute(service, session: session))).RootElement;

    private static JsonElement DocumentJson(TagSessionService session, ProjectService service) =>
        JsonDocument.Parse(DocumentHandler.Serialize(DocumentHandler.Execute(service, session: session))).RootElement;

    private static IReadOnlyList<SessionImageState.ZoneRoutine> Zone(params string[] newRungs) =>
        [new SessionImageState.ZoneRoutine("Main", newRungs.Select((t, i) => (i < 2 ? "IR" : "I", t)).ToList())];

    private static void AssertStale(JsonElement root)
    {
        Assert.True(root.GetProperty("imageStale").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("staleSince").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("staleReason").GetString()));
        Assert.Contains("reconnect (logix_connect) to refresh before relying on this result", root.GetProperty("hint").GetString());
        Assert.Equal("session-image", root.GetProperty("source").GetString());
    }

    [Fact]
    public void Accept_OfOwnPublishedReplaceAndInsert_LintNoLongerReportsDuplicateAndDocumentShowsNewRung()
    {
        var (session, service, _) = Connect();
        Assert.True(DuplicateOutputs(session, service) > 0);
        Assert.DoesNotContain("XIC(D)OTE(E);", DocumentMarkdown(session, service), StringComparison.Ordinal);

        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", insertPosition: 0, replaceCount: 2, Published));
        SessionImageState.OnAccepted(session, "MainProgram", Zone(Published), "accepted");

        Assert.Equal(0, DuplicateOutputs(session, service));
        var markdown = DocumentMarkdown(session, service);
        Assert.Contains("XIC(D)OTE(E);", markdown, StringComparison.Ordinal);
        Assert.Contains("XIC(A)XIC(C)OTE(B);", markdown, StringComparison.Ordinal);

        // 3 new rungs + the untouched third original, numbered 0..3, all ordinary rungs; the new block is at position 0.
        var rungs = session.ControllerImage!.Programs.Single().Routines.Single().Rungs.ToList();
        Assert.Equal(4, rungs.Count);
        Assert.Equal([0, 1, 2, 3], rungs.Select(r => r.Number).ToArray());
        Assert.All(rungs, r => Assert.Equal("N", r.Type!.ToString()));
        Assert.Equal(Published, rungs.Take(3).Select(r => r.Text.ToString()).ToArray());
        Assert.Equal("XIC(A)OTE(D);", rungs[3].Text.ToString());

        // The image is current, so nothing says it is stale.
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(LintJson(session, service).TryGetProperty("imageStale", out _));
        Assert.False(DocumentJson(session, service).TryGetProperty("imageStale", out _));
    }

    [Fact]
    public void RecordPublish_ReplaceOfOne_PutsTheNewBlockAtTheInsertPositionAndTheOriginalFollowsAsReplaced()
    {
        var (session, _, _) = Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 1, ["XIC(A)OTE(E);"]));

        var rungs = session.ControllerImage!.Programs.Single().Routines.Single().Rungs.ToList();
        Assert.Equal(4, rungs.Count);
        Assert.Equal(["IR", "R", "N", "N"], rungs.Select(r => r.Type!.ToString()).ToArray());
        Assert.Equal("XIC(A)OTE(E);", rungs[0].Text.ToString());
        Assert.Equal("XIC(A)OTE(B);", rungs[1].Text.ToString());
    }

    [Fact]
    public void Accept_OfOwnPublish_WithoutPendingMergeAtPublish_AppliesTheSameChangeSet()
    {
        var (session, service, _) = Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published, applyToImage: false));

        SessionImageState.OnAccepted(session, "MainProgram", Zone(Published), "assembled");

        Assert.Equal(0, DuplicateOutputs(session, service));
        Assert.Contains("XIC(D)OTE(E);", DocumentMarkdown(session, service), StringComparison.Ordinal);
        Assert.Null(SessionImageState.StalenessOf(session));
    }

    [Fact]
    public void Accept_WithUnknownChangeSet_LintDocumentAndReadsReportImageStale()
    {
        var (session, service, _) = Connect();

        // Nothing was published by this session: the zone came from somewhere else.
        SessionImageState.OnAccepted(session, "MainProgram", Zone("XIC(A)OTE(E);"), "accepted");

        AssertStale(LintJson(session, service));
        AssertStale(DocumentJson(session, service));

        // Reads that use the session image carry the same hint in their note.
        var xref = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: true));
        Assert.Equal("session-image", xref.Source);
        Assert.Contains("reconnect (logix_connect) to refresh before relying on this result", xref.Note);
        Assert.Contains("stale", xref.Note);
    }

    [Fact]
    public void Accept_WhenZoneDiffersFromWhatThisSessionPublished_MarksImageStale()
    {
        var (session, service, _) = Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published));
        var before = session.ControllerImage!.ToString();

        // Someone else added a rung to the zone, so the record is not the whole zone.
        SessionImageState.OnAccepted(session, "MainProgram", Zone([.. Published, "XIC(A)OTE(E);"]), "accepted");

        AssertStale(LintJson(session, service));
        Assert.Equal(before, session.ControllerImage!.ToString());   // nothing half-applied
    }

    [Fact]
    public void Accept_WhenTheReplaceRangeIsOutsideTheImage_MarksImageStale()
    {
        var (session, service, _) = Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published, applyToImage: false));
        Assert.False(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 9, ["XIC(A)OTE(E);"]));   // not recorded
        SessionImageState.OnAccepted(session, "MainProgram", Zone(Published), "accepted");
        Assert.Null(SessionImageState.StalenessOf(session));

        // A record whose range does not fit the image (image shrank under it) is a merge failure: stale.
        var (s2, svc2, _) = Connect(Image("XIC(A)OTE(B);"));
        Assert.True(SessionImageState.RecordPublish(s2, "MainProgram", "Main", 0, 1, ["XIC(A)OTE(E);"], applyToImage: false));
        s2.SetControllerImage(Image(), s2.ControllerImageTakenAt!.Value);   // same image slot, now without the rung
        SessionImageState.OnAccepted(s2, "MainProgram", [new("Main", [("IR", "XIC(A)OTE(E);")])], "accepted");
        var stale = SessionImageState.StalenessOf(s2);
        Assert.NotNull(stale);
        Assert.Contains("outside the session image", stale!.Reason);
        _ = svc2;
    }

    [Fact]
    public void Reconnect_ClearsTheStaleFlag()
    {
        var (session, service, _) = Connect();
        SessionImageState.OnAccepted(session, "MainProgram", Zone("XIC(A)OTE(E);"), "accepted");
        Assert.NotNull(SessionImageState.StalenessOf(session));

        // logix_connect again: a new session service and a freshly taken image.
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(Connected(), DateTimeOffset.UtcNow);

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(LintJson(session, service).TryGetProperty("imageStale", out _));
        Assert.False(DocumentJson(session, service).TryGetProperty("imageStale", out _));
    }

    [Fact]
    public void Cancel_LeavesTheImageEqualToItsStateBeforeThePublish()
    {
        var (session, service, _) = Connect();
        var before = session.ControllerImage!.ToString();

        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published));
        Assert.NotEqual(before, session.ControllerImage!.ToString());   // the publish changed the image
        Assert.Contains("XIC(D)OTE(E);", DocumentMarkdown(session, service), StringComparison.Ordinal);

        SessionImageState.OnCancelled(session, "MainProgram", Zone(Published));

        Assert.Equal(before, session.ControllerImage!.ToString());
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.True(DuplicateOutputs(session, service) > 0);
        Assert.DoesNotContain("XIC(D)OTE(E);", DocumentMarkdown(session, service), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_WhenTheImageWasNotChangedForThePublish_MarksImageStale()
    {
        var (session, service, _) = Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published, applyToImage: false));

        SessionImageState.OnCancelled(session, "MainProgram", Zone(Published));
        AssertStale(LintJson(session, service));
    }

    [Fact]
    public void Cancel_OfAZoneThisSessionDidNotPublish_MarksImageStale()
    {
        var (session, service, _) = Connect();
        SessionImageState.OnCancelled(session, "MainProgram", Zone("XIC(A)OTE(E);"));
        AssertStale(LintJson(session, service));
    }

    [Fact]
    public void StaleMark_IsFirstWins_AndOnlyOnSessionImageResponses()
    {
        var (session, _, _) = Connect();
        SessionImageState.MarkStale(session, "first");
        var since = SessionImageState.StalenessOf(session)!.Since;
        SessionImageState.MarkStale(session, "second");

        var stale = SessionImageState.StalenessOf(session)!;
        Assert.Equal("first", stale.Reason);
        Assert.Equal(since, stale.Since);
        Assert.NotNull(ProjectSourceLabel.Staleness(session, ProjectSourceLabel.SessionImage));
        Assert.Null(ProjectSourceLabel.Staleness(session, ProjectSourceLabel.ProjectFile));
        Assert.Null(ProjectSourceLabel.Staleness(null, ProjectSourceLabel.SessionImage));
    }
}

#if LOGIX_SDK
/// <summary>The accept / assemble / cancel handlers hand the zone they resolved to the session image.</summary>
[Collection("StaticHandlerHooks")]
public class SessionImageAfterLifecycleHandlerTests
{
    private static readonly string[] Published = ["XIC(A)XIC(C)OTE(B);", "XIC(D)OTE(E);", "XIC(E)OTE(C);"];

    /// <summary>The first two reads (routine, program) see the zone; once the gesture ran it is gone.</summary>
    private static Func<string, string, CancellationToken, Task<object>> Reader(string[] rungs)
    {
        var reads = 0;
        return (p, r, ct) =>
        {
            var zone = reads++ < 2;
            var entries = zone
                ? rungs.Select((t, i) => new PendingEditRungEntry(i, i < 2 ? "IR" : "I", t)).ToArray()
                : Array.Empty<PendingEditRungEntry>();
            return Task.FromResult<object>(new PendingEditsResult(
                true, "Run", null, [new RoutinePendingEditState("MainProgram", "Main", 3, entries, zone ? "accepted" : "none")],
                new StagedEditsState(0, []), DateTimeOffset.UtcNow));
        };
    }

    private static Task<Session1ScriptResult> Runner(string verb, CancellationToken ct) =>
        Task.FromResult(new Session1ScriptResult(true, "ok", ["shot.png"]));

    [Theory]
    [InlineData("accept")]
    [InlineData("assemble")]
    public async Task OwnPublishedZone_IsAppliedToTheImage(string verb)
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 2, Published));

        var result = verb == "accept"
            ? await AcceptPendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
                default, () => ("direct", null), session)
            : await AssemblePendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
                default, () => ("direct", null), session);

        Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
    }

    [Fact]
    public async Task ZoneNotPublishedByThisSession_MarksTheImageStale()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();

        var result = await AcceptPendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
            default, () => ("direct", null), session);

        Assert.IsType<CancelPendingEditsResult>(result);
        Assert.NotNull(SessionImageState.StalenessOf(session));
        Assert.True(SessionImageAfterAcceptTests.DuplicateOutputs(session, service) > 0);   // image untouched
    }

    [Fact]
    public async Task NoImageSession_LeavesTheImageAlone()
    {
        var (session, _, image) = SessionImageAfterAcceptTests.Connect();
        var before = image.ToString();

        await AcceptPendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
            default, () => ("direct", null));

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(before, session.ControllerImage!.ToString());
    }
}

/// <summary>
/// The real tool entry points (<see cref="LogixSdkTools"/>) hand the session image to accept, assemble and cancel,
/// and a publish result is recorded for them. The Studio driver and the SDK project are fakes.
/// </summary>
[Collection("StaticHandlerHooks")]
public class SessionImageThroughToolsTests
{
    private const string RoutineXPath = "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='Main']";
    private static readonly string[] Published = ["XIC(A)XIC(C)OTE(B);", "XIC(D)OTE(E);", "XIC(E)OTE(C);"];
    private static readonly string[] Originals = ["XIC(A)OTE(B);", "XIC(C)OTE(B);", "XIC(A)OTE(D);"];

    /// <summary>
    /// A routine export. Pending: the new <paramref name="rungs"/> sit at position <paramref name="insert"/>, the
    /// <paramref name="replace"/> replaced originals follow them (as the controller lands them), then the other originals.
    /// </summary>
    private static string Export(bool pending, string[] rungs, int insert = 0, int replace = 2)
    {
        var sb = new System.Text.StringBuilder();
        var n = 0;
        if (pending)
        {
            foreach (var o in Originals.Take(insert)) sb.Append($"<Rung Number=\"{n++}\" Type=\"N\"><Text><![CDATA[{o}]]></Text></Rung>");
            for (var i = 0; i < rungs.Length; i++)
                sb.Append($"<Rung Number=\"{n++}\" Type=\"{(i < replace ? "IR" : "I")}\"><Text><![CDATA[{rungs[i]}]]></Text></Rung>");
            foreach (var o in Originals.Skip(insert).Take(replace)) sb.Append($"<Rung Number=\"{n++}\" Type=\"R\"><Text><![CDATA[{o}]]></Text></Rung>");
            foreach (var o in Originals.Skip(insert + replace)) sb.Append($"<Rung Number=\"{n++}\" Type=\"N\"><Text><![CDATA[{o}]]></Text></Rung>");
        }
        else
        {
            foreach (var r in rungs) sb.Append($"<Rung Number=\"{n++}\" Type=\"N\"><Text><![CDATA[{r}]]></Text></Rung>");
        }
        return "<RSLogix5000Content><Controller><Programs><Program Name=\"MainProgram\"><Routines>" +
               "<Routine Name=\"Main\"><RLLContent>" + sb + "</RLLContent></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>";
    }

    /// <summary>
    /// A publish result with the real landed shape: the new rungs land AT <paramref name="insert"/> (the originals
    /// follow), so by default the landed indices start there. <paramref name="landed"/> overrides them.
    /// </summary>
    private static ProposeRungEditResult PublishResult(
        uint insert = 0, uint replace = 2, string[]? texts = null, int[]? landed = null, string?[]? comments = null)
    {
        texts  ??= Published;
        landed ??= Enumerable.Range((int)insert, texts.Length).ToArray();
        return new("proposed", RoutineXPath, insert, replace, "published",
            PublishedRoutines: [new PublishedRoutine(RoutineXPath, texts.Length, landed, (uint)landed[0], replace, texts, comments)]);
    }

    private static async Task<(LogixSdkTools Tools, TagSessionService Session, ProjectService Service, LogixProjectSession Sdk, FakePendingEditsProject Fake)> SetUp()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();
        var tools = new LogixSdkTools(service, session, new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true));
        var fake = new FakePendingEditsProject { ExportContent = Export(true, Published) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        return (tools, session, service, sdk, fake);
    }

    /// <summary>A Studio driver whose gesture resolves the zone: afterwards the routine holds <paramref name="after"/> as ordinary rungs.</summary>
    private static Func<string, CancellationToken, Task<Session1ScriptResult>> Runner(FakePendingEditsProject fake, string[] after) =>
        (verb, ct) =>
        {
            fake.ExportContent = Export(false, after);
            return Task.FromResult(new Session1ScriptResult(true, "gesture_sent", ["shot.png"]));
        };

    private static string StatusOf(string json) => JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()!;

    [Fact]
    public async Task PublishResult_IsRecorded_AndAcceptThroughTheTool_KeepsTheImageCurrent()
    {
        var (tools, session, service, sdk, fake) = await SetUp();
        tools.RecordPendingPublish(PublishResult());
        Assert.Contains("XIC(D)OTE(E);", session.ControllerImage!.ToString(), StringComparison.Ordinal);   // merged as pending

        var json = await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Published), () => true,
            probe: new TestProbe());

        Assert.Equal("accepted", StatusOf(json));
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
    }

    [Fact]
    public async Task AssembleThroughTheTool_OfOwnPublish_KeepsTheImageCurrent()
    {
        var (tools, session, service, sdk, fake) = await SetUp();
        tools.RecordPendingPublish(PublishResult());

        var json = await tools.GetAssemblePendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Published), () => true,
            probe: new TestProbe());

        Assert.Equal("assembled", StatusOf(json));
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
    }

    [Fact]
    public async Task AcceptThroughTheTool_WithoutARecordedPublish_MarksTheImageStale()
    {
        var (tools, session, service, sdk, fake) = await SetUp();

        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Published), () => true,
            probe: new TestProbe());

        Assert.NotNull(SessionImageState.StalenessOf(session));
        var lint = JsonDocument.Parse(LintHandler.Serialize(LintHandler.Execute(service, session: session))).RootElement;
        Assert.True(lint.GetProperty("imageStale").GetBoolean());
    }

    [Fact]
    public async Task CancelThroughTheTool_RestoresTheImageToBeforeThePublish()
    {
        var (tools, session, _, sdk, fake) = await SetUp();
        var before = session.ControllerImage!.ToString();
        tools.RecordPendingPublish(PublishResult());
        Assert.NotEqual(before, session.ControllerImage!.ToString());

        var json = await tools.GetCancelPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Originals), () => true,
            probe: new TestProbe());

        Assert.Equal("cancelled", StatusOf(json));
        Assert.Equal(before, session.ControllerImage!.ToString());
        Assert.Null(SessionImageState.StalenessOf(session));
    }

    [Fact]
    public async Task PublishResult_WhoseLandedIndicesDoNotStartAtTheInsertPosition_IsNotRecorded_SoAcceptMarksStale()
    {
        var (tools, session, _, sdk, fake) = await SetUp();
        var before = session.ControllerImage!.ToString();
        // Reported insert 1, but the rungs landed at 0..2: not the shape a publish at position 1 produces.
        tools.RecordPendingPublish(PublishResult(insert: 1, landed: [0, 1, 2]));

        Assert.Equal(before, session.ControllerImage!.ToString());
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Published), () => true,
            probe: new TestProbe());

        var stale = SessionImageState.StalenessOf(session);
        Assert.NotNull(stale);
        Assert.DoesNotContain("no publish by this session is recorded", stale!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishResult_WithNonContiguousLandedIndices_IsNotRecorded_SoAcceptMarksStale()
    {
        var (tools, session, _, sdk, fake) = await SetUp();
        var before = session.ControllerImage!.ToString();
        tools.RecordPendingPublish(PublishResult(landed: [0, 1, 3]));

        Assert.Equal(before, session.ControllerImage!.ToString());
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, Published), () => true,
            probe: new TestProbe());

        var stale = SessionImageState.StalenessOf(session);
        Assert.NotNull(stale);
        Assert.DoesNotContain("no publish by this session is recorded", stale!.Reason, StringComparison.Ordinal);
    }

    private static readonly string[] OneRung = ["XIC(A)OTE(E);"];

    private static LogixSdkTools ToolsFor(TagSessionService session, ProjectService service) =>
        new(service, session, new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true));

    private static IReadOnlyList<Rung> ImageRungs(TagSessionService session) =>
        session.ControllerImage!.Programs.Single().Routines.Single().Rungs.ToList();

    [Fact]
    public async Task Publish_Replace0_1_Landed0_ThenAccept_ImageRoutineIsTheNewRungOnly_AndNotStale()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect(OneRungImage());
        var tools = ToolsFor(session, service);
        var fake  = new FakePendingEditsProject { ExportContent = Export(true, OneRung, insert: 0, replace: 1) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0]));
        var json = await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk,
            Runner(fake, OneRung), () => true, probe: new TestProbe());

        Assert.Equal("accepted", StatusOf(json));
        Assert.Null(SessionImageState.StalenessOf(session));
        var rungs = ImageRungs(session);
        Assert.Single(rungs);
        Assert.Equal("XIC(A)OTE(E);", rungs[0].Text.ToString());
        Assert.Equal("N", rungs[0].Type!.ToString());
        var lint = JsonDocument.Parse(LintHandler.Serialize(LintHandler.Execute(service, session: session))).RootElement;
        Assert.False(lint.TryGetProperty("imageStale", out _));
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
    }

    [Fact]
    public async Task Publish_Replace0_2_Landed0_WithOneNewRung_ThenAccept_ImageHasOneRungWhereThereWereTwo()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect(TwoRungImage());
        var tools = ToolsFor(session, service);
        var fake  = new FakePendingEditsProject { ExportContent = Export(true, OneRung, insert: 0, replace: 2) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        Assert.Equal(2, ImageRungs(session).Count);

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 2, texts: OneRung, landed: [0]));
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk,
            Runner(fake, OneRung), () => true, probe: new TestProbe());

        Assert.Null(SessionImageState.StalenessOf(session));
        var rungs = ImageRungs(session);
        Assert.Single(rungs);
        Assert.Equal("XIC(A)OTE(E);", rungs[0].Text.ToString());
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
    }

    [Fact]
    public async Task Publish_InsertOnly_AtPosition1_Landed1_IsStillRecorded_AndAcceptPutsTheRungAtPosition1()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();
        var tools = ToolsFor(session, service);
        var after = new[] { Originals[0], OneRung[0], Originals[1], Originals[2] };
        var fake  = new FakePendingEditsProject { ExportContent = Export(true, OneRung, insert: 1, replace: 0) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);

        tools.RecordPendingPublish(PublishResult(insert: 1, replace: 0, texts: OneRung, landed: [1]));
        Assert.Equal(4, ImageRungs(session).Count);   // merged as pending
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk,
            Runner(fake, after), () => true, probe: new TestProbe());

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(after, ImageRungs(session).Select(r => r.Text.ToString()).ToArray());
    }

    [Fact]
    public async Task Cancel_OfARecordedReplaceZone_RestoresTheImageAsItWasBeforeThePublish()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();
        var tools = ToolsFor(session, service);
        var fake  = new FakePendingEditsProject { ExportContent = Export(true, OneRung, insert: 0, replace: 1) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        var before = session.ControllerImage!.ToString();

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0]));
        Assert.NotEqual(before, session.ControllerImage!.ToString());
        await tools.GetCancelPendingEditsResponseAsync("MainProgram", "Main", true, sdk,
            Runner(fake, Originals), () => true, probe: new TestProbe());

        Assert.Equal(before, session.ControllerImage!.ToString());
        Assert.Null(SessionImageState.StalenessOf(session));
    }

    private static string CommentOf(TagSessionService session, int index) => ImageRungs(session)[index].Comment?.ToString() ?? "";

    private static async Task<(LogixSdkTools Tools, TagSessionService Session, ProjectService Service, LogixProjectSession Sdk, FakePendingEditsProject Fake)>
        SetUpWith(L5X image, string[] pendingRungs, int insert, int replace)
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect(image);
        var tools = ToolsFor(session, service);
        var fake  = new FakePendingEditsProject { ExportContent = Export(true, pendingRungs, insert, replace) };
        var (sdk, _) = await PendingEditsTestHelpers.OpenedSessionAsync(fake);
        return (tools, session, service, sdk, fake);
    }

    private static string GetRoutineComment(TagSessionService session, ProjectService service, int rung)
    {
        var result = Assert.IsType<GetRoutineResult>(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", result.Source);
        return result.Rungs[rung].Comment;
    }

    [Fact]
    public async Task Accept_OfReplace0_1_WithPreservedOriginalComment_SessionImageGetRoutineReturnsThatComment()
    {
        var (tools, session, service, sdk, fake) = await SetUpWith(
            SessionImageAfterAcceptTests.ImageWithComments(("XIC(A)OTE(B);", "Original comment")), OneRung, 0, 1);

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0], comments: ["Original comment"]));
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, OneRung), () => true, probe: new TestProbe());

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal("Original comment", CommentOf(session, 0));
        Assert.Contains("Original comment", GetRoutineComment(session, service, 0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_WithAnExplicitNewComment_TheExplicitCommentWinsOverTheOriginal()
    {
        var (tools, session, service, sdk, fake) = await SetUpWith(
            SessionImageAfterAcceptTests.ImageWithComments(("XIC(A)OTE(B);", "Original comment")), OneRung, 0, 1);

        // The controller lands the explicit comment (the publish result carries what the rung ended up with).
        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0], comments: ["Explicit comment"]));
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, OneRung), () => true, probe: new TestProbe());

        Assert.Equal("Explicit comment", CommentOf(session, 0));
        var shown = GetRoutineComment(session, service, 0);
        Assert.Contains("Explicit comment", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Original comment", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_WithNoComment_SessionImageCommentIsEmpty()
    {
        var (tools, session, service, sdk, fake) = await SetUpWith(
            SessionImageAfterAcceptTests.ImageWithComments(("XIC(A)OTE(B);", null)), OneRung, 0, 1);

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0], comments: [null]));
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, OneRung), () => true, probe: new TestProbe());

        Assert.Equal("", CommentOf(session, 0));
        Assert.Equal("", GetRoutineComment(session, service, 0));
    }

    [Fact]
    public async Task Accept_OfInsertOnlyPublish_KeepsTheCommentsOfTheExistingRungs()
    {
        var (tools, session, service, sdk, fake) = await SetUpWith(
            SessionImageAfterAcceptTests.ImageWithComments(("XIC(A)OTE(B);", "First"), ("XIC(C)OTE(B);", "Second")), OneRung, 1, 0);
        var after = new[] { "XIC(A)OTE(B);", OneRung[0], "XIC(C)OTE(B);" };

        tools.RecordPendingPublish(PublishResult(insert: 1, replace: 0, texts: OneRung, landed: [1], comments: ["Inserted"]));
        await tools.GetAcceptPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, after), () => true, probe: new TestProbe());

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(["First", "Inserted", "Second"], Enumerable.Range(0, 3).Select(i => CommentOf(session, i)).ToArray());
        Assert.Equal(after, ImageRungs(session).Select(r => r.Text.ToString()).ToArray());
    }

    [Fact]
    public async Task Cancel_OfAPublishWithComments_RestoresThePrePublishImageUnchanged()
    {
        var (tools, session, _, sdk, fake) = await SetUpWith(
            SessionImageAfterAcceptTests.ImageWithComments(("XIC(A)OTE(B);", "Original comment")), OneRung, 0, 1);
        var before = session.ControllerImage!.ToString();

        tools.RecordPendingPublish(PublishResult(insert: 0, replace: 1, texts: OneRung, landed: [0], comments: ["Explicit comment"]));
        Assert.NotEqual(before, session.ControllerImage!.ToString());
        await tools.GetCancelPendingEditsResponseAsync("MainProgram", "Main", true, sdk, Runner(fake, ["XIC(A)OTE(B);"]), () => true, probe: new TestProbe());

        Assert.Equal(before, session.ControllerImage!.ToString());
        Assert.Equal("Original comment", CommentOf(session, 0));
    }

    private static L5X OneRungImage() => SessionImageAfterAcceptTests.ImageOf("XIC(A)OTE(B);");

    private static L5X TwoRungImage() => SessionImageAfterAcceptTests.ImageOf("XIC(A)OTE(B);", "XIC(C)OTE(B);");

    [Fact]
    public void PublishResult_WithoutLandedTexts_IsNotRecorded()
    {
        var (session, _, _) = SessionImageAfterAcceptTests.Connect();
        var tools = new LogixSdkTools(new ProjectService(), session);
        var before = session.ControllerImage!.ToString();

        tools.RecordPendingPublish(new ProposeRungEditResult("proposed", RoutineXPath, 0u, 2u, "published"));

        Assert.Equal(before, session.ControllerImage!.ToString());
    }

    private sealed class TestProbe : IEnvironmentProbe
    {
        public int CurrentSessionId { get; init; } = 1;
        public bool IsElevated { get; init; }
        public IReadOnlyList<int> LogixDesignerSessionIds { get; init; } = Array.Empty<int>();
        public bool StudioIntegrityHigherThanCurrent { get; init; }
    }
}
#endif
