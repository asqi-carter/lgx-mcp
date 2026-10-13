using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Tests that <c>logix_cross_reference</c> and <c>logix_verify_interlock</c> read the latest controller image
/// (a re-upload replaces the model; <c>includePending</c> reads the session image).
/// </summary>
public class ControllerImageAnalysisTests
{
    private const string Tag = "<Tag Name=\"{0}\" TagType=\"Base\" DataType=\"BOOL\" Radix=\"Decimal\" Constant=\"false\" ExternalAccess=\"Read/Write\">" +
                               "<Data Format=\"Decorated\"><DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

    /// <summary>Builds a one-program L5X with the given controller tags and rung texts in MainProgram/Main.</summary>
    private static L5X Image(string[] tags, params string[] rungs)
    {
        var tagXml  = string.Concat(tags.Select(t => string.Format(Tag, t)));
        var rungXml = string.Concat(rungs.Select((r, i) =>
            $"<Rung Number=\"{i}\" Type=\"N\"><Text><![CDATA[{r}]]></Text></Rung>"));
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

    private static TagSessionService OnlineSession(L5X image)
    {
        var session = new TagSessionService();
        session.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: false, isLiveController: true);
        session.SetControllerImage(image, DateTimeOffset.UtcNow);
        return session;
    }

    private static string WriteTempL5x(L5X image)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lgx-img-{Guid.NewGuid():N}.L5X");
        image.Save(path);
        return path;
    }

    [Fact]
    public void TwoUploadSourcedLoads_ThenXrefSeesTheNewRung()
    {
        var svc = new ProjectService();
        svc.RegisterFromUpload(Image(["A", "B"], "XIC(A)OTE(B);"), "/tmp/one.ACD", "comm");
        var first = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A"));
        Assert.Single(first.References);

        svc.RegisterFromUpload(Image(["A", "B"], "XIC(A)OTE(B);", "XIC(A)OTE(B);"), "/tmp/two.ACD", "comm");

        var second = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A"));
        Assert.Equal(2, second.References.Count);
        Assert.Contains(second.References, r => r.RungNumber == 1);
        Assert.Equal("/tmp/two.ACD", svc.FilePath);
        Assert.False(svc.OpenedExplicitly);
    }

    [Fact]
    public void ExplicitlyOpenedFile_IsNotReplacedByUploadLoads()
    {
        var path = WriteTempL5x(Image(["A", "B"], "XIC(A)OTE(B);"));
        try
        {
            var svc = new ProjectService();
            svc.Load(path);
            var opened = svc.Project;

            svc.RegisterFromUpload(Image(["A", "B"], "XIC(A)OTE(B);", "XIC(A)OTE(B);"), "/tmp/up.ACD", "comm");

            Assert.Same(opened, svc.Project);
            Assert.Equal(path, svc.FilePath);
            Assert.True(svc.OpenedExplicitly);
            Assert.Single(Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A")).References);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IncludePending_ReadsSessionImage_DefaultingToTrueOnSdkOnlineSessions()
    {
        var svc = new ProjectService();
        svc.RegisterFromUpload(Image(["A", "B"], "XIC(A)OTE(B);"), "/tmp/old.ACD", "comm");
        var session = OnlineSession(Image(["A", "B"], "XIC(A)OTE(B);", "XIC(A)OTE(B);"));

        // Default on an SDK-online session: the session image (2 rungs), not the registered file (1 rung).
        var byDefault = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A", session));
        Assert.Equal(2, byDefault.References.Count);
        Assert.Equal("session-image", byDefault.Source);

        // includePending: false reads the controller's accepted image, not the upload-sourced file.
        var off = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A", session, includePending: false));
        Assert.Equal(2, off.References.Count);
        Assert.Equal("accepted-image", off.Source);

        // An explicitly opened file is read as-is.
        var path = WriteTempL5x(Image(["A", "B"], "XIC(A)OTE(B);"));
        try
        {
            var explicitSvc = new ProjectService();
            explicitSvc.Load(path);
            var file = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(explicitSvc, "A", session, includePending: false));
            Assert.Single(file.References);
            Assert.Equal("project-file", file.Source);
        }
        finally { File.Delete(path); }

        // Offline session: defaults to false, can be switched on.
        var offline = new TagSessionService();
        offline.Attach(new VirtualTagService(new Dictionary<string, (object, string?)>()), isVirtual: true);
        offline.SetControllerImage(Image(["A", "B"], "XIC(A)OTE(B);", "XIC(A)OTE(B);"), DateTimeOffset.UtcNow);
        Assert.Single(Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A", offline)).References);
        Assert.Equal(2, Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A", offline, includePending: true)).References.Count);

        // verify_interlock reads the same image: the second rung (unguarded) exists only in the session image.
        var guardedFile = Image(["Cond", "Out"], "XIC(Cond)OTE(Out);");
        var driftedImage = Image(["Cond", "Out"], "XIC(Cond)OTE(Out);", "OTE(Out);");
        var vpath = WriteTempL5x(guardedFile);
        try
        {
            var vsvc = new ProjectService();
            vsvc.Load(vpath);
            var vsession = OnlineSession(driftedImage);
            Assert.True(Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(vsvc, "Out", "Cond", vsession, includePending: false)).Verified);
            Assert.False(Assert.IsType<VerifyInterlockResult>(VerifyInterlockHandler.Execute(vsvc, "Out", "Cond", vsession)).Verified);
        }
        finally { File.Delete(vpath); }
    }

    [Fact]
    public void ProjectFileOlderThanController_NoteAppearsWhenTagIsOnControllerButNotInFile()
    {
        var path = WriteTempL5x(Image(["A", "B"], "XIC(A)OTE(B);"));
        var svc = new ProjectService();
        svc.Load(path);
        var session = OnlineSession(Image(["A", "B", "NewTag"], "XIC(A)OTE(B);", "XIC(NewTag)OTE(B);"));

        try
        {
        var xref = Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "NewTag", session, includePending: false));
        Assert.Empty(xref.References);
        Assert.Contains("project file is older than the controller", xref.Note);

        var interlock = Assert.IsType<VerifyInterlockResult>(
            VerifyInterlockHandler.Execute(svc, "B", "NewTag", session, includePending: false));
        Assert.Contains("project file is older than the controller", interlock.Note);

        // No note when the tag is in the file, or when the session image is the one being read.
        Assert.Null(Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "A", session, includePending: false)).Note);
        Assert.Null(Assert.IsType<CrossRefResult>(CrossReferenceHandler.Execute(svc, "NewTag", session, includePending: true)).Note);
        }
        finally { File.Delete(path); }
    }
}
