using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// Scans the whole tree these tests run in for internal identifiers, using <see cref="PublicGuard"/>:
/// a small set of generic patterns (citation forms, tracker ids, personal paths, private addresses), so no
/// sensitive word, plain or hashed, is embedded here. The files it scans come from
/// <see cref="ExportedLayout.GuardedFiles"/>.
/// It is part of the public tree and runs there too.
/// </summary>
public class PublicTreeGuardTests
{
    /// <summary>No file in the tree contains a generic internal pattern (citation form, tracker id, personal path, private address).</summary>
    [Fact]
    public void NoInternalTokens()
    {
        var root = PublicGuard.FindProjectRoot();
        var hits = PublicGuard.Scan(root, ExportedLayout.GuardedFiles(root), includeCitations: true);

        Assert.True(
            hits.Count == 0,
            $"Internal tokens found in the tree ({hits.Count}):\n  " + string.Join("\n  ", hits));
    }
}
