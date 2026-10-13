using System.Text.Json;
using System.Text.RegularExpressions;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Fail-closed capability gate tests.
///
/// <para>
/// A <c>null</c> <see cref="Capabilities"/> object must never relax a gate.
/// Every gate is written as <c>is not { Flag: true }</c> so that null refuses;
/// a <c>null</c> publish mode is treated as <see cref="PublishMode.Confirm"/>.
/// </para>
/// </summary>
public class CapabilityGateTests
{
    private const string ValidXPath =
        "Controller/Programs/Program[@Name='MainProgram']/Routines/Routine[@Name='MainRoutine']";

#if LOGIX_SDK
    /// <summary>
    /// With null capabilities a controller-target publish is refused with
    /// <c>CapabilityDisabled</c> before any session work.
    /// </summary>
    [Fact]
    public async Task NullCapabilities_RefusesControllerPublish()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities: null);

        var json = await tools.GetProposeRungEditResponseAsync(
            ValidXPath, 0, 0, null,
            session: null, offline: false, target: "controller", confirmedByUser: true);

        Assert.Equal("CapabilityDisabled",
            JsonDocument.Parse(json).RootElement.GetProperty("kind").GetString());
    }

    /// <summary>
    /// With null capabilities, <c>logix_cancel_pending_edits</c> is refused
    /// (Studio automation is not enabled) before any session work.
    /// </summary>
    [Fact]
    public async Task NullCapabilities_RefusesCancelPendingEdits()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities: null);

        var json = await tools.GetCancelPendingEditsResponseAsync("MainProgram", "MainRoutine", confirm: true);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("CapabilityDisabled", root.GetProperty("kind").GetString());
    }

    /// <summary>
    /// With null capabilities the tool wrapper must treat publish mode as
    /// Confirm: with no session and no confirmation it must reach the gates
    /// (a <c>CapabilityDisabled</c> refusal) rather than short-circuit with
    /// "No active session", and offline-style responses report "confirm".
    /// </summary>
    [Fact]
    public async Task NullCapabilities_TreatsPublishModeAsConfirm()
    {
        var tools = new LogixSdkTools(new ProjectService(), new TagSessionService(), capabilities: null);

        // Gate reached even with no session: the Confirm-gate bypass for the
        // missing-session check must also apply to null capabilities.
        var json = await tools.ProposeRungEdit(
            ValidXPath, 0, 0, null, offline: false, target: "controller", confirmedByUser: false);

        var root = JsonDocument.Parse(json).RootElement;
        Assert.True(root.TryGetProperty("kind", out var kind),
            $"Expected a structured refusal, got: {json}");
        Assert.NotEqual("No active SDK session", kind.GetString());
        Assert.DoesNotContain("No active SDK session", json, StringComparison.Ordinal);

        // And with Auto explicitly configured, no-session still short-circuits
        // (proves the null case is not being treated as Auto).
        var autoTools = new LogixSdkTools(
            new ProjectService(), new TagSessionService(),
            new Capabilities(LiveWritesEnabled: true, PublishMode: PublishMode.Auto));
        var autoJson = await autoTools.ProposeRungEdit(
            ValidXPath, 0, 0, null, offline: false, target: "controller", confirmedByUser: false);
        Assert.Contains("No active SDK session", autoJson, StringComparison.Ordinal);
    }
#endif

    /// <summary>
    /// With null capabilities a live-controller write is refused.
    /// </summary>
    [Fact]
    public async Task NullCapabilities_RefusesLiveWrite()
    {
        var svc = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var live = new TagSessionService();
        live.Attach(svc, isVirtual: false, isLiveController: true);

        var write = await WriteTagHandler.ExecuteAsync(
            "Cmd_MotorStart", "1", live, capabilities: null);
        Assert.IsType<CapabilityDisabledError>(write);

        var modeService = new ControllerModeService();
        modeService.Reset("Run");
        var mode = await ControllerModeHandler.ExecuteAsync(
            mode: "Program", confirm: true,
            sessionService: live, modeService: modeService, capabilities: null);
        Assert.IsType<CapabilityDisabledError>(mode);
    }

    /// <summary>
    /// Source scan: no fail-open gate pattern (<c>is { Flag: false }</c>) may
    /// remain in src/, and every capability gate on a flag must be written as
    /// <c>is not { Flag: true }</c>.
    /// </summary>
    [Fact]
    public void NoFailOpenGatePatternInSource()
    {
        var root = FindProjectRoot();
        var src  = Path.Combine(root, "src");
        var failOpen = new Regex(
            @"\bis\s*\{\s*(LiveWritesEnabled|StudioAutomationEnabled)\s*:\s*false\s*\}" +
            @"|\bis\s*\{\s*PublishMode\s*:\s*PublishMode\.Confirm\s*\}" +
            @"|[Cc]apabilities\??\.(LiveWritesEnabled|StudioAutomationEnabled)\s*==\s*false" +
            @"|!\s*_?[Cc]apabilities\??\.(LiveWritesEnabled|StudioAutomationEnabled)\b",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//")) continue;
                if (failOpen.IsMatch(line))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Fail-open capability gate patterns found (a null Capabilities would pass):\n" +
            string.Join("\n", offenders));
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No *.sln found above " + AppContext.BaseDirectory);
    }
}
