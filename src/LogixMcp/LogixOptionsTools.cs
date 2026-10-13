using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>
/// MCP tool type exposing parameter discovery. Needs no open project and no PLC connection.
/// </summary>
[McpServerToolType]
public class LogixOptionsTools
{
    /// <summary>
    /// Returns the legal values of every fixed-choice tool parameter, or of one parameter when named.
    /// </summary>
    [McpServerTool(Name = "logix_list_options")]
    [Description(
        "Lists every fixed-choice parameter across the registered tools (for example backend, format, " +
        "scope, target, collision) with its legal values and a one-line meaning for each. Because the legal " +
        "values can differ per tool, each entry has one 'usages' item per tool, with the default " +
        "when there is one. Pass 'parameter' to get exactly one entry. Use this when you are unsure which value " +
        "a parameter accepts, instead of guessing and getting a validation error. " +
        "An unknown parameter name returns an UnknownParameter error listing the valid names. " +
        "Requires no open project and no PLC connection.")]
    public string ListOptions(
        [Description(
            "Optional parameter name, e.g. 'format' or 'backend' (case-insensitive). Returns exactly one entry. " +
            "Omit to list every fixed-choice parameter.")]
        string? parameter = null)
    {
        var sw     = Stopwatch.StartNew();
        var result = GetListOptionsResponse(parameter);
        sw.Stop();
        TrafficLogger.Append(
            "logix_list_options",
            requestBytes:  TrafficLogger.MeasureRequestBytes(new { parameter }),
            responseBytes: TrafficLogger.MeasureResponseBytes(result),
            elapsedMs:     sw.ElapsedMilliseconds,
            ok:            TrafficLogger.IsOkResponse(result));
        return result;
    }

    /// <summary>Builds the <c>logix_list_options</c> JSON response (testable without the MCP layer).</summary>
    /// <param name="parameter">Optional parameter name.</param>
    /// <returns>JSON string.</returns>
    public static string GetListOptionsResponse(string? parameter = null)
    {
        var r = ListOptionsHandler.Execute(parameter);
        return JsonSerializer.Serialize(r, r.GetType());
    }
}
