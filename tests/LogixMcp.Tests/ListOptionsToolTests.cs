using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <c>logix_list_options</c>: the output must cover every enum-like parameter of the registered tool
/// schemas, the optional <c>parameter</c> argument returns exactly one entry, and the hand table (code-validated
/// string parameters) must describe real parameters with a one-line meaning per value.
/// </summary>
public class ListOptionsToolTests
{
    private static IEnumerable<Assembly> ToolAssemblies()
    {
        yield return typeof(LogixTagTools).Assembly;
#if LOGIX_SDK
        yield return typeof(LogixSdkTools).Assembly;
#endif
    }

    private static JsonElement ListAll() =>
        JsonDocument.Parse(LogixOptionsTools.GetListOptionsResponse()).RootElement.GetProperty("options");

    private static HashSet<string> ListedParameters() =>
        ListAll().EnumerateArray().Select(e => e.GetProperty("parameter").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the real MCP input schema of a tool method (what clients see).</summary>
    private static JsonElement InputSchema(MethodInfo method)
    {
        object? target = method.IsStatic ? null : RuntimeHelpers.GetUninitializedObject(method.DeclaringType!);
        var tool = McpServerTool.Create(method, target);
        return tool.ProtocolTool.InputSchema;
    }

    /// <summary>Property names in a tool's input schema that carry a JSON-schema "enum".</summary>
    private static IEnumerable<(string Tool, string Parameter, string[] Values)> SchemaEnums()
    {
        foreach (var (tool, method) in ListOptionsHandler.RegisteredTools(ToolAssemblies()))
        {
            var schema = InputSchema(method);
            if (!schema.TryGetProperty("properties", out var props)) continue;
            foreach (var p in props.EnumerateObject())
            {
                var node = p.Value;
                // Nullable enums are emitted as anyOf / type arrays; look one level down as well.
                var enums = new List<JsonElement>();
                if (node.TryGetProperty("enum", out var e)) enums.Add(e);
                if (node.TryGetProperty("anyOf", out var any))
                    foreach (var alt in any.EnumerateArray())
                        if (alt.TryGetProperty("enum", out var e2)) enums.Add(e2);
                foreach (var en in enums)
                    yield return (tool, p.Name,
                        en.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                          .Select(v => v.GetString()!).ToArray());
            }
        }
    }

    [Fact]
    public void ListOptions_CoversEveryEnumParameterInRegisteredToolSchemas()
    {
        var schemaEnums = SchemaEnums().ToList();
        Assert.NotEmpty(schemaEnums); // logix_import_datatype.collision at minimum

        var options = ListAll().EnumerateArray().ToList();
        foreach (var (tool, parameter, values) in schemaEnums)
        {
            var entry = options.FirstOrDefault(o =>
                string.Equals(o.GetProperty("parameter").GetString(), parameter, StringComparison.OrdinalIgnoreCase));
            Assert.True(entry.ValueKind == JsonValueKind.Object, $"{tool}.{parameter} (schema enum) is not listed.");

            var usage = entry.GetProperty("usages").EnumerateArray()
                .FirstOrDefault(u => u.GetProperty("tool").GetString() == tool);
            Assert.True(usage.ValueKind == JsonValueKind.Object, $"{tool}.{parameter} has no usage entry.");

            var listed = usage.GetProperty("values").EnumerateArray()
                .Select(v => v.GetProperty("value").GetString()).ToHashSet();
            foreach (var v in values)
                Assert.Contains(v, listed);
        }
    }

    [Fact]
    public void ListOptions_CoversEveryEnumTypedToolParameter()
    {
        var listed = ListedParameters();
        foreach (var (tool, method) in ListOptionsHandler.RegisteredTools(ToolAssemblies()))
            foreach (var p in method.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (type.IsEnum)
                    Assert.True(listed.Contains(p.Name!), $"{tool}.{p.Name} is an enum parameter but is not listed.");
            }
    }

    [Fact]
    public void ListOptions_EveryHandTableRowNamesARealParameterOfARegisteredTool()
    {
        var tools = ListOptionsHandler.RegisteredTools(ToolAssemblies()).ToDictionary(t => t.Name, t => t.Method);
        foreach (var row in ListOptionsHandler.HandTable)
        {
            // Rows for SDK-only tools may legitimately be absent from a no-SDK build.
            if (!tools.TryGetValue(row.Tool, out var method))
            {
                Assert.True(SdkToolNames.All.Contains(row.Tool), $"Hand-table tool '{row.Tool}' is not a registered tool.");
                continue;
            }
            var names = method.GetParameters().Select(p => p.Name).ToList();
            // logix_connect.sdkMode exists only in the SDK build.
            if (row.Parameter == "sdkMode" && !names.Contains("sdkMode")) continue;
            Assert.True(names.Contains(row.Parameter), $"{row.Tool} has no parameter '{row.Parameter}'.");
        }
    }

    [Fact]
    public void ListOptions_HandTableRowsHaveValuesAndOneLineMeanings()
    {
        Assert.NotEmpty(ListOptionsHandler.HandTable);
        foreach (var row in ListOptionsHandler.HandTable)
        {
            Assert.NotEmpty(row.Values);
            foreach (var (value, meaning) in row.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.False(string.IsNullOrWhiteSpace(meaning), $"{row.Tool}.{row.Parameter}={value} lacks a meaning.");
                Assert.DoesNotContain('\n', meaning);
            }
            if (row.Default is not null)
                Assert.Contains(row.Default, row.Values.Select(v => v.Value));
        }
    }

    [Fact]
    public void ListOptions_EveryListedValueHasAMeaning()
    {
        foreach (var option in ListAll().EnumerateArray())
            foreach (var usage in option.GetProperty("usages").EnumerateArray())
                foreach (var v in usage.GetProperty("values").EnumerateArray())
                {
                    var meaning = v.GetProperty("meaning").GetString();
                    Assert.False(string.IsNullOrWhiteSpace(meaning));
                    Assert.DoesNotContain('\n', meaning);
                }
    }

    [Fact]
    public void ListOptions_ListsTheCodeValidatedStringParameters()
    {
        var listed = ListedParameters();
        // Build-aware: SDK-only tools (and their parameters) are absent from a no-SDK build.
        var tools = ListOptionsHandler.RegisteredTools(ToolAssemblies());
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "backend", "format", "scope", "collision" };
        foreach (var row in ListOptionsHandler.HandTable)
        {
            var hit = tools.FirstOrDefault(t => t.Name == row.Tool);
            if (hit.Method is not null && hit.Method.GetParameters().Any(p => p.Name == row.Parameter))
                expected.Add(row.Parameter);
        }
        foreach (var name in expected)
            Assert.Contains(name, listed);
    }

    /// <summary>
    /// Drift guard for string parameters: a string parameter whose description enumerates quoted literals
    /// ('a', 'b' or 'c') is fixed-choice and must be in the output. Parameters that only illustrate
    /// examples are named in the allowlist.
    /// </summary>
    [Fact]
    public void ListOptions_CoversStringParametersWhoseDescriptionEnumeratesValues()
    {
        var listed = ListedParameters();
        var enumerated = new Regex(@"'[A-Za-z_]+'\s*(,|or)\s*'[A-Za-z_]+'", RegexOptions.CultureInvariant);
        // Free-form parameters whose descriptions quote examples or severities, not an input vocabulary.
        var notFixedChoice = new HashSet<string>(StringComparer.Ordinal)
        {
            "logix_list_options.parameter",          // quotes example parameter names
            "logix_cross_reference.tag",             // quotes example tag names
            "logix_find_tags.dataType",              // quotes example type names
            "logix_propose_rung_edit.l5xFragment",   // refers to the target values, which are listed under 'target'
        };

        var missing = new List<string>();
        foreach (var (tool, method) in ListOptionsHandler.RegisteredTools(ToolAssemblies()))
            foreach (var p in method.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (type != typeof(string) || p.Name is null || notFixedChoice.Contains($"{tool}.{p.Name}")) continue;
                var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (desc is null) continue;
                // Description constants live on fields; the attribute already holds the resolved string.
                if (enumerated.IsMatch(desc) && !listed.Contains(p.Name))
                    missing.Add($"{tool}.{p.Name}");
            }
        Assert.True(missing.Count == 0,
            "String parameters whose description lists fixed values but are absent from logix_list_options "
            + "(add them to ListOptionsHandler.HandTable): " + string.Join(", ", missing));
    }

    [Fact]
    public void ListOptions_ParameterArgument_ReturnsExactlyOneEntry()
    {
        foreach (var name in ListedParameters())
        {
            var doc = JsonDocument.Parse(LogixOptionsTools.GetListOptionsResponse(name)).RootElement;
            var options = doc.GetProperty("options");
            Assert.Equal(1, options.GetArrayLength());
            Assert.Equal(name, options[0].GetProperty("parameter").GetString(), ignoreCase: true);
        }
    }

    [Fact]
    public void ListOptions_ParameterArgument_IsCaseInsensitive_AndFormatHasPerToolUsages()
    {
        var doc = JsonDocument.Parse(LogixOptionsTools.GetListOptionsResponse("FORMAT")).RootElement;
        var entry = doc.GetProperty("options")[0];
        Assert.Equal("format", entry.GetProperty("parameter").GetString());
        var tools = entry.GetProperty("usages").EnumerateArray().Select(u => u.GetProperty("tool").GetString()).ToList();
        Assert.Contains("logix_get_rung_diagram", tools);
        Assert.Contains("logix_trace_origin", tools);
    }

    [Fact]
    public void ListOptions_UnknownParameter_ReturnsErrorListingValidNames()
    {
        var doc = JsonDocument.Parse(LogixOptionsTools.GetListOptionsResponse("nope")).RootElement;
        Assert.Equal("UnknownParameter", doc.GetProperty("kind").GetString());
        Assert.False(doc.TryGetProperty("options", out _));
        Assert.Contains("backend", doc.GetProperty("validParameters").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public void ListOptions_IsARegisteredTool_AndNeedsNoArguments()
    {
        var method = typeof(LogixOptionsTools).GetMethod(nameof(LogixOptionsTools.ListOptions))!;
        Assert.Equal("logix_list_options", method.GetCustomAttribute<McpServerToolAttribute>()!.Name);
        Assert.All(method.GetParameters(), p => Assert.True(p.HasDefaultValue));
    }
}
