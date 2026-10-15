using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guards the public documentation (<c>README.md</c> and <c>docs/tool-reference.md</c>) against
/// drifting from the server's registered tool surface. The docs are read from the
/// directory <see cref="PublicGuard.DocsRoot"/> returns.
/// </summary>
public class PublicDocsTests
{
    private static readonly string PublicRoot = PublicGuard.DocsRoot();

    /// <summary>
    /// Every tool the no-SDK build registers (the public docs describe that build only).
    /// </summary>
    private static HashSet<string> GetAllToolNames() => PublicDocsScopeTests.NoSdkToolNames();

    /// <summary>First-cell tool names of markdown table rows, in document order.</summary>
    private static List<string> RowNames(string relativePath) =>
        Regex.Matches(
                File.ReadAllText(Path.Combine(PublicRoot, relativePath)),
                @"^\|\s*`(logix_[a-z_]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

    /// <summary>
    /// Every tool in an SDK build: the no-SDK tools plus the SDK adapter's tools. The README documents the
    /// whole 39-tool surface, SDK lane included, so it is checked against this set.
    /// </summary>
    private static HashSet<string> GetAllToolNamesIncludingSdk()
    {
        var all = GetAllToolNames();
        all.UnionWith(SdkToolNames.All);
        return all;
    }

    private static void AssertExactlyTheRegisteredTools(string relativePath, bool includeSdkTools = false)
    {
        var registered = includeSdkTools ? GetAllToolNamesIncludingSdk() : GetAllToolNames();
        Assert.NotEmpty(registered);

        var rows = RowNames(relativePath);
        Assert.NotEmpty(rows);

        var missing = registered.Except(rows).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"{relativePath} has no row for: {string.Join(", ", missing)}");

        var unknown = rows.Distinct().Except(registered).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            $"{relativePath} has rows for unregistered tools: {string.Join(", ", unknown)}");

        var duplicates = rows.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"{relativePath} has duplicate rows for: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void ToolReference_ListsEveryRegisteredTool()
    {
        AssertExactlyTheRegisteredTools(Path.Combine("docs", "tool-reference.md"), includeSdkTools: true);
    }

    /// <summary>
    /// Every tool the SDK adapter registers, discovered independently of the hand-kept
    /// <see cref="SdkToolNames.All"/> registry: <c>[McpServerTool]</c> methods in the adapter assembly
    /// (reflection) plus <c>[McpServerTool(Name = "...")]</c> attributes in <c>src/LogixMcp.SdkAdapter</c>
    /// source when that project is in the tree (so a non-SDK build still sees them).
    /// </summary>
    private static HashSet<string> DiscoverSdkAdapterTools()
    {
        var names = typeof(SdkToolNames).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
            .Where(a => a != null)
            .Select(a => a!.GetType().GetProperty("Name")?.GetValue(a) as string)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            // Real tools are all logix_*; the test assembly also holds [McpServerTool] fixtures
            // (arg_echo, boom, needs_arg) when SdkToolNames is compiled into it (non-SDK build).
            .Where(n => n.StartsWith("logix_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var adapterRoot = Path.Combine(PublicGuard.FindProjectRoot(), "src", "LogixMcp.SdkAdapter");
        if (Directory.Exists(adapterRoot))
        {
            var sep = Path.DirectorySeparatorChar;
            var registration = new Regex(@"McpServerTool\(\s*Name\s*=\s*""(logix_[a-z_]+)""");
            foreach (var file in Directory.EnumerateFiles(adapterRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                    continue;
                foreach (Match m in registration.Matches(File.ReadAllText(file)))
                    names.Add(m.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// Every tool registered by the SDK adapter (and every name in <see cref="SdkToolNames.All"/>) has a
    /// row in <c>docs/tool-reference.md</c>. Fails when a new SDK tool is registered without a doc entry,
    /// even if it was never added to the <see cref="SdkToolNames"/> registry.
    /// </summary>
    [Fact]
    public void ToolReference_HasEntryForEverySdkRegisteredTool()
    {
        var sdkTools = DiscoverSdkAdapterTools();
        sdkTools.UnionWith(SdkToolNames.All);
        Assert.NotEmpty(sdkTools);

        var rows = RowNames(Path.Combine("docs", "tool-reference.md")).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(rows);

        var missing = sdkTools.Except(rows).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"docs/tool-reference.md has no row for registered SDK tools: {string.Join(", ", missing)}");

        // The registry must not lag the registrations either (it feeds the other doc guards).
        var unlisted = DiscoverSdkAdapterTools().Except(SdkToolNames.All).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0,
            $"SdkToolNames.All is missing registered SDK tools: {string.Join(", ", unlisted)}");
    }

    /// <summary>
    /// The connect-status, pending-edits, lint and cross-reference rows describe the
    /// merged behaviour (finished connects, reconcile, pending-applied analysis view).
    /// </summary>
    [Fact]
    public void ToolReference_DescribesConnectStatusReconcileAndPendingAppliedBehaviour()
    {
        var text = File.ReadAllText(Path.Combine(PublicRoot, "docs", "tool-reference.md"));
        string Row(string tool) => Regex.Match(text, @"^\|\s*`" + tool + @"`\s*\|.*$", RegexOptions.Multiline).Value;

        Assert.Contains("finished or in progress", Row("logix_connect_status"));
        var pending = Row("logix_pending_edits");
        Assert.Contains("reconciled", pending);
        Assert.Contains("session image", pending);
        Assert.Contains("read-only", pending);
        Assert.Contains("pending-applied", Row("logix_lint"));
        Assert.Contains("pending-applied", Row("logix_cross_reference"));
    }

    [Fact]
    public void ToolReference_EveryRowHasFourPopulatedColumns()
    {
        var text = File.ReadAllText(Path.Combine(PublicRoot, "docs", "tool-reference.md"));
        var rows = Regex.Matches(text, @"^\|\s*`logix_[a-z_]+`\s*\|.*$", RegexOptions.Multiline);
        Assert.NotEmpty(rows);

        foreach (Match row in rows)
        {
            // Split on unescaped pipes (the docs escape literal pipes as \|).
            var cells = Regex.Split(row.Value.Trim().Trim('|'), @"(?<!\\)\|")
                .Select(c => c.Trim()).ToList();
            Assert.True(cells.Count == 4 && cells.All(c => c.Length > 0),
                $"Row must have name, purpose, what it can change, gates/flags: {row.Value[..Math.Min(60, row.Value.Length)]}");
        }
    }

    [Fact]
    public void Readme_ToolOverview_ListsEveryRegisteredTool_GroupedAsDocumented()
    {
        AssertExactlyTheRegisteredTools("README.md", includeSdkTools: true);
        Assert.Equal(42, GetAllToolNamesIncludingSdk().Count);

        var readme = File.ReadAllText(Path.Combine(PublicRoot, "README.md"));
        var overview = readme[readme.IndexOf("## Tools", StringComparison.Ordinal)..];
        var headings = new[]
        {
            "### Read and analyze", "### Live writes", "### Setup",
            "### SDK lane: rung edits, build and test", "### SDK lane: Studio automation",
        };
        var last = -1;
        foreach (var h in headings)
        {
            var idx = overview.IndexOf(h, StringComparison.Ordinal);
            Assert.True(idx > last, $"README tool overview is missing or misorders '{h}'");
            last = idx;
        }
    }

    [Fact]
    public void SafetyModel_NoStaleAllowlistClaim()
    {
        var text = File.ReadAllText(Path.Combine(PublicRoot, "docs", "safety-model.md"));

        Assert.DoesNotContain("no allowlist", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LGX_WRITE_ALLOWLIST", text);
        Assert.Contains("External Access", text);
    }

    /// <summary>
    /// Every file of the public documentation set must pass the same banned-pattern
    /// list the shipped-text guard applies (bench IPs and hosts, client names,
    /// internal paths and tooling names).
    /// </summary>
    [Fact]
    public void PublicTree_PassesShippedTextGuard()
    {
        Assert.True(Directory.Exists(PublicRoot), $"Missing public tree at '{PublicRoot}'.");

        var files = PublicGuard.DocFiles();
        Assert.NotEmpty(files);

        // The docs are checked here for the generic patterns other than citations; PublicTreeGuardTests covers the
        // citation patterns across the whole tree.
        var violations = PublicGuard.Scan(PublicRoot, files, includeCitations: false);

        Assert.True(violations.Count == 0,
            $"Banned internal identifiers found in the public docs ({violations.Count}):\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void SecurityMd_NamesTheSecurityContact_AndHasNoPlaceholder()
    {
        var text = File.ReadAllText(Path.Combine(PublicRoot, "SECURITY.md"));
        Assert.Contains("cgray@" + "as" + "qi.org", text, StringComparison.Ordinal);
        Assert.DoesNotContain("security@" + "as" + "qi.org", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", text, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Errors_ListsEveryErrorKind()
    {
        var text = File.ReadAllText(Path.Combine(PublicRoot, "docs", "errors.md"));

        var kinds = PublicDocsScopeTests.NoSdkEmittedKinds();

        var missing = kinds
            .Where(k => !text.Contains($"`{k}`", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0,
            $"docs/errors.md does not document these error kinds: {string.Join(", ", missing)}");
    }
}
