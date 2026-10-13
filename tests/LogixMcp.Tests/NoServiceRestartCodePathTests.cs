using System.Collections.Generic;
using System.IO;

namespace LogixMcp.Tests;

/// <summary>
/// Regression gate: asserts that no source file in <c>src/</c> contains any
/// service-restart primitive — i.e., no code path in the MCP server may call
/// <c>sc start</c>, <c>Start-Service</c>, <c>systemctl restart</c>, or any
/// equivalent OS-level service-control restart command.
///
/// <para>
/// Restarting system services from within an MCP server is inherently unsafe
/// in an industrial-automation context: it can interrupt live controller
/// sessions, corrupt in-flight tag writes, and bypass the GateEngine policy
/// that guards every write.  These tests act as a permanent CI gate so that
/// any future introduction of a service-restart call is caught before it
/// reaches a controller environment.
/// </para>
///
/// <para>
/// Verification strategy: each forbidden token gets its own <see cref="Fact"/>
/// test so that failures are surgical — a single violation points to exactly
/// one pattern rather than an aggregate list.  Each test enumerates every
/// <c>*.cs</c> file under <c>src/</c> and asserts that the token is absent.
/// The scan is case-sensitive where casing is fixed (C# identifiers) and
/// case-insensitive where shell invocations may vary.
/// </para>
///
/// <para>
/// Covered primitives:
/// <list type="bullet">
///   <item>Windows SC — <c>sc start</c>, <c>sc stop</c>, <c>sc restart</c></item>
///   <item>Windows NET — <c>net start</c>, <c>net stop</c></item>
///   <item>PowerShell cmdlets — <c>Start-Service</c>, <c>Stop-Service</c>,
///         <c>Restart-Service</c></item>
///   <item>systemd — <c>systemctl start</c>, <c>systemctl stop</c>,
///         <c>systemctl restart</c>, <c>systemctl reload</c></item>
///   <item>SysV init — <c>service … start</c>, <c>service … restart</c>,
///         <c>service … stop</c></item>
/// </list>
/// </para>
/// </summary>
public class NoServiceRestartCodePathTests
{
    // ── Helper: locate src/ ───────────────────────────────────────────────────

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until it finds a
    /// <c>src/</c> directory, or returns <c>null</c> when it cannot be located.
    /// </summary>
    private static string? LocateSrcDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir, "src");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    /// <summary>
    /// Asserts that the <c>src/</c> directory can be located and returns it.
    /// Fails the test with a descriptive message when it cannot be found.
    /// </summary>
    private static string RequireSrcDir()
    {
        var srcDir = LocateSrcDir();
        Assert.True(srcDir is not null && Directory.Exists(srcDir),
            "Could not locate the src/ directory from the test output path. " +
            "Ensure the solution layout has not changed.");
        return srcDir!;
    }

    /// <summary>
    /// Scans every <c>*.cs</c> file under <paramref name="srcDir"/> and returns
    /// the list of files that contain <paramref name="token"/>.
    /// </summary>
    /// <param name="srcDir">Root of the production source tree.</param>
    /// <param name="token">The literal string to search for.</param>
    /// <param name="comparison">String comparison mode (default: ordinal).</param>
    private static List<string> FilesContaining(
        string            srcDir,
        string            token,
        StringComparison  comparison = StringComparison.Ordinal)
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs",
            SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains(token, comparison))
                violations.Add(file);
        }
        return violations;
    }

    /// <summary>
    /// Builds the assertion failure message used by every scan test.
    /// </summary>
    private static string ViolationMessage(string token, IReadOnlyList<string> files) =>
        $"The forbidden service-restart token \"{token}\" was found in the " +
        $"following source file(s). Remove the invocation and replace it with " +
        $"an in-process mechanism that does not restart system services:\n" +
        string.Join("\n", files.Select(f => $"  - {f}"));

    // ═════════════════════════════════════════════════════════════════════════
    // Windows SC command
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain the string <c>"sc start"</c> (case-insensitive).
    /// This guards against shell-out invocations of the Windows Service Control
    /// manager's start command, e.g.
    /// <c>Process.Start("cmd", "/c sc start myservice")</c>.
    /// </summary>
    [Fact]
    public void SourceScan_NoScStart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "sc start",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("sc start", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"sc stop"</c> (case-insensitive).
    /// Stopping a service is a prerequisite for restarting it; even the stop
    /// half of a restart sequence is forbidden in production source.
    /// </summary>
    [Fact]
    public void SourceScan_NoScStop()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "sc stop",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("sc stop", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"sc restart"</c>
    /// (case-insensitive).  Although <c>sc.exe</c> does not have a first-class
    /// restart sub-command, the token would appear in any comment or script
    /// fragment that describes or orchestrates a restart sequence.
    /// </summary>
    [Fact]
    public void SourceScan_NoScRestart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "sc restart",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("sc restart", violations));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Windows NET command
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain the string <c>"net start"</c>
    /// (case-insensitive).  This guards against invocations of the legacy
    /// <c>net.exe start &lt;service&gt;</c> command.
    /// </summary>
    [Fact]
    public void SourceScan_NoNetStart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "net start",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("net start", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"net stop"</c>
    /// (case-insensitive).  Stopping a service via <c>net.exe</c> is the
    /// Windows-legacy equivalent of <c>sc stop</c>.
    /// </summary>
    [Fact]
    public void SourceScan_NoNetStop()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "net stop",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("net stop", violations));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PowerShell service cmdlets
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain the string <c>"Start-Service"</c>
    /// (ordinal, case-sensitive).  This is the PowerShell cmdlet for starting a
    /// Windows service and must not appear in any MCP server code path.
    /// </summary>
    [Fact]
    public void SourceScan_NoStartService()
    {
        var srcDir    = RequireSrcDir();
        // logix_doctor ADVISES the operator with the exact command text
        // "Start-Service '<name>'" in a returned string. DoctorHandler.cs is the one
        // exempt file; it has no process-launch API (DoctorHandlerTests
        // .DoctorSources_ContainNoMutatingCalls pins that), so nothing can run the text.
        var violations = FilesContaining(srcDir, "Start-Service",
                StringComparison.OrdinalIgnoreCase)
            .Where(f => !string.Equals(Path.GetFileName(f), "DoctorHandler.cs", StringComparison.Ordinal))
            .ToList();

        Assert.True(violations.Count == 0,
            ViolationMessage("Start-Service", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"Stop-Service"</c>
    /// (case-insensitive).  This PowerShell cmdlet stops a Windows service;
    /// it must not appear even as a preparatory step before a restart.
    /// </summary>
    [Fact]
    public void SourceScan_NoStopService()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "Stop-Service",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("Stop-Service", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"Restart-Service"</c>
    /// (case-insensitive).  This is the PowerShell cmdlet that atomically
    /// stops and restarts a Windows service — the most direct form of a
    /// service restart and therefore the highest-priority forbidden pattern.
    /// </summary>
    [Fact]
    public void SourceScan_NoRestartService()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "Restart-Service",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("Restart-Service", violations));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // systemd (Linux)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain the string <c>"systemctl start"</c>
    /// (case-insensitive).  This guards against shell-out invocations of the
    /// systemd service manager's start sub-command on Linux.
    /// </summary>
    [Fact]
    public void SourceScan_NoSystemctlStart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "systemctl start",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("systemctl start", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"systemctl stop"</c>
    /// (case-insensitive).
    /// </summary>
    [Fact]
    public void SourceScan_NoSystemctlStop()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "systemctl stop",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("systemctl stop", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"systemctl restart"</c>
    /// (case-insensitive).  This is the canonical Linux service-restart command
    /// and the highest-priority forbidden systemd pattern.
    /// </summary>
    [Fact]
    public void SourceScan_NoSystemctlRestart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "systemctl restart",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("systemctl restart", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"systemctl reload"</c>
    /// (case-insensitive).  A reload causes systemd to re-read the unit's
    /// configuration file and is effectively a partial restart; it must not
    /// appear in production source.
    /// </summary>
    [Fact]
    public void SourceScan_NoSystemctlReload()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "systemctl reload",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("systemctl reload", violations));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SysV init / Upstart (Linux legacy)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain the string <c>"service start"</c>
    /// (case-insensitive).  This guards against the legacy
    /// <c>service &lt;name&gt; start</c> pattern used on older Linux
    /// distributions before systemd was universal.
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceStart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "service start",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("service start", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"service stop"</c>
    /// (case-insensitive).
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceStop()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "service stop",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("service stop", violations));
    }

    /// <summary>
    /// No source file may contain the string <c>"service restart"</c>
    /// (case-insensitive).  This is the SysV-style service restart command
    /// and is forbidden for the same reasons as <c>systemctl restart</c>.
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceRestart()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "service restart",
            StringComparison.OrdinalIgnoreCase);

        Assert.True(violations.Count == 0,
            ViolationMessage("service restart", violations));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // .NET ServiceController API — control methods only
    //
    // NOTE: The MCP server legitimately uses System.ServiceProcess.ServiceController
    // in SdkConnectFactory.cs to READ the status of LdSdkService (a prerequisite
    // check — never a restart).  The tests below therefore do NOT ban the class
    // name outright; instead they ban the specific service-CONTROL methods that
    // would indicate a restart code path.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No source file may contain <c>WaitForStatus(</c> (ordinal, case-sensitive).
    /// <c>ServiceController.WaitForStatus</c> is used exclusively in service
    /// lifecycle control sequences — e.g., waiting for a service to reach
    /// <c>Running</c> after calling <c>Start()</c>, or <c>Stopped</c> after
    /// calling <c>Stop()</c>.  Its presence is a reliable indicator that a
    /// service-restart code path exists.
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceControllerWaitForStatus()
    {
        var srcDir    = RequireSrcDir();
        var violations = FilesContaining(srcDir, "WaitForStatus(",
            StringComparison.Ordinal);

        Assert.True(violations.Count == 0,
            ViolationMessage("WaitForStatus(", violations));
    }

    /// <summary>
    /// No source file that already uses <c>ServiceController</c> may also call
    /// <c>.Start(</c> on the controller instance (ordinal, case-sensitive).
    ///
    /// <para>
    /// The legitimate use of <c>ServiceController</c> in the server is limited to
    /// reading <c>sc.Status</c>.  Any file that both imports
    /// <c>ServiceController</c> AND calls <c>.Start(</c> is operating the service
    /// control manager as a restart primitive, which is forbidden.
    /// </para>
    ///
    /// <para>
    /// This test is narrower than banning <c>ServiceController</c> outright: it
    /// allows the existing status-read usage while catching any future attempt to
    /// extend that usage to actually start or restart the service.
    /// </para>
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceControllerStartCallInFilesUsingServiceController()
    {
        var srcDir    = RequireSrcDir();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs",
            SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // Only inspect files that reference ServiceController — files without
            // it cannot be using the API.
            if (!text.Contains("ServiceController", StringComparison.Ordinal))
                continue;

            // Within those files, look for the .Start( call pattern that would
            // indicate the controller is being told to start the service.
            // "sc.Start(" or ".Start(" appearing in service control context.
            if (text.Contains("sc.Start(", StringComparison.Ordinal) ||
                text.Contains(".Start(timeout", StringComparison.Ordinal) ||
                text.Contains("controller.Start(", StringComparison.Ordinal) ||
                text.Contains("serviceController.Start(", StringComparison.Ordinal))
            {
                violations.Add(file);
            }
        }

        Assert.True(violations.Count == 0,
            ViolationMessage("ServiceController.Start(", violations));
    }

    /// <summary>
    /// No source file that already uses <c>ServiceController</c> may also call
    /// <c>sc.Stop(</c> or <c>serviceController.Stop(</c> on the controller
    /// instance (ordinal, case-sensitive).
    ///
    /// <para>
    /// Stopping a service is a prerequisite for restarting it; the stop half of
    /// a restart sequence is equally forbidden even when done in isolation.
    /// </para>
    /// </summary>
    [Fact]
    public void SourceScan_NoServiceControllerStopCallInFilesUsingServiceController()
    {
        var srcDir    = RequireSrcDir();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs",
            SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("ServiceController", StringComparison.Ordinal))
                continue;

            if (text.Contains("sc.Stop(", StringComparison.Ordinal) ||
                text.Contains("controller.Stop(", StringComparison.Ordinal) ||
                text.Contains("serviceController.Stop(", StringComparison.Ordinal))
            {
                violations.Add(file);
            }
        }

        Assert.True(violations.Count == 0,
            ViolationMessage("ServiceController.Stop(", violations));
    }
}
