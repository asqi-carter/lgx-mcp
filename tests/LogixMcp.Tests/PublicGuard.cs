using System.Text;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// The guard that ships with the public tree. It carries only a small set of generic public patterns
/// (citation numbers, tracker ids, personal paths, private addresses) and no list of sensitive words,
/// plain or hashed.
/// It is part of the public tree.
///
/// <para>
/// This file is itself scanned, so the generic patterns are written so that their own source text does
/// not match them (a one-character class stands in for one letter of each citation prefix).
/// </para>
/// </summary>
public static class PublicGuard
{
    /// <summary>Walks up from the test binary to the directory holding the <c>*.sln</c>.</summary>
    public static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("No *.sln found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Directory the public documentation set is read from (see <see cref="ExportedLayout"/>).
    /// </summary>
    public static string DocsRoot() => ExportedLayout.DocsRoot(FindProjectRoot());

    private static readonly string[] DocRootFiles =
        { "README.md", "SECURITY.md", "CHANGELOG.md", "NOTICE", "LICENSE", "Directory.Build.props", "NuGet.config" };

    /// <summary>
    /// The public documentation set, relative to <see cref="DocsRoot"/> (forward slashes, sorted): the
    /// top-level files the export overlays plus <c>docs/*.md</c>. Same set in both layouts; in the export
    /// every other file under <c>docs/</c> is stripped, so <c>docs/*.md</c> is exactly the public docs.
    /// </summary>
    public static List<string> DocFiles()
    {
        var root = DocsRoot();
        var files = DocRootFiles.Where(f => File.Exists(Path.Combine(root, f))).ToList();
        var docs = Path.Combine(root, "docs");
        if (Directory.Exists(docs))
            files.AddRange(Directory.GetFiles(docs, "*.md").Select(f => "docs/" + Path.GetFileName(f)));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";

    /// <summary>
    /// Generic patterns: personal paths, private network addresses and comments that start with a change number (a number, an optional letter and a colon right after the comment marker). The 10.0.0.x block is the neutral
    /// example range that tests and docs use for made-up hosts, so it is not reported.
    /// </summary>
    private static readonly Regex[] GenericPatterns =
    {
        new(@"C:\\Users\\(?!Example\b)[A-Za-z]", RegexOptions.Compiled),
        new(@"/home/(?!example\b)[A-Za-z]", RegexOptions.Compiled),
        new(@"(?<![:/])(?://+|/\*+)[ \t]*\d{4}[a-z]?:", RegexOptions.Compiled),
        new(@"(?<![\d.])(?:10(?!\.0\.0\.\d)(?:\." + Octet + @"){3}|(?:172\.(?:1[6-9]|2\d|3[01])|192\.168)(?:\." + Octet + @"){2})(?![\d])", RegexOptions.Compiled),
    };

    /// <summary>Generic citation patterns: change numbers and tracker ids (removed when the public tree is built).</summary>
    private static readonly Regex[] CitationPatterns =
    {
        new(@"\bPR[D][ -]?\d{1,4}[a-z]?\b", RegexOptions.Compiled),
        new(@"\bpr[d]-\d{4}", RegexOptions.Compiled),
        new(@"docs/pr[d]/", RegexOptions.Compiled),
        new(@"\blogix-mcp-[a-z0-9]{3}\b", RegexOptions.Compiled),
    };

    /// <summary>The documentation example path, removed before matching.</summary>
    private static readonly Regex ExamplePath = new(@"C:\\Users\\Example\b", RegexOptions.Compiled);

    /// <summary>An email address; the security contact line (SECURITY.md) may carry one.</summary>
    private static readonly Regex Email = new(@"[\w.+-]+@[\w.-]+", RegexOptions.Compiled);

    /// <summary>A copyright notice: the holder name after the year is the project's own and may be listed.</summary>
    private static readonly Regex Copyright = new(
        @"(?:©|\(c\)|Copyright)\s*\d{4}(?:\s*[-–]\s*\d{4})?\s+[A-Za-z0-9.-]+", RegexOptions.Compiled);

    /// <summary>
    /// Returns the generic-pattern hits on one line. <paramref name="allowEmail"/>
    /// drops email addresses first; <paramref name="allowCopyright"/> drops copyright notices.
    /// </summary>
    public static IEnumerable<string> ScanLine(string line, bool includeCitations,
        bool allowEmail = false, bool allowCopyright = false)
    {
        if (allowEmail)
            line = Email.Replace(line, " ");
        if (allowCopyright)
            line = Copyright.Replace(line, " ");
        line = ExamplePath.Replace(line, "");

        foreach (var p in GenericPatterns)
        {
            var m = p.Match(line);
            if (m.Success) yield return m.Value;
        }
        if (includeCitations)
            foreach (var p in CitationPatterns)
            {
                var m = p.Match(line);
                if (m.Success) yield return m.Value;
            }
    }

    /// <summary>
    /// Scans <paramref name="relativePaths"/> under <paramref name="root"/> and returns the hits as
    /// <c>path:line: 'text'</c>. <paramref name="includeCitations"/> selects whether the citation patterns
    /// take part. Binary files are skipped.
    /// </summary>
    public static List<string> Scan(string root, IEnumerable<string> relativePaths, bool includeCitations)
    {
        var hits = new List<string>();
        foreach (var rel in relativePaths)
        {
            var norm = rel.Replace('\\', '/');
            string text;
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(root, rel));
                if (Array.IndexOf(bytes, (byte)0) >= 0) continue;
                text = Encoding.UTF8.GetString(bytes);
            }
            catch (IOException) { continue; }

            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (var hit in ScanLine(lines[i], includeCitations, allowEmail: norm == "SECURITY.md",
                                              allowCopyright: norm is "LICENSE" or "README.md"))
                    hits.Add($"{norm}:{i + 1}: '{hit}'");
        }
        return hits;
    }
}
