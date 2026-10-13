using System.Text.Json;
using L5Sharp.Core;

namespace LogixMcp.Tests;

/// <summary>
/// Regression tests over <c>DemoMixedRoutines.L5X</c> — the first fixture that is
/// not RLL-only and does not restrict itself to instructions the classifier knows.
///
/// <para>
/// Both behaviours asserted here were defects discovered on 2026-07-31 by driving
/// the MCP server against real Studio 5000 v36 exports. The pre-existing 414-test
/// suite was green throughout, because every fixture before this one was ladder-only
/// and used only mapped instructions.
/// </para>
/// </summary>
public class MixedRoutineGateTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "DemoMixedRoutines.L5X");

    private static L5X LoadProject() => L5X.Load(FixturePath);

    // ------------------------------------------------------------------ //
    //  Non-RLL routines must not crash the scan                            //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// A project containing an FBD routine must derive a policy without throwing.
    /// Before the guard, enumerating <c>Routine.Rungs</c> on the FBD routine threw
    /// <c>InvalidCastException: Unable to cast L5Sharp.Core.Sheet to
    /// L5Sharp.Core.Rung</c>, taking out logix_gate_report, logix_cross_reference
    /// and logix_verify_interlock on any project with a function-block routine.
    /// </summary>
    [Fact]
    public void DerivePolicy_ProjectContainingFbdRoutine_DoesNotThrow()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.NotNull(policy);
        Assert.NotEmpty(policy.Denies);
    }

    /// <summary>
    /// The ladder routine in a mixed-routine project is still scanned — skipping
    /// non-RLL routines must not skip the RLL ones alongside them.
    /// </summary>
    [Fact]
    public void DerivePolicy_MixedRoutines_LadderRungsAreStillScanned()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var motorRun = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Equal(DenyReason.ProgramWritten, motorRun.Reason);
        Assert.Contains("MainProgram/MainRoutine[0]", motorRun.Provenance);
    }

    /// <summary>
    /// <c>logix_get_routine</c> on a function-block routine returns a structured
    /// error naming the routine type, rather than throwing.
    /// </summary>
    [Fact]
    public void GetRoutine_FbdRoutine_ReturnsErrorNamingTheRoutineType()
    {
        var svc = new ProjectService();
        svc.Load(FixturePath);

        var result = GetRoutineHandler.Execute(svc, "MainProgram", "FbdRoutine");

        var error = Assert.IsType<GetRoutineError>(result);
        Assert.Contains("FBD", error.Error);
        Assert.Contains("not ladder", error.Error);
    }

    // ------------------------------------------------------------------ //
    //  Unmapped instructions fail closed                                   //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// <c>Cmd_ScanTime</c> matches the <c>Cmd_</c> command prefix, but it is the
    /// destination operand of <c>GSV</c> — an instruction absent from the KindMap.
    /// The engine cannot prove the tag is not program-driven, so it must deny
    /// rather than grant. Granting on the name convention alone is the exact
    /// failure the write-gate exists to prevent.
    /// </summary>
    [Fact]
    public void DerivePolicy_CommandTagTouchedByUnmappedInstruction_IsDenied()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        Assert.DoesNotContain(policy.Grants, g => g.Tag == "Cmd_ScanTime");

        var deny = policy.Denies.Single(d => d.Tag == "Cmd_ScanTime");
        Assert.Equal(DenyReason.UnknownWriteContext, deny.Reason);
    }

    /// <summary>
    /// The denial cites the instruction and rung that could not be classified, so
    /// an operator can either teach the classifier or override deliberately.
    /// </summary>
    [Fact]
    public void DerivePolicy_UnknownWriteContextDeny_CitesInstructionAndLocation()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);
        var deny   = policy.Denies.Single(d => d.Tag == "Cmd_ScanTime");

        Assert.Contains("GSV", deny.Provenance);
        Assert.Contains("MainProgram/MainRoutine[1]", deny.Provenance);
        Assert.Contains("cannot prove", deny.Provenance);
    }

    /// <summary>
    /// A command tag with no unclassifiable references is still granted — the
    /// fail-closed rule must not deny everything indiscriminately.
    /// </summary>
    [Fact]
    public void DerivePolicy_CleanCommandTag_IsStillGranted()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        // Cmd_MotorStart is referenced by the FBD routine, which is not parsed, so it
        // fails closed (see NonLadderScanTests); Cmd_Untouched appears nowhere in logic.
        Assert.Contains(policy.Grants, g => g.Tag == "Cmd_Untouched");
        Assert.Equal(DenyReason.UnknownWriteContext,
            policy.Denies.Single(d => d.Tag == "Cmd_MotorStart").Reason);
    }

    // ------------------------------------------------------------------ //
    //  Serialized report shape                                            //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// <see cref="DenyReason"/> serializes as its name, matching the reason strings
    /// used in the gate's own rejection messages and in docs/DEMO.md. It previously
    /// serialized as the underlying integer.
    /// </summary>
    [Fact]
    public void ToJson_DenyReason_SerializesAsName()
    {
        var json = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default).ToJson();

        Assert.Contains("\"reason\":\"UnknownWriteContext\"", json);
        Assert.DoesNotContain("\"reason\":0", json);
        Assert.DoesNotContain("\"reason\":3", json);
    }

    /// <summary>
    /// The serialized report always carries a per-reason tally covering every deny,
    /// including those omitted by the cap.
    /// </summary>
    [Fact]
    public void ToJson_AlwaysIncludesDenyCountsForEveryReason()
    {
        var json = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default).ToJson();
        var doc  = JsonDocument.Parse(json);

        var counts = doc.RootElement.GetProperty("denyCounts");
        Assert.Equal(1, counts.GetProperty("ProgramWritten").GetInt32());
        // Cmd_ScanTime (GSV) and Cmd_MotorStart (referenced from the unparsed FBD routine).
        Assert.Equal(2, counts.GetProperty("UnknownWriteContext").GetInt32());
        Assert.Equal(1, counts.GetProperty("SafetyTag").GetInt32());
    }

    /// <summary>
    /// When the deny cap drops entries, the report says so explicitly and the tally
    /// still covers the full set. Silent truncation would read as "these are all the
    /// denies" when it is not.
    /// </summary>
    [Fact]
    public void ToJson_WhenDeniesExceedCap_TruncationIsReportedNotSilent()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var doc = JsonDocument.Parse(policy.ToJson(maxDenies: 1));

        Assert.Equal(1, doc.RootElement.GetProperty("denies").GetArrayLength());

        var truncated = doc.RootElement.GetProperty("deniesTruncated");
        Assert.Equal(1, truncated.GetProperty("shown").GetInt32());
        Assert.Equal(4, truncated.GetProperty("total").GetInt32());
        Assert.Contains("omitted", truncated.GetProperty("note").GetString());

        // The tally is unaffected by the cap.
        var counts = doc.RootElement.GetProperty("denyCounts");
        Assert.Equal(4, counts.EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    /// <summary>
    /// An uncapped report carries no truncation record.
    /// </summary>
    [Fact]
    public void ToJson_WhenNothingOmitted_NoTruncationRecord()
    {
        var policy = GateEngine.DerivePolicy(LoadProject(), GateConfig.Default);

        var doc = JsonDocument.Parse(policy.ToJson(maxDenies: -1));

        Assert.False(doc.RootElement.TryGetProperty("deniesTruncated", out _));
    }
}
