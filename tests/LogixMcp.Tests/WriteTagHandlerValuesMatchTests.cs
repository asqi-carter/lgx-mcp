namespace LogixMcp.Tests;

/// <summary>
/// Unit tests for <see cref="WriteTagHandler.ValuesMatch"/> — the type-aware
/// comparison helper that determines the <c>verified</c> field of a write result.
///
/// <para>
/// Tests cover:
/// <list type="bullet">
///   <item>REAL: relative-epsilon comparison accounting for float32 truncation.</item>
///   <item>BOOL, DINT, INT, SINT: exact equality (no epsilon).</item>
///   <item>Unknown/null dataType: exact equality fallback.</item>
///   <item>Edge cases: zero values, negative values, values near the epsilon boundary.</item>
/// </list>
/// </para>
/// </summary>
public class WriteTagHandlerValuesMatchTests
{
    // ── REAL: float32 round-trip tolerance ────────────────────────────────────

    /// <summary>
    /// A double written to a REAL tag is truncated to float32 and read back as
    /// a float (or double with float32 precision).  The round-trip must still
    /// produce verified:true.
    /// </summary>
    [Fact]
    public void ValuesMatch_Real_Float32RoundTrip_ReturnsTrue()
    {
        // Simulate writing a double and reading back the float32-truncated value.
        double written  = 3.14159265358979;                   // full double precision
        double observed = (double)(float)written;             // truncated to float32

        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_ExactMatch_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(100.0, 100.0, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_ValueWithinEpsilon_ReturnsTrue()
    {
        // Difference is 5e-7, which is within the 1e-6 relative epsilon.
        double written  = 1000.0;
        double observed = 1000.0 + 1000.0 * 5e-7;  // relative diff = 5e-7 < 1e-6

        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_ValueOutsideEpsilon_ReturnsFalse()
    {
        // Difference is 2e-6, which exceeds the 1e-6 relative epsilon.
        double written  = 1000.0;
        double observed = 1000.0 + 1000.0 * 2e-6;  // relative diff = 2e-6 > 1e-6

        Assert.False(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_ZeroWrittenAndObserved_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(0.0, 0.0, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_NegativeFloat32RoundTrip_ReturnsTrue()
    {
        double written  = -123.456789;
        double observed = (double)(float)written;

        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_LargeValueFloat32RoundTrip_ReturnsTrue()
    {
        double written  = 98765.4321;
        double observed = (double)(float)written;

        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    [Fact]
    public void ValuesMatch_Real_DataTypeIsCaseInsensitive_ReturnsTrue()
    {
        double written  = 42.0;
        double observed = (double)(float)written;

        // Both lower-case and upper-case "real" should trigger epsilon comparison.
        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "real"));
        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "Real"));
    }

    [Fact]
    public void ValuesMatch_Real_ObservedAsFloat_ReturnsTrue()
    {
        // Controller may return a float; compare written double against float observed.
        double written  = 3.14f;          // representable exactly as float
        float  observed = 3.14f;

        Assert.True(WriteTagHandler.ValuesMatch(written, observed, "REAL"));
    }

    // ── REAL: epsilon constant documentation ─────────────────────────────────

    [Fact]
    public void RealEpsilon_IsWithinExpectedRange()
    {
        // The epsilon must be larger than float32 max relative rounding error
        // (~5.96e-8) but small enough to detect genuine mismatches.
        Assert.True(WriteTagHandler.RealEpsilon > 1e-7,
            "Epsilon must exceed float32 max relative rounding error (~5.96e-8).");
        Assert.True(WriteTagHandler.RealEpsilon < 1e-4,
            "Epsilon must not be so large as to mask genuine value disagreements.");
    }

    // ── BOOL: exact equality ──────────────────────────────────────────────────

    [Fact]
    public void ValuesMatch_Bool_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(true,  true,  "BOOL"));
        Assert.True(WriteTagHandler.ValuesMatch(false, false, "BOOL"));
    }

    [Fact]
    public void ValuesMatch_Bool_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch(true,  false, "BOOL"));
        Assert.False(WriteTagHandler.ValuesMatch(false, true,  "BOOL"));
    }

    // ── DINT: exact equality ─────────────────────────────────────────────────

    [Fact]
    public void ValuesMatch_Dint_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(42, 42, "DINT"));
    }

    [Fact]
    public void ValuesMatch_Dint_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch(42, 43, "DINT"));
    }

    [Fact]
    public void ValuesMatch_Dint_AdjacentIntegers_ReturnsFalse()
    {
        // Integer types must not use epsilon — adjacent integers must not match.
        Assert.False(WriteTagHandler.ValuesMatch(100, 101, "DINT"));
    }

    // ── INT: exact equality ──────────────────────────────────────────────────

    [Fact]
    public void ValuesMatch_Int_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch((short)100, (short)100, "INT"));
    }

    [Fact]
    public void ValuesMatch_Int_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch((short)100, (short)101, "INT"));
    }

    // ── SINT: exact equality ─────────────────────────────────────────────────

    [Fact]
    public void ValuesMatch_Sint_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch((sbyte)10, (sbyte)10, "SINT"));
    }

    [Fact]
    public void ValuesMatch_Sint_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch((sbyte)10, (sbyte)11, "SINT"));
    }

    // ── Unknown / null dataType: exact equality fallback ─────────────────────

    [Fact]
    public void ValuesMatch_NullDataType_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch(42, 42, null));
    }

    [Fact]
    public void ValuesMatch_NullDataType_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch(42, 43, null));
    }

    [Fact]
    public void ValuesMatch_UnknownDataType_SameValue_ReturnsTrue()
    {
        Assert.True(WriteTagHandler.ValuesMatch("abc", "abc", "MotorCmd"));
    }

    [Fact]
    public void ValuesMatch_UnknownDataType_DifferentValues_ReturnsFalse()
    {
        Assert.False(WriteTagHandler.ValuesMatch("abc", "xyz", "MotorCmd"));
    }

    // ── End-to-end: write REAL through VirtualTagService, verify:true ─────────

    /// <summary>
    /// Confirms that writing a REAL value through the full handler stack
    /// (VirtualTagService → WriteTagHandler) produces <c>verified: true</c>
    /// even when the observed value is the float32-truncated form of the written
    /// double.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteReal_FloatTruncatedReadback_ProducesVerifiedTrue()
    {
        // Arrange: seed the virtual service with a REAL tag; observe the float32
        // truncation by having the service store what the controller would store.
        double written  = 3.14159265358979;
        float  asFloat  = (float)written;     // what the controller stores

        // Use a custom VirtualTagService variant that simulates float32 truncation
        // by seeding with the float-truncated value (as double).
        var store = new Dictionary<string, (object, string?)>
        {
            ["Cmd_SpeedSetpoint"] = ((double)asFloat, "REAL"),
        };
        var inner   = new VirtualTagService(store);
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        // Act: write the full-precision double.
        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_SpeedSetpoint", written.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture),
            session);

        // Assert: verified must be true despite the float32 truncation gap.
        var success = Assert.IsType<WriteTagResult>(result);
        Assert.True(success.Verified,
            "REAL write should be verified:true even after float32 round-trip truncation.");
        Assert.Equal("REAL", success.DataType);
    }

    /// <summary>
    /// Confirms that when the observed value genuinely differs (e.g. another
    /// program overwrote the tag), <c>verified</c> is <c>false</c>.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteReal_GenuineMismatch_ProducesVerifiedFalse()
    {
        // Arrange: seed with a value that will be overwritten to something different.
        // We simulate the controller overwriting the tag by seeding a different value.
        // The VirtualTagService stores whatever we write, so we can't simulate
        // concurrent overwrite directly — instead use a custom service.
        double writtenValue   = 100.0;
        double differentValue = 200.0;  // simulated concurrent overwrite

        var overwrittenStore = new Dictionary<string, (object, string?)>
        {
            ["Cmd_SpeedSetpoint"] = (differentValue, "REAL"),
        };

        // Build a service where the seed acts as the post-write observed value —
        // we need a service that reports a different value on ReadAsync than what
        // was written.  Use a ThrowOnWriteTagService stub for that.
        var service = new FixedReadVirtualTagService(
            writeSeed:    new Dictionary<string, (object, string?)>
            {
                ["Cmd_SpeedSetpoint"] = (writtenValue, "REAL"),
            },
            readOverride: differentValue);

        var session = new TagSessionService();
        session.Attach(service, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_SpeedSetpoint", writtenValue.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture),
            session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.False(success.Verified,
            "Verified must be false when the observed value differs from what was written.");
    }

    /// <summary>
    /// When verified is false (observed != written), VerificationError must carry a
    /// human-readable note about the possible controller overwrite — not be null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteReal_GenuineMismatch_VerificationErrorIsSet()
    {
        double writtenValue   = 100.0;
        double differentValue = 200.0;

        var service = new FixedReadVirtualTagService(
            writeSeed:    new Dictionary<string, (object, string?)>
            {
                ["Cmd_SpeedSetpoint"] = (writtenValue, "REAL"),
            },
            readOverride: differentValue);

        var session = new TagSessionService();
        session.Attach(service, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync(
            "Cmd_SpeedSetpoint", writtenValue.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture),
            session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.False(success.Verified);
        Assert.NotNull(success.VerificationError);
    }

    /// <summary>
    /// When verified is true (values agree), VerificationError must be null.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WriteReal_ValuesMatch_VerificationErrorIsNull()
    {
        var store = new Dictionary<string, (object, string?)>
        {
            ["Cmd_SpeedSetpoint"] = (42.0, "REAL"),
        };
        var inner   = new VirtualTagService(store);
        var session = new TagSessionService();
        session.Attach(inner, isVirtual: true);

        var result = await WriteTagHandler.ExecuteAsync("Cmd_SpeedSetpoint", "42.0", session);

        var success = Assert.IsType<WriteTagResult>(result);
        Assert.True(success.Verified);
        Assert.Null(success.VerificationError);
    }

    // ── Helper stub ───────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="ITagService"/> stub that accepts writes normally but always
    /// returns <paramref name="readOverride"/> for <see cref="ReadAsync"/> on the
    /// single seeded tag.  Used to simulate a controller overwriting a tag between
    /// the write and the readback.
    /// </summary>
    private sealed class FixedReadVirtualTagService : ITagService
    {
        private readonly Dictionary<string, (object Value, string? DataType)> _store;
        private readonly double _readOverride;

        public FixedReadVirtualTagService(
            IReadOnlyDictionary<string, (object, string?)> writeSeed,
            double readOverride)
        {
            _store = new Dictionary<string, (object Value, string? DataType)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var kv in writeSeed)
                _store[kv.Key] = kv.Value;
            _readOverride = readOverride;
        }

        public Task ConnectAsync(string gateway, string path,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName, CancellationToken ct = default)
        {
            if (!_store.TryGetValue(tagName, out var entry))
                throw new TagServiceException(
                    new TagServiceError(TagServiceErrorKind.TagNotFound,
                        $"Tag '{tagName}' not found.", tagName));

            // Always return the override value instead of whatever was written.
            return Task.FromResult(new TagValue(tagName, _readOverride, entry.DataType));
        }

        public Task WriteAsync(string tagName, object value, CancellationToken ct = default)
        {
            if (!_store.TryGetValue(tagName, out var existing))
                throw new TagServiceException(
                    new TagServiceError(TagServiceErrorKind.TagNotFound,
                        $"Tag '{tagName}' not found.", tagName));
            _store[tagName] = (value, existing.DataType);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TagInfo>>(
                _store.Select(kv => new TagInfo(kv.Key, kv.Value.DataType)).ToList());
    }
}
