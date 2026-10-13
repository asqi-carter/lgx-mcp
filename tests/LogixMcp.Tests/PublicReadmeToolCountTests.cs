using System.Text.RegularExpressions;
using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// Guards the tool-count sentence and the tool tables in the public <c>README.md</c> against the tools the
/// build under test registers. The sentence states the core-build count and the number of tools that exist
/// only in an SDK build; the tables must hold exactly one row per registered tool.
/// </summary>
public class PublicReadmeToolCountTests
{
    /// <summary>Matches "42 tools, each with exactly one row below: 29 in the core build, and 13 more".</summary>
    private static readonly Regex CountSentence = new(
        @"(?<total>\d+)\s+tools,\s+each\s+with\s+exactly\s+one\s+row\s+below:\s+(?<core>\d+)\s+in\s+the\s+core\s+build,\s+and\s+(?<sdk>\d+)\s+more",
        RegexOptions.CultureInvariant);

    private static readonly Regex ToolRow = new(@"^\|\s*`(?<name>logix_[a-z_]+)`\s*\|", RegexOptions.Multiline);

    /// <summary>The README path, or null when this lane's tree carries no README.</summary>
    private static string? ReadmePath()
    {
        var path = Path.Combine(PublicGuard.DocsRoot(), "README.md");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Tools only an SDK build registers: the names in <see cref="SdkToolNames.All"/>, plus every
    /// <c>[McpServerTool(Name = "...")]</c> in the adapter source when that project is in the tree, less the
    /// core build's tools.
    /// </summary>
    private static HashSet<string> SdkOnlyTools(HashSet<string> core)
    {
        var names = new HashSet<string>(SdkToolNames.All, StringComparer.Ordinal);
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
        names.ExceptWith(core);
        return names;
    }

    /// <summary>Every tool the core (no-SDK) build registers, discovered by reflection over the server assembly.</summary>
    private static HashSet<string> CoreTools() => PublicDocsScopeTests.NoSdkToolNames();

    [SkippableFact]
    public void Readme_CountSentence_StatesTheRealCoreAndSdkToolCounts()
    {
        var path = ReadmePath();
        Skip.If(path is null, "README.md is not part of this lane's tree (stripped from the export); nothing to count.");

        var core = CoreTools();
        var sdkOnly = SdkOnlyTools(core);
        Assert.NotEmpty(core);
        Assert.NotEmpty(sdkOnly);

        var match = CountSentence.Match(File.ReadAllText(path!));
        Assert.True(match.Success,
            "README.md has no sentence of the form '<N> tools, each with exactly one row below: <C> in the core build, and <S> more'.");

        Assert.Equal(core.Count, int.Parse(match.Groups["core"].Value));
        Assert.Equal(sdkOnly.Count, int.Parse(match.Groups["sdk"].Value));
        Assert.Equal(core.Count + sdkOnly.Count, int.Parse(match.Groups["total"].Value));
    }

    [SkippableFact]
    public void Readme_Tables_HaveExactlyOneRowPerRegisteredTool()
    {
        var path = ReadmePath();
        Skip.If(path is null, "README.md is not part of this lane's tree (stripped from the export); nothing to count.");

        var core = CoreTools();
        var registered = new HashSet<string>(core, StringComparer.Ordinal);
        registered.UnionWith(SdkOnlyTools(core));

        var text = File.ReadAllText(path!);
        var tools = text[text.IndexOf("## Tools", StringComparison.Ordinal)..];
        var rows = ToolRow.Matches(tools).Select(m => m.Groups["name"].Value).ToList();
        Assert.NotEmpty(rows);

        var missing = registered.Except(rows).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var unknown = rows.Distinct().Except(registered).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var duplicates = rows.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(missing.Count == 0, $"README.md has no row for: {string.Join(", ", missing)}");
        Assert.True(unknown.Count == 0, $"README.md has rows for unregistered tools: {string.Join(", ", unknown)}");
        Assert.True(duplicates.Count == 0, $"README.md has duplicate rows for: {string.Join(", ", duplicates)}");
        Assert.Equal(registered.Count, rows.Count);
    }
}
