namespace LogixMcp.Tests;

/// <summary>
/// Compile-time shape verification for <see cref="ITagService"/> and its
/// supporting types.  These tests use a minimal in-process stub so no real
/// PLC connection is required; they guard the public API contract expected by
/// all downstream components.
/// </summary>
public class ITagServiceTests
{
    // ── Stub implementation used across all shape tests ───────────────────────

    /// <summary>
    /// No-op stub that satisfies every <see cref="ITagService"/> method.
    /// Existence of this class proves the interface compiles correctly and that
    /// all required method signatures are implementable.
    /// </summary>
    private sealed class StubTagService : ITagService
    {
        public Task ConnectAsync(string gateway, string path,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TagValue(tagName, 0, "DINT"));

        public Task WriteAsync(string tagName, object value,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TagInfo>>(
                new List<TagInfo> { new TagInfo("TestTag", "BOOL") });
    }

    // ── TagInfo ───────────────────────────────────────────────────────────────

    [Fact]
    public void TagInfo_ConstructsWithNameAndDataType()
    {
        var info = new TagInfo("Cmd_MotorStart", "BOOL");

        Assert.Equal("Cmd_MotorStart", info.Name);
        Assert.Equal("BOOL", info.DataType);
    }

    [Fact]
    public void TagInfo_DataType_CanBeNull()
    {
        var info = new TagInfo("UnknownTag", null);

        Assert.Null(info.DataType);
    }

    [Fact]
    public void TagInfo_IsRecord_SupportsValueEquality()
    {
        var a = new TagInfo("Tag1", "REAL");
        var b = new TagInfo("Tag1", "REAL");

        Assert.Equal(a, b);
    }

    // ── TagValue ──────────────────────────────────────────────────────────────

    [Fact]
    public void TagValue_ConstructsWithAllFields()
    {
        var value = new TagValue("Cmd_SpeedSetpoint", 42.5, "REAL");

        Assert.Equal("Cmd_SpeedSetpoint", value.TagName);
        Assert.Equal(42.5, value.Value);
        Assert.Equal("REAL", value.DataType);
    }

    [Fact]
    public void TagValue_DataType_CanBeNull()
    {
        var value = new TagValue("SomeTag", true, null);

        Assert.Null(value.DataType);
    }

    [Fact]
    public void TagValue_IsRecord_SupportsValueEquality()
    {
        var a = new TagValue("Tag", 1, "DINT");
        var b = new TagValue("Tag", 1, "DINT");

        Assert.Equal(a, b);
    }

    // ── TagServiceError ───────────────────────────────────────────────────────

    [Fact]
    public void TagServiceError_ConstructsWithKindAndMessage()
    {
        var error = new TagServiceError(TagServiceErrorKind.TagNotFound,
            "Tag 'Foo' not found.");

        Assert.Equal(TagServiceErrorKind.TagNotFound, error.Kind);
        Assert.Equal("Tag 'Foo' not found.", error.Message);
        Assert.Null(error.TagName);
    }

    [Fact]
    public void TagServiceError_CanCarryTagName()
    {
        var error = new TagServiceError(TagServiceErrorKind.WriteDenied,
            "Write denied.", "Motor_Run");

        Assert.Equal("Motor_Run", error.TagName);
    }

    [Fact]
    public void TagServiceError_IsRecord_SupportsValueEquality()
    {
        var a = new TagServiceError(TagServiceErrorKind.Unknown, "oops");
        var b = new TagServiceError(TagServiceErrorKind.Unknown, "oops");

        Assert.Equal(a, b);
    }

    // ── TagServiceErrorKind enum ──────────────────────────────────────────────

    [Fact]
    public void TagServiceErrorKind_HasAllExpectedValues()
    {
        var values = Enum.GetNames<TagServiceErrorKind>();

        Assert.Contains(nameof(TagServiceErrorKind.ConnectionFailed), values);
        Assert.Contains(nameof(TagServiceErrorKind.TagNotFound),      values);
        Assert.Contains(nameof(TagServiceErrorKind.WriteDenied),      values);
        Assert.Contains(nameof(TagServiceErrorKind.TypeMismatch),     values);
        Assert.Contains(nameof(TagServiceErrorKind.ProtocolError),    values);
        Assert.Contains(nameof(TagServiceErrorKind.Unknown),          values);
    }

    // ── TagServiceException ───────────────────────────────────────────────────

    [Fact]
    public void TagServiceException_ExposesError()
    {
        var error = new TagServiceError(TagServiceErrorKind.ConnectionFailed,
            "Cannot reach gateway.");
        var ex = new TagServiceException(error);

        Assert.Equal(error, ex.Error);
        Assert.Equal("Cannot reach gateway.", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void TagServiceException_CanWrapInnerException()
    {
        var inner = new InvalidOperationException("socket closed");
        var error = new TagServiceError(TagServiceErrorKind.ConnectionFailed, "Lost connection.");
        var ex    = new TagServiceException(error, inner);

        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void TagServiceException_InheritsFromException()
    {
        var error = new TagServiceError(TagServiceErrorKind.Unknown, "oops");
        var ex    = new TagServiceException(error);

        Assert.IsAssignableFrom<Exception>(ex);
    }

    // ── ITagService stub shape ────────────────────────────────────────────────

    [Fact]
    public async Task ITagService_ConnectAsync_AcceptsGatewayAndPath()
    {
        ITagService svc = new StubTagService();

        // Must not throw; signature check.
        await svc.ConnectAsync("198.51.100.10", "1,0");
    }

    [Fact]
    public async Task ITagService_DisconnectAsync_DoesNotThrow()
    {
        ITagService svc = new StubTagService();

        await svc.DisconnectAsync();
    }

    [Fact]
    public async Task ITagService_ReadAsync_ReturnsTagValue()
    {
        ITagService svc = new StubTagService();

        var result = await svc.ReadAsync("Cmd_MotorStart");

        Assert.NotNull(result);
        Assert.Equal("Cmd_MotorStart", result.TagName);
    }

    [Fact]
    public async Task ITagService_WriteAsync_AcceptsTagNameAndValue()
    {
        ITagService svc = new StubTagService();

        // Must not throw; verifies the Write signature is correct.
        await svc.WriteAsync("Cmd_SpeedSetpoint", 75.0);
    }

    [Fact]
    public async Task ITagService_ListTagsAsync_ReturnsNonNullList()
    {
        ITagService svc = new StubTagService();

        var tags = await svc.ListTagsAsync();

        Assert.NotNull(tags);
        Assert.IsAssignableFrom<IReadOnlyList<TagInfo>>(tags);
    }

    [Fact]
    public async Task ITagService_ListTagsAsync_ReturnsTagInfoWithNameAndType()
    {
        ITagService svc = new StubTagService();

        var tags = await svc.ListTagsAsync();

        Assert.NotEmpty(tags);
        var first = tags[0];
        Assert.False(string.IsNullOrWhiteSpace(first.Name));
    }
}
