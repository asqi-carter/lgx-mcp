namespace LogixMcp.Tests;

/// <summary>
/// Tests verifying that the CIP path (ReadTagHandler, WriteTagHandler,
/// PollTagsHandler) returns structured error objects — never throws — even when
/// the underlying <see cref="ITagService"/> implementation raises an arbitrary
/// runtime exception that is not a <see cref="TagServiceException"/>.
///
/// <para>
/// This covers the two-layer defence:
/// <list type="number">
///   <item>
///     <see cref="LibPlcTagService"/> now wraps every non-<see cref="libplctag.LibPlcTagException"/>
///     into a <see cref="TagServiceException"/> before it can escape the CIP
///     path.
///   </item>
///   <item>
///     Each handler additionally catches any residual <see cref="Exception"/>
///     and converts it to the appropriate structured error record so that the
///     MCP tool response never surfaces a raw exception.
///   </item>
/// </list>
/// </para>
/// </summary>
public class CipErrorHandlingTests
{
    // ── ThrowingTagService ────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="ITagService"/> stub that throws a caller-supplied
    /// exception from every read/write operation.  Used to verify that the
    /// handlers convert arbitrary exceptions to structured error responses.
    /// </summary>
    private sealed class ThrowingTagService : ITagService
    {
        private readonly Exception _ex;

        /// <param name="ex">The exception to throw from read/write calls.</param>
        internal ThrowingTagService(Exception ex) => _ex = ex;

        public Task ConnectAsync(string gateway, string path,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<TagValue> ReadAsync(string tagName,
            CancellationToken ct = default) => throw _ex;

        public Task WriteAsync(string tagName, object value,
            CancellationToken ct = default) => throw _ex;

        public Task<IReadOnlyList<TagInfo>> ListTagsAsync(
            CancellationToken ct = default) => throw _ex;
    }

    /// <summary>
    /// Builds a <see cref="TagSessionService"/> already attached to the given
    /// <see cref="ITagService"/> so handlers see an active session.
    /// </summary>
    private static TagSessionService BuildSession(ITagService svc)
    {
        var session = new TagSessionService();
        session.Attach(svc, isVirtual: false);
        return session;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ReadTagHandler — exception fallthrough
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadTagHandler_ServiceThrowsNotSupportedException_ReturnsReadTagError()
    {
        // A NotSupportedException should be caught and returned as a structured
        // ReadTagError rather than propagating as an unhandled exception.
        var svc     = new ThrowingTagService(new NotSupportedException("CIP not supported."));
        var session = BuildSession(svc);

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.IsType<ReadTagError>(result);
    }

    [Fact]
    public async Task ReadTagHandler_ServiceThrowsNotSupportedException_KindIsUnknown()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP not supported."));
        var session = BuildSession(svc);

        var result = (ReadTagError) await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.Equal("Unknown", result.Kind);
    }

    [Fact]
    public async Task ReadTagHandler_ServiceThrowsNotSupportedException_ErrorContainsMessage()
    {
        const string message = "Simulated NotSupportedException from CIP path.";
        var svc     = new ThrowingTagService(new NotSupportedException(message));
        var session = BuildSession(svc);

        var result = (ReadTagError) await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.Contains(message, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadTagHandler_ServiceThrowsNotSupportedException_TagPopulated()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP error."));
        var session = BuildSession(svc);

        var result = (ReadTagError) await ReadTagHandler.ExecuteAsync("MyTag", session);

        Assert.Equal("MyTag", result.Tag);
    }

    [Fact]
    public async Task ReadTagHandler_ServiceThrowsInvalidOperationException_ReturnsReadTagError()
    {
        var svc     = new ThrowingTagService(new InvalidOperationException("Internal state error."));
        var session = BuildSession(svc);

        var result = await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.IsType<ReadTagError>(result);
        Assert.Equal("Unknown", ((ReadTagError) result).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WriteTagHandler — exception fallthrough
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WriteTagHandler_ServiceThrowsNotSupportedException_ReturnsWriteTagError()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP write not supported."));
        var session = BuildSession(svc);

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "42", session);

        Assert.IsType<WriteTagError>(result);
    }

    [Fact]
    public async Task WriteTagHandler_ServiceThrowsNotSupportedException_KindIsUnknown()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP write not supported."));
        var session = BuildSession(svc);

        var result = (WriteTagError) await WriteTagHandler.ExecuteAsync("SomeTag", "42", session);

        Assert.Equal("Unknown", result.Kind);
    }

    [Fact]
    public async Task WriteTagHandler_ServiceThrowsNotSupportedException_ErrorContainsMessage()
    {
        const string message = "Simulated write failure from CIP path.";
        var svc     = new ThrowingTagService(new NotSupportedException(message));
        var session = BuildSession(svc);

        var result = (WriteTagError) await WriteTagHandler.ExecuteAsync("SomeTag", "1", session);

        Assert.Contains(message, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteTagHandler_ServiceThrowsNotSupportedException_TagPopulated()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP error."));
        var session = BuildSession(svc);

        var result = (WriteTagError) await WriteTagHandler.ExecuteAsync("MyWriteTag", "true", session);

        Assert.Equal("MyWriteTag", result.Tag);
    }

    [Fact]
    public async Task WriteTagHandler_ServiceThrowsInvalidOperationException_ReturnsWriteTagError()
    {
        var svc     = new ThrowingTagService(new InvalidOperationException("Write state error."));
        var session = BuildSession(svc);

        var result = await WriteTagHandler.ExecuteAsync("SomeTag", "0", session);

        Assert.IsType<WriteTagError>(result);
        Assert.Equal("Unknown", ((WriteTagError) result).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PollTagsHandler — exception fallthrough
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PollTagsHandler_ServiceThrowsNotSupportedException_ReturnsPollError()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP poll not supported."));
        var session = BuildSession(svc);

        var result = await PollTagsHandler.ExecuteAsync(["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.IsType<PollError>(result);
    }

    [Fact]
    public async Task PollTagsHandler_ServiceThrowsNotSupportedException_KindIsUnknown()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP poll not supported."));
        var session = BuildSession(svc);

        var result = (PollError) await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.Equal("Unknown", result.Kind);
    }

    [Fact]
    public async Task PollTagsHandler_ServiceThrowsNotSupportedException_ErrorContainsMessage()
    {
        const string message = "Simulated poll failure from CIP path.";
        var svc     = new ThrowingTagService(new NotSupportedException(message));
        var session = BuildSession(svc);

        var result = (PollError) await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.Contains(message, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollTagsHandler_ServiceThrowsNotSupportedException_TagPopulated()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP error."));
        var session = BuildSession(svc);

        var result = (PollError) await PollTagsHandler.ExecuteAsync(
            ["PollTag"], count: 1, intervalMs: 100, session);

        Assert.Equal("PollTag", result.Tag);
    }

    [Fact]
    public async Task PollTagsHandler_ServiceThrowsInvalidOperationException_ReturnsPollError()
    {
        var svc     = new ThrowingTagService(new InvalidOperationException("Poll state error."));
        var session = BuildSession(svc);

        var result = await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.IsType<PollError>(result);
        Assert.Equal("Unknown", ((PollError) result).Kind);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // hint / docs_anchor optional fields — all three error types
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadTagHandler_Error_HintDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP not supported."));
        var session = BuildSession(svc);

        var result = (ReadTagError) await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task ReadTagHandler_Error_DocsAnchorDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP not supported."));
        var session = BuildSession(svc);

        var result = (ReadTagError) await ReadTagHandler.ExecuteAsync("SomeTag", session);

        Assert.Null(result.DocsAnchor);
    }

    [Fact]
    public void ReadTagError_HintAndDocsAnchor_RoundTrip()
    {
        var err = new ReadTagError(
            Error:      "Tag not found.",
            Kind:       "TagNotFound",
            Tag:        "MyTag",
            Hint:       "Verify the tag name exists in the active project.",
            DocsAnchor: "read-tag-errors");

        Assert.Equal("Verify the tag name exists in the active project.", err.Hint);
        Assert.Equal("read-tag-errors", err.DocsAnchor);
    }

    [Fact]
    public async Task WriteTagHandler_Error_HintDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP write not supported."));
        var session = BuildSession(svc);

        var result = (WriteTagError) await WriteTagHandler.ExecuteAsync("SomeTag", "42", session);

        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task WriteTagHandler_Error_DocsAnchorDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP write not supported."));
        var session = BuildSession(svc);

        var result = (WriteTagError) await WriteTagHandler.ExecuteAsync("SomeTag", "42", session);

        Assert.Null(result.DocsAnchor);
    }

    [Fact]
    public async Task PollTagsHandler_Error_HintDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP poll not supported."));
        var session = BuildSession(svc);

        var result = (PollError) await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.Null(result.Hint);
    }

    [Fact]
    public async Task PollTagsHandler_Error_DocsAnchorDefaultsToNull()
    {
        var svc     = new ThrowingTagService(new NotSupportedException("CIP poll not supported."));
        var session = BuildSession(svc);

        var result = (PollError) await PollTagsHandler.ExecuteAsync(
            ["SomeTag"], count: 1, intervalMs: 100, session);

        Assert.Null(result.DocsAnchor);
    }

    [Fact]
    public void PollError_HintAndDocsAnchor_RoundTrip()
    {
        var err = new PollError(
            Error:      "Poll failed.",
            Kind:       "TagNotFound",
            Tag:        "MyTag",
            Hint:       "Ensure all polled tags are present in the active session.",
            DocsAnchor: "poll-errors");

        Assert.Equal("Ensure all polled tags are present in the active session.", err.Hint);
        Assert.Equal("poll-errors", err.DocsAnchor);
    }
}
