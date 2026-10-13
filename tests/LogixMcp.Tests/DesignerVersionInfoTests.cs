using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>The pinned unsupported-version pattern and the version helpers.</summary>
public class DesignerVersionInfoTests
{
    [Fact]
    public void UnsupportedVersionPattern_MatchesTheExactSdkMessage()
    {
        const string message = "Operation not supported on Logix Designer version 36.5.";

        var m = Regex.Match(message, DesignerVersionInfo.UnsupportedVersionPattern);
        Assert.True(m.Success);
        Assert.Equal("36", m.Groups[1].Value);
        Assert.Equal("5", m.Groups[2].Value);

        var parsed = DesignerVersionInfo.TryParseUnsupportedVersion(message);
        Assert.NotNull(parsed);
        Assert.Equal("36.5", parsed!.ToString());
        Assert.Equal(DesignerVersionSource.SdkError, parsed.Source);
        Assert.True(parsed.KnownBelowBuildCheck);
        Assert.False(parsed.BuildCheckAvailable);
    }

    [Theory]
    [InlineData("undefined tag reference XIC_Motor")]
    [InlineData("Operation not supported on this controller")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedVersionPattern_DoesNotMatchRealBuildFailures(string? message) =>
        Assert.Null(DesignerVersionInfo.TryParseUnsupportedVersion(message));

    [Fact]
    public void UnsupportedVersionPattern_IsDefinedOnceInSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LogixMcp.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var hits = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("not supported on Logix Designer version"))
            .ToList();
        Assert.Single(hits);
    }

    [Theory]
    [InlineData("37.0", true)]
    [InlineData("38.11", true)]
    [InlineData("36.11", null)]   // a project revision below 37 proves nothing about the Designer
    public void ProjectRevision_BuildCheckAvailable(string revision, bool? expected)
    {
        var v = DesignerVersionInfo.FromProjectRevision(revision);
        Assert.NotNull(v);
        Assert.Equal(expected, v!.BuildCheckAvailable);
        Assert.False(v.KnownBelowBuildCheck);
    }

    [Fact]
    public void ProjectRevision_UnparseableIsUnknown()
    {
        Assert.Null(DesignerVersionInfo.FromProjectRevision(null));
        Assert.Null(DesignerVersionInfo.FromProjectRevision("abc"));
    }
}
