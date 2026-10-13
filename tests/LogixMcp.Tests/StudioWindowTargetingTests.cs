#if LOGIX_SDK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// The Studio driver acts only on the Logix Designer window titled
/// <c>&lt;controller&gt; in &lt;file&gt;.ACD</c> for the SDK session. The controller and project
/// file name reach <c>bench/studio-edits.ps1</c> as script parameters; no match gives
/// <c>StudioWindowNotFound</c>, several give <c>StudioWindowAmbiguous</c>, and nothing is clicked.
/// </summary>
public class StudioWindowTargetingTests
{
    private sealed class FakeProbe : IEnvironmentProbe
    {
        public int CurrentSessionId { get; init; } = 2;
        public bool IsElevated { get; init; }
        public IReadOnlyList<int> LogixDesignerSessionIds { get; init; } = new[] { 2 };
        public bool StudioIntegrityHigherThanCurrent { get; init; }
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("project root (*.sln) not found");
    }

    private static string ScriptPath => Path.Combine(FindProjectRoot(), "bench", "studio-edits.ps1");

    private static string TempScript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"s1-target-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, "# fake\n");
        return path;
    }

    /// <summary>Runs the runner in direct mode against a fake process; returns the result and the launched arguments.</summary>
    private static async Task<(Session1ScriptResult Result, List<string> Arguments)> RunDirectAsync(
        StudioWindowTarget? target, int exitCode, string stdout)
    {
        var script = TempScript();
        try
        {
            var args = new List<string>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                args.Add(psi.Arguments);
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(exitCode, stdout, string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "assemble", script, null, new FakeProbe(), Runner, () => true,
                CancellationToken.None, program: "MainProgram", target: target);
            return (result, args);
        }
        finally { File.Delete(script); }
    }

    // ── Acceptance: runner arguments ─────────────────────────────────────────────

    /// <summary>
    /// The controller and project file name are passed to the script as its own parameters
    /// (<c>-ExpectedController</c> / <c>-ExpectedProject</c>), quoted as literals, in both launch modes;
    /// the script declares those parameters. A quote in a name cannot end the literal.
    /// </summary>
    [Fact]
    public async Task ScriptReceivesControllerAndProject()
    {
        const string okLine = "RESULT_JSON: {\"ok\":true,\"reason\":\"assemble_gesture_sent\",\"screenshots\":[]}\n";

        // Direct launch.
        var (_, direct) = await RunDirectAsync(new StudioWindowTarget("Echo", "Echo.ACD"), 0, okLine);
        var psArgs = Assert.Single(direct);
        Assert.Contains("-ExpectedController 'Echo' -ExpectedProject 'Echo.ACD'", psArgs, StringComparison.Ordinal);
        Assert.Contains("-Program 'MainProgram'", psArgs, StringComparison.Ordinal);

        // Scheduled-task launch: the same parameters inside the registered action.
        var taskCommand = Session1ScriptRunner.BuildCommand(
            @"C:\LogixMcp\bench\studio-edits.ps1", "assemble", @"C:\t\log.txt",
            target: new StudioWindowTarget("Echo", "Echo.ACD"));
        Assert.Contains("-ExpectedController ''Echo'' -ExpectedProject ''Echo.ACD''", taskCommand, StringComparison.Ordinal);

        // No target (unknown controller/file): the parameters are omitted, not passed empty.
        var (_, none) = await RunDirectAsync(null, 0, okLine);
        Assert.DoesNotContain("-ExpectedController", Assert.Single(none), StringComparison.Ordinal);
        Assert.DoesNotContain("-ExpectedProject", Assert.Single(none), StringComparison.Ordinal);

        // An upload session knows only the controller.
        var (_, ctrlOnly) = await RunDirectAsync(new StudioWindowTarget("Echo", null), 0, okLine);
        Assert.Contains("-ExpectedController 'Echo'", Assert.Single(ctrlOnly), StringComparison.Ordinal);
        Assert.DoesNotContain("-ExpectedProject", Assert.Single(ctrlOnly), StringComparison.Ordinal);

        // Quote characters (ASCII and the typographic ones PowerShell also treats as quotes) are
        // doubled, so a hostile name stays inside its literal.
        Assert.Equal("'O''Brien'", Session1ScriptRunner.PsQuote("O'Brien"));
        Assert.Equal("'A’’B'", Session1ScriptRunner.PsQuote("A’B"));
        Assert.Equal("'A‘‘B'", Session1ScriptRunner.PsQuote("A‘B"));
        var hostile = Session1ScriptRunner.BuildTargetArguments(
            new StudioWindowTarget("x'; Remove-Item C:\\ #", "p’.ACD"));
        Assert.Equal(" -ExpectedController 'x''; Remove-Item C:\\ #' -ExpectedProject 'p’’.ACD'", hostile);

        // The script binds them in its param() block.
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains("[string] $ExpectedController", script, StringComparison.Ordinal);
        Assert.Contains("[string] $ExpectedProject", script, StringComparison.Ordinal);
    }

    /// <summary>The session's controller name and project file name become the window target.</summary>
    [Fact]
    public void SessionTarget_CarriesControllerAndProjectFileName()
    {
        using var session = new LogixProjectSession
        {
            StudioControllerName  = "Echo",
            StudioProjectFileName = "Echo.ACD",
        };
        var target = LogixSdkTools.StudioWindowTargetOf(session);
        Assert.Equal("Echo", target.Controller);
        Assert.Equal("Echo.ACD", target.ProjectFile);
    }

    // ── Acceptance: script exit, then kind ───────────────────────────────────────

    /// <summary>
    /// The script exits 20 (no matching window) / 21 (several) after printing the
    /// <c>studio_window_*</c> reason and clicking nothing; the runner maps the exit code, and the
    /// handler maps the reason, to <c>StudioWindowNotFound</c> / <c>StudioWindowAmbiguous</c>, naming the
    /// expected title.
    /// </summary>
    [Fact]
    public async Task NotFoundAndAmbiguous_MappedToKinds()
    {
        // ── 1. The script: exit codes, reasons, and no gesture before the refusal. ──
        var script = File.ReadAllText(ScriptPath);
        Assert.Contains("$script:ExitWindowNotFound  = 20", script, StringComparison.Ordinal);
        Assert.Contains("$script:ExitWindowAmbiguous = 21", script, StringComparison.Ordinal);
        Assert.Contains("Reason   = 'studio_window_not_found'", script, StringComparison.Ordinal);
        Assert.Contains("Reason   = 'studio_window_ambiguous'", script, StringComparison.Ordinal);
        Assert.Contains("Kind     = 'StudioWindowNotFound'", script, StringComparison.Ordinal);
        Assert.Contains("Kind     = 'StudioWindowAmbiguous'", script, StringComparison.Ordinal);
        Assert.Contains("expectedTitle", script, StringComparison.Ordinal);
        Assert.DoesNotContain("using the first", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@($existing)[0]", script, StringComparison.Ordinal);
        // The refusal block is reached before the verb switch, i.e. before any posted command.
        int refusalExit = script.IndexOf("[int] $refusal.ExitCode", StringComparison.Ordinal);
        int verbSwitch  = script.IndexOf("switch ($Verb) {", refusalExit, StringComparison.Ordinal);
        Assert.True(refusalExit > 0 && verbSwitch > refusalExit);

        // ── 2. Runner: exit code alone (no RESULT_JSON line) maps to the kind. ──
        var target = new StudioWindowTarget("Echo", "Echo.ACD");
        var (notFound, _) = await RunDirectAsync(target, 20, "");
        Assert.False(notFound.Ok);
        Assert.Equal("StudioWindowNotFound", notFound.Kind);
        Assert.Equal("studio_window_not_found", notFound.Reason);

        var (ambiguous, _) = await RunDirectAsync(target, 21, "");
        Assert.False(ambiguous.Ok);
        Assert.Equal("StudioWindowAmbiguous", ambiguous.Kind);
        Assert.Equal("studio_window_ambiguous", ambiguous.Reason);

        var (other, _) = await RunDirectAsync(target, 1, "");
        Assert.Null(other.Kind);

        // Scheduled-task mode: LastTaskResult carries the same exit code.
        var outputFile = Path.GetTempFileName();
        try
        {
            var viaTask = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                _ => Task.FromResult("Ready"), outputFile, "logix-mcp-s1-edits",
                TimeSpan.FromSeconds(5), getLastTaskResultAsync: _ => Task.FromResult<uint?>(21));
            Assert.Equal("StudioWindowAmbiguous", viaTask.Kind);
        }
        finally { File.Delete(outputFile); }

        // ── 3. Handler: the driver's reason becomes the kind, and the message names the title. ──
        Assert.Equal("StudioWindowNotFound",  StudioLifecycleHandler.StudioStateKindOf("studio_window_not_found"));
        Assert.Equal("StudioWindowAmbiguous", StudioLifecycleHandler.StudioStateKindOf("studio_window_ambiguous"));
        Assert.Equal("StudioNotRunning",      StudioLifecycleHandler.StudioStateKindOf("studio_not_running"));

        foreach (var (reason, kind) in new[]
        {
            ("studio_window_not_found",  "StudioWindowNotFound"),
            ("studio_window_ambiguous", "StudioWindowAmbiguous"),
        })
        {
            var line =
                "RESULT_JSON: {\"ok\":false,\"reason\":\"" + reason + "\",\"screenshots\":[],\"detail\":" +
                "{\"kind\":\"" + kind + "\",\"expectedTitle\":\"Echo in Echo.ACD\"," +
                "\"message\":\"Window problem for 'Echo in Echo.ACD'. Nothing was clicked\"}}";
            var parsed = Session1ScriptRunner.ParseResultJson(line)!;

            var verbs = new List<string>();
            var result = await StudioLifecycleHandler.ExecuteAsync(
                AssembleLike, "MainProgram", "MainRoutine", true, Reader(),
                (verb, _) => { verbs.Add(verb); return Task.FromResult(parsed); },
                () => true);

            var err = Assert.IsType<CancelPendingEditsError>(result);
            Assert.Equal(kind, err.Kind);
            Assert.Contains("Echo in Echo.ACD", err.Error, StringComparison.Ordinal);
            Assert.Contains("Nothing was clicked", err.Error, StringComparison.Ordinal);
            Assert.Equal(new[] { "assemble" }, verbs);
        }
    }

    private static readonly StudioLifecycleVerb AssembleLike = new(
        Verb: "assemble", ToolName: "assemble_tool", Status: "assembled",
        GestureMenu: "Logic -> Online Edits -> Assemble Accepted Program Edits",
        CapitalVerb: "Assemble", Gerund: "assembling", PastCapital: "Assembled",
        Consequence: "assembling finalizes the pending edits", RequireAfterNone: true,
        Audit: (_, _, _, _, _) => { });

    private static Func<string, string, CancellationToken, Task<object>> Reader() =>
        (_, _, _) => Task.FromResult<object>(new PendingEditsResult(
            true, "Run", null,
            new[]
            {
                new RoutinePendingEditState("MainProgram", "MainRoutine", 2,
                    new[] { new PendingEditRungEntry(0, "I", "OTE(Motor_Run);") }, "pending"),
            },
            new StagedEditsState(0, Array.Empty<string>()), DateTimeOffset.UtcNow));

    // ── The script's own window selection (pure functions, run under pwsh when present) ──

    /// <summary>
    /// Runs <c>Select-StudioWindowForTarget</c> from the real script (extracted by AST, so nothing
    /// else in the script executes) against fake window titles. Returns without asserting when
    /// <c>pwsh</c> cannot be started on this host.
    /// </summary>
    [Fact]
    public void Script_SelectsExactlyOneMatchingTitle()
    {
        const string psScript = @"
$errs = $null; $tok = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:STUDIO_SCRIPT, [ref]$tok, [ref]$errs)
foreach ($n in 'Get-ExpectedStudioTitle','Test-StudioWindowTitleMatch','Select-StudioWindowForTarget') {
    $f = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $n }, $true)
    . ([scriptblock]::Create($f.Extent.Text))
}
$w = @(
  [pscustomobject]@{ Title = 'Logix Designer - Echo in Echo.ACD [1756-L83E 38.11]' },
  [pscustomobject]@{ Title = '[Rem Run] - Logix Designer - Echo2 in Echo.ACD [1756-L83E 38.11]' },
  [pscustomobject]@{ Title = '[Emulator] - Logix Designer - Other in Echo.ACD' })
foreach ($c in @(@('echo','ECHO.acd'), @('Echo2','Echo'), @('Nope','Echo.ACD'), @('','Echo.ACD'), @('Echo',''))) {
    $r = Select-StudioWindowForTarget -Windows $w -Controller $c[0] -ProjectFile $c[1]
    ""$($c -join '|')=$($r.Status)/$($r.MatchCount)/$($r.ExpectedTitle)""
}
";
        var lines = RunPwsh(psScript);
        if (lines is null) return; // pwsh not usable on this host
        {
            Assert.Equal(new[]
            {
                "echo|ECHO.acd=ok/1/echo in ECHO.ACD",
                "Echo2|Echo=ok/1/Echo2 in Echo.ACD",
                "Nope|Echo.ACD=not_found/0/Nope in Echo.ACD",
                "|Echo.ACD=ambiguous/3/* in Echo.ACD",
                "Echo|=ok/1/Echo (any file, or an unsaved upload)",
            }, lines);
        }
    }

    /// <summary>
    /// Runs a PowerShell snippet under <c>pwsh</c> and returns its stdout lines, or <c>null</c> when
    /// <c>pwsh</c> is missing or its runtime is unusable on this host (the caller then skips).
    /// </summary>
    private static string[]? RunPwsh(string psScript)
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(psScript);
        psi.Environment["STUDIO_SCRIPT"]      = ScriptPath;
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";

        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
        if (p is null) return null;
        using (p)
        {
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 && stderr.Contains("install or update .NET", StringComparison.Ordinal))
                return null;
            Assert.True(p.ExitCode == 0, $"pwsh failed: {stderr}");
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>
    /// A title with no file name (an upload never saved) matches when no
    /// project file is expected, the controller matches as a whole token, and a project file
    /// restricts the match to the <c>in &lt;file&gt;.ACD</c> form.
    /// </summary>
    [Fact]
    public void Script_MatchesTitleWithoutFileName()
    {
        const string psScript = @"
$errs = $null; $tok = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:STUDIO_SCRIPT, [ref]$tok, [ref]$errs)
foreach ($n in 'Get-ExpectedStudioTitle','Test-StudioWindowTitleMatch','Select-StudioWindowForTarget','Get-StudioOnlineStateFromTitle') {
    $f = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $n }, $true)
    . ([scriptblock]::Create($f.Extent.Text))
}
$upl = '[Emulator] - Logix Designer - PLC_LN02_Template [5069-L310ERM 36.11]*'
function M($t, $c, $p) { ""$(Test-StudioWindowTitleMatch -Title $t -Controller $c -ProjectFile $p)"" }
""upl=$(M $upl 'PLC_LN02_Template' '')""
""upl-foo=$(M $upl 'PLC_LN02_Template' 'Foo.ACD')""
""upl-short=$(M $upl 'PLC_LN02' '')""
""upl-long=$(M '[Emulator] - Logix Designer - PLC_LN02 [5069-L310ERM 36.11]*' 'PLC_LN02_Template' '')""
""rem=$(M '[Rem Run] - Logix Designer - X in Y.ACD [1756-L85E 38.11]' 'X' 'Y')""
""rem-noproj=$(M '[Rem Run] - Logix Designer - X in Y.ACD [1756-L85E 38.11]' 'X' '')""
""rem-other=$(M '[Rem Run] - Logix Designer - X in Y.ACD [1756-L85E 38.11]' 'X' 'Z.ACD')""
""star-only=$(M 'Logix Designer - PLC_LN02_Template*' 'plc_ln02_template' '')""
$off = 'Logix Designer - PLC_LN02_Template'
""offline=$(M $off 'PLC_LN02_Template' '')/$((Get-StudioOnlineStateFromTitle -Title $off).Online)""
$one = Select-StudioWindowForTarget -Windows @([pscustomobject]@{ Title = $off }) -Controller 'PLC_LN02_Template' -ProjectFile ''
""one=$($one.Status)/$($one.ExpectedTitle)""
$two = Select-StudioWindowForTarget -Windows @(
    [pscustomobject]@{ Title = '[Emulator] - Logix Designer - PLC_LN02_Template [5069-L310ERM 36.11]*' },
    [pscustomobject]@{ Title = 'Logix Designer - PLC_LN02_Template' }) -Controller 'PLC_LN02_Template' -ProjectFile ''
""two=$($two.Status)/$($two.MatchCount)""
";
        var lines = RunPwsh(psScript);
        if (lines is null) return; // pwsh not usable on this host
        Assert.Equal(new[]
        {
            "upl=True",
            "upl-foo=False",
            "upl-short=False",
            "upl-long=False",
            "rem=True",
            "rem-noproj=True",
            "rem-other=False",
            "star-only=True",
            "offline=True/False",
            "one=ok/PLC_LN02_Template (any file, or an unsaved upload)",
            "two=ambiguous/2",
        }, lines);
    }
}
#endif
