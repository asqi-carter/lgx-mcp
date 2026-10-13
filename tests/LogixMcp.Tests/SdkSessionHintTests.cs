#if LOGIX_SDK
using System.Text.Json;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The "No active SDK session" error must explain that a server restart
/// drops sessions and staged edits.
/// </summary>
public class SdkSessionHintTests
{
    private const string XPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

    private const string ExpectedHint =
        "SDK sessions live in this server process; if your MCP client restarted the server " +
        "(new conversation turn or client restart), call logix_connect again. " +
        "Staged edits from the earlier process are gone.";

    private static LogixSdkTools NewTools() =>
        new(new ProjectService(), new TagSessionService());

    private static void AssertHint(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Contains("No active SDK session", root.GetProperty("error").GetString());
        Assert.Equal(ExpectedHint, root.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task NoActiveSession_HintExplainsServerRestart()
    {
        var tools = NewTools();
        AssertHint(await tools.PendingEdits("MainProgram", "MainRoutine"));
        AssertHint(await tools.ExportRungs(XPath));
        AssertHint(await tools.ProposeRungEdit(
            XPath, 0, 0, null, offline: false, target: "workstation"));
    }
}
#endif
