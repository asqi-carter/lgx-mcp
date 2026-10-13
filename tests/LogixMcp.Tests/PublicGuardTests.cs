using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// Tests of the public guard mechanism (<see cref="PublicGuard"/>), which holds generic patterns only.
/// This file is itself scanned by the tree guard: samples that would match a generic
/// pattern are assembled with <see cref="Join"/> so the source text never contains them whole.
/// It is part of the public tree and runs there too.
/// </summary>
public class PublicGuardTests
{
    private static string Join(params string[] parts) => string.Concat(parts);

    private static List<string> Hits(string line, bool citations = false) =>
        PublicGuard.ScanLine(line, citations).ToList();

    [Fact]
    public void PlainWords_AreNeverReported()
    {
        // The public guard carries no word list, plain or hashed: ordinary text only matches the generic patterns.
        Assert.Empty(Hits("zorblax quux-widget plimq42 and a_b"));
    }

    [Fact]
    public void GenericPatterns_CatchPersonalPathsAndPrivateAddresses()
    {
        Assert.NotEmpty(Hits(Join("C:", "\\Users\\", "zed", "\\x")));
        Assert.Empty(Hits(Join("C:", "\\Users\\", "Example", "\\x")));
        Assert.NotEmpty(Hits(Join("/ho", "me/", "zed", "/x")));
        Assert.NotEmpty(Hits(Join("host ", "10", ".1.2.3")));
        Assert.NotEmpty(Hits(Join("host ", "172", ".20.0.5")));
        Assert.NotEmpty(Hits(Join("host ", "192", ".168.4.4")));
        Assert.Empty(Hits(Join("host ", "172", ".32.0.5")));
        Assert.Empty(Hits(Join("version ", "10", ".0.40219.1")));
        Assert.Empty(Hits(Join("host ", "8", ".8.8.8")));
        Assert.Empty(Hits(Join("host ", "10", ".0.0.5")));   // neutral example range
        Assert.NotEmpty(Hits(Join("host ", "10", ".0.1.5")));
    }

    [Fact]
    public void NumberedCommentPrefix_IsFlagged()
    {
        Assert.NotEmpty(Hits(Join("        // ", "9993", "e: the value must be set before the call")));
        Assert.NotEmpty(Hits(Join("x = 1; // ", "9993", "f: no extra output")));
        Assert.NotEmpty(Hits(Join("/// ", "1234", ": summary")));
        Assert.NotEmpty(Hits(Join("/* ", "4321", "a: note */")));
        Assert.Empty(Hits("// The 2026 plan: ship it"));
        Assert.Empty(Hits(Join("var u = \"http://", "1234", ":80\";")));
        Assert.Empty(Hits(Join("// ", "12345", " items: ok")));
    }

    [Fact]
    public void CitationPatterns_OnlyApplyWhenRequested()
    {
        var prd = Join("PR", "D 9991");
        var hyphen = Join("PR", "D-9991b");
        var bead = Join("logix-mcp-", "x", "y", "9");
        foreach (var sample in new[] { prd, hyphen, bead })
        {
            Assert.NotEmpty(Hits(sample, citations: true));
            Assert.Empty(Hits(sample, citations: false));
        }
        var path = Join("docs/", "pr", "d/", "pr", "d-9992-x.md");
        var bare = Join("pr", "d-9992");
        var dir = Join("docs/", "pr", "d/");
        foreach (var sample in new[] { path, bare, dir, Join("PR", "D 99"), Join("PR", "D9991b") })
        {
            Assert.NotEmpty(Hits(sample, citations: true));
            Assert.Empty(Hits(sample, citations: false));
        }
        Assert.Empty(Hits(Join("PR", "Dx 9991"), citations: true));
        Assert.Empty(Hits(Join("logix-mcp-", "work"), citations: true));
    }

    [Fact]
    public void Scan_ReportsPathAndLine_AndAllowsEmailOnlyInSecurityFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pubguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var path = Join("/ho", "me/", "zed", "/x");
            File.WriteAllText(Path.Combine(tmp, "a.txt"), "fine\nhas " + path + " inside\n");
            File.WriteAllText(Path.Combine(tmp, "SECURITY.md"), "mail me@" + Join("192", ".168.4.4") + "\n");
            File.WriteAllText(Path.Combine(tmp, "b.txt"), "mail me@" + Join("192", ".168.4.4") + "\n");
            File.WriteAllBytes(Path.Combine(tmp, "bin.dat"), new byte[] { 0, 1, 2 });

            var hits = PublicGuard.Scan(tmp, new[] { "a.txt", "SECURITY.md", "b.txt", "bin.dat" }, false);

            Assert.Equal(new[] { "a.txt:2: '" + path[..7] + "'", "b.txt:1: '" + Join("192", ".168.4.4") + "'" }, hits);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void EmailIsAllowedOnlyWhenRequested()
    {
        var line = "mail me@" + Join("192", ".168.4.4");
        Assert.Empty(PublicGuard.ScanLine(line, false, allowEmail: true));
        Assert.NotEmpty(PublicGuard.ScanLine(line, false));
    }

    [Fact]
    public void CopyrightHolder_IsAllowedOnlyWhenRequested()
    {
        var path = Join("/ho", "me/", "zed", "/x");
        var line = Join("Copy", "right (c) 2026 Zorblax");
        Assert.Empty(PublicGuard.ScanLine(line, false, allowCopyright: true));
        Assert.Empty(PublicGuard.ScanLine(line, false));
        Assert.NotEmpty(PublicGuard.ScanLine(line + " " + path, false, allowCopyright: true));
    }
}
