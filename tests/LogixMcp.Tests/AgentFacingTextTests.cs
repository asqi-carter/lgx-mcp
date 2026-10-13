#if !LOGIX_SDK
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// In the no-SDK build every piece of agent-facing text (the server
/// instructions, every tool description, every parameter description and the
/// <c>logix_ping</c> payload) names only tools this build registers and never tells the
/// agent to use <c>backend="sdk"</c> or the SDK-only connect parameters.
/// </summary>
[Collection("StaticHandlerHooks")]
public class AgentFacingTextTests
{
    private static readonly Assembly ToolAssembly = typeof(LogixTagTools).Assembly;

    private static readonly string[] SdkOnlyConnectParams =
        ["sdkMode", "allowControllerDownload", "sdkCommPath"];

    private static IEnumerable<MethodInfo> ToolMethods() =>
        ToolAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"));

    private static string ToolName(MethodInfo m)
    {
        var attr = m.GetCustomAttributes().First(a => a.GetType().Name == "McpServerToolAttribute");
        return (attr.GetType().GetProperty("Name")?.GetValue(attr) as string) ?? m.Name;
    }

    /// <summary>Every (label, text) pair an agent can read in this build.</summary>
    private static List<(string Label, string Text)> AgentFacingTexts()
    {
        var texts = new List<(string, string)> { ("instructions", LogixTools.CanonicalInstructions) };

        foreach (var m in ToolMethods())
        {
            var name = ToolName(m);
            var d = m.GetCustomAttribute<DescriptionAttribute>();
            if (d is not null) texts.Add(($"tool {name}", d.Description));
            foreach (var p in m.GetParameters())
            {
                if (SdkOnlyConnectParams.Contains(p.Name!)) continue; // checked separately
                var pd = p.GetCustomAttribute<DescriptionAttribute>();
                if (pd is not null) texts.Add(($"param {name}.{p.Name}", pd.Description));
            }
        }

        foreach (var caps in new[]
                 {
                     (Capabilities?)null,
                     new Capabilities(LiveWritesEnabled: false),
                     new Capabilities(LiveWritesEnabled: true, StudioAutomationEnabled: true,
                                      LiveFinalizeEnabled: true),
                 })
            texts.Add(("ping payload", JsonSerializer.Serialize(PingHandler.Execute(caps))));

        return texts;
    }

    /// <summary>
    /// Instructions, tool and parameter descriptions and the ping payload mention no
    /// unregistered tool, no <c>backend="sdk"</c>, and none of the SDK-only connect parameters.
    /// </summary>
    [Fact]
    public void NoSdkBuild_InstructionsAndDescriptionsNameOnlyRegisteredTools()
    {
        var registered = ToolMethods().Select(ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var (label, text) in AgentFacingTexts())
        {
            foreach (Match m in Regex.Matches(text, @"\blogix_[a-z_]+"))
                if (!registered.Contains(m.Value))
                    problems.Add($"{label}: unregistered tool '{m.Value}'");

            foreach (var pattern in new[]
                     {
                         @"backend\s*=\s*[""'\\]*sdk\b",
                         @"[""']sdk[""']",
                         @"\bsdk\s*\|",
                         @"\bsdkMode\b", @"\bsdkCommPath\b", @"\ballowControllerDownload\b",
                         @"Logix Designer SDK", @"\bgRPC\b", @"\bthe SDK\b", @"\bSDK['’]s\b",
                     })
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                    problems.Add($"{label}: matches /{pattern}/");
        }

        Assert.True(problems.Count == 0,
            "No-SDK agent-facing text names things this build does not have:\n  " +
            string.Join("\n  ", problems.Distinct()));
    }

    /// <summary>
    /// The three SDK-only <c>logix_connect</c> parameters are not in this build's tool schema
    /// at all, and the argument-key check names them as unknown parameters.
    /// </summary>
    [Fact]
    public void NoSdkBuild_SdkOnlyConnectParameters_AreAbsentFromSchema_AndUnknown()
    {
        var connect = ToolMethods().Single(m => ToolName(m) == "logix_connect");
        foreach (var name in SdkOnlyConnectParams)
        {
            Assert.DoesNotContain(connect.GetParameters(), p => p.Name == name);
            Assert.DoesNotContain(name, ConnectHandler.ValidParameters);

            var unknown = ConnectHandler.CheckForUnknownParameters(new[] { "gateway", name });
            Assert.NotNull(unknown);
            Assert.Equal(new[] { name }, unknown!.UnknownParameters);
        }
    }

    /// <summary>
    /// The logix_diff_against_controller description and the logix_import_datatype
    /// tool and acdPath parameter descriptions carry no SDK or ACD wording in this build.
    /// </summary>
    [Fact]
    public void NoSdkBuild_DiffAgainstControllerAndImportDatatypeAcdPath_HaveNoSdkOrAcdWording()
    {
        var diff   = ToolMethods().Single(m => ToolName(m) == "logix_diff_against_controller");
        var import = ToolMethods().Single(m => ToolName(m) == "logix_import_datatype");
        var acdParam = import.GetParameters().Single(p => p.Name == "acdPath");

        var texts = new (string Label, string? Text)[]
        {
            ("tool logix_diff_against_controller", diff.GetCustomAttribute<DescriptionAttribute>()?.Description),
            ("tool logix_import_datatype", import.GetCustomAttribute<DescriptionAttribute>()?.Description),
            ("param logix_import_datatype.acdPath", acdParam.GetCustomAttribute<DescriptionAttribute>()?.Description),
        };

        var problems = new List<string>();
        foreach (var (label, text) in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) { problems.Add($"{label}: no description"); continue; }
            // The parameter name acdPath is the schema key and cannot be removed; strip it so
            // only prose is checked.
            var prose = text.Replace("acdPath", "");
            foreach (var pattern in new[] { @"\bsdk", @"sdk[A-Z_]", @"\bacd\b", @"\.acd\b" })
                if (Regex.IsMatch(prose, pattern, RegexOptions.IgnoreCase))
                    problems.Add($"{label}: matches /{pattern}/");
        }

        Assert.True(problems.Count == 0,
            "No-SDK descriptions contain SDK or ACD wording:\n  " + string.Join("\n  ", problems));
    }

    // ── Source scan: nothing SDK-flavoured is compiled into the no-SDK hint text ──────────

    /// <summary>The one literal that is allowed to say "SDK": the dispatch refusal itself.</summary>
    private const string RefusalLiteral =
        "The SDK backend is not part of this build. Use cip, l5x or virtual.";

    /// <summary>
    /// Error-kind identifiers that legitimately contain "Sdk". They are machine-readable
    /// names, not hint text, and are returned by handlers outside the connect path.
    /// </summary>
    private static readonly HashSet<string> KindIdentifierLiterals = new(StringComparer.Ordinal) { "SdkRequired" };

    private static readonly Regex[] ForbiddenInNoSdkText =
    {
        new(@"\bsdk\b", RegexOptions.IgnoreCase),
        new(@"sdk[A-Z_]", RegexOptions.None),
        new(@"\.localfeed", RegexOptions.IgnoreCase),
        new(@"EnableLogixSdk", RegexOptions.IgnoreCase),
        new(@"provision-sdk", RegexOptions.IgnoreCase),
        new(@"LOGIX_SDK", RegexOptions.None),
        new(@"Logix Designer", RegexOptions.IgnoreCase),
        new(@"allowControllerDownload", RegexOptions.IgnoreCase),
        new(@"\bgRPC\b", RegexOptions.IgnoreCase),
        new(@"RockwellAutomation", RegexOptions.IgnoreCase),
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LogixMcp.sln not found above test base directory.");
    }

    /// <summary>
    /// String literals on code lines (comments excluded) that are compiled into the no-SDK
    /// build: everything outside <c>#if LOGIX_SDK</c> regions.
    /// </summary>
    private static List<(int Line, string Literal)> NoSdkLiterals(string relativePath)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        var lines = source.Split('\n');
        var sdkLines = NoSdkBuildTests.LinesInsideSdkGuard(source, new Regex(".*")).ToHashSet();
        var literal = new Regex("\"(?:[^\"\\\\]|\\\\.)*\"");
        var result = new List<(int, string)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (sdkLines.Contains(i + 1)) continue;
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("#")) continue;
            foreach (Match m in literal.Matches(line))
                result.Add((i + 1, m.Value.Substring(1, m.Value.Length - 2)));
        }
        return result;
    }

    private static List<string> SdkHintProblems(IEnumerable<(int Line, string Literal)> literals, string file)
    {
        var problems = new List<string>();
        foreach (var (line, text) in literals)
        {
            if (text == "sdk" || text == RefusalLiteral || KindIdentifierLiterals.Contains(text))
                continue;
            foreach (var pattern in ForbiddenInNoSdkText)
                if (pattern.IsMatch(text))
                    problems.Add($"{file}:{line}: /{pattern}/ in \"{text}\"");
        }
        return problems;
    }

    /// <summary>
    /// The ErrorKinds table and every ConnectHandler string literal compiled into the no-SDK
    /// build, plus the error and hint strings the handler returns, name only cip, l5x and
    /// virtual: no .localfeed / EnableLogixSdk / provision-sdk text and no SDK-only parameters.
    /// </summary>
    [Fact]
    public async Task NoSdkBuild_NoSdkHintsInErrors()
    {
        var problems = new List<string>();
        problems.AddRange(SdkHintProblems(NoSdkLiterals("src/LogixMcp/ErrorKinds.cs"), "ErrorKinds.cs"));
        problems.AddRange(SdkHintProblems(NoSdkLiterals("src/LogixMcp/ConnectHandler.cs"), "ConnectHandler.cs"));

        // The hint strings the handler actually returns on the paths a caller can hit.
        var errors = new List<object>
        {
            await ConnectHandler.ExecuteAsync("", "", false, new ProjectService(), new TagSessionService(), backend: "cip"),
            await ConnectHandler.ExecuteAsync("", "", false, new ProjectService(), new TagSessionService(), backend: "bogus"),
            await ConnectHandler.ExecuteAsync("", "", false, new ProjectService(), new TagSessionService(), backend: "sdk"),
            await ConnectHandler.ExecuteAsync("", "", false, new ProjectService(), new TagSessionService(), backend: "l5x"),
        };
        foreach (var e in errors)
        {
            var json = JsonSerializer.Serialize(e, e.GetType());
            foreach (var pattern in ForbiddenInNoSdkText.Skip(1)) // "sdk" alone is allowed in the refusal
                if (pattern.IsMatch(json))
                    problems.Add($"returned {e.GetType().Name}: /{pattern}/ in {json}");
            if (e is not CapabilityDisabledError &&
                Regex.IsMatch(json, @"\bsdk\b", RegexOptions.IgnoreCase))
                problems.Add($"returned {e.GetType().Name}: names sdk in {json}");
        }

        Assert.True(problems.Count == 0,
            "SDK text is compiled into the no-SDK connect path:\n  " +
            string.Join("\n  ", problems.Distinct()));
    }
    // ── Hint and workaround strings ───────────────────────────────────────────────────────

    private static readonly Regex[] ForbiddenInHints =
    {
        new(@"EnableLogixSdk", RegexOptions.IgnoreCase),
        new(@"backend\s*=\s*[""'\\]*sdk\b", RegexOptions.IgnoreCase),
        new(@"\bsdkMode\b", RegexOptions.IgnoreCase),
        new(@"\.localfeed", RegexOptions.IgnoreCase),
        new(@"provision-sdk", RegexOptions.IgnoreCase),
        new(@"Studio", RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// Matches the start of a statement that assigns a hint or workaround string: a named
    /// argument or property (<c>Hint:</c>, <c>Workaround =</c>, <c>fix:</c>) or a constant
    /// whose name carries Hint, Workaround or Fix.
    /// </summary>
    private static readonly Regex HintContext = new(
        @"(\b\w*(Hint|Workaround|Fix)\w*\s*[:=]|\bconst\s+string\s+\w*(Hint|Workaround|Fix)\w*)",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Literals compiled into the no-SDK build that sit in a hint or workaround string: the
    /// literal's concatenation chain starts on a line that names a hint, workaround or fix.
    /// </summary>
    private static List<(int Line, string Literal)> NoSdkHintLiterals(string relativePath)
    {
        var lines = File.ReadAllText(Path.Combine(RepoRoot(), relativePath)).Split('\n');
        var result = new List<(int, string)>();
        foreach (var (line, text) in NoSdkLiterals(relativePath))
        {
            int start = line - 1;
            // Walk to the first line of the concatenation chain; a lone "Hint:" / "Error:" label
            // on the line above belongs to the chain too.
            while (start > 0 && lines[start - 1].TrimEnd().EndsWith("+")) start--;
            var head = lines[start];
            if (start > 0 && Regex.IsMatch(lines[start - 1].TrimEnd(), @"[:=]$"))
                head = lines[start - 1];
            var firstQuote = head.IndexOf('"');
            if (firstQuote < 0) firstQuote = head.Length;
            if (HintContext.IsMatch(head.Substring(0, firstQuote)))
                result.Add((line, text));
        }
        return result;
    }

    /// <summary>
    /// Every hint and workaround string compiled into the no-SDK build, plus the error-kind
    /// hint table, names none of EnableLogixSdk, backend="sdk", sdkMode, .localfeed,
    /// provision-sdk or Studio.
    /// </summary>
    [Fact]
    public void NoSdkBuild_HintAndWorkaroundStrings_HaveNoSdkOrStudioText()
    {
        var sourceDir = Path.Combine(RepoRoot(), "src", "LogixMcp");
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetRelativePath(RepoRoot(), file);
            var literals = Path.GetFileName(file) == "ErrorKinds.cs"
                ? NoSdkLiterals(rel)
                : NoSdkHintLiterals(rel);
            foreach (var (line, text) in literals)
                foreach (var pattern in ForbiddenInHints)
                    if (pattern.IsMatch(text))
                        problems.Add($"{Path.GetFileName(file)}:{line}: /{pattern}/ in \"{text}\"");
        }

        Assert.True(problems.Count == 0,
            "No-SDK hint or workaround strings carry SDK or Studio text:\n  " +
            string.Join("\n  ", problems.Distinct()));
    }

    /// <summary>
    /// The hints returned at run time on the paths this task changed: an ACD open, the
    /// controller diff without a session, and the doctor's session and cipRuntime advice.
    /// </summary>
    [Fact]
    public async Task NoSdkBuild_AcdOpenAndDiffAndCipRuntimeHints_AreClean()
    {
        var savedConv = OpenProjectHandler.AcdConverter;
        try
        {
            OpenProjectHandler.AcdConverter = null;
            var acd = await OpenProjectHandler.ExecuteAsync("/some/project.ACD", new ProjectService());
            var acdJson = JsonSerializer.Serialize(acd, acd.GetType());
            Assert.Contains("not supported in this build", acdJson);
            Assert.Contains("L5X", acdJson);
            foreach (var pattern in ForbiddenInHints)
                Assert.False(pattern.IsMatch(acdJson), $"ACD open: /{pattern}/ in {acdJson}");
        }
        finally { OpenProjectHandler.AcdConverter = savedConv; }

        var diff = DiffAgainstControllerHandler.Execute("x.L5X", new TagSessionService());
        var diffJson = JsonSerializer.Serialize(diff, diff.GetType());
        foreach (var pattern in ForbiddenInHints.Append(new Regex(@"\bsdk\b", RegexOptions.IgnoreCase)))
            Assert.False(pattern.IsMatch(diffJson), $"diff: /{pattern}/ in {diffJson}");

        Assert.Contains("redistributable", LibPlcTagService.CipRuntimeMissingHint, StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in ForbiddenInHints)
            Assert.False(pattern.IsMatch(LibPlcTagService.CipRuntimeMissingHint),
                $"cipRuntime hint: /{pattern}/");
    }

    /// <summary>
    /// Every rule name in the gate-rules table of <c>docs/safety-model.md</c> appears in the
    /// <c>logix_gate_report</c> description, in the same order, and the description makes none of the
    /// retired claims about the rules.
    /// </summary>
    [Fact]
    public void GateReportDescription_ListsEveryRuleInSafetyModelOrder()
    {
        var doc = File.ReadAllLines(Path.Combine(ExportedLayout.DocsDirectory(RepoRoot()), "safety-model.md"));
        var start = Array.FindIndex(doc, l => l.StartsWith("| Classification | Rule | Outcome |"));
        Assert.True(start >= 0, "gate-rules table not found in safety-model.md");

        var ruleNames = doc.Skip(start + 2)
            .TakeWhile(l => l.StartsWith("|"))
            .Select(l => l.Split('|')[1].Trim())
            .ToList();
        Assert.True(ruleNames.Count >= 7, $"expected the gate-rules table to list rules, got {ruleNames.Count}");

        var method = ToolMethods().Single(m => ToolName(m) == "logix_gate_report");
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;

        var last = -1;
        foreach (var name in ruleNames)
        {
            var at = description.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            Assert.True(at >= 0, $"logix_gate_report description is missing the rule '{name}'");
            Assert.True(at > last, $"logix_gate_report description lists '{name}' out of safety-model.md order");
            last = at;
        }

        // The partial-export rule is first in the table, so it is first in the description, and the
        // refusal and the fields that identify it are named alongside it.
        Assert.Equal("Partial export", ruleNames[0]);
        Assert.Contains("partial export", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WriteGateRequired", description);

        foreach (var reason in new[]
                 {
                     "ProgramWritten", "SafetyTag", "UnknownWriteContext",
                     "ExternalAccessNotReadWrite", "NotDesignated",
                 })
            Assert.Contains(reason, description);

        Assert.DoesNotContain("tags created through lgx-mcp", description, StringComparison.OrdinalIgnoreCase);

        // The same rule names, in the same order, in the write-gate skill and the logix_write description.
        // The order is the order of first appearance in the write-gate section of safety-model.md.
        string[] reasons =
        [
            "WriteGateRequired", "ProgramWritten", "SafetyTag", "UnknownWriteContext",
            "ExternalAccessNotReadWrite", "NotDesignated", "InvalidValue", "InvalidTagName", "NotOnAllowlist",
        ];
        var section = string.Join("\n", doc.SkipWhile(l => !l.StartsWith("## Write-gate derivation")));
        Assert.True(section.Length > 0, "write-gate section not found in safety-model.md");
        Assert.All(reasons, r => Assert.True(section.Contains(r, StringComparison.Ordinal),
            $"safety-model.md does not mention '{r}'"));
        Assert.Equal(reasons, reasons.OrderBy(r => section.IndexOf(r, StringComparison.Ordinal)).ToArray());

        var skill = File.ReadAllText(Path.Combine(RepoRoot(), "skills", "lgx-write-gate", "SKILL.md"));
        var writeDescription = ToolMethods().Single(m => ToolName(m) == "logix_write")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        foreach (var (label, text) in new[] { ("SKILL.md", skill), ("logix_write description", writeDescription) })
        {
            var previous = -1;
            foreach (var reason in reasons)
            {
                var at = text.IndexOf(reason, StringComparison.Ordinal);
                Assert.True(at >= 0, $"{label} is missing the rule '{reason}'");
                Assert.True(at > previous, $"{label} lists '{reason}' out of safety-model.md order");
                previous = at;
            }

            // The teaching text lists only reasons the build returns, in table order.
            Assert.DoesNotContain("CreatedByThisSession", text, StringComparison.Ordinal);
            Assert.DoesNotContain("granted first", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The partial-export rule is described the same way everywhere an agent or user reads about the gate:
    /// the rule table row, <c>partialExport</c>, <c>exportTarget</c>, and the allowlist interplay
    /// (<c>allowlist+partial-export-denies</c>) appear in the docs, the write-gate skill, the connect and
    /// gate_report descriptions and the 1.0.0 changelog entry, and none still says a partial export seeds
    /// the write-gate policy or that CIP refuses to connect.
    /// </summary>
    [Fact]
    public void PartialExportRule_IsDescribedConsistentlyEverywhere()
    {
        var docsRoot = ExportedLayout.DocsRoot(RepoRoot());
        var docs = ExportedLayout.DocsDirectory(RepoRoot());

        // The rule's name in the safety-model.md table is the name the gate_report description uses.
        var safety = File.ReadAllLines(Path.Combine(docs, "safety-model.md"));
        var start = Array.FindIndex(safety, l => l.StartsWith("| Classification | Rule | Outcome |"));
        Assert.True(start >= 0, "gate-rules table not found in safety-model.md");
        var row = safety.Skip(start + 2).TakeWhile(l => l.StartsWith("|"))
            .Single(l => l.StartsWith("| Partial export |"));
        Assert.Contains("partialExport", row, StringComparison.Ordinal);
        Assert.Contains("exportTarget", row, StringComparison.Ordinal);
        Assert.Contains("WriteGateRequired", row, StringComparison.Ordinal);

        string Desc(string tool) => ToolMethods().Single(m => ToolName(m) == tool)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        var texts = new List<(string Label, string Text, string[] Required)>
        {
            ("safety-model.md", File.ReadAllText(Path.Combine(docs, "safety-model.md")),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("tool-reference.md", File.ReadAllText(Path.Combine(docs, "tool-reference.md")),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("errors.md", File.ReadAllText(Path.Combine(docs, "errors.md")),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("SECURITY.md", File.ReadAllText(Path.Combine(docsRoot, "SECURITY.md")),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("README.md", File.ReadAllText(Path.Combine(docsRoot, "README.md")),
                ["partialExport", "WriteGateRequired", "LGX_WRITE_ALLOWLIST"]),
            ("write-gate SKILL.md", File.ReadAllText(Path.Combine(RepoRoot(), "skills", "lgx-write-gate", "SKILL.md")),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("logix_connect description", Desc("logix_connect"),
                ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]),
            ("logix_gate_report description", Desc("logix_gate_report"),
                ["Partial export", "WriteGateRequired", "LGX_WRITE_ALLOWLIST"]),
        };

        var changelog = File.ReadAllText(Path.Combine(docsRoot, "CHANGELOG.md"));
        var v1 = changelog.IndexOf("## [1.0.0]", StringComparison.Ordinal);
        Assert.True(v1 >= 0, "CHANGELOG.md has no 1.0.0 section");
        var next = changelog.IndexOf("\n## [", v1 + 1, StringComparison.Ordinal);
        texts.Add(("CHANGELOG.md 1.0.0", next < 0 ? changelog[v1..] : changelog[v1..next],
            ["partialExport", "exportTarget", "WriteGateRequired", "LGX_WRITE_ALLOWLIST", "allowlist+partial-export-denies"]));

        foreach (var (label, text, required) in texts)
        {
            foreach (var needle in required)
                Assert.True(text.Contains(needle, StringComparison.Ordinal), $"{label} does not mention '{needle}'");

            Assert.DoesNotMatch(new Regex(@"seeds?\s+the\s+(write-)?gate\s+polic", RegexOptions.IgnoreCase), text);
            Assert.DoesNotMatch(new Regex(@"CIP\s+(session\s+)?(is\s+)?refus\w*\s+(to\s+)?connect", RegexOptions.IgnoreCase), text);
            Assert.DoesNotMatch(new Regex(@"refused\s+with\s+`?WriteGateRequired`?\s+\(?\s*at\s+connect", RegexOptions.IgnoreCase), text);
        }
    }

    /// <summary>The safety-model.md gate table has the MSG destination, firmware-written type and partial-export rows.</summary>
    [Fact]
    public void SafetyModelTable_HasMsgFirmwareAndPartialExportRows()
    {
        var doc = File.ReadAllLines(Path.Combine(ExportedLayout.DocsDirectory(RepoRoot()), "safety-model.md"));
        var start = Array.FindIndex(doc, l => l.StartsWith("| Classification | Rule | Outcome |"));
        Assert.True(start >= 0, "gate-rules table not found in safety-model.md");
        var rows = doc.Skip(start + 2).TakeWhile(l => l.StartsWith("|")).ToList();

        Assert.Contains(rows, r => r.StartsWith("| MSG destination |") && r.Contains("DestinationTag"));
        Assert.Contains(rows, r => r.StartsWith("| Firmware-written type |") && r.Contains("AXIS_"));
        Assert.Contains(rows, r => r.StartsWith("| Partial export |") && r.Contains("WriteGateRequired"));
    }

    /// <summary>The retired phrases are gone from the diff and open-project descriptions.</summary>
    [Fact]
    public void DiffAndOpenProjectDescriptions_DropRetiredPhrases()
    {
        string Desc(string tool) => ToolMethods().Single(m => ToolName(m) == tool)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        var diff = Desc("logix_diff_against_controller");
        foreach (var phrase in new[] { "uploaded by logix_connect", "live edit", "correlated session" })
            Assert.DoesNotContain(phrase, diff, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(".ACD", Desc("logix_open_project"), StringComparison.OrdinalIgnoreCase);
    }
}
#endif
