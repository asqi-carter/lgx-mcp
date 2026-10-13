using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// Keeps the NOTICE file honest about third-party code: every package the no-SDK server
/// project ships is listed with its resolved version, licence and source URL, the licence texts exist
/// under <c>licenses/</c>, <c>make release-nosdk</c> copies them into the release archive, and NOTICE
/// carries no reference to the Rockwell SDK lane.
///
/// <para>
/// It runs wherever NOTICE sits at the repo root, including in the public tree.
/// <see cref="ExportedLayout.Resolve"/> says where NOTICE is read from.
/// </para>
/// </summary>
public class NoticeTests
{
    /// <summary>
    /// Packages that reach the server project's restore graph only when the optional SDK lane is
    /// enabled (<c>EnableLogixSdk=true</c>); they are not in the no-SDK publish output.
    /// </summary>
    private static readonly HashSet<string> SdkLaneOnly =
        new(StringComparer.OrdinalIgnoreCase) { "System.ServiceProcess.ServiceController" };

    private static string Root => PublicGuard.FindProjectRoot();

    private static string NoticePath()
    {
        return ExportedLayout.Resolve(Root, "NOTICE") ?? Path.Combine(Root, "NOTICE");
    }

    /// <summary>The runtime package closure of the server project from its restore graph: name -> resolved version.</summary>
    private static SortedDictionary<string, string> RuntimePackages()
    {
        var assets = Path.Combine(Root, "src", "LogixMcp", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"Restore output not found: {assets} (build the solution first).");
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));

        var target = doc.RootElement.GetProperty("targets").EnumerateObject().First().Value;
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in target.EnumerateObject())
        {
            var slash = lib.Name.IndexOf('/');
            if (lib.Value.GetProperty("type").GetString() != "package")
                continue;
            versions[lib.Name[..slash]] = lib.Name[(slash + 1)..];
            deps[lib.Name[..slash]] = lib.Value;
        }

        var roots = doc.RootElement.GetProperty("projectFileDependencyGroups").EnumerateObject().First().Value
            .EnumerateArray().Select(e => e.GetString()!.Split(' ')[0]).Where(n => !SdkLaneOnly.Contains(n));

        var seen = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (seen.ContainsKey(name) || !versions.TryGetValue(name, out var version))
                continue;
            seen[name] = version;
            if (deps[name].TryGetProperty("dependencies", out var d))
                foreach (var child in d.EnumerateObject())
                    stack.Push(child.Name);
        }
        return seen;
    }

    /// <summary>Table rows of NOTICE: package -> (version, licence, source).</summary>
    private static Dictionary<string, (string Version, string License, string Source)> NoticeRows()
    {
        var rows = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(NoticePath()))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal))
                continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length == 4)
                rows[cells[0]] = (cells[1], cells[2], cells[3]);
        }
        return rows;
    }

    /// <summary>Every package in the server project's runtime closure has a NOTICE row with version, licence and source URL.</summary>
    [Fact]
    public void EveryRuntimePackageListed()
    {
        var packages = RuntimePackages();
        Assert.NotEmpty(packages);
        // The packages the task names must be in the closure, or this test reads the wrong graph.
        foreach (var expected in new[] { "libplctag", "libplctag.NativeImport", "ModelContextProtocol",
                     "ModelContextProtocol.Extensions.Tasks", "L5Sharp", "Microsoft.Extensions.Hosting" })
            Assert.True(packages.ContainsKey(expected), $"{expected} missing from the restore graph");

        var rows = NoticeRows();
        var problems = new List<string>();
        foreach (var (name, version) in packages)
        {
            if (!rows.TryGetValue(name, out var row))
            {
                problems.Add($"{name} {version}: not listed in NOTICE");
                continue;
            }
            if (row.Version != version)
                problems.Add($"{name}: NOTICE says {row.Version}, restore resolved {version}");
            if (row.License.Length == 0)
                problems.Add($"{name}: no licence in NOTICE");
            if (!Regex.IsMatch(row.Source, @"https://[^\s>]+"))
                problems.Add($"{name}: no source URL in NOTICE");
        }
        Assert.True(problems.Count == 0, "NOTICE is out of step with the runtime packages:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>The <c>licenses/</c> directory holds the licence texts, including the full MPL-2.0 that NOTICE points to.</summary>
    [Fact]
    public void LicensesDirectoryHoldsTheTexts()
    {
        var dir = Path.Combine(Root, "licenses");
        Assert.True(Directory.Exists(dir), "licenses/ directory is missing");
        var mpl = File.ReadAllText(Path.Combine(dir, "MPL-2.0.txt"));
        Assert.Contains("Mozilla Public License Version 2.0", mpl);
        Assert.Contains("Larger Work", mpl);
        Assert.Contains("Apache License", File.ReadAllText(Path.Combine(dir, "Apache-2.0.txt")));
        Assert.Contains("Permission is hereby granted", File.ReadAllText(Path.Combine(dir, "MIT-dotnet.txt")));
        Assert.Contains("Permission is hereby granted", File.ReadAllText(Path.Combine(dir, "MIT-L5Sharp.txt")));

        // Every licenses/ file NOTICE links to exists.
        var notice = File.ReadAllText(NoticePath());
        foreach (Match m in Regex.Matches(notice, @"\]\((licenses/[^)#\s]+)\)"))
            Assert.True(File.Exists(Path.Combine(Root, m.Groups[1].Value)), $"NOTICE links to missing {m.Groups[1].Value}");
    }

    /// <summary>NOTICE gives the libplctag native library's source location (MPL-2.0 section 3.2).</summary>
    [Fact]
    public void NoticeLinksLibplctagSource()
    {
        var notice = File.ReadAllText(NoticePath());
        Assert.Contains("https://github.com/libplctag/libplctag", notice);
        Assert.Contains("MPL-2.0", notice);
        Assert.Contains("licenses/MPL-2.0.txt", notice);
    }

    /// <summary>NOTICE does not mention the Rockwell SDK package, its lane or its provisioning script.</summary>
    [Fact]
    public void NoticeHasNoSdkReference()
    {
        var notice = File.ReadAllText(NoticePath());
        foreach (var banned in new[] { "RockwellAutomation", "LogixDesigner", "Designer SDK", "provision-sdk", "SDK lane",
                     "README.md#requirements", "not redistributed" })
            Assert.DoesNotContain(banned, notice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>make release-nosdk</c> copies licenses/ into both release archives.</summary>
    [Fact]
    public void ReleaseTargetCopiesLicenses()
    {
        var makefile = File.ReadAllText(Path.Combine(Root, "Makefile"));
        var start = makefile.IndexOf("\nrelease-nosdk:", StringComparison.Ordinal);
        Assert.True(start >= 0, "release-nosdk target not found");
        var recipe = makefile[start..];
        var zip = recipe.IndexOf("package-nosdk.py", StringComparison.Ordinal);
        var copy = recipe.IndexOf("cp -r licenses", StringComparison.Ordinal);
        Assert.True(copy >= 0 && copy < zip, "release-nosdk must copy licenses/ before it zips the publish output");
        Assert.Contains("$(NOSDK_OUT_WIN)", recipe[copy..zip] + recipe[..copy]);
        Assert.Contains("$(NOSDK_OUT_LINUX)", recipe[..zip]);
    }

    /// <summary><c>make release-nosdk</c> copies LICENSE, NOTICE and README.md into both publish dirs.</summary>
    [Fact]
    public void ReleaseTargetCopiesLicenseNoticeReadme()
    {
        var makefile = File.ReadAllText(Path.Combine(Root, "Makefile"));
        var start = makefile.IndexOf("\nrelease-nosdk:", StringComparison.Ordinal);
        var recipe = makefile[start..];
        Assert.Contains("\"$$out/LICENSE\"", recipe);
        Assert.Contains("\"$$out/NOTICE\"", recipe);
        Assert.Contains("\"$$out/README.md\"", recipe);
        Assert.Contains("\"$$out/SECURITY.md\"", recipe);
        Assert.Contains("\"$$out/CHANGELOG.md\"", recipe);
        Assert.Contains("\"$$out/docs\"", recipe);
    }

    /// <summary>The zip guard accepts a zip with all four required root entries and rejects each omission.</summary>
    [Fact]
    public void ZipCheckRequiresLicenseNoticeReadmeAndLicensesDir()
    {
        var script = Path.Combine(Root, "scripts", "package-nosdk.py");
        var all = new[] { "LICENSE", "NOTICE", "README.md", "SECURITY.md", "CHANGELOG.md", "docs/install-vscode.md", "licenses/MPL-2.0.txt", "lgx-mcp" };
        var tmp = Path.Combine(Path.GetTempPath(), "nosdk-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Assert.Equal(0, RunCheck(script, tmp, "ok", all));
            foreach (var omit in new[] { "LICENSE", "NOTICE", "README.md", "SECURITY.md", "CHANGELOG.md", "docs/install-vscode.md", "licenses/MPL-2.0.txt" })
                Assert.NotEqual(0, RunCheck(script, tmp, "no-" + omit.Replace('/', '_'), all.Where(n => n != omit).ToArray()));
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    private static int RunCheck(string script, string dir, string name, string[] entries)
    {
        var zipPath = Path.Combine(dir, name + ".zip");
        using (var zf = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var e in entries)
                zf.CreateEntry(e);
        var psi = new System.Diagnostics.ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--check");
        psi.ArgumentList.Add(zipPath);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
