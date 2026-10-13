using System.Diagnostics;

namespace LogixMcp.Tests;

/// <summary>
/// Guard tests: the publish bundle must ship
/// <c>bench/studio-edits.ps1</c> next to the executable, the same way
/// <c>bootstrap/</c> is shipped, so
/// <see cref="Session1ScriptRunner.FindDefaultScriptPath"/> can resolve it
/// beside the binary in a standalone publish output that has no
/// accompanying source tree to fall back to.
///
/// <para>
/// Two things are asserted:
/// </para>
///
/// <para>
/// (a) <b>Copy step</b>: the <c>Makefile</c> <c>publish-win</c> and
/// <c>publish-linux</c> targets must copy <c>bench/*.ps1</c> into
/// <c>&lt;publish&gt;/bench/</c>, before the bootstrap/bench completeness
/// guard runs.
/// </para>
///
/// <para>
/// (b) <b>Guard enforcement</b>: <c>scripts/verify-bootstrap.sh</c> (the
/// guard invoked directly from <c>publish-linux</c> and, via
/// <c>scripts/verify-vendor-closure.sh</c>, from <c>publish-win</c>) must
/// fail the publish and name <c>bench/studio-edits.ps1</c> when it is absent
/// from the output, and must pass once it is present. This is asserted by
/// actually executing the script against synthetic publish directories
/// (an outcome assertion, not an assumption about what the
/// script does), mirroring <see cref="PublishBootstrapInvariantTests"/>.
/// </para>
/// </summary>
public class PublishBundleInvariantTests
{
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

    private static readonly string ProjectRoot = FindProjectRoot();

    private static readonly string MakefilePath =
        Path.Combine(ProjectRoot, "Makefile");

    private static readonly string VerifyBootstrapScriptPath =
        Path.Combine(ProjectRoot, "scripts", "verify-bootstrap.sh");

    private static readonly string[] RequiredBootstrapScripts =
    {
        "setup.ps1",
        "cold-install-gate.ps1",
        "provision-sdk.ps1",
        "setup-mcp-client.ps1",
    };

    private static string ReadMakefile() => File.ReadAllText(MakefilePath);

    private static (int ExitCode, string Stdout, string Stderr) RunVerifyBootstrap(string publishDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList = { VerifyBootstrapScriptPath, publishDir },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bash process.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout, stderr);
    }
}
