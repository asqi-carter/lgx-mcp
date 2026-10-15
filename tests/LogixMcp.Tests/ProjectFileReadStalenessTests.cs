using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The project-file reads follow an edit this session applied to the session image through an
/// accept or assemble, or flag the stale image; a reconnect clears both. Every test builds its own session and project
/// service, so nothing here touches a shared default or the real application data folder.
/// </summary>
public class ProjectFileReadStalenessTests
{
    private const string Hint = "reconnect (logix_connect) to refresh before relying on this result";
    private const string NewRung = "XIC(A)OTE(E);";

    /// <summary>A session holding an image of the connected controller; the project is the upload logix_connect registered.</summary>
    private static (TagSessionService Session, ProjectService Service) Connect()
    {
        var image   = SessionImageAfterAcceptTests.ImageOf("XIC(A)OTE(B);", "XIC(C)OTE(B);", "XIC(A)OTE(D);");
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: false);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");
        return (session, service);
    }

    /// <summary>
    /// A publish then accept of a replace of rung 0: the controller now exports the new rung as ordinary
    /// logic, and the refresh the accept tool runs installs that export in the image.
    /// </summary>
    private static void AcceptReplaceOfRungZero(TagSessionService session)
    {
        session.ImageSourceOverride = new ExportOf(SessionImageAfterAcceptTests.ImageOf(NewRung, "XIC(C)OTE(B);", "XIC(A)OTE(D);"));
        session.MarkImageStale("MainProgram");
        Assert.True(session.RefreshControllerImageAsync().GetAwaiter().GetResult());
        Assert.Null(SessionImageState.StalenessOf(session));
    }

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result, result.GetType());

    private static void AssertStale(JsonElement root, string source)
    {
        Assert.Equal(source, root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("imageStale").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("staleSince").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("staleReason").GetString()));
        Assert.Contains(Hint, root.GetProperty("hint").GetString());
    }

    private static void AssertNotStale(JsonElement root)
    {
        Assert.False(root.TryGetProperty("imageStale", out _));
        Assert.False(root.TryGetProperty("staleSince", out _));
        Assert.False(root.TryGetProperty("staleReason", out _));
        Assert.False(root.TryGetProperty("hint", out _));
    }

    private static string[] RungTexts(JsonElement routine) =>
        routine.GetProperty("rungs").EnumerateArray().Select(r => r.GetProperty("text").GetString()!).ToArray();

    /// <summary>The connected image plus an empty ladder routine <c>MainProgram/R_New</c> the project file lacks.</summary>
    internal static L5X WithNewRoutine(L5X image) => L5X.Parse(image.ToString().Replace(
        "</Routines>", "<Routine Name=\"R_New\" Type=\"RLL\"><RLLContent /></Routine></Routines>"));

    /// <summary>A session whose image holds <c>R_New</c> although the connected project file does not.</summary>
    internal static (TagSessionService Session, ProjectService Service) ConnectThenCreateRoutine()
    {
        var (session, service) = Connect();
        session.SetControllerImage(WithNewRoutine(session.ControllerImage!), DateTimeOffset.UtcNow);
        return (session, service);
    }

    [Fact]
    public void CreatedRoutine_GetRoutineReadsItFromTheSessionImageAtOnce()
    {
        var (session, service) = ConnectThenCreateRoutine();

        var result = Json(GetRoutineHandler.Execute(service, "MainProgram", "R_New", session));

        Assert.Equal("R_New", result.GetProperty("routine").GetString());
        Assert.Equal(0, result.GetProperty("rungs").GetArrayLength());
        Assert.Equal("session-image", result.GetProperty("source").GetString());
        Assert.True(result.GetProperty("createdThisSession").GetBoolean());
    }

    [Fact]
    public void RoutineTheBaselineHoldsUnchanged_StillReadsFromTheProjectFile()
    {
        var (session, service) = ConnectThenCreateRoutine();

        var result = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));

        Assert.Equal("project-file", result.GetProperty("source").GetString());
        Assert.False(result.TryGetProperty("createdThisSession", out _));
        Assert.Equal("XIC(A)OTE(B);", RungTexts(result)[0]);
    }

    [Fact]
    public void AfterAppliedAccept_GetRoutineReturnsTheNewRungWithSessionImageSource()
    {
        var (session, service) = Connect();
        var before = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("project-file", before.GetProperty("source").GetString());
        Assert.Equal("XIC(A)OTE(B);", RungTexts(before)[0]);

        AcceptReplaceOfRungZero(session);

        var after = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", after.GetProperty("source").GetString());
        Assert.Equal([NewRung, "XIC(C)OTE(B);", "XIC(A)OTE(D);"], RungTexts(after));
        AssertNotStale(after);
    }

    [Fact]
    public void AfterAppliedAccept_WithoutAnEarlierRead_GetRoutineStillFollowsBecauseTheConnectedUploadIsTheBaseline()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);

        var after = Json(GetRoutineHandler.Execute(service, "mainprogram", "MAIN", session));
        Assert.Equal("session-image", after.GetProperty("source").GetString());
        Assert.Equal(NewRung, RungTexts(after)[0]);
    }

    [Fact]
    public void AfterAppliedAccept_RungDiagramIsDrawnFromTheSessionImage()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);

        var drawn = Json(RungDiagramHandler.Execute(service, "MainProgram", "Main", "0", "text", session));
        Assert.Equal("session-image", drawn.GetProperty("source").GetString());
        Assert.Contains("E", drawn.GetProperty("text").GetString());
        Assert.Equal("project-file",
            Json(RungDiagramHandler.Execute(service, "MainProgram", "Main", "0", "text")).GetProperty("source").GetString());
    }

    [Fact]
    public void ProjectSummary_AfterAppliedAccept_NamesTheChangedRoutinesInAppliedEdits()
    {
        var (session, service) = Connect();
        Assert.False(Json(ProjectSummaryHandler.Execute(service, session)).TryGetProperty("appliedEdits", out _));

        AcceptReplaceOfRungZero(session);

        var summary = Json(ProjectSummaryHandler.Execute(service, session));
        Assert.Equal(["MainProgram/Main"], summary.GetProperty("appliedEdits").EnumerateArray().Select(e => e.GetString()!).ToArray());
        AssertNotStale(summary);
    }

    [Fact]
    public void WithTheImageStale_GetRoutineSummaryFindTagsAndCrossReferenceEachCarryImageStaleAndTheHint()
    {
        var (session, service) = Connect();
        SessionImageState.MarkStale(session, "accepted pending edits that this session did not publish");

        AssertStale(Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session)), "project-file");
        AssertStale(Json(ProjectSummaryHandler.Execute(service, session)), "project-file");
        AssertStale(Json(FindTagsHandler.Execute(service, "*", session: session)), "project-file");
        AssertStale(Json(CrossReferenceHandler.Execute(service, "A", session)), "project-file");
        AssertStale(Json(RungDiagramHandler.Execute(service, "MainProgram", "Main", "0", "text", session)), "project-file");
        AssertStale(Json(TraceOriginHandler.Execute(service, "B", session: session)), "project-file");
        AssertStale(Json(ListDatatypesHandler.Execute(service, session: session)), "project-file");
        AssertStale(Json(ListModulesHandler.Execute(service, session)), "project-file");
    }

    [Fact]
    public void WithTheImageStale_AnAppliedRoutineServedFromTheImageIsFlaggedToo()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);
        SessionImageState.MarkStale(session, "a later accept could not be applied");

        AssertStale(Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session)), "session-image");
    }

    [Fact]
    public void WithNothingChanged_TheResponsesCarryNoStaleFieldsAndMatchTheCallsWithoutASession()
    {
        var (session, service) = Connect();

        var withSession = new object[]
        {
            GetRoutineHandler.Execute(service, "MainProgram", "Main", session),
            ProjectSummaryHandler.Execute(service, session),
            FindTagsHandler.Execute(service, "*", session: session),
            CrossReferenceHandler.Execute(service, "A", session),
            RungDiagramHandler.Execute(service, "MainProgram", "Main", "0", "text", session),
            TraceOriginHandler.Execute(service, "B", session: session),
            ListDatatypesHandler.Execute(service, session: session),
            ListModulesHandler.Execute(service, session),
        };
        var without = new object[]
        {
            GetRoutineHandler.Execute(service, "MainProgram", "Main"),
            ProjectSummaryHandler.Execute(service),
            FindTagsHandler.Execute(service, "*"),
            CrossReferenceHandler.Execute(service, "A"),
            RungDiagramHandler.Execute(service, "MainProgram", "Main", "0", "text"),
            TraceOriginHandler.Execute(service, "B"),
            ListDatatypesHandler.Execute(service),
            ListModulesHandler.Execute(service),
        };

        for (var i = 0; i < withSession.Length; i++)
        {
            var root = Json(withSession[i]);
            AssertNotStale(root);
            Assert.False(root.TryGetProperty("appliedEdits", out _));
            Assert.Equal("project-file", root.GetProperty("source").GetString());
            Assert.Equal(Json(without[i]).GetRawText(), root.GetRawText());
        }
    }

    [Fact]
    public void AReconnect_ClearsTheStaleFieldsAndTheAppliedEdits()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);
        SessionImageState.MarkStale(session, "a later accept could not be applied");
        Assert.True(Json(ProjectSummaryHandler.Execute(service, session)).TryGetProperty("appliedEdits", out _));

        // logix_connect again: a new service, a fresh image of the controller, and the upload registered as the project.
        var fresh = SessionImageAfterAcceptTests.ImageOf("XIC(A)OTE(B);", "XIC(C)OTE(B);", "XIC(A)OTE(D);", "XIC(D)OTE(E);");
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: false);
        session.SetControllerImage(fresh, DateTimeOffset.UtcNow);
        service.RegisterFromUpload(fresh, "C.ACD", "1.2.3.4");

        var routine = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("project-file", routine.GetProperty("source").GetString());
        Assert.Equal(4, RungTexts(routine).Length);
        AssertNotStale(routine);
        var summary = Json(ProjectSummaryHandler.Execute(service, session));
        AssertNotStale(summary);
        Assert.False(summary.TryGetProperty("appliedEdits", out _));
        Assert.Empty(ProjectFileStaleness.AppliedEdits(session, service));
    }

    [Fact]
    public void AnAcceptWhoseRefreshFails_FlagsTheImageStaleOnGetRoutineAndTheSummary()
    {
        var (session, service) = Connect();
        session.ImageSourceOverride = new FailingExport("export refused");
        session.MarkImageStale("MainProgram");
        Assert.False(session.RefreshControllerImageAsync().GetAwaiter().GetResult());

        var routine = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        AssertStale(routine, "project-file");
        AssertStale(Json(ProjectSummaryHandler.Execute(service, session)), "project-file");
        Assert.Equal("XIC(A)OTE(B);", RungTexts(routine)[0]);
        Assert.Contains("refresh of program 'MainProgram' failed: export refused", routine.GetProperty("staleReason").GetString());
    }

    [Fact]
    public void AnUnknownRoutineAfterAnAppliedAccept_IsStillAnError()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);

        var unknown = GetRoutineHandler.Execute(service, "MainProgram", "Other", session);
        Assert.IsType<GetRoutineError>(unknown);
    }

    private sealed class FailingExport(string message) : IControllerImageSource
    {
        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidOperationException(message));
        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) =>
            Task.FromException<string>(new InvalidOperationException(message));
    }

    /// <summary>A controller that exports whatever image it holds (here: the post-stage export, the staged rung as plain N).</summary>
    private sealed class ExportOf(L5X export) : IControllerImageSource
    {
        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default) => Task.FromResult(export.ToString());
        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) => Task.FromResult(export.ToString());
    }

    private static string[] TypesOf(TagSessionService session) =>
        SafeXml.ParseDocument(session.ControllerImage!.ToString()).Descendants("Rung")
            .Select(r => (string?)r.Attribute("Type") ?? "N").ToArray();

    /// <summary>The Z2 shape: connect, stage a replace of rung 0, then a refresh (what lint forces) with the export showing the staged rung as N.</summary>
    private static async Task<(TagSessionService Session, ProjectService Service)> StageThenRefresh(L5X export)
    {
        var image   = SessionImageAfterAcceptTests.ImageOf("XIC(A)OTE(B);");
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        // The workstation-staged block (replace of rung 0) is what the adapter's staged buffer reports.
        session.ImageSourceOverride = new ExportWithStaged(export, new StagedBlock("MainProgram", "Main", 0, 1, [NewRung]));
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");

        // Baseline read before the stage, as a project-file read would take it.
        Assert.Equal("project-file", Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session)).GetProperty("source").GetString());

        session.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(session);
        Assert.False(session.IsImageStale);
        return (session, service);
    }

    [Fact]
    public async Task StageAReplaceThenLint_GetRoutineIsStillProjectFileWithOneRung_AndAfterAcceptItIsSessionImageWithTheNewRung()
    {
        // The export shows the new rung and the original it replaces, both as plain N.
        var export = SessionImageAfterAcceptTests.ImageOf(NewRung, "XIC(A)OTE(B);");
        var (session, service) = await StageThenRefresh(export);

        Assert.Equal(["IR", "R"], TypesOf(session));
        var staged = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("project-file", staged.GetProperty("source").GetString());
        Assert.Equal(["XIC(A)OTE(B);"], RungTexts(staged));

        // Publish and accept: the controller exports just the new rung, and the refresh installs it.
        session.ImageSourceOverride = new ExportOf(SessionImageAfterAcceptTests.ImageOf(NewRung));
        session.MarkImageStale("MainProgram");
        Assert.True(await session.RefreshControllerImageAsync());
        Assert.Null(SessionImageState.StalenessOf(session));

        var accepted = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", accepted.GetProperty("source").GetString());
        Assert.Equal([NewRung], RungTexts(accepted));
        AssertNotStale(accepted);
    }

    /// <summary>An export source that also reports the staged (unpublished) block, as the SDK adapter's staged-rung buffer does.</summary>
    private sealed class ExportWithStaged(L5X export, params StagedBlock[] staged) : IControllerImageSource, IStagedRungSource
    {
        public List<StagedBlock> Blocks { get; } = [.. staged];
        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default) => Task.FromResult(export.ToString());
        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) => Task.FromResult(export.ToString());
        public IReadOnlyList<StagedBlock> StagedBlocks(string program) => Blocks;
    }

#if LOGIX_SDK
    /// <summary>The production image source must supply the staged blocks, or the refresh fix is inert on a real session.</summary>
    [Fact]
    public async Task TheSdkTagService_IsAStagedRungSource_AndReportsTheSessionsStagedBuffer()
    {
        var (session, _) = await PendingEditsTestHelpers.OpenedSessionAsync();
        var service = new TypedSdkTagService(session, L5X.Parse("<RSLogix5000Content><Controller Name=\"C\"/></RSLogix5000Content>"));
        Assert.IsAssignableFrom<IStagedRungSource>(service);
        var source = (IStagedRungSource)service;
        Assert.Empty(source.StagedBlocks("MainProgram"));

        var xPath = PendingEditsTestHelpers.MainRoutineXPath;
        session.StagedRungBuffer[(xPath, 2u)] = [ProposeRungEditHandler.NormalizeRungText("XIC(A)OTE(B);")];
        session.StagedReplaceCounts[(xPath, 2u)] = 1;
        session.StagedRungBuffer[(PendingEditsTestHelpers.MainRoutineXPath.Replace("MainProgram", "Other"), 0u)] =
            [ProposeRungEditHandler.NormalizeRungText("NOP();")];

        var block = Assert.Single(source.StagedBlocks("MainProgram"));
        Assert.Equal("MainRoutine", block.Routine);
        Assert.Equal(2, block.InsertPosition);
        Assert.Equal(1, block.ReplaceCount);
        Assert.Equal([ProposeRungEditHandler.NormalizeRungText("XIC(A)OTE(B);")], block.Rungs);
        Assert.Empty(block.KeptNormal);

        session.StagedKeptNormal[(xPath, 2u)] = [0];
        Assert.Equal([0], Assert.Single(source.StagedBlocks("MainProgram")).KeptNormal.ToArray());
    }
#endif

    private static string[] AcceptedTexts(TagSessionService session) =>
        SafeXml.ParseDocument(ControllerImageRefresh.Accepted(session.ControllerImage!).ToString()).Descendants("Rung")
            .Select(r => r.Element("Text")!.Value).ToArray();

    [Fact]
    public async Task StageAReplaceThenRefreshThenPublish_GetRoutineStaysProjectFile_AndTheAcceptedViewIsTheBaseline()
    {
        // Baseline: one rung that drives B. The staged replacement also drives B, so a doubled accepted view would
        // show two writers (DuplicateDestructiveOutput).
        const string Old = "XIC(A)OTE(B);";
        const string New = "XIC(A)XIC(C)OTE(B);";
        var image   = SessionImageAfterAcceptTests.ImageOf(Old);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        var baseline = ControllerImageRefresh.Accepted(image).ToString();
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");
        Assert.Equal("project-file", Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session)).GetProperty("source").GetString());

        // Stage a replace of rung 0: the export shows the staged rung and the original it replaces, both plain N.
        var source = new ExportWithStaged(SessionImageAfterAcceptTests.ImageOf(New, Old),
            new StagedBlock("MainProgram", "Main", 0, 1, [New]));
        session.ImageSourceOverride = source;
        session.MarkImageStale("MainProgram");

        // Lint forces the refresh in production (it prepares the image first); here the refresh is run directly.
        await AnalysisImage.PrepareAsync(session);
        Assert.Equal(["IR", "R"], TypesOf(session));
        Assert.Equal([Old], AcceptedTexts(session));

        // Publish the staged replace: the buffer empties and the controller's export holds [IR, R] (shape a).
        source.Blocks.Clear();
        session.ImageSourceOverride = new ExportOf(SessionImageAfterAcceptTests.ImageOfTyped((New, "IR"), (Old, "R")));
        session.MarkImageStale("MainProgram");
        Assert.True(await session.RefreshControllerImageAsync());
        Assert.Equal(["IR", "R"], TypesOf(session));

        // Invariant: after a refresh and a publish the accepted view equals the pre-stage baseline, rung for rung.
        Assert.Equal(baseline, ControllerImageRefresh.Accepted(session.ControllerImage!).ToString());
        var published = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("project-file", published.GetProperty("source").GetString());
        Assert.Equal([Old], RungTexts(published));

        // Nothing doubled: one writer of B in the accepted view, no DuplicateDestructiveOutput on either view.
        var refs = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: false));
        Assert.Single(refs.References, r => r.Kind == "write");
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));

        // The session's own accept: session-image, with the one new rung.
        session.ImageSourceOverride = new ExportOf(SessionImageAfterAcceptTests.ImageOf(New));
        session.MarkImageStale("MainProgram");
        Assert.True(await session.RefreshControllerImageAsync());
        Assert.Null(SessionImageState.StalenessOf(session));
        var accepted = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("session-image", accepted.GetProperty("source").GetString());
        Assert.Equal([New], RungTexts(accepted));
        AssertNotStale(accepted);
        var afterRefs = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: false));
        Assert.Single(afterRefs.References, r => r.Kind == "write");
    }

    [Fact]
    public async Task IdenticalTextStage_ThenRefresh_GetRoutineStaysProjectFile_AndLintAndXrefSeeOneRungAndOneWriter()
    {
        const string Old = "XIC(A)OTE(B);";
        var image   = SessionImageAfterAcceptTests.ImageOf(Old);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        var service = new ProjectService();
        service.RegisterFromUpload(image, "C.ACD", "1.2.3.4");

        session.ImageSourceOverride = new ExportWithStaged(SessionImageAfterAcceptTests.ImageOf(Old),
            new StagedBlock("MainProgram", "Main", 0, 1, [Old], KeptNormal: new HashSet<int> { 0 }));
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(["N"], TypesOf(session));
        Assert.Equal([Old], AcceptedTexts(session));
        var routine = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        Assert.Equal("project-file", routine.GetProperty("source").GetString());
        Assert.Equal([Old], RungTexts(routine));
        Assert.Equal(0, SessionImageAfterAcceptTests.DuplicateOutputs(session, service));
        var refs = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(service, "B", session, includePending: true));
        Assert.Single(refs.References, r => r.Kind == "write");
    }

    [Fact]
    public async Task ARefreshWithAStagedBlock_LeavesTheAcceptedViewAtTheBaselineBeforeAnyPublish()
    {
        const string Old = "XIC(A)OTE(B);";
        var image   = SessionImageAfterAcceptTests.ImageOf(Old);
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow.AddMinutes(-5));
        session.ImageSourceOverride = new ExportWithStaged(SessionImageAfterAcceptTests.ImageOf(NewRung, Old),
            new StagedBlock("MainProgram", "Main", 0, 1, [NewRung]));
        session.MarkImageStale("MainProgram");

        await AnalysisImage.PrepareAsync(session);

        Assert.Equal(["IR", "R"], TypesOf(session));
        Assert.Equal([Old], AcceptedTexts(session));
    }

    [Fact]
    public async Task ARefresh_LeavesARungThatMatchesNoRecordAsExported()
    {
        var export = SessionImageAfterAcceptTests.ImageOf("XIC(Z)OTE(Y);", "XIC(A)OTE(B);");
        var (session, _) = await StageThenRefresh(export);

        // The recorded rung is not in the export, so nothing is stamped (not even the original it replaced): left as exported.
        Assert.Equal(["N", "N"], TypesOf(session));
    }

    [Fact]
    public async Task ARefresh_WhoseExportKeepsTheZoneTypeCodes_IsNotChanged()
    {
        var export = SessionImageAfterAcceptTests.ImageOf(NewRung, "XIC(A)OTE(B);");
        var pending = L5X.Parse(export.ToString().Replace("Type=\"N\"", "Type=\"IR\"").Replace("Number=\"1\" Type=\"IR\"", "Number=\"1\" Type=\"R\""));
        var (session, _) = await StageThenRefresh(pending);

        Assert.Equal(["IR", "R"], TypesOf(session));
    }
}
