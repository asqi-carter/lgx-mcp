namespace LogixMcp.Tests;

/// <summary>
/// Layout tests for the bootstrap publish output: asserts that every file a
/// cold-install operator needs is present in the shipped <c>bootstrap/</c>
/// directory after <c>make publish-win</c> or <c>make publish-linux</c> runs.
///
/// <para>
/// Specifically covers the requirement that <c>setup-mcp-client.ps1</c>
/// must be shipped alongside the other three bootstrap scripts so the
/// operator can register the MCP server immediately after running
/// <c>setup.ps1</c>.
/// </para>
/// </summary>
public class BootstrapLayoutTests
{
    // ── Repo-root / file location ────────────────────────────────────────────

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

    private static readonly string BootstrapDir =
        Path.Combine(ProjectRoot, "bootstrap");

    private static readonly string VerifyBootstrapScriptPath =
        Path.Combine(ProjectRoot, "scripts", "verify-bootstrap.sh");

    private static string ReadVerifyBootstrapScript() =>
        File.ReadAllText(VerifyBootstrapScriptPath);

    // ── setup-mcp-client.ps1 is in the publish bootstrap output ─────────────
}
