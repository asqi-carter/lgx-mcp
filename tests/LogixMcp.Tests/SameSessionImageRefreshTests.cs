using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Analysis (cross-reference, verify-interlock, lint) sees changes made in the same session
/// (publish, stage, cancel, created tag) without a reconnect, and never runs on a stale image without saying so.
/// </summary>
public class SameSessionImageRefreshTests
{
    private const string Tag = "<Tag Name=\"{0}\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
                               "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    /// <summary>Builds a one-program L5X; each rung is (text, Type code).</summary>
    private static L5X Image(string[] tags, params (string Text, string Type)[] rungs)
    {
        var tagXml  = string.Concat(tags.Select(t => string.Format(Tag, t)));
        var rungXml = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"{r.Type}\"><Text><![CDATA[{r.Text}]]></Text></Rung>"));
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

    private static L5X Image(string[] tags, params string[] rungs) =>
        Image(tags, rungs.Select(r => (r, "N")).ToArray());

    /// <summary>A controller that exports whatever image it currently holds.</summary>
    private sealed class FakeController(L5X controller) : IControllerImageSource
    {
        public L5X Controller { get; set; } = controller;
        public int ProgramExports { get; private set; }
        public int TagExports { get; private set; }
        public Exception? Fail { get; set; }

        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default)
        {
            ProgramExports++;
            return Fail is null ? Task.FromResult(Controller.ToString()) : Task.FromException<string>(Fail);
        }

        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default)
        {
            TagExports++;
            return Fail is null ? Task.FromResult(Controller.ToString()) : Task.FromException<string>(Fail);
        }
    }

    private static TagSessionService OnlineSession(L5X image, IControllerImageSource? source = null)
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow);
        session.ImageSourceOverride = source;
        return session;
    }

    private static CrossRefResult XRef(ProjectService svc, string tag, TagSessionService session, bool? includePending = null) =>
        Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, tag, session, includePending));

    [Fact]
    public async Task AfterPublish_AnalysisSeesNewRungAndTagInSameSession()
    {
        var connected = Image(["A", "B"], "XIC(A)OTE(B);");
        var published = Image(["A", "B", "NewTag"], "XIC(A)OTE(B);", "XIC(NewTag)OTE(B);");
        var controller = new FakeController(connected);
        var session = OnlineSession(connected, controller);
        var svc = new ProjectService();

        Assert.Empty(XRef(svc, "NewTag", session).References);

        // The publish adds a rung and a controller tag.
        controller.Controller = published;
        session.RecordLiveEdit("MainProgram/Main", ["NewTag"]);
        Assert.True(session.IsImageStale);

        await AnalysisImage.PrepareAsync(session);

        Assert.False(session.IsImageStale);
        Assert.Null(session.ImageStaleNote);
        var xref = XRef(svc, "NewTag", session);
        Assert.Single(xref.References);
        Assert.Equal(1, xref.References[0].RungNumber);
        Assert.Equal(2, XRef(svc, "B", session).References.Count);
        // One export of the affected program, one of the controller tags: not a full upload.
        Assert.Equal(1, controller.ProgramExports);
        Assert.Equal(1, controller.TagExports);
        Assert.NotNull(session.ControllerImageRefreshedAt);
    }

    [Fact]
    public async Task IncludePending_TrueReadsSessionImage_FalseReadsAcceptedControllerImage()
    {
        // The session image holds a staged insert ("e"); the accepted image does not.
        var staged = Image(["A", "B", "C"], ("XIC(A)OTE(B);", "N"), ("XIC(C)OTE(B);", "e"));
        var controller = new FakeController(staged);
        var session = OnlineSession(Image(["A", "B", "C"], "XIC(A)OTE(B);"), controller);
        var svc = new ProjectService();

        session.RecordLiveEdit("MainProgram/Main");   // a stage in this session
        await AnalysisImage.PrepareAsync(session);

        var withPending = XRef(svc, "C", session, includePending: true);
        Assert.Single(withPending.References);
        Assert.Equal("session-image", withPending.Source);

        var accepted = XRef(svc, "C", session, includePending: false);
        Assert.Empty(accepted.References);
        Assert.Equal("accepted-image", accepted.Source);
        Assert.Single(XRef(svc, "A", session, includePending: false).References);

        // Cancelling the stage: the next refresh drops the staged rung from the session image as well.
        controller.Controller = Image(["A", "B", "C"], "XIC(A)OTE(B);");
        session.MarkImageStale("MainProgram");
        await AnalysisImage.PrepareAsync(session);
        Assert.Empty(XRef(svc, "C", session, includePending: true).References);
    }

    [Fact]
    public async Task FileOlderNote_FiresForTagCreatedByPublishInSameSession()
    {
        var connected = Image(["A", "B"], "XIC(A)OTE(B);");
        var path = Path.Combine(Path.GetTempPath(), $"lgx-older-{Guid.NewGuid():N}.L5X");
        connected.Save(path);
        try
        {
        var svc = new ProjectService();
        svc.Load(path);

        var controller = new FakeController(Image(["A", "B", "NewTag"], "XIC(A)OTE(B);", "XIC(NewTag)OTE(B);"));
        var session = OnlineSession(connected, controller);
        session.RecordLiveEdit("MainProgram/Main", ["NewTag"]);
        await AnalysisImage.PrepareAsync(session);

        var xref = XRef(svc, "NewTag", session, includePending: false);
        Assert.Empty(xref.References);   // the file predates the publish
        Assert.Contains("project file is older than the controller", xref.Note);
        Assert.Contains("project file is older than the controller",
            Assert.IsType<VerifyInterlockResult>(
                VerifyInterlockHandler.Execute(svc, "B", "NewTag", session, includePending: false)).Note);
        Assert.Null(XRef(svc, "NewTag", session, includePending: true).Note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task IncludePendingFalse_AfterPublishInSameSession_SeesPublishedRung_NoteOnlyOnFileFallback()
    {
        var connected = Image(["A", "B"], "XIC(A)OTE(B);");
        var published = Image(["A", "B", "NewTag"], "XIC(A)OTE(B);", "XIC(NewTag)OTE(B);");
        var controller = new FakeController(published);

        // Upload-sourced file (the connect-time snapshot): includePending:false reads the accepted image after the refresh.
        var svc = new ProjectService();
        svc.RegisterFromUpload(connected, "/tmp/connect.ACD", "comm");
        var session = OnlineSession(connected, controller);
        session.RecordLiveEdit("MainProgram/Main", ["NewTag"]);
        await AnalysisImage.PrepareAsync(session);

        var accepted = XRef(svc, "NewTag", session, includePending: false);
        Assert.Single(accepted.References);
        Assert.Equal("accepted-image", accepted.Source);
        Assert.Null(accepted.Note);

        // Explicitly opened file: the analysis falls back to the file, so the note fires.
        var path = Path.Combine(Path.GetTempPath(), $"lgx-fallback-{Guid.NewGuid():N}.L5X");
        connected.Save(path);
        try
        {
            var fileSvc = new ProjectService();
            fileSvc.Load(path);
            var fallback = XRef(fileSvc, "NewTag", session, includePending: false);
            Assert.Equal("project-file", fallback.Source);
            Assert.Empty(fallback.References);
            Assert.Contains("project file is older than the controller", fallback.Note);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StaleImage_IsNeverUsedWithoutSayingSo()
    {
        var image = Image(["A", "B"], "XIC(A)OTE(B);");
        var svc = new ProjectService();

        // No way to export: the refresh cannot run.
        var noSource = OnlineSession(image);
        noSource.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(noSource);
        Assert.True(noSource.IsImageStale);
        Assert.Contains("controller image is stale", XRef(svc, "A", noSource).Note);

        // The export fails: the image is kept as it was, and the result says it is stale.
        var failing = new FakeController(image) { Fail = new InvalidOperationException("export refused") };
        var failed = OnlineSession(image, failing);
        failed.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(failed);
        Assert.True(failed.IsImageStale);
        Assert.Same(image, failed.ControllerImage);
        var note = XRef(svc, "A", failed).Note;
        Assert.Contains("controller image is stale", note);
        Assert.Contains("export refused", note);
        Assert.Contains("controller image is stale",
            Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(svc, "B", "A", failed)).Note);
        Assert.Contains("controller image is stale",
            Assert.IsType<LintResult>(LintHandler.Execute(svc, session: failed)).Note);

        // Once the export works the note goes away.
        failing.Fail = null;
        await AnalysisImage.PrepareAsync(failed);
        Assert.False(failed.IsImageStale);
        Assert.Null(XRef(svc, "A", failed).Note);
    }

    /// <summary>
    /// G14: a published rung is pending ('I': accepted, not assembled), so includePending:false leaves it out. The result
    /// must say a pending writer exists rather than claim the tag is unwritten; includePending:true finds the writer.
    /// </summary>
    [Fact]
    public async Task VerifyInterlock_IncludePendingFalse_AfterPublishInSameSession_FindsTheWriter()
    {
        string[] tags = ["Safety_OK", "Jog_Cmd", "Jog_Out"];
        var connected = Image(tags, "XIC(Jog_Cmd)OTE(Other);");
        var controller = new FakeController(connected);
        var svc = new ProjectService();
        svc.RegisterFromUpload(connected, "/tmp/connect.ACD", "comm");
        var session = OnlineSession(connected, controller);

        // No pending writer: the wording is unchanged and nothing is listed.
        var none = Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(svc, "Jog_Out", "Safety_OK", session, includePending: false));
        Assert.Equal("Output tag 'Jog_Out' is not written by any rung in the project.", none.Error);
        Assert.Null(none.PendingWritersExcluded);

        // The publish leaves the new rung pending ('I'), not assembled.
        controller.Controller = Image(tags, ("XIC(Jog_Cmd)OTE(Other);", "N"), ("XIC(Safety_OK)XIC(Jog_Cmd)OTE(Jog_Out);", "I"));
        session.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(session);

        var excluded = Assert.IsType<VerifyInterlockError>(
            VerifyInterlockHandler.Execute(svc, "Jog_Out", "Safety_OK", session, includePending: false));
        Assert.Contains("not written by any accepted rung; 1 pending rung (Main rung 1) writes it. Pass includePending:true to include it.", excluded.Error);
        Assert.DoesNotContain("not written by any rung in the project", excluded.Error);
        var writer = Assert.Single(excluded.PendingWritersExcluded!);
        Assert.Equal(("Main", 1, "Jog_Out"), (writer.Routine, writer.RungIndex, writer.Tag));

        var included = Assert.IsType<VerifyInterlockResult>(
            VerifyInterlockHandler.Execute(svc, "Jog_Out", "Safety_OK", session, includePending: true));
        Assert.True(included.Verified);
        Assert.Null(included.PendingWritersExcluded);
    }

    [Fact]
    public async Task CrossReference_IncludePendingFalse_ReportsPendingWriter_IncludePendingTrueFindsIt()
    {
        string[] tags = ["Safety_OK", "Jog_Cmd", "Jog_Out"];
        var connected = Image(tags, "XIC(Jog_Cmd)OTE(Other);");
        var controller = new FakeController(Image(tags, ("XIC(Jog_Cmd)OTE(Other);", "N"), ("XIC(Safety_OK)OTE(Jog_Out);", "I")));
        var svc = new ProjectService();
        svc.RegisterFromUpload(connected, "/tmp/connect.ACD", "comm");
        var session = OnlineSession(connected, controller);
        session.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(session);

        var excluded = XRef(svc, "Jog_Out", session, includePending: false);
        Assert.Empty(excluded.References);
        Assert.Contains("not written by any accepted rung; 1 pending rung (Main rung 1) writes it. Pass includePending:true to include it.", excluded.Note);
        Assert.Single(excluded.PendingWritersExcluded!);

        var included = XRef(svc, "Jog_Out", session, includePending: true);
        Assert.Single(included.References);
        Assert.Null(included.PendingWritersExcluded);
        Assert.Null(included.Note);

        // A tag with no pending writer keeps its wording.
        Assert.Null(XRef(svc, "Jog_Cmd", session, includePending: false).PendingWritersExcluded);
        Assert.Null(XRef(svc, "Jog_Cmd", session, includePending: false).Note);
    }

    [Fact]
    public async Task VerifyInterlock_IsVerifiedAfterJogOutPublish_WithNoReconnect()
    {
        string[] tags = ["Safety_OK", "Jog_Cmd", "Jog_Out"];
        var connected = Image(tags, "XIC(Jog_Cmd)OTE(Other);");
        var controller = new FakeController(connected);
        var session = OnlineSession(connected, controller);
        var svc = new ProjectService();

        Assert.IsType<VerifyInterlockError>(VerifyInterlockHandler.Execute(svc, "Jog_Out", "Safety_OK", session));

        // Publish the Jog_Out rung, guarded by Safety_OK.
        controller.Controller = Image(tags, "XIC(Jog_Cmd)OTE(Other);", "XIC(Safety_OK)XIC(Jog_Cmd)OTE(Jog_Out);");
        session.RecordLiveEdit("MainProgram/Main");
        await AnalysisImage.PrepareAsync(session);

        var result = Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(svc, "Jog_Out", "Safety_OK", session));
        Assert.True(result.Verified);
    }

    [Fact]
    public void Accepted_DropsStagedRungsAndRestoresDisplacedOriginals()
    {
        var session = Image(["A", "B"], ("XIC(A)OTE(B);", "N"), ("OTE(B);", "e"), ("XIC(B)OTE(A);", "rN"));
        var accepted = ControllerImageRefresh.Accepted(session);
        var rungs = accepted.Programs.Single().Routines.Single().Rungs.ToList();
        Assert.Equal(2, rungs.Count);
        Assert.Equal("XIC(B)OTE(A);", rungs[1].Text.ToString());
        Assert.Equal(1, (int)rungs[1].Number);

        var plain = Image(["A", "B"], "XIC(A)OTE(B);");
        Assert.Same(plain, ControllerImageRefresh.Accepted(plain));
    }
}
