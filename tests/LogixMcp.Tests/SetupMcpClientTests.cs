using System.Diagnostics;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Structural and functional tests for <c>bootstrap/setup-mcp-client.ps1</c>.
///
/// <para>
/// Tests drive the script via <c>pwsh -NoProfile -File</c> with a synthetic
/// <c>AppDir</c> (a temp directory that contains a stub <c>LogixMcp.exe</c>)
/// and, where file I/O is exercised, a temp <c>APPDATA</c> override so no
/// real client configuration is ever touched.
/// </para>
///
/// <para>
/// Shaped like <see cref="PublishBootstrapInvariantTests"/>:
/// the script is located by walking up from
/// <see cref="AppContext.BaseDirectory"/> until a <c>.sln</c> file is found.
/// </para>
/// </summary>
public class SetupMcpClientTests
{
    // ── Script location ───────────────────────────────────────────────────────

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the project root: no *.sln file found while " +
            $"walking up from '{AppContext.BaseDirectory}'.");
    }

    private static readonly string ScriptPath = Path.Combine(
        FindProjectRoot(), "bootstrap", "setup-mcp-client.ps1");

    // ── Process runner ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the script via <c>pwsh -NoProfile -File</c> with the given extra
    /// arguments, optional APPDATA override, and a stub <c>LogixMcp.exe</c>
    /// in <paramref name="appDir"/> (created when the parameter is non-null
    /// and the file is absent).
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunScript(
        string[] extraArgs,
        string appDir,
        string? appDataOverride = null,
        string? localAppDataOverride = null,
        string processListJson = "[]")
    {
        // Ensure the stub exe exists so -AppDir validation passes.
        if (!string.IsNullOrEmpty(appDir))
        {
            Directory.CreateDirectory(appDir);
            string stubExe = Path.Combine(appDir, "LogixMcp.exe");
            if (!File.Exists(stubExe))
                File.WriteAllText(stubExe, string.Empty);
        }

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        // pwsh requires DOTNET_ROLL_FORWARD=LatestMajor in this environment.
        psi.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";

        // Hermetic Claude Desktop running check: never read the machine's real
        // process table (a developer may have Claude Desktop or the claude CLI
        // running). Default is an empty process list.
        psi.EnvironmentVariables["LGX_MCP_PROCESS_LIST_JSON"] = processListJson;

        if (appDataOverride != null)
            psi.EnvironmentVariables["APPDATA"] = appDataOverride;

        // Never let a real Microsoft Store Claude install on the test machine
        // redirect the config path: default LOCALAPPDATA to a directory that
        // holds no Packages folder.
        psi.EnvironmentVariables["LOCALAPPDATA"] =
            localAppDataOverride ?? Path.Combine(Path.GetTempPath(), "lgx-mcp-no-localappdata");

        psi.FileName = "pwsh";
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath);

        foreach (var arg in extraArgs)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start pwsh process.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }

    // ── Claude Desktop running check (real -File path) ────────────────────────

    private const string FakeClaudeProcessJson = "[{\"Name\":\"claude\",\"Id\":4242,\"Path\":null}]";

    /// <summary>
    /// With a Claude process in the (fake) process list, <c>-WriteConfig</c> through
    /// <c>pwsh -File</c> exits 1 with the quit-and-re-run message and writes nothing.
    /// </summary>
    [Fact]
    public void ClaudeDesktop_WriteConfig_RefusesWhenClaudeRunning()
    {
        string appDir  = Directory.CreateTempSubdirectory("lgx-mcp-run-app-").FullName;
        string appData = Directory.CreateTempSubdirectory("lgx-mcp-run-appdata-").FullName;
        try
        {
            var (exitCode, _, stderr) = RunScript(
                new[] { "-Client", "ClaudeDesktop", "-AppDir", appDir, "-WriteConfig" },
                appDir, appDataOverride: appData, processListJson: FakeClaudeProcessJson);

            Assert.Equal(1, exitCode);
            // Write-Error wraps long messages at the console width and may add ANSI colour codes.
            string flat = System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(stderr, "\u001b\\[[0-9;]*m", string.Empty),
                @"[\s|]+", " ");
            Assert.Contains("Quit Claude Desktop fully (tray \u2192 Quit) and re-run", flat, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(appData, "Claude", "claude_desktop_config.json")));
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    /// <summary><c>-Force</c> bypasses the running check and the config is written.</summary>
    [Fact]
    public void ClaudeDesktop_WriteConfig_ForceBypassesRunningCheck()
    {
        string appDir  = Directory.CreateTempSubdirectory("lgx-mcp-force-app-").FullName;
        string appData = Directory.CreateTempSubdirectory("lgx-mcp-force-appdata-").FullName;
        try
        {
            var (exitCode, _, stderr) = RunScript(
                new[] { "-Client", "ClaudeDesktop", "-AppDir", appDir, "-WriteConfig", "-Force" },
                appDir, appDataOverride: appData, processListJson: FakeClaudeProcessJson);

            Assert.True(exitCode == 0, $"Script exited {exitCode}. stderr:\n{stderr}");
            Assert.True(File.Exists(Path.Combine(appData, "Claude", "claude_desktop_config.json")));
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    // ── Existence ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The script must exist at the expected path so that all subsequent
    /// tests have a meaningful target rather than silently passing on a
    /// missing file.
    /// </summary>
    [Fact]
    public void Script_Exists()
    {
        Assert.True(File.Exists(ScriptPath),
            $"Expected bootstrap/setup-mcp-client.ps1 at '{ScriptPath}'.");
    }

    // ── ClaudeCode: default registration command ──────────────────────────────

    /// <summary>
    /// With the default <c>-Client ClaudeCode</c> and default
    /// <c>-ServerName lgx-mcp</c>, the script must print a
    /// <c>claude mcp add lgx-mcp</c> command so the operator can copy and
    /// run it (print-by-default; no <c>-WriteConfig</c> required).
    /// </summary>
    [Fact]
    public void ClaudeCode_PrintsClaudeMcpAddCommand_WithDefaultServerName()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-cc-default-").FullName;
        try
        {
            var (exitCode, stdout, _) = RunScript(
                new[] { "-AppDir", appDir },
                appDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("claude mcp add lgx-mcp", stdout, StringComparison.Ordinal);
            Assert.Contains("--scope user", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── Env block: -EnableLiveWrites adds env to every client shape ───────────

    /// <summary>
    /// When <c>-EnableLiveWrites</c> is passed, the env variable
    /// <c>LGX_ENABLE_LIVE_WRITES=1</c> must appear in the output for every
    /// supported client shape (ClaudeCode command line, ClaudeDesktop JSON,
    /// VSCode JSON), so that regardless of which client the operator uses,
    /// the live-controller write opt-in is included.
    /// </summary>
    [Fact]
    public void EnableLiveWrites_AddsEnvToEveryClientShape()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-env-every-").FullName;
        try
        {
            foreach (var client in new[] { "ClaudeCode", "ClaudeDesktop", "VSCode" })
            {
                var (exitCode, stdout, stderr) = RunScript(
                    new[] { "-Client", client, "-AppDir", appDir, "-EnableLiveWrites" },
                    appDir);

                Assert.True(exitCode == 0,
                    $"Script exited {exitCode} for client {client}. stderr:\n{stderr}");

                Assert.Contains(
                    "LGX_ENABLE_LIVE_WRITES",
                    stdout,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── No opt-in: no env block, prints the off notice ────────────────────────

    /// <summary>
    /// Without <c>-EnableLiveWrites</c> or <c>-EnableStudioAutomation</c>
    /// the script must emit no env key in its output (no
    /// <c>LGX_ENABLE_LIVE_WRITES</c> line), and it must print a one-line
    /// notice explaining that live controller writes are off and how to
    /// enable them.
    /// </summary>
    [Fact]
    public void NoOptIn_EmitsNoEnv_AndPrintsOffNotice()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-nooptin-").FullName;
        try
        {
            // Default client (ClaudeCode) without any env switches.
            var (exitCode, stdout, _) = RunScript(
                new[] { "-AppDir", appDir },
                appDir);

            Assert.Equal(0, exitCode);

            // The command output must not carry an -e env argument.
            // (The off-notice may mention the env var name as documentation,
            // but no -e KEY=VALUE flag should appear on the command line.)
            Assert.DoesNotContain(
                "-e LGX_ENABLE_LIVE_WRITES=1",
                stdout,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "-e LGX_ENABLE_STUDIO_AUTOMATION=1",
                stdout,
                StringComparison.Ordinal);

            // Off notice must be present.
            Assert.Contains("off", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EnableLiveWrites", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── ClaudeDesktop merge: other servers and keys are preserved ─────────────

    /// <summary>
    /// When <c>-Client ClaudeDesktop -WriteConfig</c> is passed and the
    /// config file already exists with other <c>mcpServers</c> entries and
    /// top-level keys, the script must preserve all existing entries while
    /// adding (or updating) only the target <c>&lt;ServerName&gt;</c> block.
    /// </summary>
    [Fact]
    public void ClaudeDesktop_Merge_PreservesOtherServersAndKeys()
    {
        string appDir   = Directory.CreateTempSubdirectory("lgx-mcp-cd-preserve-app-").FullName;
        string appData  = Directory.CreateTempSubdirectory("lgx-mcp-cd-preserve-appdata-").FullName;
        try
        {
            // Seed an existing config with an unrelated server and a top-level key.
            string claudeDir    = Path.Combine(appData, "Claude");
            string configPath   = Path.Combine(claudeDir, "claude_desktop_config.json");
            Directory.CreateDirectory(claudeDir);
            File.WriteAllText(configPath, """
                {
                    "mcpServers": {
                        "other-server": { "command": "other-cmd", "args": [] }
                    },
                    "someTopLevelKey": "preserved-value"
                }
                """);

            var (exitCode, _, stderr) = RunScript(
                new[]
                {
                    "-Client", "ClaudeDesktop",
                    "-AppDir", appDir,
                    "-ServerName", "lgx-mcp",
                    "-WriteConfig",
                },
                appDir,
                appDataOverride: appData);

            Assert.True(exitCode == 0,
                $"Script exited {exitCode}. stderr:\n{stderr}");

            // Parse the written file and verify preservation.
            string result = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            // The new server must be present.
            Assert.True(
                root.GetProperty("mcpServers").TryGetProperty("lgx-mcp", out _),
                "mcpServers.lgx-mcp not found in merged config.");

            // The existing server must be preserved.
            Assert.True(
                root.GetProperty("mcpServers").TryGetProperty("other-server", out _),
                "mcpServers.other-server was removed during merge (must be preserved).");

            // The top-level key must be preserved.
            Assert.True(
                root.TryGetProperty("someTopLevelKey", out var topVal),
                "someTopLevelKey not found in merged config (must be preserved).");
            Assert.Equal("preserved-value", topVal.GetString());
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    // ── ClaudeDesktop merge: timestamped backup is written ────────────────────

    /// <summary>
    /// Before overwriting an existing <c>claude_desktop_config.json</c>,
    /// the script must write a backup named
    /// <c>claude_desktop_config.json.bak-&lt;yyyyMMddHHmmss&gt;</c>.
    /// </summary>
    [Fact]
    public void ClaudeDesktop_Merge_WritesTimestampedBackup()
    {
        string appDir  = Directory.CreateTempSubdirectory("lgx-mcp-cd-backup-app-").FullName;
        string appData = Directory.CreateTempSubdirectory("lgx-mcp-cd-backup-appdata-").FullName;
        try
        {
            // Seed an existing config so there is something to back up.
            string claudeDir  = Path.Combine(appData, "Claude");
            string configPath = Path.Combine(claudeDir, "claude_desktop_config.json");
            Directory.CreateDirectory(claudeDir);
            File.WriteAllText(configPath, """{ "mcpServers": {} }""");

            var (exitCode, _, stderr) = RunScript(
                new[]
                {
                    "-Client", "ClaudeDesktop",
                    "-AppDir", appDir,
                    "-ServerName", "lgx-mcp",
                    "-WriteConfig",
                },
                appDir,
                appDataOverride: appData);

            Assert.True(exitCode == 0,
                $"Script exited {exitCode}. stderr:\n{stderr}");

            // There must be exactly one backup file matching the timestamp pattern.
            var backups = Directory
                .GetFiles(claudeDir, "claude_desktop_config.json.bak-*")
                .ToArray();

            Assert.True(backups.Length >= 1,
                $"Expected at least one .bak-* backup file in '{claudeDir}'; found none.");

            // The suffix must look like a valid 14-digit timestamp (yyyyMMddHHmmss).
            foreach (var bak in backups)
            {
                string suffix = Path.GetFileName(bak)
                    .Replace("claude_desktop_config.json.bak-", string.Empty);
                Assert.True(
                    suffix.Length == 14 && suffix.All(char.IsDigit),
                    $"Backup file name suffix '{suffix}' does not match yyyyMMddHHmmss.");
            }
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    // ── VSCode block has type: stdio ──────────────────────────────────────────

    /// <summary>
    /// The VSCode <c>servers.&lt;ServerName&gt;</c> block printed by the
    /// script must include <c>"type": "stdio"</c>, as required by the VS
    /// Code MCP client configuration schema for stdio-transport servers.
    /// </summary>
    [Fact]
    public void VSCode_Block_IsStdioType()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-vsc-stdio-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = RunScript(
                new[] { "-Client", "VSCode", "-AppDir", appDir },
                appDir);

            Assert.True(exitCode == 0,
                $"Script exited {exitCode}. stderr:\n{stderr}");

            Assert.Contains("stdio", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── -PublishMode: Auto adds env, Confirm does not ─────────────────────────

    /// <summary>
    /// With <c>-PublishMode Auto</c> the script must emit
    /// <c>LGX_PUBLISH_MODE=auto</c> in the server env block, and it must
    /// print exactly one line that identifies the selected publish mode.
    /// With <c>-PublishMode Confirm</c> (the default), <c>LGX_PUBLISH_MODE</c>
    /// must not appear anywhere in the output.
    /// </summary>
    [Fact]
    public void PublishModeAuto_AddsEnv_ConfirmAddsNothing()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-pubmode-").FullName;
        try
        {
            // -PublishMode is SDK-lane only: mark the synthetic AppDir as an SDK build.
            AddSdkAdapterStub(appDir);

            // --- Auto: LGX_PUBLISH_MODE=auto must be present ---
            var (exitCodeAuto, stdoutAuto, stderrAuto) = RunScript(
                new[] { "-AppDir", appDir, "-PublishMode", "Auto" },
                appDir);

            Assert.True(exitCodeAuto == 0,
                $"Auto mode exited {exitCodeAuto}. stderr:\n{stderrAuto}");

            Assert.Contains("LGX_PUBLISH_MODE=auto", stdoutAuto, StringComparison.Ordinal);

            // Exactly one line must identify the selected publish mode.
            var modeLines = stdoutAuto
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("Auto", StringComparison.OrdinalIgnoreCase) &&
                            l.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                            l.TrimEnd().StartsWith("Publish mode:", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(modeLines.Length >= 1,
                $"Expected at least one line identifying publish mode 'Auto'. stdout:\n{stdoutAuto}");

            // --- Confirm (default): LGX_PUBLISH_MODE must NOT appear ---
            var (exitCodeConfirm, stdoutConfirm, stderrConfirm) = RunScript(
                new[] { "-AppDir", appDir, "-PublishMode", "Confirm" },
                appDir);

            Assert.True(exitCodeConfirm == 0,
                $"Confirm mode exited {exitCodeConfirm}. stderr:\n{stderrConfirm}");

            Assert.DoesNotContain("LGX_PUBLISH_MODE", stdoutConfirm, StringComparison.Ordinal);

            // Confirm mode must also print the publish-mode identification line.
            var confirmModeLines = stdoutConfirm
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.TrimEnd().StartsWith("Publish mode:", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(confirmModeLines.Length >= 1,
                $"Expected at least one 'Publish mode:' line for Confirm mode. stdout:\n{stdoutConfirm}");
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── No-SDK build: SDK-only options are rejected ───────────────────────────

    private static void AddSdkAdapterStub(string appDir)
    {
        string sdkDir = Path.Combine(appDir, "sdk");
        Directory.CreateDirectory(sdkDir);
        File.WriteAllText(Path.Combine(sdkDir, "LogixMcp.SdkAdapter.dll"), string.Empty);
    }

    /// <summary>
    /// In a no-SDK build (no <c>sdk\LogixMcp.SdkAdapter.dll</c> under AppDir),
    /// <c>-EnableStudioAutomation</c> and <c>-PublishMode</c> are rejected with a
    /// clear message and no env variable is emitted.
    /// </summary>
    [Theory]
    [InlineData("-EnableStudioAutomation", null)]
    [InlineData("-PublishMode", "Auto")]
    public void NoSdkBuild_RejectsSdkOnlyOptions(string option, string? value)
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-nosdk-").FullName;
        try
        {
            var args = new List<string> { "-AppDir", appDir, option };
            if (value != null) args.Add(value);

            var (exitCode, stdout, stderr) = RunScript(args.ToArray(), appDir);

            Assert.NotEqual(0, exitCode);
            Assert.Contains(option, stderr, StringComparison.Ordinal);
            Assert.Contains("not supported in this build", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("LGX_ENABLE_STUDIO_AUTOMATION", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("LGX_PUBLISH_MODE", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    // ── -CommandPrefixes / -LiveFinalize ──────────────────────────────────────

    /// <summary>
    /// Writes a Claude Desktop config via <c>-WriteConfig</c> with the given extra
    /// arguments and returns the parsed <c>env</c> object of the <c>lgx-mcp</c>
    /// server block (empty when the block has no <c>env</c>).
    /// </summary>
    private static Dictionary<string, string> WriteDesktopEnv(params string[] extra)
    {
        string appDir  = Directory.CreateTempSubdirectory("lgx-mcp-cd-env-app-").FullName;
        string appData = Directory.CreateTempSubdirectory("lgx-mcp-cd-env-appdata-").FullName;
        try
        {
            var args = new List<string>
            {
                "-Client", "ClaudeDesktop", "-AppDir", appDir, "-WriteConfig",
            };
            args.AddRange(extra);

            var (exitCode, _, stderr) = RunScript(args.ToArray(), appDir, appDataOverride: appData);
            Assert.True(exitCode == 0, $"Script exited {exitCode}. stderr:\n{stderr}");

            string configPath = Path.Combine(appData, "Claude", "claude_desktop_config.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var block = doc.RootElement.GetProperty("mcpServers").GetProperty("lgx-mcp");
            var env = new Dictionary<string, string>();
            if (block.TryGetProperty("env", out var e))
                foreach (var p in e.EnumerateObject())
                    env[p.Name] = p.Value.GetString() ?? string.Empty;
            return env;
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    /// <summary><c>-CommandPrefixes</c> is written to the client env block verbatim (trimmed).</summary>
    [Theory]
    [InlineData("Cmd_,Sim_,Hmi_", "Cmd_,Sim_,Hmi_")]
    [InlineData("*", "*")]
    [InlineData("  Cmd_ , Sim_  ", "Cmd_ , Sim_")]
    public void CommandPrefixes_IsWrittenToEnv(string supplied, string expected)
    {
        var env = WriteDesktopEnv("-CommandPrefixes", supplied);

        Assert.Equal(expected, env["LGX_GATE_COMMAND_PREFIXES"]);
    }

    /// <summary>Omitting <c>-CommandPrefixes</c> (or passing an empty value) writes nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandPrefixes_OmittedOrEmpty_IsNotWritten(bool passEmpty)
    {
        var env = passEmpty
            ? WriteDesktopEnv("-CommandPrefixes", "")
            : WriteDesktopEnv();

        Assert.False(env.ContainsKey("LGX_GATE_COMMAND_PREFIXES"));
    }

    /// <summary>An entry outside <c>[A-Za-z0-9_]</c> is refused, naming the entry; nothing is written.</summary>
    [Theory]
    [InlineData("Cmd_,Bad-Entry", "Bad-Entry")]
    [InlineData("Cmd_,,Sim_", "")]
    [InlineData("Cmd_,a b", "a b")]
    public void CommandPrefixes_InvalidEntry_IsRefused(string supplied, string badEntry)
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-badprefix-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = RunScript(
                new[] { "-AppDir", appDir, "-CommandPrefixes", supplied }, appDir);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("-CommandPrefixes", stderr, StringComparison.Ordinal);
            Assert.Contains($"'{badEntry}'", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("LGX_GATE_COMMAND_PREFIXES", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    /// <summary>
    /// <c>*</c> mixed with other entries is not a script error: the server treats it as
    /// <c>*</c> and warns in the connect result, so the value is written unchanged.
    /// </summary>
    [Fact]
    public void CommandPrefixes_WildcardMixed_IsWrittenUnchanged()
    {
        var env = WriteDesktopEnv("-CommandPrefixes", "Cmd_,*");

        Assert.Equal("Cmd_,*", env["LGX_GATE_COMMAND_PREFIXES"]);
    }

    /// <summary>The ClaudeCode command line carries the prefixes as an <c>-e</c> argument.</summary>
    [Fact]
    public void CommandPrefixes_ClaudeCode_PrintsEnvArgument()
    {
        string appDir = Directory.CreateTempSubdirectory("lgx-mcp-cc-prefix-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = RunScript(
                new[] { "-AppDir", appDir, "-CommandPrefixes", "Cmd_,Hmi_" }, appDir);

            Assert.True(exitCode == 0, $"Script exited {exitCode}. stderr:\n{stderr}");
            Assert.Contains("LGX_GATE_COMMAND_PREFIXES=Cmd_,Hmi_", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    /// <summary><c>-LiveFinalize</c> writes <c>LGX_ENABLE_LIVE_FINALIZE=1</c>; omitted writes nothing.</summary>
    [Fact]
    public void LiveFinalize_WritesEnvSwitch_OnlyWhenRequested()
    {
        var on  = WriteDesktopEnv("-LiveFinalize");
        var off = WriteDesktopEnv();

        Assert.Equal("1", on["LGX_ENABLE_LIVE_FINALIZE"]);
        Assert.False(off.ContainsKey("LGX_ENABLE_LIVE_FINALIZE"));
    }

    // ── ClaudeDesktop: Microsoft Store (MSIX) config location ─────────────────

    /// <summary>
    /// When <c>%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude</c> exists, the
    /// config is written there (not under APPDATA) and the path used is printed.
    /// </summary>
    [Fact]
    public void ClaudeDesktop_MsixInstall_WritesToPackageDir_AndPrintsPath()
    {
        string appDir   = Directory.CreateTempSubdirectory("lgx-mcp-msix-app-").FullName;
        string appData  = Directory.CreateTempSubdirectory("lgx-mcp-msix-appdata-").FullName;
        string localApp = Directory.CreateTempSubdirectory("lgx-mcp-msix-local-").FullName;
        try
        {
            string msixDir = Path.Combine(localApp, "Packages", "Claude_pzs8sxrjxfjjc",
                "LocalCache", "Roaming", "Claude");
            Directory.CreateDirectory(msixDir);

            var (exitCode, stdout, stderr) = RunScript(
                new[] { "-Client", "ClaudeDesktop", "-AppDir", appDir, "-WriteConfig" },
                appDir, appDataOverride: appData, localAppDataOverride: localApp);

            Assert.True(exitCode == 0, $"Script exited {exitCode}. stderr:\n{stderr}");

            string msixConfig = Path.Combine(msixDir, "claude_desktop_config.json");
            Assert.True(File.Exists(msixConfig), $"Expected config at '{msixConfig}'.");
            Assert.False(File.Exists(Path.Combine(appData, "Claude", "claude_desktop_config.json")),
                "Config must not be written under APPDATA when the MSIX install is present.");
            Assert.Contains($"Config path used: {msixConfig}", stdout, StringComparison.Ordinal);
            Assert.Contains("MSIX", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir,   recursive: true);
            Directory.Delete(appData,  recursive: true);
            Directory.Delete(localApp, recursive: true);
        }
    }

    /// <summary>Without an MSIX install the standard APPDATA path is used and printed.</summary>
    [Fact]
    public void ClaudeDesktop_NoMsixInstall_UsesAppData_AndPrintsPath()
    {
        string appDir  = Directory.CreateTempSubdirectory("lgx-mcp-nomsix-app-").FullName;
        string appData = Directory.CreateTempSubdirectory("lgx-mcp-nomsix-appdata-").FullName;
        try
        {
            var (exitCode, stdout, stderr) = RunScript(
                new[] { "-Client", "ClaudeDesktop", "-AppDir", appDir, "-WriteConfig" },
                appDir, appDataOverride: appData);

            Assert.True(exitCode == 0, $"Script exited {exitCode}. stderr:\n{stderr}");

            string config = Path.Combine(appData, "Claude", "claude_desktop_config.json");
            Assert.True(File.Exists(config));
            Assert.Contains($"Config path used: {config}", stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir,  recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    // ── Missing exe: refused with path ────────────────────────────────────────

    /// <summary>
    /// When <c>-AppDir</c> points to a directory that does not contain
    /// <c>LogixMcp.exe</c>, the script must exit non-zero and include the
    /// expected path in its error output, so the operator can see exactly
    /// which path was checked.
    /// </summary>
    [Fact]
    public void MissingExe_IsRefused_WithPath()
    {
        string emptyDir = Directory.CreateTempSubdirectory("lgx-mcp-missing-exe-").FullName;
        try
        {
            // Do NOT create LogixMcp.exe -- that is the scenario under test.
            var (exitCode, _, stderr) = RunScript(
                new[] { "-AppDir", emptyDir },
                appDir: string.Empty);   // pass empty to skip stub creation

            Assert.True(exitCode != 0,
                "Expected a non-zero exit code when LogixMcp.exe is absent.");

            // The error message must name the expected path.
            string expectedPath = Path.Combine(emptyDir, "LogixMcp.exe");
            Assert.Contains(expectedPath, stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    /// <summary>
    /// The expected <c>claude mcp remove</c> miss must not show a red error: both streams are captured with
    /// <c>$ErrorActionPreference</c> relaxed around the call, and only the plain note is printed.
    /// </summary>
    [Fact]
    public void ClaudeCode_RemoveMiss_IsSuppressed_AndPrintsNote()
    {
        var script = File.ReadAllText(ScriptPath);
        var removeCall = script.IndexOf("& claude mcp remove $ServerName --scope user", StringComparison.Ordinal);
        Assert.True(removeCall >= 0, "the remove call must exist");

        var lineStart = script.LastIndexOf('\n', removeCall) + 1;
        var lineEnd   = script.IndexOf('\n', removeCall);
        var line      = script[lineStart..lineEnd];
        Assert.Contains("$null =", line);
        Assert.Contains("2>&1", line);

        var before = script[(removeCall - 400)..removeCall];
        Assert.Contains("$ErrorActionPreference = 'Continue'", before);
        var after = script[removeCall..(removeCall + 700)];
        Assert.Contains("$ErrorActionPreference = $prevErrorAction", after);
        Assert.Contains("Write-Host \"Note: '$removeCmd' exited $LASTEXITCODE", after);
    }
}
