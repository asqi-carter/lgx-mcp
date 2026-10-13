using System.ComponentModel;
using System.Reflection;

namespace LogixMcp.Tests;

/// <summary>
/// Guards MCP tool descriptions against completeness and keyword drift.
///
/// Rules enforced:
///   (1) Every registered tool must have a non-empty <see cref="DescriptionAttribute"/> string.
///   (2) Every write-capable tool's description must contain the word "gate".
///   (3) The logix_poll tool's description must contain the word "poll".
/// </summary>
public class ToolDescriptionTests
{
    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The assembly that contains all MCP tool types (LogixTools, LogixTagTools,
    /// LogixProjectTools).  The test project references this assembly, so any
    /// concrete tool class can be used to locate it.
    /// </summary>
    private static readonly Assembly ToolAssembly = typeof(LogixTagTools).Assembly;

    /// <summary>
    /// Discovers every public method decorated with <c>[McpServerTool]</c> in
    /// <see cref="ToolAssembly"/>.  The attribute type is resolved by name so
    /// that no additional package reference is required in the test project.
    /// </summary>
    /// <returns>
    /// A list of (method, toolName) pairs, where <c>toolName</c> is the value of
    /// the attribute's <c>Name</c> property (falls back to the method name when
    /// the property is absent or null).
    /// </returns>
    private static List<(MethodInfo Method, string ToolName)> GetAllRegisteredTools()
    {
        return ToolAssembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => new
            {
                Method   = m,
                ToolAttr = m.GetCustomAttributes()
                             .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"),
            })
            .Where(x => x.ToolAttr != null)
            .Select(x =>
            {
                var nameVal = x.ToolAttr!.GetType()
                               .GetProperty("Name")
                               ?.GetValue(x.ToolAttr) as string;
                return (x.Method, ToolName: nameVal ?? x.Method.Name);
            })
            .ToList();
    }

    // ── Write-capable tool registry ───────────────────────────────────────────

    /// <summary>
    /// Tools that can modify PLC state and therefore must document the write-gate
    /// policy in their description.
    /// </summary>
    private static readonly HashSet<string> WriteCapableToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "logix_connect",  // establishes the gate-aware write session
            "logix_write",    // performs gated writes to PLC tags
        };

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// (1) Every registered tool must have a non-empty description string.
    ///     Guards against accidental removal of the <c>[Description]</c> attribute
    ///     or leaving it with a whitespace-only value.
    /// </summary>
    [Fact]
    public void AllRegisteredTools_HaveNonEmptyDescription()
    {
        var tools = GetAllRegisteredTools();

        Assert.NotEmpty(tools);

        foreach (var (method, toolName) in tools)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(
                string.IsNullOrWhiteSpace(description),
                $"Tool '{toolName}' ({method.DeclaringType?.Name}.{method.Name}) " +
                $"has an empty or missing [Description] attribute.");
        }
    }

    /// <summary>
    /// (2) Every write-capable tool's description must contain the word "gate".
    ///     Ensures the write-gate safety concept is surfaced to MCP clients in
    ///     tool metadata and cannot silently disappear through description edits.
    /// </summary>
    [Fact]
    public void WriteCapableTools_DescriptionsContainKeyword_Gate()
    {
        var tools = GetAllRegisteredTools()
            .Where(t => WriteCapableToolNames.Contains(t.ToolName))
            .ToList();

        // Fail fast if any expected write-capable tool is no longer registered.
        Assert.True(
            tools.Count == WriteCapableToolNames.Count,
            $"Expected {WriteCapableToolNames.Count} write-capable tools to be registered " +
            $"({string.Join(", ", WriteCapableToolNames)}), " +
            $"but found {tools.Count}: {string.Join(", ", tools.Select(t => t.ToolName))}.");

        foreach (var (method, toolName) in tools)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            Assert.True(
                description.Contains("gate", StringComparison.OrdinalIgnoreCase),
                $"Write-capable tool '{toolName}' description must contain 'gate' " +
                $"but the current description is: \"{description}\".");
        }
    }

    /// <summary>
    /// (3) The logix_poll tool's description must contain the word "poll".
    ///     Guards against description drift that could misrepresent the
    ///     poll-and-diff nature of the tool to MCP clients.
    /// </summary>
    [Fact]
    public void LogixPollTool_DescriptionContainsKeyword_Poll()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_poll"),
            "Tool 'logix_poll' is not registered — verify the [McpServerTool] attribute " +
            "is present on the PollTags method.");

        var (method, _) = tools["logix_poll"];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            description.Contains("poll", StringComparison.OrdinalIgnoreCase),
            $"Tool 'logix_poll' description must contain 'poll' " +
            $"but the current description is: \"{description}\".");
    }

    /// <summary>
    /// (4) The logix_connect tool's description must state, for the 'l5x' backend,
    ///     that tag values come from the L5X file and do not reflect live controller
    ///     state.  Guards against description drift that would mislead a caller into
    ///     treating offline file reads as evidence about a running system.
    /// </summary>
    [Fact]
    public void LogixConnectTool_L5xBackendDescription_StatesOfflineFileSource()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_connect"),
            "Tool 'logix_connect' is not registered.");

        var (method, _) = tools["logix_connect"];

        // Check the method-level description.
        var methodDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            methodDesc.Contains("l5x", StringComparison.OrdinalIgnoreCase),
            $"logix_connect description must mention the 'l5x' backend but current description is: \"{methodDesc}\".");

        Assert.True(
            (methodDesc.Contains("L5X file", StringComparison.OrdinalIgnoreCase) ||
             methodDesc.Contains("file", StringComparison.OrdinalIgnoreCase)) &&
            (methodDesc.Contains("not reflect live controller", StringComparison.OrdinalIgnoreCase) ||
             methodDesc.Contains("do not reflect live", StringComparison.OrdinalIgnoreCase)),
            $"logix_connect description must state that 'l5x' values come from the L5X file " +
            $"and do not reflect live controller state. Current description: \"{methodDesc}\".");

        // Also check the 'backend' parameter description.
        var backendParam = method.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, "backend", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(backendParam);

        var backendDesc = backendParam!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            backendDesc.Contains("l5x", StringComparison.OrdinalIgnoreCase) &&
            (backendDesc.Contains("file", StringComparison.OrdinalIgnoreCase)) &&
            (backendDesc.Contains("not reflect live controller", StringComparison.OrdinalIgnoreCase) ||
             backendDesc.Contains("do not reflect live", StringComparison.OrdinalIgnoreCase)),
            $"logix_connect 'backend' parameter description must state that 'l5x' values come " +
            $"from the L5X file and do not reflect live controller state. " +
            $"Current description: \"{backendDesc}\".");
    }

#if LOGIX_SDK
    /// <summary>
    /// (5) The logix_connect tool's description must state, for the 'sdk' backend,
    ///     that it requires a reachable Studio 5000 or FactoryTalk Services Platform
    ///     host.  Guards against description drift that would hide the infrastructure
    ///     prerequisite from a caller choosing a backend.
    /// </summary>
    [Fact]
    public void LogixConnectTool_SdkBackendDescription_StatesHostRequirement()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_connect"),
            "Tool 'logix_connect' is not registered.");

        var (method, _) = tools["logix_connect"];

        // Check the method-level description.
        var methodDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            methodDesc.Contains("sdk", StringComparison.OrdinalIgnoreCase),
            $"logix_connect description must mention the 'sdk' backend but current description is: \"{methodDesc}\".");

        Assert.True(
            methodDesc.Contains("Studio 5000", StringComparison.OrdinalIgnoreCase) ||
            methodDesc.Contains("FactoryTalk", StringComparison.OrdinalIgnoreCase),
            $"logix_connect description must state that the 'sdk' backend requires a reachable " +
            $"Studio 5000 or FactoryTalk Services Platform host. " +
            $"Current description: \"{methodDesc}\".");

        // Also check the 'backend' parameter description.
        var backendParam = method.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, "backend", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(backendParam);

        var backendDesc = backendParam!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            backendDesc.Contains("sdk", StringComparison.OrdinalIgnoreCase) &&
            (backendDesc.Contains("Studio 5000", StringComparison.OrdinalIgnoreCase) ||
             backendDesc.Contains("FactoryTalk", StringComparison.OrdinalIgnoreCase)),
            $"logix_connect 'backend' parameter description must state that 'sdk' requires " +
            $"a reachable Studio 5000 or FactoryTalk Services Platform host. " +
            $"Current description: \"{backendDesc}\".");
    }
#endif

    /// <summary>
    /// (6) The logix_read tool's description must note that whether the returned value
    ///     reflects live controller state depends on the backend.  Guards against
    ///     description drift that would make 'l5x' offline reads indistinguishable
    ///     from live CIP reads to a caller inspecting only tool metadata.
    /// </summary>
    [Fact]
    public void LogixReadTool_DescriptionNotes_BackendDependentLiveness()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_read"),
            "Tool 'logix_read' is not registered.");

        var (method, _) = tools["logix_read"];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        Assert.True(
            (description.Contains("l5x", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("backend", StringComparison.OrdinalIgnoreCase)) &&
            (description.Contains("not reflect live controller", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("does not reflect live", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("L5X file", StringComparison.OrdinalIgnoreCase)),
            $"logix_read description must indicate that 'l5x' backend reads come from the " +
            $"L5X file and do not reflect live controller state. " +
            $"Current description: \"{description}\".");
    }

    /// <summary>
    /// (7) The <c>logix_propose_rung_edit</c> tool description must state advisory semantics:
    ///     proposals are advisory, the operator accepts or cancels in Logix Designer,
    ///     nothing executes on the controller until they do, and the gate analysis
    ///     informs the operator's decision rather than substituting for it.
    ///
    /// <para>
    /// This guards against description drift that would mislead an agent into
    /// treating a proposal as a committed change or treating gate analysis as a
    /// sufficient substitute for human operator review.
    /// </para>
    ///
    /// <para>
    /// Note: <c>logix_propose_rung_edit</c> lives in <c>LogixSdkTools</c>
    /// (<c>LogixMcp.SdkAdapter</c> assembly), not the core <c>LogixMcp</c>
    /// assembly, so it is not reached by <see cref="GetAllRegisteredTools"/>.
    /// This test resolves the method directly via <c>typeof(LogixSdkTools)</c>.
    /// </para>
    ///
    /// <para>
    /// <c>LogixSdkTools</c> only exists when the <c>LogixMcp.SdkAdapter</c>
    /// project is built (<c>EnableLogixSdk=true</c>), so this test is guarded
    /// with <c>#if LOGIX_SDK</c> to keep the non-SDK build compiling.
    /// </para>
    /// </summary>
#if LOGIX_SDK
    [Fact]
    public void LogixProposeRungEditTool_DescriptionStates_AdvisorySemantics()
    {
        // Resolve the ProposeRungEdit method directly from LogixSdkTools, which lives in the
        // LogixMcp.SdkAdapter assembly (not scanned by GetAllRegisteredTools which is scoped
        // to the core LogixMcp assembly).
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ProposeRungEdit));
        Assert.NotNull(method);

        // Confirm the [McpServerTool] attribute is present with the correct tool name.
        var toolAttr = method!.GetCustomAttributes(inherit: false)
            .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
        Assert.NotNull(toolAttr);

        var toolName = toolAttr!.GetType().GetProperty("Name")?.GetValue(toolAttr) as string;
        Assert.Equal("logix_propose_rung_edit", toolName);

        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // (a) Must state the proposal is advisory.
        Assert.True(
            description.Contains("advisory", StringComparison.OrdinalIgnoreCase),
            $"logix_propose_rung_edit description must contain 'advisory' to make clear " +
            $"proposals are not commands. Current description: \"{description}\".");

        // (b) Must reference operator action (accepts or cancels).
        Assert.True(
            description.Contains("operator", StringComparison.OrdinalIgnoreCase) &&
            (description.Contains("accepts", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("cancels", StringComparison.OrdinalIgnoreCase)),
            $"logix_propose_rung_edit description must state that the operator accepts or " +
            $"cancels the proposal in Logix Designer. Current description: \"{description}\".");

        // (c) Must state that nothing executes until the operator acts.
        Assert.True(
            description.Contains("executes", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("execute", StringComparison.OrdinalIgnoreCase),
            $"logix_propose_rung_edit description must state that nothing executes on the " +
            $"controller until the operator acts. Current description: \"{description}\".");

        // (d) Must state that gate analysis informs the operator's decision, not substitutes for it.
        Assert.True(
            description.Contains("inform", StringComparison.OrdinalIgnoreCase) &&
            description.Contains("substitute", StringComparison.OrdinalIgnoreCase),
            $"logix_propose_rung_edit description must state that gate analysis informs the " +
            $"operator's decision and does not substitute for it. " +
            $"Current description: \"{description}\".");
    }
#endif

    /// <summary>
    /// (7b) The <c>logix_propose_rung_edit</c> tool description must NOT
    ///      claim that tags declared by a pending publish are unreadable until the edit is
    ///      accepted, and must NOT reference TagNotFound as the observed return value for
    ///      pending tags.  Instead it must state that tags are created immediately and
    ///      permanently on the controller at publish time, and that this session may not
    ///      write them (CreatedByThisSession).
    ///
    /// <para>
    /// The old wording was stale: hardware verification (2026-08-04) showed that tags are
    /// created on the controller as soon as <c>target='controller'</c> publish returns, and
    /// cancelling the pending rung edit does not remove them.  Keeping the false claim in
    /// the tool description would mislead agents into avoiding reads of newly-created tags.
    /// </para>
    /// </summary>
#if LOGIX_SDK
    [Fact]
    public void ProposeRungEdit_DoesNotClaimPendingTagsUnreadable()
    {
        var method = typeof(LogixSdkTools).GetMethod(nameof(LogixSdkTools.ProposeRungEdit));
        Assert.NotNull(method);

        var description = method!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // (a) Old stale language must be absent.
        Assert.False(
            description.Contains("NOT readable until", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not readable until", StringComparison.OrdinalIgnoreCase),
            "logix_propose_rung_edit description must not claim pending tags are NOT readable " +
            $"until accepted. Current description: \"{description}\".");

        Assert.False(
            description.Contains("TagNotFound", StringComparison.OrdinalIgnoreCase),
            "logix_propose_rung_edit description must not reference 'TagNotFound' as an " +
            $"observed return for pending tags. Current description: \"{description}\".");

        // (b) New accurate language must be present.
        Assert.True(
            description.Contains("immediately", StringComparison.OrdinalIgnoreCase) &&
            description.Contains("permanently", StringComparison.OrdinalIgnoreCase),
            "logix_propose_rung_edit description must state that tags are created immediately " +
            $"and permanently on the controller. Current description: \"{description}\".");

        Assert.True(
            description.Contains("CreatedByThisSession", StringComparison.Ordinal),
            "logix_propose_rung_edit description must mention 'CreatedByThisSession' to convey " +
            $"the write-gate status of newly created tags. Current description: \"{description}\".");
    }
#endif

    /// <summary>
    /// (8) Neither <c>logix_connect</c> nor <c>logix_write</c> may expose any
    ///     caller-supplied prefix/classification parameter (e.g. <c>commandPrefixes</c>,
    ///     <c>safetyPrefixes</c>, or any parameter whose name contains the word
    ///     "prefix").
    ///
    /// <para>
    /// The write-gate policy in these tools is derived exclusively from the open L5X
    /// project (Cmd_*/Sim_* tag-name designations and UDT/scalar 'range N..M'
    /// annotations) and must never be caller-configurable.  Exposing a prefix
    /// parameter would allow a caller to re-classify tags and silently bypass the
    /// enforcement path.  This test is a permanent guard against that regression.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixConnectAndLogixWrite_ExposeNoPrefixParameters()
    {
        // Parameter names that must never appear on logix_connect or logix_write.
        // The check is twofold:
        //   (a) any parameter whose name contains "prefix" (catches future variants
        //       such as "commandPrefix", "tagPrefixes", "allowedPrefixes", etc.), AND
        //   (b) a concrete deny-list of the exact names the epic's threat model named.
        var forbiddenExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "commandPrefixes",
            "safetyPrefixes",
        };

        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        // Ensure both tools are still registered — fail fast if the tool was renamed.
        Assert.True(
            tools.ContainsKey("logix_connect"),
            "Tool 'logix_connect' is not registered — verify the [McpServerTool] attribute " +
            "is present on the Connect method.");

        Assert.True(
            tools.ContainsKey("logix_write"),
            "Tool 'logix_write' is not registered — verify the [McpServerTool] attribute " +
            "is present on the WriteTag method.");

        foreach (var toolName in new[] { "logix_connect", "logix_write" })
        {
            var (method, _) = tools[toolName];

            // Skip injected MCP framework parameters (RequestContext<...>) that are
            // not part of the callable schema exposed to MCP clients.
            var callerParams = method.GetParameters()
                .Where(p => !p.ParameterType.Name.StartsWith("RequestContext",
                                StringComparison.OrdinalIgnoreCase) &&
                            !p.ParameterType.Name.StartsWith("CancellationToken",
                                StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var param in callerParams)
            {
                var name = param.Name ?? "";

                // (a) Name contains the word "prefix".
                Assert.False(
                    name.Contains("prefix", StringComparison.OrdinalIgnoreCase),
                    $"Tool '{toolName}' parameter '{name}' contains 'prefix' — " +
                    $"caller-supplied prefix/classification parameters are forbidden on " +
                    $"enforcing paths. The write-gate policy must come from the L5X project, " +
                    $"not from the caller.");

                // (b) Exact deny-list match.
                Assert.False(
                    forbiddenExact.Contains(name),
                    $"Tool '{toolName}' parameter '{name}' is in the explicit deny-list of " +
                    $"caller-controlled classification parameters " +
                    $"({string.Join(", ", forbiddenExact)}). " +
                    $"These must never appear on enforcing paths.");
            }
        }
    }

    /// <summary>
    /// (8b) The <c>gateway</c> and <c>path</c> parameters
    ///      on <c>logix_connect</c> must be optional (default to <c>""</c>), not
    ///      non-defaulted required strings.
    ///
    /// <para>
    /// Prior to this fix, <c>gateway</c> and <c>path</c> were plain <c>string</c>
    /// parameters with no default value. The MCP argument marshaller treats a
    /// parameter with no default as required, so a call that supplied only
    /// <c>sdkCommPath</c> (the canonical <c>backend="sdk" sdkMode="online"</c>
    /// invocation) never reached the handler — it failed at the marshaller with a
    /// generic "missing required parameter" exception before any structured error
    /// could be produced. Reflection over the parameter's <see cref="ParameterInfo.HasDefaultValue"/>
    /// is a direct proxy for whether the MCP SDK will mark the parameter "required" in the
    /// tool's generated JSON schema.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixConnect_GatewayAndPath_AreOptionalWithEmptyStringDefault()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_connect"),
            "Tool 'logix_connect' is not registered — verify the [McpServerTool] attribute " +
            "is present on the Connect method.");

        var (method, _) = tools["logix_connect"];

        foreach (var paramName in new[] { "gateway", "path" })
        {
            var param = method.GetParameters()
                .FirstOrDefault(p => string.Equals(p.Name, paramName, StringComparison.Ordinal));

            Assert.True(param is not null,
                $"logix_connect must declare a '{paramName}' parameter.");

            Assert.True(
                param!.HasDefaultValue,
                $"logix_connect parameter '{paramName}' must have a default value so the MCP " +
                $"marshaller (and the tool's generated JSON schema) treat it as optional, not " +
                $"required. Without a default, calls that omit '{paramName}' (e.g. the " +
                $"canonical backend='sdk' sdkMode='online' sdkCommPath=... call) fail at the " +
                $"marshaller instead of reaching the handler's structured-error branches.");

            Assert.Equal("", param.DefaultValue);
        }
    }

#if LOGIX_SDK
    /// <summary>
    /// (8c) The <c>gateway</c> and <c>path</c> parameter descriptions on
    ///      <c>logix_connect</c> must clearly document (1) that both are optional, (2) that
    ///      <c>gateway</c> is required only for the <c>'cip'</c> backend, (3) that <c>path</c>
    ///      is never accepted by the Logix Designer SDK and is used only in the CIP context,
    ///      and (4) that SDK online callers should supply <c>sdkCommPath</c> instead.
    ///
    /// <para>
    /// The descriptions must not read as an instruction to pass <c>gateway</c> once
    /// <c>sdkCommPath</c> has already been supplied for an SDK online call — that ambiguity is
    /// exactly what was flagged as leading an agent astray. This guards the wording so a
    /// future edit cannot silently reintroduce that implication.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixConnect_GatewayAndPathDescriptions_DocumentOptionalityAndCorrectUsage()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_connect"),
            "Tool 'logix_connect' is not registered.");

        var (method, _) = tools["logix_connect"];

        var gatewayParam = method.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, "gateway", StringComparison.Ordinal));
        var pathParam = method.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, "path", StringComparison.Ordinal));

        Assert.NotNull(gatewayParam);
        Assert.NotNull(pathParam);

        var gatewayDesc = gatewayParam!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var pathDesc    = pathParam!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // (1) Both parameters must state they are optional.
        Assert.True(
            gatewayDesc.Contains("Optional", StringComparison.OrdinalIgnoreCase),
            $"'gateway' description must state it is optional. Current: \"{gatewayDesc}\".");
        Assert.True(
            pathDesc.Contains("Optional", StringComparison.OrdinalIgnoreCase),
            $"'path' description must state it is optional. Current: \"{pathDesc}\".");

        // (2) gateway is required only for the 'cip' backend.
        Assert.True(
            gatewayDesc.Contains("cip", StringComparison.OrdinalIgnoreCase) &&
            gatewayDesc.Contains("required", StringComparison.OrdinalIgnoreCase),
            $"'gateway' description must state it is required for the 'cip' backend. " +
            $"Current: \"{gatewayDesc}\".");
        Assert.True(
            gatewayDesc.Contains("NOT required", StringComparison.OrdinalIgnoreCase) ||
            gatewayDesc.Contains("Not required", StringComparison.OrdinalIgnoreCase),
            $"'gateway' description must state it is not required for SDK online connections. " +
            $"Current: \"{gatewayDesc}\".");

        // (3) path is never accepted by the Logix Designer SDK and is used only in the CIP context.
        Assert.True(
            pathDesc.Contains("ONLY", StringComparison.OrdinalIgnoreCase) &&
            pathDesc.Contains("cip", StringComparison.OrdinalIgnoreCase),
            $"'path' description must state it is used only by the 'cip' backend. " +
            $"Current: \"{pathDesc}\".");
        Assert.True(
            pathDesc.Contains("NOT accepted by the Logix Designer SDK", StringComparison.OrdinalIgnoreCase),
            $"'path' description must state it is not accepted by the Logix Designer SDK. " +
            $"Current: \"{pathDesc}\".");

        // (4) Both descriptions must point SDK online callers at sdkCommPath instead.
        Assert.True(
            gatewayDesc.Contains("sdkCommPath instead", StringComparison.Ordinal),
            $"'gateway' description must direct SDK online callers to sdkCommPath instead. " +
            $"Current: \"{gatewayDesc}\".");
        Assert.True(
            pathDesc.Contains("sdkCommPath instead", StringComparison.Ordinal),
            $"'path' description must direct SDK online callers to sdkCommPath instead. " +
            $"Current: \"{pathDesc}\".");

        // Anti-pattern guard: must not instruct passing gateway alongside an sdkCommPath call.
        Assert.True(
            gatewayDesc.Contains("do not need to also pass gateway", StringComparison.OrdinalIgnoreCase) ||
            gatewayDesc.Contains("do not need to pass gateway", StringComparison.OrdinalIgnoreCase),
            $"'gateway' description must not imply an agent should pass gateway once sdkCommPath " +
            $"has already been supplied for an SDK online call. Current: \"{gatewayDesc}\".");
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// (9) logix_get_routine, logix_project_summary, logix_find_tags,
    ///     logix_cross_reference, and logix_gate_report must each state in their
    ///     description that they read the opened or uploaded project file — not the
    ///     live controller — and must point callers at logix_export_rungs for the
    ///     live routine content including pending edits.
    ///
    /// <para>
    /// These five tools operate exclusively over the static L5X parsed by
    /// logix_open_project. Without an explicit statement in tool metadata, an agent
    /// could mistake their output for a live-controller read, or fail to notice that
    /// a proposed-but-not-yet-accepted rung edit is invisible to them.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("logix_get_routine")]
    [InlineData("logix_project_summary")]
    [InlineData("logix_find_tags")]
    [InlineData("logix_cross_reference")]
    [InlineData("logix_gate_report")]
    public void ProjectFileReadOnlyTools_DescriptionStatesProjectFileSourceAndExportRungsPointer(
        string toolName)
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey(toolName),
            $"Tool '{toolName}' is not registered.");

        var (method, _) = tools[toolName];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // (a) Must state that the source is the opened/uploaded project file.
        Assert.True(
            description.Contains("project file", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("project-file", StringComparison.OrdinalIgnoreCase),
            $"Tool '{toolName}' description must state that it reads the opened or uploaded " +
            $"project file. Current description: \"{description}\".");

        // (b) Must state that the live controller is never read by this tool.
        Assert.True(
            description.Contains("never the live controller", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not the controller", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not the live controller", StringComparison.OrdinalIgnoreCase),
            $"Tool '{toolName}' description must explicitly state it does not read the live " +
            $"controller. Current description: \"{description}\".");

        // (c) Must point agents at logix_export_rungs for the live routine including
        //     pending edits.
        Assert.True(
            description.Contains("logix_export_rungs", StringComparison.OrdinalIgnoreCase),
            $"Tool '{toolName}' description must point agents at logix_export_rungs for the " +
            $"live routine content including pending edits. " +
            $"Current description: \"{description}\".");
    }
#endif

    /// <summary>
    /// (10) Both logix_list_datatypes and logix_import_datatype must
    ///      state in their descriptions that a UDT currently in use by the controller
    ///      cannot be changed online — offline ACD edit and download is required.
    ///
    /// <para>
    /// The Logix Designer SDK refuses to modify a type definition while the type is
    /// in use online. This constraint must be surfaced in tool metadata so agents
    /// know to direct users to the correct offline workflow instead of repeatedly
    /// attempting online edits.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("logix_list_datatypes")]
    [InlineData("logix_import_datatype")]
    public void DatatypeTools_DescriptionStates_UdtCannotBeChangedOnline(string toolName)
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey(toolName),
            $"Tool '{toolName}' is not registered.");

        var (method, _) = tools[toolName];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // Must state that a UDT in use cannot be changed online.
        Assert.True(
            description.Contains("cannot be changed online", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("cannot be modified online", StringComparison.OrdinalIgnoreCase),
            $"Tool '{toolName}' description must state that a UDT in use cannot be changed " +
            $"online. Current description: \"{description}\".");

        // Must state that offline edit + download is the required workaround.
        Assert.True(
            description.Contains("offline", StringComparison.OrdinalIgnoreCase) &&
            (description.Contains("download", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("edit the ACD", StringComparison.OrdinalIgnoreCase)),
            $"Tool '{toolName}' description must state that offline ACD edit and download is " +
            $"required to change a UDT. Current description: \"{description}\".");
    }

#if LOGIX_SDK
    /// <summary>
    /// (11) The logix_import_datatype tool description must distinguish
    ///      ImportCollisionOption (used by this tool for collision handling) from
    ///      PartialImportOption (used by the rung-edit tools).
    ///
    /// <para>
    /// These are two separate SDK enums that govern entirely different operations.
    /// Confusing them leads to callers supplying wrong enum values or misunderstanding
    /// the tool's collision semantics. The description must name both enums explicitly
    /// and state that they must not be confused.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixImportDatatype_DescriptionDistinguishes_ImportCollisionOptionFromPartialImportOption()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_import_datatype"),
            "Tool 'logix_import_datatype' is not registered.");

        var (method, _) = tools["logix_import_datatype"];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // Must name ImportCollisionOption explicitly.
        Assert.True(
            description.Contains("ImportCollisionOption", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must name 'ImportCollisionOption' to identify " +
            $"the collision enum used by this tool. Current description: \"{description}\".");

        // Must name PartialImportOption explicitly.
        Assert.True(
            description.Contains("PartialImportOption", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must name 'PartialImportOption' to distinguish " +
            $"it from ImportCollisionOption. Current description: \"{description}\".");

        // Must state they are different / must not be confused.
        Assert.True(
            description.Contains("different", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("do not confuse", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("not confuse", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must state that ImportCollisionOption and " +
            $"PartialImportOption are different enums and must not be confused. " +
            $"Current description: \"{description}\".");
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// (12) The logix_import_datatype tool description must document
    ///      that PartialExportToXmlFileAsync takes XPath as the first argument and
    ///      file path as the second, and that swapping them produces
    ///      "Invalid XPath syntax: &lt;path&gt;".
    ///
    /// <para>
    /// This argument-ordering constraint caused the first bench failure on the
    /// datatype import workflow. Surfacing it in the tool description prevents
    /// agents from repeating the mistake when constructing SDK calls for the
    /// export-then-import pattern.
    /// </para>
    /// </summary>
    [Fact]
    public void LogixImportDatatype_DescriptionDocuments_PartialExportXPathFirstArgOrder()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_import_datatype"),
            "Tool 'logix_import_datatype' is not registered.");

        var (method, _) = tools["logix_import_datatype"];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // Must mention PartialExportToXmlFileAsync by name.
        Assert.True(
            description.Contains("PartialExportToXmlFileAsync", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("partial_export_to_xml_file", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention 'PartialExportToXmlFileAsync' " +
            $"(the SDK method). Current description: \"{description}\".");

        // Must state XPath is the first argument and file path is the second.
        Assert.True(
            description.Contains("XPath", StringComparison.OrdinalIgnoreCase) &&
            (description.Contains("first argument", StringComparison.OrdinalIgnoreCase) ||
             description.Contains("FIRST argument", StringComparison.OrdinalIgnoreCase)),
            $"logix_import_datatype description must state that XPath is the first argument to " +
            $"PartialExportToXmlFileAsync. Current description: \"{description}\".");

        // Must state that swapping them produces "Invalid XPath syntax".
        Assert.True(
            description.Contains("Invalid XPath syntax", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("invalid XPath", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must state that swapping XPath and file path " +
            $"produces 'Invalid XPath syntax: <path>'. Current description: \"{description}\".");
    }
#endif

#if LOGIX_SDK
    /// <summary>
    /// (13) The logix_import_datatype tool description must document the
    ///      real-run (dryRun=false, confirm=true) steps: copy, import, save, re-export, and
    ///      conditional build.  It must also state that downloading the updated project to the
    ///      controller is a separate operator step not performed by this tool.
    ///
    /// <para>
    /// The previous description only hinted at these steps; this test makes the coverage
    /// contractual so an agent reading tool metadata knows exactly what a confirmed real run
    /// does and what it does not do (controller download).
    /// </para>
    /// </summary>
    [Fact]
    public void LogixImportDatatype_DescriptionDocuments_RealRunStepsAndDownloadIsOperatorStep()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_import_datatype"),
            "Tool 'logix_import_datatype' is not registered.");

        var (method, _) = tools["logix_import_datatype"];
        var description  = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // Must mention the copy step (input ACD is copied to saveAs).
        Assert.True(
            description.Contains("copies", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("copy", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention the copy step (copying acdPath to saveAs). " +
            $"Current description: \"{description}\".");

        // Must mention the import step via PartialImportFromXmlFileAsync.
        Assert.True(
            description.Contains("PartialImportFromXmlFileAsync", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("import", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention the import step " +
            $"(PartialImportFromXmlFileAsync or import). " +
            $"Current description: \"{description}\".");

        // Must mention the save step.
        Assert.True(
            description.Contains("saves", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("save", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention the save step (saving the updated project). " +
            $"Current description: \"{description}\".");

        // Must mention the re-export step (member-for-member verification).
        Assert.True(
            description.Contains("re-exports", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("re-export", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention the re-export step " +
            $"(re-exporting for member-for-member verification). " +
            $"Current description: \"{description}\".");

        // Must mention the conditional build step.
        Assert.True(
            description.Contains("builds", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("build", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must mention the conditional build step. " +
            $"Current description: \"{description}\".");

        // Must state that controller download is a separate operator step.
        Assert.True(
            (description.Contains("separate", StringComparison.OrdinalIgnoreCase) &&
             description.Contains("operator step", StringComparison.OrdinalIgnoreCase)) ||
            description.Contains("not performed by this tool", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype description must state that downloading the updated project " +
            $"to the controller is a separate operator step not performed by this tool. " +
            $"Current description: \"{description}\".");
    }
#endif

    /// <summary>
    /// (14) The logix_import_datatype 'confirm' parameter description must
    ///      document its gate behavior: when dryRun=false and confirm is not true, the tool
    ///      returns the dry-run delta plus a 'refused' field without writing any file.
    /// </summary>
    [Fact]
    public void LogixImportDatatype_ConfirmParameter_DescribesGateBehavior()
    {
        var tools = GetAllRegisteredTools()
            .ToDictionary(t => t.ToolName, StringComparer.OrdinalIgnoreCase);

        Assert.True(
            tools.ContainsKey("logix_import_datatype"),
            "Tool 'logix_import_datatype' is not registered.");

        var (method, _) = tools["logix_import_datatype"];

        var confirmParam = method.GetParameters()
            .FirstOrDefault(p => string.Equals(p.Name, "confirm", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(confirmParam);

        var confirmDesc = confirmParam!.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

        // Must state that confirm must be true for a real import.
        Assert.True(
            confirmDesc.Contains("true", StringComparison.OrdinalIgnoreCase) &&
            (confirmDesc.Contains("dryRun=false", StringComparison.OrdinalIgnoreCase) ||
             confirmDesc.Contains("real import", StringComparison.OrdinalIgnoreCase)),
            $"logix_import_datatype 'confirm' parameter description must state that confirm=true " +
            $"is required for a real import (dryRun=false). Current description: \"{confirmDesc}\".");

        // Must state what happens when confirm is false (gate behavior: refused, delta returned).
        Assert.True(
            confirmDesc.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            confirmDesc.Contains("delta", StringComparison.OrdinalIgnoreCase) ||
            confirmDesc.Contains("review", StringComparison.OrdinalIgnoreCase),
            $"logix_import_datatype 'confirm' parameter description must document the gate " +
            $"behavior when confirm is omitted or false (returns refused field / dry-run delta). " +
            $"Current description: \"{confirmDesc}\".");
    }
}
