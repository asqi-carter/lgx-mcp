using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

/// <summary>
/// Rejects tool calls whose argument names are not in the tool's input schema. The MCP framework
/// silently drops unknown keys before binding, so a typo such as <c>sdkCommPath</c> for
/// <c>commPath</c> would otherwise be ignored. Runs from <see cref="ToolErrorFilter"/> before the
/// tool does any work.
/// </summary>
public static class ToolArgumentFilter
{
    /// <summary>Largest Levenshtein distance for which a closest-match suggestion is offered.</summary>
    public const int MaxSuggestionDistance = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the property names declared by a tool's JSON input schema.</summary>
    /// <param name="inputSchema">The tool's <c>inputSchema</c>.</param>
    /// <returns>The declared argument names (empty when the schema has no properties).</returns>
    public static IReadOnlyList<string> ValidNames(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("properties", out var props)
        && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    /// <summary>
    /// Checks the supplied argument names against the valid ones (case-insensitive, like logix_connect).
    /// </summary>
    /// <param name="toolName">Tool being called, for the error payload.</param>
    /// <param name="argumentNames">Names the caller supplied.</param>
    /// <param name="validNames">Names declared by the tool's input schema.</param>
    /// <returns>An <c>InvalidArguments</c> error result, or <c>null</c> when every name is known.</returns>
    public static CallToolResult? Check(
        string? toolName, IEnumerable<string>? argumentNames, IReadOnlyList<string> validNames)
    {
        if (argumentNames is null) return null;
        var valid = new HashSet<string>(validNames, StringComparer.OrdinalIgnoreCase);
        var unknown = argumentNames.Where(k => !valid.Contains(k)).Distinct().ToList();
        if (unknown.Count == 0) return null;

        var suggestions = new Dictionary<string, string>();
        foreach (var name in unknown)
        {
            var best = ClosestMatch(name, validNames);
            if (best is not null) suggestions[name] = best;
        }

        var tool = toolName ?? "tool";
        var error = $"Unknown argument{(unknown.Count == 1 ? "" : "s")} for {tool}: " +
                    $"{string.Join(", ", unknown.Select(n => $"'{n}'"))}. " +
                    (validNames.Count == 0
                        ? $"{tool} takes no arguments."
                        : $"Valid arguments are: {string.Join(", ", validNames)}.");
        if (suggestions.Count > 0)
            error += " " + string.Join(" ", suggestions.Select(s => $"Did you mean '{s.Value}' instead of '{s.Key}'?"));

        var payload = new
        {
            error,
            kind = ErrorKinds.InvalidArguments,
            tool = toolName,
            unknownArguments = unknown,
            validArguments = validNames,
            suggestions = suggestions.Count > 0 ? suggestions : null,
            hint = $"Remove or rename the unknown argument{(unknown.Count == 1 ? "" : "s")} and call {tool} again; nothing was executed.",
        };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }],
        };
    }

    /// <summary>Finds the valid name closest to <paramref name="name"/> within the suggestion distance.</summary>
    /// <param name="name">The unknown name.</param>
    /// <param name="candidates">Valid names.</param>
    /// <returns>The closest candidate, or <c>null</c> when none is within <see cref="MaxSuggestionDistance"/>.</returns>
    public static string? ClosestMatch(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in candidates)
        {
            var d = Levenshtein(name, c);
            if (d < bestDistance) { best = c; bestDistance = d; }
        }
        return bestDistance <= MaxSuggestionDistance ? best : null;
    }

    /// <summary>Case-insensitive Levenshtein edit distance.</summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    /// <returns>The minimum number of single-character edits turning one into the other.</returns>
    public static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
