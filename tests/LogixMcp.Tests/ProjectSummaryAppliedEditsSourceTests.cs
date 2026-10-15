using System.Text.Json;
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// A project summary that lists <c>appliedEdits</c> labels them <c>appliedEditsSource:"session-image"</c>,
/// while <c>source</c> stays <c>"project-file"</c>; with no applied edits the response is unchanged.
/// </summary>
public class ProjectSummaryAppliedEditsSourceTests
{
    private const string NewRung = "XIC(A)OTE(E);";

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

    private sealed class ExportOf(L5X export) : IControllerImageSource
    {
        public Task<string> ExportProgramL5xAsync(string program, CancellationToken ct = default) => Task.FromResult(export.ToString());
        public Task<string> ExportControllerTagsL5xAsync(CancellationToken ct = default) => Task.FromResult(export.ToString());
    }

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result, result.GetType());

    [Fact]
    public async Task NonEmptyAppliedEdits_AddsAppliedEditsSourceSessionImage_AndKeepsSourceProjectFile()
    {
        var (session, service) = Connect();
        // A publish then accept: the controller now exports the new rung as ordinary logic and the refresh installs it.
        session.ImageSourceOverride = new ExportOf(SessionImageAfterAcceptTests.ImageOf(NewRung, "XIC(C)OTE(B);", "XIC(A)OTE(D);"));
        session.MarkImageStale("MainProgram");
        Assert.True(await session.RefreshControllerImageAsync());

        var summary = Json(ProjectSummaryHandler.Execute(service, session));

        Assert.Equal(1, summary.GetProperty("appliedEdits").GetArrayLength());
        Assert.Equal("session-image", summary.GetProperty("appliedEditsSource").GetString());
        Assert.Equal("project-file", summary.GetProperty("source").GetString());
    }

    [Fact]
    public void EmptyAppliedEdits_OmitsAppliedEditsSource_AndKeepsSourceProjectFile()
    {
        var (session, service) = Connect();

        var summary = Json(ProjectSummaryHandler.Execute(service, session));

        Assert.False(summary.TryGetProperty("appliedEdits", out _));
        Assert.False(summary.TryGetProperty("appliedEditsSource", out _));
        Assert.Equal("project-file", summary.GetProperty("source").GetString());
    }

    [Fact]
    public void CreatedRoutine_IsCountedAndListedWithCreatedThisSession_AndSourceNamesTheImage()
    {
        var (session, service) = ProjectFileReadStalenessTests.ConnectThenCreateRoutine();

        var summary = Json(ProjectSummaryHandler.Execute(service, session));

        Assert.Equal("session-image", summary.GetProperty("source").GetString());
        var program = summary.GetProperty("programs").EnumerateArray().Single();
        Assert.Equal(2, program.GetProperty("routines").GetInt32());
        var list = program.GetProperty("routineList").EnumerateArray().ToList();
        var created = list.Single(r => r.GetProperty("name").GetString() == "R_New");
        Assert.True(created.GetProperty("createdThisSession").GetBoolean());
        Assert.False(list.Single(r => r.GetProperty("name").GetString() == "Main").TryGetProperty("createdThisSession", out _));
    }
}
