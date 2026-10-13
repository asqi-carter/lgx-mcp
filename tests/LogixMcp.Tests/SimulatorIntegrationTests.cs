using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Integration tests against the ab_server simulator
//
//  These tests are SKIPPED unless the environment variable AB_SERVER_HOST is
//  set to the IP / hostname of a running ab_server (libplctag's EtherNet/IP
//  simulator).  When the variable is present the tests connect over the wire
//  and exercise the full CIP stack.
//
//  ab_server well-known tags used below:
//    TestDINT — DINT (4 bytes, int32)
//    TestREAL — REAL (4 bytes, IEEE-754 single)
//
//  Run integration tests only:
//    dotnet test --filter Category=Integration
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Group 1 — Direct <see cref="LibPlcTagService"/> round-trip tests.
///
/// Connects to the configured ab_server simulator, reads a known tag, writes
/// a BOOL (bit-zero of a DINT) and a REAL (IEEE-754 float), then reads each
/// value back and verifies it matches what was written.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class SimulatorDirectTests : IntegrationTestBase
{
    // ── Well-known ab_server tag names ────────────────────────────────────────

    /// <summary>Standard DINT tag exposed by the ab_server simulator.</summary>
    private const string DintTag = "TestDINT";

    /// <summary>Standard REAL tag exposed by the ab_server simulator.</summary>
    private const string RealTag = "TestREAL";

    public SimulatorDirectTests(IntegrationFixture fixture) : base(fixture) { }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="LibPlcTagService"/> and connects it to the
    /// simulator using the host/path from the shared fixture.
    /// </summary>
    private async Task<LibPlcTagService> ConnectAsync()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync(Fixture.Host!, Fixture.Path);
        return svc;
    }

    // ── 1a: Read a known tag ──────────────────────────────────────────────────

    /// <summary>
    /// Reads the well-known <c>TestDINT</c> tag from the simulator and asserts
    /// that a non-null <see cref="TagValue"/> is returned without throwing.
    /// </summary>
    [SkippableFact]
    public async Task ReadAsync_KnownTag_TestDINT_ReturnsTagValue()
    {
        // Arrange
        var svc = await ConnectAsync();

        // Act
        var result = await svc.ReadAsync(DintTag);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(DintTag, result.TagName);
        Assert.NotNull(result.Value);
    }

    // ── 1b: BOOL round-trip ───────────────────────────────────────────────────

    /// <summary>
    /// Round-trip BOOL test.  A BOOL in CIP/Logix maps to bit 0 of the tag
    /// buffer.  <c>TestDINT</c> (4-byte DINT) is used as the carrier:
    /// <list type="number">
    ///   <item>Write <c>(int)0</c> to clear the DINT to a known state.</item>
    ///   <item>Write <c>true</c> (bool) — sets bit 0 via <c>SetBit(0, true)</c>.</item>
    ///   <item>Read back — expect the DINT to have bit 0 set (<c>value &amp; 1 == 1</c>).</item>
    ///   <item>Write <c>false</c> (bool) — clears bit 0 via <c>SetBit(0, false)</c>.</item>
    ///   <item>Read back — expect bit 0 cleared.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task WriteAndReadBool_True_RoundTrips_BitZeroSet()
    {
        // Arrange: clear the tag to a known zero state first.
        var svc = await ConnectAsync();
        await svc.WriteAsync(DintTag, (int)0);

        // Act: write bool true (SetBit → bit 0 = 1)
        await svc.WriteAsync(DintTag, true);
        var result = await svc.ReadAsync(DintTag);

        // Assert: bit 0 must be set; the rest of the DINT may be 0 (clean clear above).
        Assert.NotNull(result);
        var intVal = (int)result.Value;
        Assert.True((intVal & 1) == 1,
            $"Expected bit 0 to be set after writing bool true; got DINT value {intVal}.");
    }

    /// <summary>
    /// Verifies writing <c>false</c> (bool) clears bit 0 of the DINT after
    /// it has been set.
    /// </summary>
    [SkippableFact]
    public async Task WriteAndReadBool_False_RoundTrips_BitZeroCleared()
    {
        // Arrange: set bit 0 first.
        var svc = await ConnectAsync();
        await svc.WriteAsync(DintTag, (int)0);
        await svc.WriteAsync(DintTag, true);

        // Act: write false (SetBit → bit 0 = 0)
        await svc.WriteAsync(DintTag, false);
        var result = await svc.ReadAsync(DintTag);

        // Assert: bit 0 must be clear (value even, no lower bit).
        Assert.NotNull(result);
        var intVal = (int)result.Value;
        Assert.True((intVal & 1) == 0,
            $"Expected bit 0 to be cleared after writing bool false; got DINT value {intVal}.");
    }

    // ── 1c: REAL round-trip ───────────────────────────────────────────────────

    /// <summary>
    /// Round-trip REAL test.  Writes a known IEEE-754 float to <c>TestREAL</c>
    /// using <see cref="LibPlcTagService.WriteAsync"/> (which calls
    /// <c>SetFloat32</c>), then reads back.  Because <c>ReadAsync</c> returns
    /// the 4-byte buffer as an <c>int32</c> (<c>GetInt32</c>), the value is
    /// re-interpreted via <see cref="BitConverter.Int32BitsToSingle"/> to
    /// recover the original float for comparison.
    /// </summary>
    [SkippableFact]
    public async Task WriteAndReadReal_RoundTrips_ViaIeee754Reinterpret()
    {
        const float written = 3.14f;
        var svc = await ConnectAsync();

        // Act
        await svc.WriteAsync(RealTag, written);
        var result = await svc.ReadAsync(RealTag);

        // Re-interpret the 4-byte int32 payload as IEEE-754 float.
        Assert.NotNull(result);
        var readBack = BitConverter.Int32BitsToSingle((int)result.Value);

        // Allow a tiny epsilon to absorb any floating-point rounding.
        Assert.True(Math.Abs(readBack - written) < 0.001f,
            $"Expected ≈{written} after REAL round-trip; got {readBack} " +
            $"(raw int32 = {result.Value}).");
    }

    /// <summary>
    /// Verifies that writing <c>0.0f</c> to <c>TestREAL</c> reads back as
    /// the int32 representation of IEEE-754 +0.0 (all bits zero → int32 0).
    /// This is the simplest possible float round-trip check.
    /// </summary>
    [SkippableFact]
    public async Task WriteAndReadReal_Zero_RoundTrips_AsIntZero()
    {
        var svc = await ConnectAsync();

        await svc.WriteAsync(RealTag, 0.0f);
        var result = await svc.ReadAsync(RealTag);

        Assert.NotNull(result);
        Assert.Equal(0, (int)result.Value);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Group 2 — <see cref="GatedTagService"/> enforcement over the wire.
///
/// Tests that the Demo.L5X-derived <see cref="WriteGatePolicy"/> correctly gates
/// writes when the inner service is a live <see cref="LibPlcTagService"/>
/// connected to the ab_server simulator.
///
/// A lightweight spy wrapper (<see cref="CountingTagService"/>) sits between the
/// <see cref="GatedTagService"/> and <see cref="LibPlcTagService"/> so tests can
/// assert whether a write reached the wire — without relying on simulator-specific
/// tag state.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class SimulatorGateTests : IntegrationTestBase
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    public SimulatorGateTests(IntegrationFixture fixture) : base(fixture) { }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<LibPlcTagService> ConnectAsync()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync(Fixture.Host!, Fixture.Path);
        return svc;
    }

    private static WriteGatePolicy LoadDemoPolicy()
    {
        var project = L5X.Load(FixturePath);
        return GateEngine.DerivePolicy(project, GateConfig.Default);
    }

    /// <summary>
    /// Builds the full gate stack:
    ///   GatedTagService (Demo.L5X policy)
    ///     └─ CountingTagService (spy)
    ///          └─ LibPlcTagService (connected to simulator)
    /// </summary>
    private async Task<(GatedTagService Gated, CountingTagService Spy, LibPlcTagService Inner)>
        BuildGateStackAsync()
    {
        var inner  = await ConnectAsync();
        var spy    = new CountingTagService(inner);
        var policy = LoadDemoPolicy();
        var gated  = new GatedTagService(spy, policy);
        return (gated, spy, inner);
    }

    // ── 2a: Granted write forwards to the spy (reaches the wire) ─────────────

    /// <summary>
    /// Asserts that writing a granted tag (<c>Cmd_MotorStart</c>) through
    /// <see cref="GatedTagService"/> forwards the call to the inner service
    /// (verified by <see cref="CountingTagService.WriteCount"/> == 1).
    ///
    /// The write may fail at the simulator level (the ab_server does not carry
    /// Demo.L5X tags) — a <see cref="TagServiceException"/> whose kind is NOT
    /// <see cref="TagServiceErrorKind.WriteDenied"/> is treated as an expected
    /// simulator-missing-tag error and does not invalidate the spy assertion.
    /// </summary>
    [SkippableFact]
    public async Task GrantedWrite_CmdMotorStart_ForwardsToInnerService_SpyCountIsOne()
    {
        // Arrange
        var (gated, spy, _) = await BuildGateStackAsync();

        // Act: write a Demo.L5X-granted tag through the gate.
        // The gate must allow it, even if the simulator returns TagNotFound.
        try
        {
            await gated.WriteAsync("Cmd_MotorStart", true);
        }
        catch (TagServiceException ex)
            when (ex.Error.Kind != TagServiceErrorKind.WriteDenied)
        {
            // Acceptable: write reached the inner service but the simulator
            // does not carry the tag.  The spy count is what matters.
        }

        // Assert: the gate forwarded the write call → spy recorded 1 call.
        Assert.Equal(1, spy.WriteCount);
    }

    // ── 2b: Denied write (Motor_Run) is rejected BEFORE wire traffic ─────────

    /// <summary>
    /// Asserts that writing <c>Motor_Run</c> (denied by the Demo.L5X policy as
    /// <c>ProgramWritten</c>) is rejected by the gate BEFORE the call is
    /// forwarded to the inner service.
    ///
    /// The test verifies two things:
    /// (a) a <see cref="TagServiceException"/> with
    ///     <see cref="TagServiceErrorKind.WriteDenied"/> is thrown;
    /// (b) the <see cref="CountingTagService.WriteCount"/> remains 0,
    ///     confirming that no wire traffic was generated.
    /// </summary>
    [SkippableFact]
    public async Task DeniedWrite_MotorRun_IsRejectedBeforeWireTraffic_SpyCountIsZero()
    {
        // Arrange
        var (gated, spy, _) = await BuildGateStackAsync();

        // Act: attempt to write Motor_Run — must be rejected with WriteDenied.
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", true));

        // Assert (a): correct error kind.
        Assert.Equal(TagServiceErrorKind.WriteDenied, ex.Error.Kind);

        // Assert (b): spy saw zero write calls — gate rejected before forwarding.
        Assert.Equal(0, spy.WriteCount);
    }

    /// <summary>
    /// Corroborates that the <c>Motor_Run</c> denial carries the
    /// <c>ProgramWritten</c> deny reason so callers have machine-readable
    /// provenance.
    /// </summary>
    [SkippableFact]
    public async Task DeniedWrite_MotorRun_ErrorMessageContainsProgramWrittenProvenance()
    {
        // Arrange
        var (gated, _, _) = await BuildGateStackAsync();

        // Act
        var ex = await Assert.ThrowsAsync<TagServiceException>(
            () => gated.WriteAsync("Motor_Run", false));

        // Assert: the error message must cite ProgramWritten reason and the
        // routine/rung provenance from Demo.L5X MainRoutine[0].
        Assert.Contains("ProgramWritten", ex.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MainRoutine",    ex.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Group 3 — <see cref="PollTagsHandler"/> delta detection over the wire.
///
/// Writes an initial value to <c>TestDINT</c>, then starts a two-cycle poll
/// (with a 300 ms inter-cycle interval).  While the poll waits between cycles
/// a concurrent task updates <c>TestDINT</c> to a different value.  The test
/// asserts that cycle 2's change list correctly reports the old and new values.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class SimulatorPollTests : IntegrationTestBase
{
    /// <summary>Standard DINT tag used for poll delta testing.</summary>
    private const string DintTag = "TestDINT";

    /// <summary>Initial value written before the poll starts.</summary>
    private const int InitialValue = 42;

    /// <summary>Updated value written between cycle 1 and cycle 2.</summary>
    private const int UpdatedValue = 99;

    /// <summary>
    /// Milliseconds to wait between poll cycles.  Must be large enough for
    /// the mid-cycle write to land before cycle 2 reads.
    /// </summary>
    private const int PollIntervalMs = 300;

    /// <summary>
    /// Milliseconds after the poll starts before the value is changed.
    /// Must be less than <see cref="PollIntervalMs"/> and greater than the
    /// typical cycle-1 read latency.
    /// </summary>
    private const int WriteDelayMs = 150;

    public SimulatorPollTests(IntegrationFixture fixture) : base(fixture) { }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<(LibPlcTagService Svc, TagSessionService Session)> ConnectAsync()
    {
        var svc = new LibPlcTagService();
        await svc.ConnectAsync(Fixture.Host!, Fixture.Path);
        var session = new TagSessionService();
        session.Attach(svc, isVirtual: false);
        return (svc, session);
    }

    // ── 3a: Delta detected when tag changes between cycles ───────────────────

    /// <summary>
    /// Full integration test for <see cref="PollTagsHandler"/> delta reporting:
    /// <list type="number">
    ///   <item>Write <c>TestDINT = 42</c> as the baseline.</item>
    ///   <item>Start a 2-cycle poll (interval = 300 ms).</item>
    ///   <item>After 150 ms (mid-interval), update <c>TestDINT = 99</c>.</item>
    ///   <item>Assert cycle 1 contains <c>TestDINT</c> with current = 42.</item>
    ///   <item>Assert cycle 2 contains exactly one change:
    ///     previous = 42, current = 99.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task Poll_TagChangedBetweenCycles_DeltaIsReportedInCycleTwo()
    {
        // Arrange: connect and prime TestDINT with the initial value.
        var (svc, session) = await ConnectAsync();
        await svc.WriteAsync(DintTag, InitialValue);

        // Launch the concurrent write that will fire between cycle 1 and cycle 2.
        var midCycleWrite = Task.Run(async () =>
        {
            await Task.Delay(WriteDelayMs);
            await svc.WriteAsync(DintTag, UpdatedValue);
        });

        // Act: poll 2 cycles with 300 ms interval.  Cycle 1 will observe the
        // initial value; cycle 2 will observe the updated value.
        var raw = await PollTagsHandler.ExecuteAsync(
            tags:       new[] { DintTag },
            count:      2,
            intervalMs: PollIntervalMs,
            session:    session);

        // Ensure the background write completed without throwing.
        await midCycleWrite;

        // Assert: result must be a PollResult (not a PollError).
        var result = Assert.IsType<PollResult>(raw);
        Assert.Equal(2, result.Cycles.Count);

        // ── Cycle 1: baseline — no previous value, current = InitialValue ────
        var cycle1 = result.Cycles[0];
        Assert.Equal(1, cycle1.Cycle);
        Assert.Single(cycle1.Changes);
        var baseline = cycle1.Changes[0];
        Assert.Equal(DintTag,      baseline.Tag);
        Assert.Null(baseline.Previous);
        Assert.Equal(InitialValue, (int)baseline.Current);

        // ── Cycle 2: delta — previous = InitialValue, current = UpdatedValue ─
        var cycle2 = result.Cycles[1];
        Assert.Equal(2, cycle2.Cycle);
        Assert.True(cycle2.Changes.Count == 1,
            $"Expected exactly one changed tag in cycle 2; got {cycle2.Changes.Count}.");
        var delta = cycle2.Changes[0];
        Assert.Equal(DintTag,      delta.Tag);
        Assert.Equal(InitialValue, (int)delta.Previous!);
        Assert.Equal(UpdatedValue, (int)delta.Current);
    }

    /// <summary>
    /// Verifies that when the tag value does NOT change between cycles, cycle 2
    /// contains no changes (empty delta).
    /// </summary>
    [SkippableFact]
    public async Task Poll_TagUnchangedBetweenCycles_CycleTwoHasNoDelta()
    {
        // Arrange
        var (svc, session) = await ConnectAsync();
        await svc.WriteAsync(DintTag, InitialValue);

        // Act: poll 2 cycles — no intervening write.
        var raw = await PollTagsHandler.ExecuteAsync(
            tags:       new[] { DintTag },
            count:      2,
            intervalMs: PollIntervalMs,
            session:    session);

        // Assert
        var result = Assert.IsType<PollResult>(raw);
        Assert.Equal(2, result.Cycles.Count);

        // Cycle 2 must have no changes (value stayed the same).
        var cycle2 = result.Cycles[1];
        Assert.Empty(cycle2.Changes);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A lightweight spy decorator for <see cref="ITagService"/> that counts how
/// many times <see cref="WriteAsync"/> has been called.  All operations are
/// forwarded to the wrapped inner service.
///
/// Used by <see cref="SimulatorGateTests"/> to assert whether a write passed
/// through the <see cref="GatedTagService"/> (count &gt; 0) or was rejected
/// before reaching the wire (count == 0).
/// </summary>
internal sealed class CountingTagService : ITagService
{
    private readonly ITagService _inner;
    private int _writeCount;

    /// <summary>
    /// Number of times <see cref="WriteAsync"/> has been called on this spy.
    /// </summary>
    public int WriteCount => _writeCount;

    /// <summary>
    /// Initialises the spy, wrapping <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The service to forward all calls to.</param>
    public CountingTagService(ITagService inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    public Task ConnectAsync(string gateway, string path,
        CancellationToken cancellationToken = default)
        => _inner.ConnectAsync(gateway, path, cancellationToken);

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => _inner.DisconnectAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<TagValue> ReadAsync(string tagName,
        CancellationToken cancellationToken = default)
        => _inner.ReadAsync(tagName, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
        CancellationToken cancellationToken = default)
        => _inner.ListTagsAsync(cancellationToken);

    /// <summary>
    /// Increments <see cref="WriteCount"/>, then forwards the write to the
    /// wrapped service.
    /// </summary>
    public Task WriteAsync(string tagName, object value,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _writeCount);
        return _inner.WriteAsync(tagName, value, cancellationToken);
    }
}
