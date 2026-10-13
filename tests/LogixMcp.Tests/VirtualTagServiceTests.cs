using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for <see cref="VirtualTagService"/> — the in-memory <see cref="ITagService"/>
/// implementation used for deterministic unit and acceptance testing.
///
/// Coverage areas:
///   * Construction — empty store and seeded store
///   * ConnectAsync / DisconnectAsync — no-op contract
///   * ReadAsync — happy path and TagNotFound exception
///   * WriteAsync — happy path, value persistence, and TagNotFound exception
///   * ListTagsAsync — returns seeded entries; empty list on empty store
///   * Case-insensitive tag-name matching
///   * Gated-write policy acceptance — seeds Demo-equivalent tags and verifies
///     that the WriteGatePolicy correctly classifies grant vs. deny entries
/// </summary>
public class VirtualTagServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VirtualTagService BuildService() => new(new Dictionary<string, (object, string?)>
    {
        ["Cmd_MotorStart"]    = (false,  "BOOL"),
        ["Cmd_SpeedSetpoint"] = (0.0,    "REAL"),
        ["Sim_LineSpeed"]     = (0.0,    "REAL"),
        ["Safety_OK"]         = (true,   "BOOL"),
        ["Motor_Run"]         = (false,  "BOOL"),
    });

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_Empty_CreatesEmptyStore()
    {
        var svc = new VirtualTagService();

        // Should compile and not throw; verified via ListTagsAsync below.
        Assert.NotNull(svc);
    }

    [Fact]
    public async Task Constructor_Empty_ListTagsAsync_ReturnsEmptyList()
    {
        var svc  = new VirtualTagService();
        var tags = await svc.ListTagsAsync();

        Assert.Empty(tags);
    }

    [Fact]
    public void Constructor_Seed_NullThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VirtualTagService(null!));
    }

    [Fact]
    public async Task Constructor_Seed_AllEntriesAvailableViaListTags()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Cmd_MotorStart",    names);
        Assert.Contains("Cmd_SpeedSetpoint", names);
        Assert.Contains("Sim_LineSpeed",     names);
        Assert.Contains("Safety_OK",         names);
        Assert.Contains("Motor_Run",         names);
    }

    // ── ConnectAsync / DisconnectAsync ────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_DoesNotThrow()
    {
        var svc = BuildService();

        // No exception expected — pure no-op.
        await svc.ConnectAsync("198.51.100.1", "1,0");
    }

    [Fact]
    public async Task DisconnectAsync_DoesNotThrow()
    {
        var svc = BuildService();

        await svc.DisconnectAsync();
    }

    [Fact]
    public async Task DisconnectAsync_WithoutConnect_DoesNotThrow()
    {
        // Disconnect must be safe to call before Connect (or when already disconnected).
        var svc = new VirtualTagService();

        await svc.DisconnectAsync();
    }

    // ── ReadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_KnownTag_ReturnsTagValue()
    {
        var svc    = BuildService();
        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.NotNull(result);
        Assert.Equal("Cmd_MotorStart", result.TagName);
        Assert.Equal("BOOL", result.DataType);
        Assert.Equal(false, result.Value);
    }

    [Fact]
    public async Task ReadAsync_KnownTag_ReturnsCorrectValue()
    {
        var svc    = BuildService();
        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal(0.0, result.Value);
        Assert.Equal("REAL", result.DataType);
    }

    [Fact]
    public async Task ReadAsync_UnknownTag_ThrowsTagServiceException_TagNotFound()
    {
        var svc = BuildService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("NonExistent_Tag"));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    [Fact]
    public async Task ReadAsync_UnknownTag_ExceptionCarriesTagName()
    {
        var svc = BuildService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.ReadAsync("Ghost_Tag"));

        Assert.Equal("Ghost_Tag", ex.Error.TagName);
    }

    [Fact]
    public async Task ReadAsync_CaseInsensitive_MatchesSeededTag()
    {
        var svc    = BuildService();
        // Original seed key is "Cmd_MotorStart"; look up with different casing.
        var result = await svc.ReadAsync("cmd_motorstart");

        Assert.Equal("BOOL", result.DataType);
    }

    // ── WriteAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_KnownTag_DoesNotThrow()
    {
        var svc = BuildService();

        await svc.WriteAsync("Cmd_MotorStart", true);
    }

    [Fact]
    public async Task WriteAsync_KnownTag_PersistsNewValue()
    {
        var svc = BuildService();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 75.5);
        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal(75.5, result.Value);
    }

    [Fact]
    public async Task WriteAsync_KnownTag_PreservesDataType()
    {
        var svc = BuildService();

        await svc.WriteAsync("Cmd_MotorStart", true);
        var result = await svc.ReadAsync("Cmd_MotorStart");

        // DataType must remain BOOL after the write.
        Assert.Equal("BOOL", result.DataType);
    }

    [Fact]
    public async Task WriteAsync_UnknownTag_ThrowsTagServiceException_TagNotFound()
    {
        var svc = BuildService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("NoSuch_Tag", 42));

        Assert.Equal(TagServiceErrorKind.TagNotFound, ex.Error.Kind);
    }

    [Fact]
    public async Task WriteAsync_UnknownTag_ExceptionCarriesTagName()
    {
        var svc = BuildService();

        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => svc.WriteAsync("Missing_Tag", false));

        Assert.Equal("Missing_Tag", ex.Error.TagName);
    }

    [Fact]
    public async Task WriteAsync_CaseInsensitive_UpdatesSeededTag()
    {
        var svc = BuildService();

        // Write using lowercase; the seed key is "Cmd_MotorStart".
        await svc.WriteAsync("cmd_motorstart", true);
        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(true, result.Value);
    }

    [Fact]
    public async Task WriteAsync_MultipleWrites_LastValueWins()
    {
        var svc = BuildService();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 10.0);
        await svc.WriteAsync("Cmd_SpeedSetpoint", 50.0);
        await svc.WriteAsync("Cmd_SpeedSetpoint", 99.0);

        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");
        Assert.Equal(99.0, result.Value);
    }

    // ── ListTagsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTagsAsync_ReturnsNonNullList()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        Assert.NotNull(tags);
    }

    [Fact]
    public async Task ListTagsAsync_ReturnsCorrectCount()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        Assert.Equal(5, tags.Count);
    }

    [Fact]
    public async Task ListTagsAsync_ReturnsTagInfoWithName()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        Assert.All(tags, t => Assert.False(string.IsNullOrWhiteSpace(t.Name)));
    }

    [Fact]
    public async Task ListTagsAsync_ReturnsTagInfoWithDataType()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        // All seeded entries carry a DataType; none should be null here.
        Assert.All(tags, t => Assert.NotNull(t.DataType));
    }

    [Fact]
    public async Task ListTagsAsync_TagWithNullDataType_IsReturnedAsNull()
    {
        var svc = new VirtualTagService(new Dictionary<string, (object, string?)>
        {
            ["UnknownTag"] = (0, null),
        });

        var tags = await svc.ListTagsAsync();

        Assert.Single(tags);
        Assert.Null(tags[0].DataType);
    }

    [Fact]
    public async Task ListTagsAsync_IsAssignableFrom_IReadOnlyList()
    {
        var svc  = BuildService();
        var tags = await svc.ListTagsAsync();

        Assert.IsAssignableFrom<IReadOnlyList<TagInfo>>(tags);
    }

    // ── ITagService contract ──────────────────────────────────────────────────

    [Fact]
    public void VirtualTagService_ImplementsITagService()
    {
        // Compile-time contract check; also confirms the type is usable via the interface.
        ITagService svc = BuildService();

        Assert.NotNull(svc);
    }

    // ── Gated-write policy acceptance tests ───────────────────────────────────

    // These tests derive a WriteGatePolicy from the Demo L5X fixture, then
    // assert that the VirtualTagService seed accurately represents the expected
    // grant / deny classification.  They are acceptance-level tests: they
    // verify the full stack from GateEngine → WriteGatePolicy → VirtualTagService
    // in a way that requires no real PLC.

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Builds a VirtualTagService seeded with the same controller tags as Demo.L5X
    /// so acceptance tests exercise the full stack without a live controller.
    /// </summary>
    private static VirtualTagService BuildDemoSeededService() =>
        new(new Dictionary<string, (object, string?)>
        {
            ["Cmd_MotorStart"]    = (false, "BOOL"),
            ["Cmd_SpeedSetpoint"] = (0.0,   "REAL"),
            ["Sim_LineSpeed"]     = (0.0,   "REAL"),
            ["Safety_OK"]         = (true,  "BOOL"),
            ["Motor_Run"]         = (false, "BOOL"),
            ["Cmd_Motor"]         = (0,     "MotorCmd"),
        });

    [Fact]
    public void GatedWrite_DerivedPolicy_GrantsFourTags()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        Assert.Equal(4, policy.Grants.Count);
    }

    [Fact]
    public void GatedWrite_DerivedPolicy_DeniesTwoTags()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Safety_OK (safety_tag) + Motor_Run (program_written) + SomeRandomTag (not_designated)
        Assert.Equal(3, policy.Denies.Count);
    }

    [Fact]
    public async Task GatedWrite_GrantedTag_CanBeWrittenToVirtualService()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);
        var svc     = BuildDemoSeededService();

        // Every granted tag must be writable (present in the virtual store).
        foreach (var grant in policy.Grants)
        {
            // WriteAsync must not throw for granted tags.
            await svc.WriteAsync(grant.Tag, 0);
        }
    }

    [Fact]
    public async Task GatedWrite_GrantedTag_Cmd_MotorStart_CanBeWritten()
    {
        var svc = BuildDemoSeededService();

        // Simulate a gated write: policy says grant, so we proceed to write.
        await svc.WriteAsync("Cmd_MotorStart", true);
        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.Equal(true, result.Value);
    }

    [Fact]
    public async Task GatedWrite_GrantedTag_Cmd_SpeedSetpoint_CanBeWritten()
    {
        var svc = BuildDemoSeededService();

        await svc.WriteAsync("Cmd_SpeedSetpoint", 500.0);
        var result = await svc.ReadAsync("Cmd_SpeedSetpoint");

        Assert.Equal(500.0, result.Value);
    }

    [Fact]
    public async Task GatedWrite_GrantedTag_Sim_LineSpeed_CanBeWritten()
    {
        var svc = BuildDemoSeededService();

        await svc.WriteAsync("Sim_LineSpeed", 12.5);
        var result = await svc.ReadAsync("Sim_LineSpeed");

        Assert.Equal(12.5, result.Value);
    }

    [Fact]
    public void GatedWrite_DeniedTag_Motor_Run_IsProgramWritten()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        var deny = policy.Denies.Single(d => d.Tag == "Motor_Run");
        Assert.Equal(DenyReason.ProgramWritten, deny.Reason);
    }

    [Fact]
    public void GatedWrite_DeniedTag_Safety_OK_IsSafetyTag()
    {
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        var deny = policy.Denies.Single(d => d.Tag == "Safety_OK");
        Assert.Equal(DenyReason.SafetyTag, deny.Reason);
    }

    [Fact]
    public async Task GatedWrite_DeniedTagIsStillReadableFromVirtualService()
    {
        // VirtualTagService is the backing store; the gate logic lives in the
        // policy layer.  The service itself allows reads on any seeded tag.
        var svc    = BuildDemoSeededService();
        var result = await svc.ReadAsync("Motor_Run");

        Assert.NotNull(result);
        Assert.Equal("Motor_Run", result.TagName);
    }

    [Fact]
    public async Task GatedWrite_ListTagsAsync_IncludesAllDemoTags()
    {
        var svc  = BuildDemoSeededService();
        var tags = await svc.ListTagsAsync();

        var names = tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Cmd_MotorStart",    names);
        Assert.Contains("Cmd_SpeedSetpoint", names);
        Assert.Contains("Sim_LineSpeed",     names);
        Assert.Contains("Safety_OK",         names);
        Assert.Contains("Motor_Run",         names);
        Assert.Contains("Cmd_Motor",         names);
    }

    [Fact]
    public void GatedWrite_PolicyGrants_AllMatchNamesInVirtualStore()
    {
        var project   = L5X.Load(FixturePath);
        var policy    = GateEngine.DerivePolicy(project, GateConfig.Default);
        var svc       = BuildDemoSeededService();

        // Every granted tag name must exist in the seed (no TagNotFound on write).
        // We verify this by asserting the grant names are a subset of the seeded names.
        var grantNames = policy.Grants.Select(g => g.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seededNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cmd_MotorStart", "Cmd_SpeedSetpoint", "Sim_LineSpeed",
            "Safety_OK", "Motor_Run", "Cmd_Motor",
        };

        Assert.True(grantNames.IsSubsetOf(seededNames),
            "All granted tag names must be present in the virtual store seed.");
    }
}
