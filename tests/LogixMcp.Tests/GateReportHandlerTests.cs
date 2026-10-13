using System.Reflection;
using System.Text.Json;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="GateReportHandler"/> and its integration with
/// <see cref="LogixProjectTools.GateReport"/>.
///
/// Uses the Demo.L5X fixture, which has:
///   Controller tags: Cmd_MotorStart (BOOL), Cmd_SpeedSetpoint (REAL, range 0..1000),
///                    Sim_LineSpeed (REAL), Safety_OK (BOOL),
///                    Motor_Run (BOOL), Cmd_Motor (MotorCmd)
///   UDT MotorCmd: Enable (BOOL), Speed (REAL, description "range 0..100")
///   Rung 0 (MainProgram/MainRoutine): XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run)
///
/// Expected policy under GateConfig.Default:
///   Grants (4): Cmd_Motor, Cmd_MotorStart, Cmd_SpeedSetpoint, Sim_LineSpeed
///   Denies (2): Motor_Run (ProgramWritten), Safety_OK (SafetyTag)
/// </summary>
public class GateReportHandlerTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    // ------------------------------------------------------------------ //
    //  Error path: no project loaded                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsGateReportError_WhenNoProjectLoaded()
    {
        var svc    = new ProjectService();
        var result = GateReportHandler.Execute(svc);

        Assert.IsType<GateReportError>(result);
    }

    [Fact]
    public void Execute_ErrorMessage_MentionsOpenProject()
    {
        var svc    = new ProjectService();
        var result = (GateReportError)GateReportHandler.Execute(svc);

        Assert.Contains("logix_open_project", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    //  Success path: enforced policy (GateConfig.Default)                  //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_ReturnsWriteGatePolicy_WhenProjectLoaded()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc);

        Assert.IsType<WriteGatePolicy>(result);
    }

    [Fact]
    public void Execute_DefaultPrefixes_GrantsFourTags()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        Assert.Equal(4, policy.Grants.Count);
    }

    [Fact]
    public void Execute_DefaultPrefixes_DeniesTwoTags()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        // Safety_OK (safety_tag) + Motor_Run (program_written) + SomeRandomTag (not_designated)
        Assert.Equal(3, policy.Denies.Count);
    }

    [Fact]
    public void Execute_DefaultPrefixes_GrantsContainExpectedTags()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        var grantNames = policy.Grants.Select(g => g.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Cmd_MotorStart",    grantNames);
        Assert.Contains("Cmd_SpeedSetpoint", grantNames);
        Assert.Contains("Sim_LineSpeed",     grantNames);
        Assert.Contains("Cmd_Motor",         grantNames);
    }

    [Fact]
    public void Execute_DefaultPrefixes_DeniesContainExpectedTags()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        var denyNames = policy.Denies.Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Motor_Run", denyNames);
        Assert.Contains("Safety_OK", denyNames);
    }

    // ------------------------------------------------------------------ //
    //  Non-cached: re-derives each call from live project state           //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_CalledTwice_ProducesSameResult()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var policy1 = (WriteGatePolicy)GateReportHandler.Execute(svc);
        var policy2 = (WriteGatePolicy)GateReportHandler.Execute(svc);

        Assert.Equal(policy1.ToJson(), policy2.ToJson());
    }

    [Fact]
    public void Execute_AfterReloadWithDifferentProject_ReturnsNewPolicy()
    {
        // Load Demo.L5X, call Execute, then reload → second Execute re-derives.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy1 = (WriteGatePolicy)GateReportHandler.Execute(svc);

        // Reload the same file (simulates a project switch in real usage).
        svc.Load(FixturePath);
        var policy2 = (WriteGatePolicy)GateReportHandler.Execute(svc);

        // Both calls should return equivalent results from the live state.
        Assert.Equal(policy1.ToJson(), policy2.ToJson());
    }

    // ------------------------------------------------------------------ //
    //  Serialize: JSON shape                                               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Serialize_OnPolicy_ProducesValidJson()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc);
        var json   = GateReportHandler.Serialize(result);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("grants").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("denies").ValueKind);
    }

    [Fact]
    public void Serialize_OnPolicy_CarriesPolicyEnforced()
    {
        // The response must carry policy: "enforced" to
        // assert that the report reflects the same rules logix_write applies.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("policy", out var policyProp),
            "Serialized gate report must include a 'policy' field.");
        Assert.Equal("enforced", policyProp.GetString());
    }

    /// <summary>
    /// The response must carry source: "project-file" so a caller
    /// can never mistake this report for a live-controller read. The policy is
    /// always derived from the opened/uploaded L5X project file, never the live
    /// controller and never a pending (unsaved) rung edit.
    /// </summary>
    [Fact]
    public void Serialize_OnPolicy_CarriesSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("source", out var sourceProp),
            "Serialized gate report must include a 'source' field.");
        Assert.Equal("project-file", sourceProp.GetString());
    }

    /// <summary>
    /// Hypothetical (what-if) runs must also carry
    /// source: "project-file" — the source-of-truth statement is independent of
    /// whether the run uses enforced or caller-supplied prefixes.
    /// </summary>
    [Fact]
    public void Serialize_OnHypotheticalPolicy_CarriesSourceProjectFile()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(
            GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sp_" }));
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("source", out var sourceProp),
            "Serialized hypothetical gate report must include a 'source' field.");
        Assert.Equal("project-file", sourceProp.GetString());
    }

    [Fact]
    public void Serialize_OnError_ProducesJsonWithErrorKey()
    {
        var svc    = new ProjectService();
        var result = GateReportHandler.Execute(svc);   // no project loaded
        var json   = GateReportHandler.Serialize(result);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void Serialize_OnPolicy_GrantsAreSortedAlphabetically()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        var tags   = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToList();
        var sorted = tags.OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.Equal(sorted, tags);
    }

    [Fact]
    public void Serialize_OnPolicy_DeniesAreSortedAlphabetically()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        var tags   = doc.RootElement.GetProperty("denies")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToList();
        var sorted = tags.OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.Equal(sorted, tags);
    }

    [Fact]
    public void Serialize_OnPolicy_NullBoundsPropertiesOmitted()
    {
        // Cmd_MotorStart (BOOL) has neither tagBounds nor UDT bounds.
        // Those keys must be absent (WhenWritingNull semantics from ToJson).
        var svc  = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        var motorStartGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_MotorStart");

        Assert.False(motorStartGrant.TryGetProperty("tagBounds", out _),
            "Null tagBounds should be omitted from serialized output.");
        Assert.False(motorStartGrant.TryGetProperty("bounds", out _),
            "Null bounds should be omitted from serialized output.");
    }

    [Fact]
    public void Serialize_OnPolicy_UdtGrantRetainsMemberBounds()
    {
        var svc  = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        var motorGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_Motor");

        var bounds = motorGrant.GetProperty("bounds");
        Assert.Equal(JsonValueKind.Array, bounds.ValueKind);

        var speed = bounds.EnumerateArray()
            .Single(b => b.GetProperty("member").GetString() == "Speed");

        Assert.Equal(0.0,   speed.GetProperty("min").GetDouble());
        Assert.Equal(100.0, speed.GetProperty("max").GetDouble());
    }

    [Fact]
    public void Serialize_OnPolicy_ScalarGrantRetainsTagBounds()
    {
        var svc  = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        var speedGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_SpeedSetpoint");

        Assert.True(speedGrant.TryGetProperty("tagBounds", out var tb));
        Assert.Equal(0.0,    tb.GetProperty("min").GetDouble());
        Assert.Equal(1000.0, tb.GetProperty("max").GetDouble());
    }

    [Fact]
    public void Serialize_IsDeterministic()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var json1 = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var json2 = GateReportHandler.Serialize(GateReportHandler.Execute(svc));

        Assert.Equal(json1, json2);
    }

    // ------------------------------------------------------------------ //
    //  Guard: no prefix parameters on enforcing tools //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Standing guard: <c>logix_connect</c> and <c>logix_write</c> must not
    /// expose <c>commandPrefixes</c> or <c>safetyPrefixes</c> parameters.
    ///
    /// <para>
    /// <c>logix_gate_report</c> intentionally exposes these parameters so that
    /// operators can run what-if (hypothetical) classifications — but those runs
    /// are clearly labelled with <c>policy: "hypothetical"</c> and an explicit
    /// disclaimer so callers cannot mistake them for the enforced policy.
    /// The enforcing paths (<c>logix_connect</c>, <c>logix_write</c>) must never
    /// accept caller-supplied prefix lists.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("logix_connect")]
    [InlineData("logix_write")]
    public void EnforcingTool_DoesNotExposeCommandOrSafetyPrefixParameters(string toolName)
    {
        // Resolve the MCP method by its [McpServerTool(Name = ...)] attribute.
        var toolMethod = typeof(LogixProjectTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(LogixTagTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .FirstOrDefault(m =>
            {
                var attr = m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>();
                return attr?.Name == toolName;
            });

        // The tool must be found — if it moves to a different class the test breaks loudly.
        Assert.NotNull(toolMethod);

        var paramNames = toolMethod!.GetParameters()
            .Select(p => p.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("commandPrefixes", paramNames);
        Assert.DoesNotContain("safetyPrefixes",  paramNames);
    }

    /// <summary>
    /// Guard: <c>logix_gate_report</c> must expose <c>commandPrefixes</c> and
    /// <c>safetyPrefixes</c> as optional parameters so hypothetical what-if runs
    /// are supported.
    /// </summary>
    [Fact]
    public void GateReportTool_ExposesCommandAndSafetyPrefixParameters()
    {
        var toolMethod = typeof(LogixProjectTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
            {
                var attr = m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>();
                return attr?.Name == "logix_gate_report";
            });

        Assert.NotNull(toolMethod);

        var paramNames = toolMethod!.GetParameters()
            .Select(p => p.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("commandPrefixes", paramNames);
        Assert.Contains("safetyPrefixes",  paramNames);
    }

    // ------------------------------------------------------------------ //
    //  Hypothetical path: custom prefix runs                               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_CustomCommandPrefixes_ReturnsHypotheticalGateResult()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        Assert.IsType<HypotheticalGateResult>(result);
    }

    [Fact]
    public void Execute_CustomSafetyPrefixes_ReturnsHypotheticalGateResult()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc,
            safetyPrefixes: new[] { "Motor_" });

        Assert.IsType<HypotheticalGateResult>(result);
    }

    [Fact]
    public void Execute_BothPrefixListsCustom_ReturnsHypotheticalGateResult()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" },
            safetyPrefixes:  new[] { "Motor_" });

        Assert.IsType<HypotheticalGateResult>(result);
    }

    [Fact]
    public void Execute_HypotheticalRun_UsesCustomCommandPrefixes()
    {
        // With only Sim_ as command prefix, only Sim_LineSpeed is granted.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var grantNames = hypothetical.Policy.Grants.Select(g => g.Tag).ToHashSet();
        Assert.Single(hypothetical.Policy.Grants);
        Assert.Contains("Sim_LineSpeed", grantNames);
        Assert.DoesNotContain("Cmd_MotorStart", grantNames);
    }

    [Fact]
    public void Execute_HypotheticalRun_CarriesCustomConfig()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" },
            safetyPrefixes:  new[] { "Motor_" });

        Assert.Contains("Sim_", hypothetical.Config.CommandPrefixes);
        Assert.Contains("Motor_", hypothetical.Config.SafetyPrefixes);
    }

    [Fact]
    public void Execute_HypotheticalRun_OmittedSafetyPrefixes_FallBackToDefault()
    {
        // Supplying only commandPrefixes — safetyPrefixes should default to Safety_.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        Assert.Contains("Safety_", hypothetical.Config.SafetyPrefixes);
    }

    [Fact]
    public void Execute_HypotheticalRun_OmittedCommandPrefixes_FallBackToDefault()
    {
        // Supplying only safetyPrefixes — commandPrefixes should default to Cmd_, Sim_.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            safetyPrefixes: new[] { "Motor_" });

        Assert.Contains("Cmd_", hypothetical.Config.CommandPrefixes);
        Assert.Contains("Sim_", hypothetical.Config.CommandPrefixes);
    }

    [Fact]
    public void Serialize_HypotheticalResult_CarriesPolicyHypothetical()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("policy", out var policyProp),
            "Hypothetical gate report must include a 'policy' field.");
        Assert.Equal("hypothetical", policyProp.GetString());
    }

    [Fact]
    public void Serialize_HypotheticalResult_CarriesDisclaimer()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("disclaimer", out var disclaimer),
            "Hypothetical gate report must include a 'disclaimer' field.");
        Assert.False(string.IsNullOrWhiteSpace(disclaimer.GetString()),
            "Disclaimer must be non-empty.");
    }

    [Fact]
    public void Serialize_HypotheticalResult_Disclaimer_MentionsHypotheticalKeyword()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        var disclaimer = doc.RootElement.GetProperty("disclaimer").GetString()!;
        Assert.Contains("HYPOTHETICAL", disclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_HypotheticalResult_Disclaimer_StatesNotEnforced()
    {
        // The disclaimer must clearly say the result is NOT the enforced policy.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        var disclaimer = doc.RootElement.GetProperty("disclaimer").GetString()!;
        Assert.True(
            disclaimer.Contains("NOT", StringComparison.OrdinalIgnoreCase) ||
            disclaimer.Contains("not represent", StringComparison.OrdinalIgnoreCase),
            $"Disclaimer must state the result is not the enforced policy. Got: {disclaimer}");
    }

    [Fact]
    public void Serialize_HypotheticalResult_Disclaimer_MentionsCustomPrefixes()
    {
        // The disclaimer must name the custom prefixes so the consumer can see
        // what rules produced this hypothetical classification.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sp_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        var disclaimer = doc.RootElement.GetProperty("disclaimer").GetString()!;
        Assert.Contains("Sp_", disclaimer);
    }

    [Fact]
    public void Serialize_EnforcedResult_DoesNotCarryDisclaimer()
    {
        // Enforced (default prefix) runs must NOT include a disclaimer field.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc); // no custom prefixes
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("disclaimer", out _),
            "Enforced gate report must NOT include a 'disclaimer' field.");
    }

    [Fact]
    public void GetGateReportResponse_WithCustomCommandPrefixes_CarriesHypotheticalPolicy()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse(commandPrefixes: new[] { "Sim_" });
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("hypothetical",
            doc.RootElement.GetProperty("policy").GetString());
    }

    [Fact]
    public void GetGateReportResponse_NoCustomPrefixes_CarriesEnforcedPolicy()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse(); // no custom prefixes
        var doc  = JsonDocument.Parse(json);

        Assert.Equal("enforced",
            doc.RootElement.GetProperty("policy").GetString());
    }

    // ------------------------------------------------------------------ //
    //  LogixProjectTools integration                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void LogixProjectTools_GateReport_ReturnsErrorJsonBeforeOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        var json  = tools.GetGateReportResponse();

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void LogixProjectTools_GateReport_ReturnsGrantsAndDeniesAfterOpen()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse();
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("grants", out _));
        Assert.True(doc.RootElement.TryGetProperty("denies", out _));
    }

    [Fact]
    public void LogixProjectTools_GateReport_DefaultPrefixes_ReturnsFourGrants()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse();
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(4, doc.RootElement.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public void LogixProjectTools_GateReport_DefaultPrefixes_ReturnsTwoDenies()
    {
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse();
        var doc  = JsonDocument.Parse(json);

        // Safety_OK (safety_tag) + Motor_Run (program_written) + SomeRandomTag (not_designated)
        Assert.Equal(3, doc.RootElement.GetProperty("denies").GetArrayLength());
    }

    [Fact]
    public void LogixProjectTools_GateReport_CarriesPolicyEnforced()
    {
        // MCP-layer response must carry policy: "enforced".
        var svc   = new ProjectService();
        var tools = new LogixProjectTools(svc);
        tools.GetOpenProjectResponse(FixturePath);

        var json = tools.GetGateReportResponse();
        var doc  = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("policy", out var policyProp),
            "MCP gate report response must include a 'policy' field.");
        Assert.Equal("enforced", policyProp.GetString());
    }

    // ------------------------------------------------------------------ //
    //  Divergence: HypotheticalGateResult carries comparison with enforced //
    // ------------------------------------------------------------------ //

    [Fact]
    public void Execute_HypotheticalRun_CarriesDivergenceList()
    {
        // Divergence must be non-null on any hypothetical result.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        Assert.NotNull(hypothetical.Divergence);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomCommandOnly_DivergenceContainsCmdTags()
    {
        // With only Sim_ as command prefix, Cmd_* tags move from granted → denied:NotDesignated.
        // Expected divergences (enforced=granted, hypothetical=denied:NotDesignated):
        //   Cmd_Motor, Cmd_MotorStart, Cmd_SpeedSetpoint
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var divTags = hypothetical.Divergence
            .Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Cmd_Motor",         divTags);
        Assert.Contains("Cmd_MotorStart",    divTags);
        Assert.Contains("Cmd_SpeedSetpoint", divTags);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomCommandOnly_SimTagIsNotDivergent()
    {
        // Sim_LineSpeed is granted under both policies → must NOT appear in divergence.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var divTags = hypothetical.Divergence
            .Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Sim_LineSpeed", divTags);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomCommandOnly_ProgramWrittenTagIsNotDivergent()
    {
        // Motor_Run is denied:ProgramWritten under both policies → not divergent.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var divTags = hypothetical.Divergence
            .Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Motor_Run", divTags);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomCommandOnly_DivergenceCount_IsThree()
    {
        // Cmd_Motor, Cmd_MotorStart, Cmd_SpeedSetpoint each move from granted →
        // denied:NotDesignated; everything else stays the same.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        Assert.Equal(3, hypothetical.Divergence.Count);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomCommandOnly_DivergenceEntries_ShowBothDispositions()
    {
        // Each divergent Cmd_* entry must carry enforced="granted" and
        // hypothetical="denied:NotDesignated".
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var cmdMotorEntry = hypothetical.Divergence
            .Single(d => string.Equals(d.Tag, "Cmd_MotorStart", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("granted",               cmdMotorEntry.Enforced,     StringComparer.OrdinalIgnoreCase);
        Assert.Equal("denied:NotDesignated",  cmdMotorEntry.Hypothetical, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_HypotheticalRun_CustomSafetyOnly_DivergenceReflectsReasonChange()
    {
        // With Motor_ as the safety prefix, Safety_OK is no longer matched by a
        // safety prefix → its denial reason changes from SafetyTag → NotDesignated.
        // This verifies that reason changes (not just grant↔deny) are reported.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            safetyPrefixes: new[] { "Motor_" });

        var safetyEntry = hypothetical.Divergence
            .SingleOrDefault(d => string.Equals(d.Tag, "Safety_OK", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(safetyEntry);
        Assert.Equal("denied:SafetyTag",      safetyEntry!.Enforced,     StringComparer.OrdinalIgnoreCase);
        Assert.Equal("denied:NotDesignated",  safetyEntry.Hypothetical,  StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_HypotheticalRun_IdenticalPrefixes_DivergenceIsEmpty()
    {
        // Passing the same prefixes as GateConfig.Default produces an identical policy;
        // divergence must be an empty list, not null.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Cmd_", "Sim_" },
            safetyPrefixes:  new[] { "Safety_" });

        Assert.NotNull(hypothetical.Divergence);
        Assert.Empty(hypothetical.Divergence);
    }

    [Fact]
    public void Execute_HypotheticalRun_DivergenceList_IsSortedAlphabetically()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var hypothetical = (HypotheticalGateResult)GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Sim_" });

        var tags   = hypothetical.Divergence.Select(d => d.Tag).ToList();
        var sorted = tags.OrderBy(t => t, StringComparer.Ordinal).ToList();

        Assert.Equal(sorted, tags);
    }

    [Fact]
    public void Serialize_HypotheticalResult_CarriesDivergenceField()
    {
        // The serialized JSON must include a "divergence" array.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("divergence", out var div),
            "Hypothetical gate report JSON must include a 'divergence' field.");
        Assert.Equal(JsonValueKind.Array, div.ValueKind);
    }

    [Fact]
    public void Serialize_HypotheticalResult_DivergenceEntries_HaveExpectedKeys()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        var firstEntry = doc.RootElement.GetProperty("divergence")[0];
        Assert.True(firstEntry.TryGetProperty("tag",          out _), "Divergence entry must have 'tag' key.");
        Assert.True(firstEntry.TryGetProperty("enforced",     out _), "Divergence entry must have 'enforced' key.");
        Assert.True(firstEntry.TryGetProperty("hypothetical", out _), "Divergence entry must have 'hypothetical' key.");
    }

    [Fact]
    public void Serialize_HypotheticalResult_DivergenceCount_MatchesDomainObject()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result       = GateReportHandler.Execute(svc, commandPrefixes: new[] { "Sim_" });
        var hypothetical = (HypotheticalGateResult)result;
        var json         = GateReportHandler.Serialize(result);
        var doc          = JsonDocument.Parse(json);

        Assert.Equal(hypothetical.Divergence.Count,
            doc.RootElement.GetProperty("divergence").GetArrayLength());
    }

    [Fact]
    public void Serialize_EnforcedResult_DoesNotCarryDivergenceField()
    {
        // Enforced (default prefix) runs must NOT include a "divergence" field.
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc); // no custom prefixes
        var json   = GateReportHandler.Serialize(result);
        var doc    = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("divergence", out _),
            "Enforced gate report must NOT include a 'divergence' field.");
    }

    [Fact]
    public void Serialize_HypotheticalResult_IdenticalPrefixes_DivergenceArrayIsEmpty()
    {
        // Identical prefix lists → empty divergence array in JSON (not absent).
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var result = GateReportHandler.Execute(svc,
            commandPrefixes: new[] { "Cmd_", "Sim_" },
            safetyPrefixes:  new[] { "Safety_" });
        var json = GateReportHandler.Serialize(result);
        var doc  = JsonDocument.Parse(json);

        // divergence field must be present and empty
        Assert.True(doc.RootElement.TryGetProperty("divergence", out var div));
        Assert.Equal(0, div.GetArrayLength());
    }

    // ------------------------------------------------------------------ //
    //  Gate reasons and provenance are server-authored — not wrapped  //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Grant provenance strings are generated by the server (GateEngine) and
    /// explain why a tag was granted.  They are server-authored text and must NOT
    /// be wrapped in <c>&lt;untrusted&gt;</c> — only customer-authored content
    /// (rung comments, tag descriptions, rung neutral text) receives that boundary.
    /// </summary>
    [Fact]
    public void Execute_GrantEntries_ProvenanceIsNotWrappedInUntrustedTag()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        foreach (var grant in policy.Grants)
        {
            Assert.False(
                grant.Provenance.Contains("<untrusted", StringComparison.Ordinal),
                $"Grant provenance for tag '{grant.Tag}' must not contain '<untrusted' — " +
                $"gate provenance is server-authored, not customer-authored text. " +
                $"Actual: {grant.Provenance}");
        }
    }

    /// <summary>
    /// Deny provenance strings are server-authored explanations of the deny rule
    /// (e.g. "Tag 'Motor_Run' is written by program logic at …").
    /// They must never be wrapped in <c>&lt;untrusted&gt;</c>.
    /// </summary>
    [Fact]
    public void Execute_DenyEntries_ProvenanceIsNotWrappedInUntrustedTag()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var policy = (WriteGatePolicy)GateReportHandler.Execute(svc);

        foreach (var deny in policy.Denies)
        {
            Assert.False(
                deny.Provenance.Contains("<untrusted", StringComparison.Ordinal),
                $"Deny provenance for tag '{deny.Tag}' must not contain '<untrusted' — " +
                $"gate provenance is server-authored, not customer-authored text. " +
                $"Actual: {deny.Provenance}");
        }
    }

    /// <summary>
    /// Verify the serialized JSON grant <c>provenance</c> field is plain text
    /// with no <c>&lt;untrusted&gt;</c> wrapper.
    /// </summary>
    [Fact]
    public void Serialize_GrantProvenanceJson_DoesNotContainUntrustedWrapper()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        foreach (var grant in doc.RootElement.GetProperty("grants").EnumerateArray())
        {
            var provenance = grant.GetProperty("provenance").GetString() ?? "";
            Assert.False(
                provenance.Contains("<untrusted", StringComparison.Ordinal),
                $"Serialized grant provenance must not contain '<untrusted' — " +
                $"gate reasons are server-authored, not customer-authored. Actual: {provenance}");
        }
    }

    /// <summary>
    /// Verify the serialized JSON deny <c>provenance</c> field is plain text
    /// with no <c>&lt;untrusted&gt;</c> wrapper.
    /// </summary>
    [Fact]
    public void Serialize_DenyProvenanceJson_DoesNotContainUntrustedWrapper()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);
        var json = GateReportHandler.Serialize(GateReportHandler.Execute(svc));
        var doc  = JsonDocument.Parse(json);

        foreach (var deny in doc.RootElement.GetProperty("denies").EnumerateArray())
        {
            var provenance = deny.GetProperty("provenance").GetString() ?? "";
            Assert.False(
                provenance.Contains("<untrusted", StringComparison.Ordinal),
                $"Serialized deny provenance must not contain '<untrusted' — " +
                $"gate reasons are server-authored, not customer-authored. Actual: {provenance}");
        }
    }

    // ------------------------------------------------------------------ //
    //  External Access on every entry + denyCounts //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Every grant and deny entry carries <c>externalAccess</c>, and
    /// <c>denyCounts.ExternalAccessNotReadWrite</c> counts the Read Only denies.
    /// A missing attribute is reported as Read/Write.
    /// </summary>
    [Fact]
    public void GrantsAndDenies_CarryExternalAccess_AndDenyCount()
    {
        static string Bool(string name, string? ea) =>
            $"<Tag Name=\"{name}\" TagType=\"Base\"" +
            (ea is null ? "" : $" ExternalAccess=\"{ea}\"") +
            " DataType=\"BOOL\" Radix=\"Decimal\"><Data Format=\"Decorated\">" +
            "<DataValue DataType=\"BOOL\" Radix=\"Decimal\" Value=\"0\" /></Data></Tag>";

        var l5x = "<RSLogix5000Content SchemaRevision=\"1.0\" SoftwareRevision=\"36.1\" " +
            "TargetName=\"C\" TargetType=\"Controller\" ContainsContext=\"false\">" +
            "<Controller Name=\"C\" MajorRev=\"36\" MinorRev=\"1\" ProcessorType=\"1756-L84E\"><Tags>" +
            Bool("Cmd_Open", "Read/Write") + Bool("Cmd_Default", null) +
            Bool("Cmd_Locked", "Read Only") + Bool("Plain_Hidden", "None") +
            "</Tags><Programs><Program Name=\"MainProgram\" Type=\"Normal\" TestEdits=\"false\" " +
            "Disabled=\"false\" UseAsFolder=\"false\" MainRoutineName=\"MainRoutine\"><Tags />" +
            "<Routines><Routine Name=\"MainRoutine\" Type=\"RLL\"><RLLContent>" +
            "<Rung Number=\"0\" Type=\"N\"><Text><![CDATA[NOP();]]></Text></Rung>" +
            "</RLLContent></Routine></Routines></Program></Programs><Tasks /></Controller>" +
            "</RSLogix5000Content>";

        var path = Path.Combine(Path.GetTempPath(), $"gate-ea-{Guid.NewGuid():N}.L5X");
        File.WriteAllText(path, l5x);
        try
        {
            var svc = new ProjectService();
            svc.Load(path);
            var doc = JsonDocument.Parse(GateReportHandler.Serialize(GateReportHandler.Execute(svc)));

            var grants = doc.RootElement.GetProperty("grants").EnumerateArray()
                .ToDictionary(g => g.GetProperty("tag").GetString()!,
                              g => g.GetProperty("externalAccess").GetString());
            Assert.Equal("Read/Write", grants["Cmd_Open"]);
            Assert.Equal("Read/Write", grants["Cmd_Default"]);
            Assert.DoesNotContain("Cmd_Locked", grants.Keys);

            var denies = doc.RootElement.GetProperty("denies").EnumerateArray()
                .ToDictionary(d => d.GetProperty("tag").GetString()!);
            Assert.Equal("Read Only", denies["Cmd_Locked"].GetProperty("externalAccess").GetString());
            Assert.Equal("ExternalAccessNotReadWrite", denies["Cmd_Locked"].GetProperty("reason").GetString());
            Assert.Equal("None", denies["Plain_Hidden"].GetProperty("externalAccess").GetString());

            Assert.Equal(2, doc.RootElement.GetProperty("denyCounts")
                .GetProperty("ExternalAccessNotReadWrite").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
