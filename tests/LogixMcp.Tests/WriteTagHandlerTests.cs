#if LOGIX_SDK
using RockwellAutomation.LogixDesigner;
#endif
using L5Sharp.Core;
using Task = System.Threading.Tasks.Task;

namespace LogixMcp.Tests;

/// <summary>
/// Tests for the <see cref="WriteTagHandler.ExecuteAsync"/> tool-layer error path.
///
/// <para>
/// Exercises the full stack: real <see cref="GateEngine"/>/<c>Demo.L5X</c> policy →
/// <see cref="GatedTagService"/> → <see cref="TagSessionService"/> →
/// <see cref="WriteTagHandler.ExecuteAsync"/>.
/// </para>
///
/// <para>
/// All tests in this class target the rejection path.  Each verifies three
/// invariants required for correct MCP integration:
/// <list type="bullet">
///   <item>(a) <see cref="WriteTagHandler.ExecuteAsync"/> does not throw — gate
///         rejections must never surface as unhandled exceptions to the MCP host.</item>
///   <item>(b) The return value is not a <see cref="WriteTagResult"/> success envelope.</item>
///   <item>(c) The return value is a <see cref="WriteTagError"/> that the MCP caller
///         can consume programmatically (non-null <see cref="WriteTagError.Error"/>,
///         non-null <see cref="WriteTagError.Kind"/> matching the rejection category).</item>
/// </list>
/// </para>
///
/// Demo.L5X tag summary used for fixture construction:
///   Granted: Cmd_MotorStart (BOOL), Cmd_SpeedSetpoint (REAL, range 0..1000),
///            Sim_LineSpeed (REAL, no range), Cmd_Motor (MotorCmd UDT, member bounds)
///   Denied:  Safety_OK (SafetyTag), Motor_Run (ProgramWritten)
/// </summary>
public class WriteTagHandlerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Demo.L5X");

    /// <summary>
    /// Builds a <see cref="VirtualTagService"/> seeded with the controller-scoped
    /// tags from Demo.L5X so the inner service recognises all tag names.
    /// </summary>
    private static VirtualTagService BuildDemoVirtualService() =>
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
    /// Derives the Demo.L5X write-gate policy using the default
    /// <see cref="GateConfig"/> via the real <see cref="GateEngine"/>.
    /// </summary>
    private static WriteGatePolicy LoadDemoPolicy()
    {
        var project = L5X.Load(FixturePath);
        return GateEngine.DerivePolicy(project, GateConfig.Default);
    }

    /// <summary>
    /// Returns a connected <see cref="TagSessionService"/> backed by a
    /// <see cref="GatedTagService"/> that enforces the Demo.L5X policy over
    /// a Demo-seeded <see cref="VirtualTagService"/>.
    /// </summary>
    private static TagSessionService BuildConnectedSession()
    {
        var inner   = BuildDemoVirtualService();
        var policy  = LoadDemoPolicy();
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);
        return session;
    }

    // ── No active session ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithNoActiveSession_ReturnsWriteTagError_NotAnException()
    {
        // Arrange: session with no service attached.
        var session = new TagSessionService();

        // Act: must NOT throw.
        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "1", session);

        // Assert (c): structured error result consumable by the MCP caller.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.NotNull(error.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoActiveSession_ResultIsNotSuccessEnvelope()
    {
        var session = new TagSessionService();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "1", session);

        // Assert (b): not a success envelope.
        Assert.IsNotType<WriteTagResult>(result);
    }

    // ── SafetyTag rejection path ──────────────────────────────────────────────

    /// <summary>
    /// Writing <c>Safety_OK</c> (denied via <see cref="DenyReason.SafetyTag"/> by
    /// the real GateEngine/Demo.L5X policy) must not throw.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_DoesNotThrow()
    {
        var session = BuildConnectedSession();

        // Act: must NOT throw — gate rejections must be returned, not raised.
        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_ReturnsWriteTagError_NotSuccessEnvelope()
    {
        // Assert (b): not a WriteTagResult success envelope.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        Assert.IsNotType<WriteTagResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_ReturnsStructuredWriteTagError()
    {
        // Assert (c): structured error result consumable by the MCP caller.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.NotNull(error.Error);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_ErrorKindIsWriteDenied()
    {
        // Assert (c): Kind field enables programmatic handling without string parsing.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_ErrorCarriesTagName()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal("Safety_OK", error.Tag);
    }

    // ── ProgramWritten rejection path ─────────────────────────────────────────

    /// <summary>
    /// Writing <c>Motor_Run</c> (denied via <see cref="DenyReason.ProgramWritten"/> by
    /// the real GateEngine/Demo.L5X policy) must not throw.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_DoesNotThrow()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_ReturnsWriteTagError_NotSuccessEnvelope()
    {
        // Assert (b): not a WriteTagResult success envelope.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        Assert.IsNotType<WriteTagResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_ReturnsStructuredWriteTagError()
    {
        // Assert (c): structured error result consumable by the MCP caller.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.NotNull(error.Error);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_ErrorKindIsWriteDenied()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_ErrorCarriesTagName()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal("Motor_Run", error.Tag);
    }

    // ── NotDesignated rejection path (tag not in grant list) ─────────────────

    [Fact]
    public async Task ExecuteAsync_GateDenied_NotDesignatedTag_DoesNotThrow()
    {
        // A tag not in the grant list (e.g. a raw status tag with no Cmd_/Sim_ prefix)
        // must be rejected as structured error, not thrown as exception.
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Sim_LineSpeed_Status", "42", session);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_NotDesignatedTag_ReturnsWriteTagError_NotSuccessEnvelope()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Sim_LineSpeed_Status", "42", session);

        Assert.IsNotType<WriteTagResult>(result);
    }

    [Fact]
    public async Task ExecuteAsync_GateDenied_NotDesignatedTag_ReturnsStructuredWriteTagError()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Sim_LineSpeed_Status", "42", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.NotNull(error.Error);
    }

    // ── Post-write readback: success path ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WriteGranted_ObservedPopulatedFromReadback()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "42.0", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.NotNull(success.Observed);
    }

    [Fact]
    public async Task ExecuteAsync_WriteGranted_DataTypePopulatedFromReadback()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "42.0", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Equal("REAL", success.DataType);
    }

    [Fact]
    public async Task ExecuteAsync_WriteGranted_VerifiedTrueWhenControllerEchosWrittenValue()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "42.0", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.True(success.Verified);
    }

    [Fact]
    public async Task ExecuteAsync_WriteGranted_WrittenMatchesParsedInput()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Equal(true, success.Written);
    }

    [Fact]
    public async Task ExecuteAsync_WriteGranted_BoolTag_VerifiedTrue()
    {
        var session = BuildConnectedSession();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.True(success.Verified);
        Assert.Equal("BOOL", success.DataType);
    }

    // ── Post-write readback: readback failure path ────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WriteSucceeds_ReadbackFails_ResultIsWriteTagResult()
    {
        // Write itself succeeded, so even a readback failure must surface as
        // WriteTagResult (status "ok") — never as WriteTagError.
        var inner   = new ThrowOnReadTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("AnyTag", "1", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Equal("ok", success.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WriteSucceeds_ReadbackFails_ObservedIsNull()
    {
        var inner   = new ThrowOnReadTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("AnyTag", "1", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Null(success.Observed);
    }

    [Fact]
    public async Task ExecuteAsync_WriteSucceeds_ReadbackFails_VerifiedIsNull()
    {
        var inner   = new ThrowOnReadTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("AnyTag", "1", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Null(success.Verified);
    }

    [Fact]
    public async Task ExecuteAsync_WriteSucceeds_ReadbackFails_VerificationErrorIsSet()
    {
        var inner   = new ThrowOnReadTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("AnyTag", "1", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.NotNull(success.VerificationError);
    }

    [Fact]
    public async Task ExecuteAsync_WriteSucceeds_ReadbackFails_WrittenIsPreserved()
    {
        var inner   = new ThrowOnReadTagService();
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("AnyTag", "99", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.Equal(99, success.Written);
    }

    // ── ValuesMatch unit tests ────────────────────────────────────────────────

    [Theory]
    [InlineData(true,  true,  true)]
    [InlineData(false, false, true)]
    [InlineData(true,  false, false)]
    public void ValuesMatch_Bool_ExactEquality(bool written, bool observed, bool expected)
    {
        Assert.Equal(expected, WriteTagHandler.ValuesMatch(written, observed, "BOOL"));
    }

    [Theory]
    [InlineData(42, 42, true)]
    [InlineData(42, 43, false)]
    public void ValuesMatch_Int_ExactEquality(int written, int observed, bool expected)
    {
        Assert.Equal(expected, WriteTagHandler.ValuesMatch(written, observed, "DINT"));
    }

    [Fact]
    public void ParseValue_LargeIntegerStrings_StayExact()
    {
        Assert.Equal(9007199254740993L, WriteTagHandler.ParseValue("9007199254740993"));
        Assert.Equal(long.MaxValue, WriteTagHandler.ParseValue("9223372036854775807"));
        Assert.Equal(ulong.MaxValue, WriteTagHandler.ParseValue("18446744073709551615"));
        Assert.Equal(long.MinValue, WriteTagHandler.ParseValue("-9223372036854775808"));
    }

    [Fact]
    public void ValuesMatch_LargeLong_ComparedExactly()
    {
        Assert.True(WriteTagHandler.ValuesMatch(9007199254740993L, 9007199254740993L, "LINT"));
        Assert.False(WriteTagHandler.ValuesMatch(9007199254740993L, 9007199254740992L, "LINT"));
        Assert.True(WriteTagHandler.ValuesMatch(ulong.MaxValue, ulong.MaxValue, "ULINT"));
    }

    [Fact]
    public void ValuesMatch_IntWritten_SintObserved_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(5, (sbyte)5, "SINT"));
        Assert.False(WriteTagHandler.ValuesMatch(5, (sbyte)6, "SINT"));
    }

    [Fact]
    public void ValuesMatch_IntWritten_FloatObserved_SameValue_ReturnsTrue()
    {
        // Writing an int value (e.g. "42") to a REAL tag: parsedValue is int, readback
        // is float.  With dataType "REAL", the epsilon comparison converts both to double.
        Assert.True(WriteTagHandler.ValuesMatch(42, (float)42.0f, "REAL"));
    }

    [Fact]
    public void ValuesMatch_DoubleWithinEpsilon_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(3.14, 3.14 + 1e-6, "REAL"));
    }

    [Fact]
    public void ValuesMatch_DoubleOutsideEpsilon_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch(3.14, 4.0, "REAL"));
    }

    [Fact]
    public void ValuesMatch_StringEquality_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch("hello", "hello", null));
    }

    [Fact]
    public void ValuesMatch_StringInequality_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch("hello", "world", null));
    }

    // ── Private test doubles ──────────────────────────────────────────────────

    /// <summary>
    /// Test stub that always succeeds on <see cref="ITagService.WriteAsync"/> but
    /// always throws on <see cref="ITagService.ReadAsync"/>, exercising the
    /// readback-failure path in <see cref="WriteTagHandler.ExecuteAsync"/>.
    /// </summary>
    private sealed class ThrowOnReadTagService : ITagService
    {
        public Task ConnectAsync(string gateway, string path,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(string tagName, object value,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated readback failure");

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TagInfo>>(Array.Empty<TagInfo>());
    }

    // ── hint / docs_anchor optional fields ───────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="WriteTagError.Hint"/> is non-empty when a
    /// gate-denied write produces a <see cref="TagServiceErrorKind.WriteDenied"/>
    /// error.  The hint must name the write-gate restriction and guide the caller
    /// to discover write-granted tags via <c>logix_gate_report</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_WriteDenied_HintIsNonEmpty()
    {
        // Arrange: a connected session backed by the real GateEngine/Demo.L5X policy.
        // Safety_OK is denied via DenyReason.SafetyTag — any WriteDenied rejection works.
        var session = BuildConnectedSession();

        // Act: write to a denied tag.
        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        // Assert: error kind is WriteDenied and Hint is non-empty.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.NotNull(error.Hint);
        Assert.NotEmpty(error.Hint);
    }

    /// <summary>
    /// Verifies that <see cref="WriteTagError.Hint"/> defaults to <c>null</c> so that
    /// the new field is backward-compatible with existing error paths that carry no hint.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNoActiveSession_HintDefaultsToNull()
    {
        var session = new TagSessionService();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Null(error.Hint);
    }

    /// <summary>
    /// Verifies that <see cref="WriteTagError.DocsAnchor"/> defaults to <c>null</c> so
    /// that the new field is backward-compatible with existing error paths.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithNoActiveSession_DocsAnchorDefaultsToNull()
    {
        var session = new TagSessionService();

        var result = await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Null(error.DocsAnchor);
    }

    /// <summary>
    /// Verifies that <see cref="WriteTagError"/> can be constructed with explicit
    /// <see cref="WriteTagError.Hint"/> and <see cref="WriteTagError.DocsAnchor"/> values
    /// and that both are preserved and accessible.
    /// </summary>
    [Fact]
    public void WriteTagError_HintAndDocsAnchor_RoundTrip()
    {
        var err = new WriteTagError(
            Error:      "Something went wrong.",
            Kind:       "WriteDenied",
            Tag:        "MyTag",
            Hint:       "Check that the tag is designated writable.",
            DocsAnchor: "write-tag-errors");

        Assert.Equal("Check that the tag is designated writable.", err.Hint);
        Assert.Equal("write-tag-errors", err.DocsAnchor);
    }

    // ── Composite acceptance: real fixture, all denied tags ───────────────────

    /// <summary>
    /// Acceptance test using the real <see cref="GateEngine"/>/<c>Demo.L5X</c> fixture.
    /// Every tag in the policy deny list must return a <see cref="WriteTagError"/>
    /// when written through <see cref="WriteTagHandler.ExecuteAsync"/> — no exceptions,
    /// no success envelopes.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AllDemoDeniedTags_EachReturnsWriteTagError_NeverThrows()
    {
        // Arrange: real policy from Demo.L5X.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        // Seed the VirtualTagService with all denied tags so the inner service won't
        // throw TagNotFound before the gate even runs (gate rejects before forwarding,
        // but we seed defensively to keep the test focused on the gate path).
        var seed = policy.Denies.ToDictionary(
            d => d.Tag,
            d => ((object)0, (string?)"BOOL"));
        var inner   = new VirtualTagService(seed);
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        foreach (var deny in policy.Denies)
        {
            // Act: must NOT throw.
            var result = await WriteTagHandler.ExecuteAsync(deny.Tag, "0", session);

            // Assert (b): not a success envelope.
            Assert.IsNotType<WriteTagResult>(result);

            // Assert (c): structured error result consumable by the MCP caller.
            var error = Assert.IsType<WriteTagError>(result);
            Assert.NotNull(error.Error);
            Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        }
    }

    // ── Provenance preservation: per-classification ───────────────────────────

    /// <summary>
    /// Verifies that writing to a <see cref="DenyReason.SafetyTag"/>-denied tag
    /// produces a <see cref="WriteTagError"/> whose <see cref="WriteTagError.Error"/>
    /// field contains the L5X provenance string recorded in the deny entry.
    ///
    /// <para>
    /// The <see cref="GatedTagService"/> embeds <c>deny.Provenance</c> verbatim in
    /// the exception message; this test asserts that <see cref="WriteTagHandler"/>
    /// preserves it through the catch boundary into the <c>Error</c> field so that
    /// the MCP caller can always trace a denial back to the policy element that
    /// triggered it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_SafetyTag_ErrorContainsDenyProvenance()
    {
        // Arrange: load the real policy to obtain the genuine provenance string.
        var policy     = LoadDemoPolicy();
        var safetyDeny = policy.Denies.Single(d => d.Tag == "Safety_OK");
        var session    = BuildConnectedSession();

        // Act.
        var result = await WriteTagHandler.ExecuteAsync("Safety_OK", "false", session);

        // Assert: the deny provenance is preserved verbatim in the error message.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains(safetyDeny.Provenance, error.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that writing to a <see cref="DenyReason.ProgramWritten"/>-denied tag
    /// produces a <see cref="WriteTagError"/> whose <see cref="WriteTagError.Error"/>
    /// field contains the L5X provenance string (rung location) recorded in the deny entry.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_ProgramWritten_ErrorContainsDenyProvenance()
    {
        // Arrange: load the real policy to obtain the genuine provenance string.
        var policy      = LoadDemoPolicy();
        var programDeny = policy.Denies.Single(d => d.Tag == "Motor_Run");
        var session     = BuildConnectedSession();

        // Act.
        var result = await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session);

        // Assert: the deny provenance is preserved verbatim in the error message.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains(programDeny.Provenance, error.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that writing to a <see cref="DenyReason.NotDesignated"/>-denied tag
    /// produces a <see cref="WriteTagError"/> whose <see cref="WriteTagError.Error"/>
    /// field contains the L5X provenance string recorded in the deny entry.
    /// Uses a synthetic policy so the exact provenance text is under test control and
    /// does not depend on a specific fixture layout.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_NotDesignated_ErrorContainsDenyProvenance()
    {
        // Arrange: synthetic policy with a single NotDesignated deny entry carrying
        // a known provenance string.
        const string tagName  = "Foo_Status";
        const string provText = "Tag 'Foo_Status' does not match any command prefix " +
                                "('Cmd_', 'Sim_') and is not safety-tagged or program-written.";

        var policy  = new WriteGatePolicy(
            Grants: Array.Empty<GrantEntry>(),
            Denies: new[] { new DenyEntry(tagName, DenyReason.NotDesignated, provText) });
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { [tagName] = (0, "BOOL") });
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        // Act.
        var result = await WriteTagHandler.ExecuteAsync(tagName, "0", session);

        // Assert: the synthetic provenance is preserved verbatim in the error message.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains(provText, error.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that writing to a <see cref="DenyReason.UnknownWriteContext"/>-denied tag
    /// produces a <see cref="WriteTagError"/> whose <see cref="WriteTagError.Error"/>
    /// field contains the L5X provenance string recorded in the deny entry.
    /// Uses a synthetic policy so the exact provenance text is under test control.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_GateDenied_UnknownWriteContext_ErrorContainsDenyProvenance()
    {
        // Arrange: synthetic policy with a single UnknownWriteContext deny entry carrying
        // a known provenance string.
        const string tagName  = "Cmd_Suspect";
        const string provText = "Tag 'Cmd_Suspect' matches command prefix 'Cmd_' but also " +
                                "appears at SomeRoutine:rung 3, whose write operands cannot " +
                                "be determined. Denied because the engine cannot prove the " +
                                "tag is not driven by program logic.";

        var policy  = new WriteGatePolicy(
            Grants: Array.Empty<GrantEntry>(),
            Denies: new[] { new DenyEntry(tagName, DenyReason.UnknownWriteContext, provText) });
        var inner   = new VirtualTagService(
            new Dictionary<string, (object, string?)> { [tagName] = (0, "BOOL") });
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        // Act.
        var result = await WriteTagHandler.ExecuteAsync(tagName, "0", session);

        // Assert: the synthetic provenance is preserved verbatim in the error message.
        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains(provText, error.Error, StringComparison.Ordinal);
    }

    // ── Composite acceptance: provenance preserved for all Demo.L5X deny entries ──

    /// <summary>
    /// Composite safety-net test: every denied tag in the real Demo.L5X policy must
    /// produce a <see cref="WriteTagError"/> whose <see cref="WriteTagError.Error"/>
    /// field contains the provenance string recorded in the corresponding deny entry.
    ///
    /// <para>
    /// This test is the catch-all for any accidental weakening of the
    /// <c>logix_write</c> immediate-write path. If the handler ever discards or
    /// replaces the exception message, the provenance assertion will fail here even
    /// when the individual per-classification tests still pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AllDemoDeniedTags_EachErrorContainsDenyProvenance()
    {
        // Arrange: real policy from Demo.L5X so all provenance strings are genuine
        // L5X derivations rather than synthetic stand-ins.
        var project = L5X.Load(FixturePath);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        var seed    = policy.Denies.ToDictionary(
            d => d.Tag,
            d => ((object)0, (string?)"BOOL"));
        var inner   = new VirtualTagService(seed);
        var gated   = new GatedTagService(inner, policy);
        var session = new TagSessionService();
        session.Attach(gated, isVirtual: true);

        foreach (var deny in policy.Denies)
        {
            // Act: must NOT throw.
            var result = await WriteTagHandler.ExecuteAsync(deny.Tag, "0", session);

            // Assert: WriteDenied result with provenance string preserved verbatim.
            var error = Assert.IsType<WriteTagError>(result);
            Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);

            // The GatedTagService builds the message as:
            //   $"Write denied for tag '{tag}': {reason} — {provenance}"
            // The provenance must be present verbatim so the caller can trace the
            // denial back to the L5X element that declared the restriction.
            Assert.Contains(deny.Provenance, error.Error, StringComparison.Ordinal);
        }
    }
    // ── WriteGateRequired: ungated live CIP sessions ─────────

    private static readonly Capabilities LiveWritesOn = new(LiveWritesEnabled: true);

    private static VirtualTagService CipStore() => new(new Dictionary<string, (object, string?)>
    {
        ["Cmd_MotorStart"] = (false, "BOOL"),
        ["Motor_Run"]      = (false, "BOOL"),
    });

    private static WriteAllowlist AllowlistOf(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "wgr-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        try { return Assert.IsType<WriteAllowlist>(WriteAllowlist.Load(path)); }
        finally { File.Delete(path); }
    }

    /// <summary>Live CIP session with no project gate and no allowlist: refused, nothing written.</summary>
    [Fact]
    public async Task CipNoProject_NoAllowlist_WriteGateRequired_NothingWritten()
    {
        var store   = CipStore();
        var session = new TagSessionService();
        session.Attach(store, isVirtual: false, isLiveController: true);

        var err = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session, capabilities: LiveWritesOn));

        Assert.Equal("WriteGateRequired", err.Kind);
        Assert.Equal("Cmd_MotorStart", err.Tag);
        Assert.Contains("No write gate for this session", err.Error, StringComparison.Ordinal);
        Assert.Contains("logix_open_project", err.Hint, StringComparison.Ordinal);
        Assert.Contains(WriteAllowlist.EnvVar, err.Hint, StringComparison.Ordinal);
        Assert.Equal(false, (await store.ReadAsync("Cmd_MotorStart")).Value);
    }

    /// <summary>Live CIP session, no project, allowlist active: listed tag writes, unlisted is denied.</summary>
    [Fact]
    public async Task CipNoProject_Allowlist_ListedTagWrites_UnlistedDenied()
    {
        var store   = CipStore();
        var session = new TagSessionService();
        session.Attach(store, isVirtual: false, isLiveController: true);
        session.SetWriteAllowlist(AllowlistOf("Cmd_MotorStart\n"));

        Assert.IsType<WriteTagResult>(
            await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session, capabilities: LiveWritesOn));
        Assert.Equal(true, (await store.ReadAsync("Cmd_MotorStart")).Value);

        var err = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: LiveWritesOn));
        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), err.Kind);
        Assert.Contains(nameof(DenyReason.NotOnAllowlist), err.Error, StringComparison.Ordinal);
        Assert.Equal(false, (await store.ReadAsync("Motor_Run")).Value);
    }

    /// <summary>Live CIP session with a project gate: gate decisions are unchanged.</summary>
    [Fact]
    public async Task CipWithProject_GatedWritesUnchanged()
    {
        var store   = BuildDemoVirtualService();
        var session = new TagSessionService();
        session.Attach(new GatedTagService(store, LoadDemoPolicy()), isVirtual: false, isLiveController: true);

        Assert.IsType<WriteTagResult>(
            await WriteTagHandler.ExecuteAsync("Cmd_MotorStart", "true", session, capabilities: LiveWritesOn));

        var err = Assert.IsType<WriteTagError>(
            await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session, capabilities: LiveWritesOn));
        Assert.Equal(TagServiceErrorKind.WriteDenied.ToString(), err.Kind);
        Assert.NotEqual("WriteGateRequired", err.Kind);
    }

    /// <summary>Virtual (simulator) session with no project and no allowlist still writes.</summary>
    [Fact]
    public async Task VirtualNoProject_WriteUnchanged()
    {
        var store   = CipStore();
        var session = new TagSessionService();
        session.Attach(store, isVirtual: true);

        Assert.IsType<WriteTagResult>(await WriteTagHandler.ExecuteAsync("Motor_Run", "true", session));
        Assert.Equal(true, (await store.ReadAsync("Motor_Run")).Value);
    }

#if LOGIX_SDK
    // ── External Access: SDK backend ──────────────────────────────────────────

    /// <summary>
    /// A write to a Read Only command-prefix tag through an SDK-backed session is
    /// denied by the gate before the SDK is ever called.
    /// </summary>
    [Fact]
    public async Task SdkBackend_ReadOnlyTag_DeniedBeforeAnyWrite()
    {
        const string l5x = """
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="36.1" TargetName="C" TargetType="Controller" ContainsContext="false">
              <Controller Name="C" MajorRev="36" MinorRev="1" ProcessorType="1756-L84E">
                <Tags>
                  <Tag Name="Cmd_Locked" TagType="Base" ExternalAccess="Read Only" Constant="false" DataType="BOOL" Radix="Decimal">
                    <Data Format="Decorated"><DataValue DataType="BOOL" Radix="Decimal" Value="0" /></Data>
                  </Tag>
                </Tags>
                <Programs /><Tasks />
              </Controller>
            </RSLogix5000Content>
            """;
        var project = L5X.Parse(l5x);
        var policy  = GateEngine.DerivePolicy(project, GateConfig.Default);

        var fake       = new RecordingFakeLogixProject();
        var projectSes = new LogixProjectSession((_p, _ct) => Task.FromResult<ILogixProject>(fake));
        await projectSes.OpenAsync("dummy.ACD");
        var sdk     = new TypedSdkTagService(projectSes, project);
        var session = new TagSessionService();
        session.Attach(new GatedTagService(sdk, policy), isVirtual: false);

        var result = await WriteTagHandler.ExecuteAsync("Cmd_Locked", "1", session);

        var error = Assert.IsType<WriteTagError>(result);
        Assert.Equal(nameof(TagServiceErrorKind.WriteDenied), error.Kind);
        Assert.Contains(nameof(DenyReason.ExternalAccessNotReadWrite), error.Error);
        Assert.Contains("Read/Write in Logix Designer", error.Error);
        Assert.Null(fake.LastWriteTagPath);
    }
#endif  // LOGIX_SDK

    // ── Declared-type refusals ─────────────────────────────────

    [Theory]
    [InlineData("U", "Speed", "Count")]
    [InlineData("T1", "PRE", "ACC")]
    [InlineData("S1", "LEN", "DATA")]
    public async Task Write_WholeStructure_IsStructureWriteUnsupported_ListingTheMembers(string tag, string m1, string m2)
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync(tag, "1", session));

        Assert.Equal("StructureWriteUnsupported", err.Kind);
        Assert.Contains(m1, err.Error);
        Assert.Contains(m2, err.Error);
        Assert.NotEqual("StructureReadUnsupported", err.Kind);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task Write_WholeArray_IsStructureWriteUnsupported_WithElementHint()
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Arr", "1", session));

        Assert.Equal("StructureWriteUnsupported", err.Kind);
        Assert.Contains("Arr[0]", err.Hint);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task Write_IndexPastDimension_IsIndexOutOfRange()
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("Arr[5]", "1", session));

        Assert.Equal("IndexOutOfRange", err.Kind);
        Assert.Empty(svc.Writes);
    }

    [Fact]
    public async Task Write_MissingMember_IsMemberNotFound()
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        var err = Assert.IsType<WriteTagError>(await WriteTagHandler.ExecuteAsync("U.Nope", "1", session));

        Assert.Equal("MemberNotFound", err.Kind);
        Assert.Contains("Speed", err.Error);
        Assert.Empty(svc.Writes);
    }

    [Theory]
    [InlineData("U.Count")]
    [InlineData("Arr[3]")]
    [InlineData("T1.PRE")]
    [InlineData("Plain")]
    public async Task Write_ValidPaths_ReachTheService(string path)
    {
        var (session, svc) = ReadTagHandlerTests.TypedSession();

        await WriteTagHandler.ExecuteAsync(path, "1", session);

        Assert.Contains(path, svc.Writes);
    }
}
