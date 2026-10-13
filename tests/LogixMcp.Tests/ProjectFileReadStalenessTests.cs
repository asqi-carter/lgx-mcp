using System.Text.Json;
using L5Sharp.Core;

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

    private static readonly IReadOnlyList<SessionImageState.ZoneRoutine> Zone =
        [new SessionImageState.ZoneRoutine("Main", [("IR", NewRung)])];

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

    private static void AcceptReplaceOfRungZero(TagSessionService session)
    {
        Assert.True(SessionImageState.RecordPublish(session, "MainProgram", "Main", 0, 1, [NewRung]));
        SessionImageState.OnAccepted(session, "MainProgram", Zone, "accepted");
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
    public void AnAcceptTheSessionDidNotPublish_FlagsTheImageStaleOnGetRoutineAndTheSummary()
    {
        var (session, service) = Connect();

        SessionImageState.OnAccepted(session, "MainProgram", Zone, "accepted");

        var routine = Json(GetRoutineHandler.Execute(service, "MainProgram", "Main", session));
        AssertStale(routine, "project-file");
        AssertStale(Json(ProjectSummaryHandler.Execute(service, session)), "project-file");
        Assert.Equal("XIC(A)OTE(B);", RungTexts(routine)[0]);
    }

    [Fact]
    public void AnUnknownRoutineAfterAnAppliedAccept_IsStillAnError()
    {
        var (session, service) = Connect();
        AcceptReplaceOfRungZero(session);

        var unknown = GetRoutineHandler.Execute(service, "MainProgram", "Other", session);
        Assert.IsType<GetRoutineError>(unknown);
    }
}
