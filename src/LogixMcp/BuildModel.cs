// ── BuildModel — result shapes and log parsing for logix_build ──
//
// Core assembly: no Rockwell types. The SDK adapter (BuildHandler) collects the build log events the SDK
// raises while BuildAsync runs, hands each message to BuildLogParser, and returns a BuildResult. The build
// only reads the project; it never goes online, downloads or changes mode.
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>The accepted values of the <c>target</c> argument of <c>logix_build</c>.</summary>
public static class BuildTargetNames
{
    /// <summary>The SDK chooses: the previous build's target, or a physical controller on a first build.</summary>
    public const string Default = "default";

    /// <summary>Build for a physical controller.</summary>
    public const string Physical = "physical";

    /// <summary>Build for the Echo emulated controller.</summary>
    public const string Echo = "echo";

    /// <summary>Every accepted value.</summary>
    public static readonly IReadOnlyList<string> All = [Default, Physical, Echo];

    /// <summary>
    /// Normalizes a caller-supplied target: <c>null</c>/blank means <see cref="Default"/>; matching ignores case and
    /// surrounding white space.
    /// </summary>
    /// <param name="target">The raw argument.</param>
    /// <returns>One of <see cref="All"/>, or <c>null</c> when the value is not a known target.</returns>
    public static string? Normalize(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return Default;
        var t = target.Trim().ToLowerInvariant();
        return All.Contains(t) ? t : null;
    }
}

/// <summary>One error or warning from the build log.</summary>
/// <param name="Location">Readable location, e.g. <c>MainProgram/MainRoutine rung 3</c>; omitted when the log gave none.</param>
/// <param name="Program">Program name, when the log named one.</param>
/// <param name="Routine">Routine name, when the log named one.</param>
/// <param name="Rung">Rung number, when the log named one.</param>
/// <param name="Line">Line number (structured text), when the log named one.</param>
/// <param name="Code">The SDK/Studio error or warning code, when the log carried one.</param>
/// <param name="Message">The message text from the build log.</param>
public sealed record BuildIssue(
    [property: JsonPropertyName("location"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Location,
    [property: JsonPropertyName("program"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Program,
    [property: JsonPropertyName("routine"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Routine,
    [property: JsonPropertyName("rung"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?    Rung,
    [property: JsonPropertyName("line"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?    Line,
    [property: JsonPropertyName("code"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>The outcome of a build the SDK actually ran.</summary>
/// <param name="Status"><c>passed</c> or <c>failed</c>.</param>
/// <param name="Target">The requested target: <see cref="BuildTargetNames"/> value.</param>
/// <param name="Errors">Errors from the build log, each with its location where the log gave one.</param>
/// <param name="Warnings">Warnings from the build log.</param>
/// <param name="ElapsedMs">Wall-clock time of the build call, in milliseconds.</param>
/// <param name="DesignerVersion">The Logix Designer version, when known.</param>
/// <param name="Note">What to know about the result; omitted when nothing needs saying.</param>
public sealed record BuildResult(
    [property: JsonPropertyName("status")]   string Status,
    [property: JsonPropertyName("target")]   string Target,
    [property: JsonPropertyName("errors")]   IReadOnlyList<BuildIssue> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<BuildIssue> Warnings,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("designerVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DesignerVersion = null,
    [property: JsonPropertyName("note"),            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null)
{
    /// <summary><see cref="Status"/> when the build ran clean.</summary>
    public const string Passed = "passed";

    /// <summary>
    /// <see cref="Status"/> when the build ran clean but the project holds rungs staged in the session image, which
    /// Logix does not check. <see cref="Note"/> says so.
    /// </summary>
    public const string PassedExcludingStaged = "passed-excluding-staged";

    /// <summary>Note returned with <see cref="PassedExcludingStaged"/>.</summary>
    public const string PassedExcludingStagedNote =
        "The build passed, but this session holds rungs staged in its image, and Logix does not check staged rungs. " +
        "They are not covered by this result; they are checked when published to the controller.";

    /// <summary><see cref="Status"/> when the build ran and reported errors.</summary>
    public const string Failed = "failed";
}

/// <summary>A structured refusal from <c>logix_build</c> that is not a capability refusal.</summary>
/// <param name="Error">Plain-language description.</param>
/// <param name="Hint">What to do next.</param>
/// <param name="Kind">An <see cref="ErrorKinds"/> value.</param>
/// <param name="Session">Session state (<c>faulted</c>) when the SDK server faulted.</param>
/// <param name="BuildVerify"><c>unavailable</c> when the offline build check could not be run (no build ran, online or offline); omitted otherwise.</param>
public sealed record BuildError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint,
    [property: JsonPropertyName("kind")]  string Kind,
    [property: JsonPropertyName("session"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Session = null,
    [property: JsonPropertyName("buildVerify"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BuildVerify = null);

/// <summary>
/// Thrown by an SDK build runner when the build check is unavailable on this Logix Designer (before v37). Not a
/// build failure: callers report <c>buildVerify: "unavailable"</c>.
/// </summary>
public sealed class SdkBuildUnavailableException : Exception
{
    /// <summary>The Logix Designer version text (<c>major.minor</c>), or <c>null</c> when unknown.</summary>
    public string? DesignerVersion { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the capability table said.</param>
    /// <param name="designerVersion">The Designer version, when known.</param>
    public SdkBuildUnavailableException(string message, string? designerVersion) : base(message) =>
        DesignerVersion = designerVersion;
}

/// <summary>Turns one SDK build log message into a <see cref="BuildIssue"/> and decides whether it is an error or a warning.</summary>
public static class BuildLogParser
{
    /// <summary>How a build log message is classified.</summary>
    public enum Severity
    {
        /// <summary>Not an error or a warning (progress, status chatter).</summary>
        None,
        /// <summary>A build warning.</summary>
        Warning,
        /// <summary>A build error.</summary>
        Error,
    }

    private static readonly Regex Prefix =
        new(@"^\s*(?:\[?(?<sev>error|warning)\]?)\s*[:\-]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex XPathLoc =
        new(@"Program\[@Name='(?<p>[^']+)'\](?:/Routines/Routine\[@Name='(?<r>[^']+)'\])?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProgramWord =
        new(@"\bProgram[:=\s]+'?(?<p>[A-Za-z_][A-Za-z0-9_]*)'?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RoutineWord =
        new(@"\bRoutine[:=\s]+'?(?<r>[A-Za-z_][A-Za-z0-9_]*)'?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Slashed =
        new(@"\b(?<p>[A-Za-z_][A-Za-z0-9_]*)[/\\](?<r>[A-Za-z_][A-Za-z0-9_]*)\b(?=\s*[,:\-\s]\s*(?:rung|line)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RungNo =
        new(@"\bRung\s*#?\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineNo =
        new(@"\bLine\s*#?\s*(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CodeTok =
        new(@"(?:\b(?:error|warning|code)\s*#?\s*[:=]?\s*(?<c>[A-Za-z]{0,5}-?\d{2,6})\b|\[(?<c2>[A-Za-z]{1,5}-?\d{2,6})\])", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Classifies a message from the SDK's error channel: an error unless it opens with <c>Warning</c>.
    /// </summary>
    /// <param name="message">The message text.</param>
    public static Severity ClassifyErrorChannel(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return Severity.None;
        var m = Prefix.Match(message);
        return m.Success && m.Groups["sev"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
            ? Severity.Warning : Severity.Error;
    }

    /// <summary>
    /// Classifies a message from the SDK's status channel: only messages that open with <c>Error</c> or
    /// <c>Warning</c> count; everything else is progress chatter.
    /// </summary>
    /// <param name="message">The message text.</param>
    public static Severity ClassifyStatusChannel(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return Severity.None;
        var m = Prefix.Match(message);
        if (!m.Success) return Severity.None;
        return m.Groups["sev"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase) ? Severity.Warning : Severity.Error;
    }

    /// <summary>
    /// Reads the location (program, routine, rung or line), the code and the message out of one log message. A
    /// message with no recognizable location still yields an issue, with the location omitted.
    /// </summary>
    /// <param name="message">One build log message.</param>
    public static BuildIssue Parse(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.Trim();
        var body = Prefix.Replace(text, string.Empty, 1);
        if (body.Length == 0) body = text;

        string? program = null, routine = null;
        var x = XPathLoc.Match(text);
        if (x.Success)
        {
            program = x.Groups["p"].Value;
            if (x.Groups["r"].Success) routine = x.Groups["r"].Value;
        }
        else
        {
            var s = Slashed.Match(text);
            if (s.Success) { program = s.Groups["p"].Value; routine = s.Groups["r"].Value; }
            else
            {
                var p = ProgramWord.Match(text);
                if (p.Success) program = p.Groups["p"].Value;
                var r = RoutineWord.Match(text);
                if (r.Success) routine = r.Groups["r"].Value;
            }
        }

        int? rung = RungNo.Match(text) is { Success: true } rm ? int.Parse(rm.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        int? line = LineNo.Match(text) is { Success: true } lm ? int.Parse(lm.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

        string? code = null;
        var c = CodeTok.Match(text);
        if (c.Success) code = c.Groups["c"].Success ? c.Groups["c"].Value : c.Groups["c2"].Value;

        string? location = null;
        if (program is not null || routine is not null || rung is not null || line is not null)
        {
            var parts = new List<string>();
            if (program is not null && routine is not null) parts.Add($"{program}/{routine}");
            else if (program is not null) parts.Add(program);
            else if (routine is not null) parts.Add(routine);
            if (rung is not null) parts.Add($"rung {rung}");
            if (line is not null) parts.Add($"line {line}");
            location = string.Join(' ', parts);
        }

        return new BuildIssue(location, program, routine, rung, line, code, body);
    }
}
