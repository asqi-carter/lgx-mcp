using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LogixMcp.Tests;

/// <summary>
/// Guards the <see cref="DescriptionAttribute"/> text of the read tools (and their rows in
/// <c>docs/tool-reference.md</c>) against the handlers: <c>logix_get_routine</c> serves a routine changed by an
/// accepted or assembled edit from the session image, <c>logix_project_summary</c> lists those routines in
/// <c>appliedEdits</c>, and none of them reads the live controller or a pending edit.
/// </summary>
public class ReadToolDescriptionsTests
{
    private static readonly string[] ReadTools =
    {
        "logix_project_summary", "logix_find_tags", "logix_get_routine", "logix_trace_origin", "logix_list_datatypes",
    };

    private static string DescriptionOf(string toolName)
    {
        var method = typeof(LogixProjectTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name == "McpServerToolAttribute"
                          && (string?)a.GetType().GetProperty("Name")?.GetValue(a) == toolName));
        return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    private static string DocRow(string toolName)
    {
        var path = Path.Combine(PublicGuard.DocsRoot(), "docs", "tool-reference.md");
        return File.ReadAllLines(path).Single(l => l.StartsWith($"| `{toolName}` |", StringComparison.Ordinal));
    }

    /// <summary>True when every mention of the live controller in <paramref name="text"/> is a negation.</summary>
    private static bool OnlyNegatesLiveController(string text) =>
        Regex.Matches(text, "live controller", RegexOptions.IgnoreCase)
            .All(m => Regex.IsMatch(text[..m.Index], @"(never|not) the $", RegexOptions.IgnoreCase));

    [Theory]
    [InlineData("logix_project_summary")]
    [InlineData("logix_find_tags")]
    [InlineData("logix_get_routine")]
    [InlineData("logix_trace_origin")]
    [InlineData("logix_list_datatypes")]
    public void ReadTool_Description_DoesNotClaimToReadTheLiveController(string toolName)
    {
        var description = DescriptionOf(toolName);
        Assert.True(OnlyNegatesLiveController(description), description);
        Assert.Contains("project file", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'project-file'", description);

        var row = DocRow(toolName);
        Assert.True(OnlyNegatesLiveController(row), row);
    }

    [Fact]
    public void ReadTools_AreAllChecked()
    {
        foreach (var tool in ReadTools)
            Assert.False(string.IsNullOrWhiteSpace(DescriptionOf(tool)), tool);
    }

#if LOGIX_SDK
    [Fact]
    public void GetRoutine_Description_SaysAppliedEditsAreServedFromTheSessionImage()
    {
        var description = DescriptionOf("logix_get_routine");
        Assert.Contains("session image", description);
        Assert.Contains("'session-image'", description);
        Assert.Contains("accepted or assembled", description);
        Assert.Contains("pending (not yet accepted) edit is never read", description);
        Assert.Contains("imageStale", description);
        Assert.Contains("reconnect", description);
        Assert.DoesNotContain("never any pending (unsaved) edit", description);
    }

    [Fact]
    public void ProjectSummary_Description_SaysAppliedEditsAreListedWithSessionImageSource()
    {
        var description = DescriptionOf("logix_project_summary");
        Assert.Contains("session image", description);
        Assert.Contains("appliedEdits", description);
        Assert.Contains("'appliedEditsSource': 'session-image'", description);
        Assert.Contains("pending (not yet accepted) edits are never read", description);
        Assert.Contains("imageStale", description);
        Assert.Contains("reconnect", description);
        Assert.DoesNotContain("never any pending (unsaved) edit", description);
    }

    [Theory]
    [InlineData("logix_find_tags")]
    [InlineData("logix_trace_origin")]
    [InlineData("logix_list_datatypes")]
    public void FileOnlyReadTool_Description_SaysItNeverReadsTheSessionImageAndFlagsStaleness(string toolName)
    {
        var description = DescriptionOf(toolName);
        Assert.Contains("never reads the session image", description);
        Assert.Contains("imageStale", description);
        Assert.Contains("reconnect", description);
    }

    [Theory]
    [InlineData("logix_project_summary", "appliedEditsSource: \"session-image\"")]
    [InlineData("logix_get_routine", "source: \"session-image\"")]
    [InlineData("logix_find_tags", "never reads the session image")]
    [InlineData("logix_trace_origin", "never reads the session image")]
    [InlineData("logix_list_datatypes", "never reads the session image")]
    public void ToolReferenceRow_MatchesTheDescription(string toolName, string expected)
    {
        var row = DocRow(toolName);
        Assert.Contains(expected, row);
        Assert.Contains("imageStale: true", row);
        Assert.Contains("reconnect", row);
        Assert.DoesNotContain("never any pending (unsaved) edit", row);
    }
#else
    [Theory]
    [InlineData("logix_project_summary")]
    [InlineData("logix_find_tags")]
    [InlineData("logix_get_routine")]
    [InlineData("logix_trace_origin")]
    [InlineData("logix_list_datatypes")]
    public void ReadTool_Description_DoesNotMentionTheSessionImage(string toolName)
    {
        var description = DescriptionOf(toolName);
        Assert.DoesNotContain("session image", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-image", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("appliedEdits", description);
        Assert.DoesNotContain("imageStale", description);
    }
#endif
}
