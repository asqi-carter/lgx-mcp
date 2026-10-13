namespace LogixMcp.Tests;

/// <summary>
/// This change regression guard: no test source may use the host-wide upload lease. The scan lives in
/// <see cref="Scan"/> so it can be unit-tested against fixture strings.
/// </summary>
public class HostUploadLeaseDefaultGuardTests
{
    // Built from two pieces so this file never contains the forbidden token as one literal.
    private const string Forbidden = "HostUploadLease" + ".Default";

    /// <summary>This file's name: the real scan skips it, because its fixtures contain the forbidden token.</summary>
    private const string OwnFileName = "HostUploadLeaseDefaultGuardTests.cs";

    /// <summary>
    /// Returns the 1-based line numbers in <paramref name="source"/> that use the forbidden token in code.
    /// Comment lines (<c>//</c>, <c>///</c>) are ignored, since doc text may mention the token.
    /// </summary>
    /// <param name="source">The text of one source file.</param>
    /// <returns>The offending line numbers; empty when the source is clean.</returns>
    internal static IReadOnlyList<int> Scan(string source)
    {
        var hits = new List<int>();
        var lines = source.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (lines[i].Contains(Forbidden, StringComparison.Ordinal)) hits.Add(i + 1);
        }
        return hits;
    }

    /// <summary>Scans every <c>.cs</c> file under <paramref name="testsDir"/> except this one and build output.</summary>
    /// <param name="testsDir">The tests/ directory to scan.</param>
    /// <returns>One <c>file:line</c> entry per violation.</returns>
    internal static IReadOnlyList<string> ScanTree(string testsDir)
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), OwnFileName, StringComparison.Ordinal)) continue;
            var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj")) continue;
            foreach (var line in Scan(File.ReadAllText(file)))
                offenders.Add($"{Path.GetRelativePath(testsDir, file)}:{line}");
        }
        return offenders;
    }

    [Fact]
    public void NoTestFile_ReferencesHostUploadLeaseDefault()
    {
        var repo = FixtureMnemonicGuardTests.RepoRoot();
        Assert.NotNull(repo);
        var testsDir = Path.Combine(repo!, "tests");
        Assert.True(Directory.Exists(testsDir));

        Assert.Empty(ScanTree(testsDir));
    }

    [Fact]
    public void Scan_FlagsFixtureContainingHostUploadLeaseDefault()
    {
        var fixture = "var a = 1;\nvar r = Build(" + Forbidden + ");\n";

        Assert.Equal(new[] { 2 }, Scan(fixture));
    }

    [Fact]
    public void Scan_ReportsNothingForCleanFixture()
    {
        var fixture = "using var scope = new IsolatedUploadLease();\nvar r = Build(scope.Lease);\n";

        Assert.Empty(Scan(fixture));
    }

    [Fact]
    public void Scan_IgnoresCommentLines()
    {
        var fixture = "/// <see cref=\"" + Forbidden + "\"/>\n    // " + Forbidden + "\n";

        Assert.Empty(Scan(fixture));
    }
}
