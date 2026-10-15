using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guards the scope of the public documentation: the public docs describe the no-SDK build only.
/// Every <c>logix_*</c> name in them must be a tool the no-SDK build registers, and every
/// documented error <c>kind</c> must exist in that build. The docs are read from the
/// directory <see cref="PublicGuard.DocsRoot"/> returns.
/// </summary>
public class PublicDocsScopeTests
{
    /// <summary>
    /// Error kinds whose code lives in the core assembly but which only the Logix Designer SDK or
    /// Studio automation lanes can produce. The no-SDK build never returns them, so the public docs
    /// must not list them. (Kinds that exist only in <c>LogixMcp.SdkAdapter</c> are excluded because
    /// only <c>src/LogixMcp</c> is scanned.)
    /// </summary>
    internal static readonly IReadOnlySet<string> SdkOnlyKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "DownloadRequired", "MissingSdkCommPath", "PendingEditsPresent", "ProjectNotCorrelated",
        "SdkCommLayerFaulted", "SdkServiceDegraded", "ImagesIncompatible", "ImportRejectedByImporter",
        "MemberWriteUnsupported", "MemberPathUnsupported", "StringLengthInvalid", "SdkError",
        "SavedFileNotAProject", "CreatedByThisSession", "PendingEditsBlockImport",
        // The faulted-SDK-server kind and the `session: faulted` value; both exist only on the SDK lane.
        "SdkServerFaulted", "faulted",
        // Only emitted by RunTestHandler, which is reached solely through the SDK-only logix_run_test.
        "EmulatorOnly", "TestWriteRefused",
        "StudioElevated", "StudioInOtherSession", "StudioNotRunning", "StudioOffline",
        "StudioWindowAmbiguous", "StudioWindowNotFound", "StudioModalOpen",
    };

    /// <summary>
    /// Kinds that only <c>src/LogixMcp.SdkAdapter</c> emits and the SDK-lane docs name (the publish, pending-state
    /// and Studio kinds in <c>docs/errors.md</c>). Listed here so the check still holds in the SDK-free export,
    /// which has no adapter source to scan.
    /// </summary>
    private static readonly string[] SdkAdapterDocumentedKinds =
    {
        "AlreadyTested", "AmbiguousInput", "BuildFailedEditPending", "CancelNotApplicable", "ClearIncomplete", "DeleteNeedsExplicitEmptyRungs",
        "DeleteNeedsLive", "DeleteWithTags", "EmptyRoutineEnvelope", "EnvelopeIncomplete", "ExpectedRungTextRequired",
        "ExternalAccessChangeRefused", "InsertPositionOutOfRange", "InvalidRungText", "InvalidTag", "LiveFinalizeNotConfirmed", "NotTested",
        "PendingEditsBlockImport", "PendingZoneExists", "PendingZoneStudioOnly", "PostImportCheckInconclusive", "PublishInProgress", "PublishMismatch", "PublishMismatchUnresolved", "ReplaceCountOutOfRange", "StagedRungsPresent", "StaleTarget",
        "TestStateNotConfirmed", "UntestNotApplicable", "UntestStateNotConfirmed", "ZoneTested",
    };

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
            $"Could not locate the project root walking up from '{AppContext.BaseDirectory}'.");
    }

    private static readonly string PublicRoot = PublicGuard.DocsRoot();

    /// <summary>
    /// Every tool the no-SDK build registers: the <c>[McpServerTool]</c> methods in the core assembly.
    /// The SDK adapter's tools (<see cref="SdkToolNames.All"/>) are not part of it.
    /// </summary>
    internal static HashSet<string> NoSdkToolNames() =>
        typeof(LogixTagTools).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
            .Where(a => a != null)
            .Select(a => a!.GetType().GetProperty("Name")?.GetValue(a) as string)
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every error <c>kind</c> the no-SDK build can return: the <see cref="ErrorKinds"/> constants, the
    /// <see cref="TagServiceErrorKind"/> values and every quoted PascalCase string literal in
    /// <c>src/LogixMcp</c>, less <see cref="SdkOnlyKinds"/>.
    /// </summary>
    internal static HashSet<string> NoSdkErrorKinds()
    {
        var kinds = typeof(ErrorKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        kinds.UnionWith(Enum.GetNames<TagServiceErrorKind>());

        var literal = new Regex(@"""([A-Z][A-Za-z]+)""");
        var coreRoot = Path.Combine(FindProjectRoot(), "src", "LogixMcp");
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                continue;
            foreach (Match m in literal.Matches(File.ReadAllText(file)))
                kinds.Add(m.Groups[1].Value);
        }

        kinds.ExceptWith(SdkOnlyKinds);
        return kinds;
    }

    /// <summary>
    /// The error kinds the no-SDK build emits, which <c>errors.md</c> must all document: the
    /// <see cref="ErrorKinds"/> constants, the <see cref="TagServiceErrorKind"/> values and every
    /// <c>Kind: "X"</c> / <c>...Kind = "X"</c> literal in <c>src/LogixMcp</c>, less <see cref="SdkOnlyKinds"/>.
    /// </summary>
    internal static HashSet<string> NoSdkEmittedKinds()
    {
        var kinds = typeof(ErrorKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        kinds.UnionWith(Enum.GetNames<TagServiceErrorKind>());

        var literal = new Regex(@"\b\w*[Kk]ind\w*\s*[:=]\s*""([A-Z][A-Za-z]+)""");
        var coreRoot = Path.Combine(FindProjectRoot(), "src", "LogixMcp");
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                continue;
            foreach (Match m in literal.Matches(File.ReadAllText(file)))
                kinds.Add(m.Groups[1].Value);
        }

        kinds.ExceptWith(SdkOnlyKinds);
        return kinds;
    }

    /// <summary>Every Markdown file of the public documentation set, as (relative path, text).</summary>
    private static List<(string Path, string Text)> PublicMarkdown() =>
        PublicGuard.DocFiles().Where(f => f.EndsWith(".md", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllText(System.IO.Path.Combine(PublicRoot, f))))
            .ToList();

    /// <summary>
    /// The error kinds a document names: backticked PascalCase tokens in the <c>kind</c> column of any
    /// table, and <c>kind: "X"</c> / <c>kind=X</c> mentions anywhere in the text.
    /// </summary>
    internal static HashSet<string> DocumentedKinds(string text)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var token = new Regex(@"`([A-Z][A-Za-z]+)`");

        int kindColumn = -1;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                kindColumn = -1;
                continue;
            }

            var cells = Regex.Split(trimmed.Trim('|'), @"(?<!\\)\|").Select(c => c.Trim()).ToList();
            var header = cells.FindIndex(c => c == "`kind`");
            if (header >= 0)
            {
                kindColumn = header;
                continue;
            }

            if (kindColumn >= 0 && kindColumn < cells.Count && !cells[kindColumn].StartsWith("---"))
                foreach (Match m in token.Matches(cells[kindColumn]))
                    kinds.Add(m.Groups[1].Value);
        }

        foreach (Match m in Regex.Matches(text, @"kind(?:\s*[:=]\s*|\s+)[""`]?([A-Z][A-Za-z]+)[""`]?"))
            kinds.Add(m.Groups[1].Value);

        return kinds;
    }

    /// <summary>
    /// Public docs that may describe the SDK lane: <c>docs/ONLINE-EDITS.md</c> (shipped only with it), the
    /// README, which documents all 39 tools and has a Logix Designer SDK lane section, and
    /// <c>docs/tool-reference.md</c>, which has an entry for each SDK tool. They may name the SDK tools
    /// in <see cref="SdkToolNames.All"/>; every other public doc stays no-SDK-only here. The safety model,
    /// the error reference, troubleshooting and the verified-on page also cover the SDK lane (the online edit
    /// lifecycle, its error kinds, its setup failures and what was verified).
    /// </summary>
    private static readonly IReadOnlySet<string> SdkLaneDocs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "docs/ONLINE-EDITS.md", "README.md", "docs/tool-reference.md",
            "docs/safety-model.md", "docs/errors.md", "docs/troubleshooting.md", "docs/verified-on.md",
        };

    /// <summary>
    /// Error kinds an SDK-lane doc may name: <see cref="SdkOnlyKinds"/> plus every quoted PascalCase string literal in
    /// <c>src/LogixMcp.SdkAdapter</c> (empty when that project is not in the tree).
    /// </summary>
    private static HashSet<string> SdkLaneKinds()
    {
        var kinds = new HashSet<string>(SdkOnlyKinds, StringComparer.Ordinal);
        kinds.UnionWith(SdkAdapterDocumentedKinds);
        var literal = new Regex(@"""([A-Z][A-Za-z]+)""");
        var adapterRoot = Path.Combine(FindProjectRoot(), "src", "LogixMcp.SdkAdapter");
        if (!Directory.Exists(adapterRoot))
            return kinds;
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(adapterRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                continue;
            foreach (Match m in literal.Matches(File.ReadAllText(file)))
                kinds.Add(m.Groups[1].Value);
        }

        return kinds;
    }

    [Fact]
    public void DocsListOnlyRegisteredTools()
    {
        var registered = NoSdkToolNames();
        Assert.NotEmpty(registered);
        Assert.DoesNotContain(registered, SdkToolNames.All.Contains);

        var noSdkKinds = NoSdkErrorKinds();
        Assert.NotEmpty(noSdkKinds);

        var docs = PublicMarkdown();
        Assert.NotEmpty(docs);

        var sdkLaneKinds = SdkLaneKinds();
        var unknownTools = new List<string>();
        var unknownKinds = new List<string>();
        foreach (var (path, text) in docs)
        {
            foreach (var name in Regex.Matches(text, @"\blogix_[a-z]+(?:_[a-z]+)*")
                         .Select(m => m.Value).Distinct())
            {
                var sdkLaneDoc = SdkLaneDocs.Contains(path.Replace('\\', '/'));
                if (!registered.Contains(name) && !(sdkLaneDoc && SdkToolNames.All.Contains(name)))
                    unknownTools.Add($"{path}: {name}");
            }

            foreach (var kind in DocumentedKinds(text))
            {
                if (!noSdkKinds.Contains(kind)
                    && !(SdkLaneDocs.Contains(path.Replace('\\', '/')) && sdkLaneKinds.Contains(kind)))
                    unknownKinds.Add($"{path}: {kind}");
            }
        }

        Assert.True(unknownTools.Count == 0,
            "The public docs name tools the no-SDK build does not register:\n  " +
            string.Join("\n  ", unknownTools.OrderBy(x => x, StringComparer.Ordinal)));
        Assert.True(unknownKinds.Count == 0,
            "The public docs document error kinds that are not in ErrorKinds or the no-SDK build:\n  " +
            string.Join("\n  ", unknownKinds.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The error reference covers the SDK lane: the faulted-server, build, tag-route, publish and
    /// pending-state kinds each have a backticked entry, and the troubleshooting page covers the three
    /// common SDK setup failures.
    /// </summary>
    [Fact]
    public void ErrorsDoc_DocumentsSdkLaneKinds_AndTroubleshootingCoversSdkSetup()
    {
        var errors = File.ReadAllText(Path.Combine(PublicRoot, "docs", "errors.md"));
        var required = new[]
        {
            "SdkServerFaulted", "SdkServiceDegraded", "SdkCommLayerFaulted", "BuildNotPerformed",
            "TagRouteUnavailable", "DownloadRequired", "MissingSdkCommPath",
            "PendingZoneExists", "PendingEditsPresent", "StagedRungsPresent", "StaleTarget",
            "LiveFinalizeNotConfirmed", "PublishMismatch", "BuildFailedEditPending", "PendingZoneStudioOnly",
            "ZoneTested", "NotTested", "AlreadyTested", "StudioNotRunning", "StudioOffline",
        };
        var missing = required
            .Where(k => !errors.Contains($"`{k}`", StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0,
            $"docs/errors.md does not document SDK-lane kinds: {string.Join(", ", missing)}");

        var troubleshooting = File.ReadAllText(Path.Combine(PublicRoot, "docs", "troubleshooting.md"));
        foreach (var needle in new[] { "SDK not found", "SdkServerFaulted", "Project is not open" })
            Assert.Contains(needle, troubleshooting);
    }

    /// <summary>
    /// Every environment variable <c>src/LogixMcp</c> reads through <c>Environment.GetEnvironmentVariable</c>,
    /// whether the argument is a string literal or a <c>const string</c> declared in the same file.
    /// Includes the variables only the SDK lane acts on: the core still reads them, so the table lists them.
    /// </summary>
    internal static HashSet<string> EnvVarsReadByNoSdkBuild()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var call = new Regex(@"Environment\.GetEnvironmentVariable\(\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))");
        var coreRoot = Path.Combine(FindProjectRoot(), "src", "LogixMcp");
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(coreRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text))
            {
                if (m.Groups[1].Success)
                {
                    names.Add(m.Groups[1].Value);
                    continue;
                }

                var constant = new Regex(
                    @"\bconst\s+string\s+" + Regex.Escape(m.Groups[2].Value) + @"\s*=\s*""([^""]+)""");
                foreach (Match c in constant.Matches(text))
                    names.Add(c.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>
    /// The README "Environment variables" table must list exactly the variables the no-SDK build reads.
    /// </summary>
    [Fact]
    public void EveryEnvVarDocumented()
    {
        var read = EnvVarsReadByNoSdkBuild();
        Assert.NotEmpty(read);

        var readme = File.ReadAllText(Path.Combine(PublicRoot, "README.md"));
        const string heading = "## Environment variables";
        var start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "README is missing the 'Environment variables' section.");

        var body = readme[(start + heading.Length)..];
        var next = body.IndexOf("\n## ", StringComparison.Ordinal);
        if (next >= 0)
            body = body[..next];

        var documented = Regex.Matches(body, @"^\|\s*`([A-Z][A-Z0-9_]+)`\s*\|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        var missing = read.Except(documented).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"README 'Environment variables' table has no row for: {string.Join(", ", missing)}");

        var stale = documented.Distinct().Except(read).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            $"README 'Environment variables' table documents variables the build does not read: {string.Join(", ", stale)}");

        var duplicates = documented.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0,
            $"README 'Environment variables' table has duplicate rows for: {string.Join(", ", duplicates)}");

        foreach (var required in new[]
                 {
                     "LGX_AUDIT_MAX_MB", "LGX_AUDIT_KEEP", "LGX_AUDIT_FAIL_CLOSED",
                     "LGX_ENABLE_LIVE_WRITES", "XDG_STATE_HOME",
                     "LGX_ENABLE_STUDIO_AUTOMATION", "LGX_ENABLE_LIVE_FINALIZE",
                     "LGX_ENABLE_CONTROLLER_DOWNLOAD", "LGX_PUBLISH_MODE", "LOGIX_SDK_PATH",
                 })
            Assert.Contains(required, documented);
    }

    /// <summary>
    /// <c>LGX_AUDIT_FAIL_CLOSED</c> checks before the write that the audit log can be opened for append; it does
    /// not promise the audit record can be written. Neither the public docs nor the core source may say the
    /// log "cannot be written" within a few lines of the variable name.
    /// </summary>
    [Fact]
    public void AuditFailClosed_WordingDoesNotClaimRecordCannotBeWritten()
    {
        const string token = "LGX_AUDIT_FAIL_CLOSED";
        const string phrase = "cannot be written";
        const int window = 300;

        var files = PublicGuard.DocFiles().Select(f => Path.Combine(PublicRoot, f)).ToList();
        var srcDir = Path.Combine(FindProjectRoot(), "src", "LogixMcp");
        if (Directory.Exists(srcDir))
            files.AddRange(Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));

        var hits = new List<string>();
        foreach (var file in files)
        {
            // Collapse whitespace and comment/markdown line prefixes so a phrase wrapped across lines still matches.
            var text = Regex.Replace(File.ReadAllText(file), @"\s*\n\s*(?:///|//|>)?\s*", " ");
            for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                var from = Math.Max(0, i - window);
                var to = Math.Min(text.Length, i + token.Length + window);
                if (text[from..to].Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{Path.GetRelativePath(FindProjectRoot(), file)} (offset {i})");
            }
        }

        Assert.True(hits.Count == 0,
            $"'{phrase}' appears next to {token}; say the audit log is checked for being openable before the write: "
            + string.Join("; ", hits));
    }

    [Fact]
    public void Readme_DoctorInEveryBuild_AcdUnsupported_ParametersColumn()
    {
        var readme = File.ReadAllText(Path.Combine(PublicRoot, "README.md"));
        Assert.DoesNotContain("SDK lane.** Read-only setup", readme);
        Assert.Matches(@"\| `logix_doctor` \|[^\n]*Available in every build", readme);
        Assert.Contains(".ACD` needs the", readme);
        Assert.Contains("| Tool | Parameters | Purpose |", readme);
    }

    [Fact]
    public void Readme_HasSdkLaneSection_WithRequirementsInstallAndCapabilities()
    {
        var readme = File.ReadAllText(Path.Combine(PublicRoot, "README.md"));
        const string heading = "## Logix Designer SDK lane";
        var start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "README is missing the 'Logix Designer SDK lane' section.");

        var body = readme[(start + heading.Length)..];
        var next = body.IndexOf("\n## ", StringComparison.Ordinal);
        if (next >= 0)
            body = body[..next];

        Assert.DoesNotContain("not part of this release", readme);
        Assert.DoesNotContain("no effect in this release", readme);
        foreach (var part in new[] { "### Requirements", "### Install", "### What the lane adds" })
            Assert.Contains(part, body);
        foreach (var needle in new[]
                 {
                     "Windows", "v36", "Logix Designer SDK", "FactoryTalk Logix Echo",
                     "make publish-win", "setup.ps1", ".localfeed",
                     "Online rung edits", "Pending-edit lifecycle", "Structural build check", "`.ACD` projects",
                     "SDK tag route with a CIP fast path",
                 })
            Assert.Contains(needle, body);
    }

    [Fact]
    public void Readme_ControllerDownloadFlag_IsOffByDefaultAndNeedsConfirmation()
    {
        var readme = File.ReadAllText(Path.Combine(PublicRoot, "README.md"));
        var row = Regex.Match(readme, @"^\| `LGX_ENABLE_CONTROLLER_DOWNLOAD` \|.*$", RegexOptions.Multiline);
        Assert.True(row.Success, "README has no LGX_ENABLE_CONTROLLER_DOWNLOAD row.");
        Assert.Contains("| off |", row.Value);
        Assert.Contains("Off by default", row.Value);
        Assert.Contains("confirmedByUser=true", row.Value);
        Assert.Contains("overwrites the program", row.Value);
    }
}
