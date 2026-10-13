using System.Text;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Checks that every relative link, and every <c>#anchor</c> inside or after a relative link, in the
/// public documentation resolves. Heading anchors use GitHub's slug rules.
///
/// <para>
/// This test has <b>no</b> <c>Scope=Internal</c> trait: it must run in the exported public tree, where
/// <c>make test</c> is the only gate. The documentation sits at the repo root (<c>README.md</c>,
/// <c>CONTRIBUTING.md</c>, <c>docs/*.md</c>); <see cref="ExportedLayout"/> answers where each document
/// comes from and which paths are absent from the export.
/// </para>
/// </summary>
public class PublicDocsLinkTests
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
            $"Could not locate the project root walking up from '{AppContext.BaseDirectory}'.");
    }

    private static readonly string Root = FindProjectRoot();

    // ── Virtual (exported) layout ────────────────────────────────────────────

    /// <summary>Maps an exported-tree relative path to the file that supplies it, or null if it is not exported.</summary>
    private static string? Resolve(string rel)
    {
        rel = rel.Replace('\\', '/').Trim('/');
        if (rel.Length == 0)
            return null;

        return ExportedLayout.Resolve(Root, rel);
    }

    /// <summary>Exported-tree relative paths of the documents whose links are checked.</summary>
    private static List<string> PublicDocuments()
    {
        var docs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in new[] { "README.md", "SECURITY.md", "CHANGELOG.md", "CONTRIBUTING.md" })
            if (Resolve(f) != null)
                docs.Add(f);

        var docsDir = ExportedLayout.DocsDirectory(Root);
        if (Directory.Exists(docsDir))
            foreach (var f in Directory.GetFiles(docsDir, "*.md"))
            {
                var rel = "docs/" + Path.GetFileName(f);
                if (Resolve(rel) != null)
                    docs.Add(rel);
            }
        return docs.ToList();
    }

    // ── Markdown scanning ────────────────────────────────────────────────────

    private static readonly Regex Fence = new(@"^\s{0,3}(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex AtxHeading = new(@"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex AtxEmpty = new(@"^ {0,3}#{1,6}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex SetextUnderline = new(@"^ {0,3}(=+|-+)[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex HtmlAnchor = new(@"<a\s[^>]*?\b(?:name|id)\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InlineCode = new(@"(`+)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex InlineLink = new(@"!?\[(?:[^\[\]]|\[[^\]]*\])*\]\(\s*(<[^>]*>|[^)\s]*)(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)", RegexOptions.Compiled);
    private static readonly Regex RefDefinition = new(@"^ {0,3}\[[^\]]+\]:\s*(<[^>]*>|\S+)", RegexOptions.Compiled);

    /// <summary>
    /// GitHub's heading slug: render the inline markup to text, lowercase, drop everything except letters,
    /// digits, spaces, hyphens and underscores, then turn each space into a hyphen.
    /// </summary>
    internal static string Slug(string headingMarkdown)
    {
        var t = headingMarkdown.Trim();
        t = Regex.Replace(t, @"!?\[([^\]]*)\]\([^)]*\)", "$1");      // links and images -> their text
        t = Regex.Replace(t, @"\[([^\]]*)\]\[[^\]]*\]", "$1");        // reference links
        t = Regex.Replace(t, @"<[^>]+>", "");                         // inline HTML tags
        t = Regex.Replace(t, @"`+", "");                              // code spans keep their text
        t = Regex.Replace(t, @"(?<![\w])_+([^_]+?)_+(?![\w])", "$1"); // _emphasis_ (not snake_case)
        t = t.Replace("*", "").Replace("~~", "");
        var sb = new StringBuilder();
        foreach (var ch in t.ToLowerInvariant())
        {
            if (ch == ' ') sb.Append('-');
            else if (ch == '-' || ch == '_' || char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>All anchors a Markdown file offers: heading slugs (with GitHub's -1, -2 suffixes) and HTML anchors.</summary>
    internal static HashSet<string> Anchors(string markdown)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        void AddHeading(string text)
        {
            var slug = Slug(text);
            if (seen.TryGetValue(slug, out var n))
            {
                seen[slug] = n + 1;
                slug = $"{slug}-{n + 1}";
            }
            else
            {
                seen[slug] = 0;
            }
            result.Add(slug);
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        string? previous = null;
        foreach (var line in lines)
        {
            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                previous = null;
                continue;
            }
            if (inFence)
                continue;

            foreach (Match a in HtmlAnchor.Matches(line))
                result.Add(a.Groups[1].Value);

            var atx = AtxHeading.Match(line);
            if (atx.Success && !AtxEmpty.IsMatch(line))
            {
                AddHeading(atx.Groups[2].Value);
                previous = null;
                continue;
            }

            if (previous != null && SetextUnderline.IsMatch(line))
            {
                AddHeading(previous);
                previous = null;
                continue;
            }

            previous = string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('|')
                || line.TrimStart().StartsWith('>') || Regex.IsMatch(line, @"^\s*([-*+]|\d+\.)\s")
                ? null
                : line.Trim();
        }
        return result;
    }

    /// <summary>Link destinations in a Markdown file, outside code fences and code spans, with line numbers.</summary>
    internal static List<(int Line, string Target)> Links(string markdown)
    {
        var found = new List<(int, string)>();
        var inFence = false;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Fence.IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;

            var text = InlineCode.Replace(line, m => new string(' ', m.Length));
            foreach (Match m in InlineLink.Matches(text))
                found.Add((i + 1, m.Groups[1].Value.Trim('<', '>')));
            var def = RefDefinition.Match(text);
            if (def.Success)
                found.Add((i + 1, def.Groups[1].Value.Trim('<', '>')));
        }
        return found;
    }

    private static bool IsExternal(string target) =>
        Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:") || target.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Normalises <c>dir/../x</c> against an exported-tree directory; null when it escapes the root.</summary>
    private static string? Combine(string fromDir, string path)
    {
        var parts = new List<string>();
        var start = path.StartsWith('/') ? "" : fromDir;
        foreach (var seg in (start + "/" + path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void AllRelativeLinksAndAnchorsResolve()
    {
        var documents = PublicDocuments();
        Assert.Contains("README.md", documents);
        Assert.Contains("docs/verified-on.md", documents);

        var anchorCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> AnchorsOf(string rel) =>
            anchorCache.TryGetValue(rel, out var set)
                ? set
                : anchorCache[rel] = Anchors(File.ReadAllText(Resolve(rel)!));

        var broken = new List<string>();
        foreach (var doc in documents)
        {
            var docDir = doc.Contains('/') ? doc[..doc.LastIndexOf('/')] : "";
            foreach (var (line, rawTarget) in Links(File.ReadAllText(Resolve(doc)!)))
            {
                if (rawTarget.Length == 0 || IsExternal(rawTarget))
                    continue;

                var hash = rawTarget.IndexOf('#');
                var pathPart = Uri.UnescapeDataString(hash < 0 ? rawTarget : rawTarget[..hash]);
                var fragment = hash < 0 ? null : Uri.UnescapeDataString(rawTarget[(hash + 1)..]);
                var q = pathPart.IndexOf('?');
                if (q >= 0) pathPart = pathPart[..q];

                var targetRel = pathPart.Length == 0 ? doc : Combine(docDir, pathPart);
                if (targetRel == null || Resolve(targetRel) == null)
                {
                    broken.Add($"{doc}:{line}: '{rawTarget}' - target file does not exist");
                    continue;
                }

                if (string.IsNullOrEmpty(fragment) || !targetRel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Resolve(targetRel)!))
                    continue;

                if (!AnchorsOf(targetRel).Contains(fragment))
                    broken.Add($"{doc}:{line}: '{rawTarget}' - no heading in {targetRel} has the anchor '#{fragment}'");
            }
        }

        Assert.True(broken.Count == 0,
            "Broken relative links or anchors in the public docs:\n  " + string.Join("\n  ", broken));
    }

    [Fact]
    public void PublicDocs_DoNotReferenceInternalVerificationRecord()
    {
        var hits = PublicDocuments()
            .Where(d => Regex.IsMatch(File.ReadAllText(Resolve(d)!), "HARDWARE-VERIFIED", RegexOptions.IgnoreCase))
            .ToList();
        Assert.True(hits.Count == 0, "Public docs reference the internal verification record: " + string.Join(", ", hits));
    }

    [Theory]
    [InlineData("Controller Has Pending Edits", "controller-has-pending-edits")]
    [InlineData("`logix_read` errors", "logix_read-errors")]
    [InlineData("A -- B", "a----b")]
    [InlineData("What's verified?", "whats-verified")]
    [InlineData("Install (Claude **Code**)", "install-claude-code")]
    [InlineData("See [the model](x.md) now", "see-the-model-now")]
    public void Slug_FollowsGitHubRules(string heading, string expected)
    {
        Assert.Equal(expected, Slug(heading));
    }

    [Fact]
    public void Anchors_NumberDuplicateHeadingsAndIgnoreCodeFences()
    {
        var md = "# Title\n\n## Same\n\ntext\n\n## Same\n\n```\n## Not a heading\n```\n\nSetext\n------\n";
        var anchors = Anchors(md);
        Assert.Contains("title", anchors);
        Assert.Contains("same", anchors);
        Assert.Contains("same-1", anchors);
        Assert.Contains("setext", anchors);
        Assert.DoesNotContain("not-a-heading", anchors);
    }

    [Fact]
    public void Links_SkipCodeAndFindInlineAndReferenceTargets()
    {
        var md = "See [a](one.md#x) and `[b](two.md)`.\n\n```\n[c](three.md)\n```\n\n[ref]: four.md\n";
        var targets = Links(md).Select(l => l.Target).ToList();
        Assert.Equal(new[] { "one.md#x", "four.md" }, targets);
    }
}
