using System.Text.RegularExpressions;
using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// Every <c>///</c> summary in the C# sources opens with a capital letter. A summary that starts in lower case
/// reads as the tail of a sentence. Identifiers such as <c>logix_ping</c> or <c>replaceCount</c> are fine: only an
/// opening word made of plain lower-case letters is flagged.
/// </summary>
public class SummaryCapitalisationTests
{
    private static readonly Regex SummaryStart = new(@"^\s*///\s*<summary>", RegexOptions.Compiled);
    private static readonly Regex DocLinePrefix = new(@"^\s*///\s?", RegexOptions.Compiled);
    private static readonly Regex SummaryTag = new(@"^\s*///\s*<summary>\s*", RegexOptions.Compiled);

    // An optional "Note:" label in front of the text is looked through.
    private static readonly Regex Label = new(
        @"^Note\s*[:—–-]+\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OpeningWord = new(@"^([A-Za-z0-9_\-]+)(.?)", RegexOptions.Compiled);
    private static readonly Regex PlainLowerWord = new(@"^[a-z]+(?:-[a-z]+)*$", RegexOptions.Compiled);

    /// <summary>True when the first words of <paramref name="summaryText"/> open in lower case.</summary>
    public static bool OpensInLowercase(string summaryText)
    {
        var text = Label.Replace(summaryText.Trim(), string.Empty, 1);
        var m = OpeningWord.Match(text);
        if (!m.Success || !PlainLowerWord.IsMatch(m.Groups[1].Value))
            return false;
        return m.Groups[2].Value is not ("=" or "(" or "." or "_");
    }

    /// <summary>The text that opens the summary starting at <paramref name="start"/> (first lines joined).</summary>
    private static string OpeningText(IReadOnlyList<string> lines, int start)
    {
        var parts = new List<string> { SummaryTag.Replace(lines[start], string.Empty, 1) };
        for (var j = start + 1;
             j < lines.Count && string.Join(' ', parts).Length < 120
             && DocLinePrefix.IsMatch(lines[j]) && !lines[j - 1].Contains("</summary>", StringComparison.Ordinal);
             j++)
            parts.Add(DocLinePrefix.Replace(lines[j], string.Empty, 1));
        var text = string.Join(' ', parts.Select(p => p.Trim())).Trim();
        return Regex.Replace(text, @"^(<para>\s*)+", string.Empty);
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var dir in new[] { "src", "tests", "tools" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full))
                continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                    continue;
                yield return f;
            }
        }
    }

    /// <summary>No <c>///</c> summary in the sources opens in lower case.</summary>
    [Fact]
    public void SummaryDocComments_OpenWithACapital()
    {
        var root = PublicGuard.FindProjectRoot();
        var hits = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!SummaryStart.IsMatch(lines[i]))
                    continue;
                var text = OpeningText(lines, i);
                if (OpensInLowercase(text))
                    hits.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}: {(text.Length > 70 ? text[..70] : text)}");
            }
        }

        Assert.True(hits.Count == 0, $"Summaries that open in lower case ({hits.Count}):\n  " + string.Join("\n  ", hits));
    }

    /// <summary>The detector flags a lower-case opening word, also behind a label, and leaves identifiers and capitals alone.</summary>
    [Theory]
    [InlineData("accept and cancel are posted.", true)]
    [InlineData("Note: progress is reported per case.", true)]
    [InlineData("Note - every payload carries a mode.", true)]
    [InlineData("program-written status passes between tags.", true)]
    [InlineData("Accept and cancel are posted.", false)]
    [InlineData("Note: Progress is reported per case.", false)]
    [InlineData("logix_ping reports the settings.", false)]
    [InlineData("replaceCount must be positive.", false)]
    [InlineData("target=\"live\" imports at once.", false)]
    [InlineData("<c>logix_ping</c> reports the settings.", false)]
    public void OpensInLowercase_DetectsTheOpeningWord(string text, bool expected) =>
        Assert.Equal(expected, OpensInLowercase(text));

    /// <summary>A leading <c>Note:</c> label is looked through, so the sentence after it is what gets checked.</summary>
    [Fact]
    public void OpensInLowercase_LooksThroughTheNoteLabel()
    {
        Assert.True(OpensInLowercase("Note: the pump starts after the valve opens."));
        Assert.True(OpensInLowercase("Note - the pump starts after the valve opens."));
        Assert.False(OpensInLowercase("Note: The pump starts after the valve opens."));
    }
}
