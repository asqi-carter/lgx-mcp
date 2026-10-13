using System.Text.Json.Serialization;
using L5Sharp.Core;

/// <summary>
/// Success response of <see cref="DiffAgainstControllerHandler.Execute"/>: the
/// <c>logix_diff_projects</c> shape (<c>tags</c>, <c>dataTypes</c>, <c>routines</c>) plus the age
/// of the controller image.
/// </summary>
/// <param name="Tags">Controller-scoped tag differences.</param>
/// <param name="DataTypes">User-defined data type differences.</param>
/// <param name="Routines">Routine differences, keyed as <c>ProgramName/RoutineName</c>.</param>
/// <param name="Normalized">What was normalized away and not compared (<see cref="DiffProjectsHandler.NormalizedList"/>).</param>
/// <param name="ControllerImageTakenAt">UTC time the session's controller upload ran.</param>
/// <param name="Stale">
/// <c>true</c> when an lgx-mcp live edit happened after <paramref name="ControllerImageTakenAt"/>,
/// so the image no longer matches the running controller.
/// </param>
/// <param name="Hint">Present only when <paramref name="Stale"/> is <c>true</c>.</param>
public record DiffAgainstControllerResult(
    [property: JsonPropertyName("tags")]      IReadOnlyList<DiffEntry> Tags,
    [property: JsonPropertyName("dataTypes")] IReadOnlyList<DiffEntry> DataTypes,
    [property: JsonPropertyName("routines")]  IReadOnlyList<DiffEntry> Routines,
    [property: JsonPropertyName("normalized")] IReadOnlyList<string> Normalized,
    [property: JsonPropertyName("controllerImageTakenAt")] DateTimeOffset ControllerImageTakenAt,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint = null);

/// <summary>Structured error response of <see cref="DiffAgainstControllerHandler.Execute"/>.</summary>
/// <param name="Error">Human-readable error message.</param>
/// <param name="Kind">
/// <see cref="ErrorKinds.NoActiveSession"/> or <see cref="DiffAgainstControllerHandler.KindNotOnline"/>
/// for session validation failures; <c>null</c> for file errors.
/// </param>
/// <param name="Hint">What to do next, when known.</param>
public record DiffAgainstControllerError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Kind = null,
    [property: JsonPropertyName("hint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint = null);

/// <summary>
/// Pure handler for the read-only <c>logix_diff_against_controller</c> MCP tool.
/// Diffs an L5X file (before) against the controller image the active SDK session uploaded at
/// <c>logix_connect</c> time (after), using <see cref="DiffProjectsHandler"/>. Never writes either
/// side, never downloads, and makes no SDK or controller call.
/// </summary>
public static class DiffAgainstControllerHandler
{
    /// <summary>Error kind: a session exists but is not an online controller session.</summary>
    public const string KindNotOnline = "NotOnline";

    /// <summary>Hint attached to <see cref="DiffAgainstControllerResult.Hint"/> when the image is stale.</summary>
    public const string StaleHint = "logix_connect again for a fresh image";

#if LOGIX_SDK
    private const string ConnectHint =
        "Call logix_connect with backend=\"sdk\", sdkMode=\"online\" and an sdkCommPath so the " +
        "controller project is uploaded and correlated.";
#else
    private const string ConnectHint =
        "This build has no controller image to compare against; compare two L5X files with " +
        "logix_diff_projects instead.";
#endif

    /// <summary>
    /// Diffs <paramref name="l5xPath"/> against the session's controller image. Never throws.
    /// </summary>
    /// <param name="l5xPath">Path to the engineer's L5X file (the "before").</param>
    /// <param name="session">Tag-session singleton holding the connection and controller image.</param>
    /// <returns>A <see cref="DiffAgainstControllerResult"/> or a <see cref="DiffAgainstControllerError"/>.</returns>
    public static object Execute(string l5xPath, TagSessionService session)
    {
        if (!session.IsConnected)
            return new DiffAgainstControllerError(
                "No active session. logix_diff_against_controller needs an online, correlated session.",
                ErrorKinds.NoActiveSession, ConnectHint);

        if (!session.IsLiveController)
            return new DiffAgainstControllerError(
                "The active session is not online (virtual, file-backed or offline), so there is no " +
                "running controller to compare against.",
                KindNotOnline, ConnectHint);

        if (session.ControllerImage is not { } image || session.ControllerImageTakenAt is not { } takenAt)
            return new DiffAgainstControllerError(
                "The active online session has no controller image: only a session established by " +
                "uploading the controller project holds one.",
                ErrorKinds.NoActiveSession, ConnectHint);

        if (string.IsNullOrWhiteSpace(l5xPath))
            return new DiffAgainstControllerError("l5xPath must not be empty.");

        try
        {
            var before = SafeXml.LoadL5x(l5xPath);
            var diff   = DiffProjectsHandler.Compare(before, image);
            bool stale = session.LastLiveEditAt is { } editedAt && editedAt > takenAt;

            return new DiffAgainstControllerResult(
                Label(diff.Tags, session.WasTagLiveEdited),
                diff.DataTypes,
                AddRungDetail(Label(diff.Routines, session.WasRoutineLiveEdited), before, image),
                diff.Normalized,
                takenAt, stale,
                stale ? StaleHint : null);
        }
        catch (SafeXmlException ex)
        {
            return new DiffAgainstControllerError(ex.Message, ex.Kind);
        }
        catch (Exception ex)
        {
            return new DiffAgainstControllerError(ex.Message);
        }
    }

    /// <summary>
    /// For each <c>changed</c> ladder routine, attaches the differing rungs with wrapped before/after
    /// text. Rungs are matched by number. Non-ladder routines get no rung detail.
    /// </summary>
    private static IReadOnlyList<DiffEntry> AddRungDetail(IReadOnlyList<DiffEntry> entries, L5X before, L5X after) =>
        entries.Select(e =>
        {
            if (e.Change != "changed") return e;
            var a = FindRoutine(before, e.Name);
            var b = FindRoutine(after, e.Name);
            if (a is null || b is null || !WriteClassifier.IsLadder(a) || !WriteClassifier.IsLadder(b))
                return e;
            return e with { Rungs = DiffRungs(RungTexts(a), RungTexts(b)) };
        }).ToList();

    private static Routine? FindRoutine(L5X project, string key) =>
        project.Programs
            .SelectMany(p => p.Routines.Select(r => (Key: $"{p.Name}/{r.Name}", Routine: r)))
            .Where(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Routine)
            .FirstOrDefault();

    private static SortedDictionary<int, string> RungTexts(Routine routine)
    {
        var map = new SortedDictionary<int, string>();
        foreach (var rung in routine.Rungs)
            map[rung.Number] = rung.Text?.ToString() ?? string.Empty;
        return map;
    }

    private static IReadOnlyList<RungDiff> DiffRungs(
        SortedDictionary<int, string> before, SortedDictionary<int, string> after)
    {
        var result = new List<RungDiff>();
        foreach (var number in before.Keys.Union(after.Keys).OrderBy(n => n))
        {
            var hasA = before.TryGetValue(number, out var a);
            var hasB = after.TryGetValue(number, out var b);
            if (hasA && hasB)
            {
                if (a != b)
                    result.Add(new RungDiff(number, "changed",
                        UntrustedWrapper.WrapRungText(a!), UntrustedWrapper.WrapRungText(b!)));
            }
            else if (hasB)
                result.Add(new RungDiff(number, "added", null, UntrustedWrapper.WrapRungText(b!)));
            else
                result.Add(new RungDiff(number, "removed", UntrustedWrapper.WrapRungText(a!), null));
        }
        return result;
    }

    /// <summary>
    /// Marks entries <paramref name="wasEdited"/> accepts with <c>changedByThisSession: true</c>.
    /// </summary>
    private static IReadOnlyList<DiffEntry> Label(IReadOnlyList<DiffEntry> entries, Func<string, bool> wasEdited) =>
        entries.Select(e => wasEdited(e.Name) ? e with { ChangedByThisSession = true } : e).ToList();

    /// <summary>
    /// Converts a <c>routineXPath</c> such as
    /// <c>Controller/Programs/Program[@Name='P']/Routines/Routine[@Name='R']</c> to the diff key
    /// <c>P/R</c>, or <c>null</c> when it does not name a program routine.
    /// </summary>
    /// <param name="routineXPath">The XPath passed to a rung edit.</param>
    public static string? RoutineKeyFromXPath(string? routineXPath)
    {
        if (string.IsNullOrWhiteSpace(routineXPath)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(routineXPath,
            @"Program\[@Name\s*=\s*['""]([^'""]+)['""]\].*Routine\[@Name\s*=\s*['""]([^'""]+)['""]\]");
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }
}
