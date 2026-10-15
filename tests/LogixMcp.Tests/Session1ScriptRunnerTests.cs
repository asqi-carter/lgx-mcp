using System.Linq;
#if LOGIX_SDK

namespace LogixMcp.Tests;

/// <summary>
/// Guard tests: <c>Session1ScriptRunner</c> resolves
/// <c>bench/studio-edits.ps1</c> ONLY beside the running binary
/// (<c>&lt;baseDirectory&gt;/bench/studio-edits.ps1</c>). There is no
/// parent-directory / <c>*.sln</c> walk, and when the script is missing the
/// error names the expected path.
///
/// <para>
/// <see cref="Session1ScriptRunner.CandidateScriptPaths"/> and
/// <see cref="Session1ScriptRunner.FindDefaultScriptPath"/> accept an
/// optional <c>baseDirectory</c> override (defaulting to
/// <see cref="AppContext.BaseDirectory"/>) so resolution can be exercised
/// against synthetic directory trees.
/// </para>
/// </summary>
public class Session1ScriptRunnerTests
{
    /// <summary>The beside-binary copy is resolved even when a repo-root copy exists.</summary>
    [Fact]
    public void ResolvesScriptBesideTheBinary()
    {
        string tempDir = Directory.CreateTempSubdirectory("s1-beside-binary-").FullName;
        try
        {
            string repoRoot = Path.Combine(tempDir, "repo");
            Directory.CreateDirectory(Path.Combine(repoRoot, "bench"));
            File.WriteAllText(Path.Combine(repoRoot, "Fake.sln"), string.Empty);
            File.WriteAllText(Path.Combine(repoRoot, "bench", "studio-edits.ps1"), "# repo-root copy\n");

            string publishDir = Path.Combine(repoRoot, "bin", "publish");
            Directory.CreateDirectory(Path.Combine(publishDir, "bench"));
            string besideBinaryScript = Path.Combine(publishDir, "bench", "studio-edits.ps1");
            File.WriteAllText(besideBinaryScript, "# beside-binary copy\n");

            Assert.Equal(besideBinaryScript, Session1ScriptRunner.FindDefaultScriptPath(publishDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// A script at a <c>*.sln</c> repo root above the binary must NOT be used
    /// when nothing exists beside the binary; the only candidate is the
    /// beside-binary path and the error message names it.
    /// </summary>
    [Fact]
    public void MissingBesideBinaryScript_NoParentWalk()
    {
        string tempDir = Directory.CreateTempSubdirectory("s1-no-parent-walk-").FullName;
        try
        {
            string repoRoot = Path.Combine(tempDir, "repo");
            string publishDir = Path.Combine(repoRoot, "bin", "publish");
            Directory.CreateDirectory(publishDir);
            File.WriteAllText(Path.Combine(repoRoot, "Fake.sln"), string.Empty);
            Directory.CreateDirectory(Path.Combine(repoRoot, "bench"));
            string repoRootScript = Path.Combine(repoRoot, "bench", "studio-edits.ps1");
            File.WriteAllText(repoRootScript, "# repo-root copy\n");

            string expected = Path.Combine(publishDir, "bench", "studio-edits.ps1");

            Assert.Null(Session1ScriptRunner.FindDefaultScriptPath(publishDir));

            var candidates = Session1ScriptRunner.CandidateScriptPaths(publishDir);
            Assert.Equal(new[] { expected }, candidates);
            Assert.DoesNotContain(repoRootScript, candidates);

            var message = Session1ScriptRunner.BuildNotFoundMessage(candidates);
            Assert.Contains(expected, message, StringComparison.Ordinal);
            Assert.DoesNotContain(repoRootScript, message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Guard test: every scheduled-task name this runner
    /// touches must carry the <c>logix-mcp-s1-&lt;verb&gt;</c> prefix — never
    /// the bare legacy short name, which can collide with (and, via
    /// <c>Register-ScheduledTask -Force</c>, silently overwrite) an
    /// operator's own unrelated scheduled task of that name.
    /// </summary>
    [Fact]
    public void TaskName_HasLogixMcpPrefix_NeverLegacyName()
    {
        foreach (var verb in new[] { "status", "cancel", "accept" })
        {
            var taskName = Session1ScriptRunner.BuildTaskName(verb);

            Assert.StartsWith("logix-mcp-s1-", taskName, StringComparison.Ordinal);
            Assert.Equal($"logix-mcp-s1-{verb}", taskName);

            var command = Session1ScriptRunner.BuildCommand(
                @"C:\repo\bench\studio-edits.ps1", verb, @"C:\temp\out.log");

            Assert.Contains($"'{taskName}'", command, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Guard test: the scheduled task's own
    /// <c>-Argument</c> value (built by
    /// <see cref="Session1ScriptRunner.BuildActionArgument"/>) is the literal
    /// Win32 command line Task Scheduler hands to <c>CreateProcess</c> for
    /// <c>powershell.exe</c> at run time. That command line is parsed by
    /// <c>CommandLineToArgvW</c>, which only recognizes double quotes as a
    /// quoting character — single quotes (valid PowerShell syntax, but
    /// meaningless to <c>CommandLineToArgvW</c>) pass through literally and
    /// split any path containing a space into multiple arguments, producing
    /// the "path format not supported" failure (<c>LastTaskResult
    /// 0xFFFD0000</c>). This test asserts the generated action-argument
    /// string for a script path containing spaces wraps every path/verb in
    /// double quotes and contains no single-quote characters at all.
    /// </summary>
    [Fact]
    public void ActionString_UsesDoubleQuotes_NeverSingle()
    {
        // The redirect must live INSIDE a -Command string (PowerShell
        // parses it); with -File it is passed to the script as an argument and
        // nothing is written (measured 2026-09-07 08:10Z, LastTaskResult 1).
        const string scriptPath = @"C:\Program Files\Logix MCP\bench\studio edits.ps1";
        const string verb       = "cancel";
        const string outputFile = @"C:\Users\Example User\AppData\Local\Temp\scheduled out.log";
        var actionArgument = Session1ScriptRunner.BuildActionArgument(scriptPath, verb, outputFile);
        Assert.Equal(
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
            $"-Command \"& '{scriptPath}' -Verb '{verb}' *> '{outputFile}'\"",
            actionArgument);
        // One CommandLineToArgvW argument: exactly two outer double quotes.
        Assert.StartsWith("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"& '", actionArgument, StringComparison.Ordinal);
        Assert.EndsWith("'\"", actionArgument, StringComparison.Ordinal);
        Assert.Equal(2, actionArgument.Count(c => c == '"'));
        Assert.DoesNotContain("-File", actionArgument, StringComparison.Ordinal);
        // No visible console — it covers Studio and eats the clicks.
        Assert.Contains("-WindowStyle Hidden", actionArgument, StringComparison.Ordinal);
        Assert.Contains(" *> '", actionArgument, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionString_DoublesEmbeddedSingleQuotes()
    {
        var actionArgument = Session1ScriptRunner.BuildActionArgument(@"C:\O'Brien\studio-edits.ps1", "status", @"C:\tmp\o'log.log");
        Assert.Contains(@"'C:\O''Brien\studio-edits.ps1'", actionArgument, StringComparison.Ordinal);
        Assert.Contains(@"'C:\tmp\o''log.log'", actionArgument, StringComparison.Ordinal);
    }

    // ── Redirect-file polling ──────────────────────────────

    /// <summary>
    /// Guard test: a scheduled task has no stdout stream
    /// the launching process can read -- the driver's own output goes
    /// exclusively to the <c>*&gt;</c> redirect log file. This test proves
    /// <see cref="Session1ScriptRunner.AwaitResultFromRedirectFileAsync"/>
    /// polls the injected task-state query until it reports a state other
    /// than <c>Running</c>, then parses the <c>RESULT_JSON:</c> line out of
    /// the redirect file -- never out of the launcher's stdout (which this
    /// test does not even give it a way to read, proving the file is the
    /// only source consulted).
    /// </summary>
    [Fact]
    public async Task ReadsResultJsonFromRedirectFile_NotLauncherStdout()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), $"s1-redirect-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(
                outputFile,
                "Write-Host chatter from the driver\n" +
                "RESULT_JSON: {\"ok\":true,\"reason\":\"cancel_gesture_sent\",\"screenshots\":[]}\n");

            // Simulates the task still being Running for its first two
            // polls, then leaving the Running state on the third.
            var states = new Queue<string>(new[] { "Running", "Running", "Ready" });
            Task<string> GetTaskStateAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult(states.Count > 0 ? states.Dequeue() : "Ready");

            var result = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                GetTaskStateAsync,
                outputFile,
                "logix-mcp-s1-cancel",
                TimeSpan.FromSeconds(180),
                CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(1));

            Assert.Empty(states);
            Assert.True(result.Ok);
            Assert.Equal("cancel_gesture_sent", result.Reason);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    /// <summary>
    /// The default poll timeout is <c>OpenTimeoutSeconds</c> (120 s, mirrored
    /// from the driver's own default) plus a <c>GestureBudgetSeconds</c> (60
    /// s) allowance for the driver's own internal dialog/gesture waits -- 180
    /// s total.
    /// </summary>
    [Fact]
    public void DefaultPollTimeout_IsOpenTimeoutPlusGestureBudget_180Seconds()
    {
        Assert.Equal(120, Session1ScriptRunner.DefaultOpenTimeoutSeconds);
        Assert.Equal(60, Session1ScriptRunner.GestureBudgetSeconds);
        Assert.Equal(180, Session1ScriptRunner.DefaultPollTimeoutSeconds);
        Assert.Equal(
            Session1ScriptRunner.DefaultOpenTimeoutSeconds + Session1ScriptRunner.GestureBudgetSeconds,
            Session1ScriptRunner.DefaultPollTimeoutSeconds);
    }

    /// <summary>
    /// When the task never leaves the <c>Running</c> state within the
    /// timeout and the redirect file has no <c>RESULT_JSON:</c> line (or no
    /// file at all), the failure detail must still surface up to 2 KB of the
    /// log file's tail so an operator can see what the driver actually wrote
    /// rather than an empty message.
    /// </summary>
    [Fact]
    public async Task NoResultJsonLine_SurfacesLogTail_UpToTwoKilobytes()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), $"s1-tail-{Guid.NewGuid():N}.log");
        try
        {
            string longChatter = new string('x', 4096); // well over the 2 KB cap
            File.WriteAllText(outputFile, longChatter);

            Task<string> GetTaskStateAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult("Ready");

            var result = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                GetTaskStateAsync,
                outputFile,
                "logix-mcp-s1-cancel",
                TimeSpan.FromSeconds(180),
                CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(1));

            Assert.False(result.Ok);
            Assert.Contains(new string('x', 2048), result.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('x', 2049), result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    // ── LastTaskResult and task name surfaced everywhere ──

    /// <summary>
    /// Guard test: when the
    /// task completes but the redirect log has no <c>RESULT_JSON:</c> line
    /// (the exact failure the bench hit, where the
    /// task's own <c>LastTaskResult</c> of <c>0xFFFD0000</c> was the smoking
    /// gun for the single-quote action-string bug), the failure payload must
    /// surface the task's <c>LastTaskResult</c> in both hex and decimal
    /// form, the registered task name, and still include the log tail.
    /// </summary>
    [Fact]
    public async Task FailureSurfacesLastTaskResultAndLogTail()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), $"s1-ltr-{Guid.NewGuid():N}.log");
        const string taskName = "logix-mcp-s1-cancel";
        try
        {
            string chatter = "Write-Host chatter from the driver, no RESULT_JSON here\n";
            File.WriteAllText(outputFile, chatter);

            Task<string> GetTaskStateAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult("Ready");

            // 0xFFFD0000 -- the exact LastTaskResult the bench observed for
            // the "path format not supported" single-quote action bug (see
            // Session1ScriptRunner.BuildActionArgument's remarks).
            const uint rawLastTaskResult = 0xFFFD0000;
            Task<uint?> GetLastTaskResultAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult<uint?>(rawLastTaskResult);

            var result = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                GetTaskStateAsync,
                outputFile,
                taskName,
                TimeSpan.FromSeconds(180),
                CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(1),
                getLastTaskResultAsync: GetLastTaskResultAsync);

            Assert.False(result.Ok);

            // Task name always surfaced, both as a dedicated field and
            // within the human-readable reason text.
            Assert.Equal(taskName, result.TaskName);
            Assert.Contains(taskName, result.Reason, StringComparison.Ordinal);

            // LastTaskResult surfaced in both hex and decimal, as dedicated
            // fields and within the reason text.
            Assert.Equal("0xFFFD0000", result.LastTaskResultHex);
            Assert.Equal(4294770688L, result.LastTaskResultDecimal);
            Assert.Contains("0xFFFD0000", result.Reason, StringComparison.Ordinal);
            Assert.Contains("4294770688", result.Reason, StringComparison.Ordinal);

            // The log tail behavior this failure path already had must
            // still be intact.
            Assert.Contains(chatter.Trim(), result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    /// <summary>
    /// The success payload (a <c>RESULT_JSON:</c> line was found) must also
    /// carry the task name and <c>LastTaskResult</c> alongside the driver's
    /// own document — not only failure payloads.
    /// </summary>
    [Fact]
    public async Task Success_AlsoSurfacesTaskNameAndLastTaskResult()
    {
        string outputFile = Path.Combine(Path.GetTempPath(), $"s1-ltr-ok-{Guid.NewGuid():N}.log");
        const string taskName = "logix-mcp-s1-cancel";
        try
        {
            File.WriteAllText(
                outputFile,
                "RESULT_JSON: {\"ok\":true,\"reason\":\"cancel_gesture_sent\",\"screenshots\":[]}\n");

            Task<string> GetTaskStateAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult("Ready");

            const uint rawLastTaskResult = 0; // 0x0 == "the last run completed successfully".
            Task<uint?> GetLastTaskResultAsync(CancellationToken _) =>
                System.Threading.Tasks.Task.FromResult<uint?>(rawLastTaskResult);

            var result = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                GetTaskStateAsync,
                outputFile,
                taskName,
                TimeSpan.FromSeconds(180),
                CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(1),
                getLastTaskResultAsync: GetLastTaskResultAsync);

            Assert.True(result.Ok);
            Assert.Equal("cancel_gesture_sent", result.Reason);

            Assert.Equal(taskName, result.TaskName);
            Assert.Equal("0x00000000", result.LastTaskResultHex);
            Assert.Equal(0L, result.LastTaskResultDecimal);
        }
        finally
        {
            if (File.Exists(outputFile))
                File.Delete(outputFile);
        }
    }

    /// <summary>
    /// <see cref="Session1ScriptRunner.FormatLastTaskResult"/> is the pure
    /// function behind every hex/decimal LastTaskResult pair surfaced on a
    /// payload; assert its exact output shape directly.
    /// </summary>
    [Fact]
    public void FormatLastTaskResult_ProducesZeroPaddedHexAndDecimal()
    {
        var (hex, dec) = Session1ScriptRunner.FormatLastTaskResult(0xFFFD0000);
        Assert.Equal("0xFFFD0000", hex);
        Assert.Equal(4294770688L, dec);

        var (zeroHex, zeroDec) = Session1ScriptRunner.FormatLastTaskResult(0);
        Assert.Equal("0x00000000", zeroHex);
        Assert.Equal(0L, zeroDec);
    }

    /// <summary>
    /// Every early failure branch in <see cref="Session1ScriptRunner.RunAsync"/>
    /// -- including one reachable off Windows -- must still name the task it
    /// would have (or did) register.
    /// </summary>
    [Fact]
    public async Task RunAsync_OnNonWindows_StillSurfacesTaskName()
    {
        if (OperatingSystem.IsWindows())
            return; // this invariant only applies off Windows.

        var result = await Session1ScriptRunner.RunAsync("cancel");

        Assert.False(result.Ok);
        Assert.Equal("logix-mcp-s1-cancel", result.TaskName);
    }

    // ── Environment probe + direct-launch mode ─────────────

    private sealed class FakeProbe : IEnvironmentProbe
    {
        public int CurrentSessionId { get; init; }
        public bool IsElevated { get; init; }
        public IReadOnlyList<int> LogixDesignerSessionIds { get; init; } = Array.Empty<int>();
        public bool StudioIntegrityHigherThanCurrent { get; init; }
    }

    private const string ResultLine =
        "RESULT_JSON: {\"ok\":true,\"reason\":\"cancel_gesture_sent\",\"screenshots\":[\"a.png\"]}";

    private static string TempScript()
    {
        string path = Path.Combine(Path.GetTempPath(), $"s1-fake-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, "# fake\n");
        return path;
    }

    private sealed class ThrowingProbe : IEnvironmentProbe
    {
        public int CurrentSessionId => throw new InvalidOperationException("probe failed");
        public bool IsElevated => throw new InvalidOperationException("probe failed");
        public IReadOnlyList<int> LogixDesignerSessionIds => throw new InvalidOperationException("probe failed");
        public bool StudioIntegrityHigherThanCurrent => throw new InvalidOperationException("probe failed");
    }

    /// <summary>
    /// PlanLaunch reports exactly the (launchMode, runLevel) RunAsync stamps on its
    /// payload for the same probe, and a probe failure leaves both null without throwing.
    /// </summary>
    [Fact]
    public async Task PlanLaunch_MatchesRunAsyncDecision()
    {
        string script = TempScript();
        try
        {
            var probes = new[]
            {
                new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } },
                new FakeProbe { CurrentSessionId = 0, IsElevated = true },
                new FakeProbe { CurrentSessionId = 0, IsElevated = false },
            };

            foreach (var probe in probes)
            {
                var plan = Session1ScriptRunner.PlanLaunch(probe);
                var run = await Session1ScriptRunner.RunCoreAsync(
                    "cancel", script, null, probe,
                    (psi, t, c) => Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                        new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty)),
                    () => true, default);

                Assert.Equal(run.LaunchMode, plan.LaunchMode);
                Assert.Equal(run.RunLevel, plan.RunLevel);
            }

            Assert.Equal(("direct", (string?)null), Session1ScriptRunner.PlanLaunch(probes[0]));
            Assert.Equal(("scheduled_task", "Highest"), Session1ScriptRunner.PlanLaunch(probes[1]));
            Assert.Equal(("scheduled_task", "Limited"), Session1ScriptRunner.PlanLaunch(probes[2]));

            var failed = Session1ScriptRunner.PlanLaunch(new ThrowingProbe());
            Assert.Null(failed.LaunchMode);
            Assert.Null(failed.RunLevel);
        }
        finally { File.Delete(script); }
    }

    /// <summary>
    /// From a non-zero session the runner launches the driver directly as a
    /// child powershell.exe (same -Command/-Verb argument string as the task
    /// action, CreateNoWindow, same 180 s budget) and registers no task.
    /// </summary>
    [Fact]
    public async Task InteractiveSession_UsesDirectLaunch_RegistersNoTask()
    {
        string script = TempScript();
        try
        {
            var launches = new List<(System.Diagnostics.ProcessStartInfo Psi, TimeSpan? Timeout)>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                launches.Add((psi, timeout));
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } },
                Runner, () => true, CancellationToken.None);

            Assert.True(result.Ok);
            var (psi0, timeout0) = Assert.Single(launches);
            Assert.Equal("powershell.exe", psi0.FileName);
            Assert.True(psi0.CreateNoWindow);
            Assert.Equal(TimeSpan.FromSeconds(Session1ScriptRunner.DefaultPollTimeoutSeconds), timeout0);
            Assert.Contains(script, psi0.Arguments);
            Assert.Contains("-Verb 'cancel'", psi0.Arguments);
            foreach (var (psi, _) in launches)
            {
                Assert.DoesNotContain("Register-ScheduledTask", psi.Arguments + string.Join(" ", psi.ArgumentList));
                Assert.DoesNotContain("Start-ScheduledTask", psi.Arguments + string.Join(" ", psi.ArgumentList));
            }
            Assert.Null(result.TaskName);
            Assert.Equal(Session1ScriptRunner.LaunchMode.Direct,
                Session1ScriptRunner.DecideLaunchMode(new FakeProbe { CurrentSessionId = 2 }));
            Assert.Equal(Session1ScriptRunner.LaunchMode.ScheduledTask,
                Session1ScriptRunner.DecideLaunchMode(new FakeProbe { CurrentSessionId = 0 }));
        }
        finally { File.Delete(script); }
    }

    private static async Task<(Session1ScriptResult Result, int Launches)> RunDirectRefusalAsync(FakeProbe probe)
    {
        string script = TempScript();
        try
        {
            int launches = 0;
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                launches++;
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, probe, Runner, () => true, CancellationToken.None);
            return (result, launches);
        }
        finally { File.Delete(script); }
    }

    /// <summary>No LogixDesigner process in any session: typed StudioNotRunning with the message.</summary>
    [Fact]
    public async Task NoStudioAnywhere_IsStudioNotRunning()
    {
        var (result, launches) = await RunDirectRefusalAsync(
            new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = Array.Empty<int>() });

        Assert.False(result.Ok);
        Assert.Equal("StudioNotRunning", result.Kind);
        Assert.StartsWith("studio_not_running", result.Reason, StringComparison.Ordinal);
        Assert.Contains("go online by Upload", result.Reason, StringComparison.Ordinal);
        Assert.Contains("never launches Studio", result.Reason, StringComparison.Ordinal);
        Assert.False(result.Launched);
        Assert.Equal(0, launches);
    }

    /// <summary>Studio runs only in another session: studio_not_in_this_session reason plus kind StudioInOtherSession.</summary>
    [Fact]
    public async Task StudioInOtherSession_IsStudioInOtherSession()
    {
        var (result, launches) = await RunDirectRefusalAsync(
            new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 2 } });

        Assert.False(result.Ok);
        Assert.Equal("StudioInOtherSession", result.Kind);
        Assert.StartsWith("studio_not_in_this_session", result.Reason, StringComparison.Ordinal);
        Assert.False(result.Launched);
        Assert.Equal(0, launches);
    }

    /// <summary>Studio elevated, driver not: studio_elevated_driver_not reason plus kind StudioElevated.</summary>
    [Fact]
    public async Task ElevatedStudio_IsStudioElevated()
    {
        var (result, launches) = await RunDirectRefusalAsync(
            new FakeProbe
            {
                CurrentSessionId = 1,
                LogixDesignerSessionIds = new[] { 1 },
                StudioIntegrityHigherThanCurrent = true,
            });

        Assert.False(result.Ok);
        Assert.Equal("StudioElevated", result.Kind);
        Assert.StartsWith("studio_elevated_driver_not", result.Reason, StringComparison.Ordinal);
        Assert.False(result.Launched);
        Assert.Equal(0, launches);
    }

    /// <summary>
    /// Studio runs in session 2 while the server is in session 1: the direct
    /// launch is refused with studio_not_in_this_session, naming both ids,
    /// and no process is launched / no task is registered.
    /// </summary>
    [Fact]
    public async Task StudioInOtherSession_RefusesStudioNotInThisSession_LaunchesNothing()
    {
        string script = TempScript();
        try
        {
            var launches = new List<System.Diagnostics.ProcessStartInfo>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                launches.Add(psi);
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null,
                new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 2 } },
                Runner, () => true, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.StartsWith("studio_not_in_this_session", result.Reason, StringComparison.Ordinal);
            Assert.Contains("session 1", result.Reason, StringComparison.Ordinal);
            Assert.Contains("session 2", result.Reason, StringComparison.Ordinal);
            Assert.Empty(launches);
            Assert.Null(result.TaskName);
            Assert.False(result.Launched);
        }
        finally { File.Delete(script); }
    }

    /// <summary>
    /// Studio is elevated and the driver is not (probe reports higher integrity /
    /// Access denied on Studio's token): refused with studio_elevated_driver_not and
    /// the PRD message; nothing is launched.
    /// </summary>
    [Fact]
    public async Task StudioElevated_DriverNot_RefusesWithStartStudioNormallyHint()
    {
        string script = TempScript();
        try
        {
            var launches = new List<System.Diagnostics.ProcessStartInfo>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                launches.Add(psi);
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null,
                new FakeProbe
                {
                    CurrentSessionId = 1,
                    IsElevated = false,
                    LogixDesignerSessionIds = new[] { 1 },
                    StudioIntegrityHigherThanCurrent = true,
                },
                Runner, () => true, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.StartsWith("studio_elevated_driver_not", result.Reason, StringComparison.Ordinal);
            Assert.Contains(
                "Studio 5000 is running elevated; this lgx-mcp server is not, so Windows blocks it " +
                "from driving Studio. Start Studio normally, or run the MCP client elevated.",
                result.Reason, StringComparison.Ordinal);
            Assert.False(result.Launched);
            Assert.DoesNotContain("Access is denied", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(launches);
            Assert.Null(result.TaskName);
        }
        finally { File.Delete(script); }
    }

    /// <summary>
    /// Session 0 keeps the scheduled-task path (a Register-ScheduledTask launch happens).
    /// </summary>
    [Fact]
    public async Task Session0_UsesScheduledTask()
    {
        string script = TempScript();
        try
        {
            var commands = new List<string>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                commands.Add(string.Join(" ", psi.ArgumentList));
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(1, string.Empty, "stop here"));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, new FakeProbe { CurrentSessionId = 0 },
                Runner, () => true, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("logix-mcp-s1-cancel", result.TaskName);
            Assert.Contains("Register-ScheduledTask", Assert.Single(commands));
        }
        finally { File.Delete(script); }
    }

    private static async Task<string> RegisterCommandForSession0Async(bool elevated)
    {
        string script = TempScript();
        try
        {
            var commands = new List<string>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                commands.Add(string.Join(" ", psi.ArgumentList));
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(1, string.Empty, "stop here"));
            }

            await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null,
                new FakeProbe { CurrentSessionId = 0, IsElevated = elevated },
                Runner, () => true, CancellationToken.None);

            return Assert.Single(commands);
        }
        finally { File.Delete(script); }
    }

    /// <summary>Session 0 with an elevated token registers the task with RunLevel Highest.</summary>
    [Fact]
    public async Task Session0_Elevated_RegistersTaskRunLevelHighest()
    {
        var command = await RegisterCommandForSession0Async(elevated: true);
        Assert.Contains("Register-ScheduledTask", command);
        Assert.Contains("-RunLevel Highest", command);
        Assert.DoesNotContain("-RunLevel Limited", command);
        Assert.Contains("'logix-mcp-s1-cancel'", command);
    }

    /// <summary>Session 0 without elevation registers the task with RunLevel Limited.</summary>
    [Fact]
    public async Task Session0_NotElevated_RegistersTaskRunLevelLimited()
    {
        var command = await RegisterCommandForSession0Async(elevated: false);
        Assert.Contains("Register-ScheduledTask", command);
        Assert.Contains("-RunLevel Limited", command);
        Assert.DoesNotContain("-RunLevel Highest", command);
        Assert.Contains("'logix-mcp-s1-cancel'", command);
    }

    /// <summary>
    /// The direct path parses the redirect file exactly as the task path does
    /// (both go through ParseResultJson) and reports a timeout/no-result as a failure.
    /// </summary>
    [Fact]
    public async Task DirectLaunch_ParsesResultJson_SameAsTaskPath()
    {
        string script = TempScript();
        try
        {
            // Child writes its RESULT_JSON to the redirect file named in the arguments.
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                var m = System.Text.RegularExpressions.Regex.Match(psi.Arguments, @"\*> '([^']+)'");
                Assert.True(m.Success);
                File.WriteAllText(m.Groups[1].Value, "chatter\n" + ResultLine + "\n");
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, string.Empty, string.Empty));
            }

            var direct = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } },
                Runner, () => true, CancellationToken.None);

            string logFile = Path.Combine(Path.GetTempPath(), $"s1-task-{Guid.NewGuid():N}.log");
            try
            {
                File.WriteAllText(logFile, "chatter\n" + ResultLine + "\n");
                var viaTask = await Session1ScriptRunner.AwaitResultFromRedirectFileAsync(
                    _ => Task.FromResult("Ready"), logFile, "logix-mcp-s1-cancel",
                    TimeSpan.FromSeconds(1), CancellationToken.None);

                Assert.Equal(viaTask.Ok, direct.Ok);
                Assert.Equal(viaTask.Reason, direct.Reason);
                Assert.Equal(viaTask.Screenshots, direct.Screenshots);
                Assert.Equal(viaTask.RawResultJsonLine, direct.RawResultJsonLine);
                Assert.Equal(viaTask.Malformed, direct.Malformed);
            }
            finally { File.Delete(logFile); }

            // No RESULT_JSON anywhere -> failure that mentions the timeout budget.
            Task<Session1ScriptRunner.ProcessRunResult?> TimedOut(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _) =>
                Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(-1, "nothing", string.Empty, TimedOut: true));
            var failed = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } },
                TimedOut, () => true, CancellationToken.None);
            Assert.False(failed.Ok);
            Assert.Contains("180s timeout budget", failed.Reason);
        }
        finally { File.Delete(script); }
    }

    private static Task<Session1ScriptRunner.ProcessRunResult?> Fixed(int exit, string stdout, bool timedOut = false) =>
        Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
            new Session1ScriptRunner.ProcessRunResult(exit, stdout, string.Empty, timedOut));

    /// <summary>
    /// Every payload -- success, refusals, timeout,
    /// parse failure, launch failure, not-windows, script-not-found -- carries
    /// launchMode; task-mode payloads also carry runLevel (direct mode has none).
    /// </summary>
    [Fact]
    public async Task EveryPayload_CarriesLaunchMode()
    {
        string script = TempScript();
        try
        {
            var direct = new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } };

            Task<Session1ScriptRunner.ProcessRunResult?> Ok(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) => Fixed(0, ResultLine + "\n");
            Task<Session1ScriptRunner.ProcessRunResult?> Malformed(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) =>
                Fixed(0, "RESULT_JSON: {not json\n");
            Task<Session1ScriptRunner.ProcessRunResult?> TimedOut(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) =>
                Fixed(-1, "nothing", timedOut: true);
            Task<Session1ScriptRunner.ProcessRunResult?> NoStart(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) =>
                Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(null);
            Task<Session1ScriptRunner.ProcessRunResult?> LauncherFails(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) => Fixed(1, string.Empty);
            Task<Session1ScriptRunner.ProcessRunResult?> Throws(
                System.Diagnostics.ProcessStartInfo p, TimeSpan? t, CancellationToken _) =>
                throw new InvalidOperationException("boom");

            // ── direct mode: launchMode "direct", no runLevel ───────────────
            var directResults = new List<Session1ScriptResult>
            {
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, Ok, () => true, default),
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, Malformed, () => true, default),
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, TimedOut, () => true, default),
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, NoStart, () => true, default),
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, Throws, () => true, default),
                // refusals
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null,
                    new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 2 } }, Ok, () => true, default),
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null,
                    new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 },
                                    StudioIntegrityHigherThanCurrent = true }, Ok, () => true, default),
                // not windows / script not found
                await Session1ScriptRunner.RunCoreAsync("cancel", script, null, direct, Ok, () => false, default),
                await Session1ScriptRunner.RunCoreAsync("cancel",
                    Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ps1"), null, direct, Ok, () => true, default),
            };
            Assert.True(directResults[0].Ok);
            Assert.True(directResults[1].Malformed);
            Assert.StartsWith("studio_not_in_this_session", directResults[5].Reason, StringComparison.Ordinal);
            Assert.StartsWith("studio_elevated_driver_not", directResults[6].Reason, StringComparison.Ordinal);
            foreach (var r in directResults)
            {
                Assert.Equal("direct", r.LaunchMode);
                Assert.Null(r.RunLevel);
            }

            // ── task mode (session 0): launchMode "scheduled_task" + runLevel ─
            foreach (var (elevated, level) in new[] { (true, "Highest"), (false, "Limited") })
            {
                var task = new FakeProbe { CurrentSessionId = 0, IsElevated = elevated };
                var taskResults = new List<Session1ScriptResult>
                {
                    await Session1ScriptRunner.RunCoreAsync("cancel", script, null, task, NoStart, () => true, default),
                    await Session1ScriptRunner.RunCoreAsync("cancel", script, null, task, LauncherFails, () => true, default),
                    await Session1ScriptRunner.RunCoreAsync("cancel", script, null, task, Throws, () => true, default),
                    await Session1ScriptRunner.RunCoreAsync("cancel", script, null, task, Ok, () => false, default),
                    await Session1ScriptRunner.RunCoreAsync("cancel",
                        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ps1"), null, task, Ok, () => true, default),
                };
                foreach (var r in taskResults)
                {
                    Assert.False(r.Ok);
                    Assert.Equal("scheduled_task", r.LaunchMode);
                    Assert.Equal(level, r.RunLevel);
                }
            }
        }
        finally { File.Delete(script); }
    }

    // ── Studio integrity check scoped to this session ──────

    private const int MediumRid = 0x2000;
    private const int HighRid = 0x3000;

    [Fact]
    public void Integrity_HigherStudioInOtherSession_Ignored()
    {
        var studios = new[] { (session: 2, rid: (int?)HighRid) };
        Assert.False(EnvironmentProbe.EvaluateStudioIntegrity(1, MediumRid, studios));
    }

    [Fact]
    public void Integrity_UnreadableTokenInOtherSession_Ignored()
    {
        var studios = new[] { (session: 2, rid: (int?)null) };
        Assert.False(EnvironmentProbe.EvaluateStudioIntegrity(1, MediumRid, studios));
    }

    [Fact]
    public void Integrity_HigherStudioInThisSession_Refused()
    {
        var studios = new[] { (session: 1, rid: (int?)HighRid) };
        Assert.True(EnvironmentProbe.EvaluateStudioIntegrity(1, MediumRid, studios));
    }

    [Fact]
    public void Integrity_UnreadableTokenInThisSession_Refused()
    {
        var studios = new[] { (session: 1, rid: (int?)null) };
        Assert.True(EnvironmentProbe.EvaluateStudioIntegrity(1, MediumRid, studios));
    }

    /// <summary>
    /// The runner's pre-launch refusals (not Windows, script not found) never
    /// start a process and report Launched=false; a run that did start reports
    /// Launched=true even when the driver fails.
    /// </summary>
    [Fact]
    public async Task PreLaunchRefusals_ReportLaunchedFalse_RealRunsLaunchedTrue()
    {
        var launches = 0;
        Task<Session1ScriptRunner.ProcessRunResult?> Runner(
            System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
        {
            launches++;
            return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                new Session1ScriptRunner.ProcessRunResult(1, "no result line\n", string.Empty));
        }
        var probe = new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } };

        var notWindows = await Session1ScriptRunner.RunCoreAsync(
            "cancel", "C:\\nowhere\\studio-edits.ps1", null, probe, Runner, () => false, CancellationToken.None);
        Assert.False(notWindows.Ok);
        Assert.False(notWindows.Launched);

        var missing = await Session1ScriptRunner.RunCoreAsync(
            "cancel", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ps1"), null,
            probe, Runner, () => true, CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.False(missing.Launched);
        Assert.Equal(0, launches);

        string script = TempScript();
        try
        {
            var ran = await Session1ScriptRunner.RunCoreAsync(
                "cancel", script, null, probe, Runner, () => true, CancellationToken.None);
            Assert.False(ran.Ok);
            Assert.True(ran.Launched);
            Assert.Equal(1, launches);
        }
        finally { File.Delete(script); }
    }

    /// <summary>
    /// ParseResultJson surfaces the driver's gestureTier, and leaves it null when absent.
    /// </summary>
    [Fact]
    public void ParseResultJson_SurfacesGestureTier_WhenPresent()
    {
        var with = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":true,\"reason\":\"x\",\"screenshots\":[],\"gestureTier\":\"posted\"}\n");
        Assert.Equal("posted", with!.GestureTier);

        var without = Session1ScriptRunner.ParseResultJson(ResultLine + "\n");
        Assert.Null(without!.GestureTier);
    }

    /// <summary>
    /// The Errors-pane toggle fields are parsed when present (true and false) and default to false when an
    /// older script omits them.
    /// </summary>
    [Fact]
    public void ParseResultJson_ParsesErrorsPaneToggleFields_PresentTrue()
    {
        var r = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":true,\"reason\":\"x\",\"screenshots\":[]," +
            "\"errorsPaneToggled\":true,\"retriedAfterPaneToggle\":true,\"paneToggleRestored\":true}\n");
        Assert.True(r!.ErrorsPaneToggled);
        Assert.True(r.RetriedAfterPaneToggle);
        Assert.True(r.PaneToggleRestored);
    }

    [Fact]
    public void ParseResultJson_ParsesErrorsPaneToggleFields_PresentFalse()
    {
        var r = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":false,\"reason\":\"command_not_applicable\",\"screenshots\":[]," +
            "\"errorsPaneToggled\":false,\"retriedAfterPaneToggle\":false,\"paneToggleRestored\":false}\n");
        Assert.False(r!.ErrorsPaneToggled);
        Assert.False(r.RetriedAfterPaneToggle);
        Assert.False(r.PaneToggleRestored);
    }

    [Fact]
    public void ParseResultJson_ErrorsPaneToggleFields_DefaultToFalseWhenAbsent()
    {
        var r = Session1ScriptRunner.ParseResultJson(ResultLine + "\n");
        Assert.False(r!.ErrorsPaneToggled);
        Assert.False(r.RetriedAfterPaneToggle);
        Assert.False(r.PaneToggleRestored);
    }

    /// <summary><c>errorsPaneLayout</c> is parsed when present, absent when missing, and absent when malformed.</summary>
    [Theory]
    [InlineData("auto-hide")]
    [InlineData("docked")]
    [InlineData("hidden")]
    [InlineData("unknown")]
    public void ParseResultJson_ParsesErrorsPaneLayout_Present(string layout)
    {
        var r = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":true,\"reason\":\"x\",\"screenshots\":[]," +
            "\"errorsPaneToggled\":true,\"errorsPaneLayout\":\"" + layout + "\"}\n");
        Assert.Equal(layout, r!.ErrorsPaneLayout);
    }

    [Fact]
    public void ParseResultJson_ErrorsPaneLayout_AbsentWhenMissing()
    {
        var r = Session1ScriptRunner.ParseResultJson(ResultLine + "\n");
        Assert.Null(r!.ErrorsPaneLayout);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{\"a\":1}")]
    [InlineData("\"sideways\"")]
    [InlineData("\"\"")]
    public void ParseResultJson_ErrorsPaneLayout_MalformedIsAbsent(string rawValue)
    {
        var r = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":true,\"reason\":\"x\",\"screenshots\":[],\"errorsPaneLayout\":" + rawValue + "}\n");
        Assert.NotNull(r);
        Assert.False(r!.Malformed);
        Assert.Null(r.ErrorsPaneLayout);
    }

    // ── The runner carries test / untest / assemble ───────────────

    /// <summary>
    /// The runner is verb-agnostic: test, untest and assemble reach the driver
    /// as -Verb / -Program, get their own task names, and the driver's
    /// dialogWarnings stay readable from the parsed RESULT_JSON.
    /// </summary>
    [Fact]
    public void TestUntestAssembleVerbs_ReachTheDriver_AndDialogWarningsSurvive()
    {
        foreach (var verb in new[] { "test", "untest", "assemble" })
        {
            Assert.Equal($"logix-mcp-s1-{verb}", Session1ScriptRunner.BuildTaskName(verb));

            var action = Session1ScriptRunner.BuildActionArgument(
                @"C:\b\studio-edits.ps1", verb, @"C:\tmp\o.log", "MainProgram");
            Assert.Contains($"-Verb '{verb}' -Program 'MainProgram' *> ", action, StringComparison.Ordinal);
        }

        var parsed = Session1ScriptRunner.ParseResultJson(
            "RESULT_JSON: {\"ok\":true,\"reason\":\"test_gesture_sent\",\"screenshots\":[],\"gestureTier\":\"posted\"," +
            "\"dialogWarnings\":[\"Outputs in D rung zones stay.\"]}\n");
        Assert.NotNull(parsed);
        Assert.Equal("posted", parsed!.GestureTier);
        var warnings = parsed.ParsedResultJson!.Value.GetProperty("dialogWarnings");
        Assert.Equal("Outputs in D rung zones stay.", warnings[0].GetString());
    }

    // ── The program reaches the driver ─────────────────────

    /// <summary>
    /// studio-edits.ps1 checks the confirmation dialog against
    /// -Program and presses No on a mismatch, so the action string must carry
    /// the tool's program (single-quoted, embedded quotes doubled). Without a
    /// program the string is unchanged.
    /// </summary>
    [Fact]
    public void ActionString_CarriesProgram_WhenGiven()
    {
        var withProgram = Session1ScriptRunner.BuildActionArgument(
            @"C:\b\studio-edits.ps1", "accept", @"C:\tmp\o.log", "Main'Program");
        Assert.Contains("-Verb 'accept' -Program 'Main''Program' *> ", withProgram, StringComparison.Ordinal);
        Assert.Equal(2, withProgram.Count(c => c == '"'));

        var without = Session1ScriptRunner.BuildActionArgument(@"C:\b\studio-edits.ps1", "status", @"C:\tmp\o.log");
        Assert.DoesNotContain("-Program", without, StringComparison.Ordinal);
        Assert.Equal(without, Session1ScriptRunner.BuildActionArgument(@"C:\b\studio-edits.ps1", "status", @"C:\tmp\o.log", ""));
    }

    [Fact]
    public void ScheduledTaskCommand_CarriesProgram()
    {
        var command = Session1ScriptRunner.BuildCommand(
            @"C:\b\studio-edits.ps1", "cancel", @"C:\tmp\o.log", elevated: false,
            taskName: Session1ScriptRunner.EditsTaskName, program: "MainProgram");
        // Inside the outer single-quoted -Argument literal the inner quotes are doubled.
        Assert.Contains("-Program ''MainProgram''", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectLaunch_PassesProgramToDriver()
    {
        string script = TempScript();
        try
        {
            var launches = new List<System.Diagnostics.ProcessStartInfo>();
            Task<Session1ScriptRunner.ProcessRunResult?> Runner(
                System.Diagnostics.ProcessStartInfo psi, TimeSpan? timeout, CancellationToken _)
            {
                launches.Add(psi);
                return Task.FromResult<Session1ScriptRunner.ProcessRunResult?>(
                    new Session1ScriptRunner.ProcessRunResult(0, ResultLine + "\n", string.Empty));
            }

            var result = await Session1ScriptRunner.RunCoreAsync(
                "accept", script, null, new FakeProbe { CurrentSessionId = 1, LogixDesignerSessionIds = new[] { 1 } },
                Runner, () => true, CancellationToken.None, program: "MainProgram");

            Assert.True(result.Ok);
            var psi0 = Assert.Single(launches);
            Assert.Contains("-Verb 'accept' -Program 'MainProgram'", psi0.Arguments, StringComparison.Ordinal);
        }
        finally { File.Delete(script); }
    }

    /// <summary>The runner hands -ConfirmTimeoutSeconds to the driver as a bare integer, and omits it when unset.</summary>
    [Fact]
    public void BuildActionArgument_PassesConfirmTimeoutSeconds_OnlyWhenSet()
    {
        var without = Session1ScriptRunner.BuildActionArgument(@"C:\x\studio-edits.ps1", "cancel", @"C:\tmp\o.log");
        var with15  = Session1ScriptRunner.BuildActionArgument(
            @"C:\x\studio-edits.ps1", "cancel", @"C:\tmp\o.log", confirmTimeoutSeconds: 15);

        Assert.DoesNotContain("-ConfirmTimeoutSeconds", without, StringComparison.Ordinal);
        Assert.Contains(" -ConfirmTimeoutSeconds 15 *>", with15, StringComparison.Ordinal);
    }
}

#endif
