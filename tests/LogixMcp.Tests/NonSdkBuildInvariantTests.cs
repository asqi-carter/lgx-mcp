using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LogixMcp.Tests;

/// <summary>
/// The non-SDK Linux build
/// (<c>dotnet test -p:EnableLogixSdk=false</c>) must compile. The failure mode
/// this guards against: a helper type or static method declared inside an
/// <c>#if LOGIX_SDK</c> block in <c>LogixTagToolsTests.cs</c> gets referenced
/// by a test outside that block (or in a sibling <c>#if LOGIX_SDK</c> block
/// whose guard was later dropped from the call site but not the declaration),
/// which compiles fine when the SDK feed is present but breaks the instant
/// <c>EnableLogixSdk=false</c> is used, because the identifier no longer
/// exists.
///
/// <para>
/// This is a text-level scan, not a Roslyn-based one: it walks
/// <c>#if</c>/<c>#endif</c> nesting line-by-line to classify every line as
/// "inside an <c>#if LOGIX_SDK</c> region" or not, extracts identifiers
/// declared (as a <c>class</c> or as a <c>private</c>/<c>internal</c>
/// method) on the "inside" lines, and then checks that every reference to
/// those identifiers anywhere else in the file also falls on an "inside"
/// line.
/// </para>
/// </summary>
public class NonSdkBuildInvariantTests
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
            "Could not locate project root (no .sln found walking up from " +
            AppContext.BaseDirectory + ").");
    }

    private static string TargetFilePath() =>
        Path.Combine(FindProjectRoot(), "tests", "LogixMcp.Tests", "LogixTagToolsTests.cs");

    /// <summary>Matches a top-of-line preprocessor directive, capturing the directive word and its condition text.</summary>
    private static readonly Regex DirectiveRegex =
        new(@"^\s*#\s*(if|elif|else|endif)\b\s*(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Matches a <c>class</c> declaration, capturing the type name.
    /// </summary>
    private static readonly Regex ClassDeclRegex =
        new(@"\bclass\s+(\w+)", RegexOptions.Compiled);

    /// <summary>
    /// Matches a <c>private</c>/<c>internal</c> method declaration, tolerating
    /// return types that wrap onto a following line (e.g. tuple return types).
    /// <c>Singleline</c> lets '.' cross line boundaries; the return-type group
    /// is non-greedy so it stops at the first "whitespace, identifier, '('"
    /// sequence, which is the actual method name/parameter-list boundary.
    /// </summary>
    private static readonly Regex MethodDeclRegex =
        new(
            @"\b(?:private|internal)\s+(?:static\s+)?(?:async\s+)?(?:sealed\s+)?" +
            @"[\w\.\?\[\]<>,\s\(\)]+?\s+(\w+)\s*\(",
            RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Classifies each (1-based) line of <paramref name="lines"/> as inside an
    /// active <c>#if LOGIX_SDK</c> region: every entry on the conditional
    /// stack at that point must be the literal condition <c>LOGIX_SDK</c>
    /// (this file never mixes <c>LOGIX_SDK</c> with unrelated or negated
    /// conditions in nested form, so a simple "all frames say LOGIX_SDK"
    /// check is sufficient and avoids implementing full macro evaluation).
    /// </summary>
    private static bool[] ClassifySdkOnlyLines(string[] lines)
    {
        var result = new bool[lines.Length];
        var stack = new Stack<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var m = DirectiveRegex.Match(lines[i]);
            if (m.Success)
            {
                var word = m.Groups[1].Value;
                switch (word)
                {
                    case "if":
                        stack.Push(m.Groups[2].Value.Trim());
                        break;
                    case "elif":
                        if (stack.Count > 0) stack.Pop();
                        stack.Push(m.Groups[2].Value.Trim());
                        break;
                    case "else":
                        if (stack.Count > 0)
                        {
                            var cond = stack.Pop();
                            stack.Push(cond.StartsWith("!", StringComparison.Ordinal)
                                ? cond[1..].Trim()
                                : "!" + cond);
                        }
                        break;
                    case "endif":
                        if (stack.Count > 0) stack.Pop();
                        break;
                }

                // A directive line itself is not "inside" the region it opens/closes.
                result[i] = false;
                continue;
            }

            result[i] = stack.Count > 0 && stack.All(c => c == "LOGIX_SDK");
        }

        return result;
    }

    /// <summary>
    /// Acceptance criterion "SdkOnlyHelpers_AreNotUsedOutsideTheSdkBlock":
    /// every identifier (class or private/internal method) declared inside an
    /// <c>#if LOGIX_SDK</c> region of <c>LogixTagToolsTests.cs</c> must be
    /// referenced only on lines that are themselves inside an
    /// <c>#if LOGIX_SDK</c> region. A violation means the non-SDK build
    /// (<c>-p:EnableLogixSdk=false</c>) will fail to compile with
    /// <c>CS0246</c>/<c>CS0103</c>, exactly as happened for
    /// <c>ThrowingOpenSdkFactory</c> and <c>BuildFakeSdkFactory</c>.
    /// </summary>
    [Fact]
    public void SdkOnlyHelpers_AreNotUsedOutsideTheSdkBlock()
    {
        var path  = TargetFilePath();
        Assert.True(File.Exists(path), $"missing {path}");
        var lines = File.ReadAllLines(path);
        var sdkOnly = ClassifySdkOnlyLines(lines);

        // Collect every identifier declared on an sdk-only line.
        var declared = new Dictionary<string, int>(); // name -> declaring line (0-based)
        for (var i = 0; i < lines.Length; i++)
        {
            if (!sdkOnly[i]) continue;

            foreach (Match m in ClassDeclRegex.Matches(lines[i]))
                declared.TryAdd(m.Groups[1].Value, i);
        }

        // Method declarations may wrap onto a following line (tuple return
        // types), so scan the joined text but resolve the match position back
        // to a line number to consult the sdk-only classification.
        var joined = string.Join('\n', lines);
        var lineStartOffsets = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            lineStartOffsets[i] = offset;
            offset += lines[i].Length + 1; // +1 for the '\n' joiner
        }

        int LineOf(int charIndex)
        {
            var lo = 0;
            var hi = lineStartOffsets.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (lineStartOffsets[mid] <= charIndex) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        foreach (Match m in MethodDeclRegex.Matches(joined))
        {
            var declLine = LineOf(m.Index);
            if (sdkOnly[declLine])
                declared.TryAdd(m.Groups[1].Value, declLine);
        }

        Assert.True(declared.Count > 0,
            "Expected to find at least one SDK-only helper declaration in " +
            "LogixTagToolsTests.cs; the #if LOGIX_SDK regions may have moved " +
            "or this scan's regexes need updating.");

        // For every declared identifier, every OTHER occurrence in the file
        // (word-boundary match) must also be on an sdk-only line.
        var violations = new List<string>();
        foreach (var (name, declLine) in declared)
        {
            var usageRegex = new Regex($@"\b{Regex.Escape(name)}\b");
            for (var i = 0; i < lines.Length; i++)
            {
                if (i == declLine) continue;
                if (!usageRegex.IsMatch(lines[i])) continue;
                if (sdkOnly[i]) continue;

                violations.Add(
                    $"'{name}' (declared line {declLine + 1}) referenced outside " +
                    $"the #if LOGIX_SDK block at line {i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "SDK-only helper(s) referenced outside their #if LOGIX_SDK block " +
            "(non-SDK build will fail with CS0246/CS0103):\n" +
            string.Join("\n", violations));
    }
}
