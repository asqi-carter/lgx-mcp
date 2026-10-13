#if LOGIX_SDK
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Every "no active session" error carries the machine-readable
/// <c>kind = "NoActiveSession"</c>, with message and hint unchanged.
/// </summary>
public class NoActiveSessionKindTests
{
    private const string XPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private static LogixSdkTools NewTools() =>
        new(new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true));

    private static async Task<string> RunAsync(string tool)
    {
        var tools = NewTools();
        switch (tool)
        {
            case "logix_export_rungs":
                return await tools.ExportRungs(XPath);
            case "logix_propose_rung_edit":
                return await tools.ProposeRungEdit(
                    XPath, 0, 0, null, offline: false, target: "workstation");
            case "logix_propose_rung_edit(no project loaded)":
                return await tools.GetProposeRungEditResponseAsync(
                    XPath, 0, 0, "<Rung/>", offline: false, target: "workstation");
            case "logix_pending_edits":
                return await tools.PendingEdits("MainProgram", "MainRoutine");
            case "logix_finalize_edits":
                return await tools.FinalizeEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_cancel_pending_edits":
                return await tools.CancelPendingEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_accept_pending_edits":
                return await tools.AcceptPendingEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_test_pending_edits":
                return await tools.TestPendingEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_untest_pending_edits":
                return await tools.UntestPendingEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_assemble_pending_edits":
                return await tools.AssemblePendingEdits("MainProgram", "MainRoutine", confirm: true);
            case "logix_read":
                return JsonSerializer.Serialize(
                    await ReadTagHandler.ExecuteAsync("Tag", new TagSessionService()));
            case "logix_write":
                return JsonSerializer.Serialize(
                    await WriteTagHandler.ExecuteAsync("Tag", "1", new TagSessionService()));
            case "logix_poll":
                return JsonSerializer.Serialize(
                    await PollTagsHandler.ExecuteAsync(new[] { "Tag" }, 1, 10, new TagSessionService()));
            case "logix_controller_mode":
                return JsonSerializer.Serialize(
                    await ControllerModeHandler.ExecuteAsync(
                        null, false, new TagSessionService(), new ControllerModeService()));
            default:
                throw new ArgumentOutOfRangeException(nameof(tool), tool, null);
        }
    }

    [Theory]
    [InlineData("logix_export_rungs")]
    [InlineData("logix_propose_rung_edit")]
    [InlineData("logix_propose_rung_edit(no project loaded)")]
    [InlineData("logix_pending_edits")]
    [InlineData("logix_finalize_edits")]
    [InlineData("logix_cancel_pending_edits")]
    [InlineData("logix_accept_pending_edits")]
    [InlineData("logix_test_pending_edits")]
    [InlineData("logix_untest_pending_edits")]
    [InlineData("logix_assemble_pending_edits")]
    [InlineData("logix_read")]
    [InlineData("logix_write")]
    [InlineData("logix_poll")]
    [InlineData("logix_controller_mode")]
    public async Task EveryNoSessionError_CarriesKindNoActiveSession(string tool)
    {
        var json = await RunAsync(tool);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Contains("o active", root.GetProperty("error").GetString());
        Assert.Equal("NoActiveSession", root.GetProperty("kind").GetString());
        Assert.Equal(ErrorKinds.NoActiveSession, root.GetProperty("kind").GetString());
    }

    [Fact]
    public void Kind_IsOmittedFromJson_WhenNull()
    {
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(new ExportRungsError("e")))
            .RootElement.TryGetProperty("kind", out _));
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(new PendingEditsError("e")))
            .RootElement.TryGetProperty("kind", out _));
    }
}
#endif
