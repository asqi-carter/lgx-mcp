using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Acceptance tests for <see cref="GatedTagService"/> — the write-gate decorator
/// that wraps any <see cref="ITagService"/> and enforces a <see cref="WriteGatePolicy"/>.
///
/// All tests run against a <see cref="VirtualTagService"/> seeded with the Demo.L5X
/// controller tags and a <see cref="WriteGatePolicy"/> derived via
/// <see cref="GateEngine.DerivePolicy"/> from the same fixture.
///
/// Demo.L5X tag summary:
///   Granted: Cmd_MotorStart (BOOL), Cmd_SpeedSetpoint (REAL, range 0..1000),
///            Sim_LineSpeed (REAL, no range), Cmd_Motor (MotorCmd UDT, member bounds)
///   Denied:  Safety_OK (SafetyTag), Motor_Run (ProgramWritten)
/// </summary>
public class GatedTagServiceTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Builds a <see cref="VirtualTagService"/> seeded with the same controller-scoped
    /// tags as Demo.L5X so acceptance tests exercise the full stack without a live PLC.
    /// </summary>
    private static VirtualTagService BuildDemoService() =>
        new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"]    = (false, "BOOL"),
            ["Cmd_SpeedSetpoint"] = (0.0,   "REAL"),
            ["Sim_LineSpeed"]     = (0.0,   "REAL"),
            ["Safety_OK"]         = (true,  "BOOL"),
            ["Motor_Run"]         = (false, "BOOL"),
            ["Cmd_Motor"]         = (0,     "MotorCmd"),
        });

    /// <summary>
    /// Derives the Demo.L5X write-gate policy using the default <see cref="GateConfig"/>.
    /// </summary>
    private static WriteGatePolicy LoadDemoPolicy()
    {
        var project = L5X.Load(FixturePath);
        return GateEngine.DerivePolicy(project, GateConfig.Default);
    }

    /// <summary>
    /// Creates a <see cref="GatedTagService"/> backed by a Demo-seeded
    /// <see cref="VirtualTagService"/> and the Demo.L5X policy.
    /// </summary>
    private static (GatedTagService gated, VirtualTagService inner) BuildGatedService()
    {
        var inner  = BuildDemoService();
        var policy = LoadDemoPolicy();
        var gated  = new GatedTagService(inner, policy);
        return (gated, inner);
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidArguments_DoesNotThrow()
    {
        var inner  = BuildDemoService();
        var policy = LoadDemoPolicy();

        var gated = new GatedTagService(inner, policy);

        Assert.NotNull(gated);
    }

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        var policy = LoadDemoPolicy();

        Assert.Throws<ArgumentNullException>(() =>
            new GatedTagService(null!, policy));
    }

    [Fact]
    public void Constructor_NullPolicy_ThrowsArgumentNullException()
    {
        var inner = BuildDemoService();

        Assert.Throws<ArgumentNullException>(() =>
            new GatedTagService(inner, null!));
    }

    [Fact]
    public void GatedTagService_ImplementsITagService()
    {
        var (gated, _) = BuildGatedService();

        Assert.IsAssignableFrom<ITagService>(gated);
    }

    // ── InnerService ─────────────────────────────────────────────────────────

    [Fact]
    public void InnerService_ReturnsWrappedService()
    {
        var inner  = BuildDemoService();
        var policy = LoadDemoPolicy();
        var gated  = new GatedTagService(inner, policy);

        Assert.Same(inner, gated.InnerService);
    }

    [Fact]
    public void InnerService_IsITagService()
    {
        var (gated, inner) = BuildGatedService();

        Assert.IsAssignableFrom<ITagService>(gated.InnerService);
    }

    // ── Pass-through: Connect / Disconnect / ListTags ─────────────────────────

    [Fact]
    public async Task ConnectAsync_ForwardsToInnerService()
    {
        var (gated, _) = BuildGatedService();

        // VirtualTagService.ConnectAsync is a no-op; must not throw.
        await gated.ConnectAsync("198.51.100.1", "1,0");
    }

    [Fact]
    public async Task DisconnectAsync_ForwardsToInnerService()
    {
        var (gated, _) = BuildGatedService();

        await gated.DisconnectAsync();
    }

    [Fact]
    public async Task ListTagsAsync_ForwardsToInnerService_ReturnsAllTags()
    {
        var (gated, _) = BuildGatedService();

        var tags = await gated.ListTagsAsync();

        Assert.NotEmpty(tags);
        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cmd_MotorStart",    names);
        Assert.Contains("Cmd_SpeedSetpoint", names);
        Assert.Contains("Sim_LineSpeed",     names);
        Assert.Contains("Safety_OK",         names);
        Assert.Contains("Motor_Run",         names);
    }

    // ── Pass-through: Read ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_GrantedTag_PassesThrough()
    {
        var (gated, _) = BuildGatedService();

        var result = await gated.ReadAsync("Cmd_MotorStart");

        Assert.NotNull(result);
        Assert.Equal("Cmd_MotorStart", result.TagName);
    }

    [Fact]
    public async Task ReadAsync_DeniedTag_StillPassesThrough()
    {
        // Read is never gated; denied tags are still readable.
        var (gated, _) = BuildGatedService();

        var result = await gated.ReadAsync("Safety_OK");

        Assert.NotNull(result);
        Assert.Equal("Safety_OK", result.TagName);
    }

    [Fact]
    public async Task ReadAsync_ProgramWrittenTag_StillPassesThrough()
    {
        // Motor_Run is denied for writes but must still be readable.
        var (gated, _) = BuildGatedService();

        var result = await gated.ReadAsync("Motor_Run");

        Assert.NotNull(result);
        Assert.Equal("Motor_Run", result.TagName);
    }

    // ── Write: granted tags (no bounds) ──────────────────────────────────────

    [Fact]
    public async Task WriteAsync_GrantedBoolTag_PassesThrough()
    {
        // Cmd_MotorStart is granted (BOOL, no range bounds).
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Cmd_MotorStart", true);

        var stored = await inner.ReadAsync("Cmd_MotorStart");
        Assert.Equal(true, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_GrantedTag_NoRangeBounds_PassesThrough()
    {
        // Sim_LineSpeed is granted (REAL) with no range annotation → unbounded grant.
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Sim_LineSpeed", 9999.9);

        var stored = await inner.ReadAsync("Sim_LineSpeed");
        Assert.Equal(9999.9, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_GrantedTag_UdtType_PassesThrough()
    {
        // Cmd_Motor is a MotorCmd UDT; write at tag level passes (member bounds
        // are not enforced at the tag level).
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Cmd_Motor", 42);

        var stored = await inner.ReadAsync("Cmd_Motor");
        Assert.Equal(42, stored.Value);
    }

    // ── Write: granted tags with scalar bounds (TagBounds) ───────────────────

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_ValueAtMin_PassesThrough()
    {
        // Cmd_SpeedSetpoint: range 0..1000. Min boundary 0 must be allowed.
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Cmd_SpeedSetpoint", 0.0);

        var stored = await inner.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal(0.0, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_ValueAtMax_PassesThrough()
    {
        // Max boundary 1000.0 must be allowed.
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Cmd_SpeedSetpoint", 1000.0);

        var stored = await inner.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal(1000.0, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_ValueInRange_PassesThrough()
    {
        // 500.0 is well within [0..1000].
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("Cmd_SpeedSetpoint", 500.0);

        var stored = await inner.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal(500.0, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_ValueAboveMax_ThrowsWriteDenied()
    {
        // 1001.0 exceeds the max bound of 1000.0 → rejected.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_SpeedSetpoint", 1001.0));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_ValueBelowMin_ThrowsWriteDenied()
    {
        // -1.0 is below the min bound of 0.0 → rejected.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_SpeedSetpoint", -1.0));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_OutOfRange_ExceptionCarriesTagName()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_SpeedSetpoint", 5000.0));

        Assert.Equal("Cmd_SpeedSetpoint", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_OutOfRange_ErrorMessageMentionsBounds()
    {
        // The rejection message must cite the bounds so the caller can act on it.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_SpeedSetpoint", 2000.0));

        // Must mention the min or max values (0 and 1000).
        Assert.Contains("1000", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0",    ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_OutOfRange_ErrorMessageMentionsProvenance()
    {
        // The rejection message must include provenance so the caller understands why.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_SpeedSetpoint", 9999.0));

        // Provenance for Cmd_SpeedSetpoint mentions "Cmd_" prefix and scalar bounds.
        Assert.Contains("Cmd_SpeedSetpoint", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_GrantedScalarTag_OutOfRange_InnerServiceNotCalled()
    {
        // When bounds are violated, the inner VirtualTagService must NOT be written.
        var (gated, inner) = BuildGatedService();

        // Value before attempted write.
        var before = await inner.ReadAsync("Cmd_SpeedSetpoint");

        try { await gated.WriteAsync("Cmd_SpeedSetpoint", 99999.0); } catch { /* expected */ }

        // Value after must remain unchanged.
        var after = await inner.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal(before.Value, after.Value);
    }

    // ── Write: denied tags (policy explicitly denies them) ───────────────────

    [Fact]
    public async Task WriteAsync_SafetyTag_ThrowsWriteDenied()
    {
        // Safety_OK is denied via SafetyTag rule.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Safety_OK", false));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_SafetyTag_ExceptionCarriesTagName()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Safety_OK", false));

        Assert.Equal("Safety_OK", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_SafetyTag_ErrorMessageMentionsDenyReasonAndProvenance()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Safety_OK", false));

        // Must mention the deny reason (SafetyTag) and the provenance prefix text.
        Assert.Contains("SafetyTag", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Safety_",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_ProgramWrittenTag_ThrowsWriteDenied()
    {
        // Motor_Run is denied via ProgramWritten rule.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_ProgramWrittenTag_ExceptionCarriesTagName()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        Assert.Equal("Motor_Run", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_ProgramWrittenTag_ErrorMessageMentionsDenyReasonAndProvenance()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        // Must mention the deny reason (ProgramWritten) and hint the provenance (OTE/rung).
        Assert.Contains("ProgramWritten", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Motor_Run",      ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_DeniedTag_InnerServiceNotCalled()
    {
        // When denied, the inner VirtualTagService must NOT be written.
        var (gated, inner) = BuildGatedService();

        var before = await inner.ReadAsync("Safety_OK");

        try { await gated.WriteAsync("Safety_OK", false); } catch { /* expected */ }

        var after = await inner.ReadAsync("Safety_OK");
        Assert.Equal(before.Value, after.Value);
    }

    // ── Write: unknown tags (not in policy at all) ────────────────────────────

    [Fact]
    public async Task WriteAsync_UnknownTag_ThrowsWriteDenied()
    {
        // A tag not in the policy (and not in the virtual store) is rejected.
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("SomeRandomTag", 1));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_UnknownTag_ExceptionCarriesTagName()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("NotInPolicy_Tag", 0));

        Assert.Equal("NotInPolicy_Tag", ex.Error.TagName);
    }

    // ── Case-insensitive tag-name matching ────────────────────────────────────

    [Fact]
    public async Task WriteAsync_GrantedTag_CaseInsensitiveMatch_PassesThrough()
    {
        // Policy and inner service both use OrdinalIgnoreCase; mixed case must work.
        var (gated, inner) = BuildGatedService();

        await gated.WriteAsync("CMD_MOTORSTART", true);

        // Read back using canonical casing.
        var stored = await inner.ReadAsync("Cmd_MotorStart");
        Assert.Equal(true, stored.Value);
    }

    [Fact]
    public async Task WriteAsync_DeniedTag_CaseInsensitiveMatch_ThrowsWriteDenied()
    {
        var (gated, _) = BuildGatedService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("safety_ok", false));

        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    // ── Motor_Run: program_written rejection with MainRoutine/rung provenance ────

    /// <summary>
    /// Acceptance test: writing Motor_Run through a GatedTagService backed by a
    /// VirtualTagService seeded from the real Demo.L5X-derived policy must be
    /// rejected with (a) WriteDenied error kind, (b) ProgramWritten deny reason,
    /// and (c) provenance that cites MainRoutine and the specific rung number.
    ///
    /// Demo.L5X rung 0 of MainRoutine contains:
    ///   XIC(Cmd_MotorStart)XIC(Safety_OK)OTE(Motor_Run);
    /// So the expected provenance is "MainProgram/MainRoutine[0] via OTE".
    /// </summary>
    [Fact]
    public async Task WriteAsync_Motor_Run_IsRejected_WithProgramWrittenReasonAndMainRoutineRungProvenance()
    {
        // Arrange: load real Demo.L5X fixture and derive policy via GateEngine
        // (no hand-built objects — the policy is fully derived from the fixture).
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Seed VirtualTagService with Motor_Run so the inner service recognises it.
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Motor_Run"] = (false, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        // Act: attempt to write Motor_Run — this must be rejected.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        // Assert (a): the operation is rejected with WriteDenied.
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);

        // Assert (b): the policy deny reason is program_written.
        var deny = policy.Denies.Single(d =>
            string.Equals(d.Tag, "Motor_Run", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);

        // Assert (c): the error message cites MainRoutine and the specific rung.
        Assert.Contains("MainRoutine", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[0]",         ex.Error.Message, StringComparison.Ordinal);
    }

    // ── Safety_OK: rejected as safety_tag ────────────────────────────────────

    /// <summary>
    /// Acceptance test: writing <c>Safety_OK</c> through a <see cref="GatedTagService"/>
    /// backed by the real Demo.L5X-derived policy must be rejected because the tag
    /// name matches the configured <c>Safety_</c> safety prefix.
    ///
    /// Expected outcomes:
    ///   (a) the write throws <see cref="TagServiceException"/> with
    ///       <see cref="TagServiceErrorKind.WriteDenied"/>;
    ///   (b) the policy deny entry for <c>Safety_OK</c> carries
    ///       <see cref="DenyReason.SafetyTag"/>;
    ///   (c) the error message cites "SafetyTag" and the "Safety_" prefix.
    /// </summary>
    [Fact]
    public async Task WriteAsync_Safety_OK_IsRejected_WithSafetyTagReason()
    {
        // Arrange: load real Demo.L5X fixture and derive policy via GateEngine.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Seed VirtualTagService with Safety_OK so the inner service recognises it.
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Safety_OK"] = (true, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        // Act: attempt to write Safety_OK — this must be rejected.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Safety_OK", false));

        // Assert (a): the operation is rejected with WriteDenied.
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);

        // Assert (b): the policy deny reason is safety_tag.
        var deny = policy.Denies.Single(d =>
            string.Equals(d.Tag, "Safety_OK", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DenyReason.SafetyTag, deny.Reason);

        // Assert (c): the error message identifies the SafetyTag reason and Safety_ prefix.
        Assert.Contains("SafetyTag", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Safety_",  ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SomeRandomTag: rejected as not_designated ─────────────────────────────

    /// <summary>
    /// Acceptance test: writing <c>SomeRandomTag</c> through a <see cref="GatedTagService"/>
    /// backed by the real Demo.L5X-derived policy must be rejected because the tag does
    /// not match any command or safety prefix and is not written by program logic.
    ///
    /// Demo.L5X includes <c>SomeRandomTag</c> as a BOOL controller tag with no prefix
    /// matching <c>Cmd_</c>, <c>Sim_</c>, or <c>Safety_</c>, so GateEngine classifies
    /// it as <see cref="DenyReason.NotDesignated"/>.
    ///
    /// Expected outcomes:
    ///   (a) the write throws <see cref="TagServiceException"/> with
    ///       <see cref="TagServiceErrorKind.WriteDenied"/>;
    ///   (b) the policy deny entry for <c>SomeRandomTag</c> carries
    ///       <see cref="DenyReason.NotDesignated"/>;
    ///   (c) the error message cites "NotDesignated".
    /// </summary>
    [Fact]
    public async Task WriteAsync_SomeRandomTag_IsRejected_WithNotDesignatedReason()
    {
        // Arrange: load real Demo.L5X fixture and derive policy via GateEngine.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Seed VirtualTagService with SomeRandomTag so the inner service recognises it.
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["SomeRandomTag"] = (false, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        // Act: attempt to write SomeRandomTag — this must be rejected.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("SomeRandomTag", 1));

        // Assert (a): the operation is rejected with WriteDenied.
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);

        // Assert (b): the policy deny reason is not_designated.
        var deny = policy.Denies.Single(d =>
            string.Equals(d.Tag, "SomeRandomTag", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DenyReason.NotDesignated, deny.Reason);

        // Assert (c): the error message identifies the NotDesignated reason.
        Assert.Contains("NotDesignated", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cmd_MotorStart: write acceptance and value readback through gate ──────

    /// <summary>
    /// Acceptance test: write <c>Cmd_MotorStart = true</c> through a
    /// <see cref="GatedTagService"/> whose policy is derived from Demo.L5X via
    /// <see cref="GateEngine.DerivePolicy"/> and assert:
    /// (a) the write is accepted without throwing, and
    /// (b) the value reads back as <c>true</c> when read through the same gate.
    ///
    /// Demo.L5X: Cmd_MotorStart is a BOOL controller tag that matches the "Cmd_"
    /// command prefix and is only consumed (XIC) by program logic, never written
    /// (OTE), so GateEngine must grant it.
    /// </summary>
    [Fact]
    public async Task Cmd_MotorStart_WriteTrue_IsAcceptedByGate_And_ReadBackThroughGate_ReturnsTrue()
    {
        // Arrange: derive policy from the real Demo.L5X fixture via GateEngine.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Seed VirtualTagService with Cmd_MotorStart starting at false.
        var inner = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"] = (false, "BOOL"),
        });
        var gated = new GatedTagService(inner, policy);

        // Act (a): write true — must not throw (write is accepted by the gate).
        await gated.WriteAsync("Cmd_MotorStart", true);

        // Act (b): read back through the gate (not the inner service directly).
        var readBack = await gated.ReadAsync("Cmd_MotorStart");

        // Assert: value returned through the gate equals true.
        Assert.Equal(true, readBack.Value);
    }

    // ── Full acceptance: all Demo.L5X granted tags can be written ─────────────

    [Fact]
    public async Task WriteAsync_AllDemoGrantedTags_AllowedWithinBounds()
    {
        var (gated, _)  = BuildGatedService();
        var policy      = LoadDemoPolicy();

        foreach (var grant in policy.Grants)
        {
            // Use 0 as a safe value — within any scalar range [0..N] and neutral for UDTs.
            // Must not throw for any granted tag.
            await gated.WriteAsync(grant.Tag, 0);
        }
    }

    [Fact]
    public async Task WriteAsync_AllDemoDeniedTags_AreRejected()
    {
        var (gated, _) = BuildGatedService();
        var policy     = LoadDemoPolicy();

        foreach (var deny in policy.Denies)
        {
            var ex = await Assert.ThrowsAsync<TagServiceException>(
                () => gated.WriteAsync(deny.Tag, 0));

            Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
        }
    }

    // ── Cmd_Motor.Speed member-path write: within bounds (0..100) ───────────────

    /// <summary>
    /// Builds a <see cref="GatedTagService"/> whose policy is derived from the real
    /// Demo.L5X fixture and whose inner <see cref="VirtualTagService"/> stores both
    /// the top-level <c>Cmd_Motor</c> UDT tag and the member path
    /// <c>Cmd_Motor.Speed</c> as separate addressable entries.
    ///
    /// This lets acceptance tests write and read back <c>Cmd_Motor.Speed</c>
    /// through the gate without a live PLC.
    /// </summary>
    private static (GatedTagService gated, VirtualTagService inner) BuildCmdMotorSpeedService()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);
        var inner   = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["Cmd_Motor"]       = (0,   "MotorCmd"),
            ["Cmd_Motor.Speed"] = (0.0, "REAL"),
        });
        return (new GatedTagService(inner, policy), inner);
    }

    /// <summary>
    /// Acceptance test — within-bounds sub-case (a):
    /// Writing <c>Cmd_Motor.Speed = 50</c> through the gate must be accepted without
    /// throwing. Value 50 is inside the bounds [0..100] declared on the
    /// <c>Speed</c> member of the <c>MotorCmd</c> UDT in Demo.L5X.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value50_WithinBounds_IsAccepted()
    {
        // Arrange: real GateEngine policy from Demo.L5X; VirtualTagService with member path.
        var (gated, _) = BuildCmdMotorSpeedService();

        // Act + Assert: write 50 — must not throw (gate accepts the in-bounds value).
        await gated.WriteAsync("Cmd_Motor.Speed", 50.0);
    }

    /// <summary>
    /// Acceptance test — within-bounds sub-case (b):
    /// After writing <c>Cmd_Motor.Speed = 50</c> the value must read back at 50
    /// when retrieved through the same gate.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value50_WithinBounds_IsReadableAt50ThroughGate()
    {
        // Arrange
        var (gated, _) = BuildCmdMotorSpeedService();

        // Act
        await gated.WriteAsync("Cmd_Motor.Speed", 50.0);
        var readBack = await gated.ReadAsync("Cmd_Motor.Speed");

        // Assert: the gate passes reads through unchanged; value must be 50.
        Assert.Equal(50.0, readBack.Value);
    }

    // ── Cmd_Motor.Speed member-path write: out of bounds (0..100) ───────────────

    /// <summary>
    /// Acceptance test — out-of-bounds sub-case (a):
    /// Writing <c>Cmd_Motor.Speed = 150</c> must be rejected because 150 exceeds
    /// the upper bound of 100 declared on the <c>Speed</c> member of <c>MotorCmd</c>.
    ///
    /// Expected outcomes:
    ///   (a) the write throws <see cref="TagServiceException"/> with
    ///       <see cref="TagServiceErrorKind.WriteDenied"/>;
    ///   (b) the error message cites the bounds [0..100];
    ///   (c) the error message references the <c>MotorCmd</c> UDT (from provenance);
    ///   (d) the stored value for <c>Cmd_Motor.Speed</c> remains unchanged at 50.
    /// </summary>
    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value150_OutOfBounds_IsRejectedWithWriteDenied()
    {
        // Arrange: pre-write 50 so the stored value is known; then attempt 150.
        var (gated, _) = BuildCmdMotorSpeedService();
        await gated.WriteAsync("Cmd_Motor.Speed", 50.0);

        // Act
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_Motor.Speed", 150.0));

        // Assert (a): the write is rejected as WriteDenied.
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value150_OutOfBounds_ErrorCitesBounds_0_To_100()
    {
        // Arrange
        var (gated, _) = BuildCmdMotorSpeedService();

        // Act
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_Motor.Speed", 150.0));

        // Assert (b): the rejection message must cite the numeric bounds.
        Assert.Contains("0",   ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value150_OutOfBounds_ErrorProvenanceReferencesUdt_MotorCmd()
    {
        // Arrange
        var (gated, _) = BuildCmdMotorSpeedService();

        // Act
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Cmd_Motor.Speed", 150.0));

        // Assert (c): the error message must reference the UDT name (MotorCmd) so
        // the caller can trace the bounds back to the UDT member definition.
        Assert.Contains("MotorCmd", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_CmdMotorSpeed_Value150_OutOfBounds_StoredValueRemains50()
    {
        // Arrange: write 50 first so the stored value is known.
        var (gated, inner) = BuildCmdMotorSpeedService();
        await gated.WriteAsync("Cmd_Motor.Speed", 50.0);

        // Act: attempt the out-of-bounds write of 150 (expected to be rejected).
        try { await gated.WriteAsync("Cmd_Motor.Speed", 150.0); } catch { /* expected */ }

        // Assert (d): the stored value must still be 50 — the inner service must not
        // have been written when the gate rejected the out-of-bounds value.
        var stored = await inner.ReadAsync("Cmd_Motor.Speed");
        Assert.Equal(50.0, stored.Value);
    }
}
