using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guards the public safety docs against claims the server does not make true.
/// The public docs describe the no-SDK 1.0.0 release, so they document the live-writes
/// flag and must not document the SDK/Studio lane's flags.
/// The docs are read from the directory
/// <see cref="PublicGuard.DocsRoot"/> returns.
/// </summary>
public class SafetyClaimsTests
{
    private static readonly string PublicRoot = PublicGuard.DocsRoot();

    private static string Normalize(string path) =>
        Regex.Replace(File.ReadAllText(Path.Combine(PublicRoot, path)), @"\s+", " ");

    /// <summary>Retired claims; matched case-insensitively on whitespace-normalised text.</summary>
    private static readonly string[] RetiredClaims =
    {
        @"cannot accept its own",
        @"can(?:not|'t) (?:ever )?accept (?:its|their) own",
        @"cannot ever bypass",
        @"cannot,? ever bypass",
        @"bypass the accept/cancel review",
        @"never execute unreviewed",
        @"only studio can",
    };

    [Fact]
    public void NoCannotAcceptOwnEditsClaim()
    {
        var offenders = new List<string>();
        foreach (var rel in PublicGuard.DocFiles().Where(f => f.EndsWith(".md", StringComparison.Ordinal)))
        {
            var text = Normalize(rel);
            foreach (var pattern in RetiredClaims)
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                    offenders.Add($"{rel}: /{pattern}/");
        }

        Assert.True(offenders.Count == 0,
            "Retired safety claims found in the public docs: " + string.Join("; ", offenders));
    }

    [Theory]
    [InlineData("SECURITY.md")]
    [InlineData("README.md")]
    [InlineData("docs/safety-model.md")]
    [InlineData("docs/tool-reference.md")]
    public void SafetyDocs_DescribeLiveWritesFlag_AndNoSdkLaneFlags(string path)
    {
        var text = Normalize(path);

        Assert.Contains("LGX_ENABLE_LIVE_WRITES", text);
        // The README environment table lists every variable the build reads, including the
        // SDK-lane ones, the tool reference names the SDK-lane gates, and the safety model has a section on the SDK
        // lane's switches; SECURITY.md does not mention them.
        if (path != "README.md" && path != "docs/tool-reference.md" && path != "docs/safety-model.md")
            Assert.DoesNotContain("LGX_ENABLE_STUDIO_AUTOMATION", text);
    }

    [Fact]
    public void ControllerDownloadFlag_IsNotDocumentedInNoSdkRelease()
    {
        foreach (var path in new[] { "SECURITY.md", "CHANGELOG.md" })
            Assert.DoesNotContain("LGX_ENABLE_CONTROLLER_DOWNLOAD", Normalize(path));
    }

    [Fact]
    public void ModeChange_IsDescribedAsNeverApplied()
    {
        foreach (var path in new[] { "SECURITY.md", "docs/safety-model.md" })
            Assert.Contains("ModeChangeNotApplied", Normalize(path));
    }
}
