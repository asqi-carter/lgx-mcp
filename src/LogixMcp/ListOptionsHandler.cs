using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;

/// <summary>One legal value of a fixed-choice parameter, with a one-line meaning.</summary>
/// <param name="Value">The literal value to pass.</param>
/// <param name="Meaning">One line saying what the value does.</param>
public sealed record OptionValue(
    [property: JsonPropertyName("value")]   string Value,
    [property: JsonPropertyName("meaning")] string Meaning);

/// <summary>The legal values of one parameter on one tool.</summary>
/// <param name="Tool">Registered tool name, e.g. <c>logix_connect</c>.</param>
/// <param name="Default">Value used when the parameter is omitted; omitted from JSON when there is none.</param>
/// <param name="Source"><c>schema</c> (enum in the tool's input schema) or <c>validated-in-code</c> (hand table).</param>
/// <param name="Values">Legal values, each with a meaning.</param>
public sealed record OptionUsage(
    [property: JsonPropertyName("tool")]    string Tool,
    [property: JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Default,
    [property: JsonPropertyName("source")]  string Source,
    [property: JsonPropertyName("values")]  IReadOnlyList<OptionValue> Values);

/// <summary>One fixed-choice parameter and where it is used.</summary>
/// <param name="Parameter">Parameter name as it appears in the tool input, e.g. <c>format</c>.</param>
/// <param name="Usages">One entry per tool that takes the parameter; the legal values can differ per tool.</param>
public sealed record OptionEntry(
    [property: JsonPropertyName("parameter")] string Parameter,
    [property: JsonPropertyName("usages")]    IReadOnlyList<OptionUsage> Usages);

/// <summary>Response of <c>logix_list_options</c>.</summary>
/// <param name="Options">Every fixed-choice parameter (sorted by name), or exactly one when a parameter was requested.</param>
public sealed record ListOptionsResponse(
    [property: JsonPropertyName("options")] IReadOnlyList<OptionEntry> Options);

/// <summary>Error returned when the requested parameter is not a fixed-choice parameter.</summary>
/// <param name="Kind">Always <c>UnknownParameter</c>.</param>
/// <param name="Error">Human-readable message.</param>
/// <param name="RequestedParameter">The name that was supplied.</param>
/// <param name="ValidParameters">Every parameter name that can be requested.</param>
public sealed record UnknownOptionParameterResponse(
    [property: JsonPropertyName("kind")]               string Kind,
    [property: JsonPropertyName("error")]              string Error,
    [property: JsonPropertyName("requestedParameter")] string RequestedParameter,
    [property: JsonPropertyName("validParameters")]    IReadOnlyList<string> ValidParameters);

/// <summary>
/// Pure handler behind <c>logix_list_options</c>. Enum-typed parameters are discovered by reflecting over the
/// registered <c>[McpServerTool]</c> methods (the same methods the MCP schema is generated from), so a new enum
/// parameter appears here with no edit. String parameters whose legal values are checked in code have no schema
/// enum; they come from <see cref="HandTable"/>, which is filtered to the tools actually registered in this build.
/// </summary>
public static class ListOptionsHandler
{
    private const string SchemaSource = "schema";
    private const string CodeSource   = "validated-in-code";

    /// <summary>Hand-table row: a string parameter whose legal values are validated in code.</summary>
    internal sealed record HandRow(string Tool, string Parameter, string? Default, (string Value, string Meaning)[] Values);

    /// <summary>
    /// Code-validated string parameters. Each row must match a real tool parameter (guarded by tests); rows for
    /// tools that this build does not register are dropped at runtime.
    /// </summary>
    internal static readonly IReadOnlyList<HandRow> HandTable =
    [
        new("logix_connect", "backend", "cip",
        [
            ("cip",     "Live EtherNet/IP connection to the controller (libplctag); needs gateway."),
            ("l5x",     "In-memory session over the opened L5X project; no controller contact, nothing written to disk."),
            ("sdk",     "Logix Designer SDK session (SDK build only); see sdkMode."),
            ("virtual", "Alias of useVirtual=true: in-memory store seeded from the open project."),
        ]),
        new("logix_connect", "sdkMode", "offline",
        [
            ("offline", "SDK works on the project file only; no network and no controller."),
            ("online",  "SDK talks to the live controller; supply sdkCommPath or gateway."),
        ]),
        new("logix_get_rung_diagram", "format", "text",
        [
            ("text",  "Box-drawing text diagram between power rails."),
            ("svg",   "SVG drawing only (currentColor, theme-neutral)."),
            ("both",  "Both the text diagram and the SVG."),
        ]),
        new("logix_trace_origin", "format", "text",
        [
            ("text",    "Indented tree of the origin trace."),
            ("json",    "Nodes, edges, externalInputs, literals, gaps and cycles."),
            ("mermaid", "Mermaid flowchart, one box per writing rung."),
            ("all",     "Text, JSON and Mermaid together."),
        ]),
        new("logix_document", "scope", "controller",
        [
            ("controller", "Whole controller (default); inferred from program/routine when omitted."),
            ("program",    "One program; requires program."),
            ("routine",    "One routine; requires program and routine."),
        ]),
        new("logix_controller_mode", "mode", null,
        [
            ("Program", "Request Program mode (case-insensitive)."),
            ("Run",     "Request Run mode (case-insensitive)."),
            ("Test",    "Request Test mode (case-insensitive)."),
        ]),
        new("logix_build", "target", "default",
        [
            ("default",  "The SDK chooses: the previous build's target, or a physical controller on a first build."),
            ("physical", "Build for a physical controller."),
            ("echo",     "Build for the Echo emulated controller."),
        ]),
        new("logix_propose_rung_edit", "target", "workstation",
        [
            ("workstation", "Local-only proposal held in the server's in-memory image (default; null means this)."),
            ("controller",  "Publish accepted-but-not-assembled edits to the controller (pending zone); gated by LGX_PUBLISH_MODE."),
            ("live",        "Publish and finalize in one call; needs LGX_ENABLE_LIVE_WRITES, confirmedByUser=true, LGX_ENABLE_LIVE_FINALIZE=1."),
        ]),
    ];

    /// <summary>One-line meanings for enum values, keyed by enum type name then value name.</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EnumMeanings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [nameof(ImportCollisionOption)] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(ImportCollisionOption.Overwrite)] = "Replace the existing type with the imported one (default).",
                [nameof(ImportCollisionOption.Discard)]   = "Keep the existing type and skip the import.",
                [nameof(ImportCollisionOption.Cancel)]    = "Abort with an error if the type already exists.",
            },
        };

    /// <summary>The assemblies that carry registered tools in this process (core, plus the SDK adapter when loaded).</summary>
    public static IReadOnlyList<Assembly> DefaultToolAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a == typeof(ListOptionsHandler).Assembly
                        || string.Equals(a.GetName().Name, "LogixMcp.SdkAdapter", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Returns every fixed-choice parameter, or exactly one entry when <paramref name="parameter"/> is given.
    /// </summary>
    /// <param name="parameter">Optional parameter name (case-insensitive), e.g. <c>format</c>.</param>
    /// <param name="toolAssemblies">Assemblies to scan for registered tools; defaults to <see cref="DefaultToolAssemblies"/>.</param>
    /// <returns>A <see cref="ListOptionsResponse"/>, or an <see cref="UnknownOptionParameterResponse"/> for an unknown name.</returns>
    public static object Execute(string? parameter = null, IEnumerable<Assembly>? toolAssemblies = null)
    {
        var all = Build(toolAssemblies ?? DefaultToolAssemblies());
        if (string.IsNullOrWhiteSpace(parameter))
            return new ListOptionsResponse(all);

        var wanted = parameter.Trim();
        var hit = all.FirstOrDefault(e => string.Equals(e.Parameter, wanted, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            return new UnknownOptionParameterResponse(
                "UnknownParameter",
                $"'{wanted}' is not a fixed-choice parameter. Call logix_list_options with no argument to list them.",
                wanted,
                all.Select(e => e.Parameter).ToList());
        return new ListOptionsResponse([hit]);
    }

    /// <summary>Builds the full option list: schema enums merged with the hand table, sorted by parameter name.</summary>
    internal static IReadOnlyList<OptionEntry> Build(IEnumerable<Assembly> toolAssemblies)
    {
        var tools = RegisteredTools(toolAssemblies);
        var byParam = new Dictionary<string, List<OptionUsage>>(StringComparer.OrdinalIgnoreCase);

        void Add(string parameter, OptionUsage usage)
        {
            if (!byParam.TryGetValue(parameter, out var list))
                byParam[parameter] = list = [];
            list.Add(usage);
        }

        foreach (var (toolName, method) in tools)
        {
            foreach (var p in method.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (!type.IsEnum || p.Name is null) continue;

                EnumMeanings.TryGetValue(type.Name, out var meanings);
                var values = Enum.GetNames(type)
                    .Select(n => new OptionValue(
                        n,
                        meanings is not null && meanings.TryGetValue(n, out var m)
                            ? m
                            : $"See the '{p.Name}' description of {toolName}."))
                    .ToList();
                var def = p.HasDefaultValue && p.DefaultValue is not null ? p.DefaultValue.ToString() : null;
                Add(p.Name, new OptionUsage(toolName, def, SchemaSource, values));
            }
        }

        var registered = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in HandTable)
        {
            if (!registered.Contains(row.Tool)) continue;
            // Only advertise a parameter the registered tool really declares in this build.
            var method = tools.First(t => t.Name == row.Tool).Method;
            if (!method.GetParameters().Any(p => string.Equals(p.Name, row.Parameter, StringComparison.Ordinal)))
                continue;
            Add(row.Parameter, new OptionUsage(
                row.Tool, row.Default, CodeSource,
                row.Values.Select(v => new OptionValue(v.Value, v.Meaning)).ToList()));
        }

        return byParam
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new OptionEntry(
                kv.Key,
                kv.Value.OrderBy(u => u.Tool, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>Every public <c>[McpServerTool]</c> method in the given assemblies, with its tool name.</summary>
    internal static IReadOnlyList<(string Name, MethodInfo Method)> RegisteredTools(IEnumerable<Assembly> assemblies)
    {
        var result = new List<(string, MethodInfo)>();
        foreach (var asm in assemblies.Distinct())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var m in types.SelectMany(t => t.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            {
                var attr = m.GetCustomAttributes()
                            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr is null) continue;
                var name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string ?? m.Name;
                result.Add((name, m));
            }
        }
        return result;
    }
}
