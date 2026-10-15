using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The session image follows the controller by export, not by replay. After a publish, accept, assemble or
/// cancel the program is marked stale and <see cref="TagSessionService.RefreshControllerImageAsync"/> installs the
/// controller's export; these tests script that export (<see cref="TagSessionService.ImageSourceOverride"/>) for the nine
/// The replace shapes and the two-routine publish. A failed export marks the image stale with the error as the reason (<c>imageStale</c>,
/// <c>staleSince</c>, <c>staleReason</c>, reconnect hint) and the next successful refresh clears it.
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

    /// <summary>An image whose rungs carry the given <c>Type</c> codes, the shape a real post-stage export has (P58 probe).</summary>
    internal static L5X ImageOfTyped(params (string Text, string Type)[] rungs) =>
        BuildImage(rungs.Select(r => (r.Text, (string?)null)).ToArray(), rungs.Select(r => r.Type).ToArray());

    private static L5X BuildImage((string Text, string? Comment)[] rungs, string[]? types = null)
    {
        var tagXml  = string.Concat(Tags.Select(t => string.Format(Tag, t)));
        var rungXml = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{(types is null ? "N" : types[i])}\">" +
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

    private static void AssertStale(JsonElement root)
    {
        Assert.True(root.GetProperty("imageStale").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("staleSince").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("staleReason").GetString()));
        Assert.Contains("reconnect (logix_connect) to refresh before relying on this result", root.GetProperty("hint").GetString());
        Assert.Equal("session-image", root.GetProperty("source").GetString());
    }

    /// <summary>A controller whose program export is a fixed image, or fails with <see cref="Fail"/>.</summary>
    internal sealed class ScriptedExport(L5X? export = null) : IControllerImageSource, IStagedRungSource
    {
        public L5X? Export { get; set; } = export;
        public Exception? Fail { get; set; }
        public List<StagedBlock> Blocks { get; } = [];
        public int ProgramExports { get; private set; }

        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default)
        {
            ProgramExports++;
            return Fail is null ? Task.FromResult(Export!.ToString()) : Task.FromException<string>(Fail);
        }

        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) =>
            Fail is null ? Task.FromResult(Export!.ToString()) : Task.FromException<string>(Fail);

        public IReadOnlyList<StagedBlock> StagedBlocks(string program) => Blocks;
    }

    private static string[] TypesOf(TagSessionService session) =>
        SafeXml.ParseDocument(session.ControllerImage!.ToString()).Descendants("Rung")
            .Select(r => (string?)r.Attribute("Type") ?? "N").ToArray();

    private static string[] TextsOf(L5X image) =>
        SafeXml.ParseDocument(image.ToString()).Descendants("Rung").Select(r => r.Element("Text")!.Value).ToArray();

    private static string[] TypesOfRoutine(TagSessionService session, string routine) =>
        SafeXml.ParseDocument(session.ControllerImage!.ToString()).Descendants("Routine")
            .Single(r => (string?)r.Attribute("Name") == routine).Descendants("Rung")
            .Select(r => (string?)r.Attribute("Type") ?? "N").ToArray();

    private static string[] TextsOfRoutine(L5X image, string routine) =>
        SafeXml.ParseDocument(image.ToString()).Descendants("Routine")
            .Single(r => (string?)r.Attribute("Name") == routine).Descendants("Rung")
            .Select(r => r.Element("Text")!.Value).ToArray();

    private static JsonElement RoutineJson(ProjectService service, TagSessionService session, string routine = "Main") =>
        JsonSerializer.SerializeToElement(GetRoutineHandler.Execute(service, "MainProgram", routine, session));

    private static string[] RoutineTexts(JsonElement routine) =>
        routine.GetProperty("rungs").EnumerateArray().Select(r => r.GetProperty("text").GetString()!).ToArray();

    /// <summary>
    /// A two-routine program: <c>Main</c> and <c>Other</c>, each with the given rungs
    /// (<c>Text</c>, <c>Type</c>).
    /// </summary>
    internal static L5X TwoRoutineImage((string Text, string Type)[] main, (string Text, string Type)[] other)
    {
        string Routine(string name, (string Text, string Type)[] rungs) =>
            $"<Routine Name=\"{name}\" Type=\"RLL\"><RLLContent>" +
            string.Concat(rungs.Select((r, i) => $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>")) +
            "</RLLContent></Routine>";
        var tagXml = string.Concat(Tags.Select(t => string.Format(Tag, t)));
        return L5X.Parse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\">" +
            "<DataTypes /><Modules /><AddOnInstructionDefinitions />" +
            $"<Tags>{tagXml}</Tags>" +
            "<Programs><Program Name=\"MainProgram\" Type=\"Normal\" TestEdits=\"false\" Disabled=\"false\" UseAsFolder=\"false\" MainRoutineName=\"Main\">" +
            "<Tags /><Routines>" + Routine("Main", main) + Routine("Other", other) +
            "</Routines></Program></Programs></Controller></RSLogix5000Content>");
    }

    /// <summary>Installs <paramref name="export"/> as what the controller exports and runs the refresh the publish / accept tools run.</summary>
    internal static async Task<bool> RefreshFromAsync(TagSessionService session, L5X export)
    {
        session.ImageSourceOverride = new ScriptedExport(export);
        session.MarkImageStale("MainProgram");
        return await session.RefreshControllerImageAsync();
    }

    // ── The nine shapes ─────────────────────────────────────────────────────────────────────
    // Each shape: the routine before the publish, what the controller exports after the publish (Logix decides the
    // zone's shape: edit pairs IR/R, delete + add D/I, interleaved pairs, ...), and the plain rungs it exports after
    // the accept. The fixtures are the codes the shape map gives; the image must hold them verbatim.

    private const string R0 = "XIC(A)OTE(B);";
    private const string R1 = "XIC(C)OTE(D);";
    private const string R2 = "XIC(A)OTE(E);";
    private const string N0 = "XIC(A)XIC(C)OTE(B);";   // new rung, same output as R0
    private const string N1 = "XIC(C)XIC(A)OTE(D);";   // new rung, same output as R1
    private const string M0 = "XIC(D)OTE(C);";          // new rung, different output

    private sealed record Shape(
        string[] Before, (string Text, string Type)[] AfterPublish, string[] AfterAccept);

    private static readonly Dictionary<string, Shape> Shapes = new()
    {
        // a: replace one rung with a rung that keeps its output: an edit pair at the position.
        ["a"]   = new([R0, R1, R2], [(N0, "IR"), (R0, "R"), (R1, "N"), (R2, "N")], [N0, R1, R2]),
        // b: replace one rung with a rung whose output changes: delete + add, the new rung at position + replaceCount.
        ["b"]   = new([R0, R1, R2], [(R0, "D"), (M0, "I"), (R1, "N"), (R2, "N")], [M0, R1, R2]),
        // c: insert only.
        ["c"]   = new([R0, R1, R2], [(R0, "N"), (M0, "I"), (R1, "N"), (R2, "N")], [R0, M0, R1, R2]),
        // d: delete one rung (no new rung).
        ["d"]   = new([R0, R1, R2], [(R0, "D"), (R1, "N"), (R2, "N")], [R1, R2]),
        // d2: delete two rungs.
        ["d2"]  = new([R0, R1, R2], [(R0, "D"), (R1, "D"), (R2, "N")], [R2]),
        // e0: replace rung 0 of a two-rung routine with a different-output rung: D/I/N.
        ["e0"]  = new([R0, R1], [(R0, "D"), (M0, "I"), (R1, "N")], [M0, R1]),
        // e1: the same delete + add one rung further down.
        ["e1"]  = new([R0, R1, R2], [(R0, "N"), (R1, "D"), (M0, "I"), (R2, "N")], [R0, M0, R2]),
        // f12: replace two rungs with two edit pairs: interleaved IR/R/IR/R.
        ["f12"] = new([R0, R1, R2], [(N0, "IR"), (R0, "R"), (N1, "IR"), (R1, "R"), (R2, "N")], [N0, N1, R2]),
        // f21: replace two rungs with one edit pair and one deleted rung.
        ["f21"] = new([R0, R1, R2], [(N0, "IR"), (R0, "R"), (R1, "D"), (R2, "N")], [N0, R2]),
    };

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    [InlineData("d")]
    [InlineData("d2")]
    [InlineData("e0")]
    [InlineData("e1")]
    [InlineData("f12")]
    [InlineData("f21")]
    public async Task R24Shape_AfterPublishTheZoneIsExactlyTheExport_AfterAcceptTheAcceptedViewIsTheFixtureAndNotStale(string shape)
    {
        var fixture = Shapes[shape];
        var (session, service, baseline) = ConnectOnly(fixture.Before);

        // Publish: the program is marked stale and refreshed from the export before the response returns.
        Assert.True(await RefreshFromAsync(session, ImageOfTyped(fixture.AfterPublish)));
        Assert.Equal(fixture.AfterPublish.Select(r => r.Type).ToArray(), TypesOf(session));
        Assert.Equal(fixture.AfterPublish.Select(r => r.Text).ToArray(), TextsOf(session.ControllerImage!));
        Assert.Equal([.. Enumerable.Range(0, fixture.AfterPublish.Length)],
            session.ControllerImage!.Programs.Single().Routines.Single().Rungs.Select(r => (int)r.Number).ToArray());
        // includePending reads (the session image) show the zone; includePending: false is the controller's accepted logic.
        Assert.Equal(baseline, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        Assert.Equal(fixture.Before, TextsOf(session.AcceptedControllerImage!));
        var pending = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: true));
        Assert.Equal("session-image", pending.Source);
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(session.IsImageStale);

        // Accept: the export now holds the plain rungs, and the accepted view is exactly that, with no stale flag.
        Assert.True(await RefreshFromAsync(session, ImageOf(fixture.AfterAccept)));
        Assert.Equal(fixture.AfterAccept, TextsOf(session.ControllerImage!));
        Assert.Equal(fixture.AfterAccept, TextsOf(session.AcceptedControllerImage!));
        Assert.All(TypesOf(session), t => Assert.Equal("N", t));
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(session.IsImageStale);
        Assert.Null(session.ImageStaleNote);

        var routine = RoutineJson(service, session);
        Assert.Equal("session-image", routine.GetProperty("source").GetString());
        Assert.Equal(fixture.AfterAccept, RoutineTexts(routine));
        Assert.False(routine.TryGetProperty("imageStale", out _));
        Assert.False(LintJson(session, service).TryGetProperty("imageStale", out _));
        Assert.False(DocumentJson(session, service).TryGetProperty("imageStale", out _));
    }

    /// <summary>The deleted original and the surviving rung were the two things the replay got wrong.</summary>
    [Fact]
    public async Task E0_ReplaceRung0OfATwoRungRoutine_AcceptedViewHasTheNewRungAndTheSurvivor_NotTheDeletedOriginal()
    {
        var (session, service, _) = ConnectOnly(R0, R1);
        Assert.True(await RefreshFromAsync(session, ImageOfTyped((R0, "D"), (M0, "I"), (R1, "N"))));
        Assert.True(await RefreshFromAsync(session, ImageOf(M0, R1)));

        var routine = RoutineJson(service, session);
        Assert.Equal("session-image", routine.GetProperty("source").GetString());
        Assert.Equal([M0, R1], RoutineTexts(routine));
        Assert.DoesNotContain(R0, RoutineTexts(routine));
        Assert.False(routine.TryGetProperty("imageStale", out _));
        Assert.Null(SessionImageState.StalenessOf(session));
    }

    /// <summary>One publish to two routines of a program; accept resolves the program's whole zone.</summary>
    [Fact]
    public async Task TwoRoutinePublish_AfterPublishBothZonesShow_AfterAcceptBothRoutinesArePlainAndNotStale()
    {
        var before = TwoRoutineImage([(R0, "N"), (R1, "N")], [(R2, "N")]);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(before, DateTimeOffset.UtcNow.AddMinutes(-5));
        var service = new ProjectService();
        service.RegisterFromUpload(before, "C.ACD", "1.2.3.4");

        var published = TwoRoutineImage([(N0, "IR"), (R0, "R"), (R1, "N")], [(M0, "I"), (R2, "N")]);
        Assert.True(await RefreshFromAsync(session, published));
        Assert.Equal(["IR", "R", "N"], TypesOfRoutine(session, "Main"));
        Assert.Equal(["I", "N"], TypesOfRoutine(session, "Other"));
        Assert.Equal([N0, R0, R1], TextsOfRoutine(session.ControllerImage!, "Main"));
        Assert.Equal([M0, R2], TextsOfRoutine(session.ControllerImage!, "Other"));
        Assert.Equal([R0, R1], TextsOfRoutine(session.AcceptedControllerImage!, "Main"));
        Assert.Equal([R2], TextsOfRoutine(session.AcceptedControllerImage!, "Other"));

        var accepted = TwoRoutineImage([(N0, "N"), (R1, "N")], [(M0, "N"), (R2, "N")]);
        Assert.True(await RefreshFromAsync(session, accepted));
        Assert.Equal([N0, R1], TextsOfRoutine(session.AcceptedControllerImage!, "Main"));
        Assert.Equal([M0, R2], TextsOfRoutine(session.AcceptedControllerImage!, "Other"));
        Assert.All(TypesOfRoutine(session, "Main").Concat(TypesOfRoutine(session, "Other")), t => Assert.Equal("N", t));
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(session.IsImageStale);
        foreach (var name in new[] { "Main", "Other" })
        {
            var routine = RoutineJson(service, session, name);
            Assert.Equal("session-image", routine.GetProperty("source").GetString());
            Assert.False(routine.TryGetProperty("imageStale", out _));
        }
    }

    [Fact]
    public async Task Cancel_RefreshRestoresTheImageToTheRoutineBeforeThePublish()
    {
        var (session, service, _) = Connect();
        var before = session.ControllerImage!.ToString();
        Assert.True(await RefreshFromAsync(session, ImageOfTyped((N0, "IR"), (R0, "R"), (R1, "N"), (R2, "N"))));
        Assert.NotEqual(before, session.ControllerImage!.ToString());

        Assert.True(await RefreshFromAsync(session, ImageOf(R0, R1, R2)));

        Assert.Equal([R0, R1, R2], TextsOf(session.ControllerImage!));
        Assert.All(TypesOf(session), t => Assert.Equal("N", t));
        Assert.Null(SessionImageState.StalenessOf(session));
        _ = service;
    }

    /// <summary>The refresh copies the comment the controller holds for each rung; nothing is guessed.</summary>
    [Fact]
    public async Task Refresh_CarriesTheCommentsTheExportHolds()
    {
        var (session, service, _) = Connect(ImageWithComments((R0, "Original comment")));
        Assert.True(await RefreshFromAsync(session, ImageWithComments(("XIC(A)OTE(E);", "Explicit comment"))));

        var result = Assert.IsType<GetRoutineResult>(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", result.Source);
        Assert.Contains("Explicit comment", result.Rungs[0].Comment, StringComparison.Ordinal);
        Assert.DoesNotContain("Original comment", result.Rungs[0].Comment, StringComparison.Ordinal);
    }

    // ── A failing export ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailingExport_MarksTheImageStaleWithTheErrorInTheReason_AndTheNextSuccessfulRefreshClearsIt()
    {
        var (session, service, _) = Connect();
        var before = session.ControllerImage!.ToString();
        var source = new ScriptedExport { Fail = new InvalidOperationException("the export timed out") };
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");

        Assert.False(await session.RefreshControllerImageAsync());

        var stale = Assert.IsType<SessionImageStaleness>(SessionImageState.StalenessOf(session));
        Assert.Equal("refresh of program 'MainProgram' failed: the export timed out", stale.Reason);
        Assert.Contains("the export timed out", session.ImageStaleNote, StringComparison.Ordinal);
        Assert.Equal(before, session.ControllerImage!.ToString());   // a failed export leaves the image as it was
        AssertStale(LintJson(session, service));
        Assert.Contains("the export timed out", LintJson(session, service).GetProperty("staleReason").GetString(), StringComparison.Ordinal);
        AssertStale(DocumentJson(session, service));

        // The next successful refresh clears both the service's flags and the SessionImageState mark.
        source.Fail = null;
        source.Export = ImageOf(N0, R1, R2);
        Assert.True(await session.RefreshControllerImageAsync());

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(session.IsImageStale);
        Assert.Null(session.ImageStaleNote);
        Assert.Equal([N0, R1, R2], TextsOf(session.ControllerImage!));
        Assert.False(LintJson(session, service).TryGetProperty("imageStale", out _));
        Assert.False(DocumentJson(session, service).TryGetProperty("imageStale", out _));
    }

    [Fact]
    public async Task FailingExport_OfAProgramTheExportDoesNotHold_NamesTheProgram()
    {
        var (session, _, _) = Connect();
        session.ImageSourceOverride = new ScriptedExport(ImageOf(R0));
        session.MarkImageStale("NoSuchProgram");

        Assert.False(await session.RefreshControllerImageAsync());

        Assert.Contains("refresh of program 'NoSuchProgram' failed: the export held no program 'NoSuchProgram'",
            SessionImageState.StalenessOf(session)!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRefresh_ClearsAStaleMarkAnEarlierFailureSet_EvenWhenTheNextExportShowsAZone()
    {
        var (session, _, _) = Connect();
        session.ImageSourceOverride = new ScriptedExport { Fail = new InvalidOperationException("boom") };
        session.MarkImageStale("MainProgram");
        Assert.False(await session.RefreshControllerImageAsync());
        Assert.NotNull(SessionImageState.StalenessOf(session));

        Assert.True(await RefreshFromAsync(session, ImageOfTyped((N0, "IR"), (R0, "R"), (R1, "N"), (R2, "N"))));

        Assert.Null(SessionImageState.StalenessOf(session));
    }

    // ── Workstation-staged blocks, identical text ───────────────────────────────

    private static (TagSessionService Session, ProjectService Service, string Baseline) ConnectOnly(params string[] rungs)
    {
        var image   = ImageOf(rungs);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");
        return (session, service, ControllerImageRefresh.Accepted(image).ToString());
    }

    /// <summary>
    /// Stage against <paramref name="baseline"/>, refresh with the real post-stage export (<paramref name="export"/>, the
    /// staged rungs as er/e and the displaced originals as rN/dN), publish (the export then holds the zone codes), and
    /// check the accepted view stays at the baseline throughout.
    /// </summary>
    private static async Task StageRefreshPublishAsync(
        string[] baseline, (string Text, string Type)[] export, int replaceCount, string[] published,
        (string Text, string Type)[] exportAfterPublish, string[] expectedExportedTypes)
    {
        var (session, service, baselineAccepted) = ConnectOnly(baseline);
        Assert.Equal("project-file", RoutineJson(service, session).GetProperty("source").GetString());

        var source = new ScriptedExport(ImageOfTyped(export));
        source.Blocks.Add(new StagedBlock("MainProgram", "Main", 0, replaceCount, published));
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);

        // The export's own codes are left alone, and the accepted view is the baseline.
        Assert.Equal(expectedExportedTypes, TypesOf(session));
        Assert.Equal(export.Select(r => r.Text).ToArray(), TextsOf(session.ControllerImage!));
        Assert.Equal(baselineAccepted, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        var staged = RoutineJson(service, session);
        Assert.Equal("project-file", staged.GetProperty("source").GetString());
        Assert.Equal(baseline, RoutineTexts(staged));

        // Publish: the buffer empties and the refresh shows the zone exactly as the controller exports it.
        source.Blocks.Clear();
        source.Export = ImageOfTyped(exportAfterPublish);
        session.MarkImageStale("MainProgram");
        Assert.True(await session.RefreshControllerImageAsync());
        Assert.Equal(exportAfterPublish.Select(r => r.Type).ToArray(), TypesOf(session));
        Assert.Equal(exportAfterPublish.Select(r => r.Text).ToArray(), TextsOf(session.ControllerImage!));
        Assert.Equal(baselineAccepted, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        var after = RoutineJson(service, session);
        Assert.Equal("project-file", after.GetProperty("source").GetString());
        Assert.Equal(baseline, RoutineTexts(after));
        Assert.Equal(0, DuplicateOutputs(session, service));
        var refs = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: false));
        Assert.Equal(baseline.Count(t => t.Contains("OTE(B)")), refs.References.Count(r => r.Kind == "write"));

        // Accept: the export holds the published rung and what followed.
        var acceptedRungs = published.Concat(baseline.Skip(replaceCount)).ToArray();
        Assert.True(await RefreshFromAsync(session, ImageOf(acceptedRungs)));
        Assert.Null(SessionImageState.StalenessOf(session));
        var accepted = RoutineJson(service, session);
        Assert.Equal("session-image", accepted.GetProperty("source").GetString());
        Assert.Equal(acceptedRungs, RoutineTexts(accepted));
    }

    [Fact]
    public Task StagedReplace_WithTheRealExportShape_PublishShowsTheZoneAndKeepsTheBaseline() =>
        StageRefreshPublishAsync(
            baseline: ["XIC(A)OTE(B);"],
            export: [("XIC(A)XIC(C)OTE(B);", "er"), ("XIC(A)OTE(B);", "rN")],
            replaceCount: 1, published: ["XIC(A)XIC(C)OTE(B);"],
            exportAfterPublish: [("XIC(A)XIC(C)OTE(B);", "IR"), ("XIC(A)OTE(B);", "R")],
            expectedExportedTypes: ["er", "rN"]);

    [Fact]
    public Task StagedInsert_WithTheRealExportShape_PublishShowsTheZoneAndKeepsTheBaseline() =>
        StageRefreshPublishAsync(
            baseline: ["XIC(A)OTE(B);"],
            export: [("XIC(A)OTE(E);", "e"), ("XIC(A)OTE(B);", "N")],
            replaceCount: 0, published: ["XIC(A)OTE(E);"],
            exportAfterPublish: [("XIC(A)OTE(E);", "I"), ("XIC(A)OTE(B);", "N")],
            expectedExportedTypes: ["e", "N"]);

    [Fact]
    public Task StagedDeleteReplace_WithDnInTheExport_PublishShowsTheZoneAndKeepsTheBaseline() =>
        StageRefreshPublishAsync(
            baseline: ["XIC(A)OTE(B);", "XIC(C)OTE(D);"],
            export: [("XIC(A)XIC(C)OTE(B);", "er"), ("XIC(A)OTE(B);", "rN"), ("XIC(C)OTE(D);", "dN")],
            replaceCount: 2, published: ["XIC(A)XIC(C)OTE(B);"],
            exportAfterPublish: [("XIC(A)XIC(C)OTE(B);", "IR"), ("XIC(A)OTE(B);", "R"), ("XIC(C)OTE(D);", "D")],
            expectedExportedTypes: ["er", "rN", "dN"]);

    /// <summary>The staged block is stamped IR/R by the refresh when the export shows its rungs as plain N.</summary>
    [Fact]
    public async Task StagedBlock_ShownAsPlainNByTheExport_IsStampedIrAndRByTheRefresh()
    {
        var (session, _, baseline) = ConnectOnly(R0);
        var source = new ScriptedExport(ImageOfTyped((N0, "N"), (R0, "N")));
        source.Blocks.Add(new StagedBlock("MainProgram", "Main", 0, 1, [N0]));
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");

        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(["IR", "R"], TypesOf(session));
        Assert.Equal(baseline, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
    }

    /// <summary>An identical-text publish leaves nothing pending, so the refresh leaves the rung plain.</summary>
    [Fact]
    public async Task IdenticalTextPublish_RefreshLeavesTheRungPlain_AndTheRoutineProjectFile()
    {
        var (session, service, baseline) = ConnectOnly(R0);

        Assert.True(await RefreshFromAsync(session, ImageOfTyped((R0, "N"))));

        Assert.Equal(["N"], TypesOf(session));
        Assert.Equal([R0], TextsOf(session.ControllerImage!));
        Assert.Equal(baseline, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(0, DuplicateOutputs(session, service));
        var routine = RoutineJson(service, session);
        Assert.Equal([R0], RoutineTexts(routine));
        Assert.False(routine.TryGetProperty("imageStale", out _));
    }

    /// <summary>A staged replacement identical to the rung it displaces stays one normal rung with one writer.</summary>
    [Fact]
    public async Task IdenticalTextStage_RefreshWithExportOldN_KeepsTheImageNormal_AndOneWriter()
    {
        const string Old = "XIC(A)OTE(B);";
        var (session, service, baseline) = ConnectOnly(Old);

        var source = new ScriptedExport(ImageOfTyped((Old, "N")));
        source.Blocks.Add(new StagedBlock("MainProgram", "Main", 0, 1, [Old]) { KeptNormal = new HashSet<int> { 0 } });
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(["N"], TypesOf(session));
        Assert.Equal([Old], TextsOf(session.ControllerImage!));
        Assert.Equal(baseline, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        Assert.Equal([Old], TextsOf(ControllerImageRefresh.Accepted(session.ControllerImage!)));
        var routine = RoutineJson(service, session);
        Assert.Equal("project-file", routine.GetProperty("source").GetString());
        Assert.Equal([Old], RoutineTexts(routine));
        Assert.Equal(0, DuplicateOutputs(session, service));
        var refs = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: true));
        Assert.Single(refs.References, r => r.Kind == "write");
    }

    /// <summary>Without KeptNormal the same block is stamped as before (0188/0189 behaviour).</summary>
    [Fact]
    public async Task IdenticalTextStage_WithoutKeptNormal_IsStampedAsBefore()
    {
        const string Old = "XIC(A)OTE(B);";
        var (session, _, _) = ConnectOnly(Old);
        var source = new ScriptedExport(ImageOfTyped((Old, "N")));
        source.Blocks.Add(new StagedBlock("MainProgram", "Main", 0, 1, [Old]));
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(["IR"], TypesOf(session));
    }

    /// <summary>A mixed block stamps only the changed rung IR and only the rung that one displaces R.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedBlock_KeptNormalOld_StampsOnlyNewAsIr_AndOnlyOld2AsR(bool exportHasStagedCodes)
    {
        const string Old  = "XIC(A)OTE(B);";
        const string New  = "XIC(A)XIC(C)OTE(B);";
        const string Old2 = "XIC(A)OTE(D);";
        var (session, service, _) = ConnectOnly(Old, Old2);

        var export = exportHasStagedCodes
            ? ImageOfTyped((Old, "N"), (New, "er"), (Old2, "rN"))
            : ImageOfTyped((Old, "N"), (New, "N"), (Old2, "N"));
        var source = new ScriptedExport(export);
        source.Blocks.Add(new StagedBlock("MainProgram", "Main", 0, 2, [Old, New]) { KeptNormal = new HashSet<int> { 0 } });
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(exportHasStagedCodes ? ["N", "er", "rN"] : ["N", "IR", "R"], TypesOf(session));
        Assert.Equal([Old, Old2], TextsOf(ControllerImageRefresh.Accepted(session.ControllerImage!)));
        Assert.Equal([Old, Old2], RoutineTexts(RoutineJson(service, session)));
    }

    [Fact]
    public void TheCodeSets_AreSharedBetweenTheRefreshAndTheImage()
    {
        Assert.Equal(["I", "IR", "e", "er"], ControllerImageRefresh.PendingOnlyCodes.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["D", "R", "dN", "rN"], ControllerImageRefresh.DisplacedOriginalCodes.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Reconnect_ClearsTheStaleFlag()
    {
        var (session, service, _) = Connect();
        SessionImageState.MarkStale(session, "refresh of program 'MainProgram' failed: boom");
        AssertStale(LintJson(session, service));
        Assert.NotNull(SessionImageState.StalenessOf(session));

        // logix_connect again: a new session service and a freshly taken image.
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(Connected(), DateTimeOffset.UtcNow);

        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.False(LintJson(session, service).TryGetProperty("imageStale", out _));
        Assert.False(DocumentJson(session, service).TryGetProperty("imageStale", out _));
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
/// <summary>The accept / assemble / cancel handlers no longer touch the session image: the tool refreshes it.</summary>
[Collection("StaticHandlerHooks")]
public class SessionImageAfterLifecycleHandlerTests
{
    /// <summary>Each test starts with a ledger that knows the zone this server "published", isolated from other tests' connects.</summary>
    public SessionImageAfterLifecycleHandlerTests() => ZoneLedger.TestOverride = KnownZoneLedger();

    private static ZoneLedger KnownZoneLedger()
    {
        var ledger = new ZoneLedger();
        ledger.Record("MainProgram", new[] { "Main" });
        return ledger;
    }

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
    public async Task AcceptAndAssemble_LeaveTheImageUntouchedAndNotStale_TheToolRefreshesIt(string verb)
    {
        var (session, _, image) = SessionImageAfterAcceptTests.Connect();
        var before = image.ToString();

        var result = verb == "accept"
            ? await AcceptPendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
                default, () => ("direct", null), session)
            : await AssemblePendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
                default, () => ("direct", null), session);

        Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(before, session.ControllerImage!.ToString());
    }

    [Fact]
    public async Task Cancel_LeavesTheImageUntouchedAndNotStale()
    {
        var (session, _, image) = SessionImageAfterAcceptTests.Connect();
        var before = image.ToString();

        var result = await CancelPendingEditsHandler.ExecuteAsync("MainProgram", "Main", true, Reader(Published), Runner, () => true,
            default, () => ("direct", null), imageSession: session);

        Assert.IsType<CancelPendingEditsResult>(result);
        Assert.Null(SessionImageState.StalenessOf(session));
        Assert.Equal(before, session.ControllerImage!.ToString());
    }
}

/// <summary>
/// The real tool entry points (<see cref="LogixSdkTools"/>) mark the program stale and refresh the session image from
/// the controller export before they return (b, c). The export is scripted; the Studio driver is not reached
/// (the tools refuse without <c>confirm</c>, and the refresh runs regardless, as the mark always has).
/// </summary>
[Collection("StaticHandlerHooks")]
public class SessionImageThroughToolsTests
{
    private static readonly (string Text, string Type)[] AfterPublish =
        [("XIC(A)XIC(C)OTE(B);", "IR"), ("XIC(A)OTE(B);", "R"), ("XIC(C)OTE(B);", "N"), ("XIC(A)OTE(D);", "N")];

    private static readonly string[] AfterAccept = ["XIC(A)XIC(C)OTE(B);", "XIC(C)OTE(B);", "XIC(A)OTE(D);"];

    private static (LogixSdkTools Tools, TagSessionService Session, ProjectService Service) SetUp()
    {
        var (session, service, _) = SessionImageAfterAcceptTests.Connect();
        var tools = new LogixSdkTools(service, session, new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true));
        return (tools, session, service);
    }

    private static string[] Texts(TagSessionService session) =>
        session.ControllerImage!.Programs.Single().Routines.Single().Rungs.Select(r => r.Text.ToString()).ToArray();

    public static TheoryData<string> LifecycleTools => new() { "accept", "cancel", "assemble", "test", "untest", "clear_staged" };

    [Theory]
    [MemberData(nameof(LifecycleTools))]
    public async Task LifecycleTool_RefreshesTheImageFromTheExportBeforeReturning(string tool)
    {
        var (tools, session, service) = SetUp();
        session.ImageSourceOverride = new SessionImageAfterAcceptTests.ScriptedExport(SessionImageAfterAcceptTests.ImageOf(AfterAccept));

        _ = tool switch
        {
            "accept"       => await tools.AcceptPendingEdits("MainProgram", "Main"),
            "cancel"       => await tools.CancelPendingEdits("MainProgram", "Main"),
            "assemble"     => await tools.AssemblePendingEdits("MainProgram", "Main"),
            "test"         => await tools.TestPendingEdits("MainProgram", "Main"),
            "untest"       => await tools.UntestPendingEdits("MainProgram", "Main"),
            _              => await tools.ClearStaged(program: "MainProgram"),
        };

        Assert.Equal(AfterAccept, Texts(session));
        Assert.False(session.IsImageStale);
        Assert.Null(SessionImageState.StalenessOf(session));
        var routine = JsonSerializer.SerializeToElement(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", routine.GetProperty("source").GetString());
        Assert.False(routine.TryGetProperty("imageStale", out _));
    }

    [Fact]
    public async Task AcceptTool_WithAFailingExport_LeavesTheImageStaleWithTheExportError()
    {
        var (tools, session, service) = SetUp();
        var before = session.ControllerImage!.ToString();
        session.ImageSourceOverride = new SessionImageAfterAcceptTests.ScriptedExport { Fail = new InvalidOperationException("export failed") };

        await tools.AcceptPendingEdits("MainProgram", "Main");

        Assert.Equal(before, session.ControllerImage!.ToString());
        Assert.Equal("refresh of program 'MainProgram' failed: export failed", SessionImageState.StalenessOf(session)!.Reason);
        Assert.Contains("export failed", session.ImageStaleNote, StringComparison.Ordinal);
        var routine = JsonSerializer.SerializeToElement(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.True(routine.GetProperty("imageStale").GetBoolean());
    }

    [Fact]
    public async Task ZoneInTheExportAfterAPublish_IsShownByTheImageExactlyAsExported()
    {
        var (_, session, _) = SetUp();
        Assert.True(await SessionImageAfterAcceptTests.RefreshFromAsync(session, SessionImageAfterAcceptTests.ImageOfTyped(AfterPublish)));

        Assert.Equal(AfterPublish.Select(r => r.Text).ToArray(), Texts(session));
        Assert.Equal(AfterPublish.Select(r => r.Type).ToArray(),
            session.ControllerImage!.Programs.Single().Routines.Single().Rungs.Select(r => r.Type!.ToString()).ToArray());
    }
}
#endif
