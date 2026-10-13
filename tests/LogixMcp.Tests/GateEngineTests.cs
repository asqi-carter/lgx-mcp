using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="GateEngine"/>, <see cref="GateConfig"/>,
/// and <see cref="WriteGatePolicy"/> constituent types.
/// Uses the Demo.L5X fixture which has:
///   Controller tags: Cmd_MotorStart (BOOL), Cmd_SpeedSetpoint (REAL),
///                    Sim_LineSpeed (REAL), Safety_OK (BOOL),
///                    Motor_Run (BOOL), Cmd_Motor (MotorCmd)
///   UDT MotorCmd: Enable (BOOL), Speed (REAL, description "range 0..100")
///   Rung 0 (MainProgram/MainRoutine): XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run)
///
/// Four-rule derivation applied to ALL tags:
///   Rule 2 (program_written):  Motor_Run driven by OTE → DenyReason.ProgramWritten
///   Rule 3 (safety_tag):       Safety_OK matches "Safety_" → DenyReason.SafetyTag
///   Rule 1 (grant):            Cmd_MotorStart, Cmd_SpeedSetpoint, Sim_LineSpeed,
///                              Cmd_Motor match a CommandPrefix → granted
///   Rule 4 (not_designated):   no remaining tags in Demo.L5X
/// </summary>
public class GateEngineTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    private static L5X LoadProject() => L5X.Load(FixturePath);

    // ------------------------------------------------------------------ //
    //  GateConfig                                                          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void GateConfig_Default_HasExpectedCommandPrefixes()
    {
        var config = GateConfig.Default;
        Assert.Contains("Cmd_", config.CommandPrefixes);
        Assert.Contains("Sim_", config.CommandPrefixes);
    }

    [Fact]
    public void GateConfig_Default_HasExpectedSafetyPrefixes()
    {
        var config = GateConfig.Default;
        Assert.Contains("Safety_", config.SafetyPrefixes);
    }

    [Fact]
    public void GateConfig_Default_CommandPrefixesCount_IsTwo()
    {
        Assert.Equal(2, GateConfig.Default.CommandPrefixes.Count);
    }

    [Fact]
    public void GateConfig_Default_SafetyPrefixesCount_IsOne()
    {
        Assert.Single(GateConfig.Default.SafetyPrefixes);
    }

    [Fact]
    public void GateConfig_SerializesToValidJson()
    {
        var json = JsonSerializer.Serialize(GateConfig.Default);
        var doc  = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("commandPrefixes").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("safetyPrefixes").ValueKind);
    }

    // ------------------------------------------------------------------ //
    //  GateEngine.DerivePolicy — general shape                            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_ReturnsPolicyWithGrantsAndDenies()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.NotNull(policy);
        Assert.NotNull(policy.Grants);
        Assert.NotNull(policy.Denies);
    }

    [Fact]
    public void DerivePolicy_TotalEntryCount_EqualsAllTagCount()
    {
        // Demo has 7 controller tags: 4 grants + 3 denies (Motor_Run, Safety_OK, SomeRandomTag).
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Equal(7, policy.Grants.Count + policy.Denies.Count);
    }

    [Fact]
    public void DerivePolicy_NonCommandTags_AppearInDenyList()
    {
        // Safety_OK and Motor_Run do not match any CommandPrefix and are not
        // granted; they appear in the deny list under rule 3 and rule 2 respectively.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var denyNames = policy.Denies.Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Safety_OK",  denyNames);
        Assert.Contains("Motor_Run",  denyNames);
    }

    // ------------------------------------------------------------------ //
    //  Grant entries                                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_GrantsContainCmd_MotorStart()
    {
        // Cmd_MotorStart matches CommandPrefix and is only READ by program logic
        // (XIC), so it is granted.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_MotorStart");
    }

    [Fact]
    public void DerivePolicy_GrantsContainCmd_SpeedSetpoint()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_SpeedSetpoint");
    }

    [Fact]
    public void DerivePolicy_GrantsContainSim_LineSpeed()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Sim_LineSpeed");
    }

    [Fact]
    public void DerivePolicy_GrantsContainCmd_Motor()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Motor");
    }

    [Fact]
    public void DerivePolicy_ScalarGrantEntry_HasNullOrEmptyBounds()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_SpeedSetpoint");
        // REAL scalar — no UDT member bounds
        Assert.True(entry.Bounds is null || entry.Bounds.Count == 0);
    }

    [Fact]
    public void DerivePolicy_GrantEntry_HasNonEmptyProvenance()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        foreach (var grant in policy.Grants)
            Assert.False(string.IsNullOrWhiteSpace(grant.Provenance));
    }

    [Fact]
    public void DerivePolicy_GrantProvenance_MentionsMatchedPrefix()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var cmdEntry = policy.Grants.Single(g => g.Tag == "Cmd_SpeedSetpoint");
        Assert.Contains("Cmd_", cmdEntry.Provenance, StringComparison.OrdinalIgnoreCase);

        var simEntry = policy.Grants.Single(g => g.Tag == "Sim_LineSpeed");
        Assert.Contains("Sim_", simEntry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_GrantProvenance_MentionsTagName()
    {
        // The grant provenance must cite the matched tag element (tag name) so the
        // policy can answer "why can the agent touch X."
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        foreach (var grant in policy.Grants)
            Assert.Contains(grant.Tag, grant.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    //  UDT member bounds                                                   //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_UdtTagGrant_HasMemberBounds()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        Assert.NotNull(entry.Bounds);
        Assert.NotEmpty(entry.Bounds);
    }

    [Fact]
    public void DerivePolicy_UdtTagGrant_SpeedMemberHasCorrectBounds()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry  = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        var speed  = entry.Bounds!.Single(b => b.Member == "Speed");

        Assert.Equal(0.0,   speed.Min);
        Assert.Equal(100.0, speed.Max);
    }

    [Fact]
    public void DerivePolicy_UdtTagGrant_BoundsProvenance_MentionsUdtName()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        Assert.Contains("MotorCmd", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_UdtMemberWithoutDescription_IsNotInBounds()
    {
        // MotorCmd.Enable has no description → no bounds entry for Enable
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        Assert.DoesNotContain(entry.Bounds!, b => b.Member == "Enable");
    }

    // ------------------------------------------------------------------ //
    //  Scalar tag bounds (TagBounds parsed from tag description)          //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_ScalarGrantWithRangeDescription_HasTagBounds()
    {
        // Cmd_SpeedSetpoint (REAL) carries description "range 0..1000" →
        // TagBounds should be populated with min=0, max=1000.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_SpeedSetpoint");
        Assert.NotNull(entry.TagBounds);
    }

    [Fact]
    public void DerivePolicy_ScalarGrantWithRangeDescription_TagBoundsHasCorrectMinMax()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_SpeedSetpoint");
        Assert.Equal(0.0,    entry.TagBounds!.Min);
        Assert.Equal(1000.0, entry.TagBounds!.Max);
    }

    [Fact]
    public void DerivePolicy_ScalarGrantWithRangeDescription_ProvenanceMentionsBounds()
    {
        // The grant provenance string must mention the parsed scalar bounds so the
        // policy is self-explanatory to a human reader.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_SpeedSetpoint");
        Assert.Contains("0", entry.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1000", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_ScalarGrantWithoutDescription_HasNullTagBounds()
    {
        // Sim_LineSpeed (REAL) has no description → TagBounds must be null (unbounded grant).
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Sim_LineSpeed");
        Assert.Null(entry.TagBounds);
    }

    [Fact]
    public void DerivePolicy_BoolScalarGrant_HasNullTagBounds()
    {
        // Cmd_MotorStart (BOOL) is not a REAL/INT-family type → TagBounds must be null.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_MotorStart");
        Assert.Null(entry.TagBounds);
    }

    [Fact]
    public void DerivePolicy_UdtTagGrant_HasNullTagBounds()
    {
        // Cmd_Motor is a UDT (MotorCmd), not a scalar → TagBounds must be null.
        // Per-member bounds appear in Bounds instead.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Grants.Single(g => g.Tag == "Cmd_Motor");
        Assert.Null(entry.TagBounds);
    }

    [Fact]
    public void DerivePolicy_ScalarTagBoundsJson_HasMinAndMaxKeys()
    {
        // Cmd_SpeedSetpoint has TagBounds → JSON grant must include "tagBounds" object.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = JsonSerializer.Serialize(policy);
        var doc    = JsonDocument.Parse(json);

        var speedGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_SpeedSetpoint");

        Assert.True(speedGrant.TryGetProperty("tagBounds", out var tagBoundsEl),
            "Expected 'tagBounds' key in JSON grant for Cmd_SpeedSetpoint.");
        Assert.Equal(JsonValueKind.Object, tagBoundsEl.ValueKind);
        Assert.Equal(0.0,    tagBoundsEl.GetProperty("min").GetDouble());
        Assert.Equal(1000.0, tagBoundsEl.GetProperty("max").GetDouble());
    }

    // ------------------------------------------------------------------ //
    //  Deny entries — program_written (rule 2)                            //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_DenyList_ContainsMotorRun_AsProgramWritten()
    {
        // Motor_Run is driven by OTE on rung 0, so it is denied as program_written.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Equal(DenyReason.ProgramWritten, entry.Reason);
    }

    [Fact]
    public void DerivePolicy_ProgramWritten_Provenance_MentionsRungLocation()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Contains("MainProgram",  entry.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MainRoutine",  entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_ProgramWritten_Provenance_MentionsInstruction()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Contains("OTE", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_ProgramWritten_Provenance_MentionsRungNumber()
    {
        // Spec: program_written must cite program name, routine name, AND rung reference.
        // Rung reference is encoded as "[<number>]" in the provenance string.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Motor_Run");
        // The write-location format is "Program/Routine[N] via INSTR" — verify "[" is present
        Assert.Contains("[", entry.Provenance);
    }

    // ------------------------------------------------------------------ //
    //  Deny entries — safety_tag (rule 3)                                 //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_DenyList_ContainsSafetyOK_AsSafetyTag()
    {
        // Safety_OK matches the "Safety_" prefix → denied as safety_tag.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Safety_OK");
        Assert.Equal(DenyReason.SafetyTag, entry.Reason);
    }

    [Fact]
    public void DerivePolicy_SafetyTag_Provenance_MentionsSafetyPrefix()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var entry = policy.Denies.Single(d => d.Tag == "Safety_OK");
        Assert.Contains("Safety_", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DerivePolicy_DenyEntry_HasNonEmptyProvenance()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        foreach (var deny in policy.Denies)
            Assert.False(string.IsNullOrWhiteSpace(deny.Provenance));
    }

    [Fact]
    public void DerivePolicy_NotDesignated_Provenance_CitesCheckedCommandPrefixes()
    {
        // Spec: not_designated must cite the absence of any matching grant rule,
        // which includes naming the command prefixes that were checked.
        // Use a config where Cmd_MotorStart is not_designated (only Sim_ is a command prefix).
        var config = new GateConfig(
            CommandPrefixes: new[] { "Sim_" },
            SafetyPrefixes:  new[] { "Safety_" });

        var policy = GateEngine.DerivePolicy(LoadProject(), config);

        var entry = policy.Denies.Single(d => d.Tag == "Cmd_MotorStart");
        Assert.Equal(DenyReason.NotDesignated, entry.Reason);
        // Provenance must mention the command prefix that was checked ('Sim_')
        Assert.Contains("Sim_", entry.Provenance, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    //  Counts                                                              //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_DenyCount_IsTwo()
    {
        // Motor_Run (program_written) + Safety_OK (safety_tag) + SomeRandomTag (not_designated)
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Equal(3, policy.Denies.Count);
    }

    [Fact]
    public void DerivePolicy_GrantCount_IsFour()
    {
        // Cmd_MotorStart, Cmd_SpeedSetpoint, Sim_LineSpeed, Cmd_Motor
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Equal(4, policy.Grants.Count);
    }

    // ------------------------------------------------------------------ //
    //  Serialization                                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_SerializesToValidJson()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = JsonSerializer.Serialize(policy);
        var doc    = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("grants").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("denies").ValueKind);
    }

    [Fact]
    public void DerivePolicy_GrantJson_HasExpectedKeys()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = JsonSerializer.Serialize(policy);
        var doc    = JsonDocument.Parse(json);

        var firstGrant = doc.RootElement.GetProperty("grants")[0];
        Assert.True(firstGrant.TryGetProperty("tag",        out _));
        Assert.True(firstGrant.TryGetProperty("provenance", out _));
        // "bounds" may be null for scalar grants — just ensure the key is not missing
        // (it may be omitted by the serialiser when null — that's acceptable)
    }

    [Fact]
    public void DerivePolicy_DenyJson_HasExpectedKeys()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = JsonSerializer.Serialize(policy);
        var doc    = JsonDocument.Parse(json);

        var firstDeny = doc.RootElement.GetProperty("denies")[0];
        Assert.True(firstDeny.TryGetProperty("tag",        out _));
        Assert.True(firstDeny.TryGetProperty("reason",     out _));
        Assert.True(firstDeny.TryGetProperty("provenance", out _));
    }

    [Fact]
    public void DerivePolicy_UdtGrantBoundsJson_HasMinMaxAndMemberKeys()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = JsonSerializer.Serialize(policy);
        var doc    = JsonDocument.Parse(json);

        // Find the Cmd_Motor grant
        var motorGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_Motor");

        var boundsArr = motorGrant.GetProperty("bounds");
        Assert.Equal(JsonValueKind.Array, boundsArr.ValueKind);

        var speedBound = boundsArr.EnumerateArray()
            .Single(b => b.GetProperty("member").GetString() == "Speed");

        Assert.Equal(0.0,   speedBound.GetProperty("min").GetDouble());
        Assert.Equal(100.0, speedBound.GetProperty("max").GetDouble());
    }

    // ------------------------------------------------------------------ //
    //  Custom config                                                       //
    // ------------------------------------------------------------------ //

    [Fact]
    public void DerivePolicy_CustomCommandPrefix_GrantsOnlyMatchingTags()
    {
        // With only "Sim_" as a command prefix, only Sim_LineSpeed is granted.
        // Cmd_* tags and Safety_OK get not_designated or safety_tag; Motor_Run gets
        // program_written.
        var config = new GateConfig(
            CommandPrefixes: new[] { "Sim_" },
            SafetyPrefixes:  new[] { "Safety_" });

        var policy = GateEngine.DerivePolicy(LoadProject(), config);

        Assert.Single(policy.Grants);
        Assert.Equal("Sim_LineSpeed", policy.Grants[0].Tag);
    }

    [Fact]
    public void DerivePolicy_CustomCommandPrefix_NonSimTagsAreDenied()
    {
        var config = new GateConfig(
            CommandPrefixes: new[] { "Sim_" },
            SafetyPrefixes:  new[] { "Safety_" });

        var policy = GateEngine.DerivePolicy(LoadProject(), config);

        var denyNames = policy.Denies.Select(d => d.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Cmd_MotorStart",    denyNames);
        Assert.Contains("Cmd_SpeedSetpoint", denyNames);
        Assert.Contains("Cmd_Motor",         denyNames);
        Assert.Contains("Safety_OK",         denyNames);
        Assert.Contains("Motor_Run",         denyNames);
    }

    [Fact]
    public void DerivePolicy_EmptyCommandPrefixes_NoGrantsProduced()
    {
        // With no command prefixes, nothing can be granted; all tags are denied.
        var config = new GateConfig(
            CommandPrefixes: Array.Empty<string>(),
            SafetyPrefixes:  new[] { "Safety_" });

        var policy = GateEngine.DerivePolicy(LoadProject(), config);

        Assert.Empty(policy.Grants);
        // All 7 controller tags end up in the deny list
        Assert.Equal(7, policy.Denies.Count);
    }

    [Fact]
    public void DerivePolicy_EmptySafetyPrefixes_SafetyTagBecomesNotDesignated()
    {
        // With no safety prefixes, Safety_OK is not matched by rule 3.
        // Motor_Run is still denied (program_written); Safety_OK falls through to
        // not_designated; command-prefixed tags are granted.
        var config = new GateConfig(
            CommandPrefixes: new[] { "Cmd_", "Sim_" },
            SafetyPrefixes:  Array.Empty<string>());

        var policy = GateEngine.DerivePolicy(LoadProject(), config);

        Assert.Equal(4, policy.Grants.Count);
        // Motor_Run (program_written) + Safety_OK (not_designated, no safety prefix)
        // + SomeRandomTag (not_designated)
        Assert.Equal(3, policy.Denies.Count);

        var safetyEntry = policy.Denies.Single(d => d.Tag == "Safety_OK");
        Assert.Equal(DenyReason.NotDesignated, safetyEntry.Reason);

        var motorEntry = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Equal(DenyReason.ProgramWritten, motorEntry.Reason);
    }

    // ------------------------------------------------------------------ //
    //  WriteGatePolicy.ToJson — deterministic serialization               //
    // ------------------------------------------------------------------ //

    [Fact]
    public void ToJson_ProducesValidJson()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();

        // Must be parseable
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("grants").ValueKind);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("denies").ValueKind);
    }

    [Fact]
    public void ToJson_IsDeterministic_SameOutputOnRepeatedCalls()
    {
        // The same policy must produce byte-for-byte identical JSON on every call.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var json1 = policy.ToJson();
        var json2 = policy.ToJson();
        var json3 = policy.ToJson();

        Assert.Equal(json1, json2);
        Assert.Equal(json1, json3);
    }

    [Fact]
    public void ToJson_IsDeterministic_SameOutputFromIndependentDerivePolicy()
    {
        // Two independently derived policies from the same input must produce
        // identical JSON, proving the output is not dependent on traversal order.
        var policy1 = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var policy2 = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Equal(policy1.ToJson(), policy2.ToJson());
    }

    [Fact]
    public void ToJson_Grants_AreOrderedAlphabeticallyByTag()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var grantTags = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToList();

        var sorted = grantTags.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, grantTags);
    }

    [Fact]
    public void ToJson_Denies_AreOrderedAlphabeticallyByTag()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var denyTags = doc.RootElement.GetProperty("denies")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToList();

        var sorted = denyTags.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, denyTags);
    }

    [Fact]
    public void ToJson_ContainsAllGrantTags()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var grantTagsInJson = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var grant in policy.Grants)
            Assert.Contains(grant.Tag, grantTagsInJson);
    }

    [Fact]
    public void ToJson_ContainsAllDenyTags()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var denyTagsInJson = doc.RootElement.GetProperty("denies")
            .EnumerateArray()
            .Select(e => e.GetProperty("tag").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var deny in policy.Denies)
            Assert.Contains(deny.Tag, denyTagsInJson);
    }

    [Fact]
    public void ToJson_NullBoundsProperties_AreOmittedFromOutput()
    {
        // Null tagBounds / bounds fields must not appear in the JSON output
        // (WhenWritingNull semantics), keeping the output compact.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var motorStartGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_MotorStart");

        // Cmd_MotorStart is BOOL — no tagBounds or UDT bounds; keys must be absent.
        Assert.False(motorStartGrant.TryGetProperty("tagBounds", out _),
            "Null tagBounds should be omitted from JSON.");
        Assert.False(motorStartGrant.TryGetProperty("bounds", out _),
            "Null bounds should be omitted from JSON.");
    }

    [Fact]
    public void ToJson_UdtGrant_BoundsPreservedInOutput()
    {
        // Cmd_Motor (MotorCmd UDT) must still carry its per-member bounds in ToJson output.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var motorGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_Motor");

        var boundsArr = motorGrant.GetProperty("bounds");
        Assert.Equal(JsonValueKind.Array, boundsArr.ValueKind);

        var speedBound = boundsArr.EnumerateArray()
            .Single(b => b.GetProperty("member").GetString() == "Speed");
        Assert.Equal(0.0,   speedBound.GetProperty("min").GetDouble());
        Assert.Equal(100.0, speedBound.GetProperty("max").GetDouble());
    }

    [Fact]
    public void ToJson_ScalarGrant_TagBoundsPreservedInOutput()
    {
        // Cmd_SpeedSetpoint (REAL, range 0..1000) must retain tagBounds in ToJson output.
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var json   = policy.ToJson();
        var doc    = JsonDocument.Parse(json);

        var speedGrant = doc.RootElement.GetProperty("grants")
            .EnumerateArray()
            .Single(e => e.GetProperty("tag").GetString() == "Cmd_SpeedSetpoint");

        Assert.True(speedGrant.TryGetProperty("tagBounds", out var tagBoundsEl));
        Assert.Equal(0.0,    tagBoundsEl.GetProperty("min").GetDouble());
        Assert.Equal(1000.0, tagBoundsEl.GetProperty("max").GetDouble());
    }
    // ------------------------------------------------------------------ //
    //  External Access rule                                                //
    // ------------------------------------------------------------------ //

    private static string BoolTag(string name, string? externalAccess)
    {
        var attr = externalAccess is null ? "" : $" ExternalAccess=\"{externalAccess}\"";
        return $"""
              <Tag Name="{name}" TagType="Base"{attr} Constant="false" DataType="BOOL" Radix="Decimal">
                <Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data>
              </Tag>
        """;
    }

    private static L5X BuildExternalAccessProject(string tagsXml, string rungText = "NOP();") =>
        L5X.Parse($"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <Tags>
            {tagsXml}
                </Tags>
                <Programs>
                  <Program Name="MainProgram" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                    <Tags />
                    <Routines>
                      <Routine Name="MainRoutine" Type="RLL">
                        <RLLContent>
                          <Rung Number="0" Type="N"><Text><![CDATA[{rungText}]]></Text></Rung>
                        </RLLContent>
                      </Routine>
                    </Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """);

    [Theory]
    [InlineData("Read Only")]
    [InlineData("None")]
    public void CommandPrefixTag_ReadOnly_DeniedExternalAccessNotReadWrite(string access)
    {
        var project = BuildExternalAccessProject(BoolTag("Cmd_Locked", access));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_Locked");
        var deny = Assert.Single(policy.Denies, d => d.Tag == "Cmd_Locked");
        Assert.Equal(DenyReason.ExternalAccessNotReadWrite, deny.Reason);
        Assert.Equal(
            $"Tag 'Cmd_Locked' has External Access '{access}'; an engineer grants writes by " +
            "setting it to Read/Write in Logix Designer",
            deny.Provenance);
    }

    [Fact]
    public void CommandPrefixTag_ExternalAccessMissing_TreatedAsReadWrite_Granted()
    {
        // A missing ExternalAccess attribute is Read/Write (L5X schema and Studio default).
        var project = BuildExternalAccessProject(BoolTag("Cmd_Default", null));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Default");
        Assert.DoesNotContain(policy.Denies, d => d.Tag == "Cmd_Default");
    }

    [Fact]
    public void ProgramWrittenStillOutranksExternalAccess()
    {
        // A Read Only tag that the program also writes reports ProgramWritten, not
        // ExternalAccessNotReadWrite; the Safety_ prefix likewise outranks it.
        var project = BuildExternalAccessProject(
            BoolTag("Cmd_Driven", "Read Only") + BoolTag("Safety_Locked", "Read Only"),
            "XIC(Safety_Locked)OTE(Cmd_Driven);");

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Equal(DenyReason.ProgramWritten,
            Assert.Single(policy.Denies, d => d.Tag == "Cmd_Driven").Reason);
        Assert.Equal(DenyReason.SafetyTag,
            Assert.Single(policy.Denies, d => d.Tag == "Safety_Locked").Reason);
    }

    [Fact]
    public void NonCommandTag_ReadOnly_DeniedExternalAccessNotReadWrite_BeforeNotDesignated()
    {
        var project = BuildExternalAccessProject(BoolTag("Plain_Tag", "Read Only"));

        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Equal(DenyReason.ExternalAccessNotReadWrite,
            Assert.Single(policy.Denies, d => d.Tag == "Plain_Tag").Reason);
    }

    // ------------------------------------------------------------------ //
    //  UnparsedContent (item 2) //
    // ------------------------------------------------------------------ //

    /// <summary>A rung the tokenizer rejects (unexpected character) in an otherwise valid routine.</summary>
    private const string UnparsableRungText = "XIC(Cmd_Run)OTE(Cmd_Hidden ~~ ^^ `;";

    private static L5X UnparsableRungProject() =>
        BuildExternalAccessProject(
            BoolTag("Cmd_Hidden", null) + BoolTag("Cmd_Free", null) + BoolTag("Plain_Tag", null),
            UnparsableRungText);

    [Fact]
    public void UnparsableRung_FailsClosedAndIsReported()
    {
        var project = UnparsableRungProject();

        // Must not throw, under default prefixes and under '*'.
        var policy = GateEngine.DerivePolicy(project, GateConfig.Default);
        var wildcard = GateEngine.DerivePolicy(project, (GateConfig)GateConfigResolver.Resolve("*"));

        foreach (var p in new[] { policy, wildcard })
        {
            var entry = Assert.Single(p.Unparsed);
            Assert.Equal("MainProgram/MainRoutine[0]", entry.Location);
            Assert.StartsWith("XIC(Cmd_Run)", entry.Text);
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        }

        // Fail closed: command tags the rung might drive are UnknownWriteContext, never granted.
        foreach (var tag in new[] { "Cmd_Hidden", "Cmd_Free" })
        {
            Assert.DoesNotContain(policy.Grants, g => g.Tag == tag);
            Assert.Equal(DenyReason.UnknownWriteContext,
                Assert.Single(policy.Denies, d => d.Tag == tag).Reason);
        }
        Assert.DoesNotContain(wildcard.Grants, g => g.Tag == "Plain_Tag");
        Assert.Equal(DenyReason.UnknownWriteContext,
            Assert.Single(wildcard.Denies, d => d.Tag == "Plain_Tag").Reason);

        // Reported in the gate-report JSON: count plus the entry.
        using var doc = JsonDocument.Parse(policy.ToJson());
        Assert.Equal(1, doc.RootElement.GetProperty("unparsedContentCount").GetInt32());
        var first = doc.RootElement.GetProperty("unparsedContent")[0];
        Assert.Equal("MainProgram/MainRoutine[0]", first.GetProperty("location").GetString());
        Assert.StartsWith("XIC(Cmd_Run)", first.GetProperty("text").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("reason").GetString()));
    }

    [Fact]
    public void ParsableProject_ReportsZeroUnparsedContent()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.Empty(policy.Unparsed);
        using var doc = JsonDocument.Parse(policy.ToJson());
        Assert.Equal(0, doc.RootElement.GetProperty("unparsedContentCount").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("unparsedContent", out _));
    }

    [Fact]
    public void UnparsedContent_TextTruncatedTo200_AndReportListsFirstTen()
    {
        var longRung = "XIC(Cmd_Run)OTE(Cmd_Hidden ~~ " + new string('x', 400) + ");";
        var rungs = string.Concat(Enumerable.Range(0, 12).Select(n =>
            $"<Rung Number=\"{n}\" Type=\"N\"><Text><![CDATA[{longRung}]]></Text></Rung>"));
        var xml = $"""
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <Tags>{BoolTag("Cmd_Hidden", null)}</Tags>
                <Programs>
                  <Program Name="MainProgram" Type="Normal" TestEdits="false" Disabled="false" UseAsFolder="false" MainRoutineName="MainRoutine">
                    <Tags />
                    <Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent>{rungs}</RLLContent></Routine></Routines>
                  </Program>
                </Programs>
                <Tasks />
              </Controller>
            </RSLogix5000Content>
            """;

        var policy = GateEngine.DerivePolicy(L5X.Parse(xml), GateConfig.Default);

        Assert.Equal(12, policy.Unparsed.Count);
        Assert.All(policy.Unparsed, u => Assert.Equal(200, u.Text.Length));

        using var doc = JsonDocument.Parse(policy.ToJson());
        Assert.Equal(12, doc.RootElement.GetProperty("unparsedContentCount").GetInt32());
        Assert.Equal(10, doc.RootElement.GetProperty("unparsedContent").GetArrayLength());
    }

    [Fact]
    public async System.Threading.Tasks.Task Connect_ReportsUnparsedContentCountAndEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unparsed-{Guid.NewGuid():N}.L5X");
        UnparsableRungProject().Save(path);
        try
        {
            var projectSvc = new ProjectService();
            projectSvc.Load(path);

            var result = await ConnectHandler.ExecuteAsync(
                "198.51.100.1", "1,0", useVirtual: false, projectSvc, new TagSessionService(),
                backend: "l5x");

            var ok = Assert.IsType<ConnectResult>(result);
            Assert.Equal(1, ok.UnparsedContentCount);
            Assert.Equal("MainProgram/MainRoutine[0]", Assert.Single(ok.UnparsedContent!).Location);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ok));
            Assert.Equal(1, doc.RootElement.GetProperty("unparsedContentCount").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("unparsedContent").GetArrayLength());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScanFragment_UnparsableRung_RecordedNotThrown()
    {
        var scan = WriteClassifier.ScanFragment(
            $"""<Rung Number="3" Type="N"><Text><![CDATA[{UnparsableRungText}]]></Text></Rung>""");

        Assert.Equal("fragment[3]", Assert.Single(scan.Unparsed).Location);
    }
}
